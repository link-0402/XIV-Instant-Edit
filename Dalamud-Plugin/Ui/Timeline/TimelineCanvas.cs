using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The lanes: one child with one invisible button over it. Input goes to the editor model (hits,
/// drags, keys); drawing uses the window draw list, clipped per region and culled to the frames in
/// view. Labels, prop groups and tick texts are cached per document.
/// </summary>
internal sealed class TimelineCanvas(TimelineSession session, BonePosing posing, TimelineClipBand clipBand, TimelinePickers pickers,
    SoundFiles soundFiles, TimelineTexts texts)
{
    private const string CanvasMenu = "##timeline-canvas-menu";

    private static readonly string[] LaneGlyphs = [.. TimelineUi.LaneIcons.Select(icon => icon.ToIconString())];
    // Offset, amplitude and pose layers.
    private static readonly string[] LayerGlyphs = [.. TimelineUi.LayerIcons.Select(icon => icon.ToIconString())];
    // A layer's bone rows, folded and unfolded.
    private static readonly string FoldedGlyph = FontAwesomeIcon.CaretRight.ToIconString();
    private static readonly string UnfoldedGlyph = FontAwesomeIcon.CaretDown.ToIconString();
    private static readonly string[] AddHereLabels =
        ["Add expression here", "Add lip sync here", "Add sound here", "Add effect here", "Add prop here"];

    private readonly record struct GroupSpan(int Start, int End, int FirstRow, int LastRow);

    private float scrollY;
    private bool rightPressed;
    private TimelineHit menuHit;
    private int menuFrame;
    private TimelineLayout? groupLayout;
    private readonly List<GroupSpan> groupSpans = [];
    private readonly Dictionary<long, string> tickLabels = [];

    /// <summary> Scrolls the lanes back to the top, for another clip. </summary>
    public void ResetScroll() => scrollY = 0;

    private static string LaneGlyph(TimelineLane lane) => LaneGlyphs[Math.Min((int)lane, LaneGlyphs.Length - 1)];

    private static string LayerGlyph(MotionLayerKind kind) => LayerGlyphs[Math.Min((int)kind, LayerGlyphs.Length - 1)];

    public void Draw(TimelineEditor current, Vector2 size)
    {
        var readOnly = session.ReadOnly;
        var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var background = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.PanelBg);
        using var child = ImRaii.Child("##timeline-canvas", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        background.Dispose();
        padding.Dispose();
        if (!child.Success)
            return;

        var io = ImGui.GetIO();
        var origin = ImGui.GetCursorScreenPos();
        var area = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        var row = ImGui.GetFrameHeight() + Theme.Scaled(4);
        var ruler = ImGui.GetFrameHeight() + Theme.Scaled(2);
        var legend = Math.Min(Theme.Scaled(118), area.X * .35f);
        current.SnapTolerance = Theme.Scaled(8);
        current.SetWidth(Math.Max(1, area.X - legend));
        // Only a clip the game loops has a seam to blend or fix.
        current.Loops = session.Source is { } loaded && session.Fresh(loaded).Clip.IsLoop && !current.Document.Expression;
        var clip = TimelineClipBand.Height(current.Document, row);
        ClampScroll(current, row, ruler + clip, area.Y);
        var geometry = new TimelineGeometry(legend, ruler, row, Theme.Scaled(5), Theme.Scaled(9), scrollY, Theme.Scaled(18), clip);

        ImGui.InvisibleButton("##canvas", area,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var activated = ImGui.IsItemActivated();
        var local = io.MousePos - origin;
        var hit = hovered || active ? current.HitTest(geometry, local) : TimelineHit.None();

        HandlePointer(current, hit, local.X - legend, hovered, active, activated, readOnly, row);
        // The input above may have changed the document, the view or the scroll.
        clip = TimelineClipBand.Height(current.Document, row);
        ClampScroll(current, row, ruler + clip, area.Y);
        geometry = geometry with { ScrollY = scrollY, Clip = clip };
        if (hovered && !active)
            hit = current.HitTest(geometry, local);

        DrawTimeline(current, origin, area, geometry, hit, hovered);
        DrawTooltip(current, hit, hovered && !active, readOnly);
        DrawCanvasMenu(current, readOnly);
    }

    private void ClampScroll(TimelineEditor current, float row, float ruler, float height) =>
        scrollY = Math.Clamp(scrollY, 0, Math.Max(0, current.Layout.RowCount * row - Math.Max(0, height - ruler)));

    /// <summary> Mouse input: presses, drags and releases to the editor; pan, zoom and the context menu here. </summary>
    private void HandlePointer(TimelineEditor current, TimelineHit hit, float trackX, bool hovered, bool active, bool activated,
        bool readOnly, float row)
    {
        var io = ImGui.GetIO();
        if (activated)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                PressLeft(current, hit, readOnly);
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                rightPressed = true;
        }

        if (current.Gesture != TimelineGesture.None)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                current.Cancel();
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                current.Release();
            else if (current.Gesture == TimelineGesture.Scrub || ImGui.IsMouseDragging(ImGuiMouseButton.Left, Theme.Scaled(3)))
                current.Drag(current.View.FrameAt(trackX), io.KeyAlt);
        }

        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            current.Pan(io.MouseDelta.X);
            scrollY -= io.MouseDelta.Y;
        }

        if (hovered)
        {
            var wheel = io.MouseWheel;
            if (wheel != 0)
            {
                if (io.KeyCtrl)
                    current.Zoom(trackX, MathF.Pow(1.2f, wheel));
                else if (io.KeyShift)
                    scrollY -= wheel * row;
                else
                    current.Pan(wheel * Theme.Scaled(60));
            }
            if (io.MouseWheelH != 0)
                current.Pan(-io.MouseWheelH * Theme.Scaled(60));
        }

        if (rightPressed && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            rightPressed = false;
            // A click, not a drag: the delta stays zero below the drag threshold.
            if (hovered && current.Gesture == TimelineGesture.None && hit.Part != TimelinePart.None &&
                ImGui.GetMouseDragDelta(ImGuiMouseButton.Right) == Vector2.Zero)
            {
                menuHit = hit;
                menuFrame = hit.Part == TimelinePart.Legend ? current.Playhead : current.ContextMenu(hit, io.KeyAlt);
                ImGui.OpenPopup(CanvasMenu);
            }
        }
        else if (rightPressed && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            rightPressed = false;
        }

        var resizing = current.Gesture is TimelineGesture.ResizeLeft or TimelineGesture.ResizeRight or TimelineGesture.Length or
            TimelineGesture.LayerLeft or TimelineGesture.LayerRight or TimelineGesture.LayerFadeIn or TimelineGesture.LayerFadeOut or TimelineGesture.LayerKey or
            TimelineGesture.BoneKey or TimelineGesture.TrimLeft or TimelineGesture.TrimRight or TimelineGesture.BlendLength or TimelineGesture.EaseLength;
        if (!readOnly && (resizing || hovered && current.Gesture == TimelineGesture.None &&
                hit.Part is TimelinePart.LeftEdge or TimelinePart.RightEdge or TimelinePart.LengthHandle or TimelinePart.LayerLeft or
                    TimelinePart.LayerRight or TimelinePart.LayerFadeIn or TimelinePart.LayerFadeOut or TimelinePart.LayerKey or TimelinePart.BoneKey or
                    TimelinePart.ClipLeft or TimelinePart.ClipRight or TimelinePart.JoinEdge or TimelinePart.SeamEdge or TimelinePart.EaseEdge))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
    }

    /// <summary>
    /// A left press: a double click on an empty lane opens its picker at that frame; anything else
    /// goes to the editor. While the document can't change, only selection and scrubbing work.
    /// </summary>
    private void PressLeft(TimelineEditor current, TimelineHit hit, bool readOnly)
    {
        var io = ImGui.GetIO();
        // The seam's badge at the clip band's end selects the seam, set or not.
        if (clipBand.OnSeamBadge(io.MousePos))
        {
            current.SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
            return;
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !readOnly &&
            hit.Part is TimelinePart.Lane or TimelinePart.Legend && hit.Lane is { } lane && TimelineWindowViews.AddKind(lane) is { } kind)
        {
            pickers.Request(kind, hit.Part == TimelinePart.Legend ? current.Playhead : current.SnappedFrame(hit.Frame, io.KeyAlt), null);
            return;
        }
        // A double click on the empty bone row adds an offset section there.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !readOnly && hit.Part == TimelinePart.MotionLane && hit.Layer == null)
        {
            AddLayerAt(current, MotionLayerKind.Offset, current.SnappedFrame(hit.Frame, io.KeyAlt));
            return;
        }
        // A bone's row picks that bone for the panel and the Bones window; Ctrl adds it.
        if (hit.Bone is { } bone && hit.Layer != null && hit.Part is TimelinePart.BoneKey or TimelinePart.BoneLane or TimelinePart.Legend)
            posing.Selection.Click(bone, io.KeyCtrl && hit.Part != TimelinePart.BoneKey);
        if (!readOnly)
        {
            current.Press(hit, io.KeyCtrl, io.KeyShift, io.KeyAlt);
            return;
        }
        if (hit.Part == TimelinePart.Ruler)
            current.Press(hit, alt: io.KeyAlt);
        else if (hit.Part == TimelinePart.LayerUnfold && hit.Layer is { } folding)
            current.SetUnfolded(folding, !current.IsUnfolded(folding));
        else if (hit.OnEvent)
            current.Select(hit.Key!, io.KeyCtrl, io.KeyShift);
        else if (hit.OnLayer)
            current.SelectLayer(hit.Layer!, hit.OnKey ? hit.KeyFrame : null, hit.Part == TimelinePart.BoneKey ? hit.Bone : null);
        else if (hit.Part is TimelinePart.BoneLane or TimelinePart.Legend && hit.Layer != null)
            current.SelectLayer(hit.Layer);
        else if (hit.Part is TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight && hit.Cut >= 0)
            current.SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, hit.Cut));
        else if (hit.Part is TimelinePart.Join or TimelinePart.JoinEdge && hit.Cut >= 0)
            current.SelectCut(new TimelineCutSelection(TimelineCutTarget.Join, hit.Cut));
        else if (hit.Part is TimelinePart.Seam or TimelinePart.SeamEdge)
            current.SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
        else if (!io.KeyCtrl && !io.KeyShift)
            current.ClearSelection();
    }

    /// <summary>
    /// Adds a bone layer section starting at <paramref name="frame"/>: a second long, or to the end of the
    /// clip, moved back when the clip ends sooner.
    /// </summary>
    private static void AddLayerAt(TimelineEditor current, MotionLayerKind kind, int frame)
    {
        var length = Math.Max(TimelineDocument.MinLength, current.Document.Length);
        var start = Math.Clamp(frame, 0, length - 1);
        var end = Math.Min(length, start + MotionEdits.SectionLength);
        if (end - start < MotionEdits.SectionLength)
            start = Math.Max(0, end - MotionEdits.SectionLength);
        current.AddLayer(kind, start, end);
    }

    private void RefreshGroups(TimelineDocument doc, TimelineLayout layout)
    {
        if (ReferenceEquals(groupLayout, layout))
            return;
        groupLayout = layout;
        groupSpans.Clear();
        var events = doc.Events.IsDefault ? [] : doc.Events;
        var spans = new Dictionary<string, (GroupSpan Span, int Count)>(StringComparer.Ordinal);
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index].Group is not { } group)
                continue;
            var rowIndex = layout.RowOf(index);
            var span = new GroupSpan(events[index].Start, layout.EndOf(index), rowIndex, rowIndex);
            spans[group] = spans.TryGetValue(group, out var known)
                ? (new GroupSpan(Math.Min(known.Span.Start, span.Start), Math.Max(known.Span.End, span.End),
                    Math.Min(known.Span.FirstRow, rowIndex), Math.Max(known.Span.LastRow, rowIndex)), known.Count + 1)
                : (span, 1);
        }
        foreach (var (span, count) in spans.Values)
            if (count > 1)
                groupSpans.Add(span);
    }

    private string TickLabel(int frame, int major)
    {
        var key = ((long)frame << 16) | (uint)(major & 0xFFFF);
        if (tickLabels.TryGetValue(key, out var text))
            return text;
        if (tickLabels.Count > 2048)
            tickLabels.Clear();
        return tickLabels[key] = TimelineText.Tick(frame, major);
    }

    private void DrawTimeline(TimelineEditor current, Vector2 origin, Vector2 area, TimelineGeometry geometry, TimelineHit hit, bool hovered)
    {
        var drawList = ImGui.GetWindowDrawList();
        var doc = current.Document;
        var layout = current.Layout;
        var view = current.View;
        texts.Refresh(doc);
        RefreshGroups(doc, layout);
        var min = origin;
        var max = origin + area;
        var trackLeft = min.X + geometry.Legend;
        var rowsTop = min.Y + geometry.Header;
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        var fontSize = ImGui.GetFontSize();
        var major = view.MajorStep(Theme.Scaled(64));
        var minor = view.MinorStep(major, Theme.Scaled(8));
        var handleHot = current.Gesture == TimelineGesture.Length || hovered && hit.Part == TimelinePart.LengthHandle;

        // Lanes: alternating bands over the legend and the track, each named once with its glyph.
        drawList.PushClipRect(new Vector2(min.X, rowsTop), max, true);
        for (var index = 0; index < layout.Lanes.Length; index++)
        {
            var lane = layout.Lanes[index];
            var top = min.Y + geometry.RowTop(lane.FirstRow);
            var bottom = min.Y + geometry.RowTop(lane.EndRow);
            if (bottom < rowsTop || top > max.Y)
                continue;
            var colour = TimelineWindowViews.LaneColour(lane.Lane);
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), TimelineUi.U32(index % 2 == 0 ? Theme.PanelBg : Theme.RowAlt));
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), TimelineUi.U32(colour));
            var textY = top + (geometry.Row - fontSize) / 2;
            drawList.PushClipRect(new Vector2(min.X, top), new Vector2(trackLeft - Theme.Scaled(3), bottom), true);
            drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(min.X + Theme.Scaled(9), textY), TimelineUi.U32(colour), LaneGlyph(lane.Lane));
            drawList.AddText(new Vector2(min.X + Theme.Scaled(30), textY), TimelineUi.U32(Theme.Label), TimelineWindowViews.LaneName(lane.Lane));
            drawList.PopClipRect();
        }
        DrawLayerLegend(drawList, current, doc, layout, geometry, min, max, trackLeft, rowsTop, hovered ? hit : TimelineHit.None());
        drawList.PopClipRect();

        // The track: grid, the shade past the end, prop groups, events, then the lines on top.
        drawList.PushClipRect(new Vector2(trackLeft, rowsTop), max, true);
        var grid = TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .28f));
        for (var frame = view.FirstTick(major); frame <= view.Last; frame += major)
        {
            var x = TimelineUi.Crisp(trackLeft + view.X(frame));
            drawList.AddLine(new Vector2(x, rowsTop), new Vector2(x, max.Y), grid, line);
        }
        var endX = trackLeft + view.X(doc.Length);
        if (endX < max.X)
            drawList.AddRectFilled(new Vector2(Math.Max(endX, trackLeft), rowsTop), max, TimelineUi.U32(Theme.PastEnd));
        DrawGroups(drawList, geometry, view, min, max, trackLeft);
        DrawEvents(drawList, current, doc, layout, geometry, view, min, max, trackLeft, rowsTop, hovered && hit.OnEvent ? hit.Key : null);
        TimelinePainter.DrawLayers(drawList, current, doc, layout, geometry, view, min, max, trackLeft, rowsTop, hovered ? hit : TimelineHit.None());
        if (doc.LengthChanged)
        {
            var ghost = TimelineUi.Crisp(trackLeft + view.X(doc.SourceLength));
            drawList.AddLine(new Vector2(ghost, rowsTop), new Vector2(ghost, max.Y), TimelineUi.U32(Theme.WithAlpha(Theme.Muted, .45f)), line);
        }
        drawList.AddLine(new Vector2(TimelineUi.Crisp(endX), rowsTop), new Vector2(TimelineUi.Crisp(endX), max.Y),
            TimelineUi.U32(handleHot ? Theme.Accent : Theme.WithAlpha(Theme.Muted, .7f)), handleHot ? Theme.Scaled(2) : line);
        drawList.PopClipRect();

        DrawRuler(drawList, current, doc, geometry, view, min, max, trackLeft, min.Y + geometry.Ruler, major, minor, handleHot);
        clipBand.Draw(drawList, current, geometry, min, max, trackLeft, hovered ? hit : TimelineHit.None());

        // Lines that cross the ruler and the lanes.
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), max, true);
        if (current.SnapGuide is { } guide)
        {
            var x = TimelineUi.Crisp(trackLeft + view.X(guide));
            drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), TimelineUi.U32(Theme.SnapGuide), line);
        }
        var playheadX = TimelineUi.Crisp(trackLeft + view.X(current.Playhead));
        if (playheadX >= trackLeft - 1 && playheadX <= max.X + 1)
        {
            var playhead = TimelineUi.U32(Theme.Playhead);
            var size = Theme.Scaled(6);
            drawList.AddLine(new Vector2(playheadX, min.Y), new Vector2(playheadX, max.Y), playhead, line);
            drawList.AddTriangleFilled(new Vector2(playheadX - size, min.Y), new Vector2(playheadX + size, min.Y),
                new Vector2(playheadX, min.Y + size * 1.4f), playhead);
        }
        drawList.PopClipRect();

        var border = TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .7f));
        drawList.AddLine(new Vector2(min.X, TimelineUi.Crisp(rowsTop)), new Vector2(max.X, TimelineUi.Crisp(rowsTop)), border, line);
        drawList.AddLine(new Vector2(TimelineUi.Crisp(trackLeft), min.Y), new Vector2(TimelineUi.Crisp(trackLeft), max.Y), border, line);
    }

    private void DrawRuler(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineGeometry geometry, TimelineView view,
        Vector2 min, Vector2 max, float trackLeft, float rowsTop, int major, int minor, bool handleHot)
    {
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), new Vector2(max.X, rowsTop), true);
        drawList.AddRectFilled(new Vector2(trackLeft, min.Y), new Vector2(max.X, rowsTop), TimelineUi.U32(Theme.RowAlt));
        var tick = TimelineUi.U32(Theme.Muted);
        var faint = TimelineUi.U32(Theme.WithAlpha(Theme.Muted, .4f));
        var text = TimelineUi.U32(Theme.Hint);
        for (var frame = view.FirstTick(minor); frame <= view.Last; frame += minor)
        {
            var x = TimelineUi.Crisp(trackLeft + view.X(frame));
            var isMajor = frame % major == 0;
            drawList.AddLine(new Vector2(x, min.Y + geometry.Ruler * (isMajor ? .45f : .72f)), new Vector2(x, rowsTop), isMajor ? tick : faint, line);
            if (isMajor)
                drawList.AddText(new Vector2(x + Theme.Scaled(3), min.Y + Theme.Scaled(1)), text, TickLabel(frame, major));
        }
        var endX = trackLeft + view.X(doc.Length);
        if (endX < max.X)
            drawList.AddRectFilled(new Vector2(Math.Max(endX, trackLeft), min.Y), new Vector2(max.X, rowsTop), TimelineUi.U32(Theme.PastEnd));
        if (doc.LengthChanged)
        {
            var ghost = TimelineUi.Crisp(trackLeft + view.X(doc.SourceLength));
            drawList.AddLine(new Vector2(ghost, min.Y), new Vector2(ghost, rowsTop), TimelineUi.U32(Theme.WithAlpha(Theme.Muted, .6f)), line);
        }
        // The length handle: a grip at the end, dragged to change the length.
        var grip = Theme.Scaled(3);
        drawList.AddRectFilled(new Vector2(endX - grip, min.Y + geometry.Ruler * .15f), new Vector2(endX + grip, rowsTop),
            TimelineUi.U32(handleHot ? Theme.Accent : Theme.Muted), Theme.Scaled(2));
        drawList.PopClipRect();
    }

    private void DrawGroups(ImDrawListPtr drawList, TimelineGeometry geometry, TimelineView view, Vector2 min, Vector2 max, float trackLeft)
    {
        if (groupSpans.Count == 0)
            return;
        var fill = TimelineUi.U32(Theme.WithAlpha(Theme.TrackProp, .07f));
        var outline = TimelineUi.U32(Theme.WithAlpha(Theme.TrackProp, .4f));
        var rounding = Theme.Scaled(4);
        var margin = Theme.Scaled(3);
        foreach (var span in groupSpans)
        {
            var x0 = trackLeft + view.X(span.Start) - margin;
            var x1 = trackLeft + view.X(Math.Max(span.End, span.Start + 1)) + margin;
            var y0 = min.Y + geometry.RowTop(span.FirstRow) + 1;
            var y1 = min.Y + geometry.RowTop(span.LastRow + 1) - 1;
            if (x1 < trackLeft || x0 > max.X || y1 < min.Y || y0 > max.Y)
                continue;
            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), fill, rounding);
            drawList.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), outline, rounding);
        }
    }

    private void DrawEvents(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineLayout layout, TimelineGeometry geometry,
        TimelineView view, Vector2 min, Vector2 max, float trackLeft, float rowsTop, string? hotKey)
    {
        var events = doc.Events.IsDefault ? [] : doc.Events;
        // Markers are drawn wider than their frame; keep the ones whose glyph still reaches the view.
        var margin = geometry.Marker / view.Scale;
        var first = view.First - margin;
        var last = view.Last + margin;
        var pad = Theme.Scaled(2);
        for (var index = 0; index < events.Length && index < texts.Labels.Length; index++)
        {
            var ev = layout.Shown(index);
            var top = min.Y + geometry.RowTop(layout.RowOf(index));
            if (top + geometry.Row < rowsTop || top > max.Y)
                continue;
            var end = layout.EndOf(index);
            if (end < first || ev.Start > last)
                continue;
            TimelinePainter.DrawEvent(drawList, current, ev, texts.Labels[index], end, geometry, view, trackLeft, top + pad, top + geometry.Row - pad,
                hotKey != null && string.Equals(ev.Key, hotKey, StringComparison.Ordinal), SoundMarkOf(doc, ev));
        }
    }

    private TimelinePainter.SoundMark? SoundMarkOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ev.Magic != "C063" || TimelineSoundKey.Of(ev) is not { } key)
            return null;
        var pinned = ev.Pin != null;
        var changed = doc.SoundEdits.ContainsKey(key);
        if (soundFiles.ShownIfRead(doc, key) is not { Seconds.IsEmpty: false } shown)
            return pinned || changed ? new TimelinePainter.SoundMark(int.MaxValue, pinned, changed, false) : null;
        var span = TimelineWindowViews.SoundSpan(shown.Seconds, ev.Pin is { } pin && shown.Ids.IndexOf(pin) is >= 0 and var index ? index : null);
        var loop = ev.Values?.GetValueOrDefault("Loop", 1) ?? 1;
        var crosses = doc.Cut && TimelineSoundCuts.Crosses(doc, ev, shown.Seconds.Max());
        return new TimelinePainter.SoundMark(ev.Start + span.Solid, pinned, changed, shown.EndsEarly || loop == -1 && !shown.Loops || crosses);
    }

    /// <summary>
    /// The bone layer rows below the event lanes: each layer's kind and name over a band in its colour,
    /// with an arrow at the right that unfolds a row per bone; each bone row its bone's name, indented;
    /// or "Bones" over the empty row a first layer goes into. A line sets them apart from the events.
    /// </summary>
    private void DrawLayerLegend(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineLayout layout,
        TimelineGeometry geometry, Vector2 min, Vector2 max, float trackLeft, float rowsTop, TimelineHit hit)
    {
        if (layout.MotionRows == 0)
            return;
        var fontSize = ImGui.GetFontSize();
        var layerBand = 0;
        for (var row = layout.MotionFirstRow; row < layout.MotionFirstRow + layout.MotionRows; row++)
        {
            var top = min.Y + geometry.RowTop(row);
            var bottom = top + geometry.Row;
            var entry = layout.MotionRowAt(row)!.Value;
            if (entry.Bone == null)
                layerBand++;
            if (bottom < rowsTop || top > max.Y)
                continue;
            var layer = entry.Layer >= 0 ? doc.Motion[entry.Layer] : null;
            var colour = layer == null ? Theme.Muted : TimelineWindowViews.LayerColour(layer.Kind);
            var band = TimelineUi.U32(layerBand % 2 == 1 ? Theme.RowAlt : Theme.PanelBg);
            var textY = top + (geometry.Row - fontSize) / 2;
            if (entry.Bone is { } bone && layer != null)
            {
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), band);
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), TimelineUi.U32(Theme.WithAlpha(colour, .05f)));
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), TimelineUi.U32(Theme.WithAlpha(colour, .45f)));
                var picked = current.SelectedLayer == layer.Key && posing.Selection.Contains(bone);
                var hot = hit.Part == TimelinePart.Legend && hit.Row == row;
                drawList.PushClipRect(new Vector2(min.X, top), new Vector2(trackLeft - Theme.Scaled(3), bottom), true);
                drawList.AddText(new Vector2(min.X + Theme.Scaled(22), textY),
                    TimelineUi.U32(picked ? Theme.Accent : hot ? Theme.Text : !layer.Enabled ? Theme.Muted : Theme.Label), AnimationPresentation.BoneName(bone));
                drawList.PopClipRect();
                continue;
            }
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), band);
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), TimelineUi.U32(colour));
            var unfolds = layer != null && TimelineLayout.Unfolds(layer);
            var nameRight = trackLeft - Theme.Scaled(3) - (unfolds ? geometry.Unfold : 0);
            drawList.PushClipRect(new Vector2(min.X, top), new Vector2(nameRight, bottom), true);
            drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(min.X + Theme.Scaled(9), textY), TimelineUi.U32(Theme.WithAlpha(colour, layer?.Enabled == false ? .5f : 1)),
                LayerGlyph(layer?.Kind ?? MotionLayerKind.Offset));
            var name = layer == null ? Theme.Hint : !layer.Enabled ? Theme.Muted : current.SelectedLayer == layer.Key ? Theme.Accent : Theme.Label;
            drawList.AddText(new Vector2(min.X + Theme.Scaled(30), textY), TimelineUi.U32(name), layer?.Name ?? TimelineWindowViews.BonesLaneName);
            drawList.PopClipRect();
            if (unfolds)
            {
                var hot = hit.Part == TimelinePart.LayerUnfold && hit.Row == row;
                var glyph = current.IsUnfolded(layer!.Key) ? UnfoldedGlyph : FoldedGlyph;
                drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(trackLeft - geometry.Unfold + Theme.Scaled(3), textY),
                    TimelineUi.U32(hot ? Theme.Accent : Theme.Muted), glyph);
            }
        }
        var y = TimelineUi.Crisp(min.Y + geometry.RowTop(layout.MotionFirstRow));
        if (y > rowsTop && y < max.Y)
            drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .8f)), Math.Max(1, MathF.Floor(Theme.Scale)));
    }

    private void DrawTooltip(TimelineEditor current, TimelineHit hit, bool hovering, bool readOnly)
    {
        if (!hovering || current.Gesture != TimelineGesture.None)
            return;
        if (hit.OnLayer && MotionEdits.Find(current.Document, hit.Layer) is { } layer)
        {
            if (!Widgets.HoverDelay($"layer:{layer.Key}:{hit.Part}:{hit.KeyFrame}:{hit.Bone}"))
                return;
            DrawLayerTooltip(layer, hit, readOnly);
            return;
        }
        if (hit.Part == TimelinePart.LayerUnfold && hit.Layer is { } folding && Widgets.HoverDelay("layer-unfold:" + folding))
        {
            ImGui.SetTooltip(current.IsUnfolded(folding) ? "Hide the bone rows." : "Show a row per bone, to move one bone's keys on their own.");
            return;
        }
        if (hit.Part is TimelinePart.BoneLane or TimelinePart.Legend && hit.Bone is { } boneRow &&
            MotionEdits.Find(current.Document, hit.Layer) is { } owner && Widgets.HoverDelay($"bone-row:{owner.Key}:{boneRow}"))
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.TextColored(Theme.Label, AnimationPresentation.BoneName(boneRow));
            ImGui.TextUnformatted(TimelineWindowViews.BoneKeysText(owner, boneRow));
            if (!readOnly)
                ImGui.TextColored(Theme.Hint, "Click to pick this bone. Ctrl+click adds it.");
            return;
        }
        if (TimelineClipBand.DrawTooltip(hit, readOnly))
            return;
        if (hit.Part == TimelinePart.MotionLane && hit.Layer == null && !readOnly && Widgets.HoverDelay("timeline-bones-lane"))
        {
            ImGui.SetTooltip("Double-click to add a bone layer here.");
            return;
        }
        if (hit.OnEvent && TimelineEdits.Find(current.Document, hit.Key) is { } ev)
        {
            // Event keys are unique in the document, so they serve as the hover id.
            if (!Widgets.HoverDelay(ev.Key))
                return;
            using var tooltip = ImRaii.Tooltip();
            ImGui.TextColored(Theme.Label, TimelineWindowViews.EventLabel(ev));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, TimelineWindowViews.KindName(ev.Magic));
            ImGui.TextUnformatted("Starts at " + TimelineWindowViews.FrameText(ev.Start));
            if (TimelineDocument.Holds(ev) && TimelineDocument.Moves(ev, current.MotionFrames) == null)
                ImGui.TextUnformatted(TimelineWindowViews.HoldText(current.Document, ev));
            else if (TimelineDocument.Kept(ev))
                ImGui.TextUnformatted(TimelineDocument.Loops(ev) ? TimelineWindowViews.LoopText(ev) : TimelineWindowViews.KeptText);
            else if (current.Document.PlayedFrames(ev, current.MotionFrames) is var played and > 0)
                ImGui.TextUnformatted("Lasts " + TimelineWindowViews.DurationText(played));
            if (ev.Path.Length > 0)
                ImGui.TextColored(Theme.Hint, ev.Path);
            if (TimelinePainter.FromPack(current, ev))
                ImGui.TextUnformatted($"From the {current.Document.FacePack} expressions.");
            if (ev.LockReason is { Length: > 0 } reason)
                ImGui.TextColored(Theme.Warning, (ev.Access == TimelineAccess.Locked ? "Locked: " : "Move only: ") + reason);
            if (ev.Conditional)
                ImGui.TextColored(Theme.Info, "Plays only under a condition.");
            if (ev.Look != null)
                ImGui.TextColored(Theme.Accent, "Look changed.");
            if (soundFiles.CrossesCut(current.Document, ev, false))
                ImGui.TextColored(Theme.Warning, TimelineWindowViews.CrossesCutTip);
            return;
        }
        if (hit.Part == TimelinePart.LengthHandle && Widgets.HoverDelay("timeline-length-handle"))
            ImGui.SetTooltip(readOnly || current.FixedLength ? "The end of the clip." : "Drag to change the length.");
    }

    /// <summary> A bone layer's or key's tooltip: what it is, where it lies and what dragging the part under the pointer does. </summary>
    private static void DrawLayerTooltip(MotionLayer layer, TimelineHit hit, bool readOnly)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, layer.Name);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, MotionEdits.KindName(layer.Kind) + (layer.Enabled ? "" : " · off"));
        if (hit.Part == TimelinePart.BoneKey && hit.Bone is { } bone && MotionEdits.KeyIndex(layer, hit.KeyFrame) is var own and >= 0)
        {
            ImGui.TextUnformatted($"{AnimationPresentation.BoneName(bone)} at {TimelineWindowViews.FrameText(hit.KeyFrame)}");
            ImGui.TextUnformatted(TimelineWindowViews.KeyEaseText(layer, layer.Keys[own]));
            if (!readOnly)
                ImGui.TextColored(Theme.Hint, "Drag to move this bone's key alone. It joins a key it lands on.");
            return;
        }
        if (hit.Part == TimelinePart.LayerKey && MotionEdits.KeyIndex(layer, hit.KeyFrame) is var at and >= 0)
        {
            var bones = layer.Keys[at].Bones.Count;
            ImGui.TextUnformatted($"Key at {TimelineWindowViews.FrameText(hit.KeyFrame)} · {(bones == 1 ? "1 bone" : $"{bones} bones")}");
            ImGui.TextUnformatted(TimelineWindowViews.KeyEaseText(layer, layer.Keys[at]));
            if (!readOnly)
                ImGui.TextColored(Theme.Hint, "Drag to move the key.");
            return;
        }
        ImGui.TextUnformatted($"From {TimelineWindowViews.FrameText(layer.Start)} to {TimelineWindowViews.FrameText(layer.End)}");
        if (layer.FadeIn > 0 || layer.FadeOut > 0)
            ImGui.TextUnformatted($"Fades in {TimelineText.Seconds(layer.FadeIn)}, out {TimelineText.Seconds(layer.FadeOut)}");
        ImGui.TextUnformatted(TimelineWindowViews.LayerContent(layer));
        if (readOnly)
            return;
        ImGui.TextColored(Theme.Hint, hit.Part switch
        {
            TimelinePart.LayerLeft => "Drag to change where it starts.",
            TimelinePart.LayerRight => "Drag to change where it ends.",
            TimelinePart.LayerFadeIn => "Drag to change how long it fades in.",
            TimelinePart.LayerFadeOut => "Drag to change how long it fades out.",
            _ => "Drag to move it with its keys.",
        });
    }

    /// <summary> The right-click menu: actions on the event under the pointer, or adds and the length at that frame. </summary>
    private void DrawCanvasMenu(TimelineEditor current, bool readOnly)
    {
        using var popup = ImRaii.Popup(CanvasMenu);
        if (!popup.Success)
            return;
        if (menuHit.OnLayer && MotionEdits.Find(current.Document, menuHit.Layer) is { } layer)
        {
            DrawLayerMenu(current, layer, readOnly);
            return;
        }
        if (menuHit.Part is TimelinePart.ClipRow or TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight or
            TimelinePart.Join or TimelinePart.JoinEdge or TimelinePart.Seam or TimelinePart.SeamEdge)
        {
            clipBand.DrawMenu(current, readOnly, menuFrame);
            return;
        }
        if (menuHit.Part is TimelinePart.BoneLane or TimelinePart.Legend && menuHit.Bone is { } bone &&
            MotionEdits.Find(current.Document, menuHit.Layer) is { } owner)
        {
            ImGui.TextColored(Theme.Label, AnimationPresentation.BoneName(bone));
            ImGui.Separator();
            if (ImGui.MenuItem("Take out of this layer", "", false, !readOnly))
                current.Commit(MotionEdits.ClearBone(current.Document, owner.Key, bone));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delete this bone's keys in this layer.");
            if (ImGui.MenuItem("Hide bone rows"))
                current.SetUnfolded(owner.Key, false);
            return;
        }
        if (menuHit.Part == TimelinePart.MotionLane || menuHit.Part == TimelinePart.Legend && current.Layout.IsMotionRow(menuHit.Row))
        {
            foreach (var kind in TimelineWindowViews.LayerKinds)
            {
                if (ImGui.MenuItem($"Add {MotionEdits.KindName(kind).ToLowerInvariant()} layer here", "", false, !readOnly))
                    AddLayerAt(current, kind, menuFrame);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(TimelineWindowViews.LayerKindHint(kind));
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Fit", "F"))
                current.Fit();
            return;
        }
        if (menuHit.OnEvent && TimelineEdits.Find(current.Document, menuHit.Key) is { } ev)
        {
            ImGui.TextColored(Theme.Label, TimelineWindowViews.EventLabel(ev));
            ImGui.Separator();
            var (editable, movable) = SelectionAccess(current);
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, !readOnly && editable))
                current.DuplicateSelection();
            if (ImGui.MenuItem("Delete", "Del", false, !readOnly && movable))
                current.DeleteSelection();
            if (ImGui.MenuItem("Move to playhead", "", false, !readOnly && TimelineEdits.CanMove(ev)))
                current.MoveSelectionTo(current.Playhead);
            if (ImGui.MenuItem("Copy", "Ctrl+C", false, editable))
                current.Copy();
            return;
        }
        for (var index = 0; index < TimelineWindowViews.AddKinds.Length; index++)
            if (ImGui.MenuItem(AddHereLabels[index], "", false, !readOnly))
                pickers.Request(TimelineWindowViews.AddKinds[index], menuFrame, null);
        ImGui.Separator();
        if (ImGui.MenuItem("Paste here", "Ctrl+V", false, !readOnly && current.CanPaste))
        {
            current.Playhead = menuFrame;
            current.Paste();
        }
        if (ImGui.MenuItem("Set length here", "", false, !readOnly && !current.FixedLength && menuFrame >= TimelineDocument.MinLength))
            current.SetLength(menuFrame);
        if (ImGui.MenuItem("Fit", "F"))
            current.Fit();
    }

    /// <summary> A bone layer's menu: its key under the pointer, turning it on or off, copying, ordering and deleting it. </summary>
    private static void DrawLayerMenu(TimelineEditor current, MotionLayer layer, bool readOnly)
    {
        ImGui.TextColored(Theme.Label, layer.Name);
        ImGui.Separator();
        var doc = current.Document;
        var index = MotionEdits.IndexOf(doc, layer.Key);
        if (current.SelectedKey is { } frame && MotionEdits.KeyIndex(layer, frame) is var at and >= 0)
        {
            if (ImGui.MenuItem(current.SelectedBone is { } bone ? $"Delete {AnimationPresentation.BoneName(bone)}'s key" : "Delete key", "Del", false, !readOnly))
                current.DeleteLayerSelection();
            DrawEaseMenu(current, layer, layer.Keys[at], readOnly);
            ImGui.Separator();
        }
        if (TimelineLayout.Unfolds(layer) && ImGui.MenuItem(current.IsUnfolded(layer.Key) ? "Hide bone rows" : "Show bone rows"))
            current.SetUnfolded(layer.Key, !current.IsUnfolded(layer.Key));
        if (ImGui.MenuItem(layer.Enabled ? "Turn off" : "Turn on", "", false, !readOnly))
            current.Commit(MotionEdits.SetEnabled(doc, layer.Key, !layer.Enabled));
        if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, !readOnly))
            current.DuplicateLayer();
        if (ImGui.MenuItem("Apply earlier", "", false, !readOnly && index > 0))
            current.Commit(MotionEdits.Reorder(doc, layer.Key, -1));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Move it up: layers apply from the top down.");
        if (ImGui.MenuItem("Apply later", "", false, !readOnly && index < doc.Motion.Length - 1))
            current.Commit(MotionEdits.Reorder(doc, layer.Key, 1));
        if (ImGui.MenuItem("Delete layer", "", false, !readOnly))
            current.Commit(MotionEdits.Remove(doc, layer.Key));
    }

    /// <summary> The key's ease to the next key: the layer's way or one of the presets. Its own curve is made in the panel. </summary>
    private static void DrawEaseMenu(TimelineEditor current, MotionLayer layer, MotionKey key, bool readOnly)
    {
        using var menu = ImRaii.Menu("To the next key", !readOnly);
        if (!menu.Success)
            return;
        if (ImGui.MenuItem($"The layer's ({TimelineWindowViews.EaseName(layer.Interpolation)})", "", key.Ease == null))
            current.Commit(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, null));
        foreach (var mode in TimelineWindowViews.LayerEases)
        {
            if (ImGui.MenuItem(TimelineWindowViews.EaseName(mode), "", key.Ease == mode))
                current.Commit(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, mode));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TimelineWindowViews.EaseHint(mode));
        }
    }

    /// <summary> Whether the selection holds an event that can be duplicated, and one that can be moved or deleted. </summary>
    public static (bool Editable, bool Movable) SelectionAccess(TimelineEditor current)
    {
        bool editable = false, movable = false;
        var events = current.Document.Events.IsDefault ? [] : current.Document.Events;
        foreach (var ev in events)
        {
            if (!current.IsSelected(ev.Key))
                continue;
            editable |= TimelineEdits.CanEdit(ev);
            movable |= TimelineEdits.CanMove(ev);
        }
        return (editable, movable);
    }

    /// <summary>
    /// Shortcuts, only while this window is focused, no text is being typed, no control is active
    /// and no menu is open. Space is left to the game (it jumps), so it never plays.
    /// </summary>
    public void HandleKeys(TimelineEditor current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var io = ImGui.GetIO();
        if (io.WantTextInput)
            return;
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            return;
        var readOnly = session.ReadOnly;
        var ctrl = io.KeyCtrl;
        var shift = io.KeyShift;
        var alt = io.KeyAlt;
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (!readOnly)
                current.Key(shift ? TimelineKey.Redo : TimelineKey.Undo);
        }
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y))
        {
            if (!readOnly)
                current.Key(TimelineKey.Redo);
        }
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D))
        {
            if (!readOnly)
                current.Key(TimelineKey.Duplicate);
        }
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C))
            current.Key(TimelineKey.Copy);
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V))
        {
            if (!readOnly)
                current.Key(TimelineKey.Paste);
        }
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.A))
            current.Key(TimelineKey.SelectAll);
        else if (!ctrl && (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace)))
        {
            if (!readOnly)
                current.Key(TimelineKey.Delete);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
        {
            if (!alt || !readOnly)
                current.Key(TimelineKey.Left, shift, alt);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
        {
            if (!alt || !readOnly)
                current.Key(TimelineKey.Right, shift, alt);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Home))
            current.Key(TimelineKey.Home);
        else if (ImGui.IsKeyPressed(ImGuiKey.End))
            current.Key(TimelineKey.End);
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.F))
            current.Key(TimelineKey.Fit);
        else if (!ctrl && !alt && ImGui.IsKeyPressed(ImGuiKey.C))
        {
            if (!readOnly)
                current.Key(TimelineKey.Cut);
        }
        else if (!ctrl && !alt && ImGui.IsKeyPressed(ImGuiKey.Q))
        {
            if (!readOnly)
                current.Key(TimelineKey.RemoveBefore);
        }
        else if (!ctrl && !alt && ImGui.IsKeyPressed(ImGuiKey.W))
        {
            if (!readOnly)
                current.Key(TimelineKey.RemoveAfter);
        }
    }
}
