using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Bones window's content, drawn for <see cref="BonesWindow"/>: the body's bone map (Brio's artwork)
/// and a list of every bone to select bones on, and the panel with the rotation ball that turns, moves
/// and scales them in the Timeline window's selected bone layer at the playhead. The selection is
/// shared with the properties panel's bone picker and the bones on the character.
/// </summary>
internal sealed class BonesPanel(TimelineSession session, string? animationError, BonePosing posing, BoneEditor boneEditor,
    PoseSourcePanel poseSources, TimelinePreview timelinePreview, IBonesOverlay overlay, ITextureProvider textures)
{
    private enum BonesPage : byte { Map, List }

    private const string BonesHelp =
        "Click a dot to select a bone. Ctrl+click adds or removes one.\n" +
        "Shift+click selects a bone and every bone below it.\n" +
        "Changes go to the bone layer selected in the timeline, at the playhead.\n" +
        "Several selected bones turn and move the same way, about the character's axes.\n" +
        "A bone below another selected bone follows it.\n" +
        "Drag a ring of the ball, or turn the wheel over it, to turn. Hold Shift to turn finer.\n" +
        "In GPose the bones show on the character too. Click a dot there to select it.\n" +
        "With Live on and paused, drag the gizmo on the character to turn or move the bones.";

    private BonesPage bonesPage = BonesPage.Map;
    private bool swapSides;
    private string boneSearch = string.Empty;

    /// <summary>The Bones window's whole content; called from its draw.</summary>
    public void Draw()
    {
        if (session.Animations == null)
        {
            Widgets.Banner("##bones-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation editing is unavailable.");
            return;
        }
        if (session.Source is not { Document: not null } loaded || session.Editor is not { } current)
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
        var fresh = session.Fresh(loaded);
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
        posing.KeepSelection(skeleton);
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
        using (ImRaii.Disabled(session.Animations!.Busy))
        using (var combo = ImRaii.Combo("##bones-skeleton", "Choose the skeleton it was made for"))
        {
            if (combo.Success)
                foreach (var candidate in resolution.Candidates)
                {
                    if (ImGui.Selectable($"{AnimationPresentation.SkeletonLabel(candidate)} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)}##{AnimationSkeletonIndex.SelectionId(candidate)}"))
                        posing.ChooseSkeleton(fresh, candidate);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                }
        }
        if (posing.ChoiceError is { } choiceError)
            Widgets.HintWrapped(choiceError);
    }

    /// <summary> Live and pause, the layer's previous and next key, the layer and the playhead. </summary>
    private void DrawBonesHeader(TimelineEditor current, AnimationTimelineSource loaded, MotionLayer? layer)
    {
        timelinePreview.DrawLiveToggle(current, loaded, "##bones-live");
        ImGui.SameLine(0, Theme.Scaled(2));
        var shown = timelinePreview.Fingerprint != null;
        var playing = session.Playback.Active && !session.Playback.Paused;
        if (Widgets.IconButton("##bones-play", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                !shown ? "Turn on Live first." : playing ? "Pause" : session.Playback.Active ? "Resume" : "Play",
                shown && timelinePreview.PlaybackAvailable(out _)))
            timelinePreview.TogglePlay();
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
            Widgets.Icon(TimelineUi.LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
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
            if (TimelineUi.Toggle("##bones-swap", FontAwesomeIcon.ExchangeAlt, swapSides, swapSides
                    ? "The left side's bones show on the right, as seen from the front. Click to show them as in a mirror."
                    : "The left side's bones show on the left, as in a mirror. Click to show them as seen from the front."))
                swapSides = !swapSides;
            ImGui.SameLine(0, Theme.Scaled(2));
        }
        if (TimelineUi.Toggle("##bones-overlay", FontAwesomeIcon.Crosshairs, overlay.OnCharacter, overlay.OnCharacter
                ? "In GPose the bones show on the character, to select them there and pose them with the gizmo. Click to hide them."
                : "Show the bones on the character in GPose, to select them there and pose them with the gizmo."))
            overlay.OnCharacter = !overlay.OnCharacter;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##bones-clear", FontAwesomeIcon.Times, "Clear the selection", posing.Selection.Count > 0))
            posing.Selection.Clear();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, posing.Selection.Count switch
        {
            0 => "Nothing selected",
            1 => AnimationPresentation.BoneName(posing.Selection.Primary!),
            _ => $"{posing.Selection.Count} bones selected",
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
                    posing.Selection.Set(changed, add);
            }
            ImGui.Separator();
        }
        foreach (var group in PoseMap.Groups)
        {
            string[] members = [.. bones.Where(group.Contains)];
            if (members.Length > 0 && ImGui.MenuItem(group.Name))
                posing.Selection.Set(members, add);
        }
    }

    /// <summary>
    /// Brio's bone map: the body, its armor and its hands with toes and tail, each bone a dot over the
    /// artwork joined to the nearest dot above it. The bones below the primary one, which move with it,
    /// have their lines lit. A click on empty space clears the selection.
    /// </summary>
    private void DrawBoneMap(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationClip clip, Vector2 size)
    {
        var (index, _) = posing.BoneTree(skeleton);
        var animated = posing.AnimatedBones(clip);
        var origin = ImGui.GetCursorScreenPos();
        size = Vector2.Max(size, new Vector2(1));
        ImGui.InvisibleButton("##bone-map", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var drawList = ImGui.GetWindowDrawList();

        var placements = PoseMap.Layout(size, Theme.Gap,
            PoseMap.Armor.Spots.Any(spot => Has(index, spot.Bone)),
            PoseMap.Toes.Spots.Any(spot => spot.Bone.StartsWith("iv_", StringComparison.Ordinal) && Has(index, spot.Bone)),
            PoseMap.Groin.Spots.Any(spot => Has(index, spot.Bone)),
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
        var primary = posing.Selection.Primary is { } chosen && index.TryGetValue(chosen, out var chosenIndex) ? chosenIndex : -1;
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
                    var lit = BonePosing.IsBelow(skeleton, bone, primary);
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
            var state = BonePosing.StateOf(dot.Bone, layer, frame, animated);
            var colour = ImGui.GetColorU32(BonePosing.StateColour(state, layer));
            var selected = posing.Selection.Contains(dot.Bone);
            var isPrimary = selected && string.Equals(posing.Selection.Primary, dot.Bone, StringComparison.Ordinal);
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
            ImGui.SetTooltip($"{AnimationPresentation.BoneName(hover)}\n{BonePosing.StateText(BonePosing.StateOf(hover, layer, frame, animated), layer)}");
            if (clicked)
                posing.ClickBone(hover, skeleton);
        }
        else if (clicked && !ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
            posing.Selection.Clear();
    }

    private static bool Has(Dictionary<string, int> index, string bone) => index.ContainsKey(bone);

    /// <summary>Every bone of the skeleton as a tree, or the ones a search finds as a flat list, coloured like the map's dots.</summary>
    private void DrawBoneList(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationClip clip)
    {
        var (_, children) = posing.BoneTree(skeleton);
        var animated = posing.AnimatedBones(clip);
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
        var state = BonePosing.StateOf(name, layer, frame, animated);
        if (posing.Selection.Contains(name))
            flags |= ImGuiTreeNodeFlags.Selected;
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, BonePosing.StateColour(state, layer), state != BoneState.Animated))
            open = ImGui.TreeNodeEx($"{AnimationPresentation.BoneName(name)}{(state == BoneState.Keyed ? "  · key" : "")}##bone-{bone}",
                flags | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            posing.ClickBone(name, skeleton);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(BonePosing.StateText(state, layer));
        return open;
    }

    /// <summary> The panel beside the map: the bone editor with the ball, an amplitude's bones, or how to start. </summary>
    private void DrawBonesPanel(TimelineEditor current, AnimationTimelineSource loaded, MotionLayer? layer, SkeletonDescription skeleton)
    {
        var readOnly = session.ReadOnly;
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
        Widgets.Icon(TimelineUi.LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, MotionEdits.KindName(layer.Kind));
        ImGui.Spacing();
        if (layer.Kind == MotionLayerKind.Amplitude)
            boneEditor.DrawLimit(current, layer, skeleton, readOnly, false);
        else
            boneEditor.Draw(current, layer, skeleton, loaded, readOnly, true);
        if (layer.Kind == MotionLayerKind.Pose)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            poseSources.Draw(current, layer, skeleton, loaded, readOnly);
        }
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
        var readOnly = session.ReadOnly;
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
