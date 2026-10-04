using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>Where a catalog entry comes from, so pickers can group and explain it.</summary>
internal enum TimelineCatalogOrigin : byte
{
    /// <summary>Always loaded with the player's face (resident/face.pap): needs no library.</summary>
    Resident,
    /// <summary>An expression emote's own library (ActionTimeline facial/pose/{x}).</summary>
    Expression,
    /// <summary>Used by a vanilla emote.</summary>
    Emote,
    /// <summary>The game's common effect list (VFX sheet).</summary>
    Common,
    /// <summary>A weapon or tool model from the Item sheet.</summary>
    Item,
    /// <summary>Mapped by the mod the clip comes from.</summary>
    Mod,
}

/// <summary>Something the timeline editor can place.</summary>
/// <param name="Label">Readable name.</param>
/// <param name="Path">The motion name for expressions and lip shapes ("cfxf_smile"), the game path for sounds and effects, "" for props.</param>
/// <param name="Detail">Short secondary text: the emote, item list or mod it came from.</param>
/// <param name="Pack">The expression library (TMPP) the motion needs; null when the face always has it loaded.</param>
/// <param name="Prop">The model a prop spawns, with the WeaponTimeline row a vanilla emote animates it with.</param>
internal sealed record TimelineCatalogItem(string Label, string Path, TimelineCatalogOrigin Origin, string Detail,
    string? Pack = null, TimelinePropModel? Prop = null)
{
    /// <summary>The add request that places this item at <paramref name="start"/>.</summary>
    public TimelineAddRequest Request(TimelineAddKind kind, int start, int duration = 0) => new(kind, start, Path, duration, Pack, Prop);
}

/// <summary>A WeaponTimeline row: what a C031 entry plays on a summoned prop.</summary>
internal sealed record TimelineWeaponTimeline(ushort Id, string Key);

/// <summary>An ActionTimeline row as the catalog needs it.</summary>
internal sealed record TimelineCatalogTimeline(string Key, byte LoadType);

/// <summary>An emote's name and the timelines it plays, in sheet order.</summary>
internal sealed record TimelineCatalogEmote(string Name, ImmutableArray<TimelineCatalogTimeline> Timelines);

/// <summary>An item's weapon or tool model.</summary>
internal sealed record TimelineCatalogItemModel(string Name, ushort Model, ushort Body, ushort Variant);

/// <summary>The sheet rows a catalog is built from, read once per session.</summary>
/// <param name="FacialKeys">ActionTimeline keys "facial/pose/{x}" of rows with LoadType 0.</param>
/// <param name="VfxLocations">VFX sheet Location values (vfx/common/eff/{Location}.avfx).</param>
internal sealed record TimelineCatalogSheets(
    ImmutableArray<TimelineCatalogEmote> Emotes,
    ImmutableArray<string> FacialKeys,
    ImmutableArray<string> VfxLocations,
    ImmutableArray<TimelineCatalogItemModel> Items,
    ImmutableArray<TimelineWeaponTimeline> WeaponTimelines)
{
    public static readonly TimelineCatalogSheets Empty = new([], [], [], [], []);
}

/// <summary>
/// What a catalog is built for: the player's race code, the face folder their face skeleton uses
/// (skeleton/face/{Face}, which is also the face animation folder) and the folder of the mod the
/// clip comes from, if any.
/// </summary>
internal sealed record TimelineCatalogKey
{
    public ushort Race { get; }
    public string Face { get; }
    public string? ModRoot { get; }

