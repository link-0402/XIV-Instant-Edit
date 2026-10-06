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
                });
            Tip($"{outsideChest.Count} body areas aren't in the chest group, so tops that turn off breast physics don't stop them.");
        }
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
                if (ImGui.Selectable($"{info.Label}##area", selected == i))
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

        if (target is { } found)
        {
            var addable = PhysicsTemplates.Addable(current.File, found.Character.Skeleton);
            if (addable.Count > 0)
            {
                ImGui.Spacing();
                Widgets.SectionHeader("Add", "your skeleton has these");
                foreach (var kind in addable)
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
            }
        }
        ImGui.Spacing();
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
        DrawFitButton(current, small: false);
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
        for (var i = 0; i < file.Simulators.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var info = PhysicsAreas.Classify(file.Simulators[i]);
            var overlap = info.Bones.Any(moved.Contains);
            if (ImGui.Selectable($"{info.Label}{(overlap ? "  (you already move these)" : "")}##pick"))
            {
                var index = i;
                Change(current, () => selected = current.ImportArea(file, index));
                Say(FeedbackSeverity.Info, overlap
                    ? $"Imported {info.Label}. Turn your old area for these bones off, or both move them."
                    : $"Imported {info.Label} from {importName}.");
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

    /// <summary> A discrete change: one undo step, then the views and the automatic preview catch up. </summary>
    private void Change(PhysicsDocument current, Action edit)
    {
        current.Checkpoint();
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

        ImGui.TextColored(Theme.Text, info.Label);
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

        ImGui.Spacing();
        Widgets.SectionHeader("Movement");
        DrawMovement(current, targets);
        ImGui.Spacing();
        Widgets.SectionHeader("Collision");
        DrawCollision(current, index, targets);
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Points##points"))
            DrawPoints(current, index);
        if (ImGui.CollapsingHeader("Springs##springs"))
            DrawSprings(current, index);
        if (ImGui.CollapsingHeader("More settings##more"))
            DrawMore(current, index, targets);
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
        if (ImGui.SmallButton("Fix"))
            Change(current, () => PhysicsValidation.Apply(current.File, issue.Fix, issue.Simulator));
        Tip(issue.Fix switch
        {
            PhysicsFix.ChestGroup => "Move it to the chest group.",
            PhysicsFix.RemoveBroken => "Remove the links to things that don't exist.",
            _ => "Limit the swing to 0° to 180°.",
        });
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

    // ---- Movement ----

    private float LabelWidth => Theme.Scaled(130);

    private void DrawMovement(PhysicsDocument current, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];
        var tip = first.Nodes[^1];

        var spring = MainSpring(file.Simulators[s0], c0);
        if (spring != null)
        {
            var firmness = spring.Stiffness;
            if (Slider(current, "Firmness", "How hard it springs back to rest. Lower is bouncier.", ref firmness, 0, 0.2f, "%.4f", ImGuiSliderFlags.Logarithmic))
                Apply(current, targets, (simulator, chain, index) =>
                {
                    if (MainSpring(simulator, index) is { } own)
                        own.Stiffness = firmness;
                });
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
                            if (MainSpring(file.Simulators[s], c) == null &&
                                PhysicsTemplates.RestSpring(file.Simulators[s].Chains[c], (short)c, found.Character.Skeleton, 0.04f) is { } added)
                                file.Simulators[s].Attracts.Add(added);
                    });
                Tip("Adds a spring that pulls the tip back to where it rests, measured on your skeleton.");
            }
        }

        var damping = first.Dampening;
        if (Slider(current, "Damping", "How fast the motion calms down.", ref damping, 0, 1, "%.3f"))
            Apply(current, targets, (_, chain, _) => chain.Dampening = damping);

        var swing = PhysicsTemplates.Degrees(first.Nodes[0].ConeMaxAngle);
        if (Slider(current, "Swing", "How far it can swing away from rest.", ref swing, 0, 180, "%.0f°"))
            Apply(current, targets, (_, chain, _) => SetSwing(chain, PhysicsTemplates.Radians(swing)));

        var weight = tip.GravityScale;
        if (Slider(current, "Weight", "How much gravity pulls it.", ref weight, 0, 2, "%.2f"))
            Apply(current, targets, (_, chain, _) => SetMoving(chain, node => node.GravityScale, (node, value) => node.GravityScale = value, weight));

        var follow = tip.AttractByAnimation;
        if (Slider(current, "Follow animation", "How closely it sticks to the animation. Higher is stiffer.", ref follow, 0, 1, "%.3f",
                ImGuiSliderFlags.Logarithmic))
            Apply(current, targets, (_, chain, _) => SetMoving(chain, node => node.AttractByAnimation, (node, value) => node.AttractByAnimation = value, follow));

        var length = first.LastBoneOffset.Length() * 100;
        if (Slider(current, "Length", "How far the tip sits from the bone. Longer swings slower and wider.", ref length, 0.5f, 50, "%.1f cm"))
            Apply(current, targets, (_, chain, _) =>
            {
                var offset = chain.LastBoneOffset;
                chain.LastBoneOffset = offset.LengthSquared() > 1e-12f ? Vector3.Normalize(offset) * (length / 100) : new Vector3(length / 100, 0, 0);
            });

        var speed = first.MaxSpeed;
        if (Slider(current, "Max speed", "The fastest it may move.", ref speed, 0, 10, "%.2f"))
            Apply(current, targets, (_, chain, _) => chain.MaxSpeed = speed);

        var wind = tip.WindScale;
        if (Slider(current, "Wind", "How much wind moves it.", ref wind, 0, 0.2f, "%.4f", ImGuiSliderFlags.Logarithmic))
            Apply(current, targets, (_, chain, _) => SetMoving(chain, node => node.WindScale, (node, value) => node.WindScale = value, wind));
    }

    /// <summary> A labelled slider; the drag is one undo step and previews when let go. Returns whether the value changed. </summary>
    private bool Slider(PhysicsDocument current, string label, string tip, ref float value, float min, float max, string format,
        ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        Tip(tip);
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat($"##{label}", ref value, min, max, format, flags);
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        return changed;
    }

    /// <summary> Applies a change to every target chain, then marks the document changed. </summary>
    private static void Apply(PhysicsDocument current, List<(int Simulator, int Chain)> targets, Action<PhybSimulator, PhybChain, int> apply)
    {
        foreach (var (s, c) in targets)
            apply(current.File.Simulators[s], current.File.Simulators[s].Chains[c], c);
        current.Changed();
    }

    /// <summary> The spring that holds a chain's tip: its strongest one on a moving point. </summary>
    private static PhybAttract? MainSpring(PhybSimulator simulator, int chain)
        => simulator.Attracts.Where(attract => attract.Chain == chain && attract.Node >= 1).MaxBy(attract => MathF.Abs(attract.Stiffness));

    /// <summary> Sets the bone's swing limit; the points between bone and tip keep their share of it. </summary>
    private static void SetSwing(PhybChain chain, float radians)
    {
        var old = chain.Nodes[0].ConeMaxAngle;
        chain.Nodes[0].ConeMaxAngle = radians;
        for (var n = 1; n < chain.Nodes.Count - 1; n++)
            chain.Nodes[n].ConeMaxAngle = old > 1e-6f ? Math.Clamp(chain.Nodes[n].ConeMaxAngle * radians / old, 0, MathF.PI) : radians;
    }

    /// <summary> Sets a value on the tip; the points between bone and tip keep their share of it. </summary>
    private static void SetMoving(PhybChain chain, Func<PhybNode, float> get, Action<PhybNode, float> set, float value)
    {
        var tip = chain.Nodes[^1];
        var old = get(tip);
        for (var n = 1; n < chain.Nodes.Count - 1; n++)
            set(chain.Nodes[n], MathF.Abs(old) > 1e-9f ? get(chain.Nodes[n]) * value / old : value);
        set(tip, value);
    }

    // ---- Collision ----

    private void DrawCollision(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var simulator = file.Simulators[index];
        var (s0, c0) = targets[0];
        var tip = file.Simulators[s0].Chains[c0].Nodes[^1];
        var size = tip.Radius * 100;
        if (Slider(current, "Size", "The tip's size when it bumps into the shapes below.", ref size, 0, 20, "%.1f cm"))
            Apply(current, targets, (_, chain, _) => SetMoving(chain, node => node.Radius, (node, value) => node.Radius = value, size / 100));

        var names = file.Collision.Names.Select(name => name.Text).Distinct().ToList();
        if (names.Count == 0)
        {
            Widgets.HintWrapped("The file has no collision shapes. Add some under Collision shapes.");
            return;
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Bumps into");
        Tip("Shapes this area stays out of, such as arms and legs. Each shape costs a little performance.");
        ImGui.SameLine(LabelWidth);
        using var group = ImRaii.Group();
        foreach (var name in names)
        {
            using var id = ImRaii.PushId(name);
            var reference = simulator.Collisions.FirstOrDefault(r => r.Name.Text == name);
            var on = reference != null;
            if (ImGui.Checkbox(name, ref on))
                Change(current, () =>
                {
                    if (on)
                        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From(name), Type = PhybCollisionType.Outside });
                    else
                        simulator.Collisions.RemoveAll(r => r.Name.Text == name);
                    CollideWithAll(simulator);
                });
            if (reference != null)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Theme.Scaled(110));
                using var combo = ImRaii.Combo("##type", TypeLabel(reference.Type));
                if (combo.Success)
                    foreach (var type in Enum.GetValues<PhybCollisionType>())
                        if (ImGui.Selectable(TypeLabel(type), reference.Type == type) && reference.Type != type)
                            Change(current, () => reference.Type = type);
            }
        }
    }

    private static string TypeLabel(PhybCollisionType type) => type switch
    {
        PhybCollisionType.Outside => "Stay outside",
        PhybCollisionType.Inside => "Stay inside",
        _ => "Stick to surface",
    };

    /// <summary> After the area's shapes change: every moving point bumps into all of them. </summary>
    private static void CollideWithAll(PhybSimulator simulator)
    {
        var count = simulator.Collisions.Count;
        var mask = count >= 32 ? uint.MaxValue : (1u << count) - 1;
        foreach (var chain in simulator.Chains)
            for (var n = 1; n < chain.Nodes.Count; n++)
                chain.Nodes[n].CollisionFlags = mask;
        if (count > 0)
            simulator.Flags |= PhybSimulatorFlags.CollisionsHandled;
    }

    // ---- Advanced ----

    private void DrawPoints(PhysicsDocument current, int index)
    {
        var file = current.File;
        var chains = file.Simulators[index].Chains;
        var shown = chainChoice >= 0 && chainChoice < chains.Count ? [chainChoice] : Enumerable.Range(0, chains.Count);
        foreach (var c in shown)
        {
            using var id = ImRaii.PushId($"points-{c}");
            var chain = chains[c];
            ImGui.TextColored(Theme.Label, PhysicsValidation.ChainLabel(chain, c));
            using var table = ImRaii.Table("##points", 7, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (!table.Success)
                continue;
            ImGui.TableSetupColumn("Point", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            foreach (var column in new[] { "Size cm", "Follow", "Wind", "Weight", "Swing °" })
                ImGui.TableSetupColumn(column, ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Axis", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableHeadersRow();
            var mirror = bothSides ? PhysicsAreas.MirrorChain(file, index, c) : null;
            for (var n = 0; n < chain.Nodes.Count; n++)
            {
                using var row = ImRaii.PushId(n);
                var node = chain.Nodes[n];
                PhybNode? twin = mirror is { } m && file.Simulators[m.Simulator].Chains[m.Chain].Nodes.Count == chain.Nodes.Count
                    ? file.Simulators[m.Simulator].Chains[m.Chain].Nodes[n]
                    : null;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(n == chain.Nodes.Count - 1 ? Theme.Muted : Theme.Text,
                    n == chain.Nodes.Count - 1 ? node.Bone.IsEmpty ? "Tip" : $"Tip ({node.Bone.Text})" : node.Bone.Text);
                Cell(current, "##size", node.Radius * 100, 0.05f, 0, 50, "%.1f", value => { node.Radius = value / 100; if (twin != null) twin.Radius = value / 100; });
                Cell(current, "##follow", node.AttractByAnimation, 0.001f, 0, 1, "%.3f", value => { node.AttractByAnimation = value; if (twin != null) twin.AttractByAnimation = value; });
                Cell(current, "##wind", node.WindScale, 0.0005f, 0, 1, "%.4f", value => { node.WindScale = value; if (twin != null) twin.WindScale = value; });
                Cell(current, "##weight", node.GravityScale, 0.01f, 0, 5, "%.2f", value => { node.GravityScale = value; if (twin != null) twin.GravityScale = value; });
                Cell(current, "##swing", PhysicsTemplates.Degrees(node.ConeMaxAngle), 0.5f, 0, 180, "%.0f",
                    value => { node.ConeMaxAngle = PhysicsTemplates.Radians(value); if (twin != null) twin.ConeMaxAngle = node.ConeMaxAngle; });
                ImGui.TableNextColumn();
                var axis = node.ConeAxisOffset;
                ImGui.SetNextItemWidth(-1);
                var changed = ImGui.DragFloat3("##axis", ref axis, 0.005f, -10, 10, "%.3f");
                if (ImGui.IsItemActivated())
                    current.Checkpoint();
                if (changed)
                {
                    node.ConeAxisOffset = axis;
                    current.Changed();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    Edited(current);
                Tip("The direction the swing is measured from, in the space of the bone's parent.");
            }
        }
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

    private void DrawSprings(PhysicsDocument current, int index)
    {
        var simulator = current.File.Simulators[index];
        if (simulator.Attracts.Count == 0)
            Widgets.Hint("No springs. Firmness above can add them.");
        for (var a = 0; a < simulator.Attracts.Count; a++)
        {
            using var id = ImRaii.PushId($"spring-{a}");
            var attract = simulator.Attracts[a];
            var chainLabel = attract.Chain >= 0 && attract.Chain < simulator.Chains.Count
                ? PhysicsValidation.ChainLabel(simulator.Chains[attract.Chain], attract.Chain)
                : $"Chain {attract.Chain + 1}";
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, $"{chainLabel}, point {attract.Node + 1}");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, $"toward {attract.Bone.Text}");
            var button = ImGui.GetFrameHeight();
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - button));
            var remove = a;
            if (Widgets.GhostIconButton("##remove-spring", FontAwesomeIcon.Times, "Remove this spring", new Vector2(button)))
            {
                Change(current, () => simulator.Attracts.RemoveAt(remove));
                break;
            }
            var stiffness = attract.Stiffness;
            ImGui.SetNextItemWidth(Theme.Scaled(120));
            var changed = ImGui.DragFloat("Strength##stiffness", ref stiffness, 0.0005f, -1, 1, "%.4f");
            if (ImGui.IsItemActivated())
                current.Checkpoint();
            if (changed)
            {
                attract.Stiffness = stiffness;
                current.Changed();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Edited(current);
            ImGui.SameLine();
            var offset = attract.Offset * 100;
            ImGui.SetNextItemWidth(Theme.Scaled(220));
            changed = ImGui.DragFloat3("Point cm##offset", ref offset, 0.05f, -200, 200, "%.1f");
            if (ImGui.IsItemActivated())
                current.Checkpoint();
            if (changed)
            {
                attract.Offset = offset / 100;
                current.Changed();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Edited(current);
            Tip($"Where the spring pulls to, in {attract.Bone.Text}'s space.");
        }
    }

    private void DrawMore(PhysicsDocument current, int index, List<(int Simulator, int Chain)> targets)
    {
        var file = current.File;
        var simulator = file.Simulators[index];
        var (s0, c0) = targets[0];
        var first = file.Simulators[s0].Chains[c0];

        var friction = first.Friction;
        if (Slider(current, "Friction", "How much it slows down while sliding along a shape.", ref friction, 0, 1, "%.3f"))
            Apply(current, targets, (_, chain, _) => chain.Friction = friction);
        var collisionDamping = first.CollisionDampening;
        if (Slider(current, "Bump damping", "How much a bump calms it down.", ref collisionDamping, 0, 1, "%.3f"))
            Apply(current, targets, (_, chain, _) => chain.CollisionDampening = collisionDamping);
        var repulsion = first.RepulsionStrength;
        if (Slider(current, "Push back", "How hard shapes push it out. Higher is snappier.", ref repulsion, 0, 2, "%.3f"))
            Apply(current, targets, (_, chain, _) => chain.RepulsionStrength = repulsion);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Point shape");
        ImGui.SameLine(LabelWidth);
        var capsule = first.Type == 1;
        if (ImGui.RadioButton("Spheres", !capsule) && capsule)
            Change(current, () => { foreach (var (s, c) in targets) file.Simulators[s].Chains[c].Type = 0; });
        ImGui.SameLine();
        if (ImGui.RadioButton("Capsules", capsule) && !capsule)
            Change(current, () => { foreach (var (s, c) in targets) file.Simulators[s].Chains[c].Type = 1; });
        Tip("Capsules also bump along the line between points.");

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
            first.LastBoneOffset = tipPlace / 100;
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        Tip("Where the tip sits, in the last bone's space. Only this chain changes.");

        ImGui.Spacing();
        Widgets.SectionHeader("Whole area");
        var gravity = simulator.Gravity;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Gravity");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3("##gravity", ref gravity, 0.05f, -50, 50, "%.2f");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            simulator.Gravity = gravity;
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);
        var windVector = simulator.Wind;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Wind");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
        changed = ImGui.DragFloat3("##wind-vector", ref windVector, 0.05f, -50, 50, "%.2f");
        if (ImGui.IsItemActivated())
            current.Checkpoint();
        if (changed)
        {
            simulator.Wind = windVector;
            current.Changed();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Edited(current);

        var steps = (int)simulator.ConstraintLoop;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Steps");
        Tip("How many times per frame the game settles the chains and the bumps. The game uses 2 and 1.");
        ImGui.SameLine(LabelWidth);
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        if (ImGui.InputInt("##constraint-loop", ref steps) )
            Change(current, () => simulator.ConstraintLoop = (short)Math.Clamp(steps, 0, 8));
        ImGui.SameLine();
        var collisionSteps = (int)simulator.CollisionLoop;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        if (ImGui.InputInt("##collision-loop", ref collisionSteps))
            Change(current, () => simulator.CollisionLoop = (short)Math.Clamp(collisionSteps, 0, 8));

        Flag(current, simulator, PhybSimulatorFlags.CollisionsHandled, "Bumps", "Off: it moves through every shape.");
        ImGui.SameLine();
        Flag(current, simulator, PhybSimulatorFlags.ContinuousCollisions, "Fast bumps", "Catches bumps between frames when it moves fast. Costs more.");
        ImGui.SameLine();
        Flag(current, simulator, PhybSimulatorFlags.UsingGroundPlane, "Ground", "Stays above the ground, such as when lying down.");
        ImGui.SameLine();
        Flag(current, simulator, PhybSimulatorFlags.FixedLength, "Fixed length", "Keeps the distances between points. The game always has this on.");
    }

    private void Flag(PhysicsDocument current, PhybSimulator simulator, PhybSimulatorFlags flag, string label, string tip)
    {
        var on = simulator.Flags.HasFlag(flag);
        if (ImGui.Checkbox($"{label}##flag-{flag}", ref on))
            Change(current, () => simulator.Flags = on ? simulator.Flags | flag : simulator.Flags & ~flag);
        Tip(tip);
    }
}
