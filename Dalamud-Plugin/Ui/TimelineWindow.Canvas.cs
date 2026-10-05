using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The lanes: one child with one invisible button over it. Input goes to the editor model (hits,
/// drags, keys); drawing uses the window draw list, clipped per region and culled to the frames in
/// view. Labels, prop groups and tick texts are cached per document.
/// </summary>
public sealed partial class TimelineWindow
{
    private const string CanvasMenu = "##timeline-canvas-menu";

    private static readonly FontAwesomeIcon[] LaneIcons =
    [
        FontAwesomeIcon.Running, FontAwesomeIcon.Smile, FontAwesomeIcon.CommentDots, FontAwesomeIcon.VolumeUp,
        FontAwesomeIcon.Magic, FontAwesomeIcon.Toolbox, FontAwesomeIcon.EllipsisH,
    ];
    private static readonly string[] LaneGlyphs = [.. LaneIcons.Select(icon => icon.ToIconString())];
    private static readonly string LockGlyph = FontAwesomeIcon.Lock.ToIconString();
    private static readonly string[] AddHereLabels =
        ["Add expression here", "Add lip sync here", "Add sound here", "Add effect here", "Add prop here"];

    private readonly record struct GroupSpan(int Start, int End, int FirstRow, int LastRow);

    private float scrollY;
    private bool rightPressed;
    private TimelineHit menuHit;
    private int menuFrame;
    private TimelineDocument? labelDocument;
    private string[] labels = [];
    private string lengthText = string.Empty;
    private string? headerNote;
    private string? pastEndWarning;
    private ImmutableArray<TimelineEffectCheck.Problem> effectProblems = [];
    private string? effectWarning;
    // The copies keeping them going swaps in, and how many events it changes.
    private ImmutableArray<TimelineImport> effectCopies = [];
    private int effectEvents;
    private TimelineLayout? groupLayout;
    private readonly List<GroupSpan> groupSpans = [];
    private readonly Dictionary<long, string> tickLabels = [];

    private static FontAwesomeIcon LaneIcon(TimelineLane lane) => LaneIcons[Math.Min((int)lane, LaneIcons.Length - 1)];

    private static string LaneGlyph(TimelineLane lane) => LaneGlyphs[Math.Min((int)lane, LaneGlyphs.Length - 1)];

    private static uint U32(Vector4 colour) => ImGui.GetColorU32(colour);

    /// <summary> A pixel-snapped x for crisp one-pixel lines. </summary>
    private static float Crisp(float x) => MathF.Floor(x) + .5f;

    private void DrawCanvas(TimelineEditor current, Vector2 size)
    {
        var readOnly = ReadOnly;
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
        ClampScroll(current, row, ruler, area.Y);
        var geometry = new TimelineGeometry(legend, ruler, row, Theme.Scaled(5), Theme.Scaled(9), scrollY);

        ImGui.InvisibleButton("##canvas", area,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var activated = ImGui.IsItemActivated();
        var local = io.MousePos - origin;
        var hit = hovered || active ? current.HitTest(geometry, local) : TimelineHit.None();

        HandlePointer(current, hit, local.X - legend, hovered, active, activated, readOnly, row);
        // The input above may have changed the document, the view or the scroll.
        ClampScroll(current, row, ruler, area.Y);
        geometry = geometry with { ScrollY = scrollY };
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

        var resizing = current.Gesture is TimelineGesture.ResizeLeft or TimelineGesture.ResizeRight or TimelineGesture.Length;
        if (!readOnly && (resizing || hovered && current.Gesture == TimelineGesture.None &&
                hit.Part is TimelinePart.LeftEdge or TimelinePart.RightEdge or TimelinePart.LengthHandle))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
    }

    /// <summary>
    /// A left press: a double click on an empty lane opens its picker at that frame; anything else
    /// goes to the editor. While the document can't change, only selection and scrubbing work.
    /// </summary>
    private void PressLeft(TimelineEditor current, TimelineHit hit, bool readOnly)
    {
        var io = ImGui.GetIO();
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !readOnly &&
            hit.Part is TimelinePart.Lane or TimelinePart.Legend && hit.Lane is { } lane && TimelineWindowViews.AddKind(lane) is { } kind)
        {
            RequestPicker(kind, hit.Part == TimelinePart.Legend ? current.Playhead : current.SnappedFrame(hit.Frame, io.KeyAlt), null);
            return;
        }
        if (!readOnly)
        {
            current.Press(hit, io.KeyCtrl, io.KeyShift, io.KeyAlt);
            return;
        }
        if (hit.Part == TimelinePart.Ruler)
            current.Press(hit, alt: io.KeyAlt);
        else if (hit.OnEvent)
            current.Select(hit.Key!, io.KeyCtrl, io.KeyShift);
        else if (!io.KeyCtrl && !io.KeyShift)
            current.ClearSelection();
    }

    /// <summary> Labels, the past-end warning, the length texts and prop groups, rebuilt only when the document or layout changes. </summary>
    private void RefreshDocumentTexts(TimelineDocument doc)
    {
        if (ReferenceEquals(labelDocument, doc))
            return;
        var events = doc.Events.IsDefault ? [] : doc.Events;
        labels = new string[events.Length];
        for (var index = 0; index < events.Length; index++)
            labels[index] = TimelineWindowViews.EventLabel(events[index]);
        lengthText = TimelineWindowViews.LengthText(doc);
        headerNote = TimelineWindowViews.HeaderNote(doc);
        pastEndWarning = TimelineWindowViews.PastEndWarning(doc);
        effectProblems = source?.EffectProblems(doc) ?? [];
        effectWarning = TimelineWindowViews.EffectLoopWarning(effectProblems);
        effectCopies = TimelineEffectCheck.Copies(effectProblems);
        effectEvents = effectProblems.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count();
        labelDocument = doc;
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
        RefreshDocumentTexts(doc);
        RefreshGroups(doc, layout);
        var min = origin;
        var max = origin + area;
        var trackLeft = min.X + geometry.Legend;
        var rowsTop = min.Y + geometry.Ruler;
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
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), U32(index % 2 == 0 ? Theme.PanelBg : Theme.RowAlt));
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), U32(colour));
            var textY = top + (geometry.Row - fontSize) / 2;
            drawList.PushClipRect(new Vector2(min.X, top), new Vector2(trackLeft - Theme.Scaled(3), bottom), true);
            drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(min.X + Theme.Scaled(9), textY), U32(colour), LaneGlyph(lane.Lane));
            drawList.AddText(new Vector2(min.X + Theme.Scaled(30), textY), U32(Theme.Label), TimelineWindowViews.LaneName(lane.Lane));
            drawList.PopClipRect();
        }
        drawList.PopClipRect();

        // The track: grid, the shade past the end, prop groups, events, then the lines on top.
        drawList.PushClipRect(new Vector2(trackLeft, rowsTop), max, true);
        var grid = U32(Theme.WithAlpha(Theme.TreeLine, .28f));
        for (var frame = view.FirstTick(major); frame <= view.Last; frame += major)
        {
            var x = Crisp(trackLeft + view.X(frame));
            drawList.AddLine(new Vector2(x, rowsTop), new Vector2(x, max.Y), grid, line);
        }
        var endX = trackLeft + view.X(doc.Length);
        if (endX < max.X)
            drawList.AddRectFilled(new Vector2(Math.Max(endX, trackLeft), rowsTop), max, U32(Theme.PastEnd));
        DrawGroups(drawList, geometry, view, min, max, trackLeft);
        DrawEvents(drawList, current, doc, layout, geometry, view, min, max, trackLeft, rowsTop, hovered && hit.OnEvent ? hit.Key : null);
        if (doc.LengthChanged)
        {
            var ghost = Crisp(trackLeft + view.X(doc.SourceLength));
            drawList.AddLine(new Vector2(ghost, rowsTop), new Vector2(ghost, max.Y), U32(Theme.WithAlpha(Theme.Muted, .45f)), line);
        }
        drawList.AddLine(new Vector2(Crisp(endX), rowsTop), new Vector2(Crisp(endX), max.Y),
            U32(handleHot ? Theme.Accent : Theme.WithAlpha(Theme.Muted, .7f)), handleHot ? Theme.Scaled(2) : line);
        drawList.PopClipRect();

        DrawRuler(drawList, current, doc, geometry, view, min, max, trackLeft, rowsTop, major, minor, handleHot);

        // Lines that cross the ruler and the lanes.
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), max, true);
        if (current.SnapGuide is { } guide)
        {
            var x = Crisp(trackLeft + view.X(guide));
            drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), U32(Theme.SnapGuide), line);
        }
        var playheadX = Crisp(trackLeft + view.X(current.Playhead));
        if (playheadX >= trackLeft - 1 && playheadX <= max.X + 1)
        {
            var playhead = U32(Theme.Playhead);
            var size = Theme.Scaled(6);
            drawList.AddLine(new Vector2(playheadX, min.Y), new Vector2(playheadX, max.Y), playhead, line);
            drawList.AddTriangleFilled(new Vector2(playheadX - size, min.Y), new Vector2(playheadX + size, min.Y),
                new Vector2(playheadX, min.Y + size * 1.4f), playhead);
        }
        drawList.PopClipRect();

        var border = U32(Theme.WithAlpha(Theme.TreeLine, .7f));
        drawList.AddLine(new Vector2(min.X, Crisp(rowsTop)), new Vector2(max.X, Crisp(rowsTop)), border, line);
        drawList.AddLine(new Vector2(Crisp(trackLeft), min.Y), new Vector2(Crisp(trackLeft), max.Y), border, line);
    }

    private void DrawRuler(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineGeometry geometry, TimelineView view,
        Vector2 min, Vector2 max, float trackLeft, float rowsTop, int major, int minor, bool handleHot)
    {
        var line = Math.Max(1, MathF.Floor(Theme.Scale));
        drawList.PushClipRect(new Vector2(trackLeft, min.Y), new Vector2(max.X, rowsTop), true);
        drawList.AddRectFilled(new Vector2(trackLeft, min.Y), new Vector2(max.X, rowsTop), U32(Theme.RowAlt));
        var tick = U32(Theme.Muted);
        var faint = U32(Theme.WithAlpha(Theme.Muted, .4f));
        var text = U32(Theme.Hint);
        for (var frame = view.FirstTick(minor); frame <= view.Last; frame += minor)
        {
            var x = Crisp(trackLeft + view.X(frame));
            var isMajor = frame % major == 0;
            drawList.AddLine(new Vector2(x, min.Y + geometry.Ruler * (isMajor ? .45f : .72f)), new Vector2(x, rowsTop), isMajor ? tick : faint, line);
            if (isMajor)
                drawList.AddText(new Vector2(x + Theme.Scaled(3), min.Y + Theme.Scaled(1)), text, TickLabel(frame, major));
        }
        var endX = trackLeft + view.X(doc.Length);
        if (endX < max.X)
            drawList.AddRectFilled(new Vector2(Math.Max(endX, trackLeft), min.Y), new Vector2(max.X, rowsTop), U32(Theme.PastEnd));
        if (doc.LengthChanged)
        {
            var ghost = Crisp(trackLeft + view.X(doc.SourceLength));
            drawList.AddLine(new Vector2(ghost, min.Y), new Vector2(ghost, rowsTop), U32(Theme.WithAlpha(Theme.Muted, .6f)), line);
        }
        // The length handle: a grip at the end, dragged to change the length.
        var grip = Theme.Scaled(3);
        drawList.AddRectFilled(new Vector2(endX - grip, min.Y + geometry.Ruler * .15f), new Vector2(endX + grip, rowsTop),
            U32(handleHot ? Theme.Accent : Theme.Muted), Theme.Scaled(2));
        drawList.PopClipRect();
    }

    private void DrawGroups(ImDrawListPtr drawList, TimelineGeometry geometry, TimelineView view, Vector2 min, Vector2 max, float trackLeft)
    {
        if (groupSpans.Count == 0)
            return;
        var fill = U32(Theme.WithAlpha(Theme.TrackProp, .07f));
        var outline = U32(Theme.WithAlpha(Theme.TrackProp, .4f));
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
        for (var index = 0; index < events.Length && index < labels.Length; index++)
        {
            var ev = events[index];
            var top = min.Y + geometry.RowTop(layout.RowOf(index));
            if (top + geometry.Row < rowsTop || top > max.Y)
                continue;
            var end = layout.EndOf(index);
            if (end < first || ev.Start > last)
                continue;
            DrawEvent(drawList, current, ev, labels[index], end, geometry, view, trackLeft, top + pad, top + geometry.Row - pad,
                hotKey != null && string.Equals(ev.Key, hotKey, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One event: a clip is a filled bar over its duration, a marker a diamond on its frame, and a
    /// sound also shows how long its file plays. Selected events get an accent outline, locked ones
    /// are dimmed with a lock, conditional ones a small dot.
    /// </summary>
    private static void DrawEvent(ImDrawListPtr drawList, TimelineEditor current, TimelineEvent ev, string label, int end,
        TimelineGeometry geometry, TimelineView view, float trackLeft, float y0, float y1, bool hot)
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
                drawList.AddRectFilled(new Vector2(edge, y0 + inset), new Vector2(x1, y1 - inset), U32(Theme.WithAlpha(colour, .2f)), rounding);
            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(edge, y1), U32(Theme.WithAlpha(colour, locked ? .24f : hot ? .8f : .6f)), rounding);
            drawList.AddRect(new Vector2(x0, y0), new Vector2(edge, y1), U32(Theme.WithAlpha(colour, locked ? .45f : .95f)), rounding, ImDrawFlags.None, line);
            if (selected)
                drawList.AddRect(new Vector2(x0 - 1, y0 - 1), new Vector2(edge + 1, y1 + 1), U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            labelLeft = x0 + Theme.Scaled(4);
            labelRight = edge - Theme.Scaled(4);
            badgeX = edge - Theme.Scaled(5);
        }
        else
        {
            var centre = trackLeft + view.X(ev.Start);
            var half = Math.Min((y1 - y0) / 2, Theme.Scaled(7));
            if (x1 > centre + half + 1)
                drawList.AddRectFilled(new Vector2(centre, middle - half * .6f), new Vector2(x1, middle + half * .6f),
                    U32(Theme.WithAlpha(colour, hot ? .32f : .22f)), rounding);
            if (selected)
            {
                var ring = half + Theme.Scaled(2);
                drawList.AddQuadFilled(new Vector2(centre, middle - ring), new Vector2(centre + ring, middle), new Vector2(centre, middle + ring),
                    new Vector2(centre - ring, middle), U32(Theme.Accent));
            }
            drawList.AddQuadFilled(new Vector2(centre, middle - half), new Vector2(centre + half, middle), new Vector2(centre, middle + half),
                new Vector2(centre - half, middle), U32(Theme.WithAlpha(colour, locked ? .5f : hot ? 1 : .9f)));
            labelLeft = centre + half + Theme.Scaled(4);
            labelRight = x1 - Theme.Scaled(3);
            badgeX = centre + half;
        }
        if (locked && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), U32(Theme.Muted), LockGlyph);
            labelLeft += fontSize;
        }
        if (labelRight - labelLeft > Theme.Scaled(18))
        {
            drawList.PushClipRect(new Vector2(labelLeft, y0), new Vector2(labelRight, y1), true);
            drawList.AddText(new Vector2(labelLeft, middle - fontSize / 2), U32(locked ? Theme.Muted : Theme.Text), label);
            drawList.PopClipRect();
        }
        if (ev.Conditional)
            drawList.AddCircleFilled(new Vector2(badgeX, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), U32(Theme.Info));
    }

    private void DrawTooltip(TimelineEditor current, TimelineHit hit, bool hovering, bool readOnly)
    {
        if (!hovering || current.Gesture != TimelineGesture.None)
            return;
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
            if (ev.Duration > 0)
                ImGui.TextUnformatted("Lasts " + TimelineWindowViews.DurationText(ev.Duration));
            if (ev.Path.Length > 0)
                ImGui.TextColored(Theme.Hint, ev.Path);
            if (ev.LockReason is { Length: > 0 } reason)
                ImGui.TextColored(Theme.Warning, (ev.Access == TimelineAccess.Locked ? "Locked: " : "Move only: ") + reason);
            if (ev.Conditional)
                ImGui.TextColored(Theme.Info, "Plays only under a condition.");
            return;
        }
        if (hit.Part == TimelinePart.LengthHandle && Widgets.HoverDelay("timeline-length-handle"))
            ImGui.SetTooltip(readOnly ? "The end of the clip." : "Drag to change the length.");
    }

    /// <summary> The right-click menu: actions on the event under the pointer, or adds and the length at that frame. </summary>
    private void DrawCanvasMenu(TimelineEditor current, bool readOnly)
    {
        using var popup = ImRaii.Popup(CanvasMenu);
        if (!popup.Success)
            return;
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
                RequestPicker(TimelineWindowViews.AddKinds[index], menuFrame, null);
        ImGui.Separator();
        if (ImGui.MenuItem("Paste here", "Ctrl+V", false, !readOnly && current.CanPaste))
        {
            current.Playhead = menuFrame;
            current.Paste();
        }
        if (ImGui.MenuItem("Set length here", "", false, !readOnly && menuFrame >= TimelineDocument.MinLength))
            current.SetLength(menuFrame);
        if (ImGui.MenuItem("Fit", "F"))
            current.Fit();
    }

    /// <summary> Whether the selection holds an event that can be duplicated, and one that can be moved or deleted. </summary>
    private static (bool Editable, bool Movable) SelectionAccess(TimelineEditor current)
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
    private void HandleKeys(TimelineEditor current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var io = ImGui.GetIO();
        if (io.WantTextInput)
            return;
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            return;
        var readOnly = ReadOnly;
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
    }
}
