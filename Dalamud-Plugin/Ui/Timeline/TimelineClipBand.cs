using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The clip band under the ruler: the cut motion's parts, each labelled with the motion's own frames
/// it keeps and a line of how fast the motion moves, the blends where parts join, the loop seam's,
/// and on the ruler how many frames each cut took out. Its right-click menu cuts at the pointer, and
/// the seam's badge at its end selects the seam. The loop seam is measured here for the cut panel too.
/// While the clip is paused it marks the frames that play the playhead's pose the same way, for finding a loop.
/// </summary>
internal sealed class TimelineClipBand(TimelineSession session, MotionDecode decode)
{
    // The motion's speed per frame for the speed line, from the decode it was read from, and its top speed.
    private float[]? speedLine;
    private string? speedLineKey;
    private float speedLineMax;
    // The loop seam as the motion has it (cached per decode and range), and as the cut output plays it.
    private string? rawSeamKey;
    private SeamReport? rawSeam;
    private MotionCuts? outputSeamCuts;
    private int outputSeamLength;
    private string? outputSeamDecode;
    private SeamReport? outputSeam;
    // The loop seam as the cut output plays with its bone layers, measured on a task.
    private readonly EditedSeam editedSeam = new();
    // Where the seam's badge was drawn last, in screen pixels: a click there selects the seam.
    private (Vector2 Min, Vector2 Max)? seamBadge;
    // The motion as the cuts play it, sampled for pose matches once per output motion; the frames playing
    // the playhead's pose the same way, for the samples and playhead they were found for; and where their
    // marks were drawn last, in screen pixels.
    private (MotionClip Motion, Quaternion[][] Poses)? matchPoses;
    private (Quaternion[][] Poses, int Playhead, IReadOnlyList<PoseMatch> Found)? matches;
    private readonly List<(float X, PoseMatch Match)> marks = [];
    private (float Top, float Bottom) markBand;

    /// <summary>The clip band's height for <paramref name="doc"/>: none on an expression's timeline, which is never cut.</summary>
    public static float Height(TimelineDocument doc, float row) => doc.Expression ? 0 : row * 1.5f;

