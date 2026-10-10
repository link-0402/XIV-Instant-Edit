using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.Studio;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows;

/// <summary>
/// One mod, whatever made it, in a window of its own: three regions under a toolbar and above a
/// status bar. Its contents on the left, one tile per top-level resource; the overview or the
/// picked entry's tree in the middle; the inspector for whatever is selected on the right.
/// Every change is written into the mod as it is made (see <see cref="ModSync"/>); the status bar
/// says how far that has got. The window only lays out regions and runs the per-frame ticks
/// (validation, remembering the selection); the views draw themselves.
/// </summary>
internal sealed class ModWindow : Window
{
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromSeconds(2);
    private const string MoreMenuId = "##StudioMoreMenu";

    private readonly StudioHost _host;
    private readonly ModSession _session;
    private readonly ModSync _sync;
    private readonly string _windowId;

    private readonly ResourceActions _actions;
    private readonly HandoffActions _handoffs;
    private readonly ContentsPane _contents;
    private readonly ResourceTreeView _tree;
    private readonly OverviewView _overview;
    private readonly ModBrowseView _browse;
    private readonly SyncStatus _status;
    private readonly InspectorRouter _inspector;
    private readonly CheckView _check;

    private int _validatedRevision = -1;
    private readonly Stopwatch _sinceValidated = new();
    private bool _penumbraAvailable;
    private bool _restored;

    private static readonly (StudioView View, string Label, FontAwesomeIcon Icon, string Help)[] Views =
    {
        (StudioView.Overview,  "Overview",  FontAwesomeIcon.BoxOpen, "The mod, and everything in it at a glance."),
        (StudioView.Resources, "Resources", FontAwesomeIcon.Sitemap, "The entry picked on the left, as a tree: drop files on any tile to replace it."),
        (StudioView.Browse,    "Add from game", FontAwesomeIcon.Tshirt, "Add gear or a character feature from the game to the mod, to put files of your own on."),
        (StudioView.Check,     "Check",     FontAwesomeIcon.Stethoscope, "Check why items don't show up, and clean up the mod's files."),
    };

    public ModEditor Editor { get; }

    internal ModWindow(StudioHost host, ModEditor editor, TextureThumbnailCache thumbnails, Vector2 position)
        : base($"{editor.Name}###ie-mod:{editor.Project.FolderPath}", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _host    = host;
        Editor   = editor;
        _session = editor.Session;
        _sync    = editor.Sync;
        _windowId = editor.Project.FolderPath;

        Size              = new Vector2(1280, 760);
        SizeCondition     = ImGuiCond.FirstUseEver;
        Position          = position;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints   = new WindowSizeConstraints { MinimumSize = new Vector2(900, 500) };
        IsOpen            = true;

        var textures = new TextureSlotEditor(_session, thumbnails);
        _actions   = new ResourceActions(host, _session);
        _handoffs  = new HandoffActions(editor, $"mod:{_windowId}");
        _contents  = new ContentsPane(editor, _session, thumbnails, _actions);
        _tree      = new ResourceTreeView(editor, _session, thumbnails, textures, _actions, _handoffs);
        _overview  = new OverviewView(editor, _session, thumbnails, _actions);
        _browse    = new ModBrowseView(host, _session);
        _status    = new SyncStatus(host, _session, _sync);
        _check     = new CheckView(host, editor, _session);
        _inspector = new InspectorRouter(_session,
            new ResourceInspector(editor, _session, thumbnails, _actions, textures, _handoffs),
            new ProjectInspector(editor, _session),
            new OptionInspector(editor, _session, _actions),
            _actions);
    }

    public override void OnClose() => Editor.Flush();

    // ─────────────────────────────────────────────────────────────────────────
    // Frame
    // ─────────────────────────────────────────────────────────────────────────

    public override void PreDraw()
        => WindowName = $"{Editor.Name}###ie-mod:{_windowId}";

