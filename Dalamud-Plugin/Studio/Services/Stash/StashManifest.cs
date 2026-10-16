using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Studio.Services.Sims;
using InstantEdit.Studio.Services.Unity;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>Which tab of the stash a file is listed under.</summary>
public enum StashKind { Model, Texture, Material, Animation, Other }

/// <summary>One file of an extraction, as the stash lists it.</summary>
internal sealed class StashEntry
{
    /// <summary>Relative to the extraction folder, with '/'.</summary>
    public string Path { get; set; } = string.Empty;
    public StashKind Kind { get; set; }
    public TexturePurpose Purpose { get; set; } = TexturePurpose.Other;

    /// <summary>The materials, prefabs or CAS parts the file belongs to.</summary>
    public List<string> Groups { get; set; } = new();
    public string Detail { get; set; } = string.Empty;

    /// <summary>A model that has to be turned into a .mdl (in Blender) before the game can use it.</summary>
    public bool NeedsConversion { get; set; }
}

/// <summary>A material of the source (a Unity material, a Sims CAS part) and its textures by purpose.</summary>
internal sealed class StashMaterial
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Purpose → path relative to the extraction folder.</summary>
    public Dictionary<TexturePurpose, string> Textures { get; set; } = new();
}

/// <summary>
/// stash.json: what an extraction wrote and what each file is for. The import window lists an
/// extraction from it; a folder without one (older extractions) is scanned instead.
/// </summary>
internal sealed class StashManifest
{
    public const string FileName = "stash.json";
    private const string ReportName = "extract-report.txt";

    public int Version { get; set; } = 1;
    public string Source { get; set; } = string.Empty;

    /// <summary>"unity", "sims", or "folder" for one scanned without a manifest.</summary>
    public string SourceKind { get; set; } = string.Empty;
    public DateTime ExtractedUtc { get; set; }
    public List<StashEntry> Entries { get; set; } = new();
    public List<StashMaterial> Materials { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The folder's stash.json, or null when it has none or it can't be read.</summary>
    public static StashManifest? Read(string folder)
    {
        try
        {
            var path = System.IO.Path.Combine(folder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<StashManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Write(string folder)
    {
        var path = System.IO.Path.Combine(folder, FileName);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* a stray .tmp is harmless */ }
        }
    }

    /// <summary>The files an extraction folder holds, for one without a manifest: kinds by extension, purposes by folder and name.</summary>
    public static StashManifest Scan(string folder)
    {
        var manifest = new StashManifest { Source = folder, SourceKind = "folder", ExtractedUtc = Directory.GetLastWriteTimeUtc(folder) };
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsBookkeeping(System.IO.Path.GetFileName(file)))
                continue;
            var entry = NewEntry(folder, file);
            if (entry.Kind == StashKind.Texture)
                entry.Purpose = FromFolder(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? "") ?? TexturePurposes.FromFileName(file);
            manifest.Entries.Add(entry);
        }
        return manifest;
    }

