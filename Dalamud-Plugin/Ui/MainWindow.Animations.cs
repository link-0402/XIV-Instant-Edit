using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>
/// The Animations tab: what your character plays, listed like On Screen with the mod each animation
/// comes from and a pen that sends it to Blender, and the selected animation's tools in tabs below.
/// </summary>
public sealed partial class MainWindow
{
    private AnimationEditService? animations;
    private string? animationError;
    private AnimationCapture? animationSelection;
    private readonly HashSet<PoseBoneId> animationBones = [];
    private PoseComponents animationComponents = PoseComponents.All;
    private AnimationDestination animationDestination = AnimationDestination.NewMod;
    private bool animationStartupSelected;
    private bool animationIncludeStartup;
    private string animationModName = "";
    private string animationFilter = "";
    private readonly ResourceSearch animationSearch = new();
    // The list's view model, rebuilt when the listener's history or the On Screen snapshot changes.
    private ImmutableArray<AnimationCapture> animationRowSource;
    private IReadOnlyList<OnScreenObject>? animationRowActors;
    private ImmutableArray<AnimationRow> animationRows = [];
    private ActorView animationActor = new(null, "Your character", string.Empty, [], 0);

    internal void AttachAnimations(AnimationEditService? service, string? error)
    {
        animations = service;
        animationError = error;
    }

    private void SelectAnimation(AnimationCapture capture, bool startup = false)
    {
        animationSelection = capture;
        animationBones.Clear();
        animationBones.UnionWith(capture.Pose.Bones.Where(b => b.Id.Partial == capture.Clip.Partial && b.Id.Slot == 0).Select(b => b.Id));
        ResetAnimatedBones();
        animationComponents = PoseComponents.All;
        animationStartupSelected = startup && capture.Startup != null;
        animationModName = AnimationPresentation.DefaultModName(capture);
    }

    private void ClearAnimationSelection()
    {
        animationSelection = null;
        animationBones.Clear();
        ResetAnimatedBones();
    }

