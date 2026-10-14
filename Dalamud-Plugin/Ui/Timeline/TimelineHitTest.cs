using System.Numerics;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>What is under the pointer.</summary>
internal enum TimelinePart : byte
{
    None,
    /// <summary>The lane names left of the tracks.</summary>
    Legend,
    Ruler,
    /// <summary>Empty space in a lane.</summary>
    Lane,
    Body,
    LeftEdge,
    RightEdge,
    /// <summary>The end of the timeline, on the ruler and the motion lane.</summary>
    LengthHandle,
    /// <summary>Empty space in a bone layer row, or the empty row below the event lanes.</summary>
    MotionLane,
    LayerBody,
    LayerLeft,
    LayerRight,
    /// <summary>The handle where a layer's fade in ends, on its top edge.</summary>
    LayerFadeIn,
    /// <summary>The handle where a layer's fade out begins, on its top edge.</summary>
    LayerFadeOut,
    /// <summary>A layer's key, a diamond on its bar.</summary>
    LayerKey,
    /// <summary>The arrow at the right of a layer's name that folds its bone rows in and out.</summary>
    LayerUnfold,
    /// <summary>Empty space in one bone's row under its layer.</summary>
    BoneLane,
    /// <summary>One bone's value in a layer's key, on that bone's row.</summary>
    BoneKey,
    /// <summary>Empty space in the clip band, under the ruler.</summary>
    ClipRow,
    /// <summary>A part of the cut motion, on the clip band.</summary>
    ClipPart,
    /// <summary>A part's left edge, which trims its start.</summary>
    ClipLeft,
    /// <summary>A part's right edge, which trims its end.</summary>
    ClipRight,
    /// <summary>A join's blend, where two parts meet.</summary>
    Join,
    /// <summary>The outer edge of a join's blend, which sets its length.</summary>
    JoinEdge,
    /// <summary>The loop seam's blend, at the clip's end (or its start).</summary>
    Seam,
    /// <summary>The outer edge of the loop seam's blend, which sets its length.</summary>
    SeamEdge,
}

/// <param name="Frame">The fractional frame under the pointer; the editor rounds it.</param>
/// <param name="Lane">The lane of the row under the pointer, null on the ruler or outside the rows.</param>
/// <param name="Key">The event under the pointer for Body, LeftEdge and RightEdge.</param>
/// <param name="Row">The row under the pointer, -1 outside the rows.</param>
/// <param name="Layer">The bone layer of the row under the pointer, also on the legend and on its bone rows.</param>
/// <param name="KeyFrame">The frame of the layer's key under the pointer for LayerKey and BoneKey, else -1.</param>
/// <param name="Bone">The bone of a bone row under the pointer, also on the legend.</param>
/// <param name="Cut">On the clip band, the part (ClipPart, ClipLeft, ClipRight) or join (Join, JoinEdge) under the pointer, else -1.</param>
internal readonly record struct TimelineHit(TimelinePart Part, float Frame, TimelineLane? Lane = null, string? Key = null, int Row = -1,
    string? Layer = null, int KeyFrame = -1, string? Bone = null, int Cut = -1)
{
    public static TimelineHit None(float frame = 0) => new(TimelinePart.None, frame);
    public bool OnEvent => Key != null && Part is TimelinePart.Body or TimelinePart.LeftEdge or TimelinePart.RightEdge;
    public bool OnLayer => Layer != null && Part is TimelinePart.LayerBody or TimelinePart.LayerLeft or TimelinePart.LayerRight or
        TimelinePart.LayerFadeIn or TimelinePart.LayerFadeOut or TimelinePart.LayerKey or TimelinePart.BoneKey;
    /// <summary>On a key, a whole one on its layer's bar or one bone's on its row.</summary>
    public bool OnKey => Layer != null && KeyFrame >= 0 && Part is TimelinePart.LayerKey or TimelinePart.BoneKey;
}

