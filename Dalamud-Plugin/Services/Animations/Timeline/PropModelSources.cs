using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Services;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Models from other mods as props: which of a mod's models can be one, whether it rides a single bone, the
/// private weapon body (<c>b9000</c>..<c>b9999</c>, which the game doesn't ship) it gets in the animation's mod, and
/// the source of every file of that private copy.
/// </summary>
internal static partial class PropModelSources
{
    // The set the game's own still props use (Eat Rice Ball is w9901 b0026). Seen in game 2026-10-29: a summon on w9001 never
    // turns visible, shipped body or private, with or without a weapon animation or a C094 show; on w9901 the same copy does,
    // on a private body without an IMC too.
    private const ushort FallbackSet = 9901;
    private const ushort HiddenSet = 9001;
    private const ushort FirstBody = 9000;
    private const ushort LastBody = 9999;
    private const int BodyCount = LastBody - FirstBody + 1;

    private static readonly string[] CandidateRoots = ["chara/weapon/", "chara/monster/", "chara/demihuman/"];

    /// <summary>
    /// Whether <paramref name="ev"/> is the summon of a prop from a mod: a C198 with a model and a source, or on a private
    /// body (b9000..b9999) without one, such as one an earlier save wrote (as <see cref="Taken"/> counts them).
    /// </summary>
    internal static bool FromMod(TimelineEvent ev) =>
        ev.Magic == "C198" && ev.Values is { } values && values.GetValueOrDefault("Model") > 0 &&
        (ev.Source != null || values.GetValueOrDefault("Body") is >= FirstBody and <= LastBody);

