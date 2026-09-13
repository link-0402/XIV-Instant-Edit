using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations;

/// <summary>Emote relationships come from sheets. Path layouts follow VFXEditor's emote selector.</summary>
internal sealed class AnimationCatalog
{
    internal sealed record Timeline(ushort Id, string Key, string Name, byte LoadType, bool Loop,
        ImmutableArray<ushort> Family, ImmutableArray<ushort> Startups);
    private readonly Dictionary<ushort, Timeline> timelines = [];
    private readonly Dictionary<string, string> motions = new(StringComparer.Ordinal);
    private readonly Func<string, byte[]?> readTimeline;
    private ImmutableDictionary<string, ImmutableArray<Timeline>>? motionTimelines;
    private static readonly ImmutableHashSet<ushort> ExplicitIdleIds =
        [3, 3124, 3126, 3182, 3184, 7405, 7407, 642, 643, 653, 654, 3136, 3138, 3171,
            3132, 3134, 8002, 8004, 585, 3140, 3142, 7367, 8063, 8066, 8068];
    private static readonly ImmutableHashSet<ushort> ExplicitWalkIds = [13, 14, 15, 16, 17, 18, 19, 20];

    internal static bool IsExplicitIdle(ushort id) => ExplicitIdleIds.Contains(id);
    internal static bool IsExplicitWalk(ushort id) => ExplicitWalkIds.Contains(id);

    public AnimationCatalog(IDataManager data)
    {
        readTimeline = path => data.GetFile(path)?.Data;
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
        foreach (var motion in data.GetExcelSheet<MotionTimeline>())
        {
            var file = motion.Filename.ExtractText();
            if (file.Length > 0) motions.TryAdd(motion.RowId.ToString(), file);
        }
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
        timeline.LoadType == 0 && timeline.Key.StartsWith("facial/pose/", StringComparison.Ordinal) &&
        pap.EndsWith("/nonresident/" + timeline.Key[12..] + ".pap", StringComparison.Ordinal);

    public static ImmutableArray<string> CandidatePapPaths(Timeline timeline, IEnumerable<string> known)
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
            if (timeline.LoadType == 2 && parts[4] == "base")
                foreach (var key in PapKeys(timeline))
                    candidates.Add($"chara/human/{parts[2]}/animation/a0001/bt_common/{key}.pap");
            else if (timeline.LoadType == 0 && parts[4] == "face" && timeline.Key.StartsWith("facial/pose/", StringComparison.Ordinal))
                candidates.Add($"chara/human/{parts[2]}/animation/{parts[5]}/nonresident/{timeline.Key[12..]}.pap");
        }
        return candidates.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    internal AnimationCatalog(IEnumerable<Timeline> entries, Func<string, byte[]?> readTimeline)
    {
        foreach (var timeline in entries) timelines.Add(timeline.Id, timeline);
        this.readTimeline = readTimeline;
    }

    public IReadOnlyList<string> ResolveMotion(string key, string selectedPap, IEnumerable<string> known, CancellationToken token)
    {
        if (motions.TryGetValue(key, out var motion)) key = motion;
        var paths = known.Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.Ordinal).ToArray();
        // MotionTimeline.Filename is a PAP entry name (e.g. cfxf_bad), not an
        // ActionTimeline key (facial/pose/bad). Index the actual TMB references.
        // This vanilla index only discovers candidates; callers must verify the
        // effective collection's PAP entries before using any candidate.
        if (motionTimelines == null)
        {
            var index = new Dictionary<string, List<Timeline>>(StringComparer.Ordinal);
            foreach (var timeline in timelines.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!AnimationDependencies.SafeGamePath(timeline.Key)) continue;
                var path = $"chara/action/{timeline.Key}.tmb";
                if (readTimeline(path) is not { } bytes) continue;
                foreach (var reference in AnimationDependencies.Read(path, bytes).References.Where(r => r.Kind == "animation"))
                {
                    if (!index.TryGetValue(reference.Path, out var entries)) index[reference.Path] = entries = [];
                    entries.Add(timeline);
                }
            }
            motionTimelines = index.ToImmutableDictionary(p => p.Key, p => p.Value.Distinct().ToImmutableArray(), StringComparer.Ordinal);
        }
        var matches = paths.Where(p => p.EndsWith("/" + key + ".pap", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var timeline in motionTimelines.GetValueOrDefault(key, []).Concat(timelines.Values.Where(t => t.Key == key)))
            if (PapPath(timeline, selectedPap, paths) is { } path) matches.Add(path);
        return matches.Order(StringComparer.Ordinal).ToArray();
    }

    public static string? PapPath(Timeline timeline, string selectedPap, IEnumerable<string> known)
    {
        if (!AnimationDependencies.SafeGamePath(timeline.Key)) return null;
        var paths = known.Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.Ordinal).ToArray();
        var parts = selectedPap.Split('/');
        if (parts.Length < 8 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation") return null;
        if (timeline.LoadType == 0 && timeline.Key.StartsWith("facial/pose/", StringComparison.Ordinal))
        {
            var face = paths.Select(p => p.Split('/')).Where(p => p.Length == 7 && p[0] == "chara" && p[1] == "human" &&
                p[2] == parts[2] && p[3] == "skeleton" && p[4] == "face" && p[6].EndsWith(".sklb", StringComparison.Ordinal))
                .Select(p => p[5]).Distinct().ToArray();
            if (face.Length == 0) face = paths.Select(p => p.Split('/')).Where(p => p.Length > 6 && p[0] == "chara" && p[1] == "human" &&
                p[2] == parts[2] && p[3] == "animation" && p[4].StartsWith('f')).Select(p => p[4]).Distinct().ToArray();
            return face.Length == 1 ? $"chara/human/{parts[2]}/animation/{face[0]}/nonresident/{timeline.Key[12..]}.pap" : null;
        }
        if (AnimationDependencies.SafeGamePath(selectedPap) &&
            PapKeys(timeline).Any(key => selectedPap.EndsWith("/" + key + ".pap", StringComparison.Ordinal)))
            return selectedPap;
        var existing = paths.Where(p => p.StartsWith($"chara/human/{parts[2]}/animation/", StringComparison.Ordinal) &&
            PapKeys(timeline).Any(key => p.EndsWith("/" + key + ".pap", StringComparison.Ordinal))).ToArray();
        if (existing.Length > 0) return existing.Length == 1 ? existing[0] : null;
        if (timeline.LoadType == 1 && parts[5] == "bt_common") return null;
        if (timeline.LoadType > 2) return null;
        return $"chara/human/{parts[2]}/animation/a0001/{(timeline.LoadType == 1 ? parts[5] : "bt_common")}/{PapKeys(timeline)[0]}.pap";
    }

    /// <summary>Player idle PAPs commonly pair <c>*_loop.pap</c> with a sibling startup.</summary>
    public static string? SiblingStartupPath(string loopPath)
    {
        const string loop = "_loop.pap";
        if (!AnimationDependencies.SafeGamePath(loopPath) || !loopPath.EndsWith(loop, StringComparison.OrdinalIgnoreCase)) return null;
        return loopPath[..^loop.Length] + "_start.pap";
    }
}
