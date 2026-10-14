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
    /// <summary>
    /// How long sound <paramref name="ev"/> plays: its longest alternative. Null while its file is unread,
    /// or when it can't be read; with <paramref name="read"/> a read starts.
    /// </summary>
    private double? AudioSeconds(TimelineDocument doc, TimelineEvent ev, bool read)
    {
        if (ev.Magic != "C063" || TimelineSoundKey.Of(ev) is not { } key)
            return null;
        var shown = read ? source is { } loaded ? ShownSound(doc, key, loaded) : null : ShownIfRead(doc, key);
        return shown is { Seconds.IsEmpty: false } ? shown.Seconds.Max() : null;
    }

    /// <summary>Whether sound <paramref name="ev"/> plays across a cut, once its file is read.</summary>
    private bool CrossesCut(TimelineDocument doc, TimelineEvent ev, bool read) =>
        doc.Cut && AudioSeconds(doc, ev, read) is { } seconds && TimelineSoundCuts.Crosses(doc, ev, seconds);

    /// <summary>
    /// Why saving or previewing waits for the sounds across a cut: one still being read, or ones without
    /// a choice. A sound whose file can't be read plays whole.
    /// </summary>
    private string? SoundCutMissing(TimelineDocument document)
    {
        if (!document.Cut || source is not { } loaded || document.Events.IsDefault)
            return null;
        // Only a sound that starts before the last cut can play across one.
        var cuts = CutEdits.CutsOf(document);
        var last = cuts.Source > cuts.Parts[^1].To ? document.Length
            : cuts.Joins.Length > 0 ? cuts.PartEnd(cuts.Joins.Length - 1, document.Length) : -1;
        var undecided = 0;
        foreach (var ev in document.Events)
        {
            if (ev.Magic != "C063" || ev.Start >= last || document.SoundCuts.ContainsKey(ev.Key) || TimelineSoundKey.Of(ev) is not { } key)
                continue;
            var read = SoundFor(key.Path, loaded);
            if (read.Bytes == null)
            {
                if (read.Error == null)
                    return "Reading the sound files.";
                continue;
            }
            if (CrossesCut(document, ev, true))
                undecided++;
        }
        return undecided == 0 ? null : TimelineWindowViews.SoundCutsFooter(undecided);
    }

    /// <summary>In a sound's panel, when it plays across a cut: cut its audio with the motion, or leave it whole.</summary>
    private void DrawSoundCutChoice(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        var doc = current.Document;
        if (!CrossesCut(doc, ev, true))
            return;
        Fact("Across a cut");
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
