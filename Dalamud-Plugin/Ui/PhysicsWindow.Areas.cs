using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    private const string ImportPopup = "##physics-import";
    private float listWidth;
    private int chainChoice = -1;
    private int chainChoiceArea = -1;
    private bool bothSides = true;

    // ---- Layout ----

    private void DrawAreas(PhysicsDocument current, float height)
    {
        if (listWidth <= 0)
            listWidth = Theme.Scaled(240);
        using (var list = ImRaii.Child("##physics-areas", new Vector2(listWidth, height), true))
        {
            if (list.Success)
                DrawAreaList(current);
        }
        ImGui.SameLine(0, 0);
        Widgets.Splitter("##physics-split", ref listWidth, Theme.Scaled(170), Theme.Scaled(460), height);
        ImGui.SameLine(0, 0);
        using var editor = ImRaii.Child("##physics-editor", new Vector2(0, height), true);
        if (!editor.Success)
            return;
        if (selected == ShapesView)
            DrawShapes(current);
        else if (selected >= 0 && selected < current.File.Simulators.Count)
            DrawArea(current, selected);
        else
            Widgets.EmptyState(FontAwesomeIcon.HandPointer, "Pick an area", "Or add one your skeleton has bones for.");
        // A click's jump lands this frame or not at all (its group or row isn't drawn).
        jumpGroup = EditorGroup.None;
        jumpShape = null;
    }

    private void DrawAreaList(PhysicsDocument current)
    {
        var simulators = current.File.Simulators;
        Widgets.SectionHeader("Areas", simulators.Count.ToString());
        var all = Issues(current);
        var outsideChest = all.Where(issue => issue.Fix == PhysicsFix.ChestGroup).Select(issue => issue.Simulator).Distinct().ToList();
        if (outsideChest.Count > 1)
        {
            if (ImGui.SmallButton("Chest group for all body areas"))
                Change(current, () =>
                {
                    foreach (var index in outsideChest)
                        PhysicsValidation.Apply(current.File, PhysicsFix.ChestGroup, index);
                }, "Chest group for all body areas");
            Tip($"{outsideChest.Count} body areas aren't in the chest group, so tops that turn off breast physics don't stop them.");
        }
        var labels = PhysicsAreas.Labels(current.File);
        for (var i = 0; i < simulators.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var simulator = simulators[i];
            var on = !simulator.Excluded;
            if (ImGui.Checkbox("##on", ref on))
                Change(current, () => simulator.Excluded = !on);
            Tip(on ? "On. Untick to leave it out of the file." : "Off: left out of the file. Tick to use it again.");
            ImGui.SameLine(0, Theme.Gap);
            var info = PhysicsAreas.Classify(simulator);
            var level = all.Where(issue => issue.Simulator == i).Select(issue => (PhysicsIssueLevel?)issue.Level).Min();
            using (ImRaii.PushColor(ImGuiCol.Text, simulator.Excluded ? Theme.Inactive : level == PhysicsIssueLevel.Error ? Theme.Error : Theme.Text))
            {
                if (ImGui.Selectable($"{labels[i]}##area", selected == i))
                    selected = i;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{string.Join(", ", info.Bones.Distinct().Take(8))}{(info.Bones.Distinct().Count() > 8 ? " and more" : "")}\n" +
                                 $"Turns off with: {PhysicsGroups.Label(simulator.Group)}");
            if (level == PhysicsIssueLevel.Warning && !simulator.Excluded)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Warning, "⚠");
            }
        }

        ImGui.Spacing();
        Widgets.SectionHeader("Add an area");
        Tip("Areas your skeleton has bones for that this file doesn't move yet, the game's own ones such as the skirt or tail, " +
            "or one copied from another file.");
        if (target is { } found)
        {
            foreach (var kind in PhysicsTemplates.Addable(current.File, found.Character.Skeleton, found.MovedElsewhere))
            {
                using var id = ImRaii.PushId(kind.Key);
                var label = kind.Skeleton == "Vanilla" ? kind.Label : $"{kind.Label} ({kind.Skeleton})";
                if (ImGui.Selectable($"+ {label}##add"))
                    Change(current, () =>
                    {
                        if (current.AddArea(kind, found.Character.Skeleton) is { } index)
                            selected = index;
                    });
                Tip($"New physics for {string.Join(", ", PhysicsTemplates.Bones(kind, found.Character.Skeleton).Select(bone => bone.Name))}, " +
                    "measured on your skeleton. Tune it, then preview.");
            }
            // The game's own skirt, tail, earrings and hair, which have no template: copied from the race's file.
            if (service.GameFile(current.Race) is { } game)
            {
                var gameLabels = PhysicsAreas.Labels(game);
                foreach (var index in PhysicsTemplates.AddableFromGame(current.File, game))
                {
                    using var id = ImRaii.PushId($"game-{index}");
                    var label = gameLabels[index];
                    if (ImGui.Selectable($"+ {label}##add-game"))
                    {
                        Change(current, () => selected = current.ImportArea(game, index), $"Add {label}");
                        Say(FeedbackSeverity.Info, $"Added the game's {label.ToLowerInvariant()}.");
                    }
                    Tip($"The game's own physics for {string.Join(", ", PhysicsAreas.RootBones(game.Simulators[index]).Distinct())}, " +
                        "with the shapes it collides with.");
                }
            }
        }
        using (ImRaii.Disabled(Busy))
        {
            if (ImGui.Selectable("+ Import from a file##import"))
                OpenFileDialog(import: true);
        }
        Tip("Copy an area from another physics file, such as the butt from one preset and the chest from another.");
        ImGui.Spacing();
        ImGui.Separator();
        var shapes = current.File.Collision.Names.Count();
        if (ImGui.Selectable($"Collision shapes ({shapes})##shapes", selected == ShapesView))
            selected = ShapesView;
        Tip("The capsules and spheres areas can bump into, such as arms and legs.");
    }

    private void DrawImportPopup(PhysicsDocument current)
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(320, 0), Theme.Scaled(520, 480));
        using var popup = ImRaii.Popup(ImportPopup);
        if (!popup.Success)
            return;
        if (importFile is not { } file)
        {
            ImGui.CloseCurrentPopup();
            return;
        }
        ImGui.TextColored(Theme.Label, $"Import from {importName}");
        if (file.Simulators.Count == 0)
            Widgets.Hint("This file has no areas.");
        var moved = current.File.Simulators.Where(s => !s.Excluded).SelectMany(PhysicsAreas.RootBones).ToHashSet(StringComparer.Ordinal);
        var labels = PhysicsAreas.Labels(file);
        for (var i = 0; i < file.Simulators.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var info = PhysicsAreas.Classify(file.Simulators[i]);
            var label = labels[i];
            var overlap = info.Bones.Any(moved.Contains);
            if (ImGui.Selectable($"{label}{(overlap ? "  (you already move these)" : "")}##pick"))
            {
                var index = i;
                Change(current, () => selected = current.ImportArea(file, index), $"Import {label} from {importName}");
                Say(FeedbackSeverity.Info, overlap
                    ? $"Imported {label}. Turn your old area for these bones off, or both move them."
                    : $"Imported {label} from {importName}.");
                importFile = null;
                ImGui.CloseCurrentPopup();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join(", ", info.Bones.Distinct().Take(12)));
        }
        ImGui.Spacing();
        if (ImGui.Button("Cancel##physics-import-cancel"))
        {
            importFile = null;
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>
    /// A discrete change: one undo step, then the views and the automatic preview catch up. A name says
    /// what a change of many settings was, for undo and redo.
    /// </summary>
    private void Change(PhysicsDocument current, Action edit, string? name = null)
    {
        current.Checkpoint(name);
        edit();
        current.Changed();
        Edited(current);
    }

    // ---- One area ----

    private void DrawArea(PhysicsDocument current, int index)
    {
        var file = current.File;
        var simulator = file.Simulators[index];
        var info = PhysicsAreas.Classify(simulator);
        using var id = ImRaii.PushId($"area-{index}");

        ImGui.TextColored(Theme.Text, PhysicsAreas.Labels(file)[index]);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, $"{simulator.Chains.Count} chain{(simulator.Chains.Count == 1 ? "" : "s")}");
        var button = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - button));
        if (Widgets.GhostIconButton("##remove", FontAwesomeIcon.Trash, "Delete this area. Untick it in the list instead to only leave it out.", new Vector2(button)))
        {
            Change(current, () => current.RemoveArea(index));
            return;
        }
        Widgets.MutedWrapped(string.Join(", ", info.Bones.Distinct()));
        if (simulator.Excluded)
            Widgets.HintWrapped("Off: left out of the file. Tick it in the list to use it again.");

        DrawGroup(current, simulator);
        foreach (var issue in Issues(current).Where(issue => issue.Simulator == index))
            DrawIssue(current, issue);
        if (simulator.Chains.Count == 0)
        {
            Widgets.HintWrapped("This area has no chains, so it moves nothing.");
            return;
        }

        ImGui.Spacing();
        if (chainChoiceArea != index)
        {
            chainChoiceArea = index;
            chainChoice = -1;
        }
        DrawChainChoice(file, index);
        var targets = Targets(file, index);
        if (targets.Count == 0)
            return;
        SetSliderStart(current, targets);

        // One group per part on the character, in its colour there, so a click on a part lands on its settings.
        ImGui.Spacing();
        if (GroupHeader(EditorGroup.Bones, "Bones", PhysicsOverlayPart.Chain, Theme.Accent))
            DrawBones(current, index, targets);
        if (GroupHeader(EditorGroup.Tip, "Tip", PhysicsOverlayPart.Tip, Theme.Accent))
            DrawTip(current, index, targets);
        if (GroupHeader(EditorGroup.Swing, "Swing limit", PhysicsOverlayPart.Cone, Theme.Info))
            DrawSwing(current, targets);
        if (GroupHeader(EditorGroup.Spring, "Spring", PhysicsOverlayPart.Spring, Theme.Warning))
            DrawSpring(current, index, targets);
        if (GroupHeader(EditorGroup.Collision, "Collision with", PhysicsOverlayPart.Shape, Theme.Success,
                "Shapes these chains stay out of, such as arms and legs. Each shape costs a little performance."))
            DrawCollision(current, index, targets);
        if (GroupHeader(EditorGroup.Wind, "Wind and gravity", null, Theme.Muted))
            DrawWind(current, index, targets);
    }

    private void DrawGroup(PhysicsDocument current, PhybSimulator simulator)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Turns off with");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(220));
        using (var combo = ImRaii.Combo("##group", PhysicsGroups.Label(simulator.Group)))
        {
            if (combo.Success)
            {
                foreach (var (group, label, detail) in PhysicsGroups.Choices)
                {
                    if (ImGui.Selectable($"{label} ({group})", simulator.Group == group) && simulator.Group != group)
                        Change(current, () => simulator.Group = group);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(detail);
                }
            }
            else if (ComboWheel.Steps() is var steps and not 0
                     && ComboWheel.Pick(PhysicsGroups.Choices, choice => choice.Group == simulator.Group, steps).Group is var picked && picked != simulator.Group)
                Change(current, () => simulator.Group = picked);
        }
        Tip("The game turns physics off by group: tops can turn off chest physics, and hats hair physics.");
        var known = PhysicsGroups.Choices.FirstOrDefault(choice => choice.Group == simulator.Group);
        if (known.Label != null)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Hint, known.Detail);
        }
    }

    private void DrawIssue(PhysicsDocument current, PhysicsIssue issue)
    {
        var colour = issue.Level == PhysicsIssueLevel.Error ? Theme.Error : Theme.Warning;
        ImGui.TextColored(colour, issue.Level == PhysicsIssueLevel.Error ? "✕" : "⚠");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.TextWrapPos(ImGui.GetContentRegionMax().X - Theme.Scaled(60)))
            ImGui.TextColored(Theme.Text, issue.Text);
        if (issue.Fix == PhysicsFix.None)
            return;
        ImGui.SameLine();
        using var id = ImRaii.PushId(issue.Text);
        var fix = issue.Fix switch
        {
            PhysicsFix.ChestGroup => "Move it to the chest group.",
            PhysicsFix.RemoveBroken => "Remove the links to things that don't exist.",
            _ => "Limit the swing to 0° to 180°.",
        };
        if (ImGui.SmallButton("Fix"))
            Change(current, () => PhysicsValidation.Apply(current.File, issue.Fix, issue.Simulator), $"Fix: {fix.TrimEnd('.')}");
        Tip(fix);
    }

    private void DrawChainChoice(PhybFile file, int index)
    {
        var chains = file.Simulators[index].Chains;
        var mirrored = Enumerable.Range(0, chains.Count).Any(c => PhysicsAreas.MirrorChain(file, index, c) != null);
        if (chains.Count > 1)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, "Edit");
            ImGui.SameLine(LabelWidth);
            ImGui.SetNextItemWidth(Theme.Scaled(220));
            var preview = chainChoice < 0 || chainChoice >= chains.Count ? "All chains" : PhysicsValidation.ChainLabel(chains[chainChoice], chainChoice);
            using (var combo = ImRaii.Combo("##chain", preview))
            {
                if (combo.Success)
                {
                    if (ImGui.Selectable("All chains", chainChoice < 0))
                        chainChoice = -1;
                    for (var c = 0; c < chains.Count; c++)
                        if (ImGui.Selectable($"{PhysicsValidation.ChainLabel(chains[c], c)}##chain-{c}", chainChoice == c))
                            chainChoice = c;
                }
                else
                {
                    // Entry 0 is "All chains".
                    var entry = chainChoice < 0 || chainChoice >= chains.Count ? 0 : chainChoice + 1;
                    if (ComboWheel.Scroll(ref entry, chains.Count + 1))
                        chainChoice = entry - 1;
                }
            }
            if (mirrored)
                ImGui.SameLine();
        }
        if (mirrored)
        {
            if (chains.Count <= 1)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Label, "Edit");
                ImGui.SameLine(LabelWidth);
            }
            ImGui.Checkbox("Both sides##both", ref bothSides);
            Tip("Changes go to the left and right chains together.");
        }
    }

    /// <summary> The chains an edit changes: the chosen one or all of the area's, with their mirrors when both sides are linked. </summary>
    private List<(int Simulator, int Chain)> Targets(PhybFile file, int index)
    {
        var result = new List<(int, int)>();
        var chains = file.Simulators[index].Chains;
        var chosen = chainChoice >= 0 && chainChoice < chains.Count ? [chainChoice] : Enumerable.Range(0, chains.Count);
        foreach (var c in chosen)
        {
            if (chains[c].Nodes.Count < 2)
                continue;
            if (!result.Contains((index, c)))
                result.Add((index, c));
            if (bothSides && PhysicsAreas.MirrorChain(file, index, c) is { } mirror &&
                file.Simulators[mirror.Simulator].Chains[mirror.Chain].Nodes.Count >= 2 && !result.Contains(mirror))
                result.Add(mirror);
        }
        return result;
    }

    // ---- Groups ----

    private float LabelWidth => Theme.Scaled(130);

    private void DrawBones(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        KnobSlider(current, targets, PhysicsKnob.Length, "How far the tip sits from the bone. Longer swings slower and wider.", 0.5f, 50,
            "%.1f cm", part: PhysicsOverlayPart.Chain);
        KnobSlider(current, targets, PhysicsKnob.Weight, "How hard gravity pulls it.", 0, 2, "%.2f", part: PhysicsOverlayPart.Chain);
        KnobSlider(current, targets, PhysicsKnob.Damping, "How fast the jiggle dies out. Higher stops it sooner.", 0, 1, "%.3f", part: PhysicsOverlayPart.Chain);
        KnobSlider(current, targets, PhysicsKnob.Follow, "How strongly the animation pulls it back. Higher is stiffer.", 0, 1, "%.3f",
            ImGuiSliderFlags.Logarithmic, part: PhysicsOverlayPart.Chain);
        KnobSlider(current, targets, PhysicsKnob.MaxSpeed, "Caps how fast it may move, so a sudden turn can't fling it. Lower calms wild jiggle.",
            0.1f, 10, "%.1f m/s", part: PhysicsOverlayPart.Chain);
        DrawAlongChain(current, index, "Along the chain", "Each point's own values. The sliders above set every point and keep these shares.",
            new PointColumn("Weight", "##weight", node => node.GravityScale, (node, value) => node.GravityScale = value, 0.01f, 0, 5, "%.2f"),
            new PointColumn("Follow pose", "##follow", node => node.AttractByAnimation, (node, value) => node.AttractByAnimation = value, 0.001f, 0, 1, "%.3f"),
            new PointColumn("Swing limit °", "##swing", node => PhysicsTemplates.Degrees(node.ConeMaxAngle),
                (node, value) => node.ConeMaxAngle = PhysicsTemplates.Radians(value), 0.5f, 0, 180, "%.0f"));
    }

    private bool coneTiltOpen;

    /// <summary> The swing cone: how far it opens, and which way. </summary>
    private void DrawSwing(PhysicsDocument current, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];
        KnobSlider(current, targets, PhysicsKnob.Swing, "The furthest it swings from the cone's centre.", 0, 180, "%.0f°", part: PhysicsOverlayPart.Cone);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Cone centre");
        Tip("Which way the swing cone opens. Along the bone is where the bone rests.");
        Lights(PhysicsOverlayPart.Cone);
        ImGui.SameLine(LabelWidth);
        var skeleton = target?.Character.Skeleton;
        var tilt = skeleton != null ? PhysicsKnobs.ConeTilt(first, skeleton) : null;
        ImGui.TextColored(Theme.Text, tilt is not { } degrees ? "Unknown" : degrees < 1 ? "Along the bone" : $"Tilted {degrees.ToString("0", CultureInfo.InvariantCulture)}°");
        if (tilt >= 1 && skeleton != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Straighten"))
                Change(current, () =>
                {
                    foreach (var (s, c) in targets)
                        PhysicsKnobs.StraightenCone(file.Simulators[s].Chains[c], skeleton);
                }, "Straighten the cone");
            Tip("Points the cone along the bone again.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(coneTiltOpen ? "Done##tilt" : "Tilt##tilt"))
            coneTiltOpen = !coneTiltOpen;
        Tip("Type the cone's direction.");
        if (!coneTiltOpen)
            return;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + LabelWidth);
        var axis = first.Nodes[0].ConeAxisOffset;
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3("##axis", ref axis, 0.005f, -10, 10, "%.3f");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            var rest = RestPose();
            var from = first.Nodes[0].Bone.Text;
            foreach (var (s, c) in targets)
            {
                var node = file.Simulators[s].Chains[c].Nodes[0];
                var to = node.Bone.Text;
                // The axis is in the parent's space: chains of the same bone take it as it is, the other side's twin the mirror.
                if (string.Equals(from, to, StringComparison.Ordinal))
                    node.ConeAxisOffset = axis;
                else if (rest != null && skeleton != null && PhysicsAreas.Opposite(from, to) &&
                         skeleton.Get(from)?.Parent is { Length: > 0 } parent && skeleton.Get(to)?.Parent is { Length: > 0 } twinParent)
                    node.ConeAxisOffset = PhysicsAreas.MirrorOffset(axis, parent, twinParent, rest);
            }
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Tip("The cone's direction, in the space of the bone's parent. The other side's mirrors it.");
        Lights(PhysicsOverlayPart.Cone);
    }

    private void DrawSpring(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];

        if (PhysicsKnobs.MainSpring(file.Simulators[s0], c0) is { } spring)
        {
            // Presets aim springs elsewhere, as a lift for example; the slider is named for what its spring does.
            var name = target is { } aimed ? PhysicsKnobs.SpringName(file.Simulators[s0], spring, aimed.Character.Skeleton) : "Firmness";
            KnobSlider(current, targets, PhysicsKnob.Firmness, SpringTip(name), 0, 0.2f, "%.4f", ImGuiSliderFlags.Logarithmic, name, PhysicsOverlayPart.Spring);
            // Only on request: a spring aimed off where it rests changes only when this is clicked.
            if (target is { } found && PhysicsKnobs.CanAimAtRest(file.Simulators[s0], spring, found.Character.Skeleton))
            {
                var skeleton = found.Character.Skeleton;
                ImGui.SameLine();
                if (ImGui.SmallButton("Aim at rest"))
                    Change(current, () =>
                    {
                        foreach (var (s, c) in targets)
                            if (PhysicsKnobs.MainSpring(file.Simulators[s], c) is { } main && PhysicsKnobs.CanAimAtRest(file.Simulators[s], main, skeleton))
                                PhysicsKnobs.AimAtRest(file.Simulators[s], main, skeleton);
                    }, "Aim at rest");
                Tip($"Turns the spring to pull the tip to where it rests, keeping its strength. It points {PhysicsKnobs.SpringAim(file.Simulators[s0], spring, skeleton):F0}° off now.");
            }
            if (target is { } placed)
                SpringPlaceField(current, file.Simulators[s0], spring, placed.Character.Skeleton, spring.Stiffness < 0 ? "Pushes away from" : "Pulls toward", "##spring-place",
                    "Where the spring pulls the tip, in cm from where the tip rests: out from the middle, up, forward. Along the bone keeps it firm, " +
                    "off to a side pulls it that way. Drag the spring's dot on your character to move it.",
                    place => Apply(current, targets, (simulator, _, c) =>
                    {
                        if (PhysicsKnobs.MainSpring(simulator, c) is { } main)
                            PhysicsKnobs.SetSpringPlace(simulator, main, placed.Character.Skeleton, place);
                    }));
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, "Firmness");
            ImGui.SameLine(LabelWidth);
            ImGui.TextColored(Theme.Hint, "No spring pulls it back.");
            if (target is { } found && PhysicsTemplates.RestSpring(first, (short)c0, found.Character.Skeleton, 0.04f) != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Add spring"))
                    Change(current, () =>
                    {
                        foreach (var (s, c) in targets)
                            if (PhysicsKnobs.MainSpring(file.Simulators[s], c) == null &&
                                PhysicsTemplates.RestSpring(file.Simulators[s].Chains[c], (short)c, found.Character.Skeleton, 0.04f) is { } added)
                                file.Simulators[s].Attracts.Add(added);
                    });
                Tip("Adds a spring that pulls the tip back to where it rests.");
            }
        }
        DrawOtherSprings(current, index, targets);
    }

    /// <summary>
    /// A spring's place as three cm numbers from where its point rests (see <see cref="PhysicsKnobs.SpringPlace"/>);
    /// a drag is one undo step and previews when let go. Nothing when a bone is missing.
    /// </summary>
    private void SpringPlaceField(PhysicsDocument current, PhybSimulator simulator, PhybAttract spring, PhysicsSkeleton skeleton, string label, string id, string tip,
        Action<Vector3> set)
    {
        if (PhysicsKnobs.SpringPlace(simulator, spring, skeleton) is not { } place)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        Tip(tip);
        Lights(PhysicsOverlayPart.Spring);
        ImGui.SameLine(LabelWidth);
        var shown = place * 100;
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3(id, ref shown, 0.05f, -200, 200, "%.1f cm");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
            set(shown / 100);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Tip(tip);
        Lights(PhysicsOverlayPart.Spring);
    }

    /// <summary> The springs on the edited chains' other points, which some presets add: their strength and place, and a button to remove each. </summary>
    private void DrawOtherSprings(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var simulator = current.File.Simulators[index];
        var others = simulator.Attracts
            .Where(attract => targets.Contains((index, attract.Chain)) && !ReferenceEquals(attract, PhysicsKnobs.MainSpring(simulator, attract.Chain)))
            .ToList();
        if (others.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, "Other springs");
        Tip("Springs on other points of these chains.");
        foreach (var attract in others)
        {
            using var id = ImRaii.PushId($"spring-{simulator.Attracts.IndexOf(attract)}");
            var chain = PhysicsValidation.ChainLabel(simulator.Chains[attract.Chain], attract.Chain);
            ImGui.TextColored(Theme.Muted, $"{chain}, point {attract.Node + 1}");
            var button = ImGui.GetFrameHeight();
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - button));
            if (Widgets.GhostIconButton("##remove-spring", FontAwesomeIcon.Times, "Remove this spring", new Vector2(button)))
            {
                Change(current, () => simulator.Attracts.Remove(attract), "Remove a spring");
                break;
            }
            var stiffness = attract.Stiffness;
            if (Slider(current, "Strength", "How hard it pulls. Negative pushes away.", ref stiffness, -0.2f, 0.2f, "%.4f", ImGuiSliderFlags.Logarithmic,
                    part: PhysicsOverlayPart.Spring))
            {
                attract.Stiffness = stiffness;
                current.Changed();
            }
            if (target is { } found)
                SpringPlaceField(current, simulator, attract, found.Character.Skeleton, attract.Stiffness < 0 ? "Pushes away from" : "Pulls toward", "##place",
                    "Where it pulls the point, in cm from where the point rests: out from the middle, up, forward.",
                    place =>
                    {
                        PhysicsKnobs.SetSpringPlace(simulator, attract, found.Character.Skeleton, place);
                        current.Changed();
                    });
        }
    }

    /// <summary> The tooltip for the spring slider, by what <see cref="PhysicsKnobs.SpringName"/> calls the spring. </summary>
    private static string SpringTip(string name) => name switch
    {
        "Firmness" => "How hard it springs back to where it rests. Higher settles faster.",
        "Spring" => "How hard its spring pulls it. Where to is under Springs.",
        _ => $"How hard its spring {name.ToLowerInvariant() switch
        {
            "lift" => "lifts it",
            "pull back" => "pulls it back toward the body",
            "pull together" => "pulls the sides together",
            "pull apart" => "pulls the sides apart",
            var other => other.Replace("pull ", "pulls it "),
        }}. Follow animation is what springs it back.",
    };

    /// <summary>
    /// A slider for a knob of the edited chains: shows the first's value and sets them all. Its id is
    /// the knob's name, whatever its label says. While hovered, <paramref name="part"/> lights on the
    /// character.
    /// </summary>
    private void KnobSlider(PhysicsDocument current, List<(int Simulator, int Chain)> targets, PhysicsKnob knob, string tip,
        float min, float max, string format, ImGuiSliderFlags flags = ImGuiSliderFlags.None, string? label = null, PhysicsOverlayPart? part = null)
    {
        var (s0, c0) = targets[0];
        var value = PhysicsKnobs.Read(current.File.Simulators[s0], c0, knob) ?? 0;
        if (Slider(current, label ?? PhysicsKnobs.Label(knob), tip, ref value, min, max, format, flags, knob, part))
            Apply(current, targets, (simulator, _, index) => PhysicsKnobs.Write(simulator, index, knob, value));
    }

    /// <summary>
    /// A labelled slider; the drag is one undo step and previews when let go. With a knob, a mark under
    /// it shows where the value started. Returns whether the value changed.
    /// </summary>
    private bool Slider(PhysicsDocument current, string label, string tip, ref float value, float min, float max, string format,
        ImGuiSliderFlags flags = ImGuiSliderFlags.None, PhysicsKnob? knob = null, PhysicsOverlayPart? part = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        Tip(tip);
        Lights(part);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat($"##{knob?.ToString() ?? label}", ref value, min, max, format, flags);
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Lights(part);
        if (knob is { } shown)
            DrawStartMark(shown, min, max, format, (flags & ImGuiSliderFlags.Logarithmic) != 0);
        return changed;
    }

    /// <summary> Applies a change to every target chain, then marks the document changed. </summary>
    private static void Apply(PhysicsDocument current, List<(int Simulator, int Chain)> targets, Action<PhybSimulator, PhybChain, int> apply)
    {
        foreach (var (s, c) in targets)
            apply(current.File.Simulators[s], current.File.Simulators[s].Chains[c], c);
        current.Changed();
    }

    // ---- Collision ----

    private void DrawCollision(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var names = file.Collision.Names.Select(name => name.Text).Distinct().ToList();
        if (names.Count == 0)
            Widgets.HintWrapped("The file has no collision shapes. Add some under Collision shapes.");
        DrawShapeTicks(current, targets, names);
        DrawBumps(current, index, targets);
    }

    private void DrawShapeTicks(PhysicsDocument current, List<(int Simulator, int Chain)> targets, List<string> names)
    {
        var file = current.File;
        foreach (var name in names)
        {
            using var id = ImRaii.PushId(name);
            var state = PhysicsCollisions.State(file, targets, name);
            // Both bits set is ticked, one of them shows the dash of a partly ticked box; a click ticks or unticks all.
            var bits = state switch
            {
                PhysicsBumps.All => 3u,
                PhysicsBumps.Some => 1u,
                _ => 0u,
            };
            // A click on this shape on the character scrolls here, and the row flashes.
            if (jumpShape == name)
            {
                ImGui.SetScrollHereY(.3f);
                jumpShape = null;
            }
            bool clicked;
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Accent, flashShape == name && Flashing))
                clicked = ImGui.CheckboxFlags($"{PhysicsShapeNames.Label(file.Collision, name, target?.Character.Skeleton)}##check", ref bits, 3u);
            if (clicked)
            {
                var on = bits == 3u;
                // Nothing to change, or a refused tick, leaves no undo step behind.
                if (!PhysicsCollisions.HasPoints(file, targets))
                    Say(FeedbackSeverity.Warning, "These chains have no moving points to collide.");
                else if (on && !PhysicsCollisions.CanAdd(file, targets, name))
                    Say(FeedbackSeverity.Warning, $"A chain can collide with {PhysicsCollisions.MaxShapes} shapes at most.");
                else
                    Change(current, () =>
                    {
                        if (on)
                            PhysicsCollisions.Add(file, targets, name);
                        else
                            PhysicsCollisions.Remove(file, targets, name);
                    });
            }
            Lights(PhysicsOverlayPart.Shape, name);
            if (state == PhysicsBumps.Some)
                Tip($"Only some points collide with it, on: {string.Join(", ", Bumping(file, targets, name))}. Tick to make all of them.");
            // The area's entries for the shape, and the chains' own.
            var references = targets.Select(target => target.Simulator).Distinct()
                .SelectMany(s => file.Simulators[s].Collisions.Where(reference => reference.Name.Text == name))
                .Concat(targets.SelectMany(target => file.Simulators[target.Simulator].Chains[target.Chain].Collisions.Where(reference => reference.Name.Text == name)))
                .ToList();
            if (state == PhysicsBumps.None || references.Count == 0)
                continue;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Theme.Scaled(110));
            using var combo = ImRaii.Combo("##type", TypeLabel(references[0].Type));
            if (combo.Success)
            {
                foreach (var type in Enum.GetValues<PhybCollisionType>())
                    if (ImGui.Selectable(TypeLabel(type), references[0].Type == type) && references.Any(reference => reference.Type != type))
                        Change(current, () => references.ForEach(reference => reference.Type = type));
            }
            else if (ComboWheel.Scroll(Enum.GetValues<PhybCollisionType>(), references[0].Type, out var picked))
                Change(current, () => references.ForEach(reference => reference.Type = picked));
        }
    }

    /// <summary> The chains that collide with a shape, all of their points or some. </summary>
    private static IEnumerable<string> Bumping(PhybFile file, List<(int Simulator, int Chain)> targets, string name)
        => targets.Where(target => PhysicsCollisions.State(file, [target], name) != PhysicsBumps.None)
            .Select(target => PhysicsValidation.ChainLabel(file.Simulators[target.Simulator].Chains[target.Chain], target.Chain));

    private static string TypeLabel(PhybCollisionType type) => type switch
    {
        PhybCollisionType.Outside => "Stay outside",
        PhybCollisionType.Inside => "Stay inside",
        _ => "Stick to surface",
    };

    // ---- Along the chain ----

    /// <summary> One column of <see cref="DrawAlongChain"/>: a point's value, read and written in the units shown. </summary>
    private readonly record struct PointColumn(string Header, string Id, Func<PhybNode, float> Get, Action<PhybNode, float> Set,
        float Speed, float Min, float Max, string Format);

    /// <summary>
    /// A table per edited chain with more than one moving point (skirts, hair): one row per moving point, one
    /// column per value. With both sides linked, the other side's twin point takes the same value.
    /// </summary>
    private void DrawAlongChain(PhysicsDocument current, int index, string label, string tip, params PointColumn[] columns)
    {
        var file = current.File;
        var chains = file.Simulators[index].Chains;
        IEnumerable<int> chosen = chainChoice >= 0 && chainChoice < chains.Count ? [chainChoice] : Enumerable.Range(0, chains.Count);
        var shown = chosen.Where(c => chains[c].Nodes.Count > 2).ToList();
        if (shown.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, label);
        Tip(tip);
        foreach (var c in shown)
        {
            using var id = ImRaii.PushId($"{columns[0].Id}-points-{c}");
            var chain = chains[c];
            if (shown.Count > 1)
                ImGui.TextColored(Theme.Muted, PhysicsValidation.ChainLabel(chain, c));
            using var table = ImRaii.Table("##points", columns.Length + 1, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (!table.Success)
                continue;
            ImGui.TableSetupColumn("Point", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            foreach (var column in columns)
                ImGui.TableSetupColumn(column.Header, ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            var mirror = bothSides ? PhysicsAreas.MirrorChain(file, index, c) : null;
            var tipIndex = PhysicsKnobs.TipIndex(chain);
            for (var n = 1; n < chain.Nodes.Count; n++)
            {
                using var row = ImRaii.PushId(n);
                var node = chain.Nodes[n];
                PhybNode? twin = mirror is { } m && file.Simulators[m.Simulator].Chains[m.Chain].Nodes.Count == chain.Nodes.Count
                    ? file.Simulators[m.Simulator].Chains[m.Chain].Nodes[n]
                    : null;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(n == tipIndex ? Theme.Muted : Theme.Text,
                    n == tipIndex ? node.Bone.IsEmpty ? "Tip" : $"Tip ({node.Bone.Text})" : node.Bone.Text);
                foreach (var column in columns)
                    Cell(current, column.Id, column.Get(node), column.Speed, column.Min, column.Max, column.Format, value =>
                    {
                        column.Set(node, value);
                        if (twin != null)
                            column.Set(twin, value);
                    });
            }
        }
    }

    private PhysicsSkeleton? restPoseOf;
    private Dictionary<string, PhysicsFrame>? restPose;

    /// <summary> Your skeleton's rest pose, which mirrors one side's places onto the other; null before your character is read. </summary>
    private Dictionary<string, PhysicsFrame>? RestPose()
    {
        if (target?.Character.Skeleton is not { } skeleton)
            return null;
        if (!ReferenceEquals(skeleton, restPoseOf))
        {
            restPose = PhysicsBody.RestPose(skeleton);
            restPoseOf = skeleton;
        }
        return restPose;
    }

    /// <summary> A drag field in a table cell; one undo step per drag. </summary>
    private void Cell(PhysicsDocument current, string id, float value, float speed, float min, float max, string format, Action<float> set)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.DragFloat(id, ref value, speed, min, max, format);
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

    /// <summary> The tip: the ball that bumps into shapes and where it sits, then the sizes of the other moving points. </summary>
    private void DrawTip(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];

        KnobSlider(current, targets, PhysicsKnob.Size, "The ball at the tip that bumps into shapes.", 0, 20, "%.1f cm", part: PhysicsOverlayPart.Tip);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Tip place");
        ImGui.SameLine(LabelWidth);
        var tipPlace = first.LastBoneOffset * 100;
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var moved = ImGui.DragFloat3("##tip", ref tipPlace, 0.05f, -100, 100, "%.1f cm");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (moved)
        {
            var rest = RestPose();
            // The bone before each chain's tip, which its offset is in: not always the second last point (VFXEditor's chests).
            var from = first.Nodes[PhysicsKnobs.TipIndex(first) - 1].Bone.Text;
            foreach (var (s, c) in targets)
            {
                var chain = file.Simulators[s].Chains[c];
                var to = chain.Nodes[PhysicsKnobs.TipIndex(chain) - 1].Bone.Text;
                // The offset is in the last bone's space: chains ending in the same bone take it as it
                // is, the other side's twin the mirrored place, and chains of other bones (a skirt's
                // other panels, other hair strands) keep their own.
                if (string.Equals(from, to, StringComparison.Ordinal))
                    chain.LastBoneOffset = tipPlace / 100;
                else if (rest != null && PhysicsAreas.Opposite(from, to))
                    chain.LastBoneOffset = PhysicsAreas.MirrorOffset(tipPlace / 100, from, to, rest);
            }
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Tip("Where the tip sits, in the last bone's space. The other side's tip moves to the mirrored place. " +
            "Chains ending in other bones keep their own. Pick one to change it.");
        Lights(PhysicsOverlayPart.Tip);

        DrawAlongChain(current, index, "Sizes along the chain", "Each moving point's ball, in cm. Tip size sets every point and keeps these shares.",
            new PointColumn("Size cm", "##size", node => node.Radius * 100, (node, value) => node.Radius = value / 100, 0.05f, 0, 50, "%.1f"));
    }

    /// <summary> How the points bump into shapes, and the area's collision switches and passes. </summary>
    private void DrawBumps(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var simulator = file.Simulators[index];
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];

        ImGui.Spacing();
        KnobSlider(current, targets, PhysicsKnob.Friction, "Slows it while it slides along a shape.", 0, 1, "%.3f", part: PhysicsOverlayPart.Tip);
        KnobSlider(current, targets, PhysicsKnob.BumpDamping, "Speed it loses when it hits a shape.", 0, 1, "%.3f", part: PhysicsOverlayPart.Tip);
        KnobSlider(current, targets, PhysicsKnob.PushBack, "How far a shape pushes it out each step. 1 is all the way.", 0, 1, "%.3f", part: PhysicsOverlayPart.Tip);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Point shape");
        ImGui.SameLine(LabelWidth);
        var capsule = first.Type == 1;
        if (ImGui.RadioButton("Balls", !capsule) && capsule)
            Change(current, () => { foreach (var (s, c) in targets) file.Simulators[s].Chains[c].Type = 0; });
        ImGui.SameLine();
        if (ImGui.RadioButton("Capsules", capsule) && !capsule)
            Change(current, () => { foreach (var (s, c) in targets) file.Simulators[s].Chains[c].Type = 1; });
        Tip("Capsules also bump along the line between points.");

        Flag(current, simulator, PhybSimulatorFlags.CollisionsHandled, "Collide", "Off: it moves through every shape.");
        ImGui.SameLine();
        Flag(current, simulator, PhybSimulatorFlags.ContinuousCollisions, "Catch fast hits", "Catches hits between frames. Costs more.");
        ImGui.SameLine();
        Flag(current, simulator, PhybSimulatorFlags.UsingGroundPlane, "Stay above floor", "Keeps it above the floor.");

        var steps = (int)simulator.ConstraintLoop;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Accuracy");
        Tip("Settle and bump passes per frame. The game uses 2 and 1. More costs more.");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        if (ImGui.InputInt("##constraint-loop", ref steps))
            Change(current, () => simulator.ConstraintLoop = (short)Math.Clamp(steps, 1, 8));
        Tip("Settle passes per frame. The game uses 2.");
        ImGui.SameLine();
        var collisionSteps = (int)simulator.CollisionLoop;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        if (ImGui.InputInt("##collision-loop", ref collisionSteps))
            Change(current, () => simulator.CollisionLoop = (short)Math.Clamp(collisionSteps, 1, 8));
        Tip("Bump passes per frame. The game uses 1.");
    }

    private bool gravityOpen;

    /// <summary> The wind on the edited chains and the area's wind and gravity. </summary>
    private void DrawWind(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var simulator = current.File.Simulators[index];
        KnobSlider(current, targets, PhysicsKnob.Wind, "How much wind moves it. Needs a wind direction below.", 0, 0.2f, "%.4f", ImGuiSliderFlags.Logarithmic,
            part: PhysicsOverlayPart.Chain);
        Vector3Field(current, "Wind from", "##wind-vector", "Which way the wind blows the area. Zero is no wind.", simulator.Wind, value => simulator.Wind = value);

        var gravity = simulator.Gravity;
        var down = MathF.Abs(gravity.X) < 1e-4f && MathF.Abs(gravity.Z) < 1e-4f && gravity.Y < 0;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Gravity");
        Tip("Which way and how hard gravity pulls this area.");
        ImGui.SameLine(LabelWidth);
        ImGui.TextColored(Theme.Text, down ? $"Straight down, {(-gravity.Y).ToString("0.00", CultureInfo.InvariantCulture)}" : "Tilted");
        ImGui.SameLine();
        if (ImGui.SmallButton(gravityOpen ? "Done##gravity-change" : "Change##gravity-change"))
            gravityOpen = !gravityOpen;
        if (gravityOpen)
            Vector3Field(current, "", "##gravity", "Which way and how hard gravity pulls this area.", gravity, value => simulator.Gravity = value);
    }

    /// <summary> A labelled three-number field; a drag is one undo step and previews when let go. </summary>
    private void Vector3Field(PhysicsDocument current, string label, string id, string tip, Vector3 value, Action<Vector3> set)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        Tip(tip);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3(id, ref value, 0.05f, -50, 50, "%.2f");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            set(value);
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Tip(tip);
    }

    private void Flag(PhysicsDocument current, PhybSimulator simulator, PhybSimulatorFlags flag, string label, string tip)
    {
        var on = simulator.Flags.HasFlag(flag);
        if (ImGui.Checkbox($"{label}##flag-{flag}", ref on))
            Change(current, () => simulator.Flags = on ? simulator.Flags | flag : simulator.Flags & ~flag);
        Tip(tip);
    }
}
