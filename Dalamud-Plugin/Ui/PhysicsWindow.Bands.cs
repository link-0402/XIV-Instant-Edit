using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

/// <summary>
/// The strip under an area's sliders: where most presets set the value for this area (from a survey of
/// presets, <see cref="PhysicsRanges"/>), and a mark at the game's own value for the same bone.
/// </summary>
internal sealed partial class PhysicsWindow
{
    private string? sliderArea;
    private (PhybSimulator Simulator, int Chain)? sliderGame;
    private Task<PhybFile?>? vanillaTask;
    private ushort vanillaRace;

    /// <summary> Which area and game chain the sliders below show ranges for: the first edited chain's. </summary>
    private void SetSliderArea(PhysicsDocument current, List<(int Simulator, int Chain)> targets)
    {
        var (s0, c0) = targets[0];
        var root = current.File.Simulators[s0].Chains[c0].Nodes[0].Bone.Text;
        sliderArea = PhysicsAreas.KindOf(root)?.Key;
        sliderGame = null;
        if (target is not { } found)
            return;
        if (vanillaTask == null || vanillaRace != found.Character.Race)
        {
            vanillaRace = found.Character.Race;
            vanillaTask = service.VanillaAsync(vanillaRace);
        }
        if (vanillaTask is not { IsCompletedSuccessfully: true, Result: { } game })
            return;
        for (var s = 0; s < game.Simulators.Count; s++)
            for (var c = 0; c < game.Simulators[s].Chains.Count; c++)
                if (game.Simulators[s].Chains[c].Nodes.Count >= 2 && game.Simulators[s].Chains[c].Nodes[0].Bone.Text == root)
                {
                    sliderGame = (game.Simulators[s], c);
                    return;
                }
    }

    /// <summary> Draws the strip over the slider just drawn, and says what it means while the slider is hovered. </summary>
    private void DrawBand(PhysicsKnob knob, float min, float max, string format, bool logarithmic)
    {
        var band = sliderArea is { } area ? PhysicsRanges.Band(area, knob) : null;
        var game = sliderGame is { } chain ? PhysicsKnobs.Read(chain.Simulator, chain.Chain, knob) : null;
        if (band is null && game is null)
            return;
        var from = ImGui.GetItemRectMin();
        var to = ImGui.GetItemRectMax();
        var decimals = SliderScale.Decimals(format);
        var grab = ImGui.GetStyle().GrabMinSize;
        float At(float value) => SliderScale.X(SliderScale.Ratio(value, min, max, logarithmic, decimals), from.X, to.X, grab);
        var draw = ImGui.GetWindowDrawList();
        var height = Math.Max(2f, Theme.Scaled(3));
        var bottom = to.Y - 1;
        if (band is { } range)
        {
            var left = At(range.Low);
            var right = Math.Max(At(range.High), left + 2);
            draw.AddRectFilled(new Vector2(left, bottom - height), new Vector2(right, bottom), ImGui.GetColorU32(Theme.WithAlpha(Theme.Success, .7f)));
        }
        if (game is { } value)
        {
            var x = At(Math.Clamp(value, min, max));
            draw.AddRectFilled(new Vector2(x - 1, bottom - height - Theme.Scaled(3)), new Vector2(x + 1, bottom), ImGui.GetColorU32(Theme.Accent));
        }
        if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
        {
            var text = band is { } shown
                ? shown.Low == shown.High
                    ? $"Most presets: {SliderScale.Text(shown.Low, format)}."
                    : $"Most presets: {SliderScale.Text(shown.Low, format)} to {SliderScale.Text(shown.High, format)}."
                : string.Empty;
            if (game is { } own)
                text += (text.Length > 0 ? " " : string.Empty) + $"The game: {SliderScale.Text(own, format)}.";
            ImGui.SetTooltip(text);
        }
    }
}