    public void Draw(ImDrawListPtr drawList, TimelineEditor current, TimelineGeometry geometry, Vector2 min, Vector2 max,
        float trackLeft, TimelineHit hit)
    {
        marks.Clear();
        if (geometry.Clip <= 0)
            return;
        var doc = current.Document;
        var view = current.View;
        var top = min.Y + geometry.Ruler;
        var bottom = top + geometry.Clip;
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        var fontSize = ImGui.GetFontSize();
        var motion = TimelineWindowViews.LaneColour(TimelineLane.Motion);
        drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), TimelineUi.U32(Theme.PanelBg));
        drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), TimelineUi.U32(motion));
        drawList.AddText(new Vector2(min.X + Theme.Scaled(30), top + (geometry.Clip - fontSize) / 2), TimelineUi.U32(Theme.Label), TimelineWindowViews.ClipLegend);

        var cuts = CutEdits.CutsOf(doc);
        var speeds = session.Source is { Standalone: false } loaded ? SpeedLine(loaded) : null;
        var ease = cuts.Ease > 0 && decode.Decoded is { } decoded && speeds != null ? MotionCutter.EaseOf(decoded, cuts, doc.Length) : (Frames: 0d, Rate: 1d, AtEnd: false);
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
            drawList.AddRectFilled(new Vector2(left, barTop), new Vector2(right, barBottom), TimelineUi.U32(Theme.WithAlpha(motion, hot ? .5f : .35f)), rounding);
            drawList.AddRect(new Vector2(left, barTop), new Vector2(right, barBottom), TimelineUi.U32(Theme.WithAlpha(motion, .9f)), rounding, ImDrawFlags.None, line);
            if (current.SelectedCut is { Target: TimelineCutTarget.Part, Index: var selected } && selected == i)
                drawList.AddRect(new Vector2(left - 1, barTop - 1), new Vector2(right + 1, barBottom + 1), TimelineUi.U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            // A blend part plays none of the motion's own frames: no speed line, a ramp from its start to its end instead.
            if (cuts.Parts[i].Blend)
                drawList.AddLine(new Vector2(left, barBottom - Theme.Scaled(2)), new Vector2(right, middle), TimelineUi.U32(Theme.WithAlpha(Theme.Accent, .8f)), line);
            else if (speeds != null && speedLineMax > 0)
                DrawSpeedLine(drawList, cuts, ease, doc.Length, view, trackLeft, start, end, cuts.Parts[i].Stretch, speeds, middle, barBottom, line);
            drawList.PushClipRect(new Vector2(left, top), new Vector2(right, bottom), true);
            drawList.AddText(new Vector2(left + Theme.Scaled(4), barTop + Theme.Scaled(1)), TimelineUi.U32(Theme.Label), TimelineWindowViews.PartLabel(cuts.Parts[i], end - start));
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
            var fill = TimelineUi.U32(Theme.WithAlpha(Theme.Accent, chosen ? .85f : .55f));
            if (before > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(at - before)), barTop),
                    new Vector2(trackLeft + view.X((float)(at - before)), barBottom), new Vector2(x, middle), fill);
            if (after > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(at + after)), barTop),
                    new Vector2(x, middle), new Vector2(trackLeft + view.X((float)(at + after)), barBottom), fill);
            drawList.AddLine(new Vector2(TimelineUi.Crisp(x), barTop), new Vector2(TimelineUi.Crisp(x), barBottom), TimelineUi.U32(Theme.Accent), line);
        }
        // The seam, on a loop: a wedge rising into the end, or out of the start for a blend placed after.
        if (current.Loops && cuts.Seam != null)
        {
            var (before, after) = MotionCutter.Window(cuts, -1, doc.Length);
            var chosen = current.SelectedCut is { Target: TimelineCutTarget.Seam } || hit.Part is TimelinePart.Seam or TimelinePart.SeamEdge;
            var fill = TimelineUi.U32(Theme.WithAlpha(Theme.Accent, chosen ? .85f : .55f));
            var end = trackLeft + view.X(doc.Length);
            if (before > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X((float)(doc.Length - before)), barBottom),
                    new Vector2(end, barTop), new Vector2(end, barBottom), fill);
            if (after > 0)
                drawList.AddTriangleFilled(new Vector2(trackLeft + view.X(0), barTop),
                    new Vector2(trackLeft + view.X((float)after), barBottom), new Vector2(trackLeft + view.X(0), barBottom), fill);
        }
        // The seam's ease, on a loop: a ramp from slow at the seam up to normal speed, and its inner edge as a line to drag.
        if (current.Loops && cuts.Ease > 0)
        {
            var chosen = current.SelectedCut is { Target: TimelineCutTarget.Seam } || hit.Part is TimelinePart.Seam or TimelinePart.EaseEdge;
            var (from, to) = cuts.EaseSpan(doc.Length);
            float slow = trackLeft + view.X((float)(cuts.EaseEnd ? to : from)), full = trackLeft + view.X((float)(cuts.EaseEnd ? from : to));
            drawList.AddTriangleFilled(new Vector2(slow, barBottom), new Vector2(full, barTop), new Vector2(full, barBottom),
                TimelineUi.U32(Theme.WithAlpha(Theme.Accent, chosen ? .6f : .35f)));
            drawList.AddLine(new Vector2(TimelineUi.Crisp(full), barTop), new Vector2(TimelineUi.Crisp(full), barBottom), TimelineUi.U32(Theme.Accent), line);
        }
        DrawMatches(drawList, current, max, trackLeft, barTop, barBottom, line);
        DrawDecodeState(drawList, max, top, bottom);
        DrawSeamBadge(drawList, current, max, trackLeft, top, bottom);
        drawList.PopClipRect();
        DrawRemovedMarkers(drawList, cuts, doc.Length, view, min, max, trackLeft, top);
        drawList.AddLine(new Vector2(min.X, TimelineUi.Crisp(top)), new Vector2(max.X, TimelineUi.Crisp(top)), TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .5f)), line);
    }

    /// <summary>
    /// The speed line inside one part: the motion's speed at the source frame each output frame plays, in the
    /// bar's lower half; under the start's <paramref name="ease"/> slowed as much as the ease slows it, and divided by the
    /// part's <paramref name="stretch"/>, so a slowed part reads as slower.
    /// </summary>
    private void DrawSpeedLine(ImDrawListPtr drawList, MotionCuts cuts, (double Frames, double Rate, bool AtEnd) ease, int length, TimelineView view,
        float trackLeft, double start, double end, double stretch, float[] speeds, float middle, float barBottom, float line)
    {
        var first = Math.Max(start, view.First);
        var last = Math.Min(end, view.Last);
        if (last <= first)
            return;
        var step = Math.Max(1, 2 / view.Scale);
        var colour = TimelineUi.U32(Theme.WithAlpha(Theme.Label, .55f));
        var height = (barBottom - middle) * .9f;
        Vector2? previous = null;
        for (var o = first; o <= last + step / 2; o += step)
        {
            var at = Math.Min(o, last);
            var played = MotionCutter.EasedTime(at, ease, length);
            // How fast the ease plays this frame, a step forward, or back at the very end.
            var probe = at + .1 <= length ? .1 : -.1;
            var pace = ease.Frames > 0 ? (MotionCutter.EasedTime(at + probe, ease, length) - played) / probe : 1;
            var frame = Math.Clamp((int)cuts.SourceAt(played, length), 0, speeds.Length - 1);
            var speed = (float)(speeds[frame] * pace / stretch);
            var point = new Vector2(trackLeft + view.X((float)at), barBottom - Math.Min(1, speed / speedLineMax) * height);
            if (previous is { } from)
                drawList.AddLine(from, point, colour, line);
            previous = point;
        }
    }

    /// <summary>
    /// The frames playing the playhead's pose the same way (see <see cref="PoseMatches"/>), in the motion as
    /// the cuts play it; null while the clip plays (the editor's clock, or the playhead following the game in
    /// Preview, Live or Follow), during a drag, or before its motion is read. The motion
    /// is sampled once per cut and length (<see cref="MotionDecode.OutputMotion"/> keeps it), and searched once per playhead.
    /// </summary>
    public IReadOnlyList<PoseMatch>? Matches(TimelineEditor current)
    {
        if (current.Playing || session.PlayheadFollows || current.Gesture != TimelineGesture.None || current.Document.Expression ||
            session.Source is not { Standalone: false } loaded || decode.DecodedPose(loaded) is not { } decoded)
            return null;
        var motion = decode.OutputMotion(current, decoded);
        if (matchPoses is not { } sampled || !ReferenceEquals(sampled.Motion, motion))
            matchPoses = sampled = (motion, PoseMatches.Sample(motion));
        if (matches is { } cached && ReferenceEquals(cached.Poses, sampled.Poses) && cached.Playhead == current.Playhead)
            return cached.Found;
        var found = PoseMatches.Find(sampled.Poses, current.Playhead);
        matches = (sampled.Poses, current.Playhead, found);
        return found;
    }

    /// <summary>On a paused clip, a mark at each frame playing the playhead's pose the same way; the one under the pointer brighter.</summary>
    private void DrawMatches(ImDrawListPtr drawList, TimelineEditor current, Vector2 max, float trackLeft, float top, float bottom, float line)
    {
        markBand = (top, bottom);
        if (Matches(current) is not { Count: > 0 } found)
            return;
        var size = Theme.Scaled(4);
        foreach (var match in found)
        {
            var x = TimelineUi.Crisp(trackLeft + current.View.X(match.Frame));
            if (x >= trackLeft - size && x <= max.X + size)
                marks.Add((x, match));
        }
        var hot = MarkAt(ImGui.GetIO().MousePos);
        foreach (var (x, match) in marks)
        {
            var lit = hot == match;
            var colour = TimelineUi.U32(Theme.WithAlpha(Theme.Success, lit ? 1f : .75f));
            drawList.AddLine(new Vector2(x, top), new Vector2(x, bottom), colour, lit ? line * 2 : line);
            var middle = top + size;
            drawList.AddTriangleFilled(new Vector2(x - size, middle), new Vector2(x, middle - size), new Vector2(x + size, middle), colour);
            drawList.AddTriangleFilled(new Vector2(x - size, middle), new Vector2(x + size, middle), new Vector2(x, middle + size), colour);
        }
    }

    /// <summary>The pose match whose mark is under <paramref name="mouse"/> (screen pixels), as last drawn.</summary>
    public PoseMatch? MarkAt(Vector2 mouse)
    {
        if (marks.Count == 0 || mouse.Y < markBand.Top || mouse.Y > markBand.Bottom)
            return null;
        var reach = Theme.Scaled(5);
        PoseMatch? best = null;
        var closest = float.MaxValue;
        foreach (var (x, match) in marks)
            if (Math.Abs(mouse.X - x) is var distance && distance <= reach && distance < closest)
                (best, closest) = (match, distance);
        return best;
    }

    /// <summary>While the motion is read, how far along on the band's right; why it failed when it did.</summary>
    private void DrawDecodeState(ImDrawListPtr drawList, Vector2 max, float top, float bottom)
    {
        string? text = null;
        var colour = Theme.Muted;
        if (decode.Reading)
            text = decode.Status;
        else if (session.Source != null && decode.FailedFor(session.Source))
            (text, colour) = ("The motion could not be read: " + decode.Error, Theme.Warning);
        if (text == null)
            return;
        var size = ImGui.CalcTextSize(text);
        drawList.AddText(new Vector2(max.X - size.X - Theme.Scaled(6), top + (bottom - top - size.Y) / 2), TimelineUi.U32(colour), text);
    }

    /// <summary>On the ruler, where frames were cut out and how many: at each join, and at a trimmed start or end.</summary>
    private static void DrawRemovedMarkers(ImDrawListPtr drawList, MotionCuts cuts, int length, TimelineView view, Vector2 min, Vector2 max,
        float trackLeft, float rulerBottom)
    {
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), new Vector2(max.X, rulerBottom), true);
        var colour = TimelineUi.U32(Theme.Warning);
        void Mark(double at, int frames)
        {
            if (frames <= 0)
                return;
            var x = TimelineUi.Crisp(trackLeft + view.X((float)at));
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
        if (decode.DecodedPose(loaded) is not { } clip)
            return null;
        if (speedLineKey != decode.DecodedKey || speedLine == null)
        {
            speedLine = LoopSeam.Speeds(clip);
            speedLineMax = speedLine.Length == 0 ? 0 : speedLine.Max();
            speedLineKey = decode.DecodedKey;
        }
        return speedLine;
    }

    /// <summary>The clip band's tooltip: what the part, edge, join or seam under the pointer does.</summary>
    public static bool DrawTooltip(TimelineHit hit, MotionCuts cuts, bool readOnly)
    {
        if (hit.Part is not (TimelinePart.ClipRow or TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight or
                TimelinePart.Join or TimelinePart.JoinEdge or TimelinePart.Seam or TimelinePart.SeamEdge or TimelinePart.EaseEdge))
            return false;
        if (!Widgets.HoverDelay($"clip:{hit.Part}:{hit.Cut}"))
            return true;
        // At a blend part, its own edges resize it and its neighbours' edges roll into it.
        bool Blend(int part) => part >= 0 && part < cuts.Parts.Length && cuts.Parts[part].Blend;
        var edgeBlend = hit.Part is TimelinePart.ClipLeft or TimelinePart.ClipRight && Blend(hit.Cut);
        var edgeRoll = hit.Part == TimelinePart.ClipLeft && Blend(hit.Cut - 1) || hit.Part == TimelinePart.ClipRight && Blend(hit.Cut + 1);
        ImGui.SetTooltip(hit.Part switch
        {
            TimelinePart.ClipLeft or TimelinePart.ClipRight when !readOnly && edgeBlend => TimelineWindowViews.BlendEdgeTip,
            TimelinePart.ClipLeft when !readOnly && edgeRoll => TimelineWindowViews.RollEdgeTip,
            // Shift+drag stretches any part that isn't a blend, this one beside a blend included.
            TimelinePart.ClipRight when !readOnly && edgeRoll => TimelineWindowViews.RollEdgeTip + " " + TimelineWindowViews.StretchEdgeTip,
            TimelinePart.ClipLeft when !readOnly => "Drag to trim the start of this part.",
            TimelinePart.ClipRight when !readOnly => "Drag to trim the end of this part. " + TimelineWindowViews.StretchEdgeTip,
            TimelinePart.JoinEdge or TimelinePart.SeamEdge when !readOnly => "Drag to change how long the blend is.",
            TimelinePart.EaseEdge when !readOnly => "Drag to change how long the start eases in.",
            TimelinePart.Join => "The blend where two parts meet. Click to set it, again for the part under it.",
            TimelinePart.Seam => "Where the loop's end runs back into its start. Click to set it, again for the part under it.",
            _ => "Right-click to cut here or remove before or after. B blends over the selected part.",
        });
        return true;
    }

    /// <summary>The clip band's right-click menu, at the frame it was opened on.</summary>
    public void DrawMenu(TimelineEditor current, bool readOnly, int frame)
    {
        var menu = TimelineWindowViews.ClipMenu;
        if (ImGui.MenuItem(menu[0], "", false, !readOnly))
            current.CutAt(frame);
        if (ImGui.MenuItem(menu[1], "", false, !readOnly))
            current.RemoveAfter(frame);
        if (ImGui.MenuItem(menu[2], "", false, !readOnly))
            current.RemoveBefore(frame);
        var part = current.SelectedCut is { Target: TimelineCutTarget.Part };
        if (ImGui.MenuItem(menu[3], "Del", false, !readOnly && part))
            current.RemoveSelectedPart();
        var cuts = CutEdits.CutsOf(current.Document);
        if (current.SelectedCut is { Target: TimelineCutTarget.Part, Index: var selected } && selected < cuts.Parts.Length)
        {
            var blend = cuts.Parts[selected].Blend;
            if (ImGui.MenuItem(blend ? TimelineWindowViews.PlayItsMotion : TimelineWindowViews.ReplaceWithBlend, "B", false,
                    !readOnly && (blend || CutEdits.CanBlend(cuts, selected))))
                current.ToggleSelectedBlend();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Fit", "F"))
            current.Fit();
    }

    /// <summary>
    /// The loop seam as the motion has it between the first part's start and the last part's end (what
    /// a blend absorbs and Fix loop works from), as the cuts play it (with a seam blend or an eased start,
    /// the cut motion is measured, once per change and never during a drag), and as it plays with the bone
    /// layers (see <see cref="EditedSeam"/>): while that is measured the last measure stands, or the cut seam
    /// the first time, and <paramref name="measured"/> is false. With no layer changing the motion it is the cut seam.
    /// </summary>
    public (SeamReport Raw, SeamReport Cut, SeamReport Edited) SeamOf(TimelineEditor current, MotionClip decoded, out bool measured)
    {
        var (raw, cut) = CutSeamOf(current, decoded);
        measured = true;
        var doc = current.Document;
        if (!MotionEvaluator.Changes(doc.Motion))
            return (raw, cut, cut);
        var output = decode.OutputMotion(current, decoded);
        if (editedSeam.Report(decode.DecodedKey ?? "", output, doc, current.Gesture != TimelineGesture.None) is { } edited)
            return (raw, cut, edited);
        measured = false;
        return (raw, cut, editedSeam.Last ?? cut);
    }

    /// <summary>The loop seam as the motion has it between the first part's start and the last part's end, and as the cuts play it.</summary>
    private (SeamReport Raw, SeamReport Cut) CutSeamOf(TimelineEditor current, MotionClip decoded)
    {
        var doc = current.Document;
        var cuts = CutEdits.CutsOf(doc);
        int from = cuts.Parts[0].From, to = cuts.Parts[^1].To;
        var key = $"{decode.DecodedKey}|{from}|{to}";
        if (rawSeam == null || rawSeamKey != key)
        {
            rawSeam = LoopSeam.Analyze(decoded, from, to);
            rawSeamKey = key;
        }
        if (cuts.Seam is not { Frames: > 0 } && cuts.Ease <= 0)
            return (rawSeam, rawSeam);
        var stale = !ReferenceEquals(outputSeamCuts, doc.Cuts) || outputSeamLength != doc.Length || outputSeamDecode != decode.DecodedKey;
        if (stale && current.Gesture == TimelineGesture.None)
        {
            outputSeam = LoopSeam.Analyze(MotionCutter.Apply(decoded, cuts, doc.Length), 0, doc.Length);
            (outputSeamCuts, outputSeamLength, outputSeamDecode) = (doc.Cuts, doc.Length, decode.DecodedKey);
            stale = false;
        }
        return (rawSeam, !stale && outputSeam is { } shown ? shown : rawSeam);
    }

    /// <summary>The seam's badge at the clip band's end, on a loop whose motion is read; a click on it selects the seam.</summary>
    private void DrawSeamBadge(ImDrawListPtr drawList, TimelineEditor current, Vector2 max, float trackLeft, float top, float bottom)
    {
        seamBadge = null;
        if (!current.Loops || session.Source is not { Standalone: false } loaded || decode.DecodedPose(loaded) is not { } decoded)
            return;
        var shown = SeamOf(current, decoded, out _).Edited.State;
        var text = TimelineWindowViews.SeamBadge(shown);
        var size = ImGui.CalcTextSize(text);
        var pad = Theme.Scaled(4);
        var end = trackLeft + current.View.X(current.Document.Length);
        var left = Math.Min(end + Theme.Scaled(6), max.X - size.X - pad * 2 - Theme.Scaled(4));
        var rect = (new Vector2(left, top + Theme.Scaled(3)), new Vector2(left + size.X + pad * 2, bottom - Theme.Scaled(3)));
        var colour = shown == SeamState.Clean ? Theme.Muted : Theme.Warning;
        drawList.AddRectFilled(rect.Item1, rect.Item2, TimelineUi.U32(Theme.WithAlpha(colour, .18f)), Theme.Scaled(3));
        drawList.AddRect(rect.Item1, rect.Item2, TimelineUi.U32(Theme.WithAlpha(colour, .7f)), Theme.Scaled(3));
        drawList.AddText(new Vector2(left + pad, (top + bottom - size.Y) / 2), TimelineUi.U32(colour), text);
        seamBadge = rect;
    }

    /// <summary>Whether a click at <paramref name="mouse"/> (screen pixels) is on the seam's badge.</summary>
    public bool OnSeamBadge(Vector2 mouse) =>
        seamBadge is { } badge && mouse.X >= badge.Min.X && mouse.X <= badge.Max.X && mouse.Y >= badge.Min.Y && mouse.Y <= badge.Max.Y;
}
