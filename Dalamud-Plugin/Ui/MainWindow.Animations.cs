using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Ui.Main;

namespace InstantEdit.Ui;

/// <summary>
/// The Animations tab: what your character plays, listed like On Screen with the mod each animation
/// comes from and a pen that sends it to Blender, and below it the selected animation's timeline:
/// every tool works in the Timeline window.
/// </summary>
public sealed partial class MainWindow
{
    private AnimationEditService? animations => _animationHub.Service;
    private string? animationError => _animationHub.Error;
    private AnimationCapture? animationSelection;
    private bool animationStartupSelected;
    private string animationFilter = "";
    private readonly ResourceSearch animationSearch = new();
    // The list's view model, rebuilt when the listener's history or the On Screen snapshot changes.
    private ImmutableArray<AnimationCapture> animationRowSource;
    private IReadOnlyList<OnScreenObject>? animationRowActors;
    private ImmutableArray<AnimationRow> animationRows = [];
    private ActorView animationActor = new(null, "Your character", string.Empty, [], 0);

    internal void AttachAnimations(AnimationEditService? service, string? error)
    {
        _animationHub.Attach(service, error);
    }

    private void SelectAnimation(AnimationCapture capture, bool startup = false)
    {
        animationSelection = capture;
        animationStartupSelected = startup && capture.Startup != null;
    }

    private void ClearAnimationSelection() => animationSelection = null;

    private void DrawAnimations()
    {
        ImGui.Spacing();
        if (animations == null)
        {
            Widgets.Banner("##animations-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation integration is unavailable.");
            ImGui.Spacing();
            // Recording reads the live skeleton directly and needs none of the editing services.
            _recorder.Draw();
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
        DrawAnimationTimeline();
    }

    /// <summary>
    /// Keeps the selection in step with the listener: everything (including Sources, which an in-place
    /// edit can change without the game reloading the file) follows the latest capture of the same clip.
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
                ExpressionTimeline = current.ExpressionTimeline,
                UnavailableReason = current.UnavailableReason,
                PoseUnavailableReason = current.PoseUnavailableReason,
            };
        }
        if (animationSelection != null && animationSelection.ActorId != observer.Actor)
            ClearAnimationSelection();
        if (animationSelection is { Playing: false } stale && !AnimationPresentation.Listed(stale))
            ClearAnimationSelection();
    }

    /// <summary> The list's rows, rebuilt only when the listener's history or the On Screen snapshot changes. </summary>
    private ImmutableArray<AnimationRow> ReadAnimationRows(ImmutableArray<AnimationCapture> history)
    {
        var actors = _onScreen.Items;
        if (history == animationRowSource && ReferenceEquals(actors, animationRowActors))
            return animationRows;

        var rows = AnimationRows.Build(history);
        _animationSend.SetDetectedRows(rows);
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

    /// <summary> The list takes its rows' height, at most what leaves the timeline section its room. </summary>
    private static float AnimationListHeight(int rows, float available)
    {
        var style = ImGui.GetStyle();
        var row = ImGui.GetFrameHeight() + style.CellPadding.Y * 2;
        // The header line, the table's column headers and the frame's padding.
        var chrome = ImGui.GetFrameHeightWithSpacing() + row + style.WindowPadding.Y * 2 + Theme.Scaled(2);
        var minimum = chrome + row * 3;
        return Math.Clamp(chrome + row * rows, minimum, Math.Max(minimum, available - TimelineSectionHeight()));
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
        Widgets.Icon(AnimationIcon(row.Capture), Theme.AnimationKind);
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
            _animationSend.OpenAnimationSend(view);
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
            ImGui.TextUnformatted("Made for " + AnimationSendDialog.SkeletonLabel(source));
    }

    private static float BadgeWidth(string text) => ImGui.CalcTextSize(text).X + Theme.Scaled(12);

    /// <summary> A face for an expression, a figure for every other animation. </summary>
    private static FontAwesomeIcon AnimationIcon(AnimationCapture capture) =>
        AnimationPresentation.IsExpression(capture) ? FontAwesomeIcon.Smile : FontAwesomeIcon.Running;

    private static float IconWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    private static string? SkeletonBlock(AnimationClip clip) => clip.Resolution is { State: not SkeletonResolutionState.Matched } resolution
        ? resolution.Reason ?? "A compatible animation source is still being identified." : null;
}
