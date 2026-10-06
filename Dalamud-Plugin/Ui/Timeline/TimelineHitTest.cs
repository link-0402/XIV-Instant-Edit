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
}

/// <param name="Frame">The fractional frame under the pointer; the editor rounds it.</param>
/// <param name="Lane">The lane of the row under the pointer, null on the ruler or outside the rows.</param>
/// <param name="Key">The event under the pointer for Body, LeftEdge and RightEdge.</param>
/// <param name="Row">The row under the pointer, -1 outside the rows.</param>
/// <param name="Layer">The bone layer of the row under the pointer, also on the legend.</param>
/// <param name="KeyFrame">The frame of the layer's key under the pointer for LayerKey, else -1.</param>
internal readonly record struct TimelineHit(TimelinePart Part, float Frame, TimelineLane? Lane = null, string? Key = null, int Row = -1,
    string? Layer = null, int KeyFrame = -1)
{
    public static TimelineHit None(float frame = 0) => new(TimelinePart.None, frame);
    public bool OnEvent => Key != null && Part is TimelinePart.Body or TimelinePart.LeftEdge or TimelinePart.RightEdge;
    public bool OnLayer => Layer != null && Part is TimelinePart.LayerBody or TimelinePart.LayerLeft or TimelinePart.LayerRight or
        TimelinePart.LayerFadeIn or TimelinePart.LayerFadeOut or TimelinePart.LayerKey;
}

/// <summary>
/// Pixel sizes of the drawn timeline, all already scaled: legend width, ruler height, row height,
/// the edge grab tolerance and the narrowest an event may be to still be grabbed. Rows scroll up by
/// <paramref name="ScrollY"/> under a fixed ruler. Points are relative to the canvas' top left corner,
/// legend included. No ImGui or Dalamud dependencies.
/// </summary>
internal readonly record struct TimelineGeometry(float Legend, float Ruler, float Row, float Edge, float Marker, float ScrollY = 0)
{
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
    public float RowTop(int row) => TimelineLayout.RowTop(row, Row, Ruler, ScrollY);

    /// <summary>
    /// What is at <paramref name="point"/>. Order: the length handle; then the events of the row, where
    /// one whose own frames lie under the pointer wins at once, from the last drawn to the first, its
    /// edges before its body; where only widened grab zones reach the pointer (a marker's minimum
    /// width, an edge's tolerance) the nearest event wins, so stacked markers all stay grabbable; then
    /// the ruler; then empty lane space.
    /// </summary>
    public TimelineHit HitTest(TimelineDocument doc, TimelineLayout layout, TimelineView view, Vector2 point)
    {
        var x = point.X - Legend;
        var y = point.Y;
        var frame = view.FrameAt(x);
        if (!float.IsFinite(x) || !float.IsFinite(y) || point.X < 0 || y < 0 || x > view.Width)
            return TimelineHit.None(float.IsFinite(frame) ? frame : 0);
        var row = layout.RowAt(y, Row, Ruler, ScrollY);
        TimelineLane? lane = row >= 0 ? layout.LaneAt(row)?.Lane : null;
        var layerIndex = row >= 0 ? layout.LayerIndexAt(row) : -1;
        if (x < 0)
            return row >= 0
                ? new TimelineHit(TimelinePart.Legend, frame, lane, null, row, layerIndex >= 0 ? doc.Motion[layerIndex].Key : null)
                : TimelineHit.None(frame);

        if (MathF.Abs(x - view.X(doc.Length)) <= Edge && (y < Ruler || lane == TimelineLane.Motion))
            return new TimelineHit(TimelinePart.LengthHandle, frame, lane, null, row);
        if (y < Ruler)
            return new TimelineHit(TimelinePart.Ruler, frame);
        if (row < 0)
            return TimelineHit.None(frame);
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
