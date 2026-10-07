using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>One lane of the drawn timeline and the rows it takes; overlapping events stack into extra rows.</summary>
internal readonly record struct TimelineLaneRows(TimelineLane Lane, int FirstRow, int Rows)
{
    public int EndRow => FirstRow + Rows;
    public bool Contains(int row) => row >= FirstRow && row < EndRow;
}

/// <summary>
/// One bone layer row: the layer at <paramref name="Layer"/> in the document's layers (-1 for the empty
/// row a first layer goes into), or with <paramref name="Bone"/> one bone's keys of it, under its row.
/// </summary>
internal readonly record struct TimelineMotionRow(int Layer, string? Bone = null);

/// <summary>
/// Which row every event of a document is drawn in. Built once per document, not per frame, and
/// independent of the zoom: an event occupies [Start, Start + max(Duration, visible length, 1)), and
/// events whose spans overlap inside a lane are packed greedily into sub-rows. A motion the game plays
/// at its own speed counts as lasting what it plays (<see cref="Shown"/>). No ImGui or Dalamud dependencies.
/// </summary>
internal sealed class TimelineLayout
{
    /// <summary>Lanes the editor always shows, even empty: the motion and every lane an Add can fill.</summary>
    public static readonly ImmutableArray<TimelineLane> FixedLanes =
        [TimelineLane.Motion, TimelineLane.Expression, TimelineLane.LipSync, TimelineLane.Sound, TimelineLane.Effect, TimelineLane.Prop];

    private readonly Dictionary<string, int> indexByKey;
    private readonly int[] rows;
    private readonly int[] ends;
    private readonly TimelineEvent[] shown;
    private readonly ImmutableArray<TimelineMotionRow> motionRows;
    private readonly int[] layerRows;

    public TimelineDocument Document { get; }
    public ImmutableArray<TimelineLaneRows> Lanes { get; }

    /// <summary>Every row: the event lanes' and, below them, the bone layers'.</summary>
    public int RowCount { get; }

    /// <summary>The first bone layer row, right below the event lanes.</summary>
    public int MotionFirstRow { get; }

    /// <summary>
    /// The bone layer rows: one per layer, in the order they apply, each unfolded layer followed by one
    /// row per bone its keys name; or one empty row to add the first to. None on an expression's own
    /// timeline, whose face takes no bone layers.
    /// </summary>
    public int MotionRows => motionRows.Length;

    private TimelineLayout(TimelineDocument document, ImmutableArray<TimelineLaneRows> lanes, int rowCount,
        int[] rows, int[] ends, TimelineEvent[] shown, Dictionary<string, int> indexByKey, int motionFirstRow,
        ImmutableArray<TimelineMotionRow> motionRows, int[] layerRows)
    {
        Document = document;
        Lanes = lanes;
        RowCount = rowCount;
        MotionFirstRow = motionFirstRow;
        this.rows = rows;
        this.ends = ends;
        this.shown = shown;
        this.indexByKey = indexByKey;
        this.motionRows = motionRows;
        this.layerRows = layerRows;
    }

    /// <summary>Whether <paramref name="row"/> is a bone layer row, a layer's own or one of its bones'.</summary>
    public bool IsMotionRow(int row) => row >= MotionFirstRow && row < MotionFirstRow + MotionRows;

    /// <summary>What the bone layer row <paramref name="row"/> shows, or null for any other row.</summary>
    public TimelineMotionRow? MotionRowAt(int row) => IsMotionRow(row) ? motionRows[row - MotionFirstRow] : null;

    /// <summary>The index in the document's layers of the layer whose own row is <paramref name="row"/>, or -1 (a bone, empty or event row).</summary>
    public int LayerIndexAt(int row) => MotionRowAt(row) is { Bone: null, Layer: var layer } ? layer : -1;

    /// <summary>The layer and bone of the bone row <paramref name="row"/>, or null for any other row.</summary>
    public (int Layer, string Bone)? BoneAt(int row) => MotionRowAt(row) is { Bone: { } bone, Layer: var layer } ? (layer, bone) : null;

