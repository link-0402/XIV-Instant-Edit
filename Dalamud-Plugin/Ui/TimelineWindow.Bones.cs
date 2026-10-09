using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Bones window's content, drawn for <see cref="BonesWindow"/> from this window's state: the
/// body's bone map (Brio's artwork) and a list of every bone to select bones on, and the panel with
/// the rotation ball that turns, moves and scales them in the selected bone layer at the playhead.
/// The selection is shared with the properties panel's bone picker.
/// </summary>
public sealed partial class TimelineWindow
{
    private enum BonesPage : byte { Map, List }

    /// <summary>How a bone shows on the map and in the list, for the selected layer at the playhead.</summary>
    private enum BoneState : byte { Still, Animated, Changed, Keyed }

    private const string BonesHelp =
        "Click a dot to select a bone. Ctrl+click adds or removes one.\n" +
        "Shift+click selects a bone and every bone below it.\n" +
        "Changes go to the bone layer selected in the timeline, at the playhead.\n" +
        "Several selected bones turn and move the same way, about the character's axes.\n" +
        "A bone below another selected bone follows it.\n" +
        "Drag a ring of the ball, or turn the wheel over it, to turn. Hold Shift to turn finer.\n" +
        "In GPose the bones show on the character too. Click a dot there to select it.\n" +
        "With Live on and paused, drag the gizmo on the character to turn or move the bones.";

    private readonly BonesWindow bonesWindow;
    private readonly ITextureProvider textures;
    private readonly PoseSelection selection = new();
    private BonesPage bonesPage = BonesPage.Map;
    private bool swapSides;
    private bool characterAxes;
    // A drag on the rotation ball is running as one undo step.
    private bool ballEditing;
    private string boneSearch = string.Empty;
    // Why choosing the source skeleton here failed.
    private string? skeletonChoiceError;
    // The skeleton the selection was last checked against.
    private string? selectionSkeleton;
    // Bone indices by name and children by parent, for one skeleton.
    private (SkeletonDescription Skeleton, Dictionary<string, int> Index, int[][] Children)? boneTree;
    // The bones the clip moves, for one skeleton match.
    private (SkeletonResolution? Resolution, HashSet<string> Bones)? animatedBones;
    // The clip's own pose at one frame, local and in model space, while the decode and the frame stay.
    private (MotionClip Clip, int Frame, MotionTransform[] Local, MotionTransform[] Model)? motionPose;
    // The bones' frames for one document, layer, frame and decode.
    private (TimelineDocument Doc, string Layer, int Frame, MotionClip Clip, BoneFrames Frames)? boneFrames;

    internal BonesWindow Bones => bonesWindow;

    private void ToggleBones()
    {
        bonesWindow.IsOpen = !bonesWindow.IsOpen;
        if (bonesWindow.IsOpen)
            bonesWindow.BringToFront();
    }

