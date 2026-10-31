using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Timeline;

/// <summary> The start page's sidebar entries, in their order: what plays now, the game's animations, saved edits, partners and Record. </summary>
internal enum StartEntry : byte { Playing, Game, Saved, Partners, Record }

/// <summary>
/// The Timeline's start page: a sidebar with one row per entry and the chosen entry's page beside it, the page's last
/// line under both: the loader's line, and the animation job running or how it went. Shown when no clip is open, and over
/// an open clip after Start, with Back to the clip first.
/// </summary>
/// <param name="recorder">The live pose recorder's page; null until the main window attaches it.</param>
internal sealed class StartPage(TimelineSession session, TimelineLoader loader, RecentEditsPage recentEdits, Func<Action?> recorder)
{
    private Action? drawPlaying;
    private Func<int>? playing;
    private Action? drawPartners;
    // The last line under the page as it was last drawn; nothing takes no height.
    private float statusHeight;
    // An animation job runs (a send to Blender, an undo, a save), and whether the page showed while it ran.
    private bool jobRunning;
    private bool jobSeen;
    // How the last job the page saw ended, for its last line; null until one ends, and while the next one runs.
    private (string Text, bool Failed)? jobResult;

    /// <summary> The entry shown, for this session: Playing now at first, then the last one chosen. </summary>
    public StartEntry Current { get; set; } = StartEntry.Playing;

    /// <summary> The sidebar's entries in their order: Partners only with a spawner attached, Record only with the recorder. </summary>
    public static ImmutableArray<StartEntry> Entries(bool partners, bool record)
    {
        var entries = ImmutableArray.CreateBuilder<StartEntry>(5);
        entries.Add(StartEntry.Playing);
        entries.Add(StartEntry.Game);
        entries.Add(StartEntry.Saved);
        if (partners)
            entries.Add(StartEntry.Partners);
        if (record)
            entries.Add(StartEntry.Record);
        return entries.ToImmutable();
    }

    /// <summary> The main window's Playing now page and how many animations play now, for the sidebar. </summary>
    public void AttachPlayingNow(Action draw, Func<int> count)
    {
        drawPlaying = draw;
        playing = count;
    }

    /// <summary> The partner spawner's page; its entry shows once this is attached. </summary>
    public void AttachPartners(Action draw) => drawPartners = draw;

    /// <summary>
    /// Every frame: keeps how the animation service's job ended when the page showed while it ran (<paramref name="shown"/>),
    /// such as Send to Blender from Playing now or Undo in Saved edits. A job seen only from the clip view keeps nothing:
    /// its own row says how it went.
    /// </summary>
    public void Poll(bool shown)
    {
        if (session.Animations is not { } service)
            return;
        if (service.Busy)
        {
            if (!jobRunning)
                jobSeen = false;
            jobRunning = true;
            jobSeen |= shown;
            jobResult = null;
        }
        else if (jobRunning)
        {
            jobRunning = false;
            if (jobSeen || shown)
                jobResult = (service.Status, service.LastFailed);
        }
    }

    /// <summary> The last line's note on the animation job: running with Cancel when it can stop, else how it ended. </summary>
    private IEnumerable<StatusNote> JobNotes()
    {
        if (session.Animations is not { } service)
            return [];
        if (service.Busy)
            return [service.CanCancel
                ? new StatusNote("animation-job", StatusKind.Working, service.Status, "Cancel", service.Cancel)
                : new StatusNote("animation-job", StatusKind.Working, service.Status)];
        return jobResult is { } result ? [new StatusNote("animation-job", result.Failed ? StatusKind.Error : StatusKind.Notice, result.Text)] : [];
    }

