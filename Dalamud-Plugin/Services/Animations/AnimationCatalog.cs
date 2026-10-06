using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations;

/// <summary>Emote relationships come from sheets. Path layouts follow VFXEditor's emote selector.</summary>
internal sealed class AnimationCatalog
{
    internal sealed record Timeline(ushort Id, string Key, string Name, byte LoadType, bool Loop,
        ImmutableArray<ushort> Family, ImmutableArray<ushort> Startups)
    {
        /// <summary>
        /// An expression emote's timeline (/smile plays facial/pose/smile): it plays a face on the face
        /// skeleton, from the library its TMB names or the face's resident set.
        /// </summary>
        public bool IsExpression => IsExpressionKey(LoadType, Key);
    }

    private const string FacialPrefix = "facial/pose/";

    internal static bool IsExpressionKey(byte loadType, string key) => loadType == 0 && key.StartsWith(FacialPrefix, StringComparison.Ordinal);
    private readonly Dictionary<ushort, Timeline> timelines = [];
    private readonly Lazy<PapLoadTable?> papTable;
    /// <summary>The game's PAP load table, or null when it can't be read: then every race loads its own paths.</summary>
    public PapLoadTable? PapTable => papTable.Value;
    private static readonly ImmutableHashSet<ushort> ExplicitIdleIds =
        [3, 3124, 3126, 3182, 3184, 7405, 7407, 642, 643, 653, 654, 3136, 3138, 3171,
            3132, 3134, 8002, 8004, 585, 3140, 3142, 7367, 8063, 8066, 8068];
    private static readonly ImmutableHashSet<ushort> ExplicitWalkIds = [13, 14, 15, 16, 17, 18, 19, 20];

    internal static bool IsExplicitIdle(ushort id) => ExplicitIdleIds.Contains(id);
    internal static bool IsExplicitWalk(ushort id) => ExplicitWalkIds.Contains(id);

    public AnimationCatalog(IDataManager data)
    {
        papTable = new(() =>
        {
            try { return data.GetFile(PapLoadTable.GamePath)?.Data is { } bytes ? new PapLoadTable(bytes) : null; }
            catch (InvalidDataException) { return null; }
        });
        var sheet = data.GetExcelSheet<ActionTimeline>();
        var groups = new Dictionary<ushort, HashSet<ushort>>();
        var starts = new Dictionary<ushort, HashSet<ushort>>();
        var names = new Dictionary<ushort, string>();
        foreach (var emote in data.GetExcelSheet<Emote>())
        {
            var family = emote.ActionTimeline.Where(t => t.RowId is > 0 and <= ushort.MaxValue && t.IsValid)
                .Select(t => (ushort)t.RowId).ToHashSet();
            var startup = new HashSet<ushort>();
            if (emote.EmoteMode.RowId > 0 && emote.EmoteMode.IsValid)
            {
                var mode = emote.EmoteMode.Value;
                if (mode.StartEmote.RowId > 0 && mode.StartEmote.IsValid)
                    startup.UnionWith(mode.StartEmote.Value.ActionTimeline.Where(t => t.RowId is > 0 and <= ushort.MaxValue && t.IsValid && !t.Value.IsLoop).Select(t => (ushort)t.RowId));
                family.UnionWith(startup);
                if (mode.EndEmote.RowId > 0 && mode.EndEmote.IsValid)
                    family.UnionWith(mode.EndEmote.Value.ActionTimeline.Where(t => t.RowId is > 0 and <= ushort.MaxValue && t.IsValid).Select(t => (ushort)t.RowId));
            }
            foreach (var id in family)
            {
                if (!groups.TryGetValue(id, out var group)) groups[id] = group = [];
                group.UnionWith(family);
                if (!starts.TryGetValue(id, out var entry)) starts[id] = entry = [];
                if (sheet.TryGetRow(id, out var timeline) && timeline.IsLoop) entry.UnionWith(startup);
                names.TryAdd(id, emote.Name.ExtractText());
            }
        }
        // Explicit player timeline IDs shared with LivePose's timeline identification.
        // Movement rows are opt-in here so unrelated combat/system rows remain excluded.
        // Sitting can report either the transition/start row or the loop row:
        // chair sit 642 -> 643, ground sit 653 -> 654.
        foreach (var id in ExplicitIdleIds) { groups.TryAdd(id, [id]); names.TryAdd(id, "Idle"); }
        // Normal walking can report a directional/start/end row from the same cycle.
        foreach (var id in ExplicitWalkIds) { groups.TryAdd(id, [id]); names.TryAdd(id, "Walk"); }
        foreach (var (id, family) in groups)
            if (sheet.TryGetRow(id, out var row) && !row.Key.IsEmpty)
                timelines[id] = new Timeline(id, row.Key.ExtractText(), names[id], row.LoadType, row.IsLoop,
                    family.Order().ToImmutableArray(), starts.TryGetValue(id, out var candidates) ? candidates.Order().ToImmutableArray() : []);
    }
    public Timeline? Find(ushort id) => timelines.GetValueOrDefault(id);
    private static ImmutableArray<string> PapKeys(Timeline timeline) => timeline.Key switch
    {
        "normal/idle" => ["resident/idle"],
        _ when timeline.Key.StartsWith("normal/walk", StringComparison.Ordinal) => ["resident/move_a", "resident/move_b"],
        _ => [timeline.Key],
    };

