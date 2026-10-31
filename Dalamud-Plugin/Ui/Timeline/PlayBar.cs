using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The play bar both the Timeline and the Bones window draw: Preview, Live, Play/Pause, From start, Stop, the playhead's
/// time and Speed. Every call goes to <paramref name="preview"/>, which runs the preview and Live.
/// </summary>
internal sealed class PlayBar(TimelineSession session, TimelinePreview preview)
{
    // The time text, built again only when the playhead moves to another frame.
    private int timeFrame = -1;
    private string timeText = string.Empty;

    /// <summary>
    /// Draws the bar on one line. <paramref name="repair"/> is the standard skeleton a preview repairs the clip onto;
    /// <paramref name="id"/> starts every control's id, so each window draws its own bar.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded, SkeletonStandard? repair, string id)
    {
        var available = preview.PlaybackAvailable(out _);
        var shown = preview.Fingerprint != null;
        var active = session.Playback.Active;
        var paused = active && session.Playback.Paused;

        var blocked = preview.PreviewBlocked();
        using (ImRaii.PushId(id + "-preview"))
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Video, "Preview", null, null, null, null) && blocked == null)
                preview.StartPreview(current, loaded, repair);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Build the edited clip, redraw your GPose character with it and play it. Nothing is saved.");

        ImGui.SameLine(0, Theme.Scaled(2));
        preview.DrawLiveToggle(current, loaded, id + "-live");

        ImGui.SameLine(0, Theme.Scaled(8));
        if (Widgets.IconButton(id + "-play", active && !paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                !shown ? "Press Preview first." : active ? paused ? "Resume" : "Pause" : "Play the preview", shown && available))
            preview.TogglePlay();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton(id + "-start", FontAwesomeIcon.StepBackward, shown ? "Play again from the start" : "Press Preview first.",
                shown && available))
            preview.Replay(current);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton(id + "-stop", FontAwesomeIcon.Stop, "Stop the preview and put your character back",
                shown || active || session.Preview.Active || preview.Building))
            preview.Stop();

        if (current.Playhead != timeFrame)
        {
            timeFrame = current.Playhead;
            timeText = TimelineWindowViews.FrameText(timeFrame);
        }
        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(timeText);

        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.SetNextItemWidth(Theme.Scaled(56));
        var speed = preview.SpeedPercent;
        if (ImGui.DragFloat(id + "-speed", ref speed, 1, 10, 200, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
            preview.SpeedPercent = speed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Playback speed of the preview. Drag, or Ctrl+click to type.");
    }
}