    public override void Draw()
    {
        if (_session.Project is not { } project)
            return;

        Theme.TileScale = Math.Clamp(_host.Configuration.TileScale, 0.75f, 1.6f);
        _penumbraAvailable = _host.PenumbraIpc.IsAvailable;
        FileDrop.BeginExternalSource();

        Revalidate(project);
        TrackSelection(project);

        DrawToolbar(project);

        float statusH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        float bodyH   = ImGui.GetContentRegionAvail().Y - statusH;
        DrawBody(bodyH);
        DrawStatusBar();

        _actions.DrawPopups();
        _handoffs.DrawDialogs();
        IssueList.DrawPopup(_session.Issues, _session.Focus);
    }

    /// <summary>Re-runs the validator after edits, and every few seconds for files changing on disk.</summary>
    private void Revalidate(ModProject project)
    {
        if (_session.Revision == _validatedRevision && _sinceValidated.IsRunning && _sinceValidated.Elapsed < RevalidateEvery)
            return;

        // meta.json may have been changed in Penumbra meanwhile; the graph follows when it is read again.
        if (_sinceValidated.Elapsed >= RevalidateEvery)
            project.RefreshFromDisk();

        _validatedRevision = _session.Revision;
        _sinceValidated.Restart();
        var issues = ReplacementValidator.Validate(_session, _host.ImageFiles);
        if (!_penumbraAvailable)
            issues.Add(new PortIssue(Severity.Warning, new Target(TargetKind.Project),
                "Penumbra is not available. Changes are still written into the mod, and Penumbra reads them once it is back."));
        foreach (var conflict in _sync.Conflicts)
            issues.Add(new PortIssue(Severity.Warning, conflict.Source ?? Target.ForResource(string.Empty, conflict.GamePath, conflict.Option.Serialize()),
                $"{conflict.Label}: {conflict.Reason}."));
        issues.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        _session.Issues = issues;
    }

