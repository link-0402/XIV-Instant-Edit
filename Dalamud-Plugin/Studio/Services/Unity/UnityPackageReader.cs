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
                        pathnames[guid] = FirstLine(text);
                    break;
                case "asset.meta":
                    if (ReadText(entry, MaxMetaBytes) is { } meta)
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
            var meta = metas.TryGetValue(guid, out var m) ? UnityYaml.ReadMeta(m) : (null, -1);
            result.Assets.Add(new UnityAsset
            {
                Guid        = guid.ToLowerInvariant(),
                PackagePath = packagePath,
                Kind        = kind,
                Size        = size,
                PreviewPng  = previews.GetValueOrDefault(guid),
                TextureType = kind == UnityAssetKind.Texture ? meta.TextureType : -1,
                Selected    = kind is UnityAssetKind.Texture or UnityAssetKind.Model,
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
    /// folder is refused and reported.
    /// </summary>
    public static UnityExtraction Extract(UnityScanResult scan, string outputDir, Action<string> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.Combine(outputDir, PathNormalizer.SafeSegment(scan.PackageName));
        var result = new UnityExtraction { RootFolder = root };
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in scan.Assets.Where(a => a.Selected && a.Extractable))
        {
            var relative = SafeRelative(asset.PackagePath);
            var target = relative == null ? null : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (target == null || !target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                result.Refused.Add(asset.PackagePath);
                continue;
            }
            targets[asset.Guid] = target;
        }

        var report = new StringBuilder();
        report.AppendLine($"Extracted from {scan.PackagePath} on {DateTime.Now:yyyy-MM-dd HH:mm}");
        int done = 0;
        ForEachEntry(scan.PackagePath, ct, (guid, file, entry) =>
        {
            if (file != "asset" || !targets.TryGetValue(guid, out var target) || entry.DataStream == null)
                return;
            progress($"Writing {Path.GetFileName(target)} ({++done} of {targets.Count})");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target))
                    entry.DataStream.CopyTo(output);
                result.WrittenFiles.Add(target);
                result.FileByGuid[guid] = target;
                report.AppendLine($"written  {target}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed++;
                report.AppendLine($"FAILED   {target}: {ex.Message}");
            }
        });

        foreach (var refused in result.Refused)
            report.AppendLine($"refused  {refused} (its path leads outside the output folder)");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "extract-report.txt"), report.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The report is a convenience; the files are what matter.
        }
        return result;
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
