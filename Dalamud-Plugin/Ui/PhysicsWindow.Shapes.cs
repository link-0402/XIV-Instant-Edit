using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    private string newShapeName = string.Empty;
    private string newShapeStart = string.Empty;
    private string newShapeEnd = string.Empty;
    private float newShapeSize = 4f;
    private bool newShapeSphere;
    private string boneFilter = string.Empty;

    private void DrawShapes(PhysicsDocument current)
    {
        var collision = current.File.Collision;
        Widgets.SectionHeader("Collision shapes", "areas bump into the ones they tick");
        Widgets.MutedWrapped("Sizes and places are in centimetres, in the space of the bone each end sits on.");
        ImGui.Spacing();
        for (var i = 0; i < collision.Capsules.Count; i++)
        {
            using var id = ImRaii.PushId($"capsule-{i}");
            var shape = collision.Capsules[i];
            if (ShapeHeader(current, shape.Name.Text, $"capsule from {shape.StartBone.Text} to {shape.EndBone.Text}", () => collision.Capsules.Remove(shape)))
                break;
            Drag(current, "Radius", shape.Radius * 100, 0.05f, 0, 100, "%.1f cm", value => shape.Radius = value / 100);
            Lights(PhysicsOverlayPart.Shape, shape.Name.Text);
            Drag3(current, "Start", shape.StartOffset * 100, value => shape.StartOffset = value / 100);
            Lights(PhysicsOverlayPart.Shape, shape.Name.Text);
            Drag3(current, "End", shape.EndOffset * 100, value => shape.EndOffset = value / 100);
            Lights(PhysicsOverlayPart.Shape, shape.Name.Text);
            ImGui.Spacing();
        }
        for (var i = 0; i < collision.Spheres.Count; i++)
        {
            using var id = ImRaii.PushId($"sphere-{i}");
            var shape = collision.Spheres[i];
            if (ShapeHeader(current, shape.Name.Text, $"sphere on {shape.Bone.Text}", () => collision.Spheres.Remove(shape)))
                break;
            Drag(current, "Radius", shape.Thickness * 100, 0.05f, 0, 100, "%.1f cm", value => shape.Thickness = value / 100);
            Lights(PhysicsOverlayPart.Shape, shape.Name.Text);
            Drag3(current, "Place", shape.Offset * 100, value => shape.Offset = value / 100);
            Lights(PhysicsOverlayPart.Shape, shape.Name.Text);
            ImGui.Spacing();
        }
        var others = collision.Ellipsoids.Count + collision.NormalPlanes.Count + collision.ThreePointPlanes.Count;
        if (others > 0)
            Widgets.HintWrapped($"{others} other shape{(others == 1 ? "" : "s")} (ellipsoids or planes) are kept as they are.");

        ImGui.Separator();
        DrawNewShape(current);
    }

    /// <summary> A shape's name and what it is, with a delete button when nothing uses it. Returns true when it was deleted. </summary>
    private bool ShapeHeader(PhysicsDocument current, string name, string detail, Action remove)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, PhysicsShapeNames.Label(current.File.Collision, name, target?.Character.Skeleton));
        Lights(PhysicsOverlayPart.Shape, name);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, detail);
        Lights(PhysicsOverlayPart.Shape, name);
        var labels = PhysicsAreas.Labels(current.File);
        var users = Enumerable.Range(0, current.File.Simulators.Count).Where(s => UsesShape(current.File.Simulators[s], name)).Select(s => labels[s]).ToList();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(users.Count == 0 ? Theme.Hint : Theme.Label, users.Count == 0 ? "unused" : $"used by {string.Join(", ", users)}");
        if (users.Count > 0)
            return false;
        var button = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - button));
        if (!Widgets.GhostIconButton("##remove-shape", FontAwesomeIcon.Trash, "Delete this shape", new Vector2(button)))
            return false;
        Change(current, remove);
        return true;
    }

    private static bool UsesShape(PhybSimulator simulator, string name)
        => simulator.Collisions.Concat(simulator.CollisionConnectors).Concat(simulator.Chains.SelectMany(c => c.Collisions)).Any(r => r.Name.Text == name) ||
           simulator.PostAlignments.Any(a => a.CollisionName.Text == name);

    private void Drag(PhysicsDocument current, string label, float value, float speed, float min, float max, string format, Action<float> set)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(140));
        var changed = ImGui.DragFloat($"##{label}", ref value, speed, min, max, format);
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            set(value);
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
    }

    private void Drag3(PhysicsDocument current, string label, Vector3 value, Action<Vector3> set)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3($"##{label}", ref value, 0.05f, -200, 200, "%.1f");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            set(value);
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
    }

    private void DrawNewShape(PhysicsDocument current)
    {
        Widgets.SectionHeader("New shape");
        if (target is not { } found)
        {
            Widgets.Hint("Your character isn't found yet.");
            return;
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Kind");
        ImGui.SameLine(LabelWidth);
        if (ImGui.RadioButton("Capsule", !newShapeSphere))
            newShapeSphere = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Sphere", newShapeSphere))
            newShapeSphere = true;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Name");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(200));
        ImGui.InputTextWithHint("##shape-name", "such as forearm_l", ref newShapeName, PhybName.MaxLength);

        BonePicker(found.Character.Skeleton, newShapeSphere ? "On bone" : "From bone", ref newShapeStart);
        if (!newShapeSphere)
            BonePicker(found.Character.Skeleton, "To bone", ref newShapeEnd);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Radius");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(140));
        ImGui.DragFloat("##shape-size", ref newShapeSize, 0.05f, 0.1f, 100, "%.1f cm");

        var name = newShapeName.Trim();
        var problem = !PhybName.IsValid(name) || name.Length == 0 ? "Give it a name of letters, digits or symbols."
            : current.File.Collision.Names.Any(existing => existing.Text == name) ? "That name is taken."
            : !found.Character.Skeleton.HasBody(newShapeStart) ? "Pick the bone it sits on."
            : !newShapeSphere && !found.Character.Skeleton.HasBody(newShapeEnd) ? "Pick the bone it reaches to."
            : null;
        using (ImRaii.Disabled(problem != null))
        {
            if (ImGui.Button("Add shape"))
                Change(current, () =>
                {
                    if (newShapeSphere)
                        current.File.Collision.Spheres.Add(new PhybSphere
                        {
                            Name = PhybName.From(name), Bone = PhybName.From(newShapeStart), Thickness = newShapeSize / 100,
                        });
                    else
                        current.File.Collision.Capsules.Add(new PhybCapsule
                        {
                            Name = PhybName.From(name), StartBone = PhybName.From(newShapeStart), EndBone = PhybName.From(newShapeEnd),
                            Radius = newShapeSize / 100,
                        });
                    if (current.File.DataType == 2)
                        current.File.DataType = 3;
                    newShapeName = string.Empty;
                });
        }
        if (problem != null)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Hint, problem);
        }
    }

    private void BonePicker(PhysicsSkeleton skeleton, string label, ref string bone)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(220));
        using var combo = ImRaii.Combo($"##{label}", bone.Length == 0 ? "Pick a bone" : AnimationPresentation.BoneName(bone), ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
        {
            var current = bone;
            if (ComboWheel.Steps() is var steps and not 0 && skeleton.Body.Length > 0)
                bone = ComboWheel.Pick(skeleton.Body, candidate => candidate.Name == current, steps).Name;
            return;
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##filter", "Search", ref boneFilter, 64);
        foreach (var candidate in skeleton.Body)
        {
            var name = AnimationPresentation.BoneName(candidate.Name);
            if (boneFilter.Length > 0 && !name.Contains(boneFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable(name, candidate.Name == bone))
                bone = candidate.Name;
        }
    }
}
