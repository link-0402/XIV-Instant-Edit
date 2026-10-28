using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Unity;

/// <summary>
/// Reads a Unity .unitypackage: a gzip'd tar with one folder per asset GUID, holding
/// <c>pathname</c> (the asset's project path on its first line), <c>asset</c> (the file),
/// <c>asset.meta</c> (import settings) and sometimes <c>preview.png</c>.
///
/// The archive is streamed, never unpacked whole: packages run to hundreds of megabytes, so a
/// scan keeps only paths, meta text, thumbnails and the text of materials and prefabs, and an
/// extraction copies each wanted asset straight from the stream to its file.
/// </summary>
internal static class UnityPackageReader
{
    /// <summary>Material and prefab files larger than this are not read (none come close in practice).</summary>
    private const int MaxYamlBytes = 4 * 1024 * 1024;
    private const int MaxMetaBytes = 256 * 1024;
    private const int MaxPreviewBytes = 1024 * 1024;

    private static readonly byte[] YamlMagic = Encoding.ASCII.GetBytes("%YAML");

    public static UnityScanResult Scan(string path, Action<string> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new UnityScanResult { PackagePath = path };

        var pathnames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sizes     = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var metas     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previews  = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var yaml      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int count = 0;
        ForEachEntry(path, ct, (guid, file, entry) =>
        {
            if (++count % 50 == 0)
                progress($"Reading the package ({count} entries)");
            switch (file)
            {
                case "pathname":
                    if (ReadText(entry, 64 * 1024) is { } text)
                    {
                        pathnames[guid] = FirstLine(text);
                        // Text read before its path was known (an animation clip, a scene) that turns
                        // out not to be a material or prefab is let go of at once.
                        if (UnityAsset.KindOf(pathnames[guid]) is not (UnityAssetKind.Material or UnityAssetKind.Prefab)
                            && yaml.Remove(guid, out var dropped))
                            result.BufferedAssetBytes -= System.Text.Encoding.UTF8.GetByteCount(dropped);
                    }
                    break;
                case "asset.meta":
                    // A model's meta can run far longer (its rig, its clips), but all that is read
                    // from one (the GUID, the texture type, the material remap) comes at its start.
                    if (ReadHead(entry, MaxMetaBytes) is { } meta)
                        metas[guid] = meta;
                    break;
                case "preview.png":
                    if (entry.Length <= MaxPreviewBytes && ReadBytes(entry, MaxPreviewBytes) is { } png)
                        previews[guid] = png;
                    break;
                case "asset":
                    sizes[guid] = entry.Length;
                    // Only material and prefab text is kept. The path may not be known yet (the
                    // tar's order within a folder is not fixed), so unknown assets are kept only
                    // when they start like Unity YAML.
                    bool wanted = pathnames.TryGetValue(guid, out var known)
                        ? UnityAsset.KindOf(known) is UnityAssetKind.Material or UnityAssetKind.Prefab
                        : true;
                    if (wanted && entry.Length <= MaxYamlBytes && ReadYaml(entry) is { } body)
                    {
                        yaml[guid] = body;
                        result.BufferedAssetBytes += entry.Length;
                    }
                    break;
            }
        });

        foreach (var (guid, packagePath) in pathnames.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (!sizes.TryGetValue(guid, out var size))
                continue;   // a folder: it has a path and a meta file, but no asset
            var kind = UnityAsset.KindOf(packagePath);
            var metaText = metas.GetValueOrDefault(guid);
            var meta = metaText != null ? UnityYaml.ReadMeta(metaText) : (null, -1);
            result.Assets.Add(new UnityAsset
            {
                Guid          = guid.ToLowerInvariant(),
                PackagePath   = packagePath,
                Kind          = kind,
                Size          = size,
                PreviewPng    = previews.GetValueOrDefault(guid),
                TextureType   = kind == UnityAssetKind.Texture ? meta.TextureType : -1,
                MaterialRemap = kind == UnityAssetKind.Model && metaText != null ? UnityYaml.ReadMaterialRemap(metaText) : new Dictionary<string, string>(),
                Selected      = kind is UnityAssetKind.Texture or UnityAssetKind.Model,
            });

            if (!yaml.TryGetValue(guid, out var body))
            {
                if (kind is UnityAssetKind.Material or UnityAssetKind.Prefab && size > MaxYamlBytes)
                    result.Warnings.Add($"{packagePath} is too large to read; its textures are not grouped.");
                continue;
            }
            if (kind == UnityAssetKind.Material && UnityYaml.ReadMaterial(guid, body) is { } material)
                result.Materials.Add(material);
            else if (kind == UnityAssetKind.Prefab)
                result.Prefabs.Add(new UnityPrefab(guid.ToLowerInvariant(), Path.GetFileNameWithoutExtension(packagePath), UnityYaml.ReadPrefabMaterials(body)));
        }
        return result;
    }

