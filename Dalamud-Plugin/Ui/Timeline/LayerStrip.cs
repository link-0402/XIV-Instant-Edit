using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Bones window's layer strip: a dropdown with the clip's bone layers, the previous-key arrow, the strip with the
/// selected layer's span across the clip, its keys and the playhead, and the next-key arrow. A click or drag on the strip
/// moves the playhead; a click on a key goes there and selects it. The layer chosen here is the Timeline's selected layer.
/// </summary>
internal static class LayerStrip
{
    private static readonly string[] LayerGlyphs = [.. TimelineUi.LayerIcons.Select(icon => icon.ToIconString())];

    /// <summary>The layer's last key before <paramref name="frame"/>; null when there is none.</summary>
    public static int? Previous(MotionLayer layer, int frame)
    {
        int? found = null;
        foreach (var key in layer.Keys)
            if (key.Frame < frame && (found == null || key.Frame > found))
                found = key.Frame;
        return found;
    }

    /// <summary>The layer's first key after <paramref name="frame"/>; null when there is none.</summary>
    public static int? Next(MotionLayer layer, int frame)
    {
        int? found = null;
        foreach (var key in layer.Keys)
            if (key.Frame > frame && (found == null || key.Frame < found))
                found = key.Frame;
        return found;
    }

    /// <summary>
    /// The layer's key nearest <paramref name="frame"/>, at most <paramref name="reach"/> frames away; the earlier one on a
    /// tie, null when none is that close.
    /// </summary>
    public static int? KeyNear(MotionLayer layer, int frame, int reach)
    {
        int? found = null;
        var best = int.MaxValue;
        foreach (var key in layer.Keys)
        {
            var distance = Math.Abs(key.Frame - frame);
            if (distance > reach || distance > best || distance == best && key.Frame > found)
                continue;
            best = distance;
            found = key.Frame;
        }
        return found;
    }

    /// <summary>
    /// Draws the strip's row, filling the width left. <paramref name="id"/> starts every control's id;
    /// <paramref name="pause"/> pauses a playing preview whenever the strip moves the playhead.
    /// </summary>
    public static void Draw(TimelineEditor current, string id, Action pause)
    {
        var layer = current.SelectedLayerValue;
        DrawLayerChoice(current, id, layer);

        ImGui.SameLine(0, Theme.Gap);
        var previous = layer != null ? Previous(layer, current.Playhead) : null;
        if (Widgets.IconButton(id + "-previous", FontAwesomeIcon.ChevronLeft,
                layer == null ? "Select a bone layer first." : previous == null ? "No key before the playhead." : "Go to the layer's previous key",
                previous != null) && previous is { } back)
            GoToKey(current, layer!, back, pause);

        ImGui.SameLine(0, Theme.Scaled(2));
        DrawBar(current, id, layer, pause);

        ImGui.SameLine(0, Theme.Scaled(2));
        var next = layer != null ? Next(layer, current.Playhead) : null;
        if (Widgets.IconButton(id + "-next", FontAwesomeIcon.ChevronRight,
                layer == null ? "Select a bone layer first." : next == null ? "No key after the playhead." : "Go to the layer's next key",
                next != null) && next is { } ahead)
            GoToKey(current, layer!, ahead, pause);
    }

    /// <summary>The playhead to the layer's key at <paramref name="frame"/>, the key selected.</summary>
    private static void GoToKey(TimelineEditor current, MotionLayer layer, int frame, Action pause)
    {
        current.Playhead = frame;
        current.SelectLayer(layer.Key, keyFrame: frame);
        pause();
    }