/// <summary>
/// Pixel sizes of the drawn timeline, all already scaled: legend width, ruler height, row height,
/// the edge grab tolerance and the narrowest an event may be to still be grabbed. Rows scroll up by
/// <paramref name="ScrollY"/> under a fixed ruler. Points are relative to the canvas' top left corner,
/// legend included. <paramref name="Unfold"/> is the width of the arrow at the legend's right edge
/// that folds a layer's bone rows; 0 leaves it out. <paramref name="Clip"/> is the height of the clip
/// band under the ruler, where the cut motion's parts are; 0 leaves it out. No ImGui or Dalamud dependencies.
/// </summary>
internal readonly record struct TimelineGeometry(float Legend, float Ruler, float Row, float Edge, float Marker, float ScrollY = 0,
    float Unfold = 0, float Clip = 0)
{
    /// <summary>The fixed rows above the scrolling ones: the ruler and the clip band.</summary>
    public float Header => Ruler + Clip;

    /// <summary>
    /// Where an event is drawn and grabbed, in track pixels. A marker (no duration) is centred on its
    /// frame and at least <see cref="Marker"/> wide; a clip starts at its frame and is never narrower.
    /// <paramref name="end"/> is the layout's end for it (<see cref="TimelineLayout.EndOf"/>).
    /// </summary>
    public (float Left, float Right) EventSpan(TimelineEvent ev, int end, TimelineView view)
    {
        var left = view.X(ev.Start);
        var right = view.X(Math.Max(end, (float)ev.Start + Math.Max(ev.Duration, 1)));
        if (ev.Duration <= 0)
            return (Math.Min(left, left - Marker / 2), Math.Max(right, left + Marker / 2));
        return (left, Math.Max(right, left + Marker));
    }

    /// <summary>
    /// The x of a clip's right edge: the end of its duration, which a visible tail may pass, and never
    /// closer to the start than the narrowest drawn clip.
    /// </summary>
    public float RightEdgeX(TimelineEvent ev, TimelineView view)
    {
        var left = view.X(ev.Start);
        return Math.Max(view.X((float)ev.Start + ev.Duration), left + Marker);
    }

    /// <summary>The top of a row in canvas pixels.</summary>
    public float RowTop(int row) => TimelineLayout.RowTop(row, Row, Header, ScrollY);

    /// <summary>
    /// What is at <paramref name="point"/>. Order: the length handle; then the events of the row, where
    /// one whose own frames lie under the pointer wins at once, from the last drawn to the first, its
    /// edges before its body; where only widened grab zones reach the pointer (a marker's minimum
    /// width, an edge's tolerance) the nearest event wins, so stacked markers all stay grabbable; then
    /// the ruler; then the clip band (see <see cref="HitClip"/>), where the seam is only hit on a clip
    /// that <paramref name="loops"/>; then empty lane space.
    /// </summary>
    public TimelineHit HitTest(TimelineDocument doc, TimelineLayout layout, TimelineView view, Vector2 point, bool loops = false)
    {
        var x = point.X - Legend;
        var y = point.Y;
        var frame = view.FrameAt(x);
        if (!float.IsFinite(x) || !float.IsFinite(y) || point.X < 0 || y < 0 || x > view.Width)
            return TimelineHit.None(float.IsFinite(frame) ? frame : 0);
        var row = layout.RowAt(y, Row, Header, ScrollY);
        TimelineLane? lane = row >= 0 ? layout.LaneAt(row)?.Lane : null;
        var layerIndex = row >= 0 ? layout.LayerIndexAt(row) : -1;
        if (x < 0)
        {
            if (row < 0)
                return TimelineHit.None(frame);
            var motionRow = layout.MotionRowAt(row);
            var owner = motionRow is { Layer: >= 0 and var at } ? doc.Motion[at].Key : null;
            if (Unfold > 0 && layerIndex >= 0 && x >= -Unfold && TimelineLayout.Unfolds(doc.Motion[layerIndex]))
                return new TimelineHit(TimelinePart.LayerUnfold, frame, lane, null, row, owner);
            return new TimelineHit(TimelinePart.Legend, frame, lane, null, row, owner, Bone: motionRow?.Bone);
        }

        if (MathF.Abs(x - view.X(doc.Length)) <= Edge && (y < Ruler || lane == TimelineLane.Motion))
            return new TimelineHit(TimelinePart.LengthHandle, frame, lane, null, row);
        if (y < Ruler)
            return new TimelineHit(TimelinePart.Ruler, frame);
        if (y < Header)
            return doc.Expression ? TimelineHit.None(frame) : HitClip(doc, view, x, frame, loops);
        if (row < 0)
            return TimelineHit.None(frame);
        if (layout.BoneAt(row) is { } boneRow)
            return HitBone(doc.Motion[boneRow.Layer], boneRow.Bone, view, x, frame, row);
        if (layout.IsMotionRow(row))
            return layerIndex < 0
                ? new TimelineHit(TimelinePart.MotionLane, frame, null, null, row)
                : HitLayer(doc.Motion[layerIndex], view, x, y - RowTop(row), frame, row);

        var events = doc.Events.IsDefault ? [] : doc.Events;
        TimelineHit? nearest = null;
        var nearestDistance = float.MaxValue;
        for (var index = events.Length - 1; index >= 0; index--)
        {
            if (layout.RowOf(index) != row)
                continue;
            var ev = layout.Shown(index);
            var part = PartAt(ev, layout.EndOf(index), view, x);
            if (part == TimelinePart.None)
                continue;
            var hit = new TimelineHit(part, frame, lane, ev.Key, row);
            var own = (Left: view.X(ev.Start), Right: view.X(Math.Max(layout.EndOf(index), ev.Start + 1f)));
            var distance = x < own.Left ? own.Left - x : x > own.Right ? x - own.Right : 0;
            if (distance == 0)
                return hit;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = hit;
            }
        }
        return nearest ?? new TimelineHit(TimelinePart.Lane, frame, lane, null, row);
    }

    /// <summary>
    /// What of a bone layer is at <paramref name="x"/>, <paramref name="y"/> (y from the row's top):
    /// a key first, then a fade handle in the top third, then an edge, then the bar. Outside the bar
    /// and its grab zones, the empty row.
    /// </summary>
    private TimelineHit HitLayer(MotionLayer layer, TimelineView view, float x, float y, float frame, int row)
    {
        var left = view.X(layer.Start);
        var right = Math.Max(view.X(layer.End), left + 1);
        var middle = Row / 2;
        if (MathF.Abs(y - middle) <= Marker / 2 + 1)
        {
            TimelineHit? nearest = null;
            var best = float.MaxValue;
            foreach (var key in layer.Keys)
            {
                var distance = MathF.Abs(x - view.X(key.Frame));
                if (distance <= Marker / 2 + 1 && distance < best)
                {
                    best = distance;
                    nearest = new TimelineHit(TimelinePart.LayerKey, frame, null, null, row, layer.Key, key.Frame);
                }
            }
            if (nearest is { } key0)
                return key0;
        }
        if (y < Row / 3 && right - left > Edge * 2)
        {
            if (MathF.Abs(x - view.X(layer.Start + layer.FadeIn)) <= Edge)
                return new TimelineHit(TimelinePart.LayerFadeIn, frame, null, null, row, layer.Key);
            if (MathF.Abs(x - view.X(layer.End - layer.FadeOut)) <= Edge)
                return new TimelineHit(TimelinePart.LayerFadeOut, frame, null, null, row, layer.Key);
        }
        // Inside the bar each edge takes at most a third, so a narrow layer can still be moved.
        var inner = Math.Min(Edge, (right - left) / 3);
        if (x >= left - Edge && x <= left + inner)
            return new TimelineHit(TimelinePart.LayerLeft, frame, null, null, row, layer.Key);
        if (x >= right - inner && x <= right + Edge)
            return new TimelineHit(TimelinePart.LayerRight, frame, null, null, row, layer.Key);
        return x >= left && x <= right
            ? new TimelineHit(TimelinePart.LayerBody, frame, null, null, row, layer.Key)
            : new TimelineHit(TimelinePart.MotionLane, frame, null, null, row, layer.Key);
    }

    /// <summary>
    /// What of the clip band is at <paramref name="x"/>: a part's edge first (left of a boundary the part
    /// before ends, right of it the next one starts; the clip's start and end are edges too), then the
    /// outer edge of a join's blend, then a join's blend, then on a loop the seam's edge and blend, then
    /// the part, else the empty band.
    /// </summary>
    private TimelineHit HitClip(TimelineDocument doc, TimelineView view, float x, float frame, bool loops)
    {
        var cuts = CutEdits.CutsOf(doc);
        var length = doc.Length;
        var count = cuts.Parts.Length;
        for (var i = 0; i <= count; i++)
        {
            var at = view.X((float)(i == count ? cuts.PartEnd(count - 1, length) : cuts.PartStart(i, length)));
            if (MathF.Abs(x - at) > Edge)
                continue;
            return i == count || i > 0 && x < at
                ? new TimelineHit(TimelinePart.ClipRight, frame, Cut: i - 1)
                : new TimelineHit(TimelinePart.ClipLeft, frame, Cut: i);
        }
        var windows = new (float Left, float Right, bool Before, bool After)[cuts.Joins.Length];
        for (var j = 0; j < windows.Length; j++)
        {
            var (before, after) = MotionCutter.Window(cuts, j, length);
            var at = cuts.PartEnd(j, length);
            windows[j] = (view.X((float)(at - before)), view.X((float)(at + after)), before > 0, after > 0);
        }
        for (var j = 0; j < windows.Length; j++)
            if (windows[j].Before && MathF.Abs(x - windows[j].Left) <= Edge || windows[j].After && MathF.Abs(x - windows[j].Right) <= Edge)
                return new TimelineHit(TimelinePart.JoinEdge, frame, Cut: j);
        for (var j = 0; j < windows.Length; j++)
            if ((windows[j].Before || windows[j].After) && x >= windows[j].Left && x <= windows[j].Right)
                return new TimelineHit(TimelinePart.Join, frame, Cut: j);
        if (loops && cuts.Seam != null)
        {
            var (before, after) = MotionCutter.Window(cuts, -1, length);
            float end = view.X(length), left = view.X((float)(length - before)), right = view.X((float)after), start = view.X(0);
            if (before > 0 && MathF.Abs(x - left) <= Edge || after > 0 && MathF.Abs(x - right) <= Edge)
                return new TimelineHit(TimelinePart.SeamEdge, frame);
            if (before > 0 && x >= left && x <= end || after > 0 && x >= start && x <= right)
                return new TimelineHit(TimelinePart.Seam, frame);
        }
        return frame >= 0 && frame <= length
            ? new TimelineHit(TimelinePart.ClipPart, frame, Cut: cuts.PartAt(frame, length))
            : new TimelineHit(TimelinePart.ClipRow, frame);
    }

    /// <summary>One bone's row: the nearest of its keys within a marker's reach, else the empty row.</summary>
    private TimelineHit HitBone(MotionLayer layer, string bone, TimelineView view, float x, float frame, int row)
    {
        var best = float.MaxValue;
        var found = -1;
        foreach (var key in layer.Keys)
        {
            if (!key.Bones.ContainsKey(bone))
                continue;
            var distance = MathF.Abs(x - view.X(key.Frame));
            if (distance <= Marker / 2 + 1 && distance < best)
            {
                best = distance;
                found = key.Frame;
            }
        }
        return found >= 0
            ? new TimelineHit(TimelinePart.BoneKey, frame, null, null, row, layer.Key, found, bone)
            : new TimelineHit(TimelinePart.BoneLane, frame, null, null, row, layer.Key, Bone: bone);
    }

    /// <summary>Which part of one event's grab zones <paramref name="x"/> is in, or None.</summary>
    private TimelinePart PartAt(TimelineEvent ev, int end, TimelineView view, float x)
    {
        var (left, right) = EventSpan(ev, end, view);
        if (TimelineEdits.CanResize(ev) && ev.Duration > 0)
        {
            var edge = RightEdgeX(ev, view);
            // Inside the clip each edge takes at most a third, so a narrow clip can still be moved.
            var inner = Math.Min(Edge, (edge - left) / 3);
            var nearLeft = x >= left - Edge && x <= left + inner;
            var nearRight = x >= edge - inner && x <= edge + Edge;
            if (nearLeft && nearRight)
                return MathF.Abs(x - left) <= MathF.Abs(x - edge) ? TimelinePart.LeftEdge : TimelinePart.RightEdge;
            if (nearLeft)
                return TimelinePart.LeftEdge;
            if (nearRight)
                return TimelinePart.RightEdge;
        }
        return x >= left && x <= right ? TimelinePart.Body : TimelinePart.None;
    }
}
