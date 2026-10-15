using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// Sounds that play across a cut of the motion: a warning mark on their bar, the choice in their panel
/// to cut their audio with the motion or leave it whole, and the save and preview held back until
/// each one has a choice.
/// </summary>
public sealed partial class TimelineWindow
{
    /// <summary>In a sound's panel, when it plays across a cut: cut its audio with the motion, or leave it whole.</summary>
    private void DrawSoundCutChoice(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        var doc = current.Document;
        if (!soundFiles.CrossesCut(doc, ev, true))
            return;
        TimelineUi.Fact("Across a cut");
        bool? choice = doc.SoundCuts.TryGetValue(ev.Key, out var cut) ? cut : null;
        var choices = TimelineWindowViews.SoundCutChoices;
        using (ImRaii.Disabled(!editable))
        {
            if (ImGui.RadioButton(choices[0], choice == true))
                current.SetSoundCut(ev.Key, true);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Saving cuts the same stretch out of the sound, so it stays in step with the motion.");
            ImGui.SameLine(0, Theme.Gap);
            if (ImGui.RadioButton(choices[1], choice == false))
                current.SetSoundCut(ev.Key, false);
        }
        if (choice == false)
            Widgets.HintWrapped(TimelineWindowViews.LeaveWholeNote);
        else if (choice == null)
            ImGui.TextColored(Theme.Warning, TimelineWindowViews.CrossesCutTip);
    }
}
