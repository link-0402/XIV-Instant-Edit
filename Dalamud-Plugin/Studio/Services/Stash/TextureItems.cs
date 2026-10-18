using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using InstantEdit.Services.Previews;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Services.Sims;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>One item of an outfit as the stash sees it: the textures that go on one part (a top, the gloves), whatever their purposes.</summary>
internal sealed class TextureItem
{
    public string Name { get; set; } = string.Empty;
    public List<StashEntry> Textures { get; } = new();

    /// <summary>Textures put in because their layout matches the item's, not by name or material.</summary>
    public HashSet<StashEntry> ByLayout { get; } = new();

    /// <summary>File names of the models whose materials use the item's textures.</summary>
    public List<string> Models { get; } = new();
}

/// <summary>A stash's textures by item, then its matcaps, then what belongs to no item.</summary>
internal sealed class TextureItemList
{
    public List<TextureItem> Items { get; } = new();
    public List<StashEntry> Matcaps { get; } = new();
    public List<StashEntry> Loose { get; } = new();
}

/// <summary>
/// Sorts a stash's textures into items. Names come first: a texture set ("Shirt", "Shirt_Normal",
/// "Shirt_AO") is one item. The folder's .mtrl files join textures a material uses together when
/// their names don't (a normal map in one folder, its index map in another), and its .mdl files say
/// which models an item is on. Last, a texture left over joins the item whose layout (where its
/// UV islands are painted) it clearly matches. Matcaps are never part of an item.
/// </summary>
internal static class TextureItems
{
    private const int Grid = 64, Work = 256;
    private const double LayoutMatch = 0.6, LayoutMargin = 0.1, MinFill = 0.05, MaxFill = 0.95;
    private const long MaxMaterialBytes = 1024 * 1024;
    private const int MaxLayoutImages = 120;

    private static readonly HashSet<string> LayoutExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tex", ".dds" };

    public static TextureItemList Build(StashManifest manifest, string root, bool layout = true, CancellationToken cancel = default)
    {
        var list = new TextureItemList();
        var candidates = new List<StashEntry>();
        foreach (var entry in manifest.Entries.Where(e => e.Kind == StashKind.Texture))
            (entry.Purpose == TexturePurpose.Matcap ? list.Matcaps : candidates).Add(entry);

        // Texture sets by name, per folder.
        var full = candidates.ToDictionary(e => e, e => Full(root, e.Path));
        var setNames = TexturePurposes.SetNames(full.Values);
        var setOf = candidates.ToDictionary(e => e, e => setNames[full[e]]);
        var groups = new UnionFind<StashEntry>(candidates);
        foreach (var set in candidates.GroupBy(e => (StashManifest.FolderOf(e.Path), setOf[e].ToLowerInvariant())))
            foreach (var entry in set.Skip(1))
                groups.Union(set.First(), entry);

        // Materials join what they use together, unless that would merge two texture sets.
        var materialsOf = ReadMaterials(manifest, root, candidates, cancel);
        foreach (var uses in materialsOf.SelectMany(p => p.Value.Select(m => (Material: m, Entry: p.Key)))
                                        .GroupBy(p => p.Material, p => p.Entry, StringComparer.OrdinalIgnoreCase))
        {
            var used = uses.Distinct().ToList();
            if (used.Select(groups.Find).Distinct().Count(r => groups.Size(r) > 1) <= 1)
                foreach (var entry in used.Skip(1))
                    groups.Union(used[0], entry);
        }

        foreach (var group in candidates.GroupBy(groups.Find))
        {
            if (group.Count() < 2)
            {
                list.Loose.Add(group.Key);
                continue;
            }
            var item = new TextureItem
            {
                Name = group.GroupBy(e => setOf[e], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).First().Key,
            };
            item.Textures.AddRange(group);
            list.Items.Add(item);
        }

        if (layout && list.Items.Count > 0)
            MatchLayouts(list, full, cancel);

        ShortenNames(list.Items);
        AddModels(manifest, root, list.Items, materialsOf, cancel);
        foreach (var item in list.Items)
            item.Textures.Sort(ByPurposeThenName);
        list.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        list.Loose.Sort(ByPurposeThenName);
        list.Matcaps.Sort(ByPurposeThenName);
        return list;
    }

