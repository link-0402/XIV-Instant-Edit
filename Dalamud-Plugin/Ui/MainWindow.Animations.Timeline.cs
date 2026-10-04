using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;

namespace InstantEdit.Ui;

/// <summary> The Timeline tab: a few facts about the selected clip and the button that opens it in the Timeline window. </summary>
public sealed partial class MainWindow
{
    private Action<AnimationCapture, bool>? openTimeline;

    internal void AttachTimeline(Action<AnimationCapture, bool>? open) => openTimeline = open;

    private void DrawAnimationTimeline(AnimationCapture capture)
    {
        var startup = animationStartupSelected && capture.Startup is not null;
        var clip = startup ? capture.Startup! : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        Widgets.HintWrapped("Place sounds, expressions, effects and props on the animation, change its length and preview it in GPose.");
        ImGui.Spacing();
        using (var facts = ImRaii.Table("##timeline-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                SetupFacts();
                Fact("Length");
                ImGui.TextUnformatted(clip.Duration > 0 ? clip.Duration.ToString("0.##", CultureInfo.InvariantCulture) + "s" : "Unknown");
                Fact("Mod");
                ImGui.TextUnformatted(file?.ModName ?? file?.ModDirectory ?? "Game Data");
            }
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(openTimeline is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Film, "Open timeline", null, null, null, null))
                openTimeline?.Invoke(capture, startup);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(openTimeline is null ? "The timeline editor is unavailable." : "Open this animation in the Timeline window.");
    }
}