    public TimelineCatalogKey(ushort race, string face, string? modRoot = null)
    {
        face = face.Trim().ToLowerInvariant();
        if (race is 0 or > 9999) throw new ArgumentException("Invalid race code.", nameof(race));
        if (face.Length != 5 || face[0] != 'f' || !face[1..].All(char.IsAsciiDigit)) throw new ArgumentException("Invalid face folder.", nameof(face));
        Race = race;
        Face = face;
        ModRoot = string.IsNullOrWhiteSpace(modRoot) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot));
    }

    public string RaceCode => $"c{Race:D4}";

    /// <summary>
    /// The key for a player whose face skeleton is <paramref name="skeletonPath"/>, such as
    /// chara/human/c0101/skeleton/face/f0002/skl_c0101f0002.sklb; null for any other path. Face
    /// animations follow the face skeleton, not the face model: c0101 face 1 uses f0002.
    /// </summary>
    public static TimelineCatalogKey? FromFaceSkeleton(string skeletonPath, string? modRoot = null)
    {
        if (skeletonPath.Replace('\\', '/').Split('/') is not ["chara", "human", var race, "skeleton", "face", var face, var file] ||
            !file.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase) || race.Length != 5 || race[0] != 'c' ||
            !ushort.TryParse(race.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is 0 or > 9999) return null;
        try { return new TimelineCatalogKey(code, face, modRoot); }
        catch (ArgumentException) { return null; }
    }

    // Windows folders compare without case, so one mod is one catalog however its path was spelled.
    public bool Equals(TimelineCatalogKey? other) => other is not null && Race == other.Race && Face == other.Face &&
        string.Equals(ModRoot, other.ModRoot, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => HashCode.Combine(Race, Face, ModRoot == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ModRoot));
}

/// <summary>Everything the editor offers to place for one <see cref="TimelineCatalogKey"/>.</summary>
/// <param name="ModFiles">The source mod's sound and effect mappings: game path to the file on disk.</param>
/// <param name="Notes">Short plain notes about what could not be offered, for the picker.</param>
internal sealed record TimelineCatalogData(
    ImmutableArray<TimelineCatalogItem> Expressions,
    ImmutableArray<TimelineCatalogItem> LipShapes,
    ImmutableArray<TimelineCatalogItem> Sounds,
    ImmutableArray<TimelineCatalogItem> Effects,
    ImmutableArray<TimelineCatalogItem> Props,
    ImmutableArray<TimelineWeaponTimeline> WeaponTimelines,
    ImmutableDictionary<string, string> ModFiles,
    ImmutableArray<string> Notes)
{
    public static readonly TimelineCatalogData Empty = new([], [], [], [], [], [],
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), []);
}

/// <summary>
/// Builds a <see cref="TimelineCatalogData"/> from game files and sheet rows. Vanilla paths can't
/// be listed (the game's index holds only hashes), so every list comes from paths the sheets and
/// vanilla timelines name, checked with <c>exists</c> and read with <c>read</c>. Pure: no game or
/// Dalamud types; the source mod's metadata is read from disk, never written.
/// </summary>
internal sealed class TimelineCatalogBuilder(Func<string, bool> exists, Func<string, byte[]?> read)
{
    // PAP clip types of face animations (r14): faces, blinks and lip shapes.
    private const short FaceType = 17, BlinkType = 18, LipType = 19;
    // C031 TargetType of the summoned prop; 0 and 1 animate the main and off-hand weapons.
    private const short PropTarget = 2;
    private const string PosePrefix = "facial/pose/";

    private int unreadable;

