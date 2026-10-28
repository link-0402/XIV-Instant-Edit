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

    /// <summary>
    /// A Unity model's materials: each material name of the model → the name of the Unity material
    /// its meta file assigns it. Null (and not written) when it has none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? MaterialNames { get; set; }
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

    /// <summary>Every package of an extraction made from several at once, <see cref="Source"/> first; empty for one package.</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>The packages this extraction came from: <see cref="Sources"/>, or just <see cref="Source"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllSources => Sources.Count > 0 ? Sources : new[] { Source };

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

    /// <summary>How many files a folder scan lists at most.</summary>
    public const int MaxScanFiles = 5000;

    /// <summary>Unity text larger than this (a huge prefab or scene) is not read for material references.</summary>
    private const long MaxYamlBytes = 8 * 1024 * 1024;

    /// <summary>Set when a folder scan stopped at its cap; not saved.</summary>
    [JsonIgnore]
    public bool Truncated { get; private set; }

    /// <summary>Folders (relative, "" for the top) that list a texture other than a diffuse; worked out on first use.</summary>
    private HashSet<string>? _mixedFolders;

    /// <summary>
    /// Whether every texture listed directly in <paramref name="relativeFolder"/> ('/'-separated, ""
    /// for the top) is a diffuse: a folder of swatches that can go in as variants, not a texture set
    /// whose normals, masks and matcaps would go in with them.
    /// </summary>
    public bool HoldsOnlyDiffuses(string relativeFolder)
    {
        _mixedFolders ??= Entries.Where(e => e.Kind == StashKind.Texture && e.Purpose != TexturePurpose.Diffuse)
                                 .Select(e => FolderOf(e.Path))
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !_mixedFolders.Contains(relativeFolder);
    }

    /// <summary>The folder part of an entry's relative path, "" for the top.</summary>
    public static string FolderOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>
    /// The files a folder holds, for one without a manifest (an older extraction, or a folder of loose
    /// files the user opened): kinds by extension, purposes by folder and name. Hidden folders are
    /// skipped. Where Unity's .meta files sit beside the files, as in a VRChat avatar's resources, its
    /// .mat materials and prefabs are read too, so textures take the purpose their material gives them,
    /// and a model's meta gives the Unity material each of its materials maps to.
    /// </summary>
    public static StashManifest Scan(string folder, int maxFiles = MaxScanFiles)
    {
        var manifest = new StashManifest { Source = folder, SourceKind = "folder", ExtractedUtc = Directory.GetLastWriteTimeUtc(folder) };
        var files = new List<string>();
        var metas = new List<string>();
        foreach (var file in EnumerateVisible(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                metas.Add(file);
                continue;
            }
            if (IsBookkeeping(name))
                continue;
            if (files.Count == maxFiles)
            {
                manifest.Truncated = true;
                break;
            }
            files.Add(file);
        }

        var unity = metas.Count > 0 ? UnityFolder.Read(files, metas) : null;
        // Names are read from the folder down: the folders above it ("Downloads\Matcap test") say nothing about its files.
        var byName = TexturePurposes.FromFileNames(files.Where(f => KindOf(f) == StashKind.Texture).Select(f => Relative(folder, f)));
        foreach (var file in files)
        {
            var entry = NewEntry(folder, file);
            var used = unity?.UsesOf(file) ?? new List<(string Group, TexturePurpose? Purpose)>();
            foreach (var (group, _) in used)
                if (!entry.Groups.Contains(group))
                    entry.Groups.Add(group);
            if (entry.Kind == StashKind.Texture)
            {
                var named = byName[entry.Path];
                entry.Purpose = unity != null
                    ? TexturePurposes.Classify(null, TexturePurposes.MostCommon(used.Select(u => u.Purpose)), unity.TextureTypeOf(file), named)
                    : FromFolder(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(entry.Path)) ?? "") ?? named;
            }
            else if (entry.Kind == StashKind.Model)
                entry.MaterialNames = unity?.MaterialNamesOf(file);
            manifest.Entries.Add(entry);
        }
        if (unity != null)
            manifest.Materials.AddRange(unity.Materials(folder));
        return manifest;
    }

    /// <summary>Every file under <paramref name="folder"/>, skipping folders whose name starts with a dot (.git and the like).</summary>
    private static IEnumerable<string> EnumerateVisible(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*", options))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(current, "*", options))
                if (!System.IO.Path.GetFileName(sub).StartsWith('.'))
                    pending.Push(sub);
        }
    }

    /// <summary>The Unity side of a folder scan: GUIDs from .meta files, then the materials and prefabs that use them.</summary>
    private sealed class UnityFolder
    {
        private readonly Dictionary<string, string> _fileByGuid = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _textureType = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<UnityMaterial> _materials = new();
        private readonly Dictionary<string, List<(string Group, TexturePurpose? Purpose)>> _uses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _remaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _materialNames = new(StringComparer.OrdinalIgnoreCase);

        public static UnityFolder Read(IReadOnlyList<string> files, IReadOnlyList<string> metas)
        {
            var unity = new UnityFolder();
            foreach (var meta in metas)
            {
                var file = System.IO.Path.GetFullPath(meta[..^".meta".Length]);
                // A model's meta runs long: its rig and clips follow the material remap.
                bool model = KindOf(file) == StashKind.Model;
                var text = ReadSmall(meta, model ? MaxYamlBytes : 64 * 1024) ?? "";
                var (guid, type) = UnityYaml.ReadMeta(text);
                if (guid == null || !File.Exists(file))
                    continue;
                unity._fileByGuid[guid] = file;
                unity._textureType[file] = type;
                if (model)
                    unity._remaps[file] = UnityYaml.ReadMaterialRemap(text);
            }
            var guidOf = unity._fileByGuid.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files.Where(f => System.IO.Path.GetExtension(f).Equals(".mat", StringComparison.OrdinalIgnoreCase)))
                if (ReadSmall(file, MaxYamlBytes) is { } text
                    && UnityYaml.ReadMaterial(guidOf.GetValueOrDefault(System.IO.Path.GetFullPath(file)) ?? file, text) is { } material)
                    unity._materials.Add(material);
            foreach (var material in unity._materials)
                unity._materialNames.TryAdd(material.Guid, material.Name);

            var prefabsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(f => System.IO.Path.GetExtension(f).Equals(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                if (ReadSmall(file, MaxYamlBytes) is not { } text)
                    continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                foreach (var material in UnityYaml.ReadPrefabMaterials(text))
                {
                    if (!prefabsOf.TryGetValue(material, out var names))
                        prefabsOf[material] = names = new();
                    if (!names.Contains(name))
                        names.Add(name);
                }
            }

            foreach (var material in unity._materials)
                foreach (var (property, texture) in material.Textures)
                {
                    if (!unity._fileByGuid.TryGetValue(texture, out var file))
                        continue;
                    if (!unity._uses.TryGetValue(file, out var list))
                        unity._uses[file] = list = new();
                    list.Add((material.Name, TexturePurposes.FromUnityProperty(property)));
                    foreach (var prefab in prefabsOf.GetValueOrDefault(material.Guid) ?? new())
                        list.Add((prefab, null));
                }
            return unity;
        }

        public List<(string Group, TexturePurpose? Purpose)> UsesOf(string file)
            => _uses.GetValueOrDefault(System.IO.Path.GetFullPath(file)) ?? new();

        public int TextureTypeOf(string file) => _textureType.GetValueOrDefault(System.IO.Path.GetFullPath(file), -1);

        public Dictionary<string, string>? MaterialNamesOf(string file)
            => ResolveMaterialNames(_remaps.GetValueOrDefault(System.IO.Path.GetFullPath(file)), _materialNames);

        public IEnumerable<StashMaterial> Materials(string root)
        {
            foreach (var material in _materials)
            {
                var listed = new StashMaterial { Name = material.Name };
                foreach (var (property, texture) in material.Textures)
                    if (TexturePurposes.FromUnityProperty(property) is { } purpose && _fileByGuid.TryGetValue(texture, out var file))
                        listed.Textures.TryAdd(purpose, Relative(root, file));
                if (listed.Textures.Count > 0)
                    yield return listed;
            }
        }

        private static string? ReadSmall(string path, long max)
        {
            try { return new FileInfo(path).Length <= max ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>The <see cref="Sources"/> of an extraction from these packages: none for a single one.</summary>
    public static List<string> SourcesOf(IReadOnlyList<string> packagePaths)
        => packagePaths.Count > 1 ? packagePaths.Select(p => System.IO.Path.GetFullPath(p)).ToList() : new();

    public static StashManifest ForUnity(UnityScanResult scan, UnityExtraction extraction) => ForUnity(new[] { scan }, extraction);

    /// <summary>
    /// A Unity extraction: textures by the material properties that use them, grouped under materials and
    /// prefabs, and models with the Unity material each of their materials maps to. Several packages are
    /// read as one, so a material in one can name a texture (or a model's remap a material) of another.
    /// </summary>
    public static StashManifest ForUnity(IReadOnlyList<UnityScanResult> scans, UnityExtraction extraction)
    {
        var root = extraction.RootFolder;
        var manifest = new StashManifest
        {
            Source = System.IO.Path.GetFullPath(scans[0].PackagePath), Sources = SourcesOf(scans.Select(s => s.PackagePath).ToList()),
            SourceKind = "unity", ExtractedUtc = DateTime.UtcNow,
        };
        // An asset or material shipped in two packages has the same GUID in both: the first package's is kept.
        var materials = scans.SelectMany(s => s.Materials).DistinctBy(m => m.Guid, StringComparer.OrdinalIgnoreCase).ToList();
        var prefabs = scans.SelectMany(s => s.Prefabs).DistinctBy(p => p.Guid, StringComparer.OrdinalIgnoreCase).ToList();

        // Texture GUID → (material, property purpose) for every material property that uses it.
        var uses = new Dictionary<string, List<(UnityMaterial Material, TexturePurpose? Purpose)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials)
            foreach (var (property, texture) in material.Textures)
            {
                if (!uses.TryGetValue(texture, out var list))
                    uses[texture] = list = new();
                list.Add((material, TexturePurposes.FromUnityProperty(property)));
            }
        var prefabsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefab in prefabs)
            foreach (var material in prefab.MaterialGuids)
            {
                if (!prefabsOf.TryGetValue(material, out var names))
                    prefabsOf[material] = names = new();
                if (!names.Contains(prefab.Name))
                    names.Add(prefab.Name);
            }
        // Material GUID → name, for the models' material remaps.
        var materialNames = materials.ToDictionary(m => m.Guid, m => m.Name, StringComparer.OrdinalIgnoreCase);

        var assets = scans.SelectMany(s => s.Assets).DistinctBy(a => a.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(a => a.Guid, StringComparer.OrdinalIgnoreCase);
        var byName = TexturePurposes.FromFileNames(extraction.FileByGuid.Values.Where(f => KindOf(f) == StashKind.Texture).Select(f => Relative(root, f)));
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
                entry.Purpose = TexturePurposes.Classify(null, TexturePurposes.MostCommon(used.Select(u => u.Purpose)),
                    assets.GetValueOrDefault(guid)?.TextureType ?? -1, byName[entry.Path]);
            }
            else if (entry.Kind == StashKind.Model)
                entry.MaterialNames = ResolveMaterialNames(assets.GetValueOrDefault(guid)?.MaterialRemap, materialNames);
            manifest.Entries.Add(entry);
        }

        foreach (var material in materials)
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

    /// <summary>
    /// A model's material remap (its material name → .mat GUID) with each GUID turned into that
    /// material's name. GUIDs of materials not at hand are dropped; null when none is left.
    /// </summary>
    private static Dictionary<string, string>? ResolveMaterialNames(IReadOnlyDictionary<string, string>? remap, IReadOnlyDictionary<string, string> nameByGuid)
    {
        if (remap == null)
            return null;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (model, guid) in remap)
            if (nameByGuid.TryGetValue(guid, out var name) && name.Length > 0)
                names.TryAdd(model, name);
        return names.Count > 0 ? names : null;
    }

    public static StashManifest ForSims(string packagePath, SimsPackageInspector.ExtractionResult extraction) => ForSims(new[] { packagePath }, extraction);

    /// <summary>A Sims extraction (of one package or several): textures by their CAS part's role, grouped under the CAS part.</summary>
    public static StashManifest ForSims(IReadOnlyList<string> packagePaths, SimsPackageInspector.ExtractionResult extraction)
    {
        var root = extraction.RootFolder;
        var manifest = new StashManifest
        {
            Source = System.IO.Path.GetFullPath(packagePaths[0]), Sources = SourcesOf(packagePaths), SourceKind = "sims", ExtractedUtc = DateTime.UtcNow,
        };
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
                    entry.Purpose = TexturePurposes.FromSimsRole(texture.Role) ?? TexturePurposes.FromFileName(entry.Path);
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
                    entry.Purpose = TexturePurposes.FromFileName(entry.Path);
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

    /// <summary>Files the extraction (or Blender) writes for itself, never listed.</summary>
    public static bool IsBookkeeping(string fileName)
        => fileName.Equals(FileName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(ReportName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(SimsPackageInspector.ManifestName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(BlenderExports.FileName, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(BlenderExports.FileName + ".tmp", StringComparison.OrdinalIgnoreCase);

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
