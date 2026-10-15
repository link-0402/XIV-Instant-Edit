using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// How the canvas draws an event and the bone layers: bars, markers and their badges, each layer's
/// weight and keys, and the bone rows of an unfolded layer. Stateless, so the canvas hands it what to draw.
/// </summary>
internal static class TimelinePainter
{
    private static readonly string LockGlyph = FontAwesomeIcon.Lock.ToIconString();
    // A face from the timeline's expression library.
    private static readonly string PackGlyph = FontAwesomeIcon.Book.ToIconString();
    private static readonly string PinGlyph = FontAwesomeIcon.Thumbtack.ToIconString();

    /// <summary>
    /// What a sound clip shows besides its bar: the frame its solid part ends at (int.MaxValue while
    /// its file is read), a pin, its file's changes, and something to check (a sound that ends before
    /// its audio, or Keep looping on a file that plays once).
    /// </summary>
    public readonly record struct SoundMark(int SolidEnd, bool Pinned, bool Changed, bool Check);

    /// <summary> A dashed outline: what a random sound may still play after its shortest alternative ends. </summary>
    private static void DashedOutline(ImDrawListPtr drawList, float x0, float y0, float x1, float y1, uint colour, float thickness)
    {
        var dash = Theme.Scaled(4);
        var gap = Theme.Scaled(3);
        for (var x = x0; x < x1; x += dash + gap)
        {
            var end = Math.Min(x + dash, x1);
            drawList.AddLine(new Vector2(x, y0), new Vector2(end, y0), colour, thickness);
            drawList.AddLine(new Vector2(x, y1), new Vector2(end, y1), colour, thickness);
        }
        drawList.AddLine(new Vector2(x1, y0), new Vector2(x1, y1), colour, thickness);
    }

    /// <summary>
    /// Whether <paramref name="ev"/> is a face from the timeline's expression library, marked with a
    /// book; unmarked until the catalog knows which faces are always loaded.
    /// </summary>
    public static bool FromPack(TimelineEditor current, TimelineEvent ev) =>
        current.ResidentMotions is { } resident && TimelineEdits.FromPack(current.Document, ev, resident);

