using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// How a pose is keyed, shared by Key a pose and the pose browser: the whole body (but the groups physics and
/// gear move, unless ticked) or the selected bones, whether the bones the pose moves keep their positions, and
/// whether the body is moved so the feet stand on the ground. Then the keying itself.
/// </summary>
internal sealed class PoseKeyOptions(BonePosing posing)
{
    /// <summary>What a pose keys: its bones, and whether positions and the ground go with them.</summary>
    public readonly record struct Choice(List<string> Bones, bool Positions, bool Ground, bool SelectedOnly);

    // A whole-body pose leaves these groups out unless they are ticked: physics and gear move them on their own.
    private static readonly string[] GroupsLeftOut = ["Hair", "Skirt", "Armor and weapons"];

    private bool selectedOnly;
    // Left-out groups ticked back in.
    private readonly HashSet<string> groupsKept = new(StringComparer.Ordinal);
    // Whether positions are keyed; null until chosen for the pose, which then decides.
    private bool? positions;
    // Whether the body is moved so the feet stand on the ground; null until chosen.
    private bool? ground;

    /// <summary>Another pose waits: whether positions are keyed follows it again until chosen.</summary>
    public void NewPose() => positions = null;

    /// <summary>What keying <paramref name="import"/> keys, as chosen.</summary>
    public Choice For(PoseImport import, SkeletonDescription skeleton, TimelineDocument doc)
    {
        var selected = selectedOnly && posing.Selection.Count > 0;
        var bones = PoseEdits.PoseBones(Available(skeleton, import), selected ? posing.Selection.Bones : null, LeftOut());
        return new Choice(bones, positions ?? !import.OtherProportions, ground ?? Grounds(doc), selected);
    }

    // On by default when the clip starts from a still pose, where nothing else stands it up.
    private bool Grounds(TimelineDocument doc) => doc.Replacement is { Still: true };

    private static List<string> Available(SkeletonDescription skeleton, PoseImport? import) =>
        [.. skeleton.Bones.Select(bone => bone.Name).Where(bone => import?.Local.ContainsKey(bone) ?? true)];

    private IEnumerable<PoseGroup> LeftOut() =>
        PoseMap.Groups.Where(group => GroupsLeftOut.Contains(group.Name) && !groupsKept.Contains(group.Name));

    /// <summary>
    /// The choices for <paramref name="import"/>, or before a pose is read for any pose on <paramref name="skeleton"/>;
    /// <paramref name="wide"/> puts them on fewer lines. True when one changed.
    /// </summary>
    public bool Draw(PoseImport? import, SkeletonDescription skeleton, TimelineDocument doc, bool wide)
    {
        var changed = false;
        var available = Available(skeleton, import);
        if (ImGui.RadioButton("Whole body###pose-whole", !selectedOnly))
        {
            changed |= selectedOnly;
            selectedOnly = false;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(posing.Selection.Count == 0))
        {
            if (ImGui.RadioButton($"Selected bones ({posing.Selection.Count})###pose-selected", selectedOnly && posing.Selection.Count > 0))
            {
                changed |= !selectedOnly;
                selectedOnly = true;
            }
        }
        var selected = selectedOnly && posing.Selection.Count > 0;
        if (!selected)
        {
            var groups = PoseMap.Groups.Where(group => GroupsLeftOut.Contains(group.Name) && available.Any(group.Contains)).ToList();
            if (groups.Count > 0)
            {
                if (wide)
                    ImGui.SameLine(0, Theme.Scaled(16));
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "Also");
                foreach (var group in groups)
                {
                    ImGui.SameLine(0, Theme.Gap);
                    var kept = groupsKept.Contains(group.Name);
                    if (ImGui.Checkbox($"{group.Name}##pose-also", ref kept))
                    {
                        changed = true;
                        if (kept)
                            groupsKept.Add(group.Name);
                        else
                            groupsKept.Remove(group.Name);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Physics and gear move these on their own. Tick to key them too.");
                }
            }
        }

        // Positions: off for a pose made on a body of other proportions, unless asked for.
        var moving = import == null ? -1 : PoseEdits.PoseBones(available, selected ? posing.Selection.Bones : null, LeftOut()).Count(import.Moved.Contains);
        var keyed = positions ?? import is not { OtherProportions: true };
        if (wide)
            ImGui.SameLine(0, Theme.Scaled(16));
        using (ImRaii.Disabled(moving == 0))
        {
            if (ImGui.Checkbox((moving < 0 ? "Move bones" : moving == 1 ? "Move 1 bone" : $"Move {moving} bones") + "###pose-positions", ref keyed))
            {
                positions = keyed;
                changed = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(moving == 0
                ? "The pose moves none of these bones; it only turns them."
                : "Key the positions of the bones the pose moves, like where the body is. The others only turn.");

        var stands = ground ?? Grounds(doc);
        if (wide)
            ImGui.SameLine(0, Theme.Scaled(16));
        if (ImGui.Checkbox("Feet on the ground###pose-ground", ref stands))
        {
            ground = stands;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Move the body up or down so the lowest foot stands as low as in the clip, after bent legs lift it.");
        if (import is { OtherProportions: true })
            Widgets.HintWrapped("The pose was made on a body with other proportions. Moving bones would reshape this one.");
        return changed;
    }

    /// <summary>
    /// <paramref name="doc"/> with <paramref name="import"/> keyed in <paramref name="layer"/> at <paramref name="frame"/> as
    /// <paramref name="choice"/> says, over <paramref name="under"/>, the pose the layers before leave there.
    /// </summary>
    public TimelineDocument Key(TimelineDocument doc, string layer, int frame, PoseImport import, Choice choice, SkeletonDescription skeleton,
        MotionTransform[] under)
    {
        var index = posing.BoneTree(skeleton).Index;
        var next = PoseEdits.KeyPose(doc, layer, frame, import, choice.Bones, choice.Positions,
            bone => index.TryGetValue(bone, out var at) ? under[at] : null);
        if (choice.Ground && PoseEdits.Grounded(skeleton, under, import, choice.Bones, choice.Positions) is { } body)
            next = MotionEdits.SetKey(next, layer, frame, MotionHold.Body, body.ToBone(), true);
        return next;
    }
}
