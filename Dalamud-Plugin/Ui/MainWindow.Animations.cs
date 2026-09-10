using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private AnimationEditService? animations;
    private string? animationError;
    private AnimationCapture? animationSelection;
    private readonly HashSet<PoseBoneId> animationBones = [];
    private PoseComponents animationComponents = PoseComponents.All;
    private AnimationDestination animationDestination = AnimationDestination.NewMod;
    private bool animationStartup;
    private string animationModName = "";
    internal void AttachAnimations(AnimationEditService? service, string? error)
    { animations = service; animationError = error; }

    private void SelectAnimation(AnimationCapture capture)
    {
        animationSelection = capture;
        animationBones.Clear();
        animationBones.UnionWith(capture.Pose.Bones.Where(b => b.Id.Partial == capture.Clip.Partial && b.Id.Slot == 0).Select(b => b.Id));
        animationComponents = PoseComponents.All;
        animationStartup = false;
        animationModName = "IE Animation " + DateTime.Now.ToString("yyyyMMdd HHmmss");
    }

    private void DrawAnimations()
    {
        if (animations == null) { ImGui.TextWrapped(animationError ?? "Animation integration is unavailable."); return; }
        ImGui.Spacing();
        ImGui.TextWrapped(animations.Observer.Status);
        ImGui.TextDisabled("Emotes and idles · current player variant");
        var history = animations.Observer.History;
        if (animationSelection != null && animationSelection.ActorId != animations.Observer.Actor)
        { animationSelection = null; animationBones.Clear(); }
        var width = Math.Max(190, Math.Min(300, ImGui.GetContentRegionAvail().X * 0.32f));
        if (ImGui.BeginChild("##animation-history", new Vector2(width, 300), true))
        {
            ImGui.TextUnformatted("Playing and recent");
            if (history.IsEmpty) ImGui.TextWrapped("Play an emote or idle to capture it here.");
            foreach (var item in history)
            {
                ImGui.PushID(item.Id);
                var label = (item.Playing ? "• " : "") + item.DisplayName + " / " + item.Clip.Name;
                if (ImGui.Selectable(label, animationSelection?.Id == item.Id)) SelectAnimation(item);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Clip.GamePath + "\n" + item.CapturedUtc.ToLocalTime().ToString("G"));
                ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.SameLine();
        if (ImGui.BeginChild("##animation-capture", new Vector2(0, 300), true))
        {
            if (animationSelection is not { } selected) ImGui.TextWrapped("Select a clip to inspect its captured offsets. Selection stays fixed while playback continues.");
            else
            {
                ImGui.TextUnformatted(selected.DisplayName + " / " + selected.Clip.Name);
                ImGui.TextWrapped(selected.Clip.GamePath);
                ImGui.TextDisabled($"Binding {selected.Clip.BindingIndex} · partial {selected.Clip.Partial} · timeline {selected.Clip.Timeline}");
                ImGui.TextWrapped("Skeleton: " + selected.Clip.SkeletonPath);
                ImGui.TextWrapped("Source: " + selected.Sources.First(s => s.GamePath == selected.Clip.GamePath).ResolvedPath);
                ImGui.TextUnformatted("Collection: " + selected.CollectionName);
                ImGui.TextUnformatted("Pose captured: " + selected.Pose.CapturedUtc.ToLocalTime().ToString("G"));
                ImGui.BeginDisabled(animations.Busy);
                if (ImGui.Button("Refresh capture"))
                {
                    var latest = history.FirstOrDefault(c => c.Id == selected.Id);
                    animations.Refresh(latest ?? selected, refreshed =>
                    {
                        if (!ReferenceEquals(animationSelection, selected)) return;
                        animationSelection = refreshed;
                        animationBones.IntersectWith(refreshed.Pose.Bones.Select(b => b.Id));
                    });
                }
                ImGui.EndDisabled();
                ImGui.Separator();
                foreach (var bone in selected.Pose.Bones.Where(b => b.Id.Slot == 0 && b.Id.Partial == selected.Clip.Partial))
                {
                    ImGui.PushID(bone.Id.Name);
                    var enabled = animationBones.Contains(bone.Id);
                    if (ImGui.Checkbox(bone.Id.Name, ref enabled))
                    { if (enabled) animationBones.Add(bone.Id); else animationBones.Remove(bone.Id); }
                    ImGui.SameLine(); ImGui.TextDisabled($"{bone.Stacks.Length} stack(s)");
                    if (ImGui.TreeNode("Details"))
                    {
                        for (var i = 0; i < bone.Stacks.Length; i++)
                        {
                            var stack = bone.Stacks[i];
                            ImGui.TextWrapped($"{i + 1}. Position {stack.Position}; rotation {stack.Rotation}; scale delta {stack.Scale}");
                            ImGui.TextWrapped("Propagate: " + stack.Propagate);
                            if (stack.Ik.Enabled)
                            {
                                ImGui.TextWrapped((stack.Ik.Type == 0 ? "CCD" : "Two-joint") + " IK" + (stack.Ik.EnforceConstraints ? " · enforce constraints" : ""));
                                var count = stack.Ik.Type == 0 ? stack.Ik.Depth + 1 : stack.Ik.First + 1;
                                ImGui.TextWrapped("Solver joints (included with Position): " + string.Join(", ", bone.IkChain.Take(count)));
                            }
                        }
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
            }
        }
        ImGui.EndChild();
        if (animationSelection is { } capture)
        {
            foreach (var component in new[] { PoseComponents.Position, PoseComponents.Rotation, PoseComponents.Scale })
            {
                var enabled = animationComponents.HasFlag(component);
                if (ImGui.Checkbox(component.ToString(), ref enabled))
                    animationComponents = enabled ? animationComponents | component : animationComponents & ~component;
                ImGui.SameLine();
            }
            ImGui.NewLine();
            if (capture.Startup is { } startup)
            { ImGui.Checkbox("Also edit startup", ref animationStartup); ImGui.SameLine(); ImGui.TextDisabled(startup.Name); }
            else ImGui.TextDisabled("No unique startup relationship identified.");
            if (ImGui.RadioButton("Create new mod", animationDestination == AnimationDestination.NewMod)) animationDestination = AnimationDestination.NewMod;
            ImGui.SameLine();
            if (ImGui.RadioButton("Replace in-place", animationDestination == AnimationDestination.InPlace)) animationDestination = AnimationDestination.InPlace;
            string? blocked = capture.UnavailableReason;
            if (animationDestination == AnimationDestination.NewMod)
            {
                ImGui.SetNextItemWidth(Math.Min(400, ImGui.GetContentRegionAvail().X));
                ImGui.InputText("Mod name", ref animationModName, 128);
                blocked ??= capture.PackagingError;
                ImGui.TextDisabled("Required companion clips and effects are copied with the player variant.");
            }
            else
            {
                var paths = new[] { capture.Clip.GamePath, animationStartup ? capture.Startup?.GamePath : null }.OfType<string>();
                if (paths.Any(p => !capture.Sources.Any(s => s.GamePath == p && AnimationResources.CanReplace(s))))
                    blocked ??= "This clip or startup has no writable Penumbra source. Choose Create new mod.";
            }
            if (blocked != null) ImGui.TextWrapped(blocked);
            ImGui.BeginDisabled(animations.Busy || blocked != null || animationBones.Count == 0 || animationComponents == PoseComponents.None);
            if (ImGui.Button("Edit animation")) animations.Edit(new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination,
                animationModName.Trim(), animationStartup, animationBones.ToImmutableHashSet(), animationComponents));
            ImGui.EndDisabled();
        }
        if (animations.Busy)
        {
            ImGui.SameLine(); ImGui.BeginDisabled(!animations.CanCancel);
            if (ImGui.Button("Cancel")) animations.Cancel();
            ImGui.EndDisabled();
        }
        ImGui.TextWrapped(animations.Status);
        if (ImGui.CollapsingHeader("Recovery"))
            foreach (var journal in animations.Recovery)
            {
                ImGui.PushID(journal.Id.ToString());
                ImGui.TextWrapped($"{journal.CreatedUtc.ToLocalTime():g} · {journal.Request.Capture.DisplayName} · {journal.State}");
                ImGui.TextWrapped(journal.Message);
                ImGui.BeginDisabled(animations.Busy);
                ImGui.BeginDisabled(!journal.OffsetsCleared && journal.PoseClearOutcome != "Pending");
                if (ImGui.Button("Restore live offsets")) animations.RestoreOffsets(journal);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restore the captured live adjustments. The baked animation remains active until Undo edit.");
                ImGui.SameLine();
                if (ImGui.Button("Undo edit")) animations.Undo(journal);
                ImGui.EndDisabled(); ImGui.PopID();
            }
    }
}
