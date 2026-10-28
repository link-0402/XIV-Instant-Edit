using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Services;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Models from other mods as props: which of a mod's models can be one, whether it rides a single bone, the
/// private weapon body (<c>b9000</c>..<c>b9999</c>, which the game doesn't ship) it gets in the animation's mod, and
/// the source of every file of that private copy.
/// </summary>
internal static partial class PropModelSources
{
    private const ushort FallbackSet = 9001;
    private const ushort FirstBody = 9000;
    private const ushort LastBody = 9999;

    private static readonly string[] CandidateRoots = ["chara/weapon/", "chara/monster/", "chara/demihuman/"];

    /// <summary>The weapon, monster and demihuman models among a mod's effective files, sorted.</summary>
    internal static ImmutableArray<string> Candidates(IReadOnlyDictionary<string, string> effective)
        => [..effective.Keys
            .Where(path => path.EndsWith(".mdl", StringComparison.Ordinal) &&
                           CandidateRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];

    /// <summary>The weapon set a prop goes to: a weapon keeps its own, anything else goes to 9001.</summary>
    internal static ushort TargetSet(string sourceModel)
        => WeaponSet().Match(Normalise(sourceModel)) is { Success: true } match ? ushort.Parse(match.Groups[1].Value) : FallbackSet;

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
    {
        for (var body = FirstBody; body <= LastBody; ++body)
            if (!taken.Contains((set, body)) && !gameExists($"chara/weapon/w{set:D4}/obj/body/b{body:D4}/b{body:D4}.imc"))
                return body;
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
    /// free one (<see cref="AllocateBody"/> past <see cref="Taken"/>); null when none is free. The body it ends on is
    /// chosen from the resolved snapshot (<see cref="BodyFor(TimelineDocument, TimelinePropSource, Func{string, bool})"/>).
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
    /// share one copy, else the lowest free one; null when none is free. A pick made after the mod's options changed
    /// gets its own body, so the earlier prop keeps its look.
    /// </summary>
    internal static ushort? BodyFor(TimelineDocument doc, TimelinePropSource resolved, Func<string, bool> gameExists)
    {
        if (!doc.Events.IsDefault)
            foreach (var ev in doc.Events)
                if (ev.Source is { } source && source.Set == resolved.Set && source.SourceModel == resolved.SourceModel &&
                    string.Equals(source.ModDirectory, resolved.ModDirectory, StringComparison.OrdinalIgnoreCase) && SameFiles(source, resolved))
                    return source.Body;
        return AllocateBody(resolved.Set, gameExists, Taken(doc));
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
                     .Select(name => name.Replace('\\', '/').TrimStart('/').ToLowerInvariant())
                     .Where(name => name.Length > 0 && !name.Contains('/'))
                     .Distinct(StringComparer.Ordinal))
        {
            var material = MaterialSource(folder, name, effective);
            files[$"{target}material/v0001/{name}"] = material;
            if (Bytes(material, game) is not { } mtrl)
                continue;
            foreach (var texture in MaterialPreviewBundleBuilder.ReadMaterialTextures(mtrl))
            {
                var stored = Normalise(texture.StoredPath);
                if (textures.ContainsKey(stored))
                    continue;
                var key = PrivateTexturePath(target, stored, owners);
                owners[key] = stored;
                textures[stored] = key;
                var dx11 = Normalise(texture.Dx11Path);
                var source = TextureSource([stored, dx11], effective, gameExists);
                if (source is null)
                    continue;
                files[key] = source;
                if (dx11 != stored)
                    files[PathRules.Dx11TexturePath(key, 0x8000)] = source;
            }
        }
        return new TimelinePropSource(modDirectory, modName, sourceModel, set, body, files.ToImmutable(), textures.ToImmutable());
    }

    /// <summary>
    /// The private path of texture <paramref name="stored"/> in body folder <paramref name="target"/>:
    /// <c>texture/&lt;file name&gt;</c>, with <c>_2</c>, <c>_3</c>, ... before the extension while another source
    /// texture already holds the name (<paramref name="owners"/>: private path to the source it was given to).
    /// </summary>
    internal static string PrivateTexturePath(string target, string stored, IReadOnlyDictionary<string, string> owners)
    {
        var name = stored[(stored.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot < 0 ? (name, "") : (name[..dot], name[dot..]);
        var path = $"{target}texture/{name}";
        for (var n = 2; owners.TryGetValue(path, out var owner) && owner != stored; ++n)
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
}
