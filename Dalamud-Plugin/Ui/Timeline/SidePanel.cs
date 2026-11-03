using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary> What the side panel shows, picked by the selection (see <see cref="SidePanel.ViewFor"/>). </summary>
internal enum SideView : byte
{
    /// <summary> Nothing selected: a line saying what selecting shows here. </summary>
    Clip,
    /// <summary> A part, join or the loop seam of the clip band: the cut panel. </summary>
    Cut,
    /// <summary> A bone layer: its settings. </summary>
    Layer,
    /// <summary> One event, or the events of one group: its properties. </summary>
    Event,
    /// <summary> Several events from different groups: the count, Duplicate and Delete. </summary>
    Several,
}

/// <summary>
/// The side panel beside the lanes: one view at a time, picked by the selection. A cut, a bone layer, an event (or one
/// group) and several events show their properties; nothing selected says so.
/// </summary>
internal sealed class SidePanel(PropertiesPanel properties, EffectLookEditor effectLooks, SoundInspector soundInspector)
{
    private Action? drawRecorder;

    /// <summary>The main window's live pose recorder, which sends to Blender through it; null until attached.</summary>
    public Action? Recorder => drawRecorder;

    /// <summary>The main window's live pose recorder, which the dock's Blender tab shows.</summary>
    public void AttachRecorder(Action? recorderPanel) => drawRecorder = recorderPanel;

    /// <summary>
    /// The view for <paramref name="current"/>'s selection: a cut first, then a bone layer, then one event or one group,
    /// then several events, else the clip.
    /// </summary>
    public static SideView ViewFor(TimelineEditor current)
    {
        if (current.SelectedCut != null)
            return SideView.Cut;
        if (current.SelectedLayerValue != null)
            return SideView.Layer;
        if (current.PrimaryEvent is not { } primary || current.Selected.Count == 0)
            return SideView.Clip;
        return current.Selected.Count > 1 && !OneGroup(current, primary) ? SideView.Several : SideView.Event;
    }

    /// <summary> Whether every selected event belongs to the primary's group, such as the parts of one prop. </summary>
    public static bool OneGroup(TimelineEditor current, TimelineEvent primary)
    {
        if (primary.Group is not { } group)
            return false;
        var events = current.Document.Events.IsDefault ? [] : current.Document.Events;
        foreach (var ev in events)
            if (current.IsSelected(ev.Key) && !string.Equals(ev.Group, group, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>The side panel: the selection's view, or a line while nothing is selected.</summary>
    public void Draw(TimelineEditor current)
    {
        // A look or sound edit ends once its control lets go, even when another view is shown by then.
        effectLooks.EndEdit(current);
        soundInspector.EndEdit(current);
        using var body = ImRaii.Child("##side-page", Vector2.Zero, false);
        if (!body.Success)
            return;
        var view = ViewFor(current);
        if (view == SideView.Clip)
        {
            ImGui.Spacing();
            Widgets.HintWrapped("Select a layer, an event or a cut to edit it here.");
        }
        else
            properties.Draw(current, view);
    }
}
