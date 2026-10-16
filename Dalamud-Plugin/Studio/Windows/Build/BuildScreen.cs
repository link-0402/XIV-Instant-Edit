using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// Build mode: a new mod made from your own files, for gear, character features, weapons or
/// animations. Laid out
/// like Edit mode — three regions under a toolbar, above a status bar:
///   left   – the mod the modpack builds, then every item in the pack, one tile each;
///   middle – a view switcher (Overview · Set-up · Add from game · Build) and the active view;
///   right  – the inspector for whatever the view has selected.
/// Both dividers are draggable. Nothing is written into Penumbra until Build; the modpack is kept
/// in the plugin's settings, and the mod a build makes knows nothing of it. Editing that mod (or any
/// other) is Edit mode's job, opened from the library.
/// </summary>
internal sealed class BuildScreen
{
    private static readonly TimeSpan RevalidateEvery = TimeSpan.FromSeconds(2);
    private const string ConfirmResetId = "Start a new mod?##StudioConfirmReset";

    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly BuildController _builds;
    private readonly PortEditor _editor;
    private readonly PackContents _contents;
    private readonly PackOverview _overview;
    private readonly ItemSetupView _setup;
    private readonly ModelInspector _inspector;
    private readonly BrowsePanel _browse;
    private readonly BuildView _buildView;
    private readonly PackStructureView _structure;

    private int _validatedRevision = -1;
    private readonly Stopwatch _sinceValidated = new();
    private bool _penumbraAvailable;
    private bool _openReset;

    public BuildScreen(StudioHost host, TextureThumbnailCache thumbnails)
    {
        _host  = host;
        _session = host.Pack;
        _builds  = host.Builds;

        var textures = new TextureSlotEditor(_session, thumbnails);
        _editor    = new PortEditor(host, _session);
        _structure = new PackStructureView(host, _session, thumbnails);
        _contents  = new PackContents(host, _session, _editor, _structure, thumbnails);
        _overview  = new PackOverview(host, _session, _structure);
        _setup     = new ItemSetupView(host, _session, thumbnails, textures, _editor);
        _inspector = new ModelInspector(host, _session, textures, _editor);
        _browse    = new BrowsePanel(host, _session);
        _buildView = new BuildView(host, _session, _builds);
    }

