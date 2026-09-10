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
    public AnimationCatalog(IDataManager data)
    {
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
        // Explicit idle IDs shared with LivePose's timeline identification, never movement/combat rows.
        ushort[] idles = [3, 3124, 3126, 3182, 3184, 7405, 7407, 654, 3136, 3138, 3171,
            643, 3132, 3134, 8002, 8004, 585, 3140, 3142, 7367, 8063, 8066, 8068];
        foreach (var id in idles) { groups.TryAdd(id, [id]); names.TryAdd(id, "Idle"); }
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
    public bool Matches(Timeline timeline, string pap, string clip) =>
        pap.EndsWith("/" + timeline.Key + ".pap", StringComparison.Ordinal) ||
        clip == timeline.Key || clip == timeline.Key.Split('/').Last() ||
        timeline.LoadType == 0 && timeline.Key.StartsWith("facial/pose/", StringComparison.Ordinal) &&
        pap.EndsWith("/nonresident/" + timeline.Key[12..] + ".pap", StringComparison.Ordinal);

    public IReadOnlyList<string> ResolveMotion(string key, string selectedPap, IEnumerable<string> known)
    {
        if (motions.TryGetValue(key, out var motion)) key = motion;
        var matches = known.Where(p => p.EndsWith("/" + key + ".pap", StringComparison.Ordinal)).Distinct().ToArray();
        if (matches.Length > 0) return matches;
        var timeline = timelines.Values.FirstOrDefault(t => t.Key == key);
        if (timeline == null) return [];
        var path = PapPath(timeline, selectedPap, known);
        return path == null ? [] : [path];
    }

    public static string? PapPath(Timeline timeline, string selectedPap, IEnumerable<string> known)
    {
        var existing = known.Where(p => p.EndsWith("/" + timeline.Key + ".pap", StringComparison.Ordinal)).Distinct().ToArray();
        if (existing.Length == 1) return existing[0];
        var parts = selectedPap.Split('/');
        if (parts.Length < 8 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation") return null;
        if (timeline.Key.Contains('[')) return null;
        if (timeline.LoadType == 0 && timeline.Key.StartsWith("facial/pose/", StringComparison.Ordinal))
        {
            var face = known.Select(p => p.Split('/')).Where(p => p.Length > 6 && p[0] == "chara" && p[1] == "human" &&
                p[2] == parts[2] && p[3] == "animation" && p[4].StartsWith('f')).Select(p => p[4]).Distinct().ToArray();
            return face.Length == 1 ? $"chara/human/{parts[2]}/animation/{face[0]}/nonresident/{timeline.Key[12..]}.pap" : null;
        }
        if (timeline.LoadType == 1 && parts[5] == "bt_common") return null;
        if (timeline.LoadType > 2) return null;
        return $"chara/human/{parts[2]}/animation/a0001/{(timeline.LoadType == 1 ? parts[5] : "bt_common")}/{timeline.Key}.pap";
    }
}