    private static int ByPurposeThenName(StashEntry a, StashEntry b)
    {
        int order = a.Purpose.CompareTo(b.Purpose);
        return order != 0 ? order : string.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path), StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ─────────────────────────────────────────────────────────────────────────
    // Materials and models
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>For each texture the .mtrl files (by name) that use a texture of its file name.</summary>
    private static Dictionary<StashEntry, List<string>> ReadMaterials(StashManifest manifest, string root, List<StashEntry> textures, CancellationToken cancel)
    {
        var byFileName = textures.ToLookup(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<StashEntry, List<string>>();
        foreach (var material in manifest.Entries.Where(e => e.Kind == StashKind.Material))
        {
            cancel.ThrowIfCancellationRequested();
            var info = TryRead(Full(root, material.Path), MaxMaterialBytes, bytes => MtrlReader.LooksLikeMtrl(bytes) ? MtrlReader.Read(bytes) : null);
            if (info == null)
                continue;
            var name = Path.GetFileNameWithoutExtension(material.Path);
            foreach (var texture in info.TextureOffsets)
                foreach (var entry in byFileName[Path.GetFileName(texture.Path ?? string.Empty)])
                {
                    if (!result.TryGetValue(entry, out var names))
                        result[entry] = names = new();
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
        }
        return result;
    }

    /// <summary>
    /// The models each item is on: those with a material that uses one of its textures, or, where the
    /// folder has no .mtrl files (a port before its materials are made), those with a material named
    /// like one of its textures.
    /// </summary>
    private static void AddModels(StashManifest manifest, string root, List<TextureItem> items,
        Dictionary<StashEntry, List<string>> materialsOf, CancellationToken cancel)
    {
        var models = new List<(string File, List<string> Materials)>();
        foreach (var model in manifest.Entries.Where(e => e.Kind == StashKind.Model && e.Path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(Full(root, model.Path));
                if (RefitModel.ReadMaterialNames(stream) is { } names)
                    models.Add((Path.GetFileName(model.Path), names.Select(n => Path.GetFileNameWithoutExtension(n.Replace('\\', '/').Split('/')[^1])).ToList()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or OverflowException
                                          or ArgumentException or IndexOutOfRangeException)
            {
                // a model that can't be read just names no item
            }
        }
        if (models.Count == 0)
            return;

        // Without .mtrl files, a material names an item when its telling words ("Sera_Yura_Cow" → "cow";
        // words every item's names share, like the outfit's, say nothing) all appear in one of the
        // item's texture names ("Sera_Yura_Elbow_Glove&Stocking_Cow").
        var wordsOf = items.ToDictionary(i => i, i => i.Textures.Select(t => TexturePurposes.Words(Path.GetFileNameWithoutExtension(t.Path)).ToHashSet()).ToList());
        var shared = items.Count > 1
            ? wordsOf.Values.Select(l => l.SelectMany(w => w).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; })
            : new HashSet<string>();
        bool Names(string material, TextureItem item)
        {
            var telling = TexturePurposes.Words(material).Where(w => w != "mt" && !shared.Contains(w)).ToList();
            return telling.Count > 0 && wordsOf[item].Any(words => telling.All(words.Contains));
        }

        bool linked = materialsOf.Count > 0;
        foreach (var item in items)
        {
            var used = item.Textures.SelectMany(t => materialsOf.GetValueOrDefault(t) ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, materials) in models)
                if (linked ? materials.Any(used.Contains) : materials.Any(m => Names(m, item)))
                    item.Models.Add(file);
        }
    }

    /// <summary>Drops the start every item's name shares ("Sera_Yura_Bikini", "Sera_Yura_Shirt" → "Bikini", "Shirt").</summary>
    private static void ShortenNames(List<TextureItem> items)
    {
        if (items.Count < 2)
            return;
        var first = items[0].Name;
        int common = first.Length;
        foreach (var item in items.Skip(1))
        {
            int i = 0;
            while (i < common && i < item.Name.Length && char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(item.Name[i]))
                i++;
            common = i;
        }
        // Only up to the last separator inside the shared part, so words stay whole.
        while (common > 0 && char.IsLetterOrDigit(first[common - 1]))
            common--;
        if (common == 0)
            return;
        foreach (var item in items)
        {
            var shorter = item.Name[common..].TrimStart(' ', '_', '-', '.');
            if (shorter.Length > 0)
                item.Name = shorter;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Moves loose textures into the item whose layout they clearly share.</summary>
    private static void MatchLayouts(TextureItemList list, Dictionary<StashEntry, string> full, CancellationToken cancel)
    {
        var loose = list.Loose.Where(e => LayoutExtensions.Contains(Path.GetExtension(e.Path)) && !TexturePurposes.IsPreview(e.Path))
                              .Take(MaxLayoutImages).ToList();
        if (loose.Count == 0)
            return;

        var items = new List<(TextureItem Item, Layout Layout)>();
        foreach (var item in list.Items.Take(MaxLayoutImages))
        {
            // A normal map shows an item's islands best; then the colours.
            var pick = item.Textures.Where(t => LayoutExtensions.Contains(Path.GetExtension(t.Path)))
                                    .OrderBy(t => t.Purpose switch { TexturePurpose.Normal => 0, TexturePurpose.Diffuse => 1, _ => 2 })
                                    .FirstOrDefault();
            if (pick != null && LayoutOf(full[pick], cancel) is { Telling: true } layoutOfItem)
                items.Add((item, layoutOfItem));
        }
        if (items.Count == 0)
            return;

        foreach (var entry in loose)
        {
            if (LayoutOf(full[entry], cancel) is not { Telling: true } layoutOfEntry)
                continue;
            var ranked = items.Select(i => (i.Item, Overlap: layoutOfEntry.Overlap(i.Layout))).OrderByDescending(r => r.Overlap).ToList();
            var second = ranked.Count > 1 ? ranked[1].Overlap : 0;
            if (ranked[0].Overlap < LayoutMatch || ranked[0].Overlap - second < LayoutMargin)
                continue;
            ranked[0].Item.Textures.Add(entry);
            ranked[0].Item.ByLayout.Add(entry);
            list.Loose.Remove(entry);
        }
    }

    /// <summary>Where a texture is painted: the cells of a coarse grid that hold noticeable detail.</summary>
    internal sealed class Layout
    {
        public Layout(bool[] cells)
        {
            Cells = cells;
            Fill = cells.Count(c => c) / (double)cells.Length;
        }

        public bool[] Cells { get; }
        public double Fill { get; }

        /// <summary>Whether the layout says anything: not (nearly) empty, not painted everywhere.</summary>
        public bool Telling => Fill is >= MinFill and <= MaxFill;

        /// <summary>Intersection over union of the painted cells.</summary>
        public double Overlap(Layout other)
        {
            int both = 0, either = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] && other.Cells[i]) both++;
                if (Cells[i] || other.Cells[i]) either++;
            }
            return either == 0 ? 0 : both / (double)either;
        }

        /// <summary>
        /// The layout of an RGBA image of <see cref="Work"/> by <see cref="Work"/> pixels: colour (dimmed
        /// where transparent) and alpha gradients, summed into <see cref="Grid"/> cells, a cell counting as
        /// painted when it holds more than a quarter of the average cell's.
        /// </summary>
        public static Layout FromPixels(Rgba32[] pixels)
        {
            var energy = new double[Grid * Grid];
            const int cell = Work / Grid;
            for (int y = 0; y < Work; y++)
                for (int x = 0; x < Work; x++)
                {
                    var p = pixels[y * Work + x];
                    var right = pixels[y * Work + Math.Min(x + 1, Work - 1)];
                    var down = pixels[Math.Min(y + 1, Work - 1) * Work + x];
                    energy[(y / cell) * Grid + x / cell] += Difference(p, right) + Difference(p, down);
                }
            var total = energy.Sum();
            var threshold = total / energy.Length * 0.25;
            return new Layout(energy.Select(e => total > 0 && e > threshold).ToArray());
        }

        private static double Difference(Rgba32 a, Rgba32 b)
        {
            double aa = a.A / 255.0, ba = b.A / 255.0;
            return (Math.Abs(a.R * aa - b.R * ba) + Math.Abs(a.G * aa - b.G * ba) + Math.Abs(a.B * aa - b.B * ba)) / 255.0 + Math.Abs(aa - ba);
        }
    }

    private static readonly DecoderOptions SmallImages = new()
    {
        Configuration = StudioImages.Decoder.Configuration,
        SkipMetadata = true,
        TargetSize = new Size(Work, Work),
    };

    private static Layout? LayoutOf(string path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            using var image = LoadSmall(path);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(Work, Work), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Box }));
            var pixels = new Rgba32[Work * Work];
            image.CopyPixelDataTo(pixels);
            return Layout.FromPixels(pixels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // an image that can't be read just stays loose
        }
    }

    private static Image<Rgba32> LoadSmall(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".tex":
            {
                var decoded = TextureDecoder.Decode(File.ReadAllBytes(path), Work);
                using var bgra = Image.LoadPixelData<Bgra32>(decoded.Bgra, decoded.Width, decoded.Height);
                return bgra.CloneAs<Rgba32>();
            }
            case ".dds":
                return SimsTextureDecoder.Decode(SimsResourceType.ImgDds, File.ReadAllBytes(path)).ToImage();
            default:
                return Image.Load<Rgba32>(SmallImages, path);
        }
    }

    private static T? TryRead<T>(string path, long maxBytes, Func<byte[], T?> read) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= maxBytes ? read(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
                                      or ArgumentException or NotSupportedException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Disjoint sets with sizes, for joining textures into items.</summary>
    private sealed class UnionFind<T> where T : notnull
    {
        private readonly Dictionary<T, T> _parent = new();
        private readonly Dictionary<T, int> _size = new();

        public UnionFind(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                _parent[item] = item;
                _size[item] = 1;
            }
        }

        public T Find(T item)
        {
            while (!EqualityComparer<T>.Default.Equals(_parent[item], item))
                item = _parent[item] = _parent[_parent[item]];
            return item;
        }

        public int Size(T root) => _size[root];

        public void Union(T a, T b)
        {
            a = Find(a);
            b = Find(b);
            if (EqualityComparer<T>.Default.Equals(a, b))
                return;
            if (_size[a] < _size[b])
                (a, b) = (b, a);
            _parent[b] = a;
            _size[a] += _size[b];
        }
    }
}