    /// <summary>
    /// One event: a clip is a filled bar over its duration, a marker a diamond on its frame, and a
    /// sound also shows how long its file plays: solid to its shortest alternative, dashed to its
    /// longest. Selected events get an accent outline, locked ones are dimmed with a lock, pinned
    /// sounds get a pin, conditional ones a small dot, a changed look or sound an accent dot and a
    /// sound with something to check an amber one.
    /// </summary>
    public static void DrawEvent(ImDrawListPtr drawList, TimelineEditor current, TimelineEvent ev, string label, int end,
        TimelineGeometry geometry, TimelineView view, float trackLeft, float y0, float y1, bool hot, SoundMark? sound = null)
    {
        var (left, right) = geometry.EventSpan(ev, end, view);
        var x0 = trackLeft + left;
        var x1 = trackLeft + right;
        var colour = TimelineWindowViews.LaneColour(ev.Lane);
        var locked = ev.Access == TimelineAccess.Locked;
        var selected = current.IsSelected(ev.Key);
        var rounding = Theme.Scaled(3);
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        var middle = (y0 + y1) / 2;
        var fontSize = ImGui.GetFontSize();
        float labelLeft, labelRight, badgeX;
        if (ev.Duration > 0)
        {
            var edge = Math.Max(trackLeft + geometry.RightEdgeX(ev, view), x0 + 1);
            var inset = (y1 - y0) * .25f;
            if (x1 > edge + 1)
                drawList.AddRectFilled(new Vector2(edge, y0 + inset), new Vector2(x1, y1 - inset), TimelineUi.U32(Theme.WithAlpha(colour, .2f)), rounding);
            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(edge, y1), TimelineUi.U32(Theme.WithAlpha(colour, locked ? .24f : hot ? .8f : .6f)), rounding);
            drawList.AddRect(new Vector2(x0, y0), new Vector2(edge, y1), TimelineUi.U32(Theme.WithAlpha(colour, locked ? .45f : .95f)), rounding, ImDrawFlags.None, line);
            if (selected)
                drawList.AddRect(new Vector2(x0 - 1, y0 - 1), new Vector2(edge + 1, y1 + 1), TimelineUi.U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            labelLeft = x0 + Theme.Scaled(4);
            labelRight = edge - Theme.Scaled(4);
            badgeX = edge - Theme.Scaled(5);
        }
        else
        {
            var centre = trackLeft + view.X(ev.Start);
            var half = Math.Min((y1 - y0) / 2, Theme.Scaled(7));
            // A random sound is solid while every alternative plays, dashed while only the longer ones do.
            var solid = sound is { SolidEnd: var solidEnd and < int.MaxValue } ? Math.Clamp(trackLeft + view.X(solidEnd), centre, x1) : x1;
            if (solid > centre + half + 1)
                drawList.AddRectFilled(new Vector2(centre, middle - half * .6f), new Vector2(solid, middle + half * .6f),
                    TimelineUi.U32(Theme.WithAlpha(colour, hot ? .32f : .22f)), rounding);
            if (x1 > solid + 1)
                DashedOutline(drawList, Math.Max(solid, centre + half), middle - half * .6f, x1, middle + half * .6f,
                    TimelineUi.U32(Theme.WithAlpha(colour, hot ? .7f : .5f)), line);
            if (selected)
            {
                var ring = half + Theme.Scaled(2);
                drawList.AddQuadFilled(new Vector2(centre, middle - ring), new Vector2(centre + ring, middle), new Vector2(centre, middle + ring),
                    new Vector2(centre - ring, middle), TimelineUi.U32(Theme.Accent));
            }
            drawList.AddQuadFilled(new Vector2(centre, middle - half), new Vector2(centre + half, middle), new Vector2(centre, middle + half),
                new Vector2(centre - half, middle), TimelineUi.U32(Theme.WithAlpha(colour, locked ? .5f : hot ? 1 : .9f)));
            labelLeft = centre + half + Theme.Scaled(4);
            labelRight = x1 - Theme.Scaled(3);
            badgeX = centre + half;
        }
        if (locked && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), TimelineUi.U32(Theme.Muted), LockGlyph);
            labelLeft += fontSize;
        }
        if (FromPack(current, ev) && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), TimelineUi.U32(Theme.Text), PackGlyph);
            labelLeft += fontSize;
        }
        if (sound is { Pinned: true } && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), TimelineUi.U32(Theme.Text), PinGlyph);
            labelLeft += fontSize;
        }
        if (labelRight - labelLeft > Theme.Scaled(18))
        {
            drawList.PushClipRect(new Vector2(labelLeft, y0), new Vector2(labelRight, y1), true);
            drawList.AddText(new Vector2(labelLeft, middle - fontSize / 2), TimelineUi.U32(locked ? Theme.Muted : Theme.Text), label);
            drawList.PopClipRect();
        }
        var dot = badgeX;
        if (ev.Conditional)
        {
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), TimelineUi.U32(Theme.Info));
            dot -= Theme.Scaled(7);
        }
        if (ev.Look != null || sound is { Changed: true })
        {
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), TimelineUi.U32(Theme.Accent));
            dot -= Theme.Scaled(7);
        }
        if (sound is { Check: true })
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), TimelineUi.U32(Theme.Warning));
    }

    /// <summary>
    /// Each bone layer: a faint bar over its frames with its weight drawn on it, rising over the fade in
    /// and falling over the fade out; its keys as diamonds; and, when it is selected or under the
    /// pointer, handles where the fades end. A layer turned off is dimmed.
    /// </summary>
    public static void DrawLayers(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineLayout layout,
        TimelineGeometry geometry, TimelineView view, Vector2 min, Vector2 max, float trackLeft, float rowsTop, TimelineHit hit)
    {
        var pad = Theme.Scaled(2);
        var rounding = Theme.Scaled(3);
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        var fontSize = ImGui.GetFontSize();
        for (var index = 0; index < layout.LayerCount; index++)
        {
            var layer = doc.Motion[index];
            var top = min.Y + geometry.RowTop(layout.LayerRow(index));
            if (top + geometry.Row < rowsTop || top > max.Y || layer.End < view.First || layer.Start > view.Last)
                continue;
            var y0 = top + pad;
            var y1 = top + geometry.Row - pad;
            var x0 = trackLeft + view.X(layer.Start);
            var x1 = Math.Max(trackLeft + view.X(layer.End), x0 + 1);
            var fadeInX = Math.Clamp(trackLeft + view.X(layer.Start + layer.FadeIn), x0, x1);
            var fadeOutX = Math.Clamp(trackLeft + view.X(layer.End - layer.FadeOut), fadeInX, x1);
            var colour = TimelineWindowViews.LayerColour(layer.Kind);
            var selected = current.SelectedLayer == layer.Key;
            var hot = hit.OnLayer && hit.Layer == layer.Key;
            var alpha = layer.Enabled ? 1f : .4f;
            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), TimelineUi.U32(Theme.WithAlpha(colour, .14f * alpha)), rounding);
            drawList.AddQuadFilled(new Vector2(x0, y1), new Vector2(fadeInX, y0), new Vector2(fadeOutX, y0), new Vector2(x1, y1),
                TimelineUi.U32(Theme.WithAlpha(colour, (hot ? .62f : .46f) * alpha)));
            drawList.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), TimelineUi.U32(Theme.WithAlpha(colour, .9f * alpha)), rounding, ImDrawFlags.None, line);
            if (selected)
                drawList.AddRect(new Vector2(x0 - 1, y0 - 1), new Vector2(x1 + 1, y1 + 1), TimelineUi.U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            var labelLeft = Math.Max(x0, trackLeft) + Theme.Scaled(4);
            var labelRight = x1 - Theme.Scaled(4);
            if (labelRight - labelLeft > Theme.Scaled(18))
            {
                drawList.PushClipRect(new Vector2(labelLeft, y0), new Vector2(labelRight, y1), true);
                drawList.AddText(new Vector2(labelLeft, top + (geometry.Row - fontSize) / 2), TimelineUi.U32(layer.Enabled ? Theme.Text : Theme.Muted),
                    TimelineWindowViews.LayerLabel(layer));
                drawList.PopClipRect();
            }
            var middle = (y0 + y1) / 2;
            var half = Math.Min((y1 - y0) / 2, Theme.Scaled(5));
            foreach (var key in layer.Keys)
            {
                var x = trackLeft + view.X(key.Frame);
                if (x < trackLeft - half || x > max.X + half)
                    continue;
                var shape = KeyShapeOf(layer, key);
                if (selected && current.SelectedKey == key.Frame)
                    DrawKeyMark(drawList, shape, x, middle, half + Theme.Scaled(2),
                        TimelineUi.U32(current.SelectedBone == null ? Theme.Accent : Theme.WithAlpha(Theme.Accent, .45f)));
                // A key outside the layer still shapes the curve but never applies on its own.
                var inside = key.Frame >= layer.Start && key.Frame <= layer.End;
                var hotKey = hot && hit.Part == TimelinePart.LayerKey && hit.KeyFrame == key.Frame;
                DrawKeyMark(drawList, shape, x, middle, half, TimelineUi.U32(Theme.WithAlpha(Theme.Text, (inside ? hotKey ? 1 : .88f : .4f) * alpha)));
            }
            if (selected || hot)
            {
                var radius = Theme.Scaled(3);
                drawList.AddCircleFilled(new Vector2(fadeInX, y0), radius,
                    TimelineUi.U32(hot && hit.Part == TimelinePart.LayerFadeIn ? Theme.Accent : Theme.WithAlpha(Theme.Text, .8f)));
                drawList.AddCircleFilled(new Vector2(fadeOutX, y0), radius,
                    TimelineUi.U32(hot && hit.Part == TimelinePart.LayerFadeOut ? Theme.Accent : Theme.WithAlpha(Theme.Text, .8f)));
            }
        }
        DrawBoneRows(drawList, current, doc, layout, geometry, view, min, max, trackLeft, rowsTop, hit);
    }

    private enum KeyShape : byte { Diamond, Circle, Square }

    /// <summary>A hold is a square, a key with its own ease a circle, a key going the layer's way a diamond.</summary>
    private static KeyShape KeyShapeOf(MotionLayer layer, MotionKey key) =>
        MotionEvaluator.KeyEase(layer, key) == MotionInterpolation.Step ? KeyShape.Square : key.Ease != null ? KeyShape.Circle : KeyShape.Diamond;

    private static void DrawKeyMark(ImDrawListPtr drawList, KeyShape shape, float x, float y, float half, uint colour)
    {
        switch (shape)
        {
            case KeyShape.Square:
                var side = half * .8f;
                drawList.AddRectFilled(new Vector2(x - side, y - side), new Vector2(x + side, y + side), colour);
                break;
            case KeyShape.Circle:
                drawList.AddCircleFilled(new Vector2(x, y), half * .9f, colour);
                break;
            default:
                drawList.AddQuadFilled(new Vector2(x, y - half), new Vector2(x + half, y), new Vector2(x, y + half), new Vector2(x - half, y), colour);
                break;
        }
    }

    /// <summary>
    /// An unfolded layer's bone rows: a thin line over the layer's frames and the bone's own keys, smaller
    /// than the layer's. The selected one gets a ring, and so does each bone's part of a selected whole key.
    /// </summary>
    private static void DrawBoneRows(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineLayout layout,
        TimelineGeometry geometry, TimelineView view, Vector2 min, Vector2 max, float trackLeft, float rowsTop, TimelineHit hit)
    {
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        for (var row = layout.MotionFirstRow; row < layout.MotionFirstRow + layout.MotionRows; row++)
        {
            if (layout.BoneAt(row) is not { } entry)
                continue;
            var top = min.Y + geometry.RowTop(row);
            if (top + geometry.Row < rowsTop || top > max.Y)
                continue;
            var layer = doc.Motion[entry.Layer];
            var colour = TimelineWindowViews.LayerColour(layer.Kind);
            var alpha = layer.Enabled ? 1f : .4f;
            var middle = top + geometry.Row / 2;
            var x0 = trackLeft + view.X(layer.Start);
            var x1 = Math.Max(trackLeft + view.X(layer.End), x0 + 1);
            drawList.AddLine(new Vector2(x0, middle), new Vector2(x1, middle), TimelineUi.U32(Theme.WithAlpha(colour, .45f * alpha)), line);
            var half = Math.Min(geometry.Row / 2 - Theme.Scaled(2), Theme.Scaled(4));
            var selectedLayer = current.SelectedLayer == layer.Key;
            foreach (var key in layer.Keys)
            {
                if (!key.Bones.ContainsKey(entry.Bone))
                    continue;
                var x = trackLeft + view.X(key.Frame);
                if (x < trackLeft - half || x > max.X + half)
                    continue;
                var shape = KeyShapeOf(layer, key);
                if (selectedLayer && current.SelectedKey == key.Frame && (current.SelectedBone == null || current.SelectedBone == entry.Bone))
                    DrawKeyMark(drawList, shape, x, middle, half + Theme.Scaled(2),
                        TimelineUi.U32(current.SelectedBone == entry.Bone ? Theme.Accent : Theme.WithAlpha(Theme.Accent, .45f)));
                var inside = key.Frame >= layer.Start && key.Frame <= layer.End;
                var hotKey = hit.Part == TimelinePart.BoneKey && hit.Row == row && hit.KeyFrame == key.Frame;
                DrawKeyMark(drawList, shape, x, middle, half, TimelineUi.U32(Theme.WithAlpha(hotKey ? Theme.Text : colour, (inside ? hotKey ? 1 : .95f : .4f) * alpha)));
            }
        }
    }
}
