using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Main;

/// <summary>
/// The Animations tab: what your character plays, listed like On Screen with the mod each animation
/// comes from and a pen that sends it to Blender, and below it the selected animation's timeline
/// section: the clip, a few facts about it and the button that opens it in the Timeline window, where
/// every tool works. With nothing selected it opens the window's loader, which plays any animation on
/// the GPose character.
/// </summary>
internal sealed class AnimationsTab
{
    private readonly OnScreenService _onScreen;
    private readonly AnimationIntegration _animations;
    private readonly AnimationSendDialog _animationSend;
    private readonly RecorderPanel _recorder;
    private readonly ResourceRows _rows;
    private readonly ExpansionState _expansion;
    private readonly CancellationToken _lifetime;
    private AnimationCapture? animationSelection;
    private bool animationStartupSelected;
    private string animationFilter = "";
    private readonly ResourceSearch animationSearch = new();
    // The list's view model, rebuilt when the listener's history or the On Screen snapshot changes.
    private ImmutableArray<AnimationCapture> animationRowSource;
    private IReadOnlyList<OnScreenObject>? animationRowActors;
    private ImmutableArray<AnimationRow> animationRows = [];
    private ActorView animationActor = new(null, "Your character", string.Empty, [], 0);
    private Action<AnimationCapture, bool>? openTimeline;
    // Opens the Timeline window as it is, with its loader when no clip is open.
    private Action? showTimeline;
    private Task<ImmutableArray<TimelineEffectCheck.Problem>>? effectCheck;
    private CancellationTokenSource? effectCheckCancel;
    private string? effectCheckKey;

    public AnimationsTab(OnScreenService onScreen, AnimationIntegration animations, AnimationSendDialog animationSend, RecorderPanel recorder,
        ResourceRows rows, ExpansionState expansion, CancellationToken lifetime)
    {
        _onScreen = onScreen;
        _animations = animations;
        _animationSend = animationSend;
        _recorder = recorder;
        _rows = rows;
        _expansion = expansion;
        _lifetime = lifetime;
    }

    public void AttachTimeline(Action<AnimationCapture, bool>? open, Action? show)
    {
        openTimeline = open;
        showTimeline = show;
    }

    private void SelectAnimation(AnimationCapture capture, bool startup = false)
    {
        animationSelection = capture;
        animationStartupSelected = startup && capture.Startup != null;
    }

    private void ClearAnimationSelection() => animationSelection = null;