    /// <summary>
    /// The page, <paramref name="height"/> tall. With a clip open behind it, <paramref name="backTo"/> names the clip and a
    /// Back to button above the page calls <paramref name="back"/>; null for none.
    /// </summary>
    public void Draw(float height, string? backTo, Action back)
    {
        var start = ImGui.GetCursorPosY();
        if (backTo != null)
        {
            if (ImGui.Button($"Back to {backTo}##timeline-back-to-clip"))
                back();
            ImGui.Spacing();
        }
        var record = recorder();
        var entries = Entries(drawPartners != null, record != null);
        // An entry that went away, such as Record without the recorder, gives way to the first one.
        if (!entries.Contains(Current))
            Current = StartEntry.Playing;
        var bodyHeight = Math.Max(ImGui.GetFrameHeight() * 4, height - (ImGui.GetCursorPosY() - start) - statusHeight);
        using (var sidebar = ImRaii.Child("##start-sidebar", new Vector2(Theme.Scaled(150), bodyHeight), true))
        {
            if (sidebar.Success)
                foreach (var entry in entries)
                    DrawRow(entry);
        }
        ImGui.SameLine(0, Theme.Gap);
        using (var page = ImRaii.Child("##start-page", new Vector2(0, bodyHeight), false))
        {
            if (page.Success)
                DrawPage(Current, record);
        }
        var statusStart = ImGui.GetCursorPosY();
        loader.DrawStatus(JobNotes());
        statusHeight = ImGui.GetCursorPosY() - statusStart;
    }

    /// <summary> One sidebar row: its icon and name, and on the right how many play now or how many edits didn't finish. </summary>
    private void DrawRow(StartEntry entry)
    {
        var left = ImGui.GetCursorPosX();
        if (ImGui.Selectable($"##start-entry-{entry}", Current == entry, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
            Current = entry;
        ImGui.SameLine(left + Theme.Scaled(4));
        Widgets.Icon(Icon(entry), Theme.Muted);
        ImGui.SameLine(left + Theme.Scaled(26));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Name(entry));
        var (count, colour) = entry switch
        {
            StartEntry.Playing => (playing?.Invoke() ?? 0, Theme.Muted),
            StartEntry.Saved => (session.Animations is { } service ? RecentEditsPage.RecoveryAttention(service) : 0, Theme.Warning),
            _ => (0, Theme.Muted),
        };
        if (count <= 0)
            return;
        var text = count.ToString();
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(text).X - Theme.Scaled(4)));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(colour, text);
    }

    /// <summary> The chosen entry's page. </summary>
    private void DrawPage(StartEntry entry, Action? record)
    {
        // Playing now starts with its own spacing.
        if (entry != StartEntry.Playing)
            ImGui.Spacing();
        switch (entry)
        {
            case StartEntry.Playing:
                drawPlaying?.Invoke();
                break;
            case StartEntry.Game:
                Widgets.HintWrapped("Pick an animation to play on your GPose character and edit it here.");
                ImGui.Spacing();
                loader.Draw(ImGui.GetContentRegionAvail().Y);
                break;
            case StartEntry.Saved:
                recentEdits.Draw();
                break;
            case StartEntry.Partners:
                drawPartners?.Invoke();
                break;
            case StartEntry.Record:
                record?.Invoke();
                break;
        }
    }

    /// <summary> The entry's name in the sidebar. </summary>
    private static string Name(StartEntry entry) => entry switch
    {
        StartEntry.Playing => "Playing now",
        StartEntry.Game => "Game animations",
        StartEntry.Saved => "Saved edits",
        StartEntry.Partners => "Partners",
        _ => "Record",
    };

    /// <summary> The entry's icon in the sidebar, used nowhere else in the Timeline and Bones windows. </summary>
    private static FontAwesomeIcon Icon(StartEntry entry) => entry switch
    {
        StartEntry.Playing => FontAwesomeIcon.PlayCircle,
        StartEntry.Game => FontAwesomeIcon.Gamepad,
        StartEntry.Saved => FontAwesomeIcon.History,
        StartEntry.Partners => FontAwesomeIcon.UserFriends,
        _ => FontAwesomeIcon.Circle,
    };
}
