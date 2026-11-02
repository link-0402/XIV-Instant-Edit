using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a balance layer, drawn in its facts table: the axes it corrects, how long the hips' move is
/// averaged over, how far they may move and how readily a foot counts as down; below them where each foot is down. A slider's
/// changes until it is left are one undo step; a tick or a pick is one. It always moves the hips, so it has no part ticks.
/// </summary>
internal static class BalanceFields
{
    private static readonly PinSensitivity[] Sensitivities = [PinSensitivity.Normal, PinSensitivity.Strict, PinSensitivity.Loose];

    public static void Draw(TimelineEditor current, MotionLayer layer)
    {
        if (layer.Balance is not { } balance)
            return;
        Tick(current, layer, "Sideways", "##balance-sideways", "Move the hips side to side.", balance.Sideways, on => balance with { Sideways = on });
        Tick(current, layer, "Front to back", "##balance-front", "Move the hips forward and back.", balance.FrontBack, on => balance with { FrontBack = on });

        TimelineUi.Fact("Smooth");
        var smooth = balance.Smooth;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var smoothed = ImGui.SliderInt("##balance-smooth", ref smooth, 0, MotionBalances.MaxSmooth, "%d frames");
        LayerPanel.TrackLayer(current, smoothed, smoothed ? layer with { Balance = balance with { Smooth = Math.Clamp(smooth, 0, MotionBalances.MaxSmooth) } } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many frames the hips' move is evened out over. Ctrl+click to type.");

        TimelineUi.Fact("Max shift");
        var shift = balance.MaxShift * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var shifted = ImGui.SliderFloat("##balance-shift", ref shift, MotionBalances.MinShift * 100, MotionBalances.MaxShiftLimit * 100, "%.0f cm");
        LayerPanel.TrackLayer(current, shifted,
            shifted ? layer with { Balance = balance with { MaxShift = Math.Clamp(shift / 100, MotionBalances.MinShift, MotionBalances.MaxShiftLimit) } } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The farthest the hips move. Ctrl+click to type.");

        TimelineUi.Fact("Sensitivity");
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo("##balance-sensitivity", balance.Sensitivity.ToString()))
        {
            if (combo.Success)
            {
                foreach (var option in Sensitivities)
                    if (ImGui.Selectable(option.ToString(), option == balance.Sensitivity) && option != balance.Sensitivity)
                        current.Commit(MotionEdits.Replace(current.Document, layer with { Balance = balance with { Sensitivity = option } }));
            }
            else if (ComboWheel.Scroll(Sensitivities, balance.Sensitivity, out var picked))
                current.Commit(MotionEdits.Replace(current.Document, layer with { Balance = balance with { Sensitivity = picked } }));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("How readily a foot counts as down. Strict holds less, Loose more.");
    }

    /// <summary>
    /// Under the layer's rows: where each foot is down (<paramref name="facts"/>, null while they are worked out), or that the
    /// skeleton has no hips or feet to work on.
    /// </summary>
    public static void DrawFacts(IReadOnlyDictionary<string, int> index, IReadOnlyList<string>? facts)
    {
        if (!MotionBalances.HasBones(index))
        {
            Widgets.HintWrapped("This skeleton has no hips or feet.");
            return;
        }
        if (facts == null)
        {
            Widgets.MutedWrapped("Finding the feet");
            return;
        }
        foreach (var line in facts)
            Widgets.MutedWrapped(line);
    }

    /// <summary> A labelled tick that commits the changed balance. </summary>
    private static void Tick(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, bool value, Func<bool, MotionBalance> with)
    {
        TimelineUi.Fact(label);
        var on = value;
        if (ImGui.Checkbox(id, ref on) && on != value)
            current.Commit(MotionEdits.Replace(current.Document, layer with { Balance = with(on) }));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