    public void Draw()
    {
        ImGui.Spacing();
        var animations = _animations.Service;
        if (animations == null)
        {
            Widgets.Banner("##animations-unavailable", FeedbackSeverity.Warning, _animations.Unavailable);
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
            : new ActorView(entity, entity.PresentationCategory.ToString(), UiText.Safe(entity.Name), views, entity.ObjectIndex);

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
        using var table = ImRaii.Table("##animation-table", 4, ResourceRows.ResourceTableFlags | ImGuiTableFlags.ScrollY,
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
        ResourceRows.DrawOpaqueRow();
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
        using var id = ImRaii.PushId(UiText.SafeId(row.Key));
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.WithAlpha(Theme.AnimationKind, Theme.KindTintAlpha)));
        if (animationSelection?.Id == row.Capture.Id && animationStartupSelected == row.Startup)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(Theme.WithAlpha(Theme.Selection, .6f)));

        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorPosX() + Theme.TreeIndent;
        // A startup hangs under the animation it leads into.
        if (row.Startup)
            DrawStartupGuide();
        ImGui.SetCursorPosX(start + (row.Startup ? ResourceRows.TreeStep : 0));
        Widgets.Icon(AnimationIcon(row.Capture), Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        var problem = SkeletonBlock(row.Clip);
        var searching = row.Clip.Resolution?.State == SkeletonResolutionState.Searching;
        var problemIcon = searching ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.ExclamationTriangle;
        var tags = (row.Playing ? BadgeWidth("playing") + Theme.Gap : 0) + (problem is null ? 0 : IconWidth(problemIcon) + Theme.Gap);
        if (ImGui.Selectable($"{UiText.Safe(view.Name, "Animation")}##label", false, ImGuiSelectableFlags.None,
                new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X - tags), ImGui.GetFrameHeight())))
            SelectAnimation(row.Capture, row.Startup);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(ResourceRows.RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
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
        ResourceRows.DrawSourceBadge(view);
        ImGui.TableSetColumnIndex(2);
        Widgets.PathText(UiText.Safe(view.SourceRelativePath, view.GamePath), UiText.Safe(view.ActualPath));
        ImGui.TableSetColumnIndex(3);
        if (Widgets.IconButton("##send-animation", FontAwesomeIcon.Pen, "Send this animation to Blender"))
            _animationSend.OpenAnimationSend(view);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.GhostIconButton("##more", FontAwesomeIcon.EllipsisH, "More actions"))
            ImGui.OpenPopup(ResourceRows.RowMenuPopup);
        _rows.DrawRowMenu(animationActor, view);
    }

    /// <summary> The line joining a startup row to the animation above it, hanging from that row's glyph. </summary>
    private static void DrawStartupGuide()
    {
        var origin = ImGui.GetCursorScreenPos();
        var frame = ImGui.GetFrameHeight();
        var x = MathF.Floor(origin.X + Theme.TreeIndent + IconWidth(FontAwesomeIcon.Running) / 2) + .5f;
        var middle = MathF.Floor(origin.Y + frame / 2) + .5f;
        var end = origin.X + Theme.TreeIndent + ResourceRows.TreeStep - Theme.Scaled(3);
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

    // ---- The timeline section ----------------------------------------------------------------

    /// <summary>
    /// Checks the selected clip's effects on a task, once per clip and file version, so the section can
    /// say when a looping effect stops after its first loop before the timeline is opened.
    /// </summary>
    private ImmutableArray<TimelineEffectCheck.Problem>? EffectProblems(AnimationCapture capture, bool startup, AnimationClip clip,
        AnimationResource? file)
    {
        if (_animations.Service is not { } service)
            return [];
        var key = $"{capture.Id}|{startup}|{clip.GamePath}|{file?.Hash}|{capture.ExpressionTimeline?.Hash}";
        if (key != effectCheckKey)
        {
            effectCheckCancel?.Cancel();
            effectCheckCancel?.Dispose();
            effectCheckCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            var token = effectCheckCancel.Token;
            effectCheckKey = key;
            effectCheck = Task.Run(async () =>
            {
                var source = await service.LoadTimelineAsync(capture, startup, token);
                return source.Document is { } doc ? source.EffectProblems(doc) : [];
            }, token);
        }
        return effectCheck is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }

    /// <summary>How tall the section below the list is drawn, so the list leaves it its room.</summary>
    private static float TimelineSectionHeight() => ImGui.GetFrameHeightWithSpacing() * 6 + ImGui.GetTextLineHeightWithSpacing() * 2;

    private void DrawAnimationTimeline()
    {
        if (_animations.Service is { } service && TimelineWindow.RecoveryAttention(service) is var attention and > 0)
        {
            Widgets.Banner("##animation-recovery", FeedbackSeverity.Warning, attention == 1
                ? "An animation edit did not finish. Undo it under Edits in the Timeline window."
                : $"{attention} animation edits did not finish. Undo them under Edits in the Timeline window.");
            ImGui.Spacing();
        }
        if (animationSelection is not { } capture)
        {
            Widgets.EmptyState(FontAwesomeIcon.Film, "Select an animation",
                "Choose one above to edit it in the timeline, or open the timeline to load any animation in GPose.");
            ImGui.Spacing();
            DrawOpenTimeline(null, false);
            return;
        }
        var startup = animationStartupSelected && capture.Startup is not null;
        var clip = startup ? capture.Startup! : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        var expression = !startup ? capture.ExpressionTimeline : null;
        DrawSelectedAnimationTitle(capture);
        // An expression's timeline is a file of its own, often from another mod than its face.
        var timelineFile = expression ?? file;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, (expression != null ? "Face length " : "Length ") +
                                       (clip.Duration > 0 ? clip.Duration.ToString("0.##", CultureInfo.InvariantCulture) + "s" : "unknown") +
                                       " · " + (timelineFile?.ModName ?? timelineFile?.ModDirectory ?? "Game Data"));
        if (SkeletonBlock(clip) is { } problem)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Hint, clip.Resolution?.State == SkeletonResolutionState.Ambiguous
                ? "Several skeletons fit. Choose the one it was made for under Source in the timeline."
                : problem);
        }
        if (EffectProblems(capture, startup, clip, file) is { IsEmpty: false } found && TimelineWindowViews.EffectLoopWarning(found) is { } warning)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            var one = found.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            ImGui.TextColored(Theme.Warning, $"⚠ {warning} Open the timeline to keep {(one ? "it" : "them")} looping.");
        }
        ImGui.Spacing();
        DrawOpenTimeline(capture, startup);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, expression != null
            ? "Sounds, effects and faces, a kept face, and a preview in GPose."
            : "Events, length, bone layers, LivePose, animated bones, skeleton repair and a preview in GPose.");
    }

    /// <summary>Names the animation the section is about.</summary>
    private void DrawSelectedAnimationTitle(AnimationCapture capture)
    {
        var playing = capture.Playing && !animationStartupSelected ? " · playing" : "";
        Widgets.Icon(AnimationIcon(capture), Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, AnimationPresentation.AnimationName(capture, animationStartupSelected));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, AnimationPresentation.AnimationState(capture, animationStartupSelected) + playing);
    }

    /// <summary>Open timeline: the selected clip's, or with none the Timeline window and its loader.</summary>
    private void DrawOpenTimeline(AnimationCapture? capture, bool startup)
    {
        var available = capture is null ? showTimeline is not null : openTimeline is not null;
        using (ImRaii.Disabled(!available))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Film, "Open timeline", null, null, null, null))
            {
                if (capture is null)
                    showTimeline?.Invoke();
                else
                    openTimeline?.Invoke(capture, startup);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!available ? "The timeline editor is unavailable."
                : capture is null ? "Open the Timeline window to load an animation."
                : "Open this animation in the Timeline window.");
    }
}
