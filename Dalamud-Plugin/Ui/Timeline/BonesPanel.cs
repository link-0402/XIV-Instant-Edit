using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
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
    PoseSourcePanel poseSources, TimelinePreview timelinePreview, PlayBar playBar, Func<SkeletonStandard?> repair, IBonesOverlay overlay,
    ITextureProvider textures, ShortcutKeys keys, Action savePose)
{
    private enum BonesPage : byte { Map, List }

    private const string BonesHelp =
        "The map shows the bones your character uses or the animation moves. View > Every bone shows them all.\n" +
        "Click a dot to select a bone. Ctrl+click adds or removes one.\n" +
        "Shift+click selects a bone and every bone below it.\n" +
        "Changes go to the bone layer selected in the timeline, at the playhead.\n" +
        "Several selected bones turn and move the same way, about the character's axes.\n" +
        "A bone below another selected bone follows it.\n" +
        "Drag a ring of the ball, or turn the wheel over it, to turn. Hold Shift to turn finer.\n" +
        "In GPose the bones show on the character too. Click a dot there to select it.\n" +
        "With Live on and paused, drag the gizmo on the character to turn or move the bones.";

    // Made once: the layer strip calls it whenever it moves the playhead.
    private readonly Action pauseForStep = timelinePreview.PauseForStep;
    private BonesPage bonesPage = BonesPage.Map;
    private bool swapSides;
    // Save pose and the help tip's width, measured on the last draw, to right-align them on the tools row.
    private float saveWidth;
    private string boneSearch = string.Empty;
    // The character's bone names by index, read once per draw of the map or list (see LiveFor).
    private string[]? live;
    // The bones only a bigger standard has, for the skeleton, match and rebuild race they were worked out for.
    private (SkeletonDescription Skeleton, SkeletonResolution? Resolution, string? Model, Dictionary<string, string> Choices)? wider;
    // Whether Make symmetrical would copy anything, for the selection it was worked out for.
    private (SkeletonDescription Skeleton, MotionSide From, string[] Chosen, bool Copies)? symmetrical;

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
        if (loaded.MotionTarget.Standalone)
        {
            Widgets.EmptyState(FontAwesomeIcon.Smile, "An expression", loaded.FaceProblem ?? "This expression's face can't be edited.");
            return;
        }
        var fresh = session.Fresh(loaded);
        if (session.EditingSkeleton(loaded) is not { } skeleton)
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
        DrawBonesHeader(current, loaded);
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
                    DrawBoneMap(skeleton, layer, current.Playhead, fresh, ImGui.GetContentRegionAvail());
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
        // The link picker this window's Add layer menu asked for, at the window's own level.
        session.LinkPicker?.Draw(current, true);
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

    /// <summary> Undo and redo with the shared play bar, then the layer strip on a line of its own. </summary>
    private void DrawBonesHeader(TimelineEditor current, AnimationTimelineSource loaded)
    {
        TimelineUi.DrawUndoRedo("##bones", current, session.ReadOnly);
        ImGui.SameLine(0, Theme.Scaled(12));
        playBar.Draw(current, loaded, repair(), "##bones-play");
        LayerStrip.Draw(current, "##bones-strip", pauseForStep);
    }

    /// <summary>
    /// Map or list, the selection's groups, the View menu, Clear and the selection, then Save pose and the help on
    /// the right. A short row cuts the selection's text off, so Save pose always shows.
    /// </summary>
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
        if (ImGui.Button("View"))
            ImGui.OpenPopup("##bones-view");
        DrawViewMenu();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##bones-clear", FontAwesomeIcon.Times, "Clear the selection", posing.Selection.Count > 0))
            posing.Selection.Clear();

        ImGui.SameLine(0, Theme.Gap);
        var start = ImGui.GetCursorPosX();
        // Before the first draw has measured them, about Save pose and the help tip's width.
        var right = Math.Max(start, ImGui.GetContentRegionMax().X - (saveWidth > 0 ? saveWidth : Theme.Scaled(120)));
        var room = right - Theme.Scaled(12) - start;
        if (room > 0)
            DrawSelectionText(room);
        ImGui.SameLine(right);
        var saveStart = ImGui.GetCursorScreenPos().X;
        var face = session.Editor?.Document.Face != null;
        using (ImRaii.Disabled(face))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save pose", null, null, null, null))
                savePose();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(face ? "A face can't be saved as a pose." : "Save the pose at the playhead as a .pose file, in the pose browser.");
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip(BonesHelp);
        saveWidth = ImGui.GetItemRectMax().X - saveStart;
    }

    /// <summary>
    /// The selection's bone name or size, with the paths note when Paths draws fewer bones than are selected, cut off
    /// at <paramref name="room"/>. Hovering a cut-off text shows all of it.
    /// </summary>
    private void DrawSelectionText(float room)
    {
        var text = posing.Selection.Count switch
        {
            0 => "Nothing selected",
            1 => AnimationPresentation.BoneName(posing.Selection.Primary!),
            _ => $"{posing.Selection.Count} bones selected",
        };
        if (posing.Paths && posing.Selection.Count > MotionPaths.MaxBones)
            text += $"  (paths for the first {MotionPaths.MaxBones})";
        ImGui.AlignTextToFramePadding();
        var at = ImGui.GetCursorScreenPos();
        ImGui.PushClipRect(at, at + new Vector2(room, ImGui.GetFrameHeight()), true);
        ImGui.TextColored(Theme.Muted, text);
        if (ImGui.CalcTextSize(text).X > room && ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
        ImGui.PopClipRect();
    }

    /// <summary>
    /// The View menu: whether the bones show on the character (off, the selected ones or all), Every bone and Mirror
    /// view for the map, Onion skins and Paths with its frames. Ticking an item leaves the menu open.
    /// </summary>
    private void DrawViewMenu()
    {
        using var popup = ImRaii.Popup("##bones-view");
        if (!popup.Success)
            return;
        // Several switches change in one go: no item closes the menu.
        ImGuiP.PushItemFlag(ImGuiItemFlags.SelectableDontClosePopup, true);
        try
        {
            ImGui.TextColored(Theme.Muted, "On character");
            DrawOnCharacterItem("Off", BoneOverlayMode.Off, "No bones show on the character.");
            DrawOnCharacterItem("Selected", BoneOverlayMode.Selected,
                "In GPose only the selected bones show on the character, with the gizmo.");
            DrawOnCharacterItem("All", BoneOverlayMode.All,
                "In GPose all bones show on the character, to select them there and pose them with the gizmo.");
            ImGui.Separator();
            if (bonesPage == BonesPage.Map)
            {
                if (ImGui.MenuItem("Every bone", "", posing.EveryBone))
                    posing.EveryBone = !posing.EveryBone;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(posing.EveryBone
                        ? "Every bone shows. Click to show only the bones your character uses or the animation moves."
                        : "Only the bones your character uses or the animation moves show. Click to show every bone.");
                if (ImGui.MenuItem("Mirror view", "", !swapSides))
                    swapSides = !swapSides;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(swapSides
                        ? "The left side's bones show on the right, as seen from the front. Click to show them as in a mirror."
                        : "The left side's bones show on the left, as in a mirror. Click to show them as seen from the front.");
                ImGui.Separator();
            }
            if (ImGui.MenuItem("Onion skins", "", posing.Ghosts))
                posing.Ghosts = !posing.Ghosts;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((posing.Ghosts
                    ? "Onion skins: faint bones show the pose at the layer's key before the playhead (blue) and after it (orange). Click to hide them."
                    : "Show onion skins: faint bones for the pose at the layer's key before and after the playhead.") +
                    "\nThey leave out amplitude and fix layers and planted pins.");
            DrawPathsItem();
        }
        finally
        {
            ImGuiP.PopItemFlag();
        }
    }

    /// <summary> One of On character's choices, ticked while it is the one in use. </summary>
    private void DrawOnCharacterItem(string label, BoneOverlayMode mode, string tooltip)
    {
        if (ImGui.MenuItem(label, "", overlay.OnCharacter == mode))
            overlay.OnCharacter = mode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>
    /// The Paths item, with its frames field on the same line while Paths is on. The field is drawn before the item,
    /// so it keeps the mouse from the item under it, and letting go of a drag over the label leaves Paths on.
    /// </summary>
    private void DrawPathsItem()
    {
        var row = ImGui.GetCursorPos();
        var letGo = false;
        if (posing.Paths)
        {
            var style = ImGui.GetStyle();
            ImGui.SetCursorPos(row + new Vector2(ImGui.CalcTextSize("Paths").X + style.ItemSpacing.X, 0));
            var range = posing.PathRange;
            ImGui.SetNextItemWidth(Theme.Scaled(36));
            // As tall as the item's line.
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 0)))
            {
                if (ImGui.DragInt("##bones-path-range", ref range, .2f, MotionPaths.MinRange, MotionPaths.MaxRange, "%d"))
                    posing.PathRange = range;
            }
            letGo = ImGui.IsItemDeactivated();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Frames before and after the playhead the paths show.");
            // Room for the item's tick right of the field.
            ImGui.SameLine(0, 0);
            ImGui.Dummy(new Vector2(ImGui.GetFontSize() * 1.2f + style.ItemSpacing.X, ImGui.GetFontSize()));
            ImGui.SetCursorPos(row);
        }
        if (ImGui.MenuItem("Paths", "", posing.Paths) && !letGo)
        {
            posing.Paths = !posing.Paths;
            if (!posing.Paths)
                posing.HidePaths();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip((posing.Paths
                ? "Paths: lines show where the selected bones go over the frames around the playhead, blue before and orange after. Click to hide them."
                : "Show paths: lines for where the selected bones go over the frames around the playhead.") +
                "\nThey leave out amplitude and fix layers and planted pins.");
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
            var layerBones = posing.BonesOf(layer, skeleton);
            string[] changed = [.. bones.Where(bone => layerBones?.Pinned.Contains(bone) == true || PoseEdits.Changes(layer, bone, layerBones?.Changed))];
            using (ImRaii.Disabled(changed.Length == 0))
            {
                if (ImGui.MenuItem("Bones this layer changes"))
                    posing.Selection.Set(changed, add);
            }
            ImGui.Separator();
        }
        foreach (var group in session.Editor?.Document.Face != null ? PoseMap.FaceGroups : PoseMap.Groups)
        {
            string[] members = [.. bones.Where(group.Contains)];
            if (members.Length > 0 && ImGui.MenuItem(group.Name))
                posing.Selection.Set(members, add);
        }
    }

    /// <summary>
    /// The bones a bigger standard skeleton has and the editing one lacks, with the skeleton choice that adds them;
    /// worked out again only when the skeleton, the match or the rebuild's race changes.
    /// </summary>
    private Dictionary<string, string> WiderBones(SkeletonDescription skeleton, AnimationClip clip)
    {
        var model = session.Rebuild?.Model;
        if (wider is { } known && ReferenceEquals(known.Skeleton, skeleton) && ReferenceEquals(known.Resolution, clip.Resolution) && known.Model == model)
            return known.Choices;
        var choices = AnimationBones.WiderChoices(clip, skeleton, model);
        wider = (skeleton, clip.Resolution, model, choices);
        return choices;
    }

    /// <summary>
    /// Brio's bone map: the body, its armor and its hands with toes and tail, each bone a dot over the
    /// artwork joined to the nearest dot above it. The bones below the primary one, which move with it,
    /// have their lines lit. Bones only a bigger standard skeleton has show greyed out, their hover naming
    /// the skeleton choice that adds them. Only the bones the character uses or the clip moves show (see
    /// <see cref="BonePosing.MapBones"/>), and the selected ones and the ones the layer changes, unless the
    /// View menu's Every bone is on. A click on empty space clears the selection.
    /// </summary>
    private void DrawBoneMap(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationTimelineSource fresh, Vector2 size)
    {
        var clip = fresh.Clip;
        // A face shows its race's head picture, every dot on it.
        var face = session.Editor?.Document.Face != null;
        var (index, _) = posing.BoneTree(skeleton);
        var animated = posing.AnimatedBones(clip);
        var widerBones = face ? new Dictionary<string, string>() : WiderBones(skeleton, clip);
        var used = face ? null : posing.MapBones(fresh, skeleton);
        var layerBones = posing.BonesOf(layer, skeleton);
        // A selected bone and one the layer changes keep their dot, so an edit never loses it.
        bool Kept(string bone) => posing.Selection.Contains(bone) ||
                                  layer != null && (layerBones?.Pinned.Contains(bone) == true || PoseEdits.Changes(layer, bone, layerBones?.Changed));
        bool Shown(string bone) => (index.ContainsKey(bone) || widerBones.ContainsKey(bone)) && PoseMap.Shows(bone, used, Kept(bone));
        live = LiveFor(clip);
        var origin = ImGui.GetCursorScreenPos();
        size = Vector2.Max(size, new Vector2(1));
        ImGui.InvisibleButton("##bone-map", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var drawList = ImGui.GetWindowDrawList();

        List<PosePlacement> placements;
        if (face)
            placements = PoseMap.FaceLayout(PoseMap.FaceSection(clip.GamePath.Split('/') is [_, _, var race, ..] ? race : "", skeleton), size);
        else
        {
            var (armor, toes, groin, tail) = PoseMap.Extras(Shown);
            placements = PoseMap.Layout(size, Theme.Gap, armor, toes, groin, tail);
        }
        var dots = new List<(string Bone, Vector2 At, int Section, bool Handle)>();
        var greyed = new List<(string Bone, Vector2 At)>();
        for (var p = 0; p < placements.Count; p++)
        {
            var placement = placements[p];
            var min = origin + placement.Origin;
            var image = textures.GetFromManifestResource(typeof(TimelineWindow).Assembly, PoseMap.Resource(placement.Section)).GetWrapOrEmpty();
            drawList.AddImage(image.Handle, min, min + placement.Section.Size * placement.Scale);
            foreach (var spot in PoseMap.Place(placement.Section, swapSides))
            {
                if (!Shown(spot.Bone))
                    continue;
                if (Has(index, spot.Bone))
                    dots.Add((spot.Bone, min + spot.Position * placement.Scale, p, spot.Handle));
                else if (!spot.Handle)
                    greyed.Add((spot.Bone, min + spot.Position * placement.Scale));
            }
        }

        var mouse = ImGui.GetMousePos();
        var reach = Theme.Scaled(14);
        string? hover = null;
        var hoverGreyed = false;
        var best = float.MaxValue;
        if (hovered)
        {
            foreach (var dot in dots)
            {
                var distance = Vector2.Distance(dot.At, mouse);
                if (distance < reach && distance < best)
                {
                    best = distance;
                    hover = dot.Bone;
                }
            }
            foreach (var dot in greyed)
            {
                var distance = Vector2.Distance(dot.At, mouse);
                if (distance < reach && distance < best)
                {
                    best = distance;
                    hover = dot.Bone;
                    hoverGreyed = true;
                }
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

        // Bones only a bigger standard has: faint and hollow, never selected.
        var greyedColour = ImGui.GetColorU32(Theme.WithAlpha(Theme.Muted, .45f));
        foreach (var dot in greyed)
        {
            drawList.AddCircleFilled(dot.At, radius, ImGui.GetColorU32(Theme.WithAlpha(Theme.PanelBg, .6f)));
            drawList.AddCircle(dot.At, radius, greyedColour, 0, Theme.Scaled(1));
            if (dot.Bone == hover)
                drawList.AddCircle(dot.At, radius + Theme.Scaled(1), ImGui.GetColorU32(Theme.Muted), 0, Theme.Scaled(1.5f));
        }

        foreach (var dot in dots)
        {
            var state = BonePosing.StateOf(dot.Bone, layer, frame, animated, layerBones);
            var colour = ImGui.GetColorU32(BonePosing.StateColour(state, layer));
            var selected = posing.Selection.Contains(dot.Bone);
            var isPrimary = selected && string.Equals(posing.Selection.Primary, dot.Bone, StringComparison.Ordinal);
            if (state == BoneState.Pinned)
            {
                // A pinned bone is a square.
                var corner = new Vector2(radius);
                drawList.AddRectFilled(dot.At - corner - new Vector2(Theme.Scaled(1.5f)), dot.At + corner + new Vector2(Theme.Scaled(1.5f)), 0xC0000000);
                drawList.AddRectFilled(dot.At - corner, dot.At + corner, ImGui.GetColorU32(selected ? Theme.WithAlpha(Theme.Accent, .85f) : Theme.PanelBg));
                drawList.AddRect(dot.At - corner, dot.At + corner, colour, 0, ImDrawFlags.None, Theme.Scaled(2.5f));
            }
            else
            {
                drawList.AddCircleFilled(dot.At, radius + Theme.Scaled(1.5f), 0xC0000000);
                drawList.AddCircleFilled(dot.At, radius, ImGui.GetColorU32(Theme.PanelBg));
                if (selected)
                    drawList.AddCircleFilled(dot.At, radius - Theme.Scaled(1.5f), ImGui.GetColorU32(Theme.WithAlpha(Theme.Accent, .85f)));
                drawList.AddCircle(dot.At, radius, colour, 0, Theme.Scaled(state is BoneState.Changed or BoneState.Keyed ? 2.5f : 1.5f));
            }
            if (state == BoneState.Keyed)
                drawList.AddCircleFilled(dot.At, radius * .45f, colour);
            if (TimelineWindowViews.LandsElsewhere(dot.Bone, index[dot.Bone], live))
                drawList.AddCircle(dot.At, radius + Theme.Scaled(isPrimary ? 5 : 3), ImGui.GetColorU32(Theme.Warning), 0, Theme.Scaled(1.5f));
            if (isPrimary)
                drawList.AddCircle(dot.At, radius + Theme.Scaled(3), ImGui.GetColorU32(Theme.Accent), 0, Theme.Scaled(1.5f));
            if (dot.Bone == hover)
                drawList.AddCircle(dot.At, radius + Theme.Scaled(1), ImGui.GetColorU32(Theme.Text), 0, Theme.Scaled(1.5f));
        }

        if (hover != null && hoverGreyed)
            ImGui.SetTooltip(TimelineWindowViews.WiderBoneHint(AnimationPresentation.BoneName(hover), widerBones[hover]));
        else if (hover != null)
        {
            var text = $"{AnimationPresentation.BoneName(hover)}\n{BonePosing.StateText(BonePosing.StateOf(hover, layer, frame, animated, layerBones), layer)}";
            if (TimelineWindowViews.LandsElsewhere(hover, index[hover], live))
                text += "\n" + TimelineWindowViews.LandsElsewhereHint(index[hover], live!);
            ImGui.SetTooltip(text);
            if (clicked)
                posing.ClickBone(hover, skeleton);
        }
        else if (clicked && !ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
            posing.Selection.Clear();
    }

    private static bool Has(Dictionary<string, int> index, string bone) => index.ContainsKey(bone);

    /// <summary>
    /// The character's body skeleton's bone names by index, to mark bones whose index there is another bone; null when
    /// no character is drawn. None for a clip borrowed from another race and kept: the game converts it for the character's race.
    /// </summary>
    private string[]? LiveFor(AnimationClip clip)
    {
        if (clip.Resolution?.ActorModel != null && session.Rebuild?.Model == null)
            return null;
        try { return session.Playback.ActorBoneNames(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Every bone of the skeleton as a tree, or the ones a search finds as a flat list, coloured like the map's dots.</summary>
    private void DrawBoneList(SkeletonDescription skeleton, MotionLayer? layer, int frame, AnimationClip clip)
    {
        var (_, children) = posing.BoneTree(skeleton);
        var animated = posing.AnimatedBones(clip);
        live = LiveFor(clip);
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
        var state = BonePosing.StateOf(name, layer, frame, animated, posing.BonesOf(layer, skeleton));
        if (posing.Selection.Contains(name))
            flags |= ImGuiTreeNodeFlags.Selected;
        var elsewhere = TimelineWindowViews.LandsElsewhere(name, bone, live);
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, elsewhere ? Theme.Warning : BonePosing.StateColour(state, layer), elsewhere || state != BoneState.Animated))
            open = ImGui.TreeNodeEx($"{AnimationPresentation.BoneName(name)}{(state == BoneState.Keyed ? "  · key" : "")}##bone-{bone}",
                flags | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            posing.ClickBone(name, skeleton);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(elsewhere ? BonePosing.StateText(state, layer) + "\n" + TimelineWindowViews.LandsElsewhereHint(bone, live!) : BonePosing.StateText(state, layer));
        return open;
    }

    /// <summary> The panel beside the map: the bone editor with the ball, an amplitude's bones, or how to start. </summary>
    private void DrawBonesPanel(TimelineEditor current, AnimationTimelineSource loaded, MotionLayer? layer, SkeletonDescription skeleton)
    {
        var readOnly = session.ReadOnly;
        DrawMakeSymmetrical(current, skeleton, readOnly);
        if (layer == null)
        {
            Widgets.MutedWrapped("Select a bone layer in the timeline to pose its bones, or add one over the whole clip.");
            ImGui.Spacing();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add layer", null, null, null, null))
                ImGui.OpenPopup("##bones-add-layer");
            DrawAddLayerMenu(current, readOnly);
            return;
        }
        Widgets.Icon(TimelineUi.LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, MotionEdits.KindName(layer.Kind));
        ImGui.Spacing();
        if (layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or MotionLayerKind.Smooth or MotionLayerKind.Noise or
            MotionLayerKind.Repeat or MotionLayerKind.Spring)
            boneEditor.DrawLimit(current, layer, skeleton, readOnly);
        // A balance always works on the hips and the feet: nothing to pick or key.
        else if (layer.Kind == MotionLayerKind.Balance)
            Widgets.MutedWrapped("Moves the hips over the feet.");
        else if (layer.Kind == MotionLayerKind.LookAt)
        {
            LookFields.DrawBones(current, layer, readOnly);
            Widgets.MutedWrapped(layer.Look?.Follow is { } follow
                ? $"Follows {AnimationPresentation.BoneName(follow)}."
                : "Move the target on the character with the gizmo to key it at the playhead.");
            boneEditor.DrawGizmoWaits(layer, current.Playhead, skeleton);
        }
        else if (layer.Kind == MotionLayerKind.Symmetry)
        {
            if (posing.Selection.Primary is { } primary && posing.BonesOf(layer, skeleton)?.Changed.Contains(primary) == true)
                Widgets.HintWrapped("Copied from the other side. Add an Offset layer above this one to adjust it.");
            boneEditor.DrawSymmetryBones(current, layer, skeleton, readOnly);
        }
        else if (layer.Kind == MotionLayerKind.Pin)
            // Its own hint says what to select; with the bones on the character the gizmo choice says what the gizmo waits for.
            boneEditor.DrawPinButtons(current, layer, skeleton, loaded, readOnly);
        else if (layer.Kind == MotionLayerKind.Link)
        {
            // A link follows its partner: no keys here; the gizmo says to edit a layer above.
            LinkFields.DrawState(layer, session);
            boneEditor.DrawGizmoWaits(layer, current.Playhead, skeleton);
        }
        else
            boneEditor.Draw(current, layer, skeleton, loaded, readOnly);
        if (BoneEditor.ShowsBreakdown(layer.Kind))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            boneEditor.DrawBreakdown(current, layer, skeleton, readOnly);
        }
        if (layer.Kind == MotionLayerKind.Pose)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            poseSources.Draw(current, layer, skeleton, loaded, readOnly);
        }
    }

    /// <summary>
    /// The Add layer menu: the clip's layer kinds, each over the whole clip, then the loop-pose and fix items, each greyed out
    /// with its reason where it can't be added.
    /// </summary>
    private void DrawAddLayerMenu(TimelineEditor current, bool readOnly)
    {
        using var popup = ImRaii.Popup("##bones-add-layer");
        if (!popup.Success)
            return;
        foreach (var kind in TimelineWindowViews.AddLayerKinds(current.Document))
        {
            // A link needs its partner: the item opens the link picker, in this window.
            if (kind == MotionLayerKind.Link)
            {
                session.LinkPicker?.DrawAddItem($"{MotionEdits.KindName(kind)} layer", readOnly, TimelineWindowViews.LayerKindHint(kind), true);
                continue;
            }
            if (ImGui.MenuItem($"{MotionEdits.KindName(kind)} layer", "", false, !readOnly))
                current.AddLayer(kind, 0, Math.Max(TimelineDocument.MinLength, current.Document.Length));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TimelineWindowViews.LayerKindHint(kind));
        }
        session.LoopPose.DrawAddItems(current, readOnly, (label, enabled) => ImGui.MenuItem(label, "", false, enabled));
    }

    /// <summary>
    /// Make symmetrical: a symmetry layer over the whole clip for the selected bones, copying the side the
    /// primary bone is on (the left for a bone in the middle). Needs a selected bone with a twin.
    /// </summary>
    private void DrawMakeSymmetrical(TimelineEditor current, SkeletonDescription skeleton, bool readOnly)
    {
        var index = posing.BoneTree(skeleton).Index;
        string[] chosen = [.. posing.Selection.Bones.Where(index.ContainsKey)];
        var from = posing.Selection.Primary is { } primary && MotionMirror.Side(primary) == 'r' ? MotionSide.Right : MotionSide.Left;
        // A middle bone counts when something below it has a twin, as the chest does for the arms.
        // Worked out again only when the selection changes, not every frame.
        if (symmetrical is not { } known || !ReferenceEquals(known.Skeleton, skeleton) || known.From != from || !known.Chosen.SequenceEqual(chosen))
        {
            var copies = chosen.Length > 0 && !MotionSymmetry.Plan(skeleton,
                new MotionLayer("m0", MotionLayerKind.Symmetry, "", 0, 1, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear, [.. chosen], From: from)).Pairs.IsEmpty;
            known = (skeleton, from, chosen, copies);
            symmetrical = known;
        }
        using (ImRaii.Disabled(readOnly || !known.Copies))
        {
            if (ImGui.Button("Make symmetrical"))
                current.AddSymmetry(chosen, from);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add a layer that copies the selected side onto the other, mirrored. A bone in the middle copies its left side.");
        ImGui.SameLine();
        using (ImRaii.Disabled(readOnly || chosen.Length == 0))
        {
            if (ImGui.Button("Pin selected"))
                current.PinSelected(chosen);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Pin the selected bones in the selected Pin layer, or in a new one.");
        ImGui.Spacing();
    }

    /// <summary> In the Bones window: undo and redo, the arrows step the playhead and Space plays or pauses. </summary>
    private void HandleBonesKeys(TimelineEditor current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var io = ImGui.GetIO();
        if (io.WantTextInput)
            return;
        // The arrows step the playhead here; the game must not take them as well.
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        keys.Claim();
        if (ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            return;
        var readOnly = session.ReadOnly;
        if (keys.Ctrl && keys.Pressed(ImGuiKey.Z, VirtualKey.Z, ctrl: true))
        {
            if (!readOnly)
                current.Key(keys.Shift ? TimelineKey.Redo : TimelineKey.Undo);
        }
        else if (keys.Ctrl && keys.Pressed(ImGuiKey.Y, VirtualKey.Y, ctrl: true))
        {
            if (!readOnly)
                current.Key(TimelineKey.Redo);
        }
        else if (!keys.Alt && keys.Pressed(ImGuiKey.LeftArrow, VirtualKey.LEFT, true))
        {
            if (current.Key(TimelineKey.Left, keys.Shift))
                timelinePreview.PauseForStep();
        }
        else if (!keys.Alt && keys.Pressed(ImGuiKey.RightArrow, VirtualKey.RIGHT, true))
        {
            if (current.Key(TimelineKey.Right, keys.Shift))
                timelinePreview.PauseForStep();
        }
        else if (!keys.Ctrl && !keys.Alt && keys.Pressed(ImGuiKey.Space, VirtualKey.SPACE, ctrl: false))
            timelinePreview.PlayKey();
    }
}
