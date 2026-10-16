using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Heels;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio;
using InstantEdit.Ui.Main;

namespace InstantEdit.Ui;

/// <summary>
/// The main window: the tab row with the connection dots, the active tab and the status strip. Each tab, card
/// and dialog is a class of its own in Ui/Main; this window builds them, routes drawing, and passes on what the
/// plugin attaches once its services have started.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const string WindowOptionsPopupName = "WindowSystemContextActions";
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";
    private const string DiscordUrl = "https://discord.gg/w5vzsPXsE7";
    private const int AutoRefreshDelayMs = 1000;
    private const bool ShowAnimationsTab = true;
    private readonly Configuration _config;
    private readonly PenumbraIpc _penumbra;
    private readonly OnScreenService _onScreen;
    private readonly IPluginLog _log;
    private readonly PreviewService _previews;
    private readonly string _pluginVersion;
    private readonly Action _saveConfig;
    private readonly Action _openChangelog;
    private readonly Action _openSettings;
    private readonly IUiBuilder _uiBuilder;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly StatusFeed _feed = new();
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
    private readonly AnimationsTab _animationsTab;
    private readonly SkinSeamDialog _skinSeams;
    private readonly TextureCompressionCard _compressionCard;
    private readonly HeelsOffsetCard _heelsCard;
    private readonly PhysicsCard _physicsCard;
    private readonly SendCharacterCard _sendCharacterCard;
    private readonly QuickActionsTab _quickActions;
    // Whether the Timeline window keeps the listener running.
    private Func<bool>? _timelineListens;
    private MainTab _activeTab = MainTab.OnScreen;
    private MainTab? _pendingTab;
    private long _resourcesChangedTicks;
    // Objects Penumbra redrew since the last frame; whether they concern the snapshot is checked in Draw.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _redrawn = new();

    internal MainWindow(Configuration config, PenumbraIpc penumbra, SourceMods sourceMods, OnScreenService onScreen,
        ResourceSourceAttributor resourceSources, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider, TextureEditService textures, INotificationManager notifications,
        PreviewService previews, Action openChangelog, Action openSettings)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _log = log;
        _previews = previews;
        _saveConfig = saveConfig;
        _openChangelog = openChangelog;
        _openSettings = openSettings;
        _uiBuilder = uiBuilder;
        var lifetime = _lifetimeCts.Token;

        _notifications = new UiNotifications(config, notifications, log);
        _options = new OptionsPopup(config, saveConfig);
        _statusStrip = new StatusStrip(_feed);
        _blenderConnection = new BlenderConnection(blender, config, textures, log);
        _pluginVersion = _blenderConnection.PluginVersion;
        _models = new ModelSender(config, penumbra, sourceMods, blender, _blenderConnection, data, chat, log, resourceSources, _feed, lifetime);
        _painter = new PainterPanel(config, log, textures, _models, _feed, _pluginVersion, lifetime);
        _textureTab = new TextureSessionsTab(config, textures, previews, _painter, _feed, log, lifetime);
        _hoverCards = new ResourceHoverCards(previews);
        _animationDelivery = new AnimationDelivery(config, blender, _blenderConnection);
        _recorder = new RecorderPanel(config, saveConfig, log, _feed, _animationDelivery, lifetime);
        _animationSend = new AnimationSendDialog(_animationHub, _animationDelivery, _feed, lifetime);
        _modBrowser = new ModBrowserTab(penumbra, _feed);
        _handoff = new MainModHandoff(_models, _textureTab, _animationSend, _feed);
        _rows = new ResourceRows(_models, _painter, _textureTab, _animationSend, _modBrowser, penumbra, log, _feed);
        _slotIcons = new SlotIcons(uiBuilder, textureProvider, data, log);
        _onScreenTab = new OnScreenTab(config, saveConfig, onScreen, _rows, _hoverCards, _textureTab, _slotIcons, _expansion);
        _gameExportDialog = new GameFileExportDialog(config, saveConfig, penumbra, log, _feed, _rows);
        _gameFilesTab = new GameFilesTab(onScreen, _rows, _hoverCards, _gameExportDialog);
        _animationsTab = new AnimationsTab(onScreen, _animationHub, _animationSend, _recorder, _rows, _expansion, lifetime);
        _skinSeams = new SkinSeamDialog(onScreen, config, log, _feed, lifetime);
        _compressionCard = new TextureCompressionCard(config, log, _notifications, lifetime);
        _heelsCard = new HeelsOffsetCard(config, saveConfig, _feed);
        _physicsCard = new PhysicsCard();
        _sendCharacterCard = new SendCharacterCard(config, saveConfig, onScreen, penumbra, blender, _blenderConnection, _models, _animationHub,
            _animationDelivery, _recorder, resourceSources, chat, log, _feed, lifetime);
        // The cards in their built-in order; the user's order and removed cards are applied by the tab.
        _quickActions = new QuickActionsTab(config, saveConfig,
        [
            _compressionCard.Card,
            _heelsCard.Card,
            new SkinSeamCard(onScreen, _skinSeams).Card,
            _physicsCard.Card,
            new PaintSkinCard(config, onScreen, _painter, openSettings).Card,
            _sendCharacterCard.Card,
        ]);

        _feed.Reported += _notifications.Notify;
        _penumbra.ResourcesChanged += OnResourcesChanged;
        _penumbra.ObjectRedrawn += OnObjectRedrawn;
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
            Click = _ => OpenPage(KofiUrl, "Ko-fi"),
            ShowTooltip = () => ImGui.SetTooltip("♥ Support me on Ko-fi"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Comments,
            Click = _ => OpenPage(DiscordUrl, "Discord"),
            ShowTooltip = () => ImGui.SetTooltip("Join the Discord"),
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
        _penumbra.ObjectRedrawn -= OnObjectRedrawn;
        _feed.Reported -= _notifications.Notify;
        _compressionCard.Detach();
        _painter.Detach();
        _gameExportDialog.Detach();
        _lifetimeCts.Cancel();
        _slotIcons.Dispose();
        _lifetimeCts.Dispose();
    }

    // ---- What the plugin attaches once its services have started ---------------------------------

    public void RequestCacheSynchronization() => _blenderConnection.RequestCacheSynchronization();

    public void ReportImportFailure(BridgeFailure failure) => _models.ReportImportFailure(failure);

    internal void AttachSkeletons(ModelSkeletonResolver? value) => _models.Skeletons = value;

    internal void AttachPainter(PainterJobService painter) => _painter.Attach(painter);

    internal void AttachAnimations(AnimationEditService? service, string? error) => _animationHub.Attach(service, error);

    internal void AttachRecorder(AnimationRecorder? value) => _recorder.Recorder = value;

    /// <summary>The recorder, drawn in the Timeline window's Record page; it sends to Blender through this window.</summary>
    internal void DrawRecorderPanel() => _recorder.Draw();

    /// <summary>The connection animations go to Blender through; the Timeline window sends and reads motion with it.</summary>
    internal AnimationDelivery AnimationDelivery => _animationDelivery;

    internal void AttachTimeline(Action<AnimationCapture, bool>? open, Action? show = null, Func<bool>? listens = null)
    {
        _animationsTab.AttachTimeline(open, show);
        _timelineListens = listens;
    }

    /// <summary>Whether this window keeps the listener running: its Animations tab is shown, or Send my character waits for it.</summary>
    internal bool ListensForAnimations => IsOpen && (_activeTab == MainTab.Animations || _activeTab == MainTab.QuickActions && _sendCharacterCard.Listens);

    /// <summary> The Mod Browser: Penumbra's mods as tiles, a window per open mod, and the modpack builder. </summary>
    internal void AttachStudio(StudioHost studio) => _modBrowser.Attach(studio, _handoff);

    internal void AttachGameFiles(GameFileBrowserService browser, GameFileExportService export)
    {
        _gameFilesTab.Attach(browser);
        _gameExportDialog.Attach(browser, export);
    }

    /// <summary> Draws the Game Files export folder picker; the plugin hooks it to the UI builder like the setup window's. </summary>
    // When the window was last drawn; 0 once its previews were let go.
    private long _drawnAt;
    private const long ReleasePreviewsAfterMs = 120_000;

    /// <summary>
    /// Every frame, open or not: the export file dialog, and letting go of what only the window shows once it
    /// hasn't for a while: Game Files' data, and the hover cards' previews two minutes after the window closed.
    /// </summary>
    public void DrawFileDialog()
    {
        _gameExportDialog.DrawFileDialog();
        _gameFilesTab.ReleaseWhenUnused();
        if (_drawnAt != 0 && !IsOpen && Environment.TickCount64 - _drawnAt >= ReleasePreviewsAfterMs)
        {
            _drawnAt = 0;
            _previews.Release();
        }
    }

    internal void AttachNeckSeam(NeckSeamService service) => _skinSeams.Attach(service);

    internal void AttachTextureCompression(TextureCompressionService service) => _compressionCard.Attach(service);

    internal void AttachHeelsOffset(HeelsOffsetService service) => _heelsCard.Attach(service);

    internal void AttachPhysics(Action open) => _physicsCard.Attach(open);

    internal void AttachCharacterDrawState(PainterLiveReader? reader) => _sendCharacterCard.AttachDrawState(reader);

    // ---- The window ------------------------------------------------------------------------------

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
        if (_timelineListens?.Invoke() != true)
            _animationHub.Service?.StopObservation();
    }

    /// <summary> Penumbra reported a mod-setting change, or a redraw that concerns the snapshot; refresh once things settle. </summary>
    private void OnResourcesChanged()
        => Volatile.Write(ref _resourcesChangedTicks, Math.Max(1, Environment.TickCount64));

    /// <summary> Penumbra redrew an object, on its thread; <see cref="PumpAutoRefresh"/> checks whose. </summary>
    private void OnObjectRedrawn(int objectIndex)
        => _redrawn.TryAdd(objectIndex, 0);

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
        // Only redraws of your character, what it owns or what is listed refresh the snapshot: each
        // refresh costs a framework visit, and other players redraw constantly where sync plugins run.
        if (!_redrawn.IsEmpty)
        {
            var concerns = false;
            foreach (var index in _redrawn.Keys)
            {
                _redrawn.TryRemove(index, out _);
                concerns |= _onScreen.Concerns(index);
            }
            if (concerns)
                OnResourcesChanged();
        }
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

    private void RequestRefresh(bool fresh = false) { try { _feed.SetStatus(string.Empty, FeedbackSeverity.Success); _onScreen.RequestRefresh(fresh); } catch (Exception e) { _log.Debug(e.Message); _feed.SetStatus("Refresh unavailable. Is Penumbra running?", FeedbackSeverity.Warning); } }

    private void OpenPage(string url, string name)
    {
        try
        {
            Util.OpenLink(url);
        }
        catch (Exception e)
        {
            _log.Error(e, $"Could not open the XIV Instant Edit {name} page.");
        }
    }

    public override void Draw()
    {
        _drawnAt = Math.Max(1, Environment.TickCount64);
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
                        _animationsTab.Draw();
                        break;
                    case MainTab.QuickActions:
                        _quickActions.Draw();
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
        if (!animationsTabActive && !(activeTab == MainTab.QuickActions && _sendCharacterCard.Listens) && _timelineListens?.Invoke() != true)
            _animationHub.Service?.StopObservation();
        _textureTab.DrawDialogs();
        _animationSend.Draw(AnimationSendDialog.MainOwner);
        _painter.DrawDialogs();
        _gameExportDialog.Draw();
        _skinSeams.Draw();
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
            RequestRefresh(fresh: true);
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

    // ---- The status strip ------------------------------------------------------------------------

    private static StatusChannel ChannelOf(MainTab tab)
        => tab switch
        {
            MainTab.TextureEdits => StatusChannel.Textures,
            MainTab.Animations => StatusChannel.Animations,
            _ => StatusChannel.Models,
        };

    private FeedbackState GetFeedback(MainTab tab)
    {
        if (tab == MainTab.Animations)
        {
            var service = _animationHub.Service;
            if (service is null)
                return new FeedbackState(string.Empty, FeedbackSeverity.Error);
            return new FeedbackState(service.Status, service.LastFailed && !service.Busy
                ? FeedbackSeverity.Error
                : FeedbackSeverity.Success);
        }

        var latest = _feed.Latest(ChannelOf(tab));
        return latest is null
            ? new FeedbackState(string.Empty, FeedbackSeverity.Success)
            : new FeedbackState(latest.Text, latest.Severity);
    }

    /// <summary> The bottom row's inputs: whether anything runs, and what its Cancel button stops. </summary>
    private void DrawStatusStrip(FeedbackState feedback)
    {
        var exporting = _gameExportDialog.Export?.Busy ?? false;
        var busy = _models.Busy.IsBusy || _textureTab.Busy.IsBusy || _skinSeams.Busy.IsBusy ||
                   _onScreen.IsRefreshing || (_animationHub.Service?.Busy ?? false) || exporting;
        var animationCancel = _animationHub.Service is { Busy: true, CanCancel: true };
        // Cancel stops the Game Files export on its own tab, or when no animation work can be cancelled.
        var cancelExport = exporting && (_activeTab == MainTab.GameFiles || !animationCancel);
        Action? cancel = cancelExport ? () => _gameExportDialog.Export!.Cancel() : animationCancel ? () => _animationHub.Service!.Cancel() : null;
        _statusStrip.Draw(feedback, busy, cancel);
    }

    private enum MainTab
    {
        OnScreen,
        ModBrowser,
        TextureEdits,
        Animations,
        GameFiles,
        QuickActions,
    }
}