    /// <summary>How many layers have rows: every layer, or none on an expression's own timeline.</summary>
    public int LayerCount => layerRows.Length;

    /// <summary>The row the layer at <paramref name="index"/> is drawn in.</summary>
    public int LayerRow(int index) => layerRows[index];

    /// <summary>How many bone rows the layer at <paramref name="index"/> shows under it: none while it is folded.</summary>
    public int BoneRows(int index) => (index + 1 < layerRows.Length ? layerRows[index + 1] : MotionFirstRow + MotionRows) - layerRows[index] - 1;

    /// <summary>Whether the layer can unfold into bone rows: an offset or pose with keys. Amplitudes have none.</summary>
    public static bool Unfolds(MotionLayer layer) => layer.Kind != MotionLayerKind.Amplitude && layer.Keys.Any(key => !key.Bones.IsEmpty);

    /// <summary>
    /// Lays out <paramref name="doc"/>. <paramref name="visibleLength"/> gives the frames an event
    /// visibly takes beyond its duration (a sound's play length, a label); null counts only durations.
    /// <paramref name="motionFrames"/> gives facial motions' own lengths by name (see
    /// <see cref="TimelineDocument.PlayedFrames"/>); without it they count what they store. The layers
    /// keyed in <paramref name="unfolded"/> get a row per bone, in <paramref name="boneOrder"/> (see
    /// <see cref="MotionEdits.KeyBones"/>).
    /// </summary>
    public static TimelineLayout Build(TimelineDocument doc, Func<TimelineEvent, int>? visibleLength = null,
        IReadOnlyDictionary<string, int>? motionFrames = null, IReadOnlySet<string>? unfolded = null,
        IReadOnlyDictionary<string, int>? boneOrder = null)
    {
        var events = doc.Events.IsDefault ? [] : doc.Events;
        var rows = new int[events.Length];
        var ends = new int[events.Length];
        var shown = new TimelineEvent[events.Length];
        var indexByKey = new Dictionary<string, int>(events.Length, StringComparer.Ordinal);
        var present = new bool[(int)TimelineLane.Other + 1];
        for (var index = 0; index < events.Length; index++)
        {
            var ev = events[index];
            // The game ignores the length a motion at its own speed stores: a face shows until the
            // next face or the end, a lip motion for its own length.
            if (doc.PlayedFrames(ev, motionFrames) is var played && played != ev.Duration)
                ev = ev with { Duration = played };
            shown[index] = ev;
            // Saturating: mods use int32 durations such as 9999999 for "until the end".
            ends[index] = (int)Math.Min(int.MaxValue, (long)ev.Start + Math.Max(Math.Max(ev.Duration, visibleLength?.Invoke(ev) ?? 0), 1));
            indexByKey.TryAdd(ev.Key, index);
            present[LaneIndex(ev.Lane)] = true;
        }

        var lanes = ImmutableArray.CreateBuilder<TimelineLaneRows>();
        var next = 0;
        var subRowEnds = new List<int>();
        for (var lane = TimelineLane.Motion; lane <= TimelineLane.Other; lane++)
        {
            if (lane == TimelineLane.Other && !present[(int)lane])
                continue;
            // Earliest start first, then item order, so packing is stable across edits elsewhere.
            var members = Enumerable.Range(0, events.Length).Where(index => Normalize(events[index].Lane) == lane)
                .OrderBy(index => events[index].Start).ThenBy(index => index);
            subRowEnds.Clear();
            foreach (var index in members)
            {
                var row = subRowEnds.FindIndex(end => end <= events[index].Start);
                if (row < 0)
                {
                    row = subRowEnds.Count;
                    subRowEnds.Add(0);
                }
                subRowEnds[row] = ends[index];
                rows[index] = next + row;
            }
            var count = Math.Max(1, subRowEnds.Count);
            lanes.Add(new TimelineLaneRows(lane, next, count));
            next += count;
        }
        var motionFirst = next;
        var motion = ImmutableArray.CreateBuilder<TimelineMotionRow>();
        var layerRows = new int[doc.Expression ? 0 : doc.Motion.Length];
        for (var index = 0; index < layerRows.Length; index++)
        {
            var layer = doc.Motion[index];
            layerRows[index] = motionFirst + motion.Count;
            motion.Add(new TimelineMotionRow(index));
            if (unfolded != null && unfolded.Contains(layer.Key) && Unfolds(layer))
                foreach (var bone in MotionEdits.KeyBones(layer, boneOrder))
                    motion.Add(new TimelineMotionRow(index, bone));
        }
        if (!doc.Expression && motion.Count == 0)
            motion.Add(new TimelineMotionRow(-1));
        return new TimelineLayout(doc, lanes.ToImmutable(), next + motion.Count, rows, ends, shown, indexByKey, motionFirst,
            motion.ToImmutable(), layerRows);
    }

