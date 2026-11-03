using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Main;

/// <summary>
/// The Timeline dock's Now playing: what the shown actor plays now, a startup under its loop. A click on a row opens its
/// timeline; the row menu holds Send to Blender, the mod actions and Make wind-up, whose result shows in the Timeline's
/// status line.
/// </summary>
internal sealed class PlayingNowPage
{
    private readonly OnScreenService _onScreen;
    private readonly AnimationIntegration _animations;
    private readonly AnimationSendDialog _animationSend;
    private readonly ResourceRows _rows;
    // The list's view model, rebuilt when the listener's history or the On Screen snapshot changes.
    private ImmutableArray<AnimationCapture> animationRowSource;
    private IReadOnlyList<OnScreenObject>? animationRowActors;
    private ImmutableArray<AnimationRow> animationRows = [];
    private ActorView animationActor = new(null, "Your character", string.Empty, [], 0);
    private Action<AnimationCapture, bool>? openTimeline;
    // Make wind-up: the loop it is for and its frames, asked in a popup opened outside the row menu.
    private const string WindUpPopup = "##wind-up";
    private AnimationCapture? windUpFor;
    private int windUpFrames = AnimationEditService.DefaultWindUp;
    private bool openWindUp;

    /// <summary>Builds the page over the listener, the send dialog and the row menu it shares with On Screen.</summary>
    public PlayingNowPage(OnScreenService onScreen, AnimationIntegration animations, AnimationSendDialog animationSend, ResourceRows rows)
    {
        _onScreen = onScreen;
        _animations = animations;
        _animationSend = animationSend;
        _rows = rows;
    }

    /// <summary>What a click on a row calls: opens that animation, or its startup, in the Timeline window.</summary>
    public void AttachOpen(Action<AnimationCapture, bool> open) => openTimeline = open;

    /// <summary>The actor the list is of when it isn't you, such as a partner the Timeline shows ("Partner One"); null for "Your character".</summary>
    public string? ActorName { get; set; }

    /// <summary>
    /// How many rows play now, for the sidebar. Reads the listener's history, so it stays current on frames
    /// the page is not drawn; that also refreshes the rows the send dialog knows.
    /// </summary>
    public int Playing
    {
        get
        {
            if (_animations.Service is not { } service)
                return 0;
            var rows = ReadAnimationRows(service.Observer.History);
            var count = 0;
            foreach (var row in rows)
                if (row.Playing)
                    count++;
            return count;
        }
    }

