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
/// Only GPose actors are touched, and Stop or leaving GPose puts them back as the preview does. Its search and group are the
/// window's (<see cref="WindowLayout"/>), the same for every actor.
/// </summary>
/// <param name="open">Opens the timeline of a capture's clip, or of its startup.</param>
internal sealed class TimelineLoader(TimelineSession session, TimelinePreview timelinePreview, WindowLayout layout, Action<AnimationCapture, bool> open)
{
    // How long the loader waits before it says the listener hasn't heard the animation.
    private static readonly TimeSpan LoaderPatience = TimeSpan.FromSeconds(6);

    private Task<ImmutableArray<AnimationLibraryItem>>? libraryTask;
    private ImmutableArray<AnimationLibraryItem> library = [];
    private string? libraryError;
    // The list shown for one library, group and search.
    private (ImmutableArray<AnimationLibraryItem> Library, AnimationLibraryGroup Group, string Search, AnimationLibraryItem[] Rows)? loaderRows;
    // What the loader played and waits for the listener to hear, on which actor and since when.
    private (AnimationLibraryItem Item, ushort Actor, DateTime Since)? loaderWaiting;
    // The check that the clicked row's animation fits the actor's skeleton, before it plays.
    private (AnimationLibraryItem Item, ushort Actor, Task<string?> Problem)? loaderChecking;
    private string? loaderMessage;
    // The line DrawStatus draws where the window has no status line of its own.
    private readonly StatusLine statusLine = new();