    private void DrawAnimations()
    {
        ImGui.Spacing();
        if (animations == null)
        {
            Widgets.Banner("##animations-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation integration is unavailable.");
            ImGui.Spacing();
            // Recording reads the live skeleton directly and needs none of the editing services.
            DrawRecorder();
            return;
        }
        animations.StartObservation();
        TrackAnimationSelection(animations.Observer);
        var rows = ReadAnimationRows(animations.Observer.History);
        Widgets.SearchBox("##animation-search", ref animationFilter, "Search animations, mods and paths");
        animationSearch.Text = animationFilter;
        var shown = AnimationRows.Filter(rows, animationSearch);
        ImGui.Spacing();

        var sections = shown.Select(row => row.Group).Distinct().Count();
        var height = AnimationListHeight(shown.Count + sections, ImGui.GetContentRegionAvail().Y);
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Theme.PanelBg))
        using (var list = ImRaii.Child("##animation-list", new Vector2(0, height), true))
        {
            if (list.Success)
                DrawAnimationList(animations.Observer.Status, rows, shown);
        }
        ImGui.Spacing();
        DrawAnimationTools();
    }

    /// <summary>
    /// Keeps the selection in step with the listener. Resolution updates must not recapture offsets
    /// or change the ticked bones, but everything else (including Sources, which an in-place edit can
    /// change without the game reloading the file) follows the latest capture.
    /// </summary>
    private void TrackAnimationSelection(AnimationObserver observer)
    {
        if (animationSelection is { } previous && observer.History.FirstOrDefault(c => c.Id == previous.Id) is { } current &&
            current.Clip.BindingFingerprint == previous.Clip.BindingFingerprint && current.Clip.SourceContext == previous.Clip.SourceContext &&
            current.Clip.SkeletonFingerprint == previous.Clip.SkeletonFingerprint)
        {
            animationSelection = previous with
            {
                Clip = current.Clip, Startup = current.Startup, Playing = current.Playing,
                Sources = current.Sources, FamilyPaths = current.FamilyPaths,
                LoadedResourcePaths = current.LoadedResourcePaths, ResourceAliases = current.ResourceAliases,
                UnavailableReason = current.UnavailableReason,
                PoseUnavailableReason = current.PoseUnavailableReason,
            };
        }
        if (animationSelection != null && animationSelection.ActorId != observer.Actor)
            ClearAnimationSelection();
        if (animationSelection is { Playing: false } stale && !AnimationPresentation.Listed(stale))
            ClearAnimationSelection();
        // Another source skeleton can name the same tracks differently; only bones the animation
        // still drives can be left out.
        if (animationSelection is { } selected && AnimationBones.Animated(selected.Clip) is { IsEmpty: false } animated)
            animationExcludedBones.IntersectWith(animated);
    }

    /// <summary> The list's rows, rebuilt only when the listener's history or the On Screen snapshot changes. </summary>
    private ImmutableArray<AnimationRow> ReadAnimationRows(ImmutableArray<AnimationCapture> history)
    {
        var actors = _onScreen.Items;
        if (history == animationRowSource && ReferenceEquals(actors, animationRowActors))
            return animationRows;

        var rows = AnimationRows.Build(history);
        _animationRows.Clear();
        foreach (var row in rows)
            _animationRows[row.View] = (row.Capture.Id, row.Startup);
        // The listener follows your character: its On Screen entry names the list and gives the
        // row menu its mod actions.
        var address = rows.IsEmpty ? 0 : rows[0].Capture.ActorAddress;
        var entity = actors.FirstOrDefault(actor => address != 0 && (long)actor.Address == address)
                     ?? actors.FirstOrDefault(actor => actor.ObjectIndex == 0);
        List<ResourceView> views = [.. rows.Select(row => row.View)];
        animationActor = entity is null
            ? new ActorView(null, "Your character", string.Empty, views, 0)
            : new ActorView(entity, entity.PresentationCategory.ToString(), Safe(entity.Name), views, entity.ObjectIndex);

        animationRowSource = history;
        animationRowActors = actors;
        animationRows = rows;
        animationSearch.Invalidate();
        return rows;
    }

    /// <summary> The list takes its rows' height, at most a little under half the tab, so the tools keep their room. </summary>
    private static float AnimationListHeight(int rows, float available)
    {
        var style = ImGui.GetStyle();
        var row = ImGui.GetFrameHeight() + style.CellPadding.Y * 2;
        // The header line, the table's column headers and the frame's padding.
        var chrome = ImGui.GetFrameHeightWithSpacing() + row + style.WindowPadding.Y * 2 + Theme.Scaled(2);
        var minimum = chrome + row * 3;
        return Math.Clamp(chrome + row * rows, minimum, Math.Max(minimum, available * .45f));
    }

    /// <summary> Your character's animations as a table like On Screen's: what plays now, then what played recently. </summary>
    private void DrawAnimationList(string status, ImmutableArray<AnimationRow> rows, IReadOnlyList<AnimationRow> shown)
    {
        DrawAnimationListHeader(status);
        if (shown.Count == 0)
        {
            if (rows.IsEmpty)
                Widgets.EmptyState(FontAwesomeIcon.Running, "No animations detected yet",
                    "Play an emote, idle or walk outside combat. What your character plays appears here, with the mod it comes from.");
            else
                Widgets.EmptyState(FontAwesomeIcon.Search, "No matching animations", "Clear the search to see every animation.");
            return;
        }

        using var align = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f));
        using var table = ImRaii.Table("##animation-table", 4, ResourceTableFlags | ImGuiTableFlags.ScrollY,
            new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y)));
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Animation", ImGuiTableColumnFlags.WidthStretch, .34f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, .18f);
        ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, .48f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, Theme.Scaled(58));
        ImGui.TableHeadersRow();
        DrawAnimationSection(shown, AnimationGroup.Playing, "Playing now");
        DrawAnimationSection(shown, AnimationGroup.Recent, "Played recently");
    }

    /// <summary> The character the animations belong to, headed as On Screen heads its actors, and what the listener is doing. </summary>
    private void DrawAnimationListHeader(string status)
    {
        DrawOpaqueRow();
        Widgets.Icon(FontAwesomeIcon.User, Theme.Label);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(animationActor.Header);
        ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(status).X - Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, status);
    }

    /// <summary> A section row like On Screen's, followed by its animations while it is open. </summary>
    private void DrawAnimationSection(IReadOnlyList<AnimationRow> shown, AnimationGroup group, string label)
    {
        var rows = shown.Where(row => row.Group == group).ToArray();
        if (rows.Length == 0)
            return;
        var key = group == AnimationGroup.Playing ? "animations:playing" : "animations:recent";
        using var id = ImRaii.PushId(key);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // Sections start open, and a search opens them.
        var expanded = _expansion.IsExpanded(key, true, animationSearch.Active);
        if (Widgets.GhostIconButton("##section-toggle", expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, expanded ? "Collapse" : "Expand"))
            _expansion.Toggle(key, expanded, true);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight())))
            _expansion.Toggle(key, expanded, true);
        if (!expanded)
            return;
        foreach (var row in rows)
            DrawAnimationRow(row);
    }

    /// <summary> One animation: its name and state, the mod and file it comes from, and the pen that sends it to Blender. </summary>
    private void DrawAnimationRow(AnimationRow row)
    {
        var view = row.View;
        using var id = ImRaii.PushId(SafeId(row.Key));
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.WithAlpha(Theme.AnimationKind, Theme.KindTintAlpha)));
        if (animationSelection?.Id == row.Capture.Id && animationStartupSelected == row.Startup)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(Theme.WithAlpha(Theme.Selection, .6f)));

        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorPosX() + Theme.TreeIndent;
        // A startup hangs under the animation it leads into.
        if (row.Startup)
            DrawStartupGuide();
        ImGui.SetCursorPosX(start + (row.Startup ? TreeStep : 0));
        Widgets.Icon(FontAwesomeIcon.Running, Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        var problem = SkeletonBlock(row.Clip);
        var searching = row.Clip.Resolution?.State == SkeletonResolutionState.Searching;
        var problemIcon = searching ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.ExclamationTriangle;
        var tags = (row.Playing ? BadgeWidth("playing") + Theme.Gap : 0) + (problem is null ? 0 : IconWidth(problemIcon) + Theme.Gap);
        if (ImGui.Selectable($"{Safe(view.Name, "Animation")}##label", false, ImGuiSelectableFlags.None,
                new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X - tags), ImGui.GetFrameHeight())))
            SelectAnimation(row.Capture, row.Startup);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            DrawAnimationRowTooltip(row, problem);
        if (row.Playing)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Badge("playing", Theme.Success);
        }
        if (problem is not null)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Icon(problemIcon, searching ? Theme.Muted : Theme.Warning);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(problem);
        }

        ImGui.TableSetColumnIndex(1);
        DrawSourceBadge(view);
        ImGui.TableSetColumnIndex(2);
        Widgets.PathText(Safe(view.SourceRelativePath, view.GamePath), Safe(view.ActualPath));
        ImGui.TableSetColumnIndex(3);
        if (Widgets.IconButton("##send-animation", FontAwesomeIcon.Pen, "Send this animation to Blender"))
            OpenAnimationSend(view);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.GhostIconButton("##more", FontAwesomeIcon.EllipsisH, "More actions"))
            ImGui.OpenPopup(RowMenuPopup);
        DrawRowMenu(animationActor, view);
    }

    /// <summary> The line joining a startup row to the animation above it, hanging from that row's glyph. </summary>
    private static void DrawStartupGuide()
    {
        var origin = ImGui.GetCursorScreenPos();
        var frame = ImGui.GetFrameHeight();
        var x = MathF.Floor(origin.X + Theme.TreeIndent + IconWidth(FontAwesomeIcon.Running) / 2) + .5f;
        var middle = MathF.Floor(origin.Y + frame / 2) + .5f;
        var end = origin.X + Theme.TreeIndent + TreeStep - Theme.Scaled(3);
        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Theme.TreeLine);
        var thickness = Math.Max(1, MathF.Floor(Theme.Scale));
        drawList.AddLine(new Vector2(x, origin.Y - ImGui.GetStyle().CellPadding.Y), new Vector2(x, middle), colour, thickness);
        drawList.AddLine(new Vector2(x, middle), new Vector2(end, middle), colour, thickness);
    }

    private static void DrawAnimationRowTooltip(AnimationRow row, string? problem)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, row.View.Name);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, AnimationPresentation.AnimationState(row.Capture, row.Startup));
        ImGui.TextColored(Theme.Hint, "Game path: " + row.Clip.GamePath);
        if (problem is not null)
            ImGui.TextColored(Theme.Warning, problem);
        else if (row.Clip.Resolution?.Selected is { } source)
            ImGui.TextUnformatted("Made for " + SkeletonLabel(source));
    }

    private static float BadgeWidth(string text) => ImGui.CalcTextSize(text).X + Theme.Scaled(12);

    private static float IconWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    /// <summary> The selected animation's tools, one tab per task. The tabs at the right need no selection. </summary>
    private void DrawAnimationTools()
    {
        if (animationSelection is { } capture)
            DrawSelectedAnimationTitle(capture);
        using var tabs = ImRaii.TabBar("##animation-tools");
        if (!tabs.Success)
            return;
        DrawAnimationTool("Source", "see its file and the skeleton it was made for", DrawAnimationSource);
        DrawAnimationTool("LivePose", "bake your LivePose adjustments into it", DrawLivePose);
        DrawAnimationTool("Animated bones", "choose the bones it moves", DrawAnimatedBones);
        DrawAnimationTool("Skeleton repair", "retarget it onto a standard skeleton", DrawSkeletonRepair);
        DrawRecentEditsTab();
        if (ImGui.BeginTabItem("Record live pose", ImGuiTabItemFlags.Trailing))
        {
            DrawToolBody(DrawRecorder);
            ImGui.EndTabItem();
        }
    }

    /// <summary> Names the animation the tools below work on. </summary>
    private void DrawSelectedAnimationTitle(AnimationCapture capture)
    {
        var clip = animationStartupSelected && capture.Startup is { } startup ? startup : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        var playing = capture.Playing && !animationStartupSelected ? " · playing" : "";
        Widgets.Icon(FontAwesomeIcon.Running, Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, AnimationPresentation.AnimationName(capture, animationStartupSelected));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, $"{AnimationPresentation.AnimationState(capture, animationStartupSelected)}{playing} · " +
                                       (file?.ModName ?? file?.ModDirectory ?? "Game Data"));
    }

    /// <summary> A tab for the selected animation: its tool, or a prompt to choose an animation. </summary>
    private void DrawAnimationTool(string label, string purpose, Action<AnimationCapture> draw)
    {
        if (!ImGui.BeginTabItem(label, ImGuiTabItemFlags.None))
            return;
        DrawToolBody(() =>
        {
            if (animationSelection is { } capture)
                draw(capture);
            else
                Widgets.EmptyState(FontAwesomeIcon.Running, "Select an animation", $"Choose an animation in the list above to {purpose}.");
        });
        ImGui.EndTabItem();
    }

    private void DrawRecentEditsTab()
    {
        var attention = RecoveryAttention();
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, attention > 0))
            open = ImGui.BeginTabItem(attention > 0 ? $"Recent edits ({attention})###recent-edits" : "Recent edits###recent-edits",
                ImGuiTabItemFlags.Trailing);
        if (attention > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(attention == 1 ? "An edit did not finish and needs an undo." : $"{attention} edits did not finish and need an undo.");
        if (!open)
            return;
        DrawToolBody(DrawRecovery);
        ImGui.EndTabItem();
    }

    /// <summary> A tool tab's body, which scrolls below the tab bar. </summary>
    private static void DrawToolBody(Action draw)
    {
        using var body = ImRaii.Child("##tool", Vector2.Zero, false);
        if (!body.Success)
            return;
        ImGui.Spacing();
        draw();
    }

    /// <summary> Starts a label and value table for an animation's facts. </summary>
    private static void SetupFacts()
    {
        ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
    }

    /// <summary> Starts a fact row: its muted label, then the value's cell. </summary>
    private static void Fact(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
    }

    /// <summary> The Source tab: where the animation comes from, and the skeleton it was made for. </summary>
    private void DrawAnimationSource(AnimationCapture capture)
    {
        var startup = animationStartupSelected && capture.Startup is not null;
        var clip = startup ? capture.Startup! : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        using (var facts = ImRaii.Table("##source-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                SetupFacts();
                Fact("Mod");
                ImGui.TextUnformatted(file?.ModName ?? file?.ModDirectory ?? "None, it is the game's own file");
                Fact("File");
                Widgets.PathText(Safe(file?.RelativePath, clip.GamePath), Safe(file?.ResolvedPath, clip.GamePath));
                Fact("Game path");
                Widgets.PathText(clip.GamePath, clip.GamePath);
                Fact("Plays on");
                ImGui.TextUnformatted(AnimationPresentation.ModelName(clip));
                Fact(startup ? "Leads into" : "Startup");
                if (startup)
                    ImGui.TextUnformatted(AnimationPresentation.AnimationName(capture));
                else if (capture.Startup is null)
                    ImGui.TextColored(Theme.Hint, "No linked startup was identified");
                else
                    ImGui.TextUnformatted(AnimationPresentation.AnimationName(capture, true));
                Fact("Live skeleton");
                DrawLiveSkeleton(capture.Clip);
                Fact("Made for");
                DrawSourceSkeleton(capture, clip, startup);
                // A rebake can include the startup, which needs its own source skeleton.
                if (!startup && capture.Startup is { Resolution.State: SkeletonResolutionState.Ambiguous })
                {
                    Fact("Startup made for");
                    DrawSourceSkeleton(capture, capture.Startup, true);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(capture.Clip.LastOperationError))
        {
            ImGui.Spacing();
            Widgets.Banner("##animation-last-error", FeedbackSeverity.Warning, "Last attempt: " + capture.Clip.LastOperationError);
        }
        ImGui.Spacing();
        // The library is only rebuilt on request, from Settings.
        if (Widgets.IconButton("##skeleton-library", FontAwesomeIcon.Cog,
                "Rebuild the skeleton library in Settings when a source skeleton is missing, for example after installing skeleton mods"))
            _openSettings();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, "Skeleton library in Settings");
    }

    private static void DrawLiveSkeleton(AnimationClip clip)
    {
        var skeleton = clip.TargetSkeleton;
        ImGui.TextUnformatted(skeleton == null ? "Unavailable" : $"{Path.GetFileName(clip.SkeletonPath)} · {skeleton.Bones.Length} bones");
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(clip.SkeletonPath))
            ImGui.SetTooltip(clip.SkeletonPath);
    }

    /// <summary>
    /// The skeleton a clip was made for, which it is sampled and rebaked on: a choice when several
    /// fit equally well, then the file it comes from.
    /// </summary>
    private void DrawSourceSkeleton(AnimationCapture capture, AnimationClip clip, bool startup)
    {
        var resolution = clip.Resolution;
        var source = resolution?.Selected ?? resolution?.Candidates.FirstOrDefault();
        if (resolution is null || source is null)
        {
            ImGui.TextColored(resolution?.State == SkeletonResolutionState.Incompatible ? Theme.Warning : Theme.Hint,
                resolution?.Reason ?? "Finding the skeleton this animation was made for");
            return;
        }
        if (resolution.Candidates.Length > 1)
        {
            // The animation's and its startup's choices can share a table.
            using var id = ImRaii.PushId(startup ? "startup" : "animation");
            using (ImRaii.Disabled(animations!.Busy))
            {
                ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(480), ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - Theme.Gap));
                using var combo = ImRaii.Combo("##skeleton", resolution.Selected is null ? "Choose the skeleton it was made for" : SkeletonLabel(source));
                if (combo.Success)
                {
                    foreach (var candidate in resolution.Candidates)
                    {
                        if (ImGui.Selectable($"{SkeletonLabel(candidate)} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)}##{AnimationSkeletonIndex.SelectionId(candidate)}",
                                candidate == resolution.Selected))
                            ChooseSourceSkeleton(capture, clip, candidate, startup);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                    }
                }
            }
        }
        else
        {
            ImGui.TextUnformatted(SkeletonLabel(source));
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("Why this skeleton: " + source.Rationale);
        if (resolution.Selected is null)
            Widgets.HintWrapped(resolution.Reason ?? "Several skeletons fit. Choose the one this animation was made for.");
        Widgets.PathText(source.Source.Resource.ResolvedPath, source.Source.Resource.ResolvedPath);
        var aliases = source.Source.MappedGamePaths.Select(AnimationSkeletonIndex.ModelFromPath).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        if (aliases.Length > 0)
            ImGui.TextColored(Theme.Hint, "Mapped aliases: " + string.Join(", ", aliases));
        if (source.Source.LeadingBones > 0)
            Widgets.HintWrapped($"Uses only its first {source.Source.LeadingBones} bones: no whole skeleton fits, so the animation " +
                                "was likely made before more bones were added at the end of this skeleton.");
    }

    private void ChooseSourceSkeleton(AnimationCapture capture, AnimationClip clip, SkeletonCandidate candidate, bool startup)
    {
        try
        {
            var chosen = animations!.Observer.ChooseSkeleton(clip, candidate);
            animationSelection = startup ? capture with { Startup = chosen } : capture with { Clip = chosen };
        }
        catch (InvalidOperationException e)
        {
            _feed.Report(StatusChannel.Animations, FeedbackSeverity.Warning, e.Message);
        }
    }

    /// <summary> The LivePose tab: bakes the ticked LivePose adjustments into the animation. </summary>
    private void DrawLivePose(AnimationCapture capture)
    {
        Widgets.HintWrapped("Bakes the LivePose adjustments ticked below into the animation, so the pose becomes part of it, " +
                            "and clears them from your live pose.");
        var poseUnavailable = capture.PoseUnavailableReason != null;
        if (poseUnavailable)
        {
            ImGui.Spacing();
            Widgets.Banner("##livepose-unavailable", FeedbackSeverity.Warning, "LivePose adjustments are unavailable: " + capture.PoseUnavailableReason);
        }
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Bake");
        foreach (var component in new[] { PoseComponents.Position, PoseComponents.Rotation, PoseComponents.Scale })
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            var enabled = animationComponents.HasFlag(component);
            if (ImGui.Checkbox(component.ToString(), ref enabled))
                animationComponents = enabled ? animationComponents | component : animationComponents & ~component;
        }
        DrawOffsetTools(capture, poseUnavailable);
        ImGui.Spacing();
        DrawAdjustedBones(capture);
        DrawRebake(capture, AnimationOperation.BakeOffsets, "Rebake with LivePose",
            "Bake the ticked LivePose adjustments into the animation and clear them from the live pose.");
    }

    /// <summary> Your live LivePose offsets: read them again, clear them, or put cleared ones back. </summary>
    private void DrawOffsetTools(AnimationCapture capture, bool poseUnavailable)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Live offsets");
        ImGui.SameLine(0, Theme.Scaled(12));
        using (ImRaii.Disabled(animations!.Busy || poseUnavailable))
        {
            if (ImGui.Button("Refresh offsets"))
            {
                var selected = capture;
                animations.Refresh(selected, refreshed =>
                {
                    if (animationSelection?.Id != selected.Id) return;
                    animationSelection = refreshed;
                    animationBones.IntersectWith(refreshed.Pose.Bones.Select(b => b.Id));
                });
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Read your LivePose adjustments again, for example after changing them in LivePose.");
        ImGui.SameLine();
        using (ImRaii.Disabled(animations.Busy || poseUnavailable))
        {
            if (ImGui.Button("Clear all offsets")) animations.ClearOffsets(capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Remove every LivePose adjustment from your character. Reapply offsets puts them back.");
        ImGui.SameLine();
        using (ImRaii.Disabled(animations.Busy || !animations.CanReapplyOffsets || poseUnavailable))
        {
            if (ImGui.Button("Reapply offsets")) animations.ReapplyOffsets(capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Put back the live offsets that the last clear or bake removed.");
        ImGui.SameLine();
        var restorable = FindOffsetJournal(capture);
        using (ImRaii.Disabled(animations.Busy || restorable is null))
        {
            if (ImGui.Button("Restore offsets")) animations.RestoreOffsets(restorable!);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(restorable is null
                ? "No bake of this animation cleared live offsets that could be put back."
                : $"Put back the live offsets captured before the edit from {restorable.CreatedUtc.ToLocalTime():g}.");
    }

    /// <summary> The bones with LivePose adjustments on the animation's skeleton; the ticked ones are baked. </summary>
    private void DrawAdjustedBones(AnimationCapture capture)
    {
        var bones = capture.Pose.Bones.Where(b => b.Id.Slot == 0 && b.Id.Partial == capture.Clip.Partial).ToArray();
        if (bones.Length == 0)
        {
            Widgets.HintWrapped("Your character has no LivePose adjustments on this animation's skeleton. " +
                                "Adjust bones in LivePose, then press Refresh offsets.");
            return;
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, $"Adjusted bones · {animationBones.Count} of {bones.Length} ticked");
        ImGui.SameLine();
        if (ImGui.SmallButton("Tick all")) animationBones.UnionWith(bones.Select(b => b.Id));
        ImGui.SameLine();
        if (ImGui.SmallButton("Untick all")) animationBones.Clear();
        var row = ImGui.GetFrameHeightWithSpacing();
        var height = Math.Clamp(bones.Length * row + Theme.Scaled(8), row + Theme.Scaled(8), Theme.Scaled(220));
        using var list = ImRaii.Child("##adjusted-bones", new Vector2(0, height), true);
        if (!list.Success)
            return;
        foreach (var bone in bones)
        {
            using var id = ImRaii.PushId(bone.Id.Name);
            var enabled = animationBones.Contains(bone.Id);
            if (ImGui.Checkbox(AnimationPresentation.BoneName(bone.Id.Name), ref enabled))
            {
                if (enabled) animationBones.Add(bone.Id);
                else animationBones.Remove(bone.Id);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(bone.Stacks.Length == 1 ? "1 adjustment" : $"{bone.Stacks.Length} adjustments");
        }
    }

    /// <summary>
    /// The Skeleton repair tab: retargets the animation onto a standard skeleton, never your own rig,
    /// which skeleton mods often grow to every bone group there is.
    /// </summary>
    private void DrawSkeletonRepair(AnimationCapture capture)
    {
        Widgets.HintWrapped("Retargets the animation onto a standard skeleton, the game's own, IVCS or IVCS + YAS, so every bone " +
                            "sits at the index other players' skeletons and sync plugins expect, whatever your own skeleton mod " +
                            "adds. Only bones the animation moves are written. Live offsets stay as they are.");
        ImGui.Spacing();
        var clip = capture.Clip;
        var target = RepairTarget(clip);
        var plan = target is { } chosen ? AnimationBones.Plan(clip, chosen, animationExcludedBones) : null;
        using (var facts = ImRaii.Table("##repair-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                SetupFacts();
                Fact("Made for");
                if (SkeletonBlock(clip) is null && clip.Resolution?.Selected is { } source)
                    ImGui.TextUnformatted(SkeletonLabel(source));
                else
                    ImGui.TextColored(Theme.Hint, clip.Resolution?.State == SkeletonResolutionState.Ambiguous
                        ? "Several fit; choose one in the Source tab"
                        : "Not identified yet");
                Fact("Repair onto");
                DrawRepairTargets(clip, target);
                var animated = AnimationBones.Animated(clip).Length;
                if (plan is not null && animated > 0)
                {
                    Fact("Result");
                    ImGui.TextUnformatted(plan.Kept.IsEmpty
                        ? $"None of the {animated} animated bones"
                        : $"{plan.Kept.Length} of {animated} animated bones · highest bone index {plan.HighestIndex}" +
                          $" (now {AnimationBones.HighestIndex(clip, [])})");
                }
            }
        }
        if (plan is { Dropped.IsEmpty: false })
            Widgets.HintWrapped($"{plan.Dropped.Length} animated {(plan.Dropped.Length == 1 ? "bone" : "bones")} the " +
                                $"{AnimationBones.StandardName(plan.Target.Standard)} skeleton lacks will be left out: " +
                                string.Join(", ", plan.Dropped.Take(6).Select(AnimationPresentation.BoneName)) +
                                (plan.Dropped.Length > 6 ? $" and {plan.Dropped.Length - 6} more." : "."));
        DrawRebake(capture, AnimationOperation.RepairSkeleton, "Repair skeleton",
            "Retarget the animation onto the chosen standard skeleton, writing only the bones it moves.");
    }

    /// <summary> The standard skeleton a repair retargets onto: your choice, else the smallest that has every kept bone. </summary>
    private SkeletonStandard? RepairTarget(AnimationClip clip)
        => animationRepairTarget is { } chosen && AnimationBones.Standard(clip, chosen) is not null
            ? chosen
            : AnimationBones.DefaultRepairTarget(clip, animationExcludedBones);

    private void DrawRepairTargets(AnimationClip clip, SkeletonStandard? target)
    {
        var first = true;
        foreach (var standard in Enum.GetValues<SkeletonStandard>())
        {
            if (!first) ImGui.SameLine(0, Theme.Scaled(12));
            first = false;
            var found = AnimationBones.Standard(clip, standard);
            using (ImRaii.Disabled(found is null))
            {
                if (ImGui.RadioButton(AnimationBones.StandardName(standard), target == standard))
                    animationRepairTarget = standard;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(found is not null
                    ? $"{found.Skeleton.Bones.Length} bones, from {found.Origin}"
                    : standard == SkeletonStandard.Vanilla
                        ? "The game's own skeleton for this animation's model was not found."
                        : $"No {AnimationBones.StandardName(standard)} skeleton for this model is in the skeleton library. " +
                          "Install one and rebuild the library in Settings.");
        }
    }

    /// <summary> What stops a repair onto the chosen standard, or null when nothing does. </summary>
    private string? RepairBlock(AnimationCapture capture, ImmutableArray<string> excluded)
    {
        if (RepairTarget(capture.Clip) is not { } target)
            return "No standard skeleton was found for this animation's model.";
        var name = AnimationBones.StandardName(target);
        if (AnimationBones.Plan(capture.Clip, target, excluded) is not { } plan)
            return $"No {name} skeleton was found for this animation's model.";
        if (plan.Kept.IsEmpty)
            return $"None of the bones this animation moves are on the {name} skeleton.";
        if (excluded.IsEmpty && capture.Clip.Resolution?.Selected?.Skeleton.Fingerprint == plan.Target.Skeleton.Fingerprint)
            return $"The animation was already made for the {name} skeleton.";
        return null;
    }

    /// <summary>
    /// The end of the LivePose, Animated bones and Skeleton repair tabs: what the rebake covers and
    /// where it is saved, then the tab's rebake button and Undo last edit.
    /// </summary>
    private void DrawRebake(AnimationCapture capture, AnimationOperation operation, string label, string description)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (animationStartupSelected)
            Widgets.HintWrapped($"Rebakes change {AnimationPresentation.AnimationName(capture)}, the animation this startup leads into. " +
                                "Include the startup to change it as well.");
        var startupReady = capture.Startup is { Resolution: { State: SkeletonResolutionState.Matched, Selected: not null } };
        if (capture.Startup is not null)
        {
            using (ImRaii.Disabled(!startupReady))
                ImGui.Checkbox("Include startup in rebake", ref animationIncludeStartup);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(startupReady
                    ? AnimationPresentation.AnimationName(capture, true)
                    : SkeletonBlock(capture.Startup) ?? "The startup animation is not ready to include.");
        }
        var includeStartup = animationIncludeStartup && startupReady;

        if (ImGui.RadioButton("Create new mod", animationDestination == AnimationDestination.NewMod))
            animationDestination = AnimationDestination.NewMod;
        ImGui.SameLine();
        if (ImGui.RadioButton("Replace in-place", animationDestination == AnimationDestination.InPlace))
            animationDestination = AnimationDestination.InPlace;
        if (animationDestination == AnimationDestination.NewMod)
        {
            ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(400), ImGui.GetContentRegionAvail().X));
            ImGui.InputText("Mod name", ref animationModName, 128);
        }

        var clips = includeStartup && capture.Startup is { } startupClip ? new[] { capture.Clip, startupClip } : new[] { capture.Clip };
        // Bones unticked under Animated bones are left out of every rebake.
        ImmutableArray<string> excluded = [.. animationExcludedBones.Order(StringComparer.Ordinal)];
        var problem = RebakeProblem(capture, operation, clips, excluded);
        if (problem is not null)
            Widgets.Banner("##rebake-problem", FeedbackSeverity.Warning, problem);
        else if (operation != AnimationOperation.ExcludeBones && !excluded.IsEmpty)
            Widgets.Hint(excluded.Length == 1
                ? "1 bone unticked under Animated bones will be left out as well."
                : $"{excluded.Length} bones unticked under Animated bones will be left out as well.");
        var missing = RebakeMissing(capture, operation, excluded);
        using (ImRaii.Disabled(animations!.Busy || problem is not null || missing is not null))
        {
            if (ImGui.Button(label))
                animations.Edit(RebakeRequest(capture, operation, includeStartup, excluded));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(missing ?? description);
        ImGui.SameLine(0, Theme.Scaled(16));
        DrawUndoLastEdit();
    }

    /// <summary> What stops a rebake, shown as a warning above its button; null when nothing does. </summary>
    private string? RebakeProblem(AnimationCapture capture, AnimationOperation operation, AnimationClip[] clips, ImmutableArray<string> excluded)
    {
        var problem = capture.UnavailableReason ?? clips.Select(SkeletonBlock).FirstOrDefault(reason => reason != null);
        if (animationDestination == AnimationDestination.InPlace &&
            clips.Any(clip => !capture.Sources.Any(s => s.GamePath == clip.GamePath && AnimationResources.CanReplace(s))))
            problem ??= "This animation has no writable Penumbra source. Choose Create new mod.";
        problem ??= AnimationBones.Problem(capture.Clip, excluded);
        if (operation == AnimationOperation.BakeOffsets)
            problem ??= AnimationBones.OffsetConflict(excluded, animationBones.Select(b => b.Name));
        // An included startup is repaired onto the same standard, so it needs that skeleton too.
        if (operation == AnimationOperation.RepairSkeleton && RepairTarget(capture.Clip) is { } target &&
            clips.Skip(1).Any(clip => AnimationBones.Standard(clip, target) is null))
            problem ??= $"No {AnimationBones.StandardName(target)} skeleton was found for the startup. Leave it out of the rebake.";
        return problem;
    }

    /// <summary> What a rebake still needs from you, shown on its button; null when it has everything. </summary>
    private string? RebakeMissing(AnimationCapture capture, AnimationOperation operation, ImmutableArray<string> excluded)
        => operation switch
        {
            AnimationOperation.BakeOffsets when capture.PoseUnavailableReason is not null => "LivePose adjustments are unavailable.",
            AnimationOperation.BakeOffsets when animationBones.Count == 0 => "Tick at least one adjusted bone.",
            AnimationOperation.BakeOffsets when animationComponents == PoseComponents.None => "Tick position, rotation or scale.",
            AnimationOperation.ExcludeBones when excluded.IsEmpty => "Untick at least one bone the animation should stop moving.",
            AnimationOperation.RepairSkeleton => RepairBlock(capture, excluded),
            _ => null,
        };

    private AnimationBakeRequest RebakeRequest(AnimationCapture capture, AnimationOperation operation, bool includeStartup, ImmutableArray<string> excluded)
        => operation == AnimationOperation.BakeOffsets
            ? new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination, animationModName.Trim(), includeStartup,
                animationBones.ToImmutableHashSet(), animationComponents, ExcludedBones: excluded)
            : new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination, animationModName.Trim(), includeStartup,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, operation, ExcludedBones: excluded,
                RepairTarget: operation == AnimationOperation.RepairSkeleton ? RepairTarget(capture.Clip) : null);

    private void DrawUndoLastEdit()
    {
        var undoable = FindUndoableLastEdit();
        using (ImRaii.Disabled(animations!.Busy || undoable is null))
        {
            if (ImGui.Button("Undo last edit"))
                animations.Undo(undoable!);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(undoable is null
                ? "There is no completed edit to undo."
                : $"Revert {AnimationPresentation.AnimationName(undoable.Request.Capture, false)} ({OperationLabel(undoable.Request)}, {DestinationLabel(undoable.Request)}) and restore its live offsets.");
    }

    private static string? SkeletonBlock(AnimationClip clip) => clip.Resolution is { State: not SkeletonResolutionState.Matched } resolution
        ? resolution.Reason ?? "A compatible animation source is still being identified." : null;
}