    /// <summary>A Unity extraction: textures by the material properties that use them, grouped under materials and prefabs.</summary>
    public static StashManifest ForUnity(UnityScanResult scan, UnityExtraction extraction)
    {
        var root = extraction.RootFolder;
        var manifest = new StashManifest { Source = System.IO.Path.GetFullPath(scan.PackagePath), SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };

        // Texture GUID → (material, property purpose) for every material property that uses it.
        var uses = new Dictionary<string, List<(UnityMaterial Material, TexturePurpose? Purpose)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in scan.Materials)
            foreach (var (property, texture) in material.Textures)
            {
                if (!uses.TryGetValue(texture, out var list))
                    uses[texture] = list = new();
                list.Add((material, TexturePurposes.FromUnityProperty(property)));
            }
        var prefabsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefab in scan.Prefabs)
            foreach (var material in prefab.MaterialGuids)
            {
                if (!prefabsOf.TryGetValue(material, out var names))
                    prefabsOf[material] = names = new();
                if (!names.Contains(prefab.Name))
                    names.Add(prefab.Name);
            }

        var assets = scan.Assets.ToDictionary(a => a.Guid, StringComparer.OrdinalIgnoreCase);
        foreach (var (guid, file) in extraction.FileByGuid.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            var entry = NewEntry(root, file);
            var used = uses.GetValueOrDefault(guid) ?? new();
            foreach (var (material, _) in used)
            {
                if (!entry.Groups.Contains(material.Name)) entry.Groups.Add(material.Name);
                foreach (var prefab in prefabsOf.GetValueOrDefault(material.Guid) ?? new())
                    if (!entry.Groups.Contains(prefab)) entry.Groups.Add(prefab);
            }
            if (entry.Kind == StashKind.Texture)
            {
                var fromProperty = used.Select(u => u.Purpose).FirstOrDefault(p => p != null);
                entry.Purpose = TexturePurposes.Classify(null, fromProperty, assets.GetValueOrDefault(guid)?.TextureType ?? -1, file);
            }
            manifest.Entries.Add(entry);
        }

        foreach (var material in scan.Materials)
        {
            var listed = new StashMaterial { Name = material.Name };
            foreach (var (property, texture) in material.Textures)
                if (TexturePurposes.FromUnityProperty(property) is { } purpose && extraction.FileByGuid.TryGetValue(texture, out var file))
                    listed.Textures.TryAdd(purpose, Relative(root, file));
            if (listed.Textures.Count > 0)
                manifest.Materials.Add(listed);
        }
        return manifest;
    }

    /// <summary>A Sims extraction: textures by their CAS part's role, grouped under the CAS part.</summary>
    public static StashManifest ForSims(string packagePath, SimsPackageInspector.ExtractionResult extraction)
    {
        var root = extraction.RootFolder;
        var manifest = new StashManifest { Source = System.IO.Path.GetFullPath(packagePath), SourceKind = "sims", ExtractedUtc = DateTime.UtcNow };
        var textures = extraction.Textures.ToDictionary(t => System.IO.Path.GetFullPath(t.File), StringComparer.OrdinalIgnoreCase);
        var parts = new Dictionary<string, StashMaterial>(StringComparer.Ordinal);

        foreach (var file in extraction.WrittenFiles.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsBookkeeping(System.IO.Path.GetFileName(file)))
                continue;
            var entry = NewEntry(root, file);
            if (entry.Kind == StashKind.Texture)
            {
                if (textures.TryGetValue(System.IO.Path.GetFullPath(file), out var texture))
                {
                    entry.Purpose = TexturePurposes.FromSimsRole(texture.Role) ?? TexturePurposes.FromFileName(file);
                    if (texture.Part.Length > 0)
                    {
                        entry.Groups.Add(texture.Part);
                        if (!parts.TryGetValue(texture.Part, out var part))
                            parts[texture.Part] = part = new StashMaterial { Name = texture.Part };
                        part.Textures.TryAdd(entry.Purpose, entry.Path);
                    }
                }
                else
                {
                    entry.Purpose = TexturePurposes.FromFileName(file);
                }
            }
            manifest.Entries.Add(entry);
        }
        manifest.Materials.AddRange(parts.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        return manifest;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mdl", ".fbx", ".obj", ".glb", ".gltf", ".blend", ".dae" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".exr", ".dds", ".tex" };

    public static StashKind KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (ModelExtensions.Contains(ext)) return StashKind.Model;
        if (ImageExtensions.Contains(ext)) return StashKind.Texture;
        return ext.ToLowerInvariant() switch
        {
            ".mtrl"         => StashKind.Material,
            ".pap" or ".tmb" => StashKind.Animation,
            _               => StashKind.Other,
        };
    }

    /// <summary>Files the extraction writes for itself, never listed.</summary>
    public static bool IsBookkeeping(string fileName)
        => fileName.Equals(FileName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(ReportName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(SimsPackageInspector.ManifestName, StringComparison.OrdinalIgnoreCase);

    private static StashEntry NewEntry(string root, string file)
    {
        var kind = KindOf(file);
        bool convert = kind == StashKind.Model && !System.IO.Path.GetExtension(file).Equals(".mdl", StringComparison.OrdinalIgnoreCase);
        return new StashEntry
        {
            Path            = Relative(root, file),
            Kind            = kind,
            NeedsConversion = convert,
            Detail          = convert ? "convert to .mdl in Blender first" : string.Empty,
        };
    }

    private static string Relative(string root, string file) => System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>The Sims importer's role folders (textures/diffuse, textures/normal, textures/specular).</summary>
    private static TexturePurpose? FromFolder(string folder) => folder.ToLowerInvariant() switch
    {
        "diffuse"  => TexturePurpose.Diffuse,
        "normal"   => TexturePurpose.Normal,
        "specular" => TexturePurpose.Mask,
        _          => null,
    };
}
