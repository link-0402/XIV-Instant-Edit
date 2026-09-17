using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private float animationRetimeDuration;
    private string? animationRetimeKey;

    private void DrawRetimeAction(AnimationCapture capture)
    {
        if (animationStartupSelected) return;
        var clip = capture.Clip;
        if (clip.Duration <= 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Animation length");
            ImGui.TextDisabled("This animation's length is unknown, so it cannot be retimed.");
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Animation length");
        ImGui.TextWrapped("Make the animation run longer or shorter. Its timeline events - footsteps, sounds, " +
            "effects - are rescaled by the same amount so they stay in step with the motion.");

        var key = capture.Id + ":" + clip.Duration;
        if (animationRetimeKey != key) { animationRetimeKey = key; animationRetimeDuration = clip.Duration; }

        if (AnimationTimelineWidget.DrawLength("##animation-length", clip.Duration, animationRetimeDuration, !animations!.Busy) is { } dragged)
            animationRetimeDuration = dragged;
        ImGui.SetNextItemWidth(180);
        ImGui.InputFloat("New length (seconds)", ref animationRetimeDuration, 0.1f, 0.5f, "%.2f");
        animationRetimeDuration = Math.Clamp(animationRetimeDuration, 1f / 30, 600);
        var factor = animationRetimeDuration / clip.Duration;
        ImGui.TextDisabled($"Original {clip.Duration:0.00}s, new {animationRetimeDuration:0.00}s ({factor:0.00}x speed change).");

        string? blocked = capture.UnavailableReason;
        if (animationDestination == AnimationDestination.NewMod)
        {
            blocked ??= capture.PackagingError;
            ImGui.SetNextItemWidth(Math.Min(400, ImGui.GetContentRegionAvail().X));
            ImGui.InputText("Retimed mod name", ref animationModName, 128);
        }
        else if (!capture.Sources.Any(s => s.GamePath == clip.GamePath && AnimationResources.CanReplace(s)))
            blocked ??= "This animation has no writable Penumbra source. Choose Create new mod.";
        blocked ??= SkeletonBlock(clip);
        if (!AnimationPoseRules.ValidRetimeDuration(animationRetimeDuration))
            blocked ??= "Enter a length above 0 and at most 600 seconds.";
        if (Math.Abs(factor - 1) < 0.0005f) blocked ??= "Choose a different length.";
        if (blocked != null) ImGui.TextWrapped(blocked);
        ImGui.TextDisabled("Animations carrying root motion cannot be retimed yet; the edit will say so.");

        ImGui.BeginDisabled(animations.Busy || blocked != null);
        if (ImGui.Button("Retime animation"))
            animations.Edit(new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination,
                animationModName.Trim(), false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
                AnimationOperation.Retime, RetimeOptions: new AnimationRetimeOptions(animationRetimeDuration)));
        ImGui.EndDisabled();
    }
}