    /// <summary>The selected layer's icon and the dropdown of the clip's bone layers, each with its icon and name.</summary>
    private static void DrawLayerChoice(TimelineEditor current, string id, MotionLayer? layer)
    {
        var motion = current.Document.Motion;
        // A fixed slot for the icon, so the row keeps its widths whether a layer is selected or not.
        var slot = new Vector2(Theme.Scaled(18), ImGui.GetFrameHeight());
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(slot);
        if (layer != null)
            DrawGlyph(ImGui.GetWindowDrawList(), layer.Kind, at, slot, TimelineWindowViews.LayerColour(layer.Kind));

        ImGui.SameLine(0, Theme.Scaled(2));
        ImGui.SetNextItemWidth(Theme.Scaled(150));
        using (ImRaii.Disabled(motion.IsEmpty))
        using (var combo = ImRaii.Combo(id + "-layer", layer?.Name ?? (motion.IsEmpty ? "No bone layer" : "Choose a layer")))
        {
            if (combo.Success)
                foreach (var each in motion)
                {
                    using var pushed = ImRaii.PushId(each.Key);
                    var x = ImGui.GetCursorPosX();
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextColored(TimelineWindowViews.LayerColour(each.Kind), Glyph(each.Kind));
                    ImGui.SameLine(x + Theme.Scaled(22));
                    bool chosen;
                    using (ImRaii.PushColor(ImGuiCol.Text, Theme.Muted, !each.Enabled))
                        chosen = ImGui.Selectable(each.Name, each.Key == layer?.Key);
                    if (chosen)
                        current.SelectLayer(each.Key);
                }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(motion.IsEmpty ? "This clip has no bone layer. Add one in the timeline." : "The bone layer to pose, as selected in the timeline.");
    }

    /// <summary>
    /// The strip: the clip's length, the layer's span, its keys as diamonds and the playhead. A click or drag moves the
    /// playhead; a click within a few pixels of a key goes to that key and selects it.
    /// </summary>
    private static void DrawBar(TimelineEditor current, string id, MotionLayer? layer, Action pause)
    {
        var height = ImGui.GetFrameHeight();
        // The room left after the next-key arrow.
        var width = Math.Max(Theme.Scaled(40), ImGui.GetContentRegionAvail().X - height - Theme.Scaled(2));
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        ImGui.InvisibleButton(id + "-bar", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var activated = ImGui.IsItemActivated();
        var active = ImGui.IsItemActive();

        var length = Math.Max(1, current.Document.Length);
        var pad = Theme.Scaled(6);
        var left = min.X + pad;
        var span = Math.Max(1, width - pad * 2);
        float X(int frame) => left + span * Math.Clamp(frame, 0, length) / length;

        if (active)
        {
            var frame = Math.Clamp((int)MathF.Round((ImGui.GetMousePos().X - left) / span * length), 0, length);
            if (activated)
            {
                var reach = (int)MathF.Ceiling(Theme.Scaled(5) * length / span);
                if (layer != null && KeyNear(layer, frame, reach) is { } key)
                    GoToKey(current, layer, key, pause);
                else
                {
                    current.Playhead = frame;
                    pause();
                }
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && frame != current.Playhead)
            {
                current.Playhead = frame;
                pause();
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered || active ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
        var middle = MathF.Floor(min.Y + height / 2) + .5f;
        drawList.AddLine(new Vector2(left, middle), new Vector2(left + span, middle), TimelineUi.U32(Theme.Inactive), Theme.Scaled(1));
        if (layer != null)
        {
            var band = Theme.Scaled(4);
            var colour = TimelineWindowViews.LayerColour(layer.Kind);
            drawList.AddRectFilled(new Vector2(X(layer.Start), middle - band), new Vector2(X(layer.End), middle + band),
                TimelineUi.U32(Theme.WithAlpha(colour, layer.Enabled ? .45f : .2f)), Theme.Scaled(2));
            var half = Theme.Scaled(4.5f);
            var selected = current.SelectedLayer == layer.Key ? current.SelectedKey : null;
            foreach (var key in layer.Keys)
            {
                var x = X(key.Frame);
                var fill = TimelineUi.U32(key.Frame == selected ? Theme.Accent : Theme.WithAlpha(Theme.Text, layer.Enabled ? 1 : .5f));
                drawList.AddQuadFilled(new Vector2(x, middle - half), new Vector2(x + half, middle), new Vector2(x, middle + half),
                    new Vector2(x - half, middle), fill);
            }
        }
        var playhead = TimelineUi.Crisp(X(current.Playhead));
        drawList.AddLine(new Vector2(playhead, min.Y + Theme.Scaled(2)), new Vector2(playhead, max.Y - Theme.Scaled(2)),
            TimelineUi.U32(Theme.Playhead), Theme.Scaled(2));
        drawList.PopClipRect();

        if (hovered && !active)
            ImGui.SetTooltip(layer == null ? "Click or drag to move the playhead." : "Click or drag to move the playhead. Click a key to go to it.");
    }

    private static string Glyph(MotionLayerKind kind) => LayerGlyphs[Math.Min((int)kind, LayerGlyphs.Length - 1)];

    /// <summary>A layer kind's icon centred in a box.</summary>
    private static void DrawGlyph(ImDrawListPtr drawList, MotionLayerKind kind, Vector2 at, Vector2 box, Vector4 colour)
    {
        var glyph = Glyph(kind);
        Vector2 size;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            size = ImGui.CalcTextSize(glyph);
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), at + (box - size) / 2, TimelineUi.U32(colour), glyph);
    }
}
