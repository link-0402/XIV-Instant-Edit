using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The loader: any emote, idle, sitting or lying pose, movement track, expression or other timeline,
/// played on the GPose actor. Once the listener hears it, its timeline opens (<paramref name="open"/>).
/// Only GPose actors are touched, and Stop or leaving GPose puts them back as the preview does.
/// </summary>
/// <param name="open">Opens the timeline of a capture's clip, or of its startup.</param>
internal sealed class TimelineLoader(TimelineSession session, TimelinePreview timelinePreview, Action<AnimationCapture, bool> open)
{
    private const string LoaderPopup = "##timeline-loader";
    // How long the loader waits before it says the listener hasn't heard the animation.
    private static readonly TimeSpan LoaderPatience = TimeSpan.FromSeconds(6);

    private Task<ImmutableArray<AnimationLibraryItem>>? libraryTask;
    private ImmutableArray<AnimationLibraryItem> library = [];
    private string? libraryError;
    private AnimationLibraryGroup loaderGroup = AnimationLibraryGroup.Emotes;
    private string loaderSearch = string.Empty;
    // The list shown for one library, group and search.
    private (ImmutableArray<AnimationLibraryItem> Library, AnimationLibraryGroup Group, string Search, AnimationLibraryItem[] Rows)? loaderRows;
    // What the loader played and waits for the listener to hear, on which actor and since when.
    private (AnimationLibraryItem Item, ushort Actor, DateTime Since)? loaderWaiting;
    private string? loaderMessage;

    /// <summary> Stops waiting for what the loader played, and drops its message, when the window closes. </summary>
    public void Reset()
    {
        loaderWaiting = null;
        loaderMessage = null;
    }
    private void EnsureLibrary()
    {
        // A failed read is not started again every frame; its error stays shown.
        if (libraryTask != null || !library.IsEmpty || libraryError != null)
            return;
        libraryTask = Task.Run(() => AnimationLibrary.Read(session.Data));
    }

    public void PollLibrary()
    {
        if (libraryTask is not { IsCompleted: true } task)
            return;
        libraryTask = null;
        if (task.IsCompletedSuccessfully)
            library = task.Result;
        else
        {
            libraryError = TimelineSession.ErrorText(task.Exception);
            session.Log.Warning(task.Exception!, "Could not read the animation list.");
        }
    }

