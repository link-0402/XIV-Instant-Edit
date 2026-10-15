using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;
using InstantEdit.Studio;
using InstantEdit.Ui.Main;

namespace InstantEdit.Ui;

/// <summary>Compact resource browser for the authoritative Penumbra resource snapshot.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const string WindowOptionsPopupName = "WindowSystemContextActions";
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";
    private const int AutoRefreshDelayMs = 1000;
    private readonly Configuration _config; private readonly PenumbraService _penumbra; private readonly OnScreenService _onScreen;
    private readonly BlenderClient _blender; private readonly IDataManager _data; private readonly IChatGui _chat; private readonly IPluginLog _log;
    private readonly UiNotifications _notifications;
    private readonly OptionsPopup _options;
    private readonly StatusStrip _statusStrip;
    private readonly BlenderConnection _blenderConnection;
    private readonly ModelSender _models;
    private readonly PainterPanel _painter;
    private readonly TextureSessionsTab _textureTab;
    private readonly ResourceHoverCards _hoverCards;
    private readonly AnimationIntegration _animationHub = new();
    private readonly AnimationDelivery _animationDelivery;
    private readonly RecorderPanel _recorder;
    private readonly AnimationSendDialog _animationSend;
    private readonly ModBrowserTab _modBrowser;
    private readonly MainModHandoff _handoff;
    private readonly ResourceRows _rows;
    private readonly SlotIcons _slotIcons;
    private readonly ExpansionState _expansion = new();
    private readonly OnScreenTab _onScreenTab;
    private readonly GameFileExportDialog _gameExportDialog;
    private readonly GameFilesTab _gameFilesTab;
    private readonly PreviewService _previews;
    private readonly string _pluginVersion;
    private readonly ResourceSourceAttributor _resourceSources;
    private readonly Action _saveConfig;
    private readonly Action _openChangelog;
    private readonly Action _openSettings;
    private readonly IUiBuilder _uiBuilder;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private MainTab _activeTab = MainTab.OnScreen;
    private MainTab? _pendingTab;
    private long _resourcesChangedTicks;
    private const bool ShowAnimationsTab = true;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen,
        ResourceSourceAttributor resourceSources, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider, TextureEditService textures, INotificationManager notifications,
        PreviewService previews, Action openChangelog, Action openSettings)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log;
        _notifications = new UiNotifications(config, notifications, log);
        _options = new OptionsPopup(config, saveConfig);
        _statusStrip = new StatusStrip(_feed);
        _blenderConnection = new BlenderConnection(blender, config, textures, log);
        _previews = previews;
        _pluginVersion = _blenderConnection.PluginVersion;
        _resourceSources = resourceSources;
        _models = new ModelSender(config, penumbra, blender, _blenderConnection, data, chat, log, resourceSources, _feed, _lifetimeCts.Token);
        _painter = new PainterPanel(config, log, textures, _models, _feed, _blenderConnection.PluginVersion, _lifetimeCts.Token);
        _textureTab = new TextureSessionsTab(config, textures, previews, _painter, _feed, log, _lifetimeCts.Token);
        _hoverCards = new ResourceHoverCards(previews);
        _animationDelivery = new AnimationDelivery(config, blender, _blenderConnection);
        _recorder = new RecorderPanel(config, saveConfig, log, _feed, _animationDelivery, _lifetimeCts.Token);
        _animationSend = new AnimationSendDialog(_animationHub, _animationDelivery, _feed, _lifetimeCts.Token);
        _modBrowser = new ModBrowserTab(penumbra, _feed);
        _handoff = new MainModHandoff(_models, _textureTab, _animationSend, _feed);
        _rows = new ResourceRows(_models, _painter, _textureTab, _animationSend, _modBrowser, penumbra, log, _feed);
        _slotIcons = new SlotIcons(uiBuilder, textureProvider, data, log);
        _onScreenTab = new OnScreenTab(config, saveConfig, onScreen, _rows, _hoverCards, _textureTab, _slotIcons, _expansion);
        _gameExportDialog = new GameFileExportDialog(config, saveConfig, penumbra, log, _feed, _rows);
        _gameFilesTab = new GameFilesTab(onScreen, _rows, _hoverCards, _gameExportDialog);
        _saveConfig = saveConfig;
        _openChangelog = openChangelog;
        _openSettings = openSettings;
        _uiBuilder = uiBuilder;
        _feed.Reported += _notifications.Notify;
        _penumbra.ResourcesChanged += OnResourcesChanged;
        Size = new Vector2(880, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(660, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        AllowPinning = true;
        AllowClickthrough = true;
        AllowBackgroundBlur = true;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            Click = _ => OpenKofiPage(),
            ShowTooltip = () => ImGui.SetTooltip("♥ Support me on Ko-fi"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.BookBookmark,
            Click = _ => _openChangelog(),
            ShowTooltip = () => ImGui.SetTooltip("View changelog"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Click = _ => _openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    public void Dispose()
    {
        _penumbra.ResourcesChanged -= OnResourcesChanged;
        _feed.Reported -= _notifications.Notify;
        if (_compression is not null)
            _compression.Optimized -= NotifyOptimized;
        _painter.Detach();
        _gameExportDialog.Detach();
        _lifetimeCts.Cancel();
        _slotIcons.Dispose();
        _lifetimeCts.Dispose();
    }

    public void RequestCacheSynchronization() => _blenderConnection.RequestCacheSynchronization();

    public void ReportImportFailure(BridgeFailure failure) => _models.ReportImportFailure(failure);

    internal void AttachPainter(PainterJobService painter) => _painter.Attach(painter);

    internal void AttachRecorder(AnimationRecorder? value) => _recorder.Recorder = value;

    /// <summary>The recorder, drawn in the Timeline window's Record page; it sends to Blender through this window.</summary>
    internal void DrawRecorderPanel() => _recorder.Draw();

    /// <summary> The Mod Browser: Penumbra's mods as tiles, a window per open mod, and the modpack builder. </summary>
    internal void AttachStudio(StudioHost studio) => _modBrowser.Attach(studio, _handoff);

    internal void AttachGameFiles(GameFileBrowserService browser, GameFileExportService export)
    {
        _gameFilesTab.Attach(browser);
        _gameExportDialog.Attach(browser, export);
    }

    /// <summary> Draws the Game Files export folder picker; the plugin hooks it to the UI builder like the setup window's. </summary>
    public void DrawFileDialog() => _gameExportDialog.DrawFileDialog();

    internal void AttachSkeletons(ModelSkeletonResolver? value) => _models.Skeletons = value;

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public override void OnOpen()
    {
        if (_onScreen.Items.Count == 0)
            RequestRefresh();
    }

    public override void OnClose()
    {
        // The Timeline window keeps the listener running while it is open.
        if (timelineListens?.Invoke() != true)
            animations?.StopObservation();
    }

    /// <summary> Penumbra reported a mod-setting change or a redraw; refresh once things settle. </summary>
    private void OnResourcesChanged()
        => Volatile.Write(ref _resourcesChangedTicks, Math.Max(1, Environment.TickCount64));

    /// <summary>
    /// Glamourer changed an actor in place, which Penumbra does not report as a redraw.
    /// Changes to other players (e.g. applied by sync plugins) are ignored.
    /// </summary>
    internal void OnGlamourerAppearanceChanged(nint address)
    {
        if (_onScreen.ShowsActor(address))
            OnResourcesChanged();
    }

    /// <summary>
    /// Refreshes the snapshot a second after the last Penumbra change. Changes that arrive while
    /// a refresh is running keep the timestamp, so one more refresh follows when it finishes.
    /// </summary>
    private void PumpAutoRefresh()
    {
        var changed = Volatile.Read(ref _resourcesChangedTicks);
        if (changed == 0)
            return;
        if (!_config.AutoRefreshOnScreen)
        {
            Volatile.Write(ref _resourcesChangedTicks, 0);
            return;
        }
        if (Environment.TickCount64 - changed < AutoRefreshDelayMs || _onScreen.IsRefreshing)
            return;
        if (Interlocked.CompareExchange(ref _resourcesChangedTicks, 0, changed) != changed)
            return;
        try
        {
            _onScreen.RequestRefresh();
        }
        catch (Exception e)
        {
            _log.Debug($"Automatic refresh failed: {e.Message}");
        }
    }

    private void RequestRefresh() { try { SetStatus(string.Empty, FeedbackSeverity.Success); _onScreen.RequestRefresh(); } catch (Exception e) { _log.Debug(e.Message); SetStatus("Refresh unavailable. Is Penumbra running?", FeedbackSeverity.Warning); } }

    private void OpenKofiPage()
    {
        try
        {
            Util.OpenLink(KofiUrl);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not open the XIV Instant Edit Ko-fi page.");
        }
    }

    public override void Draw()
    {
        PumpAutoRefresh();
        _previews.Pump();
        var activeTab = DrawToolbar();
        var stripHeight = ImGui.GetFrameHeight();
        var contentHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y - stripHeight - ImGui.GetStyle().ItemSpacing.Y);
        var animationsTabActive = false;
        using (var region = ImRaii.Child("##instant-edit-tab-region", new Vector2(0, contentHeight), false, ImGuiWindowFlags.NoBackground))
        {
            if (region.Success)
            {
                switch (activeTab)
                {
                    case MainTab.ModBrowser:
                        _modBrowser.Draw();
                        break;
                    case MainTab.GameFiles:
                        _gameFilesTab.Draw();
                        break;
                    case MainTab.TextureEdits:
                        _textureTab.Draw();
                        break;
                    case MainTab.Animations:
                        animationsTabActive = true;
                        DrawAnimations();
                        break;
                    case MainTab.QuickActions:
                        DrawQuickActionsTab();
                        break;
                    default:
                        _onScreenTab.Draw();
                        break;
                }
            }
        }

        _activeTab = activeTab;
        // The listener runs only while the Animations tab or the Timeline window is open, or Quick
        // Actions waits to send your character in the animation it plays: until a skeleton library is
        // saved, its first match builds one.
        if (!animationsTabActive && !(activeTab == MainTab.QuickActions && CharacterSendListens) && timelineListens?.Invoke() != true)
            animations?.StopObservation();
        _textureTab.DrawDialogs();
        _animationSend.Draw(AnimationSendDialog.MainOwner);
        _painter.DrawDialogs();
        _gameExportDialog.Draw();
        DrawNeckSeamDialog();
        DrawStatusStrip(GetFeedback(activeTab));
        DrawWindowOptionsExtension();
    }

    /// <summary>
    /// Adds the plugin-specific visibility option to Dalamud's standard window options popup.
    /// The built-in popup is drawn by <see cref="WindowSystem"/> after the window content, so
    /// this must stay the last top-level call of <see cref="Draw"/>, outside any child or id scope.
    /// </summary>
    public void DrawWindowOptionsExtension()
    {
        if (!ImGui.IsPopupOpen(WindowOptionsPopupName))
            return;

        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);
        using var popup = ImRaii.Popup(WindowOptionsPopupName, ImGuiWindowFlags.NoMove);
        if (!popup.Success)
            return;
        var keepVisible = _config.KeepVisibleWhenUiHidden;
        if (ImGui.Checkbox("Don't hide the plugin window when hiding UI", ref keepVisible))
        {
            _config.KeepVisibleWhenUiHidden = keepVisible;
            _uiBuilder.DisableUserUiHide = keepVisible;
            _saveConfig();
        }
        Widgets.HelpTip("Keep XIV Instant Edit visible when the game UI is hidden with Scroll Lock.");
    }

    /// <summary> The tab row with the connection dots, refresh and options at its right edge. </summary>
    private MainTab DrawToolbar()
    {
        var active = _pendingTab ?? _activeTab;
        _pendingTab = null;
        var style = ImGui.GetStyle();
        var frame = ImGui.GetFrameHeight();

        if (Widgets.TabButton(FontAwesomeIcon.Eye, "On Screen", active == MainTab.OnScreen)) active = MainTab.OnScreen;
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.TabButton(FontAwesomeIcon.FolderOpen, "Mod Browser", active == MainTab.ModBrowser)) active = MainTab.ModBrowser;
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.TabButton(FontAwesomeIcon.Database, "Game Files", active == MainTab.GameFiles)) active = MainTab.GameFiles;
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.TabButton(FontAwesomeIcon.Images, _textureTab.SessionsTabLabel(), active == MainTab.TextureEdits)) active = MainTab.TextureEdits;
        if (ShowAnimationsTab)
        {
            ImGui.SameLine(0, Theme.Gap);
            if (Widgets.TabButton(FontAwesomeIcon.Running, "Animations", active == MainTab.Animations)) active = MainTab.Animations;
        }
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.TabButton(FontAwesomeIcon.Bolt, "Quick Actions", active == MainTab.QuickActions)) active = MainTab.QuickActions;

        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        _blenderConnection.StartCheckIfNeeded();
        var blender = _blenderConnection.State;
        var penumbraValue = penumbra ? "OK" : "Unavailable";
        var (blenderValue, blenderDetail) = blender switch
        {
            BlenderConnectionState.Online => ("Online", "The XIV Instant Edit add-on is reachable and matches this plugin version."),
            BlenderConnectionState.VersionMismatch => ("Mismatch", BlenderClient.VersionMismatchMessage(_pluginVersion)),
            _ => ("Offline", "Start Blender and enable the XIV Instant Edit add-on to edit models."),
        };

        var painterDot = _painter.Enabled ? PainterPanel.Dot(_painter.State, _pluginVersion) : default;
        var rightWidth = Widgets.StatusDotWidth("Penumbra", penumbraValue) + Theme.Scaled(10)
                         + Widgets.StatusDotWidth("Blender", blenderValue) + Theme.Scaled(10)
                         + (_painter.Enabled ? Widgets.StatusDotWidth("Painter", painterDot.Value) + Theme.Scaled(10) : 0)
                         + frame + style.ItemSpacing.X + frame;
        ImGui.SameLine();
        var rightStart = ImGui.GetWindowContentRegionMax().X - rightWidth;
        if (rightStart > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(rightStart);
        ImGui.AlignTextToFramePadding();
        Widgets.StatusDot("Penumbra", penumbra ? Theme.Online : Theme.Offline, penumbraValue,
            penumbra ? "Penumbra is running and its IPC is available." : "Penumbra is not available. Install or enable it to browse and edit resources.");
        ImGui.SameLine(0, Theme.Scaled(10));
        ImGui.AlignTextToFramePadding();
        Widgets.StatusDot("Blender", ConnectionColour(blender), blenderValue, blenderDetail);
        ImGui.SameLine(0, Theme.Scaled(10));
        if (_painter.Enabled)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.StatusDot("Painter", painterDot.Colour, painterDot.Value, painterDot.Detail);
            ImGui.SameLine(0, Theme.Scaled(10));
        }
        var refreshing = _onScreen.IsRefreshing;
        if (Widgets.IconButton("##refresh", FontAwesomeIcon.Sync, refreshing ? "Refreshing the on-screen resource list" : "Refresh the on-screen resource list", !refreshing))
            RequestRefresh();
        ImGui.SameLine();
        if (Widgets.IconButton("##options", FontAwesomeIcon.SlidersH, "Model import, texture and animation export options"))
            _options.Open();
        _options.Draw();
        ImGui.Separator();
        return active;
    }

    private static Vector4 ConnectionColour(BlenderConnectionState state)
        => state switch
        {
            BlenderConnectionState.Online => Theme.Online,
            BlenderConnectionState.VersionMismatch => Theme.Mismatch,
            _ => Theme.Offline,
        };

    /// <summary> The bottom row's inputs: whether anything runs, and what its Cancel button stops. </summary>
    private void DrawStatusStrip(FeedbackState feedback)
    {
        var exporting = _gameExportDialog.Export?.Busy ?? false;
        var busy = _models.Busy.IsBusy || _textureTab.Busy.IsBusy || Volatile.Read(ref _neckSeamBusy) != 0 ||
                   _onScreen.IsRefreshing || (animations?.Busy ?? false) || exporting;
        var animationCancel = animations is { Busy: true, CanCancel: true };
        // Cancel stops the Game Files export on its own tab, or when no animation work can be cancelled.
        var cancelExport = exporting && (_activeTab == MainTab.GameFiles || !animationCancel);
        Action? cancel = cancelExport ? () => _gameExportDialog.Export!.Cancel() : animationCancel ? () => animations!.Cancel() : null;
        _statusStrip.Draw(feedback, busy, cancel);
    }
}