    /// <summary>
    /// Opening a mod shows what was picked when it was left (once its resources are read); after
    /// that, what is picked is remembered in its sidecar, if it has one.
    /// </summary>
    private void TrackSelection(ModProject project)
    {
        var graph = _session.Graph;
        if (!_restored)
        {
            if (ReferenceEquals(graph, Models.Resources.ResourceGraph.Empty))
                return;
            _restored = true;

            var saved = project.Sidecar.Ui.SelectedRoot;
            if (string.IsNullOrEmpty(saved) || saved == "project")
                return;
            if (graph.FindRoot(saved) is { } root)
                _session.Focus(root.Target);
            else if (Models.Resources.ResourceGraph.GroupOf(saved) is { } group && project.Meta.FindGroupView(group) != null)
                _session.FocusGroup(group);
            return;
        }
        _session.SettleSelection();

        var current = _session.Selection switch
        {
            { Kind: TargetKind.Project }    => "project",
            { Kind: TargetKind.Resource } s => s.Root,
            _                               => null,
        };
        if (!string.IsNullOrEmpty(current) && current != project.Sidecar.Ui.SelectedRoot)
        {
            project.Sidecar.Ui.SelectedRoot = current;
            project.MarkUiStateDirty();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Toolbar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawToolbar(ModProject project)
    {
        using (Fonts.Title())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Editor.Name);
        }
        if (ImGui.IsItemClicked())
            _session.Focus(new Target(TargetKind.Project));
        Gui.Tooltip("Click to see the mod's details.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Faint, project.FolderName);

        float buttons = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.FileImport, "Add files")
                      + ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X * 2;
        Gui.AlignRight(buttons);
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Add files", "Add files of your own to the mod (textures, models, sounds, effects)."))
            _actions.BrowseAdd(null);
        ImGui.SameLine();
        if (Gui.IconButton(FontAwesomeIcon.EllipsisV, "more", "More"))
            ImGui.OpenPopup(MoreMenuId);
        ImGui.SameLine();
        if (Gui.IconButton(FontAwesomeIcon.Cog, "Settings", "Settings"))
            _host.OpenSettings();

        if (ImGui.BeginPopup(MoreMenuId))
        {
            if (ImGui.MenuItem("Open the mod's folder"))
                OpenFolder(project.FolderPath);
            if (ImGui.MenuItem("Reload it in Penumbra", "", false, _penumbraAvailable))
                _host.PenumbraIpc.ReloadMod(project.FolderName);
            Gui.Tooltip("Penumbra reads the mod from disk again. It does so by itself after every change written here.");
            bool canCopy = Editor.CanDuplicate(out var copyReason);
            if (ImGui.MenuItem("Duplicate the mod", "", false, canCopy))
                Editor.Duplicate();
            Gui.Tooltip(canCopy
                ? "Copies the mod into a new folder beside it (\"Name (copy)\"), adds it to Penumbra and opens the copy. The mod itself stays as it is."
                : copyReason);
            ImGui.Separator();
            if (ImGui.MenuItem("Close the window"))
                IsOpen = false;
            ImGui.EndPopup();
        }

        ImGui.Spacing();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body: contents | view | inspector
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawBody(float height)
    {
        var cfg   = _host.Configuration;
        float total = ImGui.GetContentRegionAvail().X;
        float split = Theme.SplitterWidth;
        float minW  = Theme.S(Theme.MinPaneWidth);

        float contentsW  = Math.Clamp(Theme.S(cfg.ContentsWidth),  minW, Math.Max(minW, total * 0.4f));
        float inspectorW = Math.Clamp(Theme.S(cfg.InspectorWidth), minW, Math.Max(minW, total - contentsW - minW - split * 2));

        if (ImGui.BeginChild("##Contents", new Vector2(contentsW, height), true))
            _contents.Draw();
        ImGui.EndChild();

        // While a divider is dragged its width is written through every frame (the next frame
        // reads it back from the config) and saved once the drag ends. Only while dragging:
        // otherwise shrinking the window would clamp and permanently overwrite the widths.
        bool released = Gui.VerticalSplitter("##SplitLeft", ref contentsW, minW, total - inspectorW - minW - split * 2, height);
        if (ImGui.IsItemActive() || released)
            cfg.ContentsWidth = contentsW / Theme.S(1);
        if (released)
            cfg.Save();

        float middleW = total - contentsW - inspectorW - split * 2;
        ImGui.BeginGroup();
        DrawViewStrip(middleW);
        if (ImGui.BeginChild("##Canvas", new Vector2(middleW, height - ImGui.GetFrameHeightWithSpacing()), true))
        {
            switch (_session.ActiveView)
            {
                case StudioView.Overview: _overview.Draw();      break;
                case StudioView.Browse:   _browse.DrawCanvas();  break;
                case StudioView.Check:    _check.DrawCanvas();   break;
                default:                  _tree.Draw();          break;
            }
        }
        ImGui.EndChild();
        ImGui.EndGroup();

        released = Gui.VerticalSplitter("##SplitRight", ref inspectorW, minW, total - contentsW - minW - split * 2, height, invert: true);
        if (ImGui.IsItemActive() || released)
            cfg.InspectorWidth = inspectorW / Theme.S(1);
        if (released)
            cfg.Save();

        if (ImGui.BeginChild("##Inspector", new Vector2(inspectorW, height), true))
        {
            if (_session.ActiveView == StudioView.Browse)
                _browse.DrawInspector();
            else if (_session.ActiveView == StudioView.Check)
                _check.DrawInspector();
            else
                _inspector.Draw();
        }
        ImGui.EndChild();
    }

    /// <summary>The view switcher above the centre, as a segmented strip so the tree can carry an issue count.</summary>
    private void DrawViewStrip(float width)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float tabW = (width - spacing * (Views.Length - 1)) / Views.Length;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < Views.Length; i++)
        {
            var view = Views[i];
            bool active = _session.ActiveView == view.View;
            if (i > 0) ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = active ? 0.55f : 0.25f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent with { W = 0.65f });
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(view.Icon, $"{view.Label}##view{i}", new Vector2(tabW, 0));
            ImGui.PopStyleColor(4);
            Gui.Tooltip(view.Help);
            if (clicked)
                _session.GoToView(view.View);

            // Gear dragged from the browser can be dropped on any of the strip's buttons.
            if (SubjectDrop.Target() is { } dropped)
                _session.AddItem(dropped, _session.AddInto);