    /// <summary>The index of the event with <paramref name="key"/> in the document's events, or -1.</summary>
    public int IndexOf(string key) => indexByKey.TryGetValue(key, out var index) ? index : -1;

    /// <summary>The row the event at <paramref name="index"/> is drawn in.</summary>
    public int RowOf(int index) => rows[index];

    /// <summary>The frame where the event at <paramref name="index"/> stops taking space in its row.</summary>
    public int EndOf(int index) => ends[index];

    /// <summary>
    /// The event at <paramref name="index"/> as it is drawn and grabbed: a motion at its own speed
    /// lasts what the game plays (<see cref="TimelineDocument.PlayedFrames"/>) instead of the length
    /// it stores. Every other event is the document's own.
    /// </summary>
    public TimelineEvent Shown(int index) => shown[index];

    public TimelineLaneRows? Lane(TimelineLane lane)
    {
        lane = Normalize(lane);
        foreach (var item in Lanes)
            if (item.Lane == lane)
                return item;
        return null;
    }

    /// <summary>The lane drawn in <paramref name="row"/>, or null outside the rows.</summary>
    public TimelineLaneRows? LaneAt(int row)
    {
        foreach (var lane in Lanes)
            if (lane.Contains(row))
                return lane;
        return null;
    }

    /// <summary>The top of <paramref name="row"/> below a ruler of <paramref name="rulerHeight"/>, with the rows scrolled up by <paramref name="scrollY"/>.</summary>
    public static float RowTop(int row, float rowHeight, float rulerHeight, float scrollY = 0) => rulerHeight + row * rowHeight - scrollY;

    /// <summary>The row at <paramref name="y"/>, or -1 on the ruler or below the last row.</summary>
    public int RowAt(float y, float rowHeight, float rulerHeight, float scrollY = 0)
    {
        if (y < rulerHeight || rowHeight <= 0 || !float.IsFinite(y))
            return -1;
        var row = (int)MathF.Floor((y - rulerHeight + scrollY) / rowHeight);
        return row >= 0 && row < RowCount ? row : -1;
    }

    /// <summary>The height of the ruler and every row.</summary>
    public float Height(float rowHeight, float rulerHeight) => rulerHeight + RowCount * rowHeight;

    /// <summary>The top and bottom of a lane's rows, or (0, 0) when the lane is not shown.</summary>
    public (float Top, float Bottom) LaneSpan(TimelineLane lane, float rowHeight, float rulerHeight, float scrollY = 0) =>
        Lane(lane) is { } span
            ? (RowTop(span.FirstRow, rowHeight, rulerHeight, scrollY), RowTop(span.EndRow, rowHeight, rulerHeight, scrollY))
            : (0, 0);

    /// <summary>Lanes outside the enum (from a newer reader) are drawn with Other.</summary>
    private static TimelineLane Normalize(TimelineLane lane) => lane <= TimelineLane.Other ? lane : TimelineLane.Other;

    private static int LaneIndex(TimelineLane lane) => (int)Normalize(lane);
}
