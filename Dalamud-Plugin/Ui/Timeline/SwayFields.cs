using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a sway layer, drawn in its facts table: its swings, their direction and size, and how stiff, bouncy
/// and heavy its bones are. A slider's changes until it is left are one undo step. It only turns bones, so it has no part ticks.
/// </summary>
internal static class SwayFields
{
    public static void Draw(TimelineEditor current, MotionLayer layer)
    {
        if (layer.Sway is not { } sway)
            return;
        TimelineUi.Fact("Swings");
        var count = sway.Count;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var counted = ImGui.SliderInt("##sway-count", ref count, 1, MotionSways.MaxCount, "%d");
        // A tooltip is no item, so the tracking still reads the slider.
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many times it sways back and forth over the layer. Ctrl+click to type.");
        LayerPanel.TrackLayer(current, counted, counted ? layer with { Sway = sway with { Count = Math.Clamp(count, 1, MotionSways.MaxCount) } } : null);
        Slider(current, layer, "Direction", "##sway-direction", "0° side to side, 90° front to back.", sway.Direction, -180, 180, "%.0f°", 1,
            value => layer with { Sway = sway with { Direction = value } });
        Slider(current, layer, "Angle", "##sway-angle", "How far the top bone swings.", sway.Angle, 0, MotionSways.MaxAngle, "%.0f°", 1,
            value => layer with { Sway = sway with { Angle = value } });
        Slider(current, layer, "Stiffness", "##sway-stiffness", "How firmly the bones keep their shape.", sway.Stiffness, MotionSways.MinStiffness, 1, "%.0f%%", 100,
            value => layer with { Sway = sway with { Stiffness = value } });
        Slider(current, layer, "Bounce", "##sway-bounce", "How freely the bones swing on.", sway.Bounce, 0, MotionSways.MaxBounce, "%.0f%%", 100,
            value => layer with { Sway = sway with { Bounce = value } });
        Slider(current, layer, "Hang", "##sway-hang", "How much the bones hang.", sway.Hang, 0, 1, "%.0f%%", 100,
            value => layer with { Sway = sway with { Hang = value } });
    }

    /// <summary> Under a sway's bone list: what to do when it has none. </summary>
    public static void DrawNotes(MotionLayer layer)
    {
        if (layer.Bones.IsEmpty)
            Widgets.HintWrapped("Select bones to sway.");
    }

    /// <summary> A labelled slider row whose value shows as <paramref name="scale"/> times the setting. </summary>
    private static void Slider(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, float value, float min, float max,
        string format, float scale, Func<float, MotionLayer> with)
    {
        TimelineUi.Fact(label);
        var shown = value * scale;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat(id, ref shown, min * scale, max * scale, format);
        LayerPanel.TrackLayer(current, changed, changed ? with(Math.Clamp(shown / scale, min, max)) : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip + " Ctrl+click to type.");
    }
}