    /// <summary>
    /// Writes the ticked textures and models under <paramref name="outputDir"/>/&lt;package name&gt;,
    /// keeping their project layout minus the leading Assets/. A path that would land outside that
    /// folder is refused and reported. A run that stops or fails leaves nothing behind, and an
    /// earlier extraction of the package it would have replaced is kept (see <see cref="Stash.ExtractionFolder"/>).
    /// </summary>
    public static UnityExtraction Extract(UnityScanResult scan, string outputDir, Action<string> progress, CancellationToken ct)
        => Extract(new[] { scan }, outputDir, progress, ct);

    /// <summary>
    /// Several packages extracted together, into one folder named after the first, so a material in
    /// one finds its textures in another. Where two packages write the same path, the first one's file is kept.
    /// </summary>
    public static UnityExtraction Extract(IReadOnlyList<UnityScanResult> scans, string outputDir, Action<string> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = scans.Select(s => Path.GetFullPath(s.PackagePath)).ToList();
        var claim = Stash.ExtractionFolder.Claim(outputDir, PathNormalizer.SafeSegment(scans[0].PackageName), folder => IsOwnRun(folder, sources));
        bool finished = false;
        try
        {
            var extraction = Write(scans, claim.Path, progress, ct);
            finished = true;
            return extraction;
        }
        finally
        {
            if (finished) claim.Finish();
            else          claim.Abandon();
        }
    }