    /// <summary>The weapon, monster and demihuman models among a mod's effective files, sorted.</summary>
    internal static ImmutableArray<string> Candidates(IReadOnlyDictionary<string, string> effective)
        => [..effective.Keys
            .Where(path => path.EndsWith(".mdl", StringComparison.Ordinal) &&
                           CandidateRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The weapon set a prop goes to: a weapon keeps its own, except one of w9001, whose summons the game never shows;
    /// that and anything else go to 9901.
    /// </summary>
    internal static ushort TargetSet(string sourceModel)
        => WeaponSet().Match(Normalise(sourceModel)) is { Success: true } match && ushort.Parse(match.Groups[1].Value) is var set && set != HiddenSet
            ? set
            : FallbackSet;

    /// <summary>
    /// Whether a model can be a prop: a weapon always; anything else only when its meshes are skinned to
    /// <c>n_root</c> or <c>n_hara</c> alone, the bones a weapon model has.
    /// </summary>
    internal static bool Qualifies(string sourceModel, byte[] mdl)
    {
        if (Normalise(sourceModel).StartsWith("chara/weapon/", StringComparison.Ordinal))
            return true;
        VanillaModelInfo info;
        try { info = VanillaModelReader.Read(mdl); }
        catch { return false; }
        foreach (var table in info.BoneTables)
            foreach (var index in table.BoneIndex)
            {
                if (index >= info.BoneNames.Length)
                    return false;
                var bone = info.BoneNames[index];
                if (!string.Equals(bone, "n_root", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(bone, "n_hara", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        return true;
    }

    /// <summary>The lowest body 9000..9999 of <paramref name="set"/> the game doesn't ship and nothing has taken.</summary>
    internal static ushort? AllocateBody(ushort set, Func<string, bool> gameExists, IReadOnlySet<(ushort Set, ushort Body)> taken)
        => AllocateBody(set, FirstBody, gameExists, taken, _ => false);

    /// <summary>
    /// The first body of <paramref name="set"/> from <paramref name="start"/> upward, 9999 wrapping to 9000, that isn't
    /// in <paramref name="taken"/>, the game doesn't ship and isn't <paramref name="busyOutside"/> the timeline (asked last);
    /// null when none of the 1000 is free.
    /// </summary>
    internal static ushort? AllocateBody(ushort set, ushort start, Func<string, bool> gameExists, IReadOnlySet<(ushort Set, ushort Body)> taken,
        Func<ushort, bool> busyOutside)
    {
        foreach (var body in Walk(start))
            if (!taken.Contains((set, body)) && !gameExists(ImcPath(set, body)) && !busyOutside(body))
                return body;
        return null;
    }

    /// <summary>The 1000 private bodies from <paramref name="start"/> upward, 9999 wrapping to 9000.</summary>
    private static IEnumerable<ushort> Walk(ushort start)
    {
        var first = start is >= FirstBody and <= LastBody ? start - FirstBody : 0;
        for (var i = 0; i < BodyCount; ++i)
            yield return (ushort)(FirstBody + (first + i) % BodyCount);
    }

    private static string ImcPath(ushort set, ushort body) => $"chara/weapon/w{set:D4}/obj/body/b{body:D4}/b{body:D4}.imc";

    /// <summary>The private model's game path of weapon <paramref name="set"/> body <paramref name="body"/>.</summary>
    internal static string ModelPath(ushort set, ushort body) => $"chara/weapon/w{set:D4}/obj/body/b{body:D4}/model/w{set:D4}b{body:D4}.mdl";

    /// <summary>
    /// The body the search for a free one starts at: 9000 plus the 32-bit FNV-1a hash (of the UTF-8 text) of the mod's
    /// directory, the source model and the snapshot's sources in the order of their private paths, joined by <c>|</c>,
    /// modulo 1000. The same pick always starts at the same body, whichever body it was resolved under; another mod's or
    /// option's rarely does, so two mods saved apart seldom pick the same body.
    /// </summary>
    internal static ushort StartBody(TimelinePropSource source)
    {
        var text = string.Join("|", [source.ModDirectory, source.SourceModel,
            .. source.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)]);
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return (ushort)(FirstBody + hash % BodyCount);
    }

    /// <summary>
    /// The private bodies (b9000..b9999) <paramref name="gamePaths"/> map something under: what a mod already uses for
    /// its props, in any option.
    /// </summary>
    internal static IReadOnlySet<(ushort Set, ushort Body)> MappedBodies(IEnumerable<string> gamePaths)
    {
        var bodies = new HashSet<(ushort, ushort)>();
        foreach (var path in gamePaths)
            if (PrivateBodyPath().Match(path.Replace('\\', '/')) is { Success: true } match)
                bodies.Add((ushort.Parse(match.Groups[1].Value), ushort.Parse(match.Groups[2].Value)));
        return bodies;
    }

    /// <summary>
    /// The private bodies the mod at <paramref name="modRoot"/> maps in its meta.json, every option and container
    /// (<see cref="MappedBodies"/>); none when its meta.json can't be read (an in-place save then fails on its own).
    /// </summary>
    internal static IReadOnlySet<(ushort Set, ushort Body)> ModBodies(string modRoot)
    {
        try { return MappedBodies(ModFileMap.Read(File.ReadAllText(Path.Combine(modRoot, "meta.json"))).Mappings.Select(m => m.GamePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return new HashSet<(ushort, ushort)>(); }
    }

    /// <summary>
    /// What the search for a prop's body (<see cref="FindBody"/>) found outside the timeline: where it started
    /// (<see cref="StartBody"/>), the body it found free (null when none is), and the bodies it found busy, mapped by the
    /// animation's own mod or served by a mod in its collection (Penumbra gives the model's path another file).
    /// </summary>
    internal sealed record PropBodies(ushort Start, ushort? Free, IReadOnlySet<ushort> Busy)
    {
        /// <summary>
        /// Whether <paramref name="body"/> counts as busy outside the timeline: every body but the free one, so a body the
        /// search didn't ask about is never given out.
        /// </summary>
        public bool Outside(ushort body) => body != Free;
    }

    /// <summary>
    /// Walks the bodies of <paramref name="source"/>'s set from <see cref="StartBody"/> for a free one: not
    /// <paramref name="taken"/> in the timeline, not shipped by the game, not <paramref name="mapped"/> by the animation's
    /// mod, and whose model path <paramref name="resolve"/> (Penumbra in the animation's collection) gives back as itself.
    /// Only bodies past the cheap checks are resolved.
    /// </summary>
    internal static async Task<PropBodies> FindBody(TimelinePropSource source, IReadOnlySet<(ushort Set, ushort Body)> taken,
        IReadOnlySet<(ushort Set, ushort Body)> mapped, Func<string, bool> gameExists, Func<string, Task<string>> resolve)
    {
        var start = StartBody(source);
        var busy = new HashSet<ushort>();
        foreach (var body in Walk(start))
        {
            if (taken.Contains((source.Set, body)) || gameExists(ImcPath(source.Set, body)))
                continue;
            if (mapped.Contains((source.Set, body)))
            {
                busy.Add(body);
                continue;
            }
            var model = ModelPath(source.Set, body);
            var resolved = await resolve(model).ConfigureAwait(false);
            if (!string.Equals(resolved.Replace('\\', '/'), model, StringComparison.OrdinalIgnoreCase))
            {
                busy.Add(body);
                continue;
            }
            return new PropBodies(start, body, busy);
        }
        return new PropBodies(start, null, busy);
    }

    /// <summary>The status text for a model whose material name a save would refuse (<see cref="MaterialUnsavable"/>).</summary>
    internal const string UnsavableMaterial = "This model's material name can't be saved.";

    /// <summary>
    /// Whether a material of <paramref name="source"/> has a private path a save refuses: the name comes from the model
    /// and is used as written, so a name with a space or another character the game paths don't take can't be copied.
    /// </summary>
    internal static bool MaterialUnsavable(TimelinePropSource source)
        => source.Files.Keys.Any(path => path.Contains("/material/", StringComparison.Ordinal) && !AnimationDependencies.SafeGamePath(path));

    /// <summary>The status text for a model naming a material neither the mod nor the game has: every save of the prop would fail.</summary>
    internal static string MissingMaterial(string name) => $"This model's material {name} is in neither the mod nor the game.";

    /// <summary>
    /// Why <paramref name="source"/> can't be placed: a material name a save refuses (<see cref="MaterialUnsavable"/>), or a
    /// material the game is to give that it doesn't have (<see cref="MissingMaterial"/>); null when it can be.
    /// </summary>
    internal static string? PickProblem(TimelinePropSource source, Func<string, bool> gameExists)
    {
        if (MaterialUnsavable(source))
            return UnsavableMaterial;
        foreach (var (path, from) in source.Files)
            if (path.EndsWith(".mtrl", StringComparison.Ordinal) && from.StartsWith("game:", StringComparison.Ordinal) && !gameExists(from[5..]))
                return MissingMaterial(path[(path.LastIndexOf('/') + 1)..]);
        return null;
    }

    /// <summary>
    /// The private bodies <paramref name="doc"/> uses: each prop source's set and body, the model and body of each summon
    /// with a source, and any summon on a private body (b9000..b9999) without one, such as a prop from a mod an earlier
    /// save wrote into the animation.
    /// </summary>
    internal static IReadOnlySet<(ushort Set, ushort Body)> Taken(TimelineDocument doc)
    {
        var taken = new HashSet<(ushort, ushort)>();
        if (doc.Events.IsDefault)
            return taken;
        foreach (var ev in doc.Events)
        {
            if (ev.Source is { } source)
                taken.Add((source.Set, source.Body));
            if (ev.Magic is not ("C198" or "C043") || ev.Values is not { } values)
                continue;
            var model = values.GetValueOrDefault("Model");
            var body = values.GetValueOrDefault("Body");
            if (model is > 0 and <= ushort.MaxValue && body is >= 0 and <= ushort.MaxValue &&
                (ev.Source != null || body is >= FirstBody and <= LastBody))
                taken.Add(((ushort)model, (ushort)body));
        }
        return taken;
    }

    /// <summary>
    /// The body a pick of <paramref name="sourceModel"/> of mod <paramref name="modDirectory"/> under <paramref name="set"/>
    /// is first resolved with: the body of a prop in <paramref name="doc"/> from the same mod and model, else the lowest
    /// free one in the timeline (<c>AllocateBody</c> past <see cref="Taken"/>); null when none is free. The snapshot
    /// doesn't depend on it, and the body it ends on is chosen from the resolved snapshot (<see cref="BodyFor(TimelineDocument, TimelinePropSource, Func{string, bool}, ushort, Func{ushort, bool})"/>).
    /// </summary>
    internal static ushort? BodyFor(TimelineDocument doc, string modDirectory, string sourceModel, ushort set, Func<string, bool> gameExists)
    {
        var model = Normalise(sourceModel);
        if (!doc.Events.IsDefault)
            foreach (var ev in doc.Events)
                if (ev.Source is { } source && source.Set == set && source.SourceModel == model &&
                    string.Equals(source.ModDirectory, modDirectory, StringComparison.OrdinalIgnoreCase))
                    return source.Body;
        return AllocateBody(set, gameExists, Taken(doc));
    }

    /// <summary>
    /// The private body <paramref name="resolved"/> (a snapshot made under any body) goes on: the body of a prop in
    /// <paramref name="doc"/> from the same mod and model whose snapshot is the same (<see cref="SameFiles"/>), so both
    /// share one copy, else the first free one from <paramref name="start"/> (<c>AllocateBody</c> past <see cref="Taken"/>
    /// and <paramref name="busyOutside"/>, what the pick's search found outside the timeline, see <see cref="FindBody"/>);
    /// null when none is free. A pick made after the mod's options changed gets its own body, so the earlier prop keeps its look.
    /// </summary>
    internal static ushort? BodyFor(TimelineDocument doc, TimelinePropSource resolved, Func<string, bool> gameExists, ushort start,
        Func<ushort, bool> busyOutside)
    {
        if (!doc.Events.IsDefault)
            foreach (var ev in doc.Events)
                if (ev.Source is { } source && source.Set == resolved.Set && source.SourceModel == resolved.SourceModel &&
                    string.Equals(source.ModDirectory, resolved.ModDirectory, StringComparison.OrdinalIgnoreCase) && SameFiles(source, resolved))
                    return source.Body;
        return AllocateBody(resolved.Set, start, gameExists, Taken(doc), busyOutside);
    }

    /// <summary> Whether two snapshots copy the same files, whichever private body each was made under. </summary>
    internal static bool SameFiles(TimelinePropSource a, TimelinePropSource b)
    {
        if (a.Set != b.Set || a.Files.Count != b.Files.Count || a.Textures.Count != b.Textures.Count)
            return false;
        var moved = a.Body == b.Body ? b : Rebodied(b, a.Body);
        return a.Files.All(pair => moved.Files.TryGetValue(pair.Key, out var from) && from == pair.Value) &&
               a.Textures.All(pair => moved.Textures.TryGetValue(pair.Key, out var to) && to == pair.Value);
    }

    /// <summary>
    /// <paramref name="source"/> under private body <paramref name="body"/>: the same sources at the same paths in that
    /// body's folder, as <see cref="Resolve"/> would give it there.
    /// </summary>
    internal static TimelinePropSource Rebodied(TimelinePropSource source, ushort body)
    {
        if (body == source.Body)
            return source;
        var from = $"chara/weapon/w{source.Set:D4}/obj/body/b{source.Body:D4}/";
        var to = $"chara/weapon/w{source.Set:D4}/obj/body/b{body:D4}/";
        var fromModel = $"{from}model/w{source.Set:D4}b{source.Body:D4}.mdl";
        var toModel = $"{to}model/w{source.Set:D4}b{body:D4}.mdl";
        string Moved(string path) => path == fromModel ? toModel : path.StartsWith(from, StringComparison.Ordinal) ? to + path[from.Length..] : path;
        return source with
        {
            Body = body,
            Files = source.Files.ToImmutableDictionary(pair => Moved(pair.Key), pair => pair.Value, StringComparer.Ordinal),
            Textures = source.Textures.ToImmutableDictionary(pair => pair.Key, pair => Moved(pair.Value), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// A model of a mod that can be listed as a prop: its game path, its file name and whether it qualifies (<see cref="Qualifies"/>).
    /// </summary>
    internal sealed record PropCandidate(string GamePath, string Label, bool Qualifies);

    /// <summary>
    /// The files the mod at <paramref name="modFolder"/> serves under Penumbra's <paramref name="settings"/> for it (null when
    /// the collection has none), every group's selection given (<see cref="ModOptionGroups.Selection"/>).
    /// </summary>
    internal static ImmutableDictionary<string, string> ModFiles(string modFolder, IReadOnlyDictionary<string, IReadOnlyList<string>>? settings)
        => ModOptionGroups.EffectiveFiles(modFolder, ModOptionGroups.Selection(modFolder, settings));

    /// <summary>
    /// The prop candidates of the mod at <paramref name="modFolder"/> under <paramref name="settings"/> (see <see cref="ModFiles"/>),
    /// sorted, each with its qualification. Only monster and demihuman models are read; a weapon always qualifies, and a
    /// file that can't be read doesn't.
    /// </summary>
    internal static ImmutableArray<PropCandidate> ModModels(string modFolder, IReadOnlyDictionary<string, IReadOnlyList<string>>? settings)
    {
        var effective = ModFiles(modFolder, settings);
        return [.. Candidates(effective).Select(path => new PropCandidate(path, path[(path.LastIndexOf('/') + 1)..], Readable(path, effective[path])))];

        static bool Readable(string gamePath, string file)
        {
            if (gamePath.StartsWith("chara/weapon/", StringComparison.Ordinal))
                return true;
            try { return Qualifies(gamePath, File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// The files of the private copy of <paramref name="sourceModel"/> as weapon <paramref name="set"/> body
    /// <paramref name="body"/>: the model, each material it names (the mod's, else the game's) and each texture those
    /// materials list (the mod's, else the game's; left out of the files when neither has it).
    /// <see cref="TimelinePropSource.Textures"/> names every listed texture's private path, found or not; a
    /// texture whose material entry is flagged DX11 is looked up under its stored path, then its <c>--</c> path,
    /// and its file is stored under both private names.
    /// </summary>
    internal static TimelinePropSource Resolve(string modDirectory, string modName, string sourceModel, ushort set, ushort body,
        IReadOnlyDictionary<string, string> effective, Func<string, byte[]?> game, Func<string, bool> gameExists)
    {
        sourceModel = Normalise(sourceModel);
        var target = $"chara/weapon/w{set:D4}/obj/body/b{body:D4}/";
        var folder = SourceFolder(sourceModel);
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var textures = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        var model = effective.TryGetValue(sourceModel, out var modelFile) ? "file:" + modelFile : "game:" + sourceModel;
        files[$"{target}model/w{set:D4}b{body:D4}.mdl"] = model;
        var mdl = Bytes(model, game);
        if (mdl is null)
            return new TimelinePropSource(modDirectory, modName, sourceModel, set, body, files.ToImmutable(), textures.ToImmutable());

        foreach (var name in VanillaModelReader.Read(mdl).MaterialNames
                     .Select(MaterialFileName)
                     .Where(name => name.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            var material = MaterialSource(folder, name, effective);
            var privatePath = $"{target}material/v0001/{name}";
            files[privatePath] = material;
            // A name a save refuses is refused at the pick (MaterialUnsavable); its textures aren't looked for.
            if (!AnimationDependencies.SafeGamePath(privatePath) || Bytes(material, game) is not { } mtrl)
                continue;
            foreach (var texture in MaterialPreviewBundleBuilder.ReadMaterialTextures(mtrl))
            {
                var stored = Normalise(texture.StoredPath);
                if (textures.ContainsKey(stored))
                    continue;
                var dx11 = Normalise(texture.Dx11Path);
                var twin = dx11 != stored;
                var key = PrivateTexturePath(target, stored, owners, twin);
                owners[key] = stored;
                if (twin)
                    owners[PathRules.Dx11TexturePath(key, 0x8000)] = stored;
                textures[stored] = key;
                var source = TextureSource([stored, dx11], effective, gameExists);
                if (source is null)
                    continue;
                files[key] = source;
                if (twin)
                    files[PathRules.Dx11TexturePath(key, 0x8000)] = source;
            }
        }
        return new TimelinePropSource(modDirectory, modName, sourceModel, set, body, files.ToImmutable(), textures.ToImmutable());
    }

    /// <summary>
    /// The file name a material name of a model stands for, lowercased: the part after its last <c>/</c>, as the game
    /// cuts an absolute name to its file name.
    /// </summary>
    internal static string MaterialFileName(string name)
    {
        var path = name.Replace('\\', '/').ToLowerInvariant();
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The private path of texture <paramref name="stored"/> in body folder <paramref name="target"/>:
    /// <c>texture/&lt;file name&gt;</c>, each character of its stem other than a letter, digit, <c>_</c>, <c>.</c> or
    /// <c>-</c> made <c>_</c> so a save takes it, with <c>_2</c>, <c>_3</c>, ... before the extension while another
    /// source texture already holds the name (<paramref name="owners"/>: private path to the source it was given to), or
    /// with <paramref name="dx11Copy"/> its <c>--</c> name, which the texture's DX11 copy takes too.
    /// </summary>
    internal static string PrivateTexturePath(string target, string stored, IReadOnlyDictionary<string, string> owners, bool dx11Copy = false)
    {
        var file = stored[(stored.LastIndexOf('/') + 1)..];
        var dot = file.LastIndexOf('.');
        var (stem, extension) = dot < 0 ? (file, "") : (file[..dot], file[dot..]);
        stem = string.Concat(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_'));
        var name = stem + extension;
        var path = $"{target}texture/{name}";
        bool Taken(string at) => owners.TryGetValue(at, out var owner) && owner != stored;
        for (var n = 2; Taken(path) || dx11Copy && Taken(PathRules.Dx11TexturePath(path, 0x8000)); ++n)
            path = $"{target}texture/{stem}_{n}{extension}";
        return path;
    }

    /// <summary>A texture from the mod under the first of <paramref name="paths"/> it has, else from the game the same way, else null.</summary>
    private static string? TextureSource(string[] paths, IReadOnlyDictionary<string, string> effective, Func<string, bool> gameExists)
    {
        foreach (var path in paths)
            if (effective.TryGetValue(path, out var file))
                return "file:" + file;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
            if (gameExists(path))
                return "game:" + path;
        return null;
    }

    /// <summary>
    /// A material the model names: under the model's folder in <c>material/v0001/</c> of the mod, else in the
    /// mod's lowest other variant folder, else the game's <c>material/v0001/</c> file.
    /// </summary>
    private static string MaterialSource(string folder, string name, IReadOnlyDictionary<string, string> effective)
    {
        var first = $"{folder}material/v0001/{name}";
        if (effective.TryGetValue(first, out var file))
            return "file:" + file;
        var variant = effective.Keys
            .Where(path => path.StartsWith(folder, StringComparison.Ordinal) && path.EndsWith("/" + name, StringComparison.Ordinal) &&
                           MaterialVariant().IsMatch(path[folder.Length..^name.Length]))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        return variant is not null ? "file:" + effective[variant] : "game:" + first;
    }

    /// <summary>The model's folder up to and including <c>obj/body/bYYYY/</c> or <c>obj/equipment/eYYYY/</c>.</summary>
    private static string SourceFolder(string sourceModel)
        => ModelFolder().Match(sourceModel) is { Success: true } match
            ? match.Value
            : sourceModel[..(sourceModel.LastIndexOf("/model/", StringComparison.Ordinal) + 1)];

    private static byte[]? Bytes(string source, Func<string, byte[]?> game)
        => source.StartsWith("file:", StringComparison.Ordinal) ? File.ReadAllBytes(source[5..]) : game(source[5..]);

    private static string Normalise(string gamePath) => gamePath.Replace('\\', '/').ToLowerInvariant();

    [GeneratedRegex(@"^chara/weapon/w(\d{4})/")]
    private static partial Regex WeaponSet();

    [GeneratedRegex(@"^chara/(?:(?:weapon|monster)/[wm]\d{4}/obj/body/b\d{4}/|demihuman/d\d{4}/obj/equipment/e\d{4}/)")]
    private static partial Regex ModelFolder();

    [GeneratedRegex(@"^material/v\d{4}/$")]
    private static partial Regex MaterialVariant();

    [GeneratedRegex(@"^chara/weapon/w(\d{4})/obj/body/b(9\d{3})/", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateBodyPath();
}