    private static readonly (StageId Stage, string Label, FontAwesomeIcon Icon, string Help)[] Views =
    {
        (StageId.ModInfo, "Overview",      FontAwesomeIcon.BoxOpen, "The mod the modpack builds, and everything in it at a glance."),
        (StageId.Details, "Set-up",        FontAwesomeIcon.Sitemap, "The item picked on the left, as a tree: drop your files on its tiles."),
        (StageId.Browse,  "Add from game", FontAwesomeIcon.Tshirt,  "Find what your files replace, and add it to the modpack."),
        (StageId.Build,   "Build",         FontAwesomeIcon.Hammer,  "Build the mod into Penumbra's mod folder, and see what was written."),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Frame
    // ─────────────────────────────────────────────────────────────────────────

    public void Draw()
    {
        _penumbraAvailable = _host.PenumbraIpc.IsAvailable;
        _session.EnsureLoaded();
        Revalidate();

        DrawToolbar();

        float statusH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        float bodyH   = ImGui.GetContentRegionAvail().Y - statusH;
        DrawBody(bodyH);
        DrawStatusBar();

        _editor.DrawPopups();
        _structure.DrawPopups();
        DrawResetPopup();
        IssueList.DrawPopup(_session.Issues, _session.Focus);
    }

    /// <summary>Re-runs the validator after edits, and every few seconds for files changing on disk.</summary>
    private void Revalidate()
    {
        if (_session.Revision == _validatedRevision && _sinceValidated.IsRunning && _sinceValidated.Elapsed < RevalidateEvery)
            return;

        // Variant folders may have gained files: each new one gets its option.
        _session.SyncVariantGroups();
        _validatedRevision = _session.Revision;
        _sinceValidated.Restart();
        _session.Issues = PortValidator.Validate(_session, _host.GameData, _host.MaterialTemplates, _host.ModelFiles, _host.ImageFiles, _penumbraAvailable);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Toolbar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        using (Fonts.Title())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Build a new mod");
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Faint, "set up gear, features, weapons or animations, then build them into Penumbra");

        float buttons = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.FileImport, "Import package")
                      + Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.File, "Start over")
                      + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X * 2;
        Gui.AlignRight(buttons);
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Import package",
                "Pulls models and textures out of a Sims 4 .package or a Unity .unitypackage."))
            _host.ToggleSimsImport();

        ImGui.SameLine();
        bool empty = _session.Items.Count == 0 && string.IsNullOrWhiteSpace(_session.ModName);
        using (Gui.Disabled(empty || _builds.IsRunning))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.File, "Start over",
                    empty
                        ? "The modpack is already empty."
                        : "Starts over with an empty modpack, for a mod of its own. The mod built before stays in Penumbra as it is."))
                _openReset = true;
        }

        ImGui.SameLine();
        if (Gui.IconButton(FontAwesomeIcon.Cog, "Settings", "Settings"))
            _host.OpenSettings();

        ImGui.Spacing();
    }

    private void DrawResetPopup()
    {
        if (_openReset)
        {
            _openReset = false;
            ImGui.OpenPopup(ConfirmResetId);
        }

        Confirm.Modal(ConfirmResetId, () =>
        {
            ImGui.TextUnformatted($"Remove all {_session.Items.Count} item(s) from the modpack, with their race models and materials, and start a new mod?");
            ImGui.TextColored(Theme.Muted, "Its name and description are cleared too; the mod built from this pack stays in Penumbra as it is.");
            ImGui.TextColored(Theme.Bad, "The set-ups cannot be brought back.");
        }, "Start over", () => _session.ResetModpack(), danger: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body: modpack | view | inspector
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawBody(float height)
    {
        var cfg   = _host.Configuration;
        float total = ImGui.GetContentRegionAvail().X;
        float split = Theme.SplitterWidth;
        float minW  = Theme.S(Theme.MinPaneWidth);

        float browserW   = Math.Clamp(Theme.S(cfg.BrowserWidth),   minW, Math.Max(minW, total * 0.4f));
        float inspectorW = Math.Clamp(Theme.S(cfg.InspectorWidth), minW, Math.Max(minW, total - browserW - minW - split * 2));

        if (ImGui.BeginChild("##Pack", new Vector2(browserW, height), true))
            _contents.Draw();
        ImGui.EndChild();

        // While a divider is dragged its width is written through every frame (the next frame
        // reads it back from the config) and saved once the drag ends. Only while dragging:
        // otherwise shrinking the window would clamp and permanently overwrite the widths.
        bool released = Gui.VerticalSplitter("##SplitLeft", ref browserW, minW, total - inspectorW - minW - split * 2, height);
        if (ImGui.IsItemActive() || released)
            cfg.BrowserWidth = browserW / Theme.S(1);
        if (released)
            cfg.Save();

        float middleW = total - browserW - inspectorW - split * 2;
        ImGui.BeginGroup();
        DrawViewStrip(middleW);
        if (ImGui.BeginChild("##Canvas", new Vector2(middleW, height - ImGui.GetFrameHeightWithSpacing()), true))
        {
            switch (_session.ActiveStage)
            {
                case StageId.ModInfo: _overview.DrawCanvas();  break;
                case StageId.Browse:  _browse.DrawCanvas();    break;
                case StageId.Build:   _buildView.DrawCanvas(); break;
                default:
                    if (_structure.Shows(_session.Selection)) _structure.DrawCanvas();
                    else                                      _setup.Draw();
                    break;
            }
        }
        ImGui.EndChild();
        ImGui.EndGroup();

        released = Gui.VerticalSplitter("##SplitRight", ref inspectorW, minW, total - browserW - minW - split * 2, height, invert: true);
        if (ImGui.IsItemActive() || released)
            cfg.InspectorWidth = inspectorW / Theme.S(1);
        if (released)
            cfg.Save();

        if (ImGui.BeginChild("##Inspector", new Vector2(inspectorW, height), true))
        {
            switch (_session.ActiveStage)
            {
                case StageId.ModInfo: _overview.DrawInspector();  break;
                case StageId.Browse:  _browse.DrawInspector();    break;
                case StageId.Build:   _buildView.DrawInspector(); break;
                default:
                    if (_structure.Shows(_session.Selection)) _structure.DrawInspector();
                    else                                      _inspector.Draw();
                    break;
            }
        }
        ImGui.EndChild();
    }

    /// <summary>The view switcher above the centre, as a segmented strip so a view can carry an issue count.</summary>
    private void DrawViewStrip(float width)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float tabW = (width - spacing * (Views.Length - 1)) / Views.Length;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < Views.Length; i++)
        {
            var view = Views[i];
            bool active = _session.ActiveStage == view.Stage;
            if (i > 0) ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = active ? 0.55f : 0.25f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent with { W = 0.65f });
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(view.Icon, $"{view.Label}##view{i}", new Vector2(tabW, 0));
            ImGui.PopStyleColor(4);
            Gui.Tooltip(view.Help);
            if (clicked)
                _session.GoToStage(view.Stage);

            // Gear dragged from the browser can be dropped on any of the strip's buttons.
            if (SubjectDrop.Target() is { } dropped)
                _session.AddItem(dropped);

            if (view.Stage is StageId.Details or StageId.Build)
                DrawStageBadge(view.Stage);
        }
        ImGui.PopStyleVar();
    }

    /// <summary>A small count bubble in the top-right corner of the tab just drawn.</summary>
    private void DrawStageBadge(StageId stage)
    {
        int errors   = PortValidator.Count(_session.Issues, stage, Severity.Error);
        int warnings = PortValidator.Count(_session.Issues, stage, Severity.Warning);
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
            : "The item browser still works, but mods cannot be built until Penumbra is loaded.");

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
        var issueText  = _session.Items.Count == 0 ? "Nothing in the modpack"
                       : !_session.Items.Exists(i => i.HasWork) ? "Nothing set up yet"
                       : errors + warnings == 0 ? "Ready to build"
                       : $"{errors} error(s), {warnings} warning(s)";

        ImGui.PushStyleColor(ImGuiCol.Text, issueColor);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        bool open = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(issueIcon, issueText);
        ImGui.PopStyleColor(2);
        Gui.Tooltip("Show all problems");
        if (open)
            ImGui.OpenPopup(IssueList.PopupId);

        float buildW = Theme.S(150);

        // Last action result, cut to the room left before the build button.
        var status = _session.LiveStatus;
        if (status != null)
        {
            ImGui.SameLine(0, Theme.S(18));
            float room = ImGui.GetContentRegionMax().X - buildW - ImGui.GetCursorPosX() - Theme.S(18);
            if (room > Theme.S(40))
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Gui.SeverityColor(status.Severity), ResourceStyle.Fit(status.Text, room));
                Gui.Tooltip(status.Text);
            }
        }

        // Primary action
        Gui.AlignRight(buildW);
        var job = _builds.Running;
        if (job != null)
        {
            ImGui.ProgressBar(job.Progress.Fraction, new Vector2(buildW, 0), "Building");
            Gui.Tooltip(job.Progress.Current);
            return;
        }

        bool can = _builds.CanBuild(out var reason);
        using (Gui.Disabled(!can))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Hammer, "Build mod", buildW, can ? "Build the mod into Penumbra's mod folder." : reason))
                _builds.Start();
        }
    }
}
