using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// The right end of the status bar: whether the mod has everything set up here — up to date, about
/// to be written, being written, or waiting on a choice — and, opened, what the last update did and
/// the files changed elsewhere to decide on. There is no build to start: changes are written into
/// the mod as they are made.
/// </summary>
internal sealed class SyncStatus
{
    private const string PopupId = "##StudioSyncPopup";

    private readonly StudioHost _host;
    private readonly ModSession _session;
    private readonly ModSync _sync;
    private bool _everything;

    public SyncStatus(StudioHost host, ModSession session, ModSync sync)
    {
        _host  = host;
        _session = session;
        _sync    = sync;
    }

    /// <summary>How wide the status is drawn, for laying out what sits beside it.</summary>
    public static float Width => Theme.S(210);

    /// <summary>The status, right-aligned in the status bar; a click opens the details.</summary>
    public void Draw()
    {
        Gui.AlignRight(Width);
        bool open;
        var state = _sync.State;
        if (state == SyncState.Writing && _sync.Running is { } job)
        {
            var progress = job.Progress;
            ImGui.ProgressBar(progress.Fraction, new Vector2(Width, 0), "Updating the mod");
            open = ImGui.IsItemClicked();
            Gui.Tooltip($"{progress.Current}\n\nChanges are written into the mod as you make them.");
        }
        else
        {
            var (icon, color, text, help) = Describe(state);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            open = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, text, new Vector2(Width, 0));
            ImGui.PopStyleColor(2);
            Gui.Tooltip(help);
        }

