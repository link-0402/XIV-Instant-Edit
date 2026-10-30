using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// Which of a stash's items a material's name points at when no .mtrl file says: the items with a
/// texture name that holds all the material's telling words ("Sera_Yura_Cow" → "cow"; "mt" and the
/// words every item's names share, like the outfit's, say nothing), as
/// "Sera_Yura_Elbow_Glove&amp;Stocking_Cow" does. A word keeps its digits while some item's names hold
/// it so ("UV1" beside "UV2" and "UV3"); else they are dropped ("Pearl 1" → "pearl"). Gives the models an
/// item is on, and the textures a model's material gets (see <see cref="ModelTextureLookup"/>).
/// </summary>
internal sealed class TextureItemNames
{
    private readonly WordIndex _plain, _numbered;

    public TextureItemNames(IReadOnlyList<TextureItem> items)
    {
        _plain    = new WordIndex(items, numbered: false);
        _numbered = new WordIndex(items, numbered: true);
    }

    /// <summary>The words of a texture's file name, as <see cref="Names"/> reads them; with <paramref name="numbered"/>, with their digits.</summary>
    public static HashSet<string> WordsOf(string texturePath, bool numbered = false)
        => TexturePurposes.Words(Path.GetFileNameWithoutExtension(texturePath), numbered).ToHashSet();

    /// <summary>The words of a material's name that can tell the items apart, and whether they keep their digits.</summary>
    public (List<string> Words, bool Numbered) TellingWords(string material)
    {
        var numbered = _numbered.Telling(material);
        return numbered.Count > 0 && _numbered.AnyHolds(numbered) ? (numbered, true) : (_plain.Telling(material), false);
    }

    /// <summary>Whether the material's telling words all appear in one of the item's texture names.</summary>
    public bool Names(string material, TextureItem item)
    {
        var (telling, numbered) = TellingWords(material);
        return telling.Count > 0 && (numbered ? _numbered : _plain).Holds(item, telling);
    }

    /// <summary>
    /// The words of each item's texture names, read one way, and those every item shares. A texture that joined
    /// by its layout doesn't name the item: it may be another's that looks alike.
    /// </summary>
    private sealed class WordIndex
    {
        private readonly bool _numbered;
        private readonly Dictionary<TextureItem, List<HashSet<string>>> _wordsOf;
        private readonly HashSet<string> _shared;

        public WordIndex(IReadOnlyList<TextureItem> items, bool numbered)
        {
            _numbered = numbered;
            _wordsOf = items.ToDictionary(i => i, i => i.Textures.Where(t => !i.ByLayout.Contains(t)).Select(t => WordsOf(t.Path, numbered)).ToList());
            _shared = items.Count > 1
                ? _wordsOf.Values.Select(l => l.SelectMany(w => w).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; })
                : new HashSet<string>();
        }

        public List<string> Telling(string material)
            => TexturePurposes.Words(material, _numbered).Where(w => w != "mt" && !_shared.Contains(w)).ToList();

        public bool Holds(TextureItem item, List<string> telling) => _wordsOf[item].Any(words => telling.All(words.Contains));

        public bool AnyHolds(List<string> telling) => _wordsOf.Keys.Any(item => Holds(item, telling));
    }
}

/// <summary>
/// Sorts a stash's textures into items. Names come first: a texture set ("Shirt", "Shirt_Normal",
/// "Shirt_AO") is one item. The folder's .mtrl files join textures a material uses together when
/// their names don't (a normal map in one folder, its index map in another), a Unity material pulls
/// the loose textures it uses into its set, and the .mdl files say which models an item is on. Last,
/// a texture left over joins the item whose layout (where its UV islands are painted) it clearly
/// matches. Matcaps are never part of an item.
/// </summary>
internal static class TextureItems
{
    private const int Grid = 64, Work = 256;
    private const double LayoutMatch = 0.6, LayoutMargin = 0.1, MinFill = 0.05, MaxFill = 0.95;
    private const double MinBackground = 0.1, MaxBackground = 0.98;
    private const byte Dark = 8;
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

