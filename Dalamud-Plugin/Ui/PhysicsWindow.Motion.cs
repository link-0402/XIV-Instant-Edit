using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

/// <summary>
/// Idle, walk and run in GPose: your posed character walks or runs in place, so the physics can be
/// judged while it moves. Played through the shared GPose playback, the one the Timeline preview uses.
/// </summary>
internal sealed partial class PhysicsWindow
{
    private const int MotionCheckDelayMs = 600;
    private const string NotPosing = "Enter GPose to walk or run your character in place.";

    private long motionCheckDue;

    private static float MotionsWidth => ImGui.GetFrameHeight() * 3 + ImGui.GetStyle().ItemSpacing.X * 2;

    private bool Moving => playback.Active && playback.Timeline is PhysicsMotions.Walk or PhysicsMotions.Run;

    private void DrawMotions()
    {
        var actor = service.PosedActor();
        var reason = !service.Posing ? NotPosing : actor is null ? "Your posed character isn't found." : null;
        var playing = playback.Active ? playback.Timeline : (ushort)0;
        if (MotionButton("##physics-idle", FontAwesomeIcon.StreetView, !playback.Active, reason == null, reason ?? "Back to idle") && playback.Active)
            playback.Stop();
        ImGui.SameLine();
        if (MotionButton("##physics-walk", FontAwesomeIcon.Walking, playing == PhysicsMotions.Walk, reason == null, reason ?? "Walk in place"))
            Move(PhysicsMotions.Walk, actor!.Value);
        ImGui.SameLine();
        if (MotionButton("##physics-run", FontAwesomeIcon.Running, playing == PhysicsMotions.Run, reason == null, reason ?? "Run in place"))
            Move(PhysicsMotions.Run, actor!.Value);
    }

    private void Move(ushort timeline, ushort actor)
    {
        if (!playback.Play(timeline, actorIndex: actor))
            Say(FeedbackSeverity.Warning, playback.Error);
    }

    /// <summary>
    /// A preview or save redraws your character. A walk should carry on, since the game keeps it on the
    /// character rather than its model; once after each redraw it starts again if it stopped.
    /// </summary>
    private void KeepMoving()
    {
        if (motionCheckDue == 0 || Environment.TickCount64 < motionCheckDue)
            return;
        motionCheckDue = 0;
        if (!Moving || service.PosedActor() is not { } actor)
            return;
        if (playback.BaseTimeline() is { } now && now != playback.Timeline)
            playback.Play(playback.Timeline, actorIndex: actor);
    }

    /// <summary> Closing the builder stands a walking or running character still again. </summary>
    private void StopMoving()
    {
        if (Moving)
            playback.Stop();
    }

    /// <summary> A square icon button, filled while its motion plays. </summary>
    private static bool MotionButton(string id, FontAwesomeIcon icon, bool active, bool enabled, string tooltip)
    {
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, active).Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), active))
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon, new Vector2(ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked && enabled;
    }
}