    public static bool Matches(Timeline timeline, string pap, string clip) =>
        PapKeys(timeline).Any(key => pap.EndsWith("/" + key + ".pap", StringComparison.Ordinal)) ||
        clip == timeline.Key || clip == timeline.Key.Split('/').Last() ||
        timeline.IsExpression && pap.EndsWith("/nonresident/" + timeline.Key[FacialPrefix.Length..] + ".pap", StringComparison.Ordinal);

    /// <summary>
    /// The PAPs that may hold what <paramref name="timeline"/> plays, for the player whose loaded
    /// paths are <paramref name="known"/>. An expression's face comes from the library its TMB names
    /// (<paramref name="facePacks"/>, such as "smile"), or from the face's resident set when it
    /// names none: Straight Face, Shut Eyes, Endure, Ouch and Concentrate play cfxf_ motions
    /// every face always has loaded.
    /// </summary>
    public static ImmutableArray<string> CandidatePapPaths(Timeline timeline, IEnumerable<string> known, PapLoadTable? table = null,
        IEnumerable<string>? facePacks = null)
    {
        var paths = known.Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.Ordinal).ToArray();
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (!AnimationDependencies.SafeGamePath(timeline.Key)) return [];
        // Penumbra's resource tree exposes skeletons and material PAPs, but does
        // not enumerate the character's skeletal animation packs. Resolve the
        // active ActionTimeline against the player's base/face skeleton variants.
        foreach (var path in paths)
        {
            if (path.EndsWith(".pap", StringComparison.Ordinal) && Matches(timeline, path, "")) candidates.Add(path);
            var parts = path.Split('/');
            if (parts.Length != 7 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "skeleton" ||
                !path.EndsWith(".sklb", StringComparison.Ordinal)) continue;
            // A race without its own copy plays the one the game's load table names.
            if (timeline.LoadType == 2 && parts[4] == "base")
                foreach (var key in PapKeys(timeline))
                    candidates.Add(Loaded($"chara/human/{parts[2]}/animation/a0001/bt_common/{key}.pap", table));
            else if (timeline.IsExpression && parts[4] == "face")
            {
                // The face's animation folder is its skeleton's: faces sharing a skeleton share it too.
                var face = $"chara/human/{parts[2]}/animation/{parts[5]}";
                candidates.Add($"{face}/nonresident/{timeline.Key[FacialPrefix.Length..]}.pap");
                candidates.Add($"{face}/resident/face.pap");
                foreach (var pack in facePacks ?? [])
                    if (FacePackPath(face, pack) is { } library)
                        candidates.Add(library);
            }
        }
        return candidates.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>A face library's PAP in a face's animation folder, or null for a name that is no safe path.</summary>
    private static string? FacePackPath(string faceFolder, string pack)
    {
        var path = $"{faceFolder}/nonresident/{pack}.pap";
        return pack.Length > 0 && AnimationDependencies.SafeGamePath(path) ? path : null;
    }

