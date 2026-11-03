using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline window's play controls: Play/Pause, From start and Stop. Each acts on every actor with a clip open at once
/// (<paramref name="transport"/>); <paramref name="preview"/> is this workspace's, which says whether anything can play.
/// Live, Preview and the time are in the window's header.
/// </summary>
internal sealed class PlayBar(TimelinePreview preview, ITimelineTransport transport)
{
    /// <summary> Draws the controls on one line. <paramref name="id"/> starts every control's id. </summary>
    public void Draw(string id)
    {
        var available = preview.PlaybackAvailable(out _);
        var shown = transport.AnyShown;
        var playing = transport.Playing;

        if (Widgets.IconButton(id + "-play", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                !shown ? "Press Preview first." : playing ? "Pause every actor" : "Play every actor", shown && available))
            transport.PlayPause();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton(id + "-start", FontAwesomeIcon.StepBackward, shown ? "Play again from the start" : "Press Preview first.",
                shown && available))
            transport.FromStart();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton(id + "-stop", FontAwesomeIcon.Stop, "Stop the preview and put the actors back", transport.AnyActive))
            transport.Stop();
    }

    /// <summary> Space: Play or Pause for every actor, while the bar's button can be pressed. </summary>
    public void PlayKey()
    {
        if (transport.AnyShown && preview.PlaybackAvailable(out _))
            transport.PlayPause();
    }
}