    /// <summary>The rows playing now, a startup under its loop, or a line saying nothing plays.</summary>
    public void Draw()
    {
        var animations = _animations.Service;
        if (animations == null)
        {
            Widgets.HintWrapped(_animations.Unavailable);
            return;
        }
        animations.StartObservation();
        var rows = ReadAnimationRows(animations.Observer.History);
        var playing = rows.Where(row => row.Group == AnimationGroup.Playing).ToArray();
        if (playing.Length == 0)
            ImGui.TextColored(Theme.Hint, "Nothing plays now.");
        using (ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f)))
            foreach (var row in playing)
                DrawAnimationRow(row);
        DrawWindUpPopup(animations);
    }

    /// <summary>Make wind-up's popup: how many frames the move into the loop takes, and Save.</summary>
    private void DrawWindUpPopup(AnimationEditService service)
    {
        if (openWindUp)
        {
            openWindUp = false;
            windUpFrames = AnimationEditService.DefaultWindUp;
            ImGui.OpenPopup(WindUpPopup);
        }
        using var popup = ImRaii.Popup(WindUpPopup);
        if (!popup.Success || windUpFor is not { } capture)
            return;
        ImGui.TextColored(Theme.Label, "Frames");
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var enter = ImGui.InputInt("##wind-up-frames", ref windUpFrames, 1, 10, default, ImGuiInputTextFlags.EnterReturnsTrue);
        windUpFrames = Math.Clamp(windUpFrames, AnimationEditService.MinWindUp, AnimationEditService.MostWindUp);
        ImGui.TextColored(Theme.Muted, $"{windUpFrames / (float)TimelineDocument.Fps:0.0} seconds.");
        var blocked = service.Busy ? "Another animation edit is running." : AnimationEditService.InstantStartupBlock(capture);
        using (ImRaii.Disabled(blocked is not null))
        {
            if ((ImGui.Button("Save##wind-up-save") || enter) && blocked is null)
            {
                service.MakeInstantStartup(capture, windUpFrames);
                ImGui.CloseCurrentPopup();
            }
        }
        if (blocked is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked);
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
            ? new ActorView(null, ActorName ?? "Your character", string.Empty, views, 0)
            : new ActorView(entity, entity.PresentationCategory.ToString(), UiText.Safe(entity.Name), views, entity.ObjectIndex);

        animationRowSource = history;
        animationRowActors = actors;
        animationRows = rows;
        return rows;
    }

    /// <summary>
    /// One animation: its icon and name, a skeleton warning, and the More button for its menu. A click opens its timeline; its mod and
    /// file are in the tooltip.
    /// </summary>
    private void DrawAnimationRow(AnimationRow row)
    {
        var view = row.View;
        using var id = ImRaii.PushId(UiText.SafeId(row.Key));
        var left = ImGui.GetCursorPosX();
        // A startup hangs under the animation it leads into.
        if (row.Startup)
            DrawStartupGuide();
        ImGui.SetCursorPosX(left + (row.Startup ? ResourceRows.TreeStep : 0));
        Widgets.Icon(AnimationIcon(row.Capture), Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        var problem = SkeletonBlock(row.Clip);
        var searching = row.Clip.Resolution?.State == SkeletonResolutionState.Searching;
        var problemIcon = searching ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.ExclamationTriangle;
        var more = ImGui.GetFrameHeight();
        var tags = more + Theme.Gap + (problem is null ? 0 : IconWidth(problemIcon) + Theme.Gap);
        if (ImGui.Selectable($"{UiText.Safe(view.Name, "Animation")}##label", false, ImGuiSelectableFlags.None,
                new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X - tags), ImGui.GetFrameHeight())))
            openTimeline?.Invoke(row.Capture, row.Startup);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(ResourceRows.RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            DrawAnimationRowTooltip(row, problem, _animations.Service is { } service
                ? AnimationEditService.SavedRepair(service.Recovery, row.Capture, row.Startup) : null);
        if (problem is not null)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Icon(problemIcon, searching ? Theme.Muted : Theme.Warning);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(problem);
        }
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - more));
        if (Widgets.GhostIconButton("##more", FontAwesomeIcon.EllipsisH, "More actions"))
            ImGui.OpenPopup(ResourceRows.RowMenuPopup);
        _rows.DrawRowMenu(animationActor, view, () => DrawRowItems(row));
    }

    /// <summary>The row menu's own items: Send to Blender, and Make wind-up on a loop with a startup.</summary>
    private void DrawRowItems(AnimationRow row)
    {
        if (ImGui.MenuItem("Send to Blender"))
            _animationSend.OpenAnimationSend(row.View, AnimationSendDialog.TimelineOwner);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Send this animation to Blender as its file has it.");
        if (!row.Startup && row.Capture.Startup is not null)
            DrawInstantStartupItem(row.Capture);
    }

    /// <summary>A loop's menu item that gives it a wind-up startup (see <see cref="AnimationEditService.MakeInstantStartup"/>), asking its frames first.</summary>
    private void DrawInstantStartupItem(AnimationCapture capture)
    {
        var service = _animations.Service;
        var blocked = service is null ? _animations.Unavailable
            : service.Busy ? "Another animation edit is running."
            : AnimationEditService.InstantStartupBlock(capture);
        using (ImRaii.Disabled(blocked is not null))
            if (ImGui.MenuItem("Make wind-up") && service is not null)
            {
                windUpFor = capture;
                openWindUp = true;
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Replace the startup with a smooth move into the loop.\n" +
                $"Saved in {UiText.Safe(capture.Sources.FirstOrDefault(s => s.GamePath == capture.Clip.GamePath)?.ModName, "the loop's mod")}.\n" +
                "Undo it in Save history.");
    }

    /// <summary> The line joining a startup row to the animation above it, hanging from that row's glyph. </summary>
    private static void DrawStartupGuide()
    {
        var origin = ImGui.GetCursorScreenPos();
        var frame = ImGui.GetFrameHeight();
        var x = MathF.Floor(origin.X + IconWidth(FontAwesomeIcon.Running) / 2) + .5f;
        var middle = MathF.Floor(origin.Y + frame / 2) + .5f;
        var end = origin.X + ResourceRows.TreeStep - Theme.Scaled(3);
        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Theme.TreeLine);
        var thickness = Math.Max(1, MathF.Floor(Theme.Scale));
        drawList.AddLine(new Vector2(x, origin.Y - ImGui.GetStyle().ItemSpacing.Y), new Vector2(x, middle), colour, thickness);
        drawList.AddLine(new Vector2(x, middle), new Vector2(end, middle), colour, thickness);
    }

    /// <summary>
    /// A row's name, state and game path, and the skeleton it was made for or why none is matched yet; with <paramref name="saved"/>,
    /// the standard a save repaired its file onto, which the file alone reads back as a smaller skeleton.
    /// </summary>
    private static void DrawAnimationRowTooltip(AnimationRow row, string? problem, SkeletonStandard? saved)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Label, row.View.Name);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, AnimationPresentation.AnimationState(row.Capture, row.Startup));
        ImGui.TextUnformatted(row.View.SourceModName.Length > 0 ? "From " + row.View.SourceModName : "From the game's own files");
        ImGui.TextColored(Theme.Hint, "Game path: " + row.Clip.GamePath);
        ImGui.TextColored(Theme.Hint, "Click to open it. Right-click for more.");
        if (problem is not null)
            ImGui.TextColored(Theme.Warning, problem);
        else if (row.Clip.Resolution?.Selected is { } source)
            ImGui.TextUnformatted("Made for " + (saved is { } standard
                ? AnimationPresentation.SkeletonLabel(source, standard, AnimationBones.Standard(row.Clip, standard)?.Skeleton)
                : AnimationSendDialog.SkeletonLabel(source)));
    }


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
