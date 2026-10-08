using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

/// <summary> A mark under an area's sliders where the value stood in the file you started from. </summary>
internal sealed partial class PhysicsWindow
{
    private (PhybSimulator Simulator, int Chain)? sliderStart;

    /// <summary> The started-from chain the sliders below mark: the one with the first edited chain's bone. </summary>
    private void SetSliderStart(PhysicsDocument current, List<(int Simulator, int Chain)> targets)
    {
        var (s0, c0) = targets[0];
        var root = current.File.Simulators[s0].Chains[c0].Nodes[0].Bone.Text;
        sliderStart = null;
        var start = current.Start;
        for (var s = 0; s < start.Simulators.Count; s++)
            for (var c = 0; c < start.Simulators[s].Chains.Count; c++)
                if (start.Simulators[s].Chains[c].Nodes.Count >= 2 && start.Simulators[s].Chains[c].Nodes[0].Bone.Text == root)
                {
                    sliderStart = (start.Simulators[s], c);
                    return;
                }
    }

    /// <summary> Draws the starting value's mark over the slider just drawn, and names it while the slider is hovered. </summary>
    private void DrawStartMark(PhysicsKnob knob, float min, float max, string format, bool logarithmic)
    {
        if (sliderStart is not { } chain || PhysicsKnobs.Read(chain.Simulator, chain.Chain, knob) is not { } value)
            return;
        var from = ImGui.GetItemRectMin();
        var to = ImGui.GetItemRectMax();
        var grab = ImGui.GetStyle().GrabMinSize;
        var x = SliderScale.X(SliderScale.Ratio(Math.Clamp(value, min, max), min, max, logarithmic, SliderScale.Decimals(format)), from.X, to.X, grab);
        var height = Math.Max(2f, Theme.Scaled(3));
        var bottom = to.Y - 1;
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(x - 1, bottom - height - Theme.Scaled(3)), new Vector2(x + 1, bottom),
            ImGui.GetColorU32(Theme.Accent));
        if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
            ImGui.SetTooltip($"Started at {SliderScale.Text(value, format)}.");
    }
}
