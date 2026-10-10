using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Library;

/// <summary>
/// What makes two installs of a mod the same: its name, every payload file by relative path, size
/// and SHA-256, and its option structure with ids and write times removed.
/// </summary>
internal sealed record ModFingerprint(string Name, IReadOnlyList<(string Rel, long Size, string Sha256)> Files, string Structure)
{
    /// <summary>One string that is equal exactly when two fingerprints are.</summary>
    public string Key { get; } = string.Join('\n', new[] { Name, Structure }.Concat(Files.Select(f => $"{f.Rel}\0{f.Size}\0{f.Sha256}")));
}

/// <summary>
/// Mods installed more than once: the same name (ignoring case and surrounding spaces) and the same
/// fingerprint, though Penumbra gave each install its own ids. Only mods that share a name with
/// another are read. Mod Optimizer's duplicate mod finder, rebuilt here. Dalamud-free.
/// </summary>
internal static class DuplicateModScan
{
    private static readonly HashSet<string> VolatileKeys = new(StringComparer.Ordinal) { "Id", "Identifier", "LastWrite", "ParentSetting" };

    /// <summary>The fingerprint of the mod in a folder, or null when its meta.json can't be read.</summary>
    public static ModFingerprint? Fingerprint(string folder) => Fingerprint(folder, CancellationToken.None);

    public static ModFingerprint? Fingerprint(string folder, CancellationToken token)
    {
        PenumbraModMeta meta;
        try
        {
            if (!File.Exists(Path.Combine(folder, PenumbraModMeta.FileName)))
                return null;
            meta = PenumbraModMeta.Load(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }

        var structure = new JObject
        {
            ["Default"] = meta.Root["DefaultData"]?.DeepClone(),
            ["Groups"]  = meta.Root["Groups"]?.DeepClone(),
        };
        if (meta.Root["PageNames"] is { } pages)
            structure["PageNames"] = pages.DeepClone();
        Canonicalize(structure);

        var files = new List<(string Rel, long Size, string Sha256)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(folder, path).Replace('\\', '/').ToLowerInvariant();
                if (IsMetadata(rel))
                    continue;
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                files.Add((rel, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        return new ModFingerprint(NameKey(meta.Name), files, structure.ToString(Formatting.None));
    }

    /// <summary>
    /// Sets of folder names of identical installs, each set sorted by folder name. Mods that share a
    /// name are first compared by their file list (paths and sizes, no reading); only those whose
    /// lists match are hashed.
    /// </summary>
    /// <param name="fingerprint">Reads a mod by its full path; null leaves the mod out.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Find(IReadOnlyList<InstalledMod> mods, Func<string, ModFingerprint?> fingerprint,
        IProgress<CheckProgress>? progress = null, CancellationToken token = default)
    {
        var candidates = mods.GroupBy(mod => NameKey(mod.Name)).Where(group => group.Count() > 1)
            .SelectMany(group => group.GroupBy(mod => Listing(mod.FullPath) ?? $"\0unreadable\0{mod.FolderName}", StringComparer.Ordinal))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var prints = new List<(InstalledMod Mod, string Key)>();
        for (int i = 0; i < candidates.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new CheckProgress("Comparing mods with equal names", i + 1, candidates.Length));
            if (fingerprint(candidates[i].FullPath) is { } print)
                prints.Add((candidates[i], $"{NameKey(candidates[i].Name)}\n{print.Key}"));
        }
        return prints.GroupBy(item => item.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<string>)group.Select(item => item.Mod.FolderName).Order(StringComparer.OrdinalIgnoreCase).ToArray())
            .ToArray();
    }

    private static string NameKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>The mod's payload files by relative path and size, without reading them; null when the folder can't be listed.</summary>
    internal static string? Listing(string folder)
    {
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(file => (Rel: Path.GetRelativePath(folder, file.FullName).Replace('\\', '/').ToLowerInvariant(), file.Length))
                .Where(file => !IsMetadata(file.Rel))
                .OrderBy(file => file.Rel, StringComparer.Ordinal)
                .Select(file => $"{file.Rel}\0{file.Length}");
            return string.Join('\n', files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>meta.json and Penumbra's other root .json files, backups and half-written files: not the mod's payload.</summary>
    private static bool IsMetadata(string rel)
        => rel.EndsWith(".bak", StringComparison.Ordinal)
        || rel.EndsWith(".instant-edit-tmp", StringComparison.Ordinal)
        || rel.Contains(".modopt-", StringComparison.Ordinal)
        || (!rel.Contains('/') && rel.EndsWith(".json", StringComparison.Ordinal));

    private static void Canonicalize(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties().Where(p => VolatileKeys.Contains(p.Name)).ToArray())
                    property.Remove();
                var ordered = obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                obj.RemoveAll();
                foreach (var property in ordered)
                {
                    if (property.Name is "Files" or "FileSwaps" && property.Value is JObject mapping)
                    {
                        // Game paths and relative paths are compared without case or slash style, as Penumbra does.
                        // Keys that differ only in case (Penumbra uses one of them) are kept together, sorted.
                        var entries = mapping.Properties()
                            .Select(p => (Key: p.Name.ToLowerInvariant(),
                                Value: p.Value.Type == JTokenType.String ? p.Value.Value<string>()!.Replace('\\', '/').ToLowerInvariant() : p.Value.ToString(Formatting.None)))
                            .GroupBy(e => e.Key, StringComparer.Ordinal)
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .Select(g => g.Count() == 1
                                ? new JProperty(g.Key, g.First().Value)
                                : new JProperty(g.Key, new JArray(g.Select(e => e.Value).Order(StringComparer.Ordinal))));
                        obj.Add(new JProperty(property.Name, new JObject(entries)));
                        continue;
                    }
                    Canonicalize(property.Value);
                    obj.Add(property);
                }
                break;
            case JArray array:
                foreach (var child in array)
                    Canonicalize(child);
                break;
        }
    }
}