        // A Unity material pulls the loose textures it uses into the one texture set among them ("UV1 Black
        // Gold" puts UV1_Black with UV1_Normal2). Its colour variants share textures in ways that would
        // chain loose ones into one item, so it makes none of its own.
        var byPath = candidates.ToLookup(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var material in manifest.Materials)
        {
            var used = material.Textures.Values.SelectMany(p => byPath[p]).Distinct().ToList();
            var sets = used.Select(groups.Find).Distinct().Where(r => groups.Size(r) > 1).ToList();
            if (sets.Count == 1)
                foreach (var entry in used)
                    groups.Union(sets[0], entry);
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

        // Without .mtrl files, a material names an item by its telling words (see TextureItemNames).
        var naming = new TextureItemNames(items);
        bool linked = materialsOf.Count > 0;
        foreach (var item in items)
        {
            var used = item.Textures.SelectMany(t => materialsOf.GetValueOrDefault(t) ?? new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, materials) in models)
                if (linked ? materials.Any(used.Contains) : materials.Any(m => naming.Names(m, item)))
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

    /// <summary>
    /// Moves loose textures into the item whose layout they clearly share: more than any item of another name
    /// (a set in several folders is alike in each). A texture is held against the items' textures of its own
    /// purpose (a colour variant against their diffuses): a normal map painted all over, as Substance Painter's
    /// are, looks like every item's. An item with none of that purpose goes by its normal map, then its colours.
    /// </summary>
    private static void MatchLayouts(TextureItemList list, Dictionary<StashEntry, string> full, CancellationToken cancel)
    {
        var loose = list.Loose.Where(e => LayoutExtensions.Contains(Path.GetExtension(e.Path)) && !TexturePurposes.IsPreview(e.Path))
                              .Take(MaxLayoutImages).ToList();
        if (loose.Count == 0)
            return;

        // As the items were before any texture joined them by layout, each image read once.
        var items = list.Items.Take(MaxLayoutImages)
                        .Select(i => (Item: i, Images: i.Textures.Where(t => LayoutExtensions.Contains(Path.GetExtension(t.Path))).ToList()))
                        .Where(i => i.Images.Count > 0)
                        .ToList();
        if (items.Count == 0)
            return;
        var layouts = new Dictionary<StashEntry, Layout?>();
        Layout? Read(StashEntry texture)
        {
            if (!layouts.TryGetValue(texture, out var layout))
                layouts[texture] = layout = LayoutOf(full[texture], cancel) is { Telling: true } telling ? telling : null;
            return layout;
        }
        Layout? LayoutAs(List<StashEntry> images, TexturePurpose purpose)
            => Read(images.FirstOrDefault(t => t.Purpose == purpose)
                    ?? images.OrderBy(t => t.Purpose switch { TexturePurpose.Normal => 0, TexturePurpose.Diffuse => 1, _ => 2 }).First());

        foreach (var entry in loose)
        {
            if (Read(entry) is not { } layoutOfEntry)
                continue;
            var ranked = items.Select(i => (i.Item, Layout: LayoutAs(i.Images, entry.Purpose)))
                              .Where(i => i.Layout != null)
                              .Select(i => (i.Item, Overlap: layoutOfEntry.Overlap(i.Layout!)))
                              .OrderByDescending(r => r.Overlap)
                              .ToList();
            if (ranked.Count == 0)
                continue;
            var second = ranked.Skip(1).FirstOrDefault(r => !string.Equals(r.Item.Name, ranked[0].Item.Name, StringComparison.OrdinalIgnoreCase)).Overlap;
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

        /// <summary>Of the cells <paramref name="other"/> paints, the share this one paints too; 0 when it paints none.</summary>
        public double Covers(Layout other)
        {
            int both = 0, theirs = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (!other.Cells[i]) continue;
                theirs++;
                if (Cells[i]) both++;
            }
            return theirs == 0 ? 0 : both / (double)theirs;
        }

        /// <summary>
        /// Where triangles of a UV map lie, wrapped into the 0-1 square with the game's top-left origin: the
        /// cells whose centre a triangle covers, and the cell a triangle too small to cover one starts in.
        /// </summary>
        public static Layout FromUvTriangles(IEnumerable<(Vector2 A, Vector2 B, Vector2 C)> triangles)
        {
            var cells = new bool[Grid * Grid];
            foreach (var (a, b, c) in triangles)
            {
                var (pa, pb, pc) = (a * Grid, b * Grid, c * Grid);
                if (!float.IsFinite(pa.X + pa.Y + pb.X + pb.Y + pc.X + pc.Y))
                    continue;
                var min = Vector2.Min(pa, Vector2.Min(pb, pc));
                var max = Vector2.Max(pa, Vector2.Max(pb, pc));
                if (max.X - min.X > Grid * 4 || max.Y - min.Y > Grid * 4)
                    continue;   // a stray vertex far outside the map, not an island
                Mark(cells, (int)MathF.Floor(pa.X), (int)MathF.Floor(pa.Y));
                for (int y = (int)MathF.Floor(min.Y); y < (int)MathF.Ceiling(max.Y); y++)
                    for (int x = (int)MathF.Floor(min.X); x < (int)MathF.Ceiling(max.X); x++)
                        if (Inside(new Vector2(x + 0.5f, y + 0.5f), pa, pb, pc))
                            Mark(cells, x, y);
            }
            return new Layout(cells);
        }

        private static void Mark(bool[] cells, int x, int y)
            => cells[(((y % Grid) + Grid) % Grid) * Grid + ((x % Grid) + Grid) % Grid] = true;

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
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

    /// <summary>
    /// Where an image's UV islands are, read off a flat background: the cells holding a quarter or more pixels
    /// that are neither transparent nor near-black. An ambient occlusion or mask map baked by Substance Painter
    /// has such a background; its colour and normal maps are dilated across the whole image, so they give null,
    /// as does an image that is (nearly) all background or none.
    /// </summary>
    public static Layout? IslandsOf(string path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!LayoutExtensions.Contains(Path.GetExtension(path)))
            return null;
        Rgba32[] pixels;
        try
        {
            using var image = LoadSmall(path);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(Work, Work), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.NearestNeighbor }));
            pixels = new Rgba32[Work * Work];
            image.CopyPixelDataTo(pixels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        static bool Background(Rgba32 p) => p.A <= Dark || (p.R <= Dark && p.G <= Dark && p.B <= Dark);
        var background = pixels.Count(Background) / (double)pixels.Length;
        if (background is < MinBackground or > MaxBackground)
            return null;
        const int cell = Work / Grid;
        var painted = new int[Grid * Grid];
        for (int y = 0; y < Work; y++)
            for (int x = 0; x < Work; x++)
                if (!Background(pixels[y * Work + x]))
                    painted[(y / cell) * Grid + x / cell]++;
        return new Layout(painted.Select(p => p * 4 >= cell * cell).ToArray());
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
