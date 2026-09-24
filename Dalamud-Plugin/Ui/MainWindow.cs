using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Services;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui;

/// <summary>Compact resource browser for the authoritative Penumbra resource snapshot.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const string WindowOptionsPopupName = "WindowSystemContextActions";
    private const string OptionsPopupName = "##instant-edit-options";
    private const string HistoryPopupName = "##instant-edit-status-history";
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";
    private const string ModBrowserAmbiguityWarning = "Imports from the Mod Browser can be ambiguous when a mod maps the same model in several options. Use the On Screen tab for Mashups and for saving to new modpacks.";
    private const int AutoRefreshDelayMs = 1000;
    private readonly Configuration _config; private readonly PenumbraService _penumbra; private readonly OnScreenService _onScreen;
    private readonly BlenderClient _blender; private readonly IDataManager _data; private readonly IChatGui _chat; private readonly IPluginLog _log;
    private readonly INotificationManager _notifications;
    private readonly string _pluginVersion;
    private readonly MaterialPreviewBundleBuilder _materialPreviews;
    private readonly ResourceSourceAttributor _resourceSources;
    private readonly Action _saveConfig;
    private readonly Action _openChangelog;
    private readonly Action _openSettings;
    private readonly IUiBuilder _uiBuilder;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IReadOnlyDictionary<string, IDalamudTextureWrap> _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
    private BlenderConnectionState _blenderState = BlenderConnectionState.Offline;
    private bool _blenderChecking; private int _editing;
    private DateTime _lastBlenderCheck = DateTime.MinValue;
    private DateTime _lastModListRefresh = DateTime.MinValue;
    private string _filter = string.Empty, _modFilter = string.Empty;
    private string? _selectedModDirectory, _loadedModDirectory;
    private IReadOnlyList<PenumbraMod> _mods = Array.Empty<PenumbraMod>();
    private ActorView? _loadedModView;
    private CancellationTokenSource? _modLoadCts;
    private bool _modLoading, _modLoadFailed;
    private MainTab _activeTab = MainTab.OnScreen;
    private MainTab? _pendingTab;
    private long _resourcesChangedTicks;
    private string _sessionsTabLabel = "Sessions";
    private int _sessionsTabCount = -1;
    private const bool ShowAnimationsTab = true;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen,
        ResourceSourceAttributor resourceSources, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider, TextureEditService textures, INotificationManager notifications,
        PreviewService previews, Action openChangelog, Action openSettings)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log;
        _textures = textures;
        _notifications = notifications;
        _previews = previews;
        _pluginVersion = BlenderClient.CurrentPluginVersion;
        _resourceSources = resourceSources;
        _materialPreviews = new MaterialPreviewBundleBuilder(data, log, _resourceSources);
        _saveConfig = saveConfig;
        _openChangelog = openChangelog;
        _openSettings = openSettings;
        _uiBuilder = uiBuilder;
        _feed.Reported += Notify;
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
        _ = uiBuilder.RunWhenUiPrepared(() => LoadSlotIcons(uiBuilder, textureProvider), true);
    }

    public void Dispose()
    {
        _penumbra.ResourcesChanged -= OnResourcesChanged;
        _feed.Reported -= Notify;
        _lifetimeCts.Cancel();
        _modLoadCts?.Cancel();
        _modLoadCts?.Dispose();
        _modLoadCts = null;
        IReadOnlyDictionary<string, IDalamudTextureWrap> icons;
        lock (_stateLock)
        {
            icons = _slotIcons;
            _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
        }

        foreach (var icon in icons.Values.Distinct())
            icon.Dispose();
        _lifetimeCts.Dispose();
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public override void OnOpen()
    {
        if (_onScreen.Items.Count == 0)
            RequestRefresh();
    }

    public override void OnClose() => animations?.StopObservation();

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
                        DrawModsTab();
                        break;
                    case MainTab.TextureEdits:
                        DrawTextureSessions();
                        break;
                    case MainTab.Animations:
                        animationsTabActive = true;
                        DrawAnimations();
                        break;
                    default:
                        DrawOnScreenTab();
                        break;
                }
            }
        }

        _activeTab = activeTab;
        // The listener runs for the Animations tab, and for On Screen while its Animations filter
        // is on. It is not started otherwise: until a skeleton library is saved, its first match builds one.
        if (activeTab == MainTab.OnScreen && _kinds.Contains(ResourceKinds.Animation))
            animations?.StartObservation();
        else if (!animationsTabActive)
            animations?.StopObservation();
        DrawTextureDialogs();
        DrawAnimationSendDialog();
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
        if (Widgets.TabButton(FontAwesomeIcon.Images, SessionsTabLabel(), active == MainTab.TextureEdits)) active = MainTab.TextureEdits;
        if (ShowAnimationsTab)
        {
            ImGui.SameLine(0, Theme.Gap);
            if (Widgets.TabButton(FontAwesomeIcon.Running, "Animations", active == MainTab.Animations)) active = MainTab.Animations;
        }

        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        StartBlenderCheckIfNeeded();
        BlenderConnectionState blender;
        lock (_stateLock) blender = _blenderState;
        var penumbraValue = penumbra ? "OK" : "Unavailable";
        var (blenderValue, blenderDetail) = blender switch
        {
            BlenderConnectionState.Online => ("Online", "The XIV Instant Edit add-on is reachable and matches this plugin version."),
            BlenderConnectionState.VersionMismatch => ("Mismatch", BlenderClient.VersionMismatchMessage(_pluginVersion)),
            _ => ("Offline", "Start Blender and enable the XIV Instant Edit add-on to edit models."),
        };

        var rightWidth = Widgets.StatusDotWidth("Penumbra", penumbraValue) + Theme.Scaled(10)
                         + Widgets.StatusDotWidth("Blender", blenderValue) + Theme.Scaled(10)
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
        var refreshing = _onScreen.IsRefreshing;
        if (Widgets.IconButton("##refresh", FontAwesomeIcon.Sync, refreshing ? "Refreshing the on-screen resource list…" : "Refresh the on-screen resource list", !refreshing))
            RequestRefresh();
        ImGui.SameLine();
        if (Widgets.IconButton("##options", FontAwesomeIcon.SlidersH, "Model import, texture and animation export options"))
            ImGui.OpenPopup(OptionsPopupName);
        DrawOptionsPopup();
        ImGui.Separator();
        return active;
    }

    private string SessionsTabLabel()
    {
        var count = _textures.Sessions.Count;
        if (count != _sessionsTabCount)
        {
            _sessionsTabCount = count;
            _sessionsTabLabel = count == 0 ? "Sessions" : $"Sessions ({count})";
        }
        return _sessionsTabLabel;
    }

    private static Vector4 ConnectionColour(BlenderConnectionState state)
        => state switch
        {
            BlenderConnectionState.Online => Theme.Online,
            BlenderConnectionState.VersionMismatch => Theme.Mismatch,
            _ => Theme.Offline,
        };

    private void DrawOptionsPopup()
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(600, 640));
        using var popup = ImRaii.Popup(OptionsPopupName);
        if (!popup.Success)
            return;
        Widgets.SectionHeader("Model import");
        DrawModelOptions();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Widgets.SectionHeader("Texture editing");
        DrawTextureOptions();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Widgets.SectionHeader("Animation export");
        DrawAnimationExportOptions();
    }

    /// <summary> The bottom row: latest message for the active tab, busy indicator, and the history popover. </summary>
    private void DrawStatusStrip(FeedbackState feedback)
    {
        var busy = Volatile.Read(ref _editing) != 0 || Volatile.Read(ref _textureBusy) != 0 || _onScreen.IsRefreshing || (animations?.Busy ?? false);
        var canCancel = animations is { Busy: true, CanCancel: true };
        var style = ImGui.GetStyle();
        var frame = ImGui.GetFrameHeight();
        using var strip = ImRaii.Child("##instant-edit-status-strip", new Vector2(0, frame), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!strip.Success)
            return;

        var rightWidth = frame;
        if (busy)
            rightWidth += frame + style.ItemSpacing.X + ImGui.CalcTextSize("Working…").X + style.ItemSpacing.X;
        if (canCancel)
            rightWidth += ImGui.CalcTextSize("Cancel").X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        var messageWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - rightWidth - style.ItemSpacing.X);
        using (var message = ImRaii.Child("##instant-edit-status-message", new Vector2(messageWidth, frame), false,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (message.Success)
            {
                ImGui.AlignTextToFramePadding();
                if (feedback.Text.Length == 0)
                    ImGui.TextColored(Theme.Hint, "Ready");
                else
                {
                    var (accent, _, icon) = Theme.Severity(feedback.Severity);
                    ImGui.TextColored(accent, icon);
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextUnformatted(FirstLine(feedback.Text));
                    if (ImGui.IsItemHovered())
                    {
                        using var tooltip = ImRaii.Tooltip();
                        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(480));
                        ImGui.TextUnformatted(feedback.Text);
                    }
                }
            }
        }

        ImGui.SameLine();
        if (busy)
        {
            Widgets.Spinner();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Working…");
            ImGui.SameLine();
            if (canCancel && ImGui.SmallButton("Cancel"))
                animations!.Cancel();
            if (canCancel)
                ImGui.SameLine();
        }
        if (Widgets.IconButton("##history", FontAwesomeIcon.History, "Recent messages"))
            ImGui.OpenPopup(HistoryPopupName);
        DrawStatusHistory();
    }

    private void DrawStatusHistory()
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(380, 0), Theme.Scaled(680, 440));
        using var popup = ImRaii.Popup(HistoryPopupName);
        if (!popup.Success)
            return;
        var entries = _feed.History(20);
        if (entries.Count == 0)
        {
            ImGui.TextColored(Theme.Hint, "No messages yet.");
            return;
        }

        foreach (var entry in entries)
        {
            var (accent, _, icon) = Theme.Severity(entry.Severity);
            ImGui.TextColored(Theme.Muted, entry.Time.ToLocalTime().ToString("HH:mm:ss"));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, Theme.Gap);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextUnformatted(entry.Text);
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return end < 0 ? text : text[..end];
    }

    /// <summary> Mirrors warnings, errors and model handoff results as Dalamud notifications. </summary>
    private void Notify(StatusEntry entry)
    {
        if (!_config.ShowNotifications)
            return;
        if (entry.Severity == FeedbackSeverity.Success && entry.Channel != StatusChannel.Models)
            return;
        try
        {
            _notifications.AddNotification(new Notification
            {
                Title = "XIV Instant Edit",
                Content = entry.Text,
                Type = entry.Severity switch
                {
                    FeedbackSeverity.Warning => NotificationType.Warning,
                    FeedbackSeverity.Error => NotificationType.Error,
                    FeedbackSeverity.Info => NotificationType.Info,
                    _ => NotificationType.Success,
                },
            });
        }
        catch (Exception e)
        {
            _log.Debug($"Could not show a notification: {e.Message}");
        }
    }

    private void DrawTextureOptions()
    {
        var recompress = _config.RecompressTextures;
        if (ImGui.Checkbox("Recompress saved textures", ref recompress))
        {
            _config.RecompressTextures = recompress;
            _saveConfig();
        }
        Widgets.HintWrapped("Texture edits keep their original compression (BC1–BC7). When off, saves are written uncompressed: larger files without compression artifacts.");
    }

    private void DrawModelOptions()
    {
        var useExistingSkeleton = _config.UseExistingSkeleton;
        if (ImGui.Checkbox("Remove imported armature and use existing skeleton", ref useExistingSkeleton))
        {
            _config.UseExistingSkeleton = useExistingSkeleton;
            SaveModelOptions();
        }

        if (_config.UseExistingSkeleton)
        {
            Widgets.HintWrapped("Imported meshes receive an Armature modifier targeting this Blender object:");
            var skeletonName = _config.SkeletonObjectName;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(180), ImGui.GetContentRegionAvail().X * 0.45f));
            if (ImGui.InputText("Skeleton object", ref skeletonName, 128))
            {
                _config.SkeletonObjectName = skeletonName;
                SaveModelOptions();
            }
            ImGui.SameLine();
            Widgets.Hint("must be an existing Blender Armature");
        }
        else
        {
            Widgets.HintWrapped("Each import creates its own InstantEditArmature. This armature is only safe for import and export and is not suited for posing, animation or scaling.");
        }

        var applyTexturesAndMaterials = _config.ApplyTexturesAndMaterials;
        if (ImGui.Checkbox("Apply textures and materials", ref applyTexturesAndMaterials))
        {
            _config.ApplyTexturesAndMaterials = applyTexturesAndMaterials;
            SaveModelOptions();
        }
        Widgets.HintWrapped("Creates display-only Blender materials. Quick Export still writes model data only.");

        using var indent = ImRaii.PushIndent();
        using (ImRaii.Disabled(!applyTexturesAndMaterials))
        {
            var excludeBodyAndGeneralMaterials = _config.ExcludeBodyAndGeneralMaterials;
            if (ImGui.Checkbox("Exclude body and general materials", ref excludeBodyAndGeneralMaterials))
            {
                _config.ExcludeBodyAndGeneralMaterials = excludeBodyAndGeneralMaterials;
                SaveModelOptions();
            }
        }
        Widgets.HintWrapped("Leaves body skin, body-piercing, and pube materials as colored placeholders.");
    }

    private void SaveModelOptions()
    {
        _config.SkeletonObjectName = string.IsNullOrWhiteSpace(_config.SkeletonObjectName)
            ? "Skeleton"
            : _config.SkeletonObjectName.Trim();
        if (_config.SkeletonObjectName.Length > 128)
            _config.SkeletonObjectName = _config.SkeletonObjectName[..128];
        _saveConfig();
    }
}
