using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The clip band under the ruler: the cut motion's parts, each labelled with the motion's own frames
/// it keeps and a line of how fast the motion moves, the blends where parts join, the loop seam's,
/// and on the ruler how many frames each cut took out. Its right-click menu cuts at the pointer.
/// </summary>
public sealed partial class TimelineWindow
{
    // The motion's speed per frame for the speed line, from the decode it was read from, and its top speed.
    private float[]? speedLine;
    private string? speedLineKey;
    private float speedLineMax;

    /// <summary>The clip band's height for <paramref name="doc"/>: none on an expression's timeline, which is never cut.</summary>
    private static float ClipBand(TimelineDocument doc, float row) => doc.Expression ? 0 : row * 1.5f;

    private void DrawClipBand(ImDrawListPtr drawList, TimelineEditor current, TimelineGeometry geometry, Vector2 min, Vector2 max,
        float trackLeft, TimelineHit hit)
    {
        if (geometry.Clip <= 0)
            return;
        var doc = current.Document;
        var view = current.View;
        var top = min.Y + geometry.Ruler;
        var bottom = top + geometry.Clip;
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        var fontSize = ImGui.GetFontSize();
        var motion = TimelineWindowViews.LaneColour(TimelineLane.Motion);
        drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), U32(Theme.PanelBg));
        drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), U32(motion));
        drawList.AddText(new Vector2(min.X + Theme.Scaled(30), top + (geometry.Clip - fontSize) / 2), U32(Theme.Label), TimelineWindowViews.ClipLegend);

        var cuts = CutEdits.CutsOf(doc);
        var speeds = source is { Standalone: false } loaded ? SpeedLine(loaded) : null;
        var barTop = top + Theme.Scaled(3);
        var barBottom = bottom - Theme.Scaled(3);
        var middle = (barTop + barBottom) / 2;
        drawList.PushClipRect(new Vector2(trackLeft, top), new Vector2(max.X, bottom), true);
        for (var i = 0; i < cuts.Parts.Length; i++)
        {
            var start = cuts.PartStart(i, doc.Length);
            var end = cuts.PartEnd(i, doc.Length);
            var left = trackLeft + view.X((float)start);
            var right = trackLeft + view.X((float)end);
            if (right < trackLeft || left > max.X)
                continue;
            var hot = hit.Part is TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight && hit.Cut == i;
            var rounding = Theme.Scaled(3);
            drawList.AddRectFilled(new Vector2(left, barTop), new Vector2(right, barBottom), U32(Theme.WithAlpha(motion, hot ? .5f : .35f)), rounding);
            drawList.AddRect(new Vector2(left, barTop), new Vector2(right, barBottom), U32(Theme.WithAlpha(motion, .9f)), rounding, ImDrawFlags.None, line);
            if (current.SelectedCut is { Target: TimelineCutTarget.Part, Index: var selected } && selected == i)
                drawList.AddRect(new Vector2(left - 1, barTop - 1), new Vector2(right + 1, barBottom + 1), U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            if (speeds != null && speedLineMax > 0)
                DrawSpeedLine(drawList, cuts, doc.Length, view, trackLeft, start, end, speeds, middle, barBottom, line);
            drawList.PushClipRect(new Vector2(left, top), new Vector2(right, bottom), true);
            drawList.AddText(new Vector2(left + Theme.Scaled(4), barTop + Theme.Scaled(1)), U32(Theme.Label), TimelineWindowViews.PartLabel(cuts.Parts[i]));
            drawList.PopClipRect();
        }
        // Joins: a bow-tie as wide as the frames each blend really uses.
        for (var j = 0; j < cuts.Joins.Length; j++)
        {
            var (before, after) = MotionCutter.Window(cuts, j, doc.Length);
            var at = cuts.PartEnd(j, doc.Length);
            var x = trackLeft + view.X((float)at);
            var chosen = current.SelectedCut is { Target: TimelineCutTarget.Join, Index: var index } && index == j ||
                         hit.Part is TimelinePart.Join or TimelinePart.JoinEdge && hit.Cut == j;
            var fill = U32(Theme.WithAlpha(Theme.Accent, chosen ? .85f : .55f));
            if (before > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(at - before)), barTop),
                    new Vector2(trackLeft + view.X((float)(at - before)), barBottom), new Vector2(x, middle), fill);
            if (after > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(at + after)), barTop),
                    new Vector2(x, middle), new Vector2(trackLeft + view.X((float)(at + after)), barBottom), fill);
            drawList.AddLine(new Vector2(Crisp(x), barTop), new Vector2(Crisp(x), barBottom), U32(Theme.Accent), line);
        }
        // The seam, on a loop: a wedge rising into the end, or out of the start for a blend placed after.
        if (current.Loops && cuts.Seam != null)
        {
            var (before, after) = MotionCutter.Window(cuts, -1, doc.Length);
            var chosen = current.SelectedCut is { Target: TimelineCutTarget.Seam } || hit.Part is TimelinePart.Seam or TimelinePart.SeamEdge;
            var fill = U32(Theme.WithAlpha(Theme.Accent, chosen ? .85f : .55f));
            var end = trackLeft + view.X(doc.Length);
            if (before > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(doc.Length - before)), barBottom),
                    new Vector2(end, barTop), new Vector2(end, barBottom), fill);
            if (after > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X(0), barTop),
                    new Vector2(trackLeft + view.X((float)after), barBottom), new Vector2(trackLeft + view.X(0), barBottom), fill);
        }
        DrawDecodeState(drawList, max, top, bottom);
        DrawSeamBadge(drawList, current, max, trackLeft, top, bottom);
        drawList.PopClipRect();
        DrawRemovedMarkers(drawList, cuts, doc.Length, view, min, max, trackLeft, top);
        drawList.AddLine(new Vector2(min.X, Crisp(top)), new Vector2(max.X, Crisp(top)), U32(Theme.WithAlpha(Theme.TreeLine, .5f)), line);
    }

    /// <summary>The speed line inside one part: the motion's speed at the source frame each output frame plays, in the bar's lower half.</summary>
    private void DrawSpeedLine(ImDrawListPtr drawList, MotionCuts cuts, int length, TimelineView view, float trackLeft, double start, double end,
        float[] speeds, float middle, float barBottom, float line)
    {
        var first = Math.Max(start, view.First);
        var last = Math.Min(end, view.Last);
        if (last <= first)
            return;
        var step = Math.Max(1, 2 / view.Scale);
        var colour = U32(Theme.WithAlpha(Theme.Label, .55f));
        var height = (barBottom - middle) * .9f;
        Vector2? previous = null;
        for (var o = first; o <= last + step / 2; o += step)
        {
            var at = Math.Min(o, last);
            var frame = Math.Clamp((int)cuts.SourceAt(at, length), 0, speeds.Length - 1);
            var point = new Vector2(trackLeft + view.X((float)at), barBottom - Math.Min(1, speeds[frame] / speedLineMax) * height);
            if (previous is { } from)
                drawList.AddLine(from, point, colour, line);
            previous = point;
        }
    }

    /// <summary>While the motion is read, how far along on the band's right; why it failed when it did.</summary>
    private void DrawDecodeState(ImDrawListPtr drawList, Vector2 max, float top, float bottom)
    {
        string? text = null;
        var colour = Theme.Muted;
        if (decodeTask != null)
            text = decodeStatus;
        else if (decodeError != null && source != null && failedDecodeKey == DecodeKey(Fresh(source)))
            (text, colour) = ("The motion could not be read: " + decodeError, Theme.Warning);
        if (text == null)
            return;
        var size = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(max.X - size.X - Theme.Scaled(6), top + (bottom - top - size.Y) / 2), U32(colour), text);
    }

    /// <summary>On the ruler, where frames were cut out and how many: at each join, and at a trimmed start or end.</summary>
    private static void DrawRemovedMarkers(ImDrawListPtr drawList, MotionCuts cuts, int length, TimelineView view, Vector2 min, Vector2 max,
        float trackLeft, float rulerBottom)
    {
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), new Vector2(max.X, rulerBottom), true);
        var colour = U32(Theme.Warning);
        void Mark(double at, int frames)
        {
            if (frames <= 0)
                return;
            var x = Crisp(trackLeft + view.X((float)at));
            var text = TimelineWindowViews.RemovedMarker(frames);
            var size = ImGui.CalcTextSize(text);
            drawList.AddLine(new Vector2(x, rulerBottom - Theme.Scaled(6)), new Vector2(x, rulerBottom), colour, Math.Max(1, MathF.Floor(Theme.Scale)));
            drawList.AddText(new Vector2(Math.Max(trackLeft, x - size.X / 2), rulerBottom - size.Y - Theme.Scaled(5)), colour, text);
        }
        Mark(0, cuts.Parts[0].From);
        for (var j = 0; j < cuts.Joins.Length; j++)
            Mark(cuts.PartEnd(j, length), cuts.Parts[j + 1].From - cuts.Parts[j].To);
        Mark(length, cuts.Source - cuts.Parts[^1].To);
        drawList.PopClipRect();
    }

    /// <summary>The motion's speed per frame for the speed line, read once per decode; null until the motion is decoded, which this starts.</summary>
    private float[]? SpeedLine(AnimationTimelineSource loaded)
    {
        if (DecodedPose(loaded) is not { } clip)
            return null;
        if (speedLineKey != decodedKey || speedLine == null)
        {
            speedLine = LoopSeam.Speeds(clip);
            speedLineMax = speedLine.Length == 0 ? 0 : speedLine.Max();
            speedLineKey = decodedKey;
        }
        return speedLine;
    }

    /// <summary>The clip band's tooltip: what the part, edge, join or seam under the pointer does.</summary>
    private static bool DrawClipTooltip(TimelineHit hit, bool readOnly)
    {
        if (hit.Part is not (TimelinePart.ClipRow or TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight or
                TimelinePart.Join or TimelinePart.JoinEdge or TimelinePart.Seam or TimelinePart.SeamEdge))
            return false;
        if (!Widgets.HoverDelay($"clip:{hit.Part}:{hit.Cut}"))
            return true;
        ImGui.SetTooltip(hit.Part switch
        {
            TimelinePart.ClipLeft when !readOnly => "Drag to trim the start of this part.",
            TimelinePart.ClipRight when !readOnly => "Drag to trim the end of this part.",
            TimelinePart.JoinEdge or TimelinePart.SeamEdge when !readOnly => "Drag to change how long the blend is.",
            TimelinePart.Join => "The blend where two parts meet. Click to set it.",
            TimelinePart.Seam => "The blend from the loop's end into its start. Click to set it.",
            _ => "C cuts at the playhead. Q and W remove before or after it. Right-click to cut here.",
        });
        return true;
    }

    /// <summary>The clip band's right-click menu, at the frame it was opened on.</summary>
    private void DrawClipMenu(TimelineEditor current, bool readOnly)
    {
        var menu = TimelineWindowViews.ClipMenu;
        if (ImGui.MenuItem(menu[0], "C", false, !readOnly))
            current.CutAt(menuFrame);
        if (ImGui.MenuItem(menu[1], "", false, !readOnly))
            current.RemoveAfter(menuFrame);
        if (ImGui.MenuItem(menu[2], "", false, !readOnly))
            current.RemoveBefore(menuFrame);
        var part = current.SelectedCut is { Target: TimelineCutTarget.Part };
        if (ImGui.MenuItem(menu[3], "Del", false, !readOnly && part))
            current.RemoveSelectedPart();
        ImGui.Separator();
        if (ImGui.MenuItem("Fit", "F"))
            current.Fit();
    }
}