    internal AnimationCatalog(IEnumerable<Timeline> entries, PapLoadTable? table = null)
    {
        papTable = new(() => table);
        foreach (var timeline in entries) timelines.Add(timeline.Id, timeline);
    }

    private static string Loaded(string path, PapLoadTable? table) => table?.Resolve(path) ?? path;

    /// <summary>The race of the one base skeleton among loaded paths, the race that requests body PAPs.</summary>
    private static string? BaseRace(IEnumerable<string> paths)
    {
        var races = paths.Select(p => p.Split('/')).Where(p => p.Length == 7 && p[0] == "chara" && p[1] == "human" &&
            p[3] == "skeleton" && p[4] == "base" && p[6].EndsWith(".sklb", StringComparison.Ordinal)).Select(p => p[2]).Distinct().ToArray();
        return races.Length == 1 ? races[0] : null;
    }

    public static string? PapPath(Timeline timeline, string selectedPap, IEnumerable<string> known, PapLoadTable? table = null)
    {
        if (!AnimationDependencies.SafeGamePath(timeline.Key)) return null;
        var paths = known.Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.Ordinal).ToArray();
        var parts = selectedPap.Split('/');
        if (parts.Length < 8 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation") return null;
        if (timeline.IsExpression)
        {
            var face = paths.Select(p => p.Split('/')).Where(p => p.Length == 7 && p[0] == "chara" && p[1] == "human" &&
                p[2] == parts[2] && p[3] == "skeleton" && p[4] == "face" && p[6].EndsWith(".sklb", StringComparison.Ordinal))
                .Select(p => p[5]).Distinct().ToArray();
            if (face.Length == 0) face = paths.Select(p => p.Split('/')).Where(p => p.Length > 6 && p[0] == "chara" && p[1] == "human" &&
                p[2] == parts[2] && p[3] == "animation" && p[4].StartsWith('f')).Select(p => p[4]).Distinct().ToArray();
            return face.Length == 1 ? $"chara/human/{parts[2]}/animation/{face[0]}/nonresident/{timeline.Key[FacialPrefix.Length..]}.pap" : null;
        }
        if (AnimationDependencies.SafeGamePath(selectedPap) &&
            PapKeys(timeline).Any(key => selectedPap.EndsWith("/" + key + ".pap", StringComparison.Ordinal)))
            return selectedPap;
        // The selected clip may be another race's copy. The player's race requests the
        // family member, and the load table can send it to a third race.
        var race = BaseRace(paths) ?? parts[2];
        var requested = timeline.LoadType > 2 || timeline.LoadType == 1 && parts[5] == "bt_common" ? null :
            Loaded($"chara/human/{race}/animation/a0001/{(timeline.LoadType == 1 ? parts[5] : "bt_common")}/{PapKeys(timeline)[0]}.pap", table);
        var existing = paths.Where(p => (p == requested || p.StartsWith($"chara/human/{race}/animation/", StringComparison.Ordinal)) &&
            PapKeys(timeline).Any(key => p.EndsWith("/" + key + ".pap", StringComparison.Ordinal))).ToArray();
        if (existing.Length > 0) return existing.Length == 1 ? existing[0] : null;
        return requested;
    }

    /// <summary>
    /// Player idle PAPs commonly pair <c>*_loop.pap</c> with a sibling startup. The player's race
    /// requests it, so a loop borrowed from another race can still start from the player's own copy.
    /// </summary>
    public static string? SiblingStartupPath(string loopPath, IEnumerable<string>? known = null, PapLoadTable? table = null)
    {
        const string loop = "_loop.pap";
        if (!AnimationDependencies.SafeGamePath(loopPath) || !loopPath.EndsWith(loop, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = (loopPath[..^loop.Length] + "_start.pap").Split('/');
        if (parts.Length > 4 && parts[0] == "chara" && parts[1] == "human" && parts[3] == "animation" &&
            known != null && BaseRace(known.Where(AnimationDependencies.SafeGamePath)) is { } race)
            parts[2] = race;
        return Loaded(string.Join('/', parts), table);
    }
}