    /// <summary>The Bones window's whole content; called from its draw.</summary>
    internal void DrawBones()
    {
        if (animations == null)
        {
            Widgets.Banner("##bones-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation editing is unavailable.");
            return;
        }
        if (source is not { Document: not null } loaded || editor is not { } current)
        {
            Widgets.EmptyState(FontAwesomeIcon.Child, "No timeline open",
                "Open or load an animation in the Timeline window. This window poses its bones.");
            return;
        }
        if (loaded.Standalone)
        {
            Widgets.EmptyState(FontAwesomeIcon.Smile, "An expression", "An expression's face takes no bone layers.");
            return;
        }
        var fresh = Fresh(loaded);
        if (fresh.Clip.Resolution?.Selected?.Skeleton is not { } skeleton)
        {
            if (fresh.Clip.Resolution is { State: SkeletonResolutionState.Ambiguous, Candidates.Length: > 0 } open)
            {
                Widgets.EmptyState(FontAwesomeIcon.Child, "Choose the skeleton",
                    "Several skeletons fit this animation. Choose the one it was made for.");
                DrawSkeletonChoice(fresh, open);
            }
            else
                Widgets.EmptyState(FontAwesomeIcon.Child, "Skeleton unknown",
                    fresh.Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source first.");
            return;
        }
        KeepSelection(skeleton);
        var layer = current.SelectedLayerValue;
        DrawBonesHeader(current, loaded, layer);
        DrawBonesTools(skeleton, layer);

        var avail = ImGui.GetContentRegionAvail();
        var panelWidth = Math.Clamp(avail.X * .34f, Theme.Scaled(250), Theme.Scaled(340));
        var pagesWidth = Math.Max(Theme.Scaled(160), avail.X - panelWidth - ImGui.GetStyle().ItemSpacing.X);
        using (var pages = ImRaii.Child("##bones-pages", new Vector2(pagesWidth, avail.Y), false,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (pages.Success)
            {
                if (bonesPage == BonesPage.Map)
                    DrawBoneMap(skeleton, layer, current.Playhead, fresh.Clip, ImGui.GetContentRegionAvail());
                else
                    DrawBoneList(skeleton, layer, current.Playhead, fresh.Clip);
            }
        }
        ImGui.SameLine();
        using (var panel = ImRaii.Child("##bones-panel", new Vector2(0, avail.Y), true, ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel.Success)
                DrawBonesPanel(current, loaded, layer, skeleton);
        }
        HandleBonesKeys(current);
    }

    /// <summary>
    /// The skeletons that fit the clip equally well, to choose the one it was made for. The choice is
    /// shared with the Timeline window's Source page, so both keep it.
    /// </summary>
    private void DrawSkeletonChoice(AnimationTimelineSource fresh, SkeletonResolution resolution)
    {
        ImGui.Spacing();
        var width = Math.Min(Theme.Scaled(480), ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
        ImGui.SetNextItemWidth(width);
        using (ImRaii.Disabled(animations!.Busy))
        using (var combo = ImRaii.Combo("##bones-skeleton", "Choose the skeleton it was made for"))
        {
            if (combo.Success)
                foreach (var candidate in resolution.Candidates)
                {
                    if (ImGui.Selectable($"{AnimationPresentation.SkeletonLabel(candidate)} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)}##{AnimationSkeletonIndex.SelectionId(candidate)}"))
                    {
                        try
                        {
                            source = fresh with { Clip = animations.Observer.ChooseSkeleton(fresh.Clip, candidate) };
                            skeletonChoiceError = null;
                        }
                        catch (InvalidOperationException e)
                        {
                            skeletonChoiceError = e.Message;
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                }
        }
        if (skeletonChoiceError != null)
            Widgets.HintWrapped(skeletonChoiceError);
    }

    /// <summary> Live and pause, the layer's previous and next key, the layer and the playhead. </summary>
    private void DrawBonesHeader(TimelineEditor current, AnimationTimelineSource loaded, MotionLayer? layer)
    {
        DrawLiveToggle(current, loaded, "##bones-live");
        ImGui.SameLine(0, Theme.Scaled(2));
        var shown = previewFingerprint != null;
        var playing = playback.Active && !playback.Paused;
        if (Widgets.IconButton("##bones-play", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                !shown ? "Turn on Live first." : playing ? "Pause" : playback.Active ? "Resume" : "Play",
                shown && PlaybackAvailable(out _)))
            TogglePlay();
        ImGui.SameLine(0, Theme.Scaled(8));
        var frame = current.Playhead;
        int? previous = null, next = null;
        if (layer != null)
            foreach (var key in layer.Keys)
            {
                if (key.Frame < frame)
                    previous = key.Frame;
                else if (key.Frame > frame && next == null)
                    next = key.Frame;
            }
        if (Widgets.IconButton("##bones-previous-key", FontAwesomeIcon.StepBackward, "Go to the layer's previous key", previous != null) && previous is { } back)
        {
            current.Playhead = back;
            current.SelectLayer(layer!.Key, back);
        }
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##bones-next-key", FontAwesomeIcon.StepForward, "Go to the layer's next key", next != null) && next is { } ahead)
        {
            current.Playhead = ahead;
            current.SelectLayer(layer!.Key, ahead);
        }
        ImGui.SameLine(0, Theme.Scaled(12));
        if (layer != null)
        {
            Widgets.Icon(LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, layer.Name);
            if (!layer.Enabled)
            {
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "(off)");
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "No bone layer selected");
        }
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "· " + TimelineWindowViews.FrameText(frame));
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip(BonesHelp);
    }

    /// <summary> Map or list, the selection's groups, swapping sides and the selection's size. </summary>
    private void DrawBonesTools(SkeletonDescription skeleton, MotionLayer? layer)
    {
        if (Widgets.TabButton(FontAwesomeIcon.Child, "Map", bonesPage == BonesPage.Map))
            bonesPage = BonesPage.Map;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.TabButton(FontAwesomeIcon.List, "All bones", bonesPage == BonesPage.List))
            bonesPage = BonesPage.List;
        ImGui.SameLine(0, Theme.Scaled(12));
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ObjectGroup, "Select", null, null, null, null))
            ImGui.OpenPopup("##bones-groups");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Select a group of bones. Hold Ctrl to add them to the selection.");
        DrawGroupMenu(skeleton, layer);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (bonesPage == BonesPage.Map)
        {
            if (Toggle("##bones-swap", FontAwesomeIcon.ExchangeAlt, swapSides, swapSides
                    ? "The left side's bones show on the right, as seen from the front. Click to show them as in a mirror."
                    : "The left side's bones show on the left, as in a mirror. Click to show them as seen from the front."))
                swapSides = !swapSides;
            ImGui.SameLine(0, Theme.Scaled(2));
        }
        if (Toggle("##bones-overlay", FontAwesomeIcon.Crosshairs, showOnCharacter, showOnCharacter
                ? "In GPose the bones show on the character, to select them there and pose them with the gizmo. Click to hide them."
                : "Show the bones on the character in GPose, to select them there and pose them with the gizmo."))
            showOnCharacter = !showOnCharacter;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##bones-clear", FontAwesomeIcon.Times, "Clear the selection", selection.Count > 0))
            selection.Clear();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, selection.Count switch
        {
            0 => "Nothing selected",
            1 => AnimationPresentation.BoneName(selection.Primary!),
            _ => $"{selection.Count} bones selected",
        });
    }

    private void DrawGroupMenu(SkeletonDescription skeleton, MotionLayer? layer)
    {
        using var popup = ImRaii.Popup("##bones-groups");
        if (!popup.Success)
            return;
        var add = ImGui.GetIO().KeyCtrl;
        var bones = skeleton.Bones.Select(bone => bone.Name).ToArray();
        if (layer != null)
        {
            string[] changed = [.. bones.Where(bone => PoseEdits.Changes(layer, bone))];
            using (ImRaii.Disabled(changed.Length == 0))
            {
                if (ImGui.MenuItem("Bones this layer changes"))
                    selection.Set(changed, add);
            }
            ImGui.Separator();
        }
        foreach (var group in PoseMap.Groups)
        {
            string[] members = [.. bones.Where(group.Contains)];
            if (members.Length > 0 && ImGui.MenuItem(group.Name))
                selection.Set(members, add);
        }
    }

    /// <summary>Drops selected bones another clip's skeleton does not have.</summary>
    private void KeepSelection(SkeletonDescription skeleton)
    {
        if (selectionSkeleton == skeleton.Fingerprint)
            return;
        var index = BoneTree(skeleton).Index;
        selection.Keep(index.ContainsKey);
        selectionSkeleton = skeleton.Fingerprint;
    }

    private (Dictionary<string, int> Index, int[][] Children) BoneTree(SkeletonDescription skeleton)
    {
        // The decode's skeleton and the match's are the same one, not always the same instance.
        if (boneTree is { } cached && (ReferenceEquals(cached.Skeleton, skeleton) ||
                                       cached.Skeleton.Fingerprint == skeleton.Fingerprint && cached.Skeleton.Bones.Length == skeleton.Bones.Length))
            return (cached.Index, cached.Children);
        var index = MotionEvaluator.Index(skeleton);
        var children = new List<int>[skeleton.Bones.Length];
        for (var b = 0; b < children.Length; b++)
            children[b] = [];
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (skeleton.Bones[b].Parent is var parent and >= 0 && parent < children.Length)
                children[parent].Add(b);
        var tree = children.Select(list => list.ToArray()).ToArray();
        boneTree = (skeleton, index, tree);
        return (index, tree);
    }

    /// <summary>The bones the clip moves; every bone while that is unknown.</summary>
    private HashSet<string>? AnimatedBones(AnimationClip clip)
    {
        if (animatedBones is { } cached && ReferenceEquals(cached.Resolution, clip.Resolution))
            return cached.Bones.Count == 0 ? null : cached.Bones;
        var bones = AnimationBones.Animated(clip).ToHashSet(StringComparer.Ordinal);
        animatedBones = (clip.Resolution, bones);
        return bones.Count == 0 ? null : bones;
    }

    private static BoneState StateOf(string bone, MotionLayer? layer, int frame, HashSet<string>? animated)
    {
        if (layer != null)
        {
            if (layer.Kind != MotionLayerKind.Amplitude && PoseEdits.Keyed(layer, bone, frame))
                return BoneState.Keyed;
            if (PoseEdits.Changes(layer, bone))
                return BoneState.Changed;
        }
        return animated == null || animated.Contains(bone) ? BoneState.Animated : BoneState.Still;
    }

    private static string StateText(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed => "Keyed here in this layer.",
        BoneState.Changed => layer?.Kind == MotionLayerKind.Amplitude ? "This layer scales its movement." : "This layer changes it.",
        BoneState.Animated => "The clip moves it.",
        _ => "The clip doesn't move it.",
    };

    private static Vector4 StateColour(BoneState state, MotionLayer? layer) => state switch
    {
        BoneState.Keyed or BoneState.Changed when layer != null => TimelineWindowViews.LayerColour(layer.Kind),
        BoneState.Still => Theme.Inactive,
        _ => Theme.Label,
    };

    /// <summary>
    /// A click on <paramref name="bone"/> on the map or in the list: alone it selects only the bone,
    /// Ctrl adds or removes it, Shift selects it with every bone below it (Ctrl+Shift adds them).
    /// </summary>
    private void ClickBone(string bone, SkeletonDescription skeleton)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift)
        {
            var (index, children) = BoneTree(skeleton);
            var branch = new List<string>();
            var stack = new Stack<int>();
            if (index.TryGetValue(bone, out var start))
                stack.Push(start);
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                branch.Add(skeleton.Bones[b].Name);
                for (var c = children[b].Length - 1; c >= 0; c--)
                    stack.Push(children[b][c]);
            }
            selection.Set(branch, io.KeyCtrl, bone);
            return;
        }
        selection.Click(bone, io.KeyCtrl);
    }

    /// <summary>
    /// Brio's bone map: the body, its armor and its hands with toes and tail, each bone a dot over the
    /// artwork joined to the nearest dot above it. The bones below the primary one, which move with it,
    /// have their lines lit. A click on empty space clears the selection.
    /// </summary>
    private void DrawBoneMap(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationClip clip, Vector2 size)
    {
        var (index, _) = BoneTree(skeleton);
        var animated = AnimatedBones(clip);
        var origin = ImGui.GetCursorScreenPos();
        size = Vector2.Max(size, new Vector2(1));
        ImGui.InvisibleButton("##bone-map", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var drawList = ImGui.GetWindowDrawList();

        var placements = PoseMap.Layout(size, Theme.Gap,
            PoseMap.Armor.Spots.Any(spot => Has(index, spot.Bone)),
            PoseMap.Toes.Spots.Any(spot => spot.Bone.StartsWith("iv_", StringComparison.Ordinal) && Has(index, spot.Bone)),
            PoseMap.Tail.Spots.Any(spot => Has(index, spot.Bone)));
        var dots = new List<(string Bone, Vector2 At, int Section, bool Handle)>();
        for (var p = 0; p < placements.Count; p++)
        {
            var placement = placements[p];
            var min = origin + placement.Origin;
            var image = textures.GetFromManifestResource(typeof(TimelineWindow).Assembly, PoseMap.Resource(placement.Section)).GetWrapOrEmpty();
            drawList.AddImage(image.Handle, min, min + placement.Section.Size * placement.Scale);
            foreach (var spot in PoseMap.Place(placement.Section, swapSides))
                if (Has(index, spot.Bone))
                    dots.Add((spot.Bone, min + spot.Position * placement.Scale, p, spot.Handle));
        }

        var mouse = ImGui.GetMousePos();
        var reach = Theme.Scaled(14);
        string? hover = null;
        var best = float.MaxValue;
        if (hovered)
            foreach (var dot in dots)
            {
                var distance = Vector2.Distance(dot.At, mouse);
                if (distance < reach && distance < best)
                {
                    best = distance;
                    hover = dot.Bone;
                }
            }

        // Lines first, from each dot to the nearest dot above it on the same picture.
        var placed = new Dictionary<(int, string), Vector2>();
        foreach (var dot in dots)
            if (!dot.Handle)
                placed[(dot.Section, dot.Bone)] = dot.At;
        var primary = selection.Primary is { } chosen && index.TryGetValue(chosen, out var chosenIndex) ? chosenIndex : -1;
        var lineColour = ImGui.GetColorU32(Theme.WithAlpha(Theme.Label, .35f));
        var litColour = ImGui.GetColorU32(Theme.WithAlpha(Theme.Accent, .85f));
        var radius = Theme.Scaled(6);
        foreach (var dot in dots)
        {
            if (dot.Handle)
                continue;
            var bone = index[dot.Bone];
            for (var parent = skeleton.Bones[bone].Parent; parent >= 0; parent = skeleton.Bones[parent].Parent)
            {
                if (!placed.TryGetValue((dot.Section, skeleton.Bones[parent].Name), out var above))
                    continue;
                var along = above - dot.At;
                if (along.LengthSquared() > radius * radius * 4)
                {
                    var step = Vector2.Normalize(along) * radius;
                    // A lit line: the bone moves with the primary bone.
                    var lit = IsBelow(skeleton, bone, primary);
                    drawList.AddLine(dot.At + step, above - step, lit ? litColour : lineColour, Theme.Scaled(lit ? 2 : 1));
                }
                break;
            }
        }
        if (dots.Count == 0)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(Theme.Gap));
            Widgets.HintWrapped("This skeleton has none of the map's bones. Pick them under All bones.");
        }

        foreach (var dot in dots)
        {
            var state = StateOf(dot.Bone, layer, frame, animated);
            var colour = ImGui.GetColorU32(StateColour(state, layer));
            var selected = selection.Contains(dot.Bone);
            var isPrimary = selected && string.Equals(selection.Primary, dot.Bone, StringComparison.Ordinal);
            drawList.AddCircleFilled(dot.At, radius + Theme.Scaled(1.5f), 0xC0000000);
            drawList.AddCircleFilled(dot.At, radius, ImGui.GetColorU32(Theme.PanelBg));
            if (selected)
                drawList.AddCircleFilled(dot.At, radius - Theme.Scaled(1.5f), ImGui.GetColorU32(Theme.WithAlpha(Theme.Accent, .85f)));
            drawList.AddCircle(dot.At, radius, colour, 0, Theme.Scaled(state is BoneState.Changed or BoneState.Keyed ? 2.5f : 1.5f));
            if (state == BoneState.Keyed)
                drawList.AddCircleFilled(dot.At, radius * .45f, colour);
            if (isPrimary)
                drawList.AddCircle(dot.At, radius + Theme.Scaled(3), ImGui.GetColorU32(Theme.Accent), 0, Theme.Scaled(1.5f));
            if (dot.Bone == hover)
                drawList.AddCircle(dot.At, radius + Theme.Scaled(1), ImGui.GetColorU32(Theme.Text), 0, Theme.Scaled(1.5f));
        }

        if (hover != null)
        {
            ImGui.SetTooltip($"{AnimationPresentation.BoneName(hover)}\n{StateText(StateOf(hover, layer, frame, animated), layer)}");
            if (clicked)
                ClickBone(hover, skeleton);
        }
        else if (clicked && !ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
            selection.Clear();
    }

    private static bool Has(Dictionary<string, int> index, string bone) => index.ContainsKey(bone);

    /// <summary>Whether bone <paramref name="bone"/> sits below <paramref name="ancestor"/> in the skeleton.</summary>
    private static bool IsBelow(SkeletonDescription skeleton, int bone, int ancestor)
    {
        if (ancestor < 0)
            return false;
        for (var parent = skeleton.Bones[bone].Parent; parent >= 0; parent = skeleton.Bones[parent].Parent)
            if (parent == ancestor)
                return true;
        return false;
    }

    /// <summary>Every bone of the skeleton as a tree, or the ones a search finds as a flat list, coloured like the map's dots.</summary>
    private void DrawBoneList(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationClip clip)
    {
        var (_, children) = BoneTree(skeleton);
        var animated = AnimatedBones(clip);
        Widgets.SearchBox("##bones-search", ref boneSearch, "Search bones");
        using var list = ImRaii.Child("##bones-list", Vector2.Zero, false);
        if (!list.Success)
            return;
        if (boneSearch.Length > 0)
        {
            for (var b = 0; b < skeleton.Bones.Length; b++)
            {
                var name = skeleton.Bones[b].Name;
                if (!AnimationPresentation.BoneName(name).Contains(boneSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                DrawBoneRow(skeleton, b, layer, frame, animated, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
            }
            return;
        }
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (skeleton.Bones[b].Parent < 0)
                DrawBoneBranch(skeleton, children, b, 0, layer, frame, animated);
    }

    private void DrawBoneBranch(SkeletonDescription skeleton, int[][] children, int bone, int depth, MotionLayer? layer, int frame,
        HashSet<string>? animated)
    {
        var leaf = children[bone].Length == 0;
        var flags = leaf ? ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen
            : depth < 3 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!DrawBoneRow(skeleton, bone, layer, frame, animated, flags) || leaf)
            return;
        foreach (var child in children[bone])
            DrawBoneBranch(skeleton, children, child, depth + 1, layer, frame, animated);
        ImGui.TreePop();
    }

    /// <summary>One bone's row; true when it is open and its children follow.</summary>
    private bool DrawBoneRow(SkeletonDescription skeleton, int bone, MotionLayer? layer, int frame, HashSet<string>? animated,
        ImGuiTreeNodeFlags flags)
    {
        var name = skeleton.Bones[bone].Name;
        var state = StateOf(name, layer, frame, animated);
        if (selection.Contains(name))
            flags |= ImGuiTreeNodeFlags.Selected;
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, StateColour(state, layer), state != BoneState.Animated))
            open = ImGui.TreeNodeEx($"{AnimationPresentation.BoneName(name)}{(state == BoneState.Keyed ? "  · key" : "")}##bone-{bone}",
                flags | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            ClickBone(name, skeleton);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(StateText(state, layer));
        return open;
    }

    /// <summary> The panel beside the map: the bone editor with the ball, an amplitude's bones, or how to start. </summary>
    private void DrawBonesPanel(TimelineEditor current, AnimationTimelineSource loaded, MotionLayer? layer, SkeletonDescription skeleton)
    {
        var readOnly = ReadOnly;
        if (layer == null)
        {
            Widgets.MutedWrapped("Select a bone layer in the timeline to pose its bones, or add one over the whole clip:");
            ImGui.Spacing();
            using (ImRaii.Disabled(readOnly))
            {
                foreach (var kind in TimelineWindowViews.LayerKinds)
                {
                    if (ImGui.Button($"{MotionEdits.KindName(kind)} layer"))
                        current.AddLayer(kind, 0, Math.Max(TimelineDocument.MinLength, current.Document.Length));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(TimelineWindowViews.LayerKindHint(kind));
                }
            }
            return;
        }
        Widgets.Icon(LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, MotionEdits.KindName(layer.Kind));
        ImGui.Spacing();
        if (layer.Kind == MotionLayerKind.Amplitude)
            DrawLayerLimit(current, layer, skeleton, readOnly, false);
        else
            DrawBoneEditor(current, layer, skeleton, loaded, readOnly, true);
        if (layer.Kind == MotionLayerKind.Pose)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawPoseSources(current, layer, skeleton, loaded, readOnly);
        }
    }

    /// <summary>
    /// The rotation ball over the primary bone: its rings show the bone's own axes as the camera sees
    /// it, or the character's. A drag is one undo step and turns every bone the edit changes (see
    /// <see cref="PoseEdits.Turned"/>). With several of them the rings are the character's: each bone's
    /// own axes point elsewhere, so only those turn them all the same way.
    /// </summary>
    private void DrawBall(TimelineEditor current, MotionLayer layer, BoneFrames? frames, IReadOnlyList<PoseTarget> turned,
        PoseTarget primary, int frame)
    {
        if (CameraRotation() is not { } view)
            return;
        var actor = ActorRotation() ?? Quaternion.Identity;
        var several = turned.Count > 1;
        var character = characterAxes || several;
        // The primary bone as the layers turn it, in character space.
        Quaternion? bone = frames is { } known ? Quaternion.Normalize(known.Space(primary.Bone) * Quaternion.Normalize(primary.Value.Rotation)) : null;
        var ring = character || bone is not { } own ? actor : actor * own;
        var size = Math.Min(ImGui.GetContentRegionAvail().X, Theme.Scaled(220));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - size) / 2));
        var result = RotationBall.Draw("##bone-ball", size, ring, view);
        // Turns need the bones' frames from the motion, which may still be read.
        if (result.Started && frames != null)
        {
            current.BeginFieldEdit();
            ballEditing = true;
        }
        if (result.Turn is { } turn && frames != null && bone is { } axes)
            current.Change(PoseEdits.Turn(current.Document, layer.Key, frame, turned, character ? turn : PoseEdits.Around(axes, turn), frames.Space));
        if (result.Ended && ballEditing)
        {
            current.EndFieldEdit();
            ballEditing = false;
        }
        if (frames == null)
            DrawMotionWait();
        using (ImRaii.Disabled(several))
        {
            if (ImGui.RadioButton("Bone axes", !character))
                characterAxes = false;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several
                ? "Several bones turn about the character's axes: their own axes point different ways."
                : "The ball and the gizmo use the bone's own axes.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Character axes", character))
            characterAxes = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The ball and the gizmo use the character's axes: up, forward and to the side.");
        if (OverlayWanted)
            DrawGizmoChoice(layer, frame);
    }

    /// <summary> Whether the gizmo on the character turns or moves, and what it waits for. </summary>
    private void DrawGizmoChoice(MotionLayer layer, int frame)
    {
        var turns = layer.Components.HasFlag(PoseComponents.Rotation);
        var moves = layer.Components.HasFlag(PoseComponents.Position);
        var move = moves && (gizmoMove || !turns);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Gizmo");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(!turns))
        {
            if (ImGui.RadioButton("Turn##gizmo-turn", !move))
                gizmoMove = false;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(turns ? "The gizmo on the character turns the bones." : "This layer doesn't turn bones.");
        ImGui.SameLine();
        using (ImRaii.Disabled(!moves))
        {
            if (ImGui.RadioButton("Move##gizmo-move", move))
                gizmoMove = true;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(moves ? "The gizmo on the character moves the bones." : "This layer doesn't move bones.");
        if (GizmoOperation(layer, frame, out var reason) == null && reason != null)
            Widgets.HintWrapped(reason);
    }

    /// <summary>
    /// The bones' frames at the playhead for the selected layer, from the clip with its offset and pose
    /// layers applied there: what edits made to several bones at once and the rotation ball turn in.
    /// </summary>
    private sealed class BoneFrames(SkeletonDescription skeleton, Dictionary<string, int> index, MotionTransform[] before,
        MotionTransform[] model, bool offset)
    {
        /// <summary>
        /// The rotation from character space to the space a bone's value turns in: for an offset the bone's
        /// own rotation as the layers before this one leave it, under its parent as all of them do; for a
        /// pose its parent's.
        /// </summary>
        public Quaternion Space(string bone)
        {
            if (!index.TryGetValue(bone, out var at))
                return Quaternion.Identity;
            var parent = skeleton.Bones[at].Parent;
            var above = parent >= 0 ? model[parent].Rotation : Quaternion.Identity;
            return Quaternion.Normalize(offset ? above * before[at].Rotation : above);
        }
    }

    /// <summary>
    /// The bones' frames at <paramref name="frame"/> for <paramref name="layer"/>; null while the clip's motion is
    /// read, which this starts. Kept while the document, the layer, the frame and the decode stay.
    /// </summary>
    private BoneFrames? FramesAt(AnimationTimelineSource loaded, TimelineDocument doc, MotionLayer layer, int frame)
    {
        if (MotionPose(loaded, frame) is not { } own || decoded is not { } clip)
            return null;
        if (boneFrames is { } cached && ReferenceEquals(cached.Doc, doc) && cached.Layer == layer.Key && cached.Frame == frame &&
            ReferenceEquals(cached.Clip, clip))
            return cached.Frames;
        var skeleton = clip.Skeleton;
        var at = Math.Max(0, MotionEdits.IndexOf(doc, layer.Key));
        MotionTransform[] before, after;
        try
        {
            var loop = LoopFrames(loaded, doc);
            var end = MotionEnd(loaded, doc);
            before = MotionEvaluator.ApplyAt(skeleton, own.Local, doc.Motion.Take(at).ToArray(), frame, loop, end);
            after = MotionEvaluator.ApplyAt(skeleton, own.Local, doc.Motion, frame, loop, end);
        }
        catch (InvalidDataException)
        {
            // A layer names a bone this skeleton lacks: the clip's own pose still gives frames.
            before = after = own.Local;
        }
        var frames = new BoneFrames(skeleton, BoneTree(skeleton).Index, before, MotionEvaluator.ModelSpace(skeleton, after),
            layer.Kind == MotionLayerKind.Offset);
        boneFrames = (doc, layer.Key, frame, clip, frames);
        return frames;
    }

    /// <summary> In place of what needs the clip's motion: the reading's progress, or why it failed. </summary>
    private void DrawMotionWait()
    {
        if (decodeError is { } error)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, "The motion could not be read: " + error);
            return;
        }
        Widgets.Spinner();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, decodeStatus);
    }

    /// <summary>The rotation of the game camera's view, for the ball; null without a camera.</summary>
    private static unsafe Quaternion? CameraRotation()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return null;
        var camera = manager->GetActiveCamera();
        if (camera == null)
            return null;
        Matrix4x4 view = camera->CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1;
        return Matrix4x4.Decompose(view, out _, out var rotation, out _) ? Quaternion.Normalize(rotation) : null;
    }

    private Quaternion? ActorRotation()
    {
        try { return playback.ActorRotation(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The clip's own pose at <paramref name="frame"/>, local and in model space, from the decoded motion;
    /// null while it is read, which this starts.
    /// </summary>
    private (MotionTransform[] Local, MotionTransform[] Model)? MotionPose(AnimationTimelineSource loaded, int frame)
    {
        var fresh = Fresh(loaded);
        if (decoded == null || decodedKey != DecodeKey(fresh))
        {
            EnsureDecode(fresh);
            return null;
        }
        if (motionPose is { } cached && ReferenceEquals(cached.Clip, decoded) && cached.Frame == frame)
            return (cached.Local, cached.Model);
        var local = decoded.Pose(frame);
        var model = MotionEvaluator.ModelSpace(decoded.Skeleton, local);
        motionPose = (decoded, frame, local, model);
        return (local, model);
    }

    /// <summary> In the Bones window: undo and redo, and the arrows step the playhead. </summary>
    private void HandleBonesKeys(TimelineEditor current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var io = ImGui.GetIO();
        if (io.WantTextInput)
            return;
        // The arrows step the playhead here; the game must not take them as well.
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            return;
        var readOnly = ReadOnly;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (!readOnly)
                current.Key(io.KeyShift ? TimelineKey.Redo : TimelineKey.Undo);
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y))
        {
            if (!readOnly)
                current.Key(TimelineKey.Redo);
        }
        else if (!io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
            current.Key(TimelineKey.Left, io.KeyShift);
        else if (!io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
            current.Key(TimelineKey.Right, io.KeyShift);
    }
}