    private static UnityExtraction Write(IReadOnlyList<UnityScanResult> scans, string root, Action<string> progress, CancellationToken ct)
    {
        var result = new UnityExtraction { RootFolder = root };
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var report = new StringBuilder();

        // Per package, GUID → file; a GUID or path an earlier package already writes is left to that one.
        var targets = scans.Select(_ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)).ToList();
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < scans.Count; i++)
            foreach (var asset in scans[i].Assets.Where(a => a.Selected && a.Extractable))
            {
                var relative = SafeRelative(asset.PackagePath);
                var target = relative == null ? null : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (target == null || !target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    result.Refused.Add(asset.PackagePath);
                    continue;
                }
                if (!guids.Add(asset.Guid))
                    continue;
                if (!paths.Add(target))
                {
                    result.Skipped++;
                    report.AppendLine($"skipped  {asset.PackagePath} from {scans[i].PackageName}: an earlier package writes that path");
                    continue;
                }
                targets[i][asset.Guid] = target;
            }

        report.Insert(0, $"Extracted from {string.Join(", ", scans.Select(s => s.PackagePath))} on {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}");
        int done = 0, total = targets.Sum(t => t.Count);
        try
        {
            Directory.CreateDirectory(root);
            for (int i = 0; i < scans.Count; i++)
            {
                var wanted = targets[i];
                ForEachEntry(scans[i].PackagePath, ct, (guid, file, entry) =>
                {
                    if (file != "asset" || !wanted.TryGetValue(guid, out var target) || entry.DataStream == null)
                        return;
                    progress($"Writing {Path.GetFileName(target)} ({++done} of {total})");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        // Listed before it is written, so even a half-written file is known to be ours.
                        result.FileByGuid[guid] = target;
                        using (var output = File.Create(target))
                            entry.DataStream.CopyTo(output);
                        result.WrittenFiles.Add(target);
                        report.AppendLine($"written  {target}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Failed++;
                        report.AppendLine($"FAILED   {target}: {ex.Message}");
                        try { File.Delete(target); result.FileByGuid.Remove(guid); }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* stays listed, so the next run removes it */ }
                    }
                });
            }
        }
        finally
        {
            // Written whatever happened: without them the next run of this package would take the
            // folder for someone else's. (A run that stops is removed whole by Extract.)
            foreach (var refused in result.Refused)
                report.AppendLine($"refused  {refused} (its path leads outside the output folder)");
            try
            {
                File.WriteAllText(Path.Combine(root, "extract-report.txt"), report.ToString());
                Stash.StashManifest.ForUnity(scans, result).Write(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without them the next run treats the folder as someone else's and uses a new one.
            }
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> holds nothing but an earlier extraction of <paramref name="sources"/>:
    /// its stash.json names those packages, no more and no fewer, and lists every file there besides
    /// the models Blender saved into it (which the next run keeps). Files someone else put there make
    /// it another's folder, which is never touched.
    /// </summary>
    private static bool IsOwnRun(string folder, IReadOnlyList<string> sources)
    {
        try
        {
            var manifest = Stash.StashManifest.Read(folder);
            if (manifest == null || !manifest.AllSources.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sources))
                return false;
            var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(folder, Stash.StashManifest.FileName),
                Path.Combine(folder, "extract-report.txt"),
            };
            foreach (var entry in manifest.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(folder, entry.Path));
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    owned.Add(path);
            }
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (!owned.Contains(Path.GetFullPath(file)) && !Stash.BlenderExports.IsBlenderFile(folder, file))
                    return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A package path made safe to write under the output folder: "Assets/A/b.png" becomes "A/b.png".
    /// Null for a rooted path, a drive, or one that climbs with "..".
    /// </summary>
    public static string? SafeRelative(string packagePath)
    {
        var path = packagePath.Trim().Replace('\\', '/');
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':'))
            return null;
        var segments = path.Split('/').ToList();
        if (segments.Count > 1 && string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase))
            segments.RemoveAt(0);
        if (segments.Any(s => s.Length == 0 || s is "." or ".."))
            return null;
        return string.Join('/', segments.Select(PathNormalizer.SafeSegment));
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Calls <paramref name="visit"/> with (GUID, file name, entry) for every file directly inside a GUID folder.</summary>
    private static void ForEachEntry(string path, CancellationToken ct, Action<string, string, TarEntry> visit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            var slash = name.IndexOf('/');
            if (slash <= 0 || name.IndexOf('/', slash + 1) >= 0)
                continue;
            visit(name[..slash], name[(slash + 1)..], entry);
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return (end >= 0 ? text[..end] : text).TrimEnd('\r').Trim();
    }

    private static byte[]? ReadBytes(TarEntry entry, int max)
    {
        if (entry.DataStream == null || entry.Length > max)
            return null;
        var buffer = new byte[entry.Length];
        entry.DataStream.ReadExactly(buffer);
        return buffer;
    }

    private static string? ReadText(TarEntry entry, int max)
        => ReadBytes(entry, max) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The entry's text, or only its first <paramref name="max"/> bytes when it is longer.</summary>
    private static string? ReadHead(TarEntry entry, int max)
    {
        if (entry.DataStream == null)
            return null;
        var buffer = new byte[Math.Min(entry.Length, max)];
        entry.DataStream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>The entry's text when it starts like a Unity YAML file; otherwise null, having read only its first bytes.</summary>
    private static string? ReadYaml(TarEntry entry)
    {
        if (entry.DataStream == null || entry.Length < YamlMagic.Length)
            return null;
        Span<byte> magic = stackalloc byte[YamlMagic.Length];
        entry.DataStream.ReadExactly(magic);
        if (!magic.SequenceEqual(YamlMagic))
            return null;
        var buffer = new byte[entry.Length];
        magic.CopyTo(buffer);
        entry.DataStream.ReadExactly(buffer, YamlMagic.Length, buffer.Length - YamlMagic.Length);
        return Encoding.UTF8.GetString(buffer);
    }
}
