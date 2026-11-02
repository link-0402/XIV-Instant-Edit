using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a spring layer, drawn in its facts table: how far its bones lag and how much they overshoot. A
/// slider's changes until it is left are one undo step. It only turns bones, so it has no part ticks.
/// </summary>
internal static class SpringFields
{
    public static void Draw(TimelineEditor current, MotionLayer layer)
    {
        if (layer.Spring is not { } spring)
            return;
        Slider(current, layer, "Lag", "##spring-lag", "How many frames the bones trail the motion.", spring.Lag, MotionSprings.MinLag, MotionSprings.MaxLag,
            "%.1f frames", 1, value => layer with { Spring = spring with { Lag = value } });
        Slider(current, layer, "Bounce", "##spring-bounce", "How far the bones overshoot and swing back.", spring.Bounce, 0, MotionSprings.MaxBounce,
            "%.0f%%", 100, value => layer with { Spring = spring with { Bounce = value } });
    }

    /// <summary> Under a spring's bone list: what to do when it has none. </summary>
    public static void DrawNotes(MotionLayer layer)
    {
        if (layer.Bones.IsEmpty)
            Widgets.HintWrapped("Select bones to spring.");
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