    /// <summary> Stops waiting for what the loader played, and drops its message, when the window closes. </summary>
    public void Reset()
    {
        loaderWaiting = null;
        loaderChecking = null;
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

    /// <summary> The loader's search, group and list, <paramref name="height"/> tall, then why nothing can play when it can't. </summary>
    public void Draw(float height)
    {
        EnsureLibrary();
        PollLibrary();
        var start = ImGui.GetCursorPosY();
        var available = timelinePreview.PlaybackAvailable(out var reason);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##loader-search", "Search every animation", ref layout.LoaderSearch, 128);
        var searching = layout.LoaderSearch.Trim().Length > 0;
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(searching))
        {
            using (var combo = ImRaii.Combo("##loader-group", AnimationLibrary.GroupName(layout.LoaderGroup), ImGuiComboFlags.HeightLarge))
            {
                if (combo.Success)
                    foreach (var group in Groups)
                    {
                        if (ImGui.Selectable(AnimationLibrary.GroupName(group), group == layout.LoaderGroup))
                            layout.LoaderGroup = group;
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(GroupTip(group));
                    }
                else if (ComboWheel.Scroll(Groups, layout.LoaderGroup, out var picked))
                    layout.LoaderGroup = picked;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(searching ? "The search looks through every group." : GroupTip(layout.LoaderGroup));
        }

        var footer = available ? 0 : ImGui.GetFrameHeightWithSpacing() * 2;
        var listHeight = Math.Max(ImGui.GetFrameHeight() * 3, height - (ImGui.GetCursorPosY() - start) - footer);
        using (var list = ImRaii.Child("##loader-list", new Vector2(0, listHeight), true))
        {
            if (list.Success)
                DrawLoaderList(available);
        }
        if (!available)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, reason);
        }
    }

    /// <summary> The groups in the dropdown's order. </summary>
    private static readonly AnimationLibraryGroup[] Groups = Enum.GetValues<AnimationLibraryGroup>();

    private static string GroupTip(AnimationLibraryGroup group) => group switch
    {
        AnimationLibraryGroup.Idles => "The standing idle and every /changepose idle, with their starts.",
        AnimationLibraryGroup.GroundSits => "Sitting on the ground and every /changepose version, with their starts.",
        AnimationLibraryGroup.ChairSits => "Sitting in a chair and every /changepose version, with their starts.",
        AnimationLibraryGroup.Lying => "Lying in bed and every /changepose version, with their starts.",
        AnimationLibraryGroup.Movement => "Walking, running, sprinting, jumping and turning.",
        AnimationLibraryGroup.Expressions => "Faces such as /smile.",
        AnimationLibraryGroup.Other => "Every other animation the game has, by its name in the game files.",
        _ => "Every emote, with its sitting and upper body versions.",
    };

    /// <summary>The rows of the chosen group, or every match while searching.</summary>
    private void DrawLoaderList(bool available)
    {
        if (libraryTask != null)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Reading the game's animations");
            return;
        }
        if (libraryError != null)
        {
            Widgets.HintWrapped("The animation list could not be read: " + libraryError);
            return;
        }
        var search = layout.LoaderSearch.Trim();
        var rows = LoaderRows(search);
        if (rows.Length == 0)
        {
            ImGui.TextDisabled(search.Length > 0 ? "Nothing matches." : "None.");
            return;
        }
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
                    // An emote's /command only clutters the row: the search still finds it.
                    var shownDetail = item.Detail.StartsWith('/') ? null : item.Detail;
                    var detail = search.Length > 0
                        ? shownDetail is null ? AnimationLibrary.GroupName(item.Group) : $"{AnimationLibrary.GroupName(item.Group)} · {shownDetail}"
                        : shownDetail ?? string.Empty;
                    var detailWidth = ImGui.CalcTextSize(detail).X;
                    // In a narrow dock the detail goes to the tooltip rather than cut into the name.
                    var room = ImGui.GetContentRegionAvail().X;
                    var both = detail.Length == 0 || ImGui.CalcTextSize(item.Name).X + Theme.Gap + detailWidth <= room;
                    using (ImRaii.Disabled(!available))
                    {
                        if (ImGui.Selectable(item.Name, waiting, ImGuiSelectableFlags.None,
                                new Vector2(Math.Max(1, both && detail.Length > 0 ? room - detailWidth - Theme.Gap : room), 0)))
                            PlayFromLoader(item);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(both ? $"{(shownDetail is null ? item.Detail + " · " : "")}{item.Key} · timeline {item.Timeline}"
                            : $"{item.Name}\n{detail}\n{item.Key} · timeline {item.Timeline}");
                    if (!both || detail.Length == 0)
                        continue;
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextColored(Theme.Muted, detail);
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    private AnimationLibraryItem[] LoaderRows(string search)
    {
        if (loaderRows is { } cached && cached.Library == library && cached.Group == layout.LoaderGroup && cached.Search == search)
            return cached.Rows;
        AnimationLibraryItem[] rows = search.Length > 0
            ? [.. library.Where(item => AnimationLibrary.Matches(item, search))]
            : [.. library.Where(item => item.Group == layout.LoaderGroup)];
        loaderRows = (library, layout.LoaderGroup, search, rows);
        return rows;
    }

    /// <summary>
    /// Plays <paramref name="item"/> on the GPose actor and waits for the listener to hear it, once its
    /// animation is known to fit the actor's skeleton (<see cref="Poll"/>). The preview and Live end
    /// first: they play the open clip on the same actor. The actor keeps playing what it plays until the
    /// new one starts, rather than going back to its own animation in between.
    /// </summary>
    private bool PlayFromLoader(AnimationLibraryItem item)
    {
        timelinePreview.Stop(keepPlaying: true);
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
        loaderMessage = null;
        if (session.Animations is not { } service)
            return Play(item, index);
        loaderChecking = (item, index, service.PlayProblemAsync(index, item.Timeline, CancellationToken.None));
        loaderWaiting = (item, index, DateTime.UtcNow);
        return true;
    }

    private bool Play(AnimationLibraryItem item, ushort index)
    {
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
        if (loaderChecking is { Problem.IsCompleted: true } checking)
        {
            loaderChecking = null;
            // A check that failed plays it as the game would anyway.
            var problem = checking.Problem.IsCompletedSuccessfully ? checking.Problem.Result : null;
            if (checking.Problem.IsFaulted)
                session.Log.Warning(checking.Problem.Exception!, "Could not check the animation against the skeleton.");
            if (problem != null)
            {
                loaderWaiting = null;
                loaderMessage = problem;
            }
            else if (!Play(checking.Item, checking.Actor))
                loaderWaiting = null;
        }
        if (loaderChecking != null || loaderWaiting is not { } waiting || session.Animations is not { } service)
            return;
        // Another clip may have opened meanwhile, or the actor went back to its own animation.
        AnimationClip? Played(AnimationCapture capture) => capture.Clip.Timeline == waiting.Item.Timeline ? capture.Clip
            : capture.Startup?.Timeline == waiting.Item.Timeline ? capture.Startup : null;
        var playing = service.Observer.HistoryAt(waiting.Actor).Where(capture => capture.Playing && capture.ActorIndex == waiting.Actor && Played(capture) != null).ToArray();
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

    /// <summary>
    /// The loader's own line, for the main area while no clip is open and a timeline that can't be edited, with <paramref name="more"/> notes
    /// after the loader's; an open clip shows <see cref="Notes"/> in the window's status line instead.
    /// </summary>
    public void DrawStatus(IEnumerable<StatusNote>? more = null) => statusLine.Draw("##loader", more == null ? Notes() : Notes().Concat(more));

    /// <summary>The status line's note while the loader waits, with Cancel, or why it couldn't play.</summary>
    public IEnumerable<StatusNote> Notes()
    {
        if (loaderWaiting is { } waiting)
        {
            var slow = DateTime.UtcNow - waiting.Since > LoaderPatience;
            return [new StatusNote("loader", StatusKind.Working, slow
                ? $"{waiting.Item.Name} doesn't seem to play. It may need a weapon, a mount or another race."
                : $"Waiting for {waiting.Item.Name} to play", "Cancel", () =>
            {
                loaderWaiting = null;
                loaderChecking = null;
            })];
        }
        return loaderMessage is { } message ? [new StatusNote("loader", StatusKind.Error, message)] : [];
    }
}