        if (open)
            ImGui.OpenPopup(PopupId);
        DrawPopup();
    }

    private (FontAwesomeIcon Icon, Vector4 Color, string Text, string Help) Describe(SyncState state)
    {
        var report = _sync.LastReport;
        return state switch
        {
            SyncState.Waiting   => (FontAwesomeIcon.Clock, Theme.Muted, "Changes waiting",
                                    "Written into the mod as soon as you stop editing for a moment."),
            SyncState.Writing   => (FontAwesomeIcon.Sync, Theme.Muted, "Updating the mod", "Writing your changes into the mod."),
            SyncState.Conflicts => (FontAwesomeIcon.ExclamationTriangle, Theme.Warn, $"{_sync.Conflicts.Count} changed elsewhere",
                                    "Files you changed here were also changed elsewhere (in Penumbra, say) since they were written. " +
                                    "Nothing was written over them — click to choose what to keep."),
            SyncState.Failed    => (FontAwesomeIcon.TimesCircle, Theme.Bad, "Update failed",
                                    $"{report?.Summary()}\n\nClick for details."),
            SyncState.UpToDate when report is { Failed: > 0 } r
                                => (FontAwesomeIcon.ExclamationTriangle, Theme.Warn, $"Up to date · {r.Failed} failed",
                                    "Everything else set up here is in the mod. Click to see what could not be written."),
            SyncState.UpToDate  => (FontAwesomeIcon.CheckCircle, Theme.Good, "Mod up to date",
                                    "Everything set up here is in the mod. Changes are written as you make them, " +
                                    "and changes made to it elsewhere are kept."),
            _                   => (FontAwesomeIcon.Hourglass, Theme.Faint, "Reading the mod", "Reading the mod."),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Details
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawPopup()
    {
        ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.S(460), 0), new Vector2(Theme.S(760), Theme.S(620)));
        if (!ImGui.BeginPopup(PopupId))
            return;

        var (icon, color, text, _) = Describe(_sync.State);
        Gui.Icon(icon, color);
        ImGui.SameLine();
        ImGui.TextColored(color, _sync.State == SyncState.Writing && _sync.Running is { } job ? $"Updating the mod — {job.Progress.Current}" : text);
        ImGui.Separator();

        DrawConflicts();
        DrawLastUpdate();

        ImGui.Spacing();
        ImGui.Separator();
        if (Gui.IconTextButton(FontAwesomeIcon.Redo, "Redraw everyone"))
            _host.PenumbraIpc.RedrawAll();
        Gui.Tooltip("Characters only show changed files once they are redrawn.");
        ImGui.SameLine();
        var project = _session.Project;
        using (Gui.Disabled(project == null || !_host.PenumbraIpc.IsAvailable))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.SyncAlt, "Reload in Penumbra") && project != null)
                _host.PenumbraIpc.ReloadMod(project.FolderName);
        }
        Gui.Tooltip("Penumbra reads the mod from disk again. It does so by itself after every change written here.");

        var cfg = _host.Configuration;
        bool redraw = cfg.RedrawAfterBuild;
        if (ImGui.Checkbox("Redraw everyone after each change is written", ref redraw))
        {
            cfg.RedrawAfterBuild = redraw;
            cfg.Save();
        }
        ImGui.EndPopup();
    }

    private void DrawConflicts()
    {
        var conflicts = _sync.Conflicts.ToList();
        if (conflicts.Count == 0)
            return;

        ImGui.Spacing();
        Gui.SectionHeader($"Changed elsewhere ({conflicts.Count})",
            "These files were changed outside XIV Instant Edit after it wrote them, and what is set up for them here changed too. " +
            "Nothing was written over them; choose which to keep.");
        Gui.HintWrapped("Keep theirs leaves the file as it was changed elsewhere until you change its set-up here again. " +
                       "Use mine writes what is set up here over it.");

        float buttons = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Check, "Use mine")
                      + Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Undo, "Keep theirs")
                      + ImGui.GetStyle().ItemSpacing.X;
        float height = Math.Min(conflicts.Count, 6) * (ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing()) + Theme.S(4);
        if (ImGui.BeginChild("##conflicts", new Vector2(Math.Max(Theme.S(440), ImGui.GetContentRegionAvail().X), height), false))
        {
            for (int i = 0; i < conflicts.Count; i++)
            {
                var conflict = conflicts[i];
                ImGui.PushID(i);
                ImGui.AlignTextToFramePadding();
                if (ImGui.Selectable($"{conflict.Label}##go", false, ImGuiSelectableFlags.None,
                        new Vector2(ImGui.GetContentRegionAvail().X - buttons - ImGui.GetStyle().ItemSpacing.X, 0)))
                    Go(conflict.Source, conflict.GamePath, conflict.Option.Serialize());
                Gui.Tooltip($"{conflict.GamePath}\n{conflict.RelPath}\n\nClick to go to it.");
                ImGui.SameLine();
                if (Gui.IconTextButton(FontAwesomeIcon.Check, "Use mine"))
                    _sync.Resolve(conflict, ConflictChoice.UseMine);
                Gui.Tooltip("Write what is set up here over the change made elsewhere.");
                ImGui.SameLine();
                if (Gui.IconTextButton(FontAwesomeIcon.Undo, "Keep theirs"))
                    _sync.Resolve(conflict, ConflictChoice.KeepTheirs);
                Gui.Tooltip("Keep the file as it was changed elsewhere; the set-up here leaves it alone until you change it again.");
                ImGui.TextColored(Theme.Muted, ResourceStyle.Fit(conflict.Reason, ImGui.GetContentRegionAvail().X));
                Gui.Tooltip(conflict.Reason);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (conflicts.Count > 1)
        {
            if (Gui.IconTextButton(FontAwesomeIcon.CheckDouble, "Use mine for all"))
                _sync.ResolveAll(ConflictChoice.UseMine);
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Undo, "Keep all of theirs"))
                _sync.ResolveAll(ConflictChoice.KeepTheirs);
        }
    }

    private void DrawLastUpdate()
    {
        var report = _sync.LastReport;
        ImGui.Spacing();
        Gui.SectionHeader("Last update");
        if (report == null)
        {
            Gui.Hint("Nothing written since the mod was opened.");
            return;
        }

        var ago = DateTime.UtcNow - report.FinishedAt;
        var when = ago.TotalSeconds < 60 ? "just now" : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago" : report.FinishedAt.ToLocalTime().ToString("t");
        ImGui.PushStyleColor(ImGuiCol.Text, report.FatalError != null || report.Failed > 0 ? Theme.Bad
                                           : report.Conflicts.Count > 0 || report.Skipped > 0 ? Theme.Warn
                                           : Theme.Muted);
        ImGui.TextWrapped($"{when}: {report.Summary()}");
        ImGui.PopStyleColor();
        if (report.Unchanged > 0)
            ImGui.TextColored(Theme.Faint, $"{report.Unchanged} file(s) were already in the mod as set up, and left as they are.");
        if (report.ReloadResult is { } reload && reload is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            ImGui.TextColored(Theme.Warn, $"Penumbra did not read the mod again ({reload}) — reload it in Penumbra.");

        if (report.FatalError != null && Gui.IconTextButton(FontAwesomeIcon.Redo, "Try again"))
            _sync.Retry();

        var entries = report.Entries.Where(e => _everything || e.Outcome != WriteOutcome.Ok).ToList();
        ImGui.Checkbox("Show every file written", ref _everything);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.Clipboard, "Copy report"))
        {
            ImGui.SetClipboardText(report.ToClipboardText());
            _session.Notify("Report of the last update copied to the clipboard.", Severity.Info);
        }

        if (entries.Count == 0)
        {
            Gui.Hint(_everything ? "Nothing was written." : "Nothing was skipped or failed.");
            return;
        }

        float rowH = ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##entries", new Vector2(Math.Max(Theme.S(440), ImGui.GetContentRegionAvail().X), Math.Min(entries.Count, 12) * rowH + Theme.S(4)), false))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var (glyph, tint) = e.Outcome switch
                {
                    WriteOutcome.Ok      => (FontAwesomeIcon.Check, Theme.Good),
                    WriteOutcome.Merged  => (FontAwesomeIcon.CodeBranch, Theme.Good),
                    WriteOutcome.Kept    => (FontAwesomeIcon.ExclamationTriangle, Theme.Warn),
                    WriteOutcome.Skipped => (FontAwesomeIcon.Minus, Theme.Warn),
                    _                    => (FontAwesomeIcon.Times, Theme.Bad),
                };
                ImGui.PushID(i);
                Gui.Icon(glyph, tint);
                ImGui.SameLine();
                var line = string.IsNullOrEmpty(e.Detail) ? e.Label : $"{e.Label} — {e.Detail}";
                if (ImGui.Selectable(ResourceStyle.Fit(line, ImGui.GetContentRegionAvail().X), false))
                    Go(e.Source, e.GamePath, string.Empty);
                Gui.Tooltip($"{e.GamePath ?? e.Label}\n{e.Detail}");
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    /// <summary>Goes to the file a line is about.</summary>
    private void Go(Target? source, string? gamePath, string option)
    {
        if (source is { } target)
            _session.Focus(target);
        else if (!string.IsNullOrEmpty(gamePath))
            _session.Focus(Target.ForResource(string.Empty, gamePath, option));
        ImGui.CloseCurrentPopup();
    }
}
