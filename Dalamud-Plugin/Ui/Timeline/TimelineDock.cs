using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The dock at the Timeline window's left: the tabs (Animations, Saved edits, Blender) with the arrow that folds it, the
/// actors, and the chosen tab's page. Folded it is a strip with the arrow and the actors alone. Its tab, width and fold
/// are the window's (<see cref="WindowLayout"/>), so every actor's workspace shows the same dock. It also keeps how an
/// animation job ended that neither the save row nor a preview build started (an undo in Saved edits, a wind-up, a send to Blender),
/// for the status line.
/// </summary>
/// <param name="recorder">The live pose recorder's page; null until the main window attaches it.</param>
/// <param name="reportsElsewhere">Whether the job running is the save row's or a preview build, which report on their own.</param>
internal sealed class TimelineDock(TimelineSession session, WindowLayout layout, ITimelineHost host, TimelineLoader loader,
    RecentEditsPage recentEdits, BlenderMotionPanel blenderMotion, Func<Action?> recorder, Func<bool> reportsElsewhere)
{
    /// <summary> The tabs in their order. </summary>
    public static readonly ImmutableArray<DockTab> Tabs = [DockTab.Animations, DockTab.Saved, DockTab.Blender];

    private Action? drawPlaying;
    private Func<int>? playing;
    // An animation job runs (a send to Blender, an undo, a wind-up), and whether the save row or a preview build started it.
    private bool jobRunning;
    private bool jobFromFooter;
    // How the last job this dock reports ended, for the status line; null until one ends, and while the next one runs.
    private (string Text, bool Failed)? jobResult;

    /// <summary> The main window's Now playing list and how many animations play now, for the Animations tab. </summary>
    public void AttachPlayingNow(Action draw, Func<int> count)
    {
        drawPlaying = draw;
        playing = count;
    }

    /// <summary> Every frame: keeps how the animation service's job ended, unless the save row or a preview build ran it. </summary>
    public void Poll()
    {
        if (session.Animations is not { } service)
            return;
        if (service.Busy)
        {
            if (!jobRunning)
                jobFromFooter = reportsElsewhere();
            jobRunning = true;
            jobResult = null;
        }
        else if (jobRunning)
        {
            jobRunning = false;
            if (!jobFromFooter)
                jobResult = (service.Status, service.LastFailed);
        }
    }

    /// <summary> The status line's note on the animation job: running with Cancel when it can stop, else how it ended. </summary>
    public IEnumerable<StatusNote> JobNotes()
    {
        if (session.Animations is not { } service)
            return [];
        if (service.Busy)
            return jobFromFooter ? []
                : service.CanCancel ? [new StatusNote("animation-job", StatusKind.Working, service.Status, "Cancel", service.Cancel)]
                : [new StatusNote("animation-job", StatusKind.Working, service.Status)];
        return jobResult is { } result ? [new StatusNote("animation-job", result.Failed ? StatusKind.Error : StatusKind.Notice, result.Text)] : [];
    }

    /// <summary>
    /// The dock: open, the tabs, the actors and the tab's page; folded, the arrow and the actors. A partner still spawning
    /// (<paramref name="waiting"/>) greys the page out until its actor is there.
    /// </summary>
    public void Draw(string? waiting)
    {
        if (layout.DockFolded)
        {
            if (Widgets.GhostIconButton("##dock-unfold", FontAwesomeIcon.AngleDoubleRight, "Show the dock"))
                layout.DockFolded = false;
            host.DrawActors(true);
            return;
        }
        DrawTabs();
        ImGui.Spacing();
        host.DrawActors(false);
        ImGui.Spacing();
        if (waiting != null)
            Widgets.HintWrapped(waiting);
        using var page = ImRaii.Child("##dock-page", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y)), false);
        using (ImRaii.Disabled(waiting != null))
        {
            if (page.Success)
                DrawPage(layout.Tab);
        }
    }

    /// <summary> The tabs, one row each with its count, and the arrow that folds the dock beside them. </summary>
    private void DrawTabs()
    {
        var fold = ImGui.GetFrameHeight();
        var style = ImGui.GetStyle();
        var height = Tabs.Length * ImGui.GetFrameHeight() + (Tabs.Length - 1) * style.ItemSpacing.Y + style.WindowPadding.Y * 2;
        using (var tabs = ImRaii.Child("##dock-tabs", new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X - fold - style.ItemSpacing.X), height), true,
                   ImGuiWindowFlags.NoScrollbar))
        {
            if (tabs.Success)
                foreach (var tab in Tabs)
                    DrawTab(tab);
        }
        ImGui.SameLine();
        if (Widgets.GhostIconButton("##dock-fold", FontAwesomeIcon.AngleDoubleLeft, "Hide the dock"))
            layout.DockFolded = true;
    }

    /// <summary> One tab: its icon and name, and on the right how many play now or how many edits didn't finish. </summary>
    private void DrawTab(DockTab tab)
    {
        var left = ImGui.GetCursorPosX();
        if (ImGui.Selectable($"##dock-tab-{tab}", layout.Tab == tab, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
            layout.Tab = tab;
        ImGui.SameLine(left + Theme.Scaled(4));
        Widgets.Icon(Icon(tab), Theme.Muted);
        ImGui.SameLine(left + Theme.Scaled(26));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Name(tab));
        var (count, colour) = tab switch
        {
            DockTab.Animations => (playing?.Invoke() ?? 0, Theme.Muted),
            DockTab.Saved => (session.Animations is { } service ? RecentEditsPage.RecoveryAttention(service) : 0, Theme.Warning),
            _ => (0, Theme.Muted),
        };
        if (count <= 0)
            return;
        var text = count.ToString();
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(text).X - Theme.Scaled(4)));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(colour, text);
    }

    /// <summary> The tab's page. </summary>
    private void DrawPage(DockTab tab)
    {
        switch (tab)
        {
            case DockTab.Animations:
                Widgets.SectionHeader("Now playing");
                drawPlaying?.Invoke();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                loader.Draw(ImGui.GetContentRegionAvail().Y);
                break;
            case DockTab.Saved:
                recentEdits.Draw();
                break;
            case DockTab.Blender:
                Widgets.SectionHeader("Motion");
                blenderMotion.DrawButtons(session.Editor);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                Widgets.SectionHeader("Record");
                if (recorder() is { } record)
                    record();
                else
                    Widgets.HintWrapped("Recording isn't available.");
                break;
        }
    }

    /// <summary> The tab's name. </summary>
    private static string Name(DockTab tab) => tab switch
    {
        DockTab.Animations => "Animations",
        DockTab.Saved => "Saved edits",
        _ => "Blender",
    };

    /// <summary> The tab's icon, used nowhere else in the Timeline and Bones windows. </summary>
    private static FontAwesomeIcon Icon(DockTab tab) => tab switch
    {
        DockTab.Animations => FontAwesomeIcon.PlayCircle,
        DockTab.Saved => FontAwesomeIcon.History,
        _ => FontAwesomeIcon.ExchangeAlt,
    };
}
