using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// The stash's textures for the materials a model names: a model Blender saved into the stash (or any
/// model of a stash folder) dropped on a Build item finds each material's textures in the package it
/// came from. Reads the stash's files, and nothing of the game or the session.
/// </summary>
internal sealed class ModelTextureLookup
{
    private static readonly IReadOnlyDictionary<TexturePurpose, string> None = new Dictionary<TexturePurpose, string>();

    /// <summary>The number Blender gives a second material of the same name ("Body.001").</summary>
    private static readonly Regex BlenderSuffix = new(@"\.\d{3}$", RegexOptions.CultureInvariant);

    /// <summary>The source model's entry: its Unity material remap, when it has one.</summary>
    private readonly StashEntry? _source;
    private readonly CancellationToken _cancel;
    private (TextureItemList Items, TextureItemNames Names)? _items;

    private ModelTextureLookup(string? folder, StashManifest? manifest, StashEntry? source, CancellationToken cancel)
    {
        Folder   = folder;
        Manifest = manifest;
        _source  = source;
        _cancel  = cancel;
    }

    /// <summary>The stash folder the textures come from; null when the model is in none, so no material finds any.</summary>
    public string? Folder { get; }

    /// <summary>That folder's files: its stash.json (or a scan of it) with Blender's models added.</summary>
    public StashManifest? Manifest { get; }

