using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The side panel beside the lanes: Properties, Source (the clip view: where the clip comes from, its length and the bones it
/// keeps), Edits (every saved edit with Undo) and Record (a live recording for Blender).
/// </summary>
internal sealed class SidePanel(TimelineSession session, PropertiesPanel properties, RecentEditsPage recentEdits,
    EffectLookEditor effectLooks, SoundInspector soundInspector, ClipView clipView)
{
    private enum SidePage : byte { Properties, Source, Edits, Record }

    private SidePage sidePage = SidePage.Properties;
    // A page the panel switches to on its next draw.
    private SidePage? sideRequest;
    // What was selected when the panel last looked: a new selection brings the properties back.
    private string? sideSelection;
    private Action? drawRecorder;

    /// <summary>The main window's live pose recorder, which sends to Blender through it; null until attached.</summary>
    public Action? Recorder => drawRecorder;

    /// <summary> Shows the properties on the next draw, as for a layer just added. </summary>
    public void ShowProperties() => sideRequest = SidePage.Properties;

    /// <summary> Clears the clip view's bones filter and leaves its bones-kept page, for another clip. </summary>
    public void ClearFilter() => clipView.ClearFilter();

    /// <summary>
    /// What the Timeline window borrows from the main window: Settings, where the skeleton library is
    /// rebuilt, and the live pose recorder, which sends to Blender through it.
    /// </summary>
    public void AttachTools(Action? settings, Action? recorderPanel)
    {
        clipView.OpenSettings = settings;
        drawRecorder = recorderPanel;
    }

    /// <summary>The side panel: a tab per page, the properties first.</summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        // A look or sound edit ends once its control lets go, even when another page is shown by then.
        effectLooks.EndEdit(current);
        soundInspector.EndEdit(current);
        var selection = $"{current.Primary}|{current.SelectedLayer}|{current.SelectedKey}";
        if (selection != sideSelection)
        {
            if (sideSelection != null && (current.Primary != null || current.SelectedLayer != null))
                sideRequest = SidePage.Properties;
            sideSelection = selection;
        }
        using var tabs = ImRaii.TabBar("##timeline-side", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs.Success)
            return;
        SideTab(SidePage.Properties, "Properties", "The selected event or bone layer.", () => properties.Draw(current));
        SideTab(SidePage.Source, "Source", "Where the animation comes from, its length and the bones it keeps.", () => clipView.Draw(current, loaded));
        DrawEditsTab();
        if (drawRecorder != null)
            SideTab(SidePage.Record, "Record", "Record a character's live pose for Blender.", drawRecorder);
        sideRequest = null;
    }

    private void SideTab(SidePage page, string label, string tooltip, Action draw)
    {
        var flags = sideRequest == page ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var open = ImGui.BeginTabItem(label, flags);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        if (!open)
            return;
        sidePage = page;
        DrawPage(draw);
        ImGui.EndTabItem();
    }

    /// <summary>The Edits tab, marked while an edit did not finish.</summary>
    public void DrawEditsTab()
    {
        var attention = session.Animations is { } service ? RecentEditsPage.RecoveryAttention(service) : 0;
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, attention > 0))
            open = ImGui.BeginTabItem(attention > 0 ? $"Edits ({attention})###edits" : "Edits###edits",
                sideRequest == SidePage.Edits ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(attention switch
            {
                0 => "Every saved edit, with Undo.",
                1 => "An edit did not finish and needs an undo.",
                _ => $"{attention} edits did not finish and need an undo.",
            });
        if (!open)
            return;
        sidePage = SidePage.Edits;
        DrawPage(recentEdits.Draw);
        ImGui.EndTabItem();
    }

    /// <summary>A page's body, which scrolls below the tabs.</summary>
    public static void DrawPage(Action draw)
    {
        using var body = ImRaii.Child("##side-page", Vector2.Zero, false);
        if (!body.Success)
            return;
        ImGui.Spacing();
        draw();
    }
}
