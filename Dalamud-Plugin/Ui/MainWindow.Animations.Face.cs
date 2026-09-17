using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private string animationFaceMotion = "";
    private string? animationFaceKey;

    private void DrawFaceAction(AnimationCapture capture)
    {
        if (animationStartupSelected) return;

        ImGui.Separator();
        ImGui.TextUnformatted("Facial expression");
        ImGui.TextWrapped("Swap the expression this animation plays. Only the animation's own reference to it " +
            "changes; the expression itself is a game file and is not copied into the mod.");

        ImGui.BeginDisabled(animations!.Busy);
        if (ImGui.Button(animations.FaceCapture == capture.Id ? "Read again" : "Read this animation's expression"))
            animations.DiscoverFaces(capture);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads which expression this animation names, then the game's facial timelines to find what else it could name.");

        // Results belong to whichever capture was read; another selection must not
        // inherit them.
        if (animations.FaceCapture != capture.Id)
        { ImGui.TextDisabled("Read this animation to choose an expression."); return; }
        if (animations.FaceError != null) { ImGui.TextWrapped(animations.FaceError); return; }
        if (animations.Faces.IsEmpty || animations.FaceCurrent.Length == 0)
        { ImGui.TextDisabled("Read this animation to choose an expression."); return; }

        var current = animations.FaceCurrent;
        var key = capture.Id + ":" + current;
        if (animationFaceKey != key) { animationFaceKey = key; animationFaceMotion = current; }
        ImGui.TextDisabled("Currently plays: " +
            (animations.Faces.FirstOrDefault(clip => clip.Motion == current) is { } playing
                ? $"{playing.Name} ({current})" : current));

        var chosen = animations.Faces.FirstOrDefault(clip => clip.Motion == animationFaceMotion);
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Expression", chosen?.Name ?? animationFaceMotion))
        {
            foreach (var clip in animations.Faces)
                if (ImGui.Selectable($"{clip.Name} ({clip.Motion})", clip.Motion == animationFaceMotion))
                    animationFaceMotion = clip.Motion;
            ImGui.EndCombo();
        }

        string? blocked = capture.UnavailableReason;
        if (animationDestination == AnimationDestination.NewMod)
        {
            blocked ??= capture.PackagingError;
            ImGui.SetNextItemWidth(Math.Min(400, ImGui.GetContentRegionAvail().X));
            ImGui.InputText("Expression mod name", ref animationModName, 128);
        }
        else if (!capture.Sources.Any(s => s.GamePath == capture.Clip.GamePath && AnimationResources.CanReplace(s)))
            blocked ??= "This animation has no writable Penumbra source. Choose Create new mod.";
        if (animationFaceMotion == current) blocked ??= "Choose a different expression to attach.";
        if (blocked != null) ImGui.TextWrapped(blocked);

        ImGui.BeginDisabled(animations.Busy || blocked != null);
        if (ImGui.Button("Attach expression"))
            animations.AttachFace(new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination,
                animationModName.Trim(), false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
                AnimationOperation.AttachFace,
                FaceOptions: new AnimationFaceOptions(current, animationFaceMotion, chosen?.Name ?? animationFaceMotion)));
        ImGui.EndDisabled();
    }
}