    /// <summary>
    /// The lookup for <paramref name="modelPath"/>. The textures come from, first that applies: the stash
    /// folder holding the first source of a model Blender saved (the package it came from), the stash
    /// folder holding the model, or none.
    /// </summary>
    /// <param name="stashRoots">The folders extractions sit in: the importer's output folder and the cache's package-imports.</param>
    /// <param name="openedFolders">The folders of loose files the user opened in the stash.</param>
    public static ModelTextureLookup Open(string modelPath, IEnumerable<string> stashRoots, IEnumerable<string> openedFolders,
        CancellationToken cancel = default)
    {
        var roots  = stashRoots.ToList();
        var opened = openedFolders.ToList();
        if (Full(modelPath) is not { } model || FolderHolding(model, roots, opened) is not { } modelFolder)
            return new ModelTextureLookup(null, null, null, cancel);

        string folder = modelFolder, source = model;
        if (ExportOf(modelFolder, model) is { Sources.Count: > 0 } export && Full(export.Sources[0]) is { } first
            && FolderHolding(first, roots, opened) is { } sourceFolder)
        {
            folder = sourceFolder;
            source = first;
        }

        var manifest = ReadManifest(folder);
        var relative = Relative(folder, source);
        var entry = manifest?.Entries.FirstOrDefault(e => string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase));
        return new ModelTextureLookup(folder, manifest, entry, cancel);
    }

    /// <summary>
    /// The stash folder <paramref name="file"/> is in: the extraction right inside one of
    /// <paramref name="stashRoots"/> that holds it, else the deepest opened folder holding it; null for none.
    /// </summary>
    public static string? FolderHolding(string file, IReadOnlyList<string> stashRoots, IReadOnlyList<string> openedFolders)
    {
        if (Full(file) is not { } full)
            return null;
        foreach (var root in stashRoots)
        {
            if (Full(root) is not { } top || Inside(top, full) is not { } relative)
                continue;
            int cut = relative.IndexOf(Path.DirectorySeparatorChar);
            if (cut <= 0)
                continue;
            var folder = Path.Combine(top, relative[..cut]);
            if (StashFolders.IsExtraction(folder))
                return folder;
        }
        return openedFolders.Select(Full).OfType<string>()
                            .Where(o => Inside(o, full) != null)
                            .OrderByDescending(o => o.Length)
                            .FirstOrDefault();
    }

    /// <summary>A model's material as the stash names it: its file name without .mtrl ("/Body.mtrl" → "Body").</summary>
    public static string NameOf(string referenced)
    {
        var name = referenced.Trim().Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? name[..^".mtrl".Length] : name;
    }

    /// <summary>
    /// The textures for one of the model's materials, by purpose, as full paths. First that has it: the
    /// Unity material the source model's remap gives the name, the stash material of the same name
    /// (ignoring case and Blender's ".001"), the texture set the name's telling words point at (see
    /// <see cref="TextureItemNames"/>). Empty when none does.
    /// </summary>
    public IReadOnlyDictionary<TexturePurpose, string> TexturesFor(string referenced)
    {
        if (Folder == null || Manifest == null)
            return None;
        var name = NameOf(referenced);
        var names = BlenderSuffix.IsMatch(name) ? new[] { name, BlenderSuffix.Replace(name, string.Empty) } : new[] { name };

        if (_source?.MaterialNames is { } remap)
            foreach (var candidate in names)
                foreach (var (model, unity) in remap)
                    if (string.Equals(model, candidate, StringComparison.OrdinalIgnoreCase) && MaterialNamed(unity) is { } remapped)
                        return Absolute(remapped.Textures);
        foreach (var candidate in names)
            if (MaterialNamed(candidate) is { } named)
                return Absolute(named.Textures);
        return Absolute(ByWords(name));
    }

    private StashMaterial? MaterialNamed(string name)
        => Manifest!.Materials.FirstOrDefault(m => m.Name == name)
        ?? Manifest.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The textures of the item the name's telling words point at: of several, one with a diffuse, then
    /// the one with a texture named in the fewest words. Of each purpose, a texture whose name holds the
    /// telling words comes first (the "Cow" of a set with a plain and a cow diffuse), of those the one
    /// named in the fewest words.
    /// </summary>
    private IReadOnlyDictionary<TexturePurpose, string> ByWords(string name)
    {
        var (list, names) = Items();
        var telling = names.TellingWords(name);
        bool Holds(StashEntry texture) => telling.All(TextureItemNames.WordsOf(texture.Path).Contains);
        int Words(StashEntry texture) => TextureItemNames.WordsOf(texture.Path).Count;

        TextureItem? best = null;
        (bool NoDiffuse, int Words) fewest = (true, int.MaxValue);
        foreach (var item in list.Items.Where(i => names.Names(name, i)))
        {
            var rank = (!item.Textures.Exists(t => t.Purpose == TexturePurpose.Diffuse), item.Textures.Where(Holds).Min(Words));
            if (best == null || rank.CompareTo(fewest) < 0)
                (best, fewest) = (item, rank);
        }
        if (best == null)
            return None;

        var textures = new Dictionary<TexturePurpose, string>();
        foreach (var texture in best.Textures.Where(t => t.Purpose is not (TexturePurpose.Other or TexturePurpose.Matcap))
                                             .OrderBy(t => Holds(t) ? 0 : 1).ThenBy(t => Holds(t) ? Words(t) : 0))
            textures.TryAdd(texture.Purpose, texture.Path);
        return textures;
    }

    /// <summary>The folder's textures by item, sorted once on first use: reading their images takes a moment.</summary>
    private (TextureItemList Items, TextureItemNames Names) Items()
    {
        if (_items is { } done)
            return done;
        TextureItemList list;
        try
        {
            list = TextureItems.Build(Manifest!, Folder!, layout: true, _cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            list = new TextureItemList();   // a folder that can't be sorted just names nothing by words
        }
        _items = (list, new TextureItemNames(list.Items));
        return _items.Value;
    }

    private IReadOnlyDictionary<TexturePurpose, string> Absolute(IReadOnlyDictionary<TexturePurpose, string> textures)
    {
        var full = new Dictionary<TexturePurpose, string>();
        foreach (var (purpose, path) in textures)
            if (Full(Path.Combine(Folder!, path.Replace('/', Path.DirectorySeparatorChar))) is { } absolute)
                full[purpose] = absolute;
        return full;
    }

    /// <summary>The Blender sidecar's entry for <paramref name="model"/>, saved into <paramref name="folder"/>.</summary>
    private static BlenderExport? ExportOf(string folder, string model)
    {
        var relative = Relative(folder, model);
        return BlenderExports.Read(folder).FirstOrDefault(e => string.Equals(e.File, relative, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The folder's stash.json, or a scan of it, with Blender's models added; null when it can't be read.</summary>
    private static StashManifest? ReadManifest(string folder)
    {
        try
        {
            var manifest = StashManifest.Read(folder) ?? StashManifest.Scan(folder);
            BlenderExports.Merge(manifest, folder);
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');

    /// <summary><paramref name="full"/>'s path inside <paramref name="folder"/>, or null when it isn't in it.</summary>
    private static string? Inside(string folder, string full)
    {
        var root = folder + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length ? full[root.Length..] : null;
    }

    /// <summary>A typed path made full, without a trailing separator; null for one that can't be read.</summary>
    private static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