            if (view.View == StudioView.Resources)
                DrawViewBadge(_session.Issues.Where(i => i.Target.Kind == TargetKind.Resource).Select(i => i.Severity));
            else if (view.View == StudioView.Check && Editor.Check is { Report: { } report, IsStale: false })
                DrawViewBadge(report.Findings.Select(f => f.Severity));
        }
        ImGui.PopStyleVar();
    }

    /// <summary>A small count bubble in the top-right corner of the button just drawn: its errors, else its warnings.</summary>
    private static void DrawViewBadge(IEnumerable<Severity> severities)
    {
        int errors = 0, warnings = 0;
        foreach (var severity in severities)
        {
            if (severity == Severity.Error) errors++;
            else if (severity == Severity.Warning) warnings++;
        }
        if (errors + warnings == 0)
            return;

        var color = errors > 0 ? Theme.Bad : Theme.Warn;
        var text  = (errors > 0 ? errors : warnings).ToString();
        var max   = ImGui.GetItemRectMax();
        var min   = ImGui.GetItemRectMin();
        var size  = ImGui.CalcTextSize(text);
        float r   = MathF.Max(size.X, size.Y) * 0.5f + Theme.S(3);
        var center = new Vector2(max.X - r - Theme.S(4), (min.Y + max.Y) * 0.5f);

        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, r, ImGui.GetColorU32(color));
        draw.AddText(center - size * 0.5f, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1f)), text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status bar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawStatusBar()
    {
        ImGui.Separator();

        // Penumbra
        Gui.Icon(FontAwesomeIcon.Circle, _penumbraAvailable ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(_penumbraAvailable ? Theme.Muted : Theme.Bad, _penumbraAvailable ? "Penumbra" : "Penumbra unavailable");
        Gui.Tooltip(_penumbraAvailable
            ? $"Connected. {(_host.GameData.IsItemCacheReady ? $"{_host.GameData.CacheCount} items cached." : "")}"
            : "Changes are still written into the mod; Penumbra reads them once it is back.");

        // Issues
        ImGui.SameLine(0, Theme.S(18));
        int errors = 0, warnings = 0;
        foreach (var issue in _session.Issues)
        {
            if (issue.Severity == Severity.Error) errors++;
            else if (issue.Severity == Severity.Warning) warnings++;
        }

        var issueColor = errors > 0 ? Theme.Bad : warnings > 0 ? Theme.Warn : Theme.Good;
        var issueIcon  = errors > 0 ? FontAwesomeIcon.TimesCircle : warnings > 0 ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.CheckCircle;
        var issueText  = errors + warnings == 0 ? "No problems" : $"{errors} error(s), {warnings} warning(s)";

        ImGui.PushStyleColor(ImGuiCol.Text, issueColor);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        bool open = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(issueIcon, issueText);
        ImGui.PopStyleColor(2);
        Gui.Tooltip("Show all problems");
        if (open)
            ImGui.OpenPopup(IssueList.PopupId);

        if (Editor.Graphs.IsBuilding)
        {
            ImGui.SameLine(0, Theme.S(18));
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Faint);
            Gui.Tooltip("Reading the mod's files");
        }

        // Last action result, cut to the room left before the mod's status: what a change here did,
        // or what Instant Edit reported for the last hand-off started from this window.
        var status = _session.LiveStatus;
        var handoff = _handoffs.Message;
        (string Text, Vector4 Color)? shown = status != null ? (status.Text, Gui.SeverityColor(status.Severity))
            : handoff is { } h ? (h.Text, HandoffColor(h.Severity))
            : null;
        if (shown is { } line)
        {
            ImGui.SameLine(0, Theme.S(18));
            float room = ImGui.GetContentRegionMax().X - SyncStatus.Width - ImGui.GetCursorPosX() - Theme.S(24);
            if (room > Theme.S(40))
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(line.Color, ResourceStyle.Fit(line.Text, room));
                Gui.Tooltip(line.Text);
            }
        }

        // Where the mod stands against what is set up here.
        ImGui.SameLine();
        _status.Draw();
    }

    private static Vector4 HandoffColor(InstantEdit.Ui.FeedbackSeverity severity) => severity switch
    {
        InstantEdit.Ui.FeedbackSeverity.Error   => Theme.Bad,
        InstantEdit.Ui.FeedbackSeverity.Warning => Theme.Warn,
        InstantEdit.Ui.FeedbackSeverity.Success => Theme.Good,
        _                                       => Theme.Muted,
    };

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _session.Notify($"Could not open the folder: {ex.Message}", Severity.Error);
        }
    }
}