    /// <summary>The toolbar's Load button and its popup.</summary>
    public void DrawButton()
    {
        if (Widgets.IconButton("##timeline-load", FontAwesomeIcon.FolderOpen, "Load an animation onto your GPose character and open it here"))
        {
            EnsureLibrary();
            ImGui.OpenPopup(LoaderPopup);
        }
        using var popup = ImRaii.Popup(LoaderPopup);
        if (!popup.Success)
            return;
        // A popup sizes itself to its content, so the loader gets a box of its own to fill.
        var played = false;
        using (var body = ImRaii.Child("##loader-body", new Vector2(Theme.Scaled(560), Theme.Scaled(420)), false))
        {
            if (body.Success)
                played = Draw(ImGui.GetContentRegionAvail().Y);
        }
        if (played)
            ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// The loader's search, group tabs and list, <paramref name="height"/> tall. True when an animation
    /// was played, so a popup around it can close.
    /// </summary>
    public bool Draw(float height)
    {
        EnsureLibrary();
        PollLibrary();
        var start = ImGui.GetCursorPosY();
        var available = timelinePreview.PlaybackAvailable(out var reason);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##loader-search", "Search every animation", ref loaderSearch, 128);
        var searching = loaderSearch.Trim().Length > 0;
        using (ImRaii.Disabled(searching))
        using (var tabs = ImRaii.TabBar("##loader-groups", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            if (tabs.Success)
                foreach (var group in Enum.GetValues<AnimationLibraryGroup>())
                {
                    if (ImGui.BeginTabItem(AnimationLibrary.GroupName(group)))
                    {
                        loaderGroup = group;
                        ImGui.EndTabItem();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(GroupTip(group));
                }
        }

        var footer = ImGui.GetFrameHeightWithSpacing() * 2;
        var listHeight = Math.Max(ImGui.GetFrameHeight() * 3, height - (ImGui.GetCursorPosY() - start) - footer);
        var played = false;
        using (var list = ImRaii.Child("##loader-list", new Vector2(0, listHeight), true))
        {
            if (list.Success)
                played = DrawLoaderList(available);
        }
        if (!available)
            ImGui.TextColored(Theme.Warning, reason);
        else
            ImGui.TextColored(Theme.Hint, "Plays on your GPose character, then opens here.");
        return played;
    }

    private static string GroupTip(AnimationLibraryGroup group) => group switch
    {
        AnimationLibraryGroup.Idles => "The standing idle and every /changepose idle.",
        AnimationLibraryGroup.GroundSits => "Sitting on the ground and every /changepose version.",
        AnimationLibraryGroup.ChairSits => "Sitting in a chair and every /changepose version.",
        AnimationLibraryGroup.Lying => "Lying in bed and every /changepose version.",
        AnimationLibraryGroup.Movement => "Walking, running, sprinting, jumping and turning.",
        AnimationLibraryGroup.Expressions => "Faces such as /smile.",
        AnimationLibraryGroup.Other => "Every other animation the game has, by its name in the game files.",
        _ => "Every emote, with its sitting and upper body versions.",
    };

    /// <summary>The rows of the chosen group, or every match while searching; true when one was played.</summary>
    private bool DrawLoaderList(bool available)
    {
        if (libraryTask != null)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Reading the game's animations");
            return false;
        }
        if (libraryError != null)
        {
            Widgets.HintWrapped("The animation list could not be read: " + libraryError);
            return false;
        }
        var search = loaderSearch.Trim();
        var rows = LoaderRows(search);
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(search.Length > 0 ? "Nothing matches." : "None.");
            return false;
        }
        var played = false;
        using var align = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f));
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Length, -1);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var item = rows[index];
                    using var id = ImRaii.PushId(index);
                    var waiting = loaderWaiting?.Item == item;
                    Widgets.Icon(item.Face ? FontAwesomeIcon.Smile : FontAwesomeIcon.Running, Theme.AnimationKind);
                    ImGui.SameLine(0, Theme.Gap);
                    var detail = search.Length > 0 ? $"{AnimationLibrary.GroupName(item.Group)} · {item.Detail}" : item.Detail;
                    var detailWidth = ImGui.CalcTextSize(detail).X;
                    using (ImRaii.Disabled(!available))
                    {
                        if (ImGui.Selectable(item.Name, waiting, ImGuiSelectableFlags.None,
                                new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X - detailWidth - Theme.Gap), 0)))
                            played = PlayFromLoader(item);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip($"{item.Key} · timeline {item.Timeline}");
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextColored(Theme.Muted, detail);
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
        return played;
    }

    private AnimationLibraryItem[] LoaderRows(string search)
    {
        if (loaderRows is { } cached && cached.Library == library && cached.Group == loaderGroup && cached.Search == search)
            return cached.Rows;
        AnimationLibraryItem[] rows = search.Length > 0
            ? [.. library.Where(item => AnimationLibrary.Matches(item, search))]
            : [.. library.Where(item => item.Group == loaderGroup)];
        loaderRows = (library, loaderGroup, search, rows);
        return rows;
    }

    /// <summary>
    /// Plays <paramref name="item"/> on the GPose actor and waits for the listener to hear it. The
    /// preview and Live end first: they play the open clip on the same actor.
    /// </summary>
    private bool PlayFromLoader(AnimationLibraryItem item)
    {
        timelinePreview.Stop();
        ushort? actor;
        try { actor = session.Playback.ActorIndex(); }
        catch (InvalidOperationException e)
        {
            loaderMessage = e.Message;
            return false;
        }
        if (actor is not { } index)
        {
            loaderMessage = "Enter GPose to load an animation.";
            return false;
        }
        bool played;
        try { played = session.Playback.Play(item.Timeline, null, null, item.Face, index); }
        catch (InvalidOperationException e)
        {
            loaderMessage = e.Message;
            return false;
        }
        if (!played)
        {
            loaderMessage = session.Playback.Error;
            return false;
        }
        session.Animations?.StartObservation();
        session.Animations?.Observer.Hear(item.Timeline);
        loaderWaiting = (item, index, DateTime.UtcNow);
        loaderMessage = null;
        return true;
    }

    /// <summary>Every frame: opens what the loader played once the listener has heard it.</summary>
    public void Poll()
    {
        if (loaderWaiting is not { } waiting || session.Animations is not { } service)
            return;
        // Another clip may have opened meanwhile, or the actor went back to its own animation.
        AnimationClip? Played(AnimationCapture capture) => capture.Clip.Timeline == waiting.Item.Timeline ? capture.Clip
            : capture.Startup?.Timeline == waiting.Item.Timeline ? capture.Startup : null;
        var playing = service.Observer.History.Where(capture => capture.Playing && capture.ActorIndex == waiting.Actor && Played(capture) != null).ToArray();
        if (playing.Length == 0)
            return;
        // One timeline can play several clips, such as Dance's cbem_dance and its 6-bone cbep_e_dance: the one moving
        // the most bones opens, once each knows its skeleton (or the loader's patience runs out).
        if (playing.Length > 1 && DateTime.UtcNow - waiting.Since < LoaderPatience &&
            playing.Any(capture => Played(capture)!.Resolution is null or { State: SkeletonResolutionState.Searching }))
            return;
        var chosen = playing.MaxBy(capture => Played(capture)!.Resolution?.TrackBones.Length ?? 0)!;
        loaderWaiting = null;
        loaderMessage = null;
        open(chosen, chosen.Clip.Timeline != waiting.Item.Timeline);
    }

    /// <summary>The loader's line while it waits, or why it couldn't play.</summary>
    public void DrawStatus()
    {
        if (loaderWaiting is { } waiting)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            var slow = DateTime.UtcNow - waiting.Since > LoaderPatience;
            ImGui.TextColored(slow ? Theme.Warning : Theme.Muted, slow
                ? $"{waiting.Item.Name} doesn't seem to play. It may need a weapon, a mount or another race."
                : $"Waiting for {waiting.Item.Name} to play");
            ImGui.SameLine(0, Theme.Gap);
            if (ImGui.SmallButton("Cancel##loader"))
                loaderWaiting = null;
        }
        else if (loaderMessage is { } message)
        {
            if (Widgets.GhostIconButton("##loader-message-dismiss", FontAwesomeIcon.Times, "Dismiss"))
                loaderMessage = null;
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, message);
        }
    }
}