    public TimelineCatalogData Build(TimelineCatalogSheets sheets, TimelineCatalogKey key, CancellationToken token = default)
    {
        unreadable = 0;
        var notes = new List<string>();
        var faceRoot = $"chara/human/{key.RaceCode}/animation/{key.Face}";
        var facialNames = FacialNames(sheets);

        // Expressions and lip shapes the face always has loaded.
        var expressions = new List<TimelineCatalogItem>();
        var lips = new List<TimelineCatalogItem>();
        var resident = new HashSet<string>(StringComparer.Ordinal);
        if (FaceEntries($"{faceRoot}/resident/face.pap") is { } residentEntries)
            foreach (var entry in residentEntries)
            {
                if (entry.Type == LipType) lips.Add(new(Readable(entry.Name), entry.Name, TimelineCatalogOrigin.Resident, "Always loaded"));
                else if (entry.Type is FaceType or BlinkType && resident.Add(entry.Name))
                    expressions.Add(new(FaceLabel(entry.Name, facialNames), entry.Name, TimelineCatalogOrigin.Resident, "Always loaded"));
            }
        else notes.Add($"No face animations for {key.Face}, so no expressions.");

        // The vanilla emotes: their sounds, effects, props and expression libraries.
        var harvest = new Harvest();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = AnimationSkeletonIndex.Ancestors(key.RaceCode);
        foreach (var emote in sheets.Emotes)
            foreach (var timeline in emote.Timelines)
            {
                token.ThrowIfCancellationRequested();
                if (timeline.LoadType == 0 || !AnimationDependencies.SafeGamePath(timeline.Key) || !visited.Add(timeline.Key)) continue;
                var label = emote.Name.Length > 0 ? emote.Name : timeline.Key[(timeline.Key.LastIndexOf('/') + 1)..];
                // A race without its own copy of an emote plays its skeleton parent's.
                if (timeline.LoadType == 2)
                    foreach (var model in ancestors)
                        if (HarvestFile($"chara/human/{model}/animation/a0001/bt_common/{timeline.Key}.pap", label, harvest)) break;
                HarvestFile($"chara/action/{timeline.Key}.tmb", label, harvest);
            }
        // Emote timelines name few sounds themselves (12 in 2026); most play from their effects.
        foreach (var (effect, emote) in harvest.Effects.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (Read(effect) is not { } avfx) continue;
            try
            {
                foreach (var sound in AnimationDependencies.ReadVfxPaths(avfx).Sounds)
                    if (sound.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && AnimationDependencies.SafeGamePath(sound) &&
                        !harvest.Sounds.ContainsKey(sound) && Exists(sound))
                        harvest.Sounds.Add(sound, emote);
            }
            catch (InvalidDataException) { unreadable++; }
        }

        // Expression emotes: facial/pose/{x} plays cfxf_{x} from library {x}, or from the resident set.
        var libraries = new HashSet<(string Motion, string Pack)>();
        foreach (var facialKey in sheets.FacialKeys)
        {
            token.ThrowIfCancellationRequested();
            if (!facialKey.StartsWith(PosePrefix, StringComparison.Ordinal) || !AnimationDependencies.SafeGamePath(facialKey)) continue;
            var path = $"chara/action/{facialKey}.tmb";
            if (Entries(path) is not { } tmb) continue;
            var pack = tmb.Entries.Where(e => e.Magic == "TMPP").Select(e => Text(tmb.Bytes, e, 8)).FirstOrDefault(p => p is { Length: > 0 });
            if (pack == null || !SafePack(pack)) continue;
            AddLibrary(pack, TimelineCatalogOrigin.Expression, "Expression");
        }
        foreach (var (pack, emote) in harvest.Packs)
        {
            token.ThrowIfCancellationRequested();
            AddLibrary(pack, TimelineCatalogOrigin.Emote, emote);
        }

        // Sounds and effects: the source mod's first, then the emotes', then the common effect list.
        var modFiles = key.ModRoot != null && Directory.Exists(key.ModRoot) ? ModFiles(key.ModRoot, notes) : ImmutableDictionary<string, string>.Empty;
        var modName = key.ModRoot != null ? System.IO.Path.GetFileName(key.ModRoot) : "";
        var sounds = new Resources();
        var effects = new Resources();
        foreach (var path in modFiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) sounds.Add(new(Stem(path), path, TimelineCatalogOrigin.Mod, modName));
            else if (path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) effects.Add(new(Stem(path), path, TimelineCatalogOrigin.Mod, modName));
        }
        foreach (var (path, emote) in harvest.Sounds.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            sounds.Add(new($"{emote}: {Stem(path)}", path, TimelineCatalogOrigin.Emote, emote));
        foreach (var (path, emote) in harvest.Effects.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            effects.Add(new($"{emote}: {Stem(path)}", path, TimelineCatalogOrigin.Emote, emote));
        foreach (var location in sheets.VfxLocations)
        {
            token.ThrowIfCancellationRequested();
            var path = $"vfx/common/eff/{location}.avfx";
            if (location.Length > 0 && AnimationDependencies.SafeGamePath(path) && !effects.Contains(path) && Exists(path))
                effects.Add(new(location, path, TimelineCatalogOrigin.Common, "Common effect"));
        }

        // Props: first the model and animation pairs vanilla prop emotes use, then every weapon and tool.
        var props = new List<TimelineCatalogItem>();
        var seenProps = new HashSet<TimelinePropModel>();
        // A model some emote animates is offered with that animation only, not again without one.
        var animated = harvest.Props.Where(p => p.Prop.WeaponTimeline != null).Select(p => p.Prop with { WeaponTimeline = null }).ToHashSet();
        foreach (var (prop, emote) in harvest.Props)
            if ((prop.WeaponTimeline != null || !animated.Contains(prop)) && seenProps.Add(prop))
                props.Add(new(emote, "", TimelineCatalogOrigin.Emote, PropDetail(prop, sheets.WeaponTimelines), Prop: prop));
        // Item models a recipe already offers are not listed twice.
        seenProps.UnionWith(animated);
        foreach (var group in sheets.Items.Where(i => i.Model != 0 && i.Body != 0 && i.Name.Length > 0)
                     .GroupBy(i => new TimelinePropModel(i.Model, i.Body, i.Variant, null)))
        {
            token.ThrowIfCancellationRequested();
            if (!seenProps.Add(group.Key) || !Exists(WeaponModelPath(group.Key))) continue;
            var names = group.Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
            props.Add(new(names[0], "", TimelineCatalogOrigin.Item,
                names.Length > 1 ? $"Also {string.Join(", ", names.Skip(1).Take(2))}{(names.Length > 3 ? $" and {names.Length - 3} more" : "")}" : "Item",
                Prop: group.Key));
        }

        if (unreadable > 0) notes.Add($"{unreadable} game {(unreadable == 1 ? "file" : "files")} could not be read.");
        return new TimelineCatalogData([.. expressions], [.. lips], sounds.Items, effects.Items, [.. props],
            [.. sheets.WeaponTimelines.Where(w => w.Key.Length > 0).OrderBy(w => w.Id)],
            modFiles.WithComparers(StringComparer.OrdinalIgnoreCase), [.. notes]);

        void AddLibrary(string pack, TimelineCatalogOrigin origin, string detail)
        {
            if (FaceEntries($"{faceRoot}/nonresident/{pack}.pap") is not { } entries) return;
            foreach (var entry in entries)
            {
                // A motion the face always has needs no library; offering it with one would only
                // spend the timeline's single library slot.
                if (resident.Contains(entry.Name) || !libraries.Add((entry.Name, pack))) continue;
                if (entry.Type == LipType) lips.Add(new(Readable(entry.Name), entry.Name, origin, detail, pack));
                else if (entry.Type is FaceType or BlinkType)
                    expressions.Add(new(FaceLabel(entry.Name, facialNames), entry.Name, origin, detail, pack));
            }
        }
    }

    /// <summary>
    /// The emote names of expression timelines by pose ("bow" is "Shut Eyes"), which label the
    /// cfxf_{pose} motions better than their file names.
    /// </summary>
    private static Dictionary<string, string> FacialNames(TimelineCatalogSheets sheets)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var emote in sheets.Emotes)
            foreach (var timeline in emote.Timelines)
                if (emote.Name.Length > 0 && timeline.Key.StartsWith(PosePrefix, StringComparison.Ordinal))
                    names.TryAdd("cfxf_" + timeline.Key[PosePrefix.Length..], emote.Name);
        return names;
    }

    private static string FaceLabel(string motion, Dictionary<string, string> facialNames) =>
        facialNames.TryGetValue(motion, out var name) ? name : Readable(motion);

    /// <summary>
    /// A readable name for a motion: its kind prefix (cfxf_, cfxb_, cfxl_) dropped and its words
    /// capitalised, so "cfxf_battle_camera1" reads "Battle Camera 1".
    /// </summary>
    internal static string Readable(string motion)
    {
        var name = motion.StartsWith("cfx", StringComparison.Ordinal) && motion.IndexOf('_') is > 0 and var cut ? motion[(cut + 1)..] : motion;
        var words = new List<string>();
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            // Split a trailing number from its word: "camera1" -> "camera 1".
            var digits = part.Length; while (digits > 0 && char.IsAsciiDigit(part[digits - 1])) digits--;
            if (digits > 0) words.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part[..digits]));
            if (digits < part.Length) words.Add(part[digits..]);
        }
        return words.Count > 0 ? string.Join(' ', words) : motion;
    }

    private static string Stem(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    private static bool SafePack(string pack) => AnimationDependencies.SafeGamePath(pack + ".pap");

    private static string PropDetail(TimelinePropModel prop, ImmutableArray<TimelineWeaponTimeline> timelines)
    {
        var model = $"w{prop.Model:D4} b{prop.Body:D4} v{prop.Variant}";
        if (prop.WeaponTimeline is not { } id) return model;
        var key = timelines.FirstOrDefault(t => t.Id == id)?.Key;
        return key is { Length: > 0 } ? $"{model}, {key[(key.LastIndexOf('/') + 1)..]}" : $"{model}, animation {id}";
    }

    /// <summary>The model file a weapon model's items use, to leave out items whose model the game lacks.</summary>
    internal static string WeaponModelPath(TimelinePropModel prop) =>
        $"chara/weapon/w{prop.Model:D4}/obj/body/b{prop.Body:D4}/model/w{prop.Model:D4}b{prop.Body:D4}.mdl";

    private bool Exists(string path)
    {
        try { return exists(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private byte[]? Read(string path)
    {
        try { return read(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { unreadable++; return null; }
    }

    private ImmutableArray<AnimationPap.Entry>? FaceEntries(string path)
    {
        if (Read(path) is not { } bytes) return null;
        try { return new AnimationPap(bytes).Entries; }
        catch (InvalidDataException) { unreadable++; return null; }
    }

    private (byte[] Bytes, ImmutableArray<AnimationTimelineEntry> Entries)? Entries(string path)
    {
        if (Read(path) is not { } bytes) return null;
        try { return (bytes, AnimationDependencies.TimelineEntries(path, bytes)); }
        catch (InvalidDataException) { unreadable++; return null; }
    }

    private static string? Text(byte[] bytes, AnimationTimelineEntry entry, int field)
    {
        try { return AnimationDependencies.EntryString(bytes, entry, field); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Collects what one emote file plays. False when the game has no such file.</summary>
    private bool HarvestFile(string path, string emote, Harvest harvest)
    {
        if (Entries(path) is not { } file) return false;
        foreach (var timeline in file.Entries.GroupBy(e => e.Timeline))
        {
            var models = new List<(TimelinePropModel Model, int Summon)>();
            var animations = new List<ushort>();
            foreach (var entry in timeline)
            {
                var bytes = file.Bytes; var at = entry.Position;
                switch (entry.Magic)
                {
                    case "TMPP" when Text(bytes, entry, 8) is { Length: > 0 } pack && SafePack(pack):
                        harvest.Packs.TryAdd(pack, emote); break;
                    case "C063" when Text(bytes, entry, 20) is { } sound && sound.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && AnimationDependencies.SafeGamePath(sound):
                        harvest.Sounds.TryAdd(sound, emote); break;
                    case "C012" or "C173" when Text(bytes, entry, 20) is { } effect && effect.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) && AnimationDependencies.SafeGamePath(effect):
                        harvest.Effects.TryAdd(effect, emote); break;
                    // C198: ModelId i16 @32, BodyId i16 @34, Variant i32 @36, SummonId u8 @28 (VFXEditor C198).
                    case "C198" when entry.Size >= 40:
                        if (Prop(I16(bytes, at + 32), I16(bytes, at + 34), AnimationPap.ReadInt(bytes, at + 36)) is { } spawned) models.Add((spawned, bytes[at + 28]));
                        break;
                    // C043: WeaponId i16 @24, BodyId i16 @26, VariantId i32 @28 (VFXEditor C043).
                    case "C043" when entry.Size >= 32:
                        if (Prop(I16(bytes, at + 24), I16(bytes, at + 26), AnimationPap.ReadInt(bytes, at + 28)) is { } summoned) models.Add((summoned, 0));
                        break;
                    // C031: WeaponTimeline u16 @20, TargetType i16 @22.
                    case "C031" when entry.Size >= 24 && I16(bytes, at + 22) == PropTarget:
                        var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 20));
                        if (id != 0 && !animations.Contains(id)) animations.Add(id);
                        break;
                }
            }
            // C043 and C198 often spawn the same model (sp19): it is one prop. The animation plays on
            // the first summon; a second prop (sp75) is spawned still.
            models = models.GroupBy(m => m.Model).Select(g => (g.Key, g.Min(m => m.Summon))).ToList();
            var animated = models.FindIndex(m => m.Summon == 0);
            if (animated < 0 && models.Count > 0) animated = 0;
            for (var i = 0; i < models.Count; i++)
            {
                var prop = models[i].Model with { WeaponTimeline = i == animated && animations.Count > 0 ? animations[0] : null };
                if (!harvest.Props.Any(p => p.Prop == prop)) harvest.Props.Add((prop, emote));
            }
        }
        return true;
    }

    private static short I16(byte[] bytes, int at) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at));

    private static TimelinePropModel? Prop(short model, short body, int variant) =>
        model > 0 && body > 0 && variant is > 0 and <= ushort.MaxValue ? new((ushort)model, (ushort)body, (ushort)variant, null) : null;

    /// <summary>
    /// The sound and effect game paths the mod maps, to the files on disk: Files of meta.json's
    /// DefaultData and groups (Penumbra 1.7), default_mod.json and group_*.json (older mods), each
    /// with its options and containers. Read only; a mapping that leaves the mod folder is ignored.
    /// </summary>
    internal static ImmutableDictionary<string, string> ModFiles(string modRoot, List<string>? notes = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot));
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var broken = 0;

        void Visit(JsonNode? node)
        {
            if (node is not JsonObject container || container["Files"] is not JsonObject mapping) return;
            foreach (var (gamePath, value) in mapping)
            {
                if (!gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && !gamePath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) continue;
                if (!AnimationDependencies.SafeGamePath(gamePath) || value is not JsonValue text || !text.TryGetValue<string>(out var relative) ||
                    string.IsNullOrWhiteSpace(relative)) continue;
                string full;
                try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) files.TryAdd(gamePath, full);
            }
        }

        void VisitGroup(JsonNode? node)
        {
            if (node is not JsonObject group) return;
            Visit(group);
            foreach (var list in new[] { "Options", "Containers" })
                if (group[list] is JsonArray items)
                    foreach (var item in items) Visit(item);
        }

        var documents = new List<string> { Path.Combine(root, "meta.json"), Path.Combine(root, "default_mod.json") };
        try { documents.AddRange(Directory.EnumerateFiles(root, "group_*.json").Order(StringComparer.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { broken++; }
        foreach (var document in documents)
        {
            if (!File.Exists(document)) continue;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(document), documentOptions: new JsonDocumentOptions
                    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (json is not JsonObject value) continue;
                var name = Path.GetFileName(document);
                if (name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    Visit(value["DefaultData"]);
                    if (value["Groups"] is JsonArray groups)
                        foreach (var group in groups) VisitGroup(group);
                }
                else if (name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase)) Visit(value);
                else VisitGroup(value);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { broken++; }
        }
        if (broken > 0) notes?.Add("Some of the mod's settings could not be read.");
        return files.ToImmutable();
    }

    private sealed class Harvest
    {
        public readonly Dictionary<string, string> Packs = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Sounds = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Effects = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(TimelinePropModel Prop, string Emote)> Props = [];
    }

    /// <summary>A list of game paths kept once each, in the order they were added.</summary>
    private sealed class Resources
    {
        private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TimelineCatalogItem> items = [];
        public ImmutableArray<TimelineCatalogItem> Items => [.. items];
        public bool Contains(string path) => seen.Contains(path);
        public void Add(TimelineCatalogItem item) { if (seen.Add(item.Path)) items.Add(item); }
    }
}
