using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a swing layer's settings and a smooth motion layer's window, drawn in its facts table. A
/// slider's changes until it is left are one undo step; a checkbox click is one.
/// </summary>
internal static class SwingFields
{
    /// <summary> A swing layer's rhythm, direction, sizes, delay and shape, and its twin and chain ticks. </summary>
    public static void Draw(TimelineEditor current, MotionLayer layer)
    {
        if (layer.Swing is not { } swing)
            return;
        var count = Whole("Swings", "##swing-count", "How many times it swings back and forth over the layer.", swing.Count, 1, MotionSwings.MaxCount, "%d");
        LayerPanel.TrackLayer(current, count != null, count is { } swings ? layer with { Swing = swing with { Count = swings } } : null);
        Slider(current, layer, "Direction", "##swing-direction", "0° side to side, 90° front to back.", swing.Direction, -180, 180, "%.0f°", 1,
            value => swing with { Direction = value });
        Slider(current, layer, "Tilt", "##swing-tilt", "How far the bones lean.", swing.Tilt, 0, MotionSwings.MaxAngle, "%.0f°", 1,
            value => swing with { Tilt = value });
        Slider(current, layer, "Twist", "##swing-twist", "How far the bones turn.", swing.Twist, 0, MotionSwings.MaxAngle, "%.0f°", 1,
            value => swing with { Twist = value });
        Slider(current, layer, "Slide", "##swing-slide", "How far the top bone moves.", swing.Slide, 0, MotionSwings.MaxSlide, "%.1f cm", 100,
            value => swing with { Slide = value });
        Slider(current, layer, "Delay", "##swing-delay", "How far each bone trails the one above it.", swing.Delay, 0, MotionSwings.MaxDelay, "%.0f frames", 1,
            value => swing with { Delay = value });
        Slider(current, layer, "Shape", "##swing-shape", "Higher lingers at each end.", swing.Shape, 0, 1, "%.0f%%", 100,
            value => swing with { Shape = value });
        TimelineUi.Fact("");
        var opposite = swing.TwinsOpposite;
        if (ImGui.Checkbox("Twins opposite##swing-twins", ref opposite))
            current.Commit(MotionEdits.Replace(current.Document, layer with { Swing = swing with { TwinsOpposite = opposite } }));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Left and right swing opposite ways.");
        ImGui.SameLine(0, Theme.Scaled(10));
        var spread = swing.Spread;
        if (ImGui.Checkbox("Spread over chain##swing-spread", ref spread))
            current.Commit(MotionEdits.Replace(current.Document, layer with { Swing = swing with { Spread = spread } }));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bones in a chain share the angle.");
    }

    /// <summary> A smooth motion layer's window in frames. </summary>
    public static void DrawWindow(TimelineEditor current, MotionLayer layer)
    {
        var window = Whole("Window", "##smooth-window", "How many frames it averages over. Wider is softer.", layer.Window,
            MotionSmoothing.MinWindow, MotionSmoothing.MaxWindow, "%d frames");
        LayerPanel.TrackLayer(current, window != null, window is { } frames ? layer with { Window = frames } : null);
    }

    /// <summary> Under a swing's bone list: what to do when it has none, and a reminder that swung hips carry the feet. </summary>
    public static void DrawNotes(MotionLayer layer)
    {
        if (layer.Bones.IsEmpty)
            Widgets.HintWrapped("Select bones to swing.");
        else if (layer.Bones.Contains("j_kosi") || layer.Bones.Contains(MotionHold.Body))
            Widgets.MutedWrapped("Add Pin feet after it to keep the feet planted.");
    }

    /// <summary> A labelled slider row whose value shows as <paramref name="scale"/> times the setting. </summary>
    private static void Slider(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, float value, float min, float max,
        string format, float scale, Func<float, MotionSwing> with)
    {
        TimelineUi.Fact(label);
        var shown = value * scale;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat(id, ref shown, min * scale, max * scale, format);
        LayerPanel.TrackLayer(current, changed, changed ? layer with { Swing = with(Math.Clamp(shown / scale, min, max)) } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip + " Ctrl+click to type.");
    }

    /// <summary> A labelled whole-number slider row; its new value while it changes, else null. Its caller tracks the edit. </summary>
    private static int? Whole(string label, string id, string tooltip, int value, int min, int max, string format)
    {
        TimelineUi.Fact(label);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderInt(id, ref value, min, max, format);
        // A tooltip is no item, so the caller's tracking still reads the slider.
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip + " Ctrl+click to type.");
        return changed ? Math.Clamp(value, min, max) : null;
    }
}
