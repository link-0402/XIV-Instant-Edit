using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
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
    // A face from the timeline's expression library.
    private static readonly string PackGlyph = FontAwesomeIcon.Book.ToIconString();
    private static readonly string PinGlyph = FontAwesomeIcon.Thumbtack.ToIconString();
    // Offset, amplitude and pose layers.
    private static readonly FontAwesomeIcon[] LayerIcons = [FontAwesomeIcon.Bone, FontAwesomeIcon.WaveSquare, FontAwesomeIcon.StreetView];
    private static readonly string[] LayerGlyphs = [.. LayerIcons.Select(icon => icon.ToIconString())];
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
    private TimelineDocument? labelDocument;
    private string[] labels = [];
    private string lengthText = string.Empty;
    private string? headerNote;
    private string? pastEndWarning;
    private string? layerBonesWarning;
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

    private static FontAwesomeIcon LayerIcon(MotionLayerKind kind) => LayerIcons[Math.Min((int)kind, LayerIcons.Length - 1)];

    private static string LayerGlyph(MotionLayerKind kind) => LayerGlyphs[Math.Min((int)kind, LayerGlyphs.Length - 1)];

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
        // Only a clip the game loops has a seam to blend or fix.
        current.Loops = source is { } loaded && Fresh(loaded).Clip.IsLoop && !current.Document.Expression;
        var clip = ClipBand(current.Document, row);
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
        clip = ClipBand(current.Document, row);
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
            TimelineGesture.BoneKey or TimelineGesture.TrimLeft or TimelineGesture.TrimRight or TimelineGesture.BlendLength;
        if (!readOnly && (resizing || hovered && current.Gesture == TimelineGesture.None &&
                hit.Part is TimelinePart.LeftEdge or TimelinePart.RightEdge or TimelinePart.LengthHandle or TimelinePart.LayerLeft or
                    TimelinePart.LayerRight or TimelinePart.LayerFadeIn or TimelinePart.LayerFadeOut or TimelinePart.LayerKey or TimelinePart.BoneKey or
                    TimelinePart.ClipLeft or TimelinePart.ClipRight or TimelinePart.JoinEdge or TimelinePart.SeamEdge))
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
        if (OnSeamBadge(io.MousePos))
        {
            current.SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
            return;
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !readOnly &&
            hit.Part is TimelinePart.Lane or TimelinePart.Legend && hit.Lane is { } lane && TimelineWindowViews.AddKind(lane) is { } kind)
        {
            RequestPicker(kind, hit.Part == TimelinePart.Legend ? current.Playhead : current.SnappedFrame(hit.Frame, io.KeyAlt), null);
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
            selection.Click(bone, io.KeyCtrl && hit.Part != TimelinePart.BoneKey);
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
        layerBonesWarning = source == null ? null : TimelineWindowViews.LayerBonesWarning(doc, Fresh(source).Clip);
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
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), U32(index % 2 == 0 ? Theme.PanelBg : Theme.RowAlt));
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), U32(colour));
            var textY = top + (geometry.Row - fontSize) / 2;
            drawList.PushClipRect(new Vector2(min.X, top), new Vector2(trackLeft - Theme.Scaled(3), bottom), true);
            drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(min.X + Theme.Scaled(9), textY), U32(colour), LaneGlyph(lane.Lane));
            drawList.AddText(new Vector2(min.X + Theme.Scaled(30), textY), U32(Theme.Label), TimelineWindowViews.LaneName(lane.Lane));
            drawList.PopClipRect();
        }
        DrawLayerLegend(drawList, current, doc, layout, geometry, min, max, trackLeft, rowsTop, hovered ? hit : TimelineHit.None());
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
        DrawLayers(drawList, current, doc, layout, geometry, view, min, max, trackLeft, rowsTop, hovered ? hit : TimelineHit.None());
        if (doc.LengthChanged)
        {
            var ghost = Crisp(trackLeft + view.X(doc.SourceLength));
            drawList.AddLine(new Vector2(ghost, rowsTop), new Vector2(ghost, max.Y), U32(Theme.WithAlpha(Theme.Muted, .45f)), line);
        }
        drawList.AddLine(new Vector2(Crisp(endX), rowsTop), new Vector2(Crisp(endX), max.Y),
            U32(handleHot ? Theme.Accent : Theme.WithAlpha(Theme.Muted, .7f)), handleHot ? Theme.Scaled(2) : line);
        drawList.PopClipRect();

        DrawRuler(drawList, current, doc, geometry, view, min, max, trackLeft, min.Y + geometry.Ruler, major, minor, handleHot);
        DrawClipBand(drawList, current, geometry, min, max, trackLeft, hovered ? hit : TimelineHit.None());

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
            var ev = layout.Shown(index);
            var top = min.Y + geometry.RowTop(layout.RowOf(index));
            if (top + geometry.Row < rowsTop || top > max.Y)
                continue;
            var end = layout.EndOf(index);
            if (end < first || ev.Start > last)
                continue;
            DrawEvent(drawList, current, ev, labels[index], end, geometry, view, trackLeft, top + pad, top + geometry.Row - pad,
                hotKey != null && string.Equals(ev.Key, hotKey, StringComparison.Ordinal), SoundMarkOf(doc, ev));
        }
    }

    /// <summary>
    /// What a sound clip shows besides its bar: the frame its solid part ends at (int.MaxValue while
    /// its file is read), a pin, its file's changes, and something to check (a sound that ends before
    /// its audio, or Keep looping on a file that plays once).
    /// </summary>
    private readonly record struct SoundMark(int SolidEnd, bool Pinned, bool Changed, bool Check);

    private SoundMark? SoundMarkOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ev.Magic != "C063" || TimelineSoundKey.Of(ev) is not { } key)
            return null;
        var pinned = ev.Pin != null;
        var changed = doc.SoundEdits.ContainsKey(key);
        if (ShownIfRead(doc, key) is not { Seconds.IsEmpty: false } shown)
            return pinned || changed ? new SoundMark(int.MaxValue, pinned, changed, false) : null;
        var span = TimelineWindowViews.SoundSpan(shown.Seconds, ev.Pin is { } pin && shown.Ids.IndexOf(pin) is >= 0 and var index ? index : null);
        var loop = ev.Values?.GetValueOrDefault("Loop", 1) ?? 1;
        return new SoundMark(ev.Start + span.Solid, pinned, changed, shown.EndsEarly || loop == -1 && !shown.Loops);
    }

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
    private static bool FromPack(TimelineEditor current, TimelineEvent ev) =>
        current.ResidentMotions is { } resident && TimelineEdits.FromPack(current.Document, ev, resident);

    /// <summary>
    /// One event: a clip is a filled bar over its duration, a marker a diamond on its frame, and a
    /// sound also shows how long its file plays: solid to its shortest alternative, dashed to its
    /// longest. Selected events get an accent outline, locked ones are dimmed with a lock, pinned
    /// sounds get a pin, conditional ones a small dot, a changed look or sound an accent dot and a
    /// sound with something to check an amber one.
    /// </summary>
    private static void DrawEvent(ImDrawListPtr drawList, TimelineEditor current, TimelineEvent ev, string label, int end,
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
            // A random sound is solid while every alternative plays, dashed while only the longer ones do.
            var solid = sound is { SolidEnd: var solidEnd and < int.MaxValue } ? Math.Clamp(trackLeft + view.X(solidEnd), centre, x1) : x1;
            if (solid > centre + half + 1)
                drawList.AddRectFilled(new Vector2(centre, middle - half * .6f), new Vector2(solid, middle + half * .6f),
                    U32(Theme.WithAlpha(colour, hot ? .32f : .22f)), rounding);
            if (x1 > solid + 1)
                DashedOutline(drawList, Math.Max(solid, centre + half), middle - half * .6f, x1, middle + half * .6f,
                    U32(Theme.WithAlpha(colour, hot ? .7f : .5f)), line);
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
        if (FromPack(current, ev) && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), U32(Theme.Text), PackGlyph);
            labelLeft += fontSize;
        }
        if (sound is { Pinned: true } && labelRight - labelLeft > Theme.Scaled(14))
        {
            drawList.AddText(UiBuilder.IconFont, fontSize * .8f, new Vector2(labelLeft, middle - fontSize * .4f), U32(Theme.Text), PinGlyph);
            labelLeft += fontSize;
        }
        if (labelRight - labelLeft > Theme.Scaled(18))
        {
            drawList.PushClipRect(new Vector2(labelLeft, y0), new Vector2(labelRight, y1), true);
            drawList.AddText(new Vector2(labelLeft, middle - fontSize / 2), U32(locked ? Theme.Muted : Theme.Text), label);
            drawList.PopClipRect();
        }
        var dot = badgeX;
        if (ev.Conditional)
        {
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), U32(Theme.Info));
            dot -= Theme.Scaled(7);
        }
        if (ev.Look != null || sound is { Changed: true })
        {
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), U32(Theme.Accent));
            dot -= Theme.Scaled(7);
        }
        if (sound is { Check: true })
            drawList.AddCircleFilled(new Vector2(dot, y0 + Theme.Scaled(3)), Theme.Scaled(2.5f), U32(Theme.Warning));
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
            var band = U32(layerBand % 2 == 1 ? Theme.RowAlt : Theme.PanelBg);
            var textY = top + (geometry.Row - fontSize) / 2;
            if (entry.Bone is { } bone && layer != null)
            {
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), band);
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), U32(Theme.WithAlpha(colour, .05f)));
                drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), U32(Theme.WithAlpha(colour, .45f)));
                var picked = current.SelectedLayer == layer.Key && selection.Contains(bone);
                var hot = hit.Part == TimelinePart.Legend && hit.Row == row;
                drawList.PushClipRect(new Vector2(min.X, top), new Vector2(trackLeft - Theme.Scaled(3), bottom), true);
                drawList.AddText(new Vector2(min.X + Theme.Scaled(22), textY),
                    U32(picked ? Theme.Accent : hot ? Theme.Text : !layer.Enabled ? Theme.Muted : Theme.Label), AnimationPresentation.BoneName(bone));
                drawList.PopClipRect();
                continue;
            }
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(max.X, bottom), band);
            drawList.AddRectFilled(new Vector2(min.X, top), new Vector2(min.X + Theme.Scaled(3), bottom), U32(colour));
            var unfolds = layer != null && TimelineLayout.Unfolds(layer);
            var nameRight = trackLeft - Theme.Scaled(3) - (unfolds ? geometry.Unfold : 0);
            drawList.PushClipRect(new Vector2(min.X, top), new Vector2(nameRight, bottom), true);
            drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(min.X + Theme.Scaled(9), textY), U32(Theme.WithAlpha(colour, layer?.Enabled == false ? .5f : 1)),
                LayerGlyph(layer?.Kind ?? MotionLayerKind.Offset));
            var name = layer == null ? Theme.Hint : !layer.Enabled ? Theme.Muted : current.SelectedLayer == layer.Key ? Theme.Accent : Theme.Label;
            drawList.AddText(new Vector2(min.X + Theme.Scaled(30), textY), U32(name), layer?.Name ?? TimelineWindowViews.BonesLaneName);
            drawList.PopClipRect();
            if (unfolds)
            {
                var hot = hit.Part == TimelinePart.LayerUnfold && hit.Row == row;
                var glyph = current.IsUnfolded(layer!.Key) ? UnfoldedGlyph : FoldedGlyph;
                drawList.AddText(UiBuilder.IconFont, fontSize, new Vector2(trackLeft - geometry.Unfold + Theme.Scaled(3), textY),
                    U32(hot ? Theme.Accent : Theme.Muted), glyph);
            }
        }
        var y = Crisp(min.Y + geometry.RowTop(layout.MotionFirstRow));
        if (y > rowsTop && y < max.Y)
            drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), U32(Theme.WithAlpha(Theme.TreeLine, .8f)), Math.Max(1, MathF.Floor(Theme.Scale)));
    }

    /// <summary>
    /// Each bone layer: a faint bar over its frames with its weight drawn on it, rising over the fade in
    /// and falling over the fade out; its keys as diamonds; and, when it is selected or under the
    /// pointer, handles where the fades end. A layer turned off is dimmed.
    /// </summary>
    private static void DrawLayers(ImDrawListPtr drawList, TimelineEditor current, TimelineDocument doc, TimelineLayout layout,
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
            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), U32(Theme.WithAlpha(colour, .14f * alpha)), rounding);
            drawList.AddQuadFilled(new Vector2(x0, y1), new Vector2(fadeInX, y0), new Vector2(fadeOutX, y0), new Vector2(x1, y1),
                U32(Theme.WithAlpha(colour, (hot ? .62f : .46f) * alpha)));
            drawList.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), U32(Theme.WithAlpha(colour, .9f * alpha)), rounding, ImDrawFlags.None, line);
            if (selected)
                drawList.AddRect(new Vector2(x0 - 1, y0 - 1), new Vector2(x1 + 1, y1 + 1), U32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));
            var labelLeft = Math.Max(x0, trackLeft) + Theme.Scaled(4);
            var labelRight = x1 - Theme.Scaled(4);
            if (labelRight - labelLeft > Theme.Scaled(18))
            {
                drawList.PushClipRect(new Vector2(labelLeft, y0), new Vector2(labelRight, y1), true);
                drawList.AddText(new Vector2(labelLeft, top + (geometry.Row - fontSize) / 2), U32(layer.Enabled ? Theme.Text : Theme.Muted),
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
                        U32(current.SelectedBone == null ? Theme.Accent : Theme.WithAlpha(Theme.Accent, .45f)));
                // A key outside the layer still shapes the curve but never applies on its own.
                var inside = key.Frame >= layer.Start && key.Frame <= layer.End;
                var hotKey = hot && hit.Part == TimelinePart.LayerKey && hit.KeyFrame == key.Frame;
                DrawKeyMark(drawList, shape, x, middle, half, U32(Theme.WithAlpha(Theme.Text, (inside ? hotKey ? 1 : .88f : .4f) * alpha)));
            }
            if (selected || hot)
            {
                var radius = Theme.Scaled(3);
                drawList.AddCircleFilled(new Vector2(fadeInX, y0), radius,
                    U32(hot && hit.Part == TimelinePart.LayerFadeIn ? Theme.Accent : Theme.WithAlpha(Theme.Text, .8f)));
                drawList.AddCircleFilled(new Vector2(fadeOutX, y0), radius,
                    U32(hot && hit.Part == TimelinePart.LayerFadeOut ? Theme.Accent : Theme.WithAlpha(Theme.Text, .8f)));
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
            drawList.AddLine(new Vector2(x0, middle), new Vector2(x1, middle), U32(Theme.WithAlpha(colour, .45f * alpha)), line);
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
                        U32(current.SelectedBone == entry.Bone ? Theme.Accent : Theme.WithAlpha(Theme.Accent, .45f)));
                var inside = key.Frame >= layer.Start && key.Frame <= layer.End;
                var hotKey = hit.Part == TimelinePart.BoneKey && hit.Row == row && hit.KeyFrame == key.Frame;
                DrawKeyMark(drawList, shape, x, middle, half, U32(Theme.WithAlpha(hotKey ? Theme.Text : colour, (inside ? hotKey ? 1 : .95f : .4f) * alpha)));
            }
        }
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
        if (DrawClipTooltip(hit, readOnly))
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
            if (FromPack(current, ev))
                ImGui.TextUnformatted($"From the {current.Document.FacePack} expressions.");
            if (ev.LockReason is { Length: > 0 } reason)
                ImGui.TextColored(Theme.Warning, (ev.Access == TimelineAccess.Locked ? "Locked: " : "Move only: ") + reason);
            if (ev.Conditional)
                ImGui.TextColored(Theme.Info, "Plays only under a condition.");
            if (ev.Look != null)
                ImGui.TextColored(Theme.Accent, "Look changed.");
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
            DrawClipMenu(current, readOnly);
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
                RequestPicker(TimelineWindowViews.AddKinds[index], menuFrame, null);
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
