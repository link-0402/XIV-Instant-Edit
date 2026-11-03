using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The play bar both the Timeline and the Bones window draw: Preview, Live, Play/Pause, From start, Stop and the playhead's
/// time. Each acts on every actor with a clip open at once (<paramref name="transport"/>); <paramref name="preview"/> is this
/// workspace's, which says why Preview waits.
/// </summary>
internal sealed class PlayBar(TimelineSession session, TimelinePreview preview, ITimelineTransport transport)
{
    // The time text, built again only when the playhead moves to another frame.
    private int timeFrame = -1;
    private string timeText = string.Empty;

    /// <summary>
    /// Draws the bar on one line. <paramref name="id"/> starts every control's id, so each window draws its own bar. The
    /// Timeline window leaves out Live and the time (<paramref name="live"/>, <paramref name="time"/>): its header has them.
    /// </summary>
    public void Draw(TimelineEditor current, string id, bool live = true, bool time = true)
    {
        var available = preview.PlaybackAvailable(out _);
        var shown = transport.AnyShown;
        var playing = transport.Playing;

        var blocked = preview.PreviewBlocked();
        using (ImRaii.PushId(id + "-preview"))
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Video, "Preview", null, null, null, null) && blocked == null)
                transport.Preview();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip((blocked ?? "Build the edited clips, redraw the GPose actors with them and play them. Nothing is saved.") + "\n" +
                             TimelinePreview.PreviewHints);

        if (live)
        {
            ImGui.SameLine(0, Theme.Scaled(2));
            DrawLive(current, id + "-live");
        }

        ImGui.SameLine(0, Theme.Scaled(8));
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

        if (!time)
            return;
        if (current.Playhead != timeFrame)
        {
            timeFrame = current.Playhead;
            timeText = TimelineWindowViews.FrameText(timeFrame);
        }
        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(timeText);
    }

    /// <summary> Space: Play or Pause for every actor, while the bar's button can be pressed. </summary>
    public void PlayKey()
    {
        if (transport.AnyShown && preview.PlaybackAvailable(out _))
            transport.PlayPause();
    }

    /// <summary> Live, icon and label, filled while it is on for any actor: the Bones window's. </summary>
    private void DrawLive(TimelineEditor current, string id)
    {
        var on = transport.AnyLive;
        var blocked = on ? null : transport.LiveBlocked;
        bool clicked;
        using (ImRaii.PushId(id))
        using (ImRaii.Disabled(blocked != null))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, on)
                   .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), on))
            clicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Bolt, "Live", null, null, null, null);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.LiveTip(on, transport.LiveActors, blocked, preview.LiveHint(current)));
        if (clicked && blocked == null && !session.ReadOnly)
            transport.ToggleLive();
    }
}
