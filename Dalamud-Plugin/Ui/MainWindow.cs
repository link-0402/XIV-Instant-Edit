using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary>Compact resource browser for the authoritative Penumbra resource snapshot.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const string WindowOptionsPopupName = "WindowSystemContextActions";
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";
    private const string ModBrowserAmbiguityWarning = "The Mod Browser selection is potentially ambiguous. Use the On Screen tab for Mashups and saving to new modpacks instead.";
    private readonly Configuration _config; private readonly PenumbraService _penumbra; private readonly OnScreenService _onScreen;
    private readonly BlenderClient _blender; private readonly IDataManager _data; private readonly IChatGui _chat; private readonly IPluginLog _log;
    private readonly string _pluginVersion;
    private readonly MaterialPreviewBundleBuilder _materialPreviews;
    private readonly ResourceSourceAttributor _resourceSources;
    private readonly Action _saveConfig;
    private readonly Action _openChangelog;
    private readonly Action _openSettings;
    private readonly IUiBuilder _uiBuilder;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedFiltered = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IDalamudTextureWrap> _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
    private BlenderConnectionState _blenderState = BlenderConnectionState.Offline;
    private bool _blenderChecking; private int _editing;
    private DateTime _lastBlenderCheck = DateTime.MinValue;
    private DateTime _lastModListRefresh = DateTime.MinValue;
    private string _filter = string.Empty, _modFilter = string.Empty, _resourceTypeFilter = "Models", _status = string.Empty, _textureStatus = string.Empty;
    private string? _selectedModDirectory, _loadedModDirectory;
    private IReadOnlyList<PenumbraMod> _mods = Array.Empty<PenumbraMod>();
    private ActorView? _loadedModView;
    private CancellationTokenSource? _modLoadCts;
    private bool _modLoading, _modLoadFailed;
    private FeedbackSeverity _statusSeverity = FeedbackSeverity.Success;
    private FeedbackSeverity _textureStatusSeverity = FeedbackSeverity.Success;
    private MainTab _activeTab = MainTab.OnScreen;
    private const bool ShowAnimationsTab = true;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen,
        ResourceSourceAttributor resourceSources, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider, TextureEditService textures, Action openChangelog, Action openSettings)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log;
        _textures = textures;
        _pluginVersion = BlenderClient.CurrentPluginVersion;
        _resourceSources = resourceSources;
        _materialPreviews = new MaterialPreviewBundleBuilder(data, log, _resourceSources);
        _saveConfig = saveConfig;
        _openChangelog = openChangelog;
        _openSettings = openSettings;
        _uiBuilder = uiBuilder;
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
        DrawHeader();
        var feedback = GetFeedback(_activeTab);
        var feedbackHeight = GetFeedbackHeight(feedback);
        var feedbackSpacing = feedbackHeight > 0 ? ImGui.GetStyle().ItemSpacing.Y : 0;
        var tabRegionHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y - feedbackHeight - feedbackSpacing);
        var activeTab = _activeTab;
        var animationsTabActive = false;
        using (var region = ImRaii.Child("##instant-edit-tab-region", new Vector2(0, tabRegionHeight), false, ImGuiWindowFlags.NoBackground))
        {
            if (region.Success)
            {
                using var tabs = ImRaii.TabBar("##instant-edit-tabs");
                if (tabs.Success)
                {
                    using (var tab = ImRaii.TabItem("On Screen"))
                    {
                        if (tab.Success)
                        {
                            activeTab = MainTab.OnScreen;
                            DrawOnScreenTab();
                        }
                    }

                    using (var tab = ImRaii.TabItem("Mod Browser"))
                    {
                        if (tab.Success)
                        {
                            activeTab = MainTab.ModBrowser;
                            DrawModsTab();
                        }
                    }

                    using (var tab = ImRaii.TabItem("Texture Edit Sessions"))
                    {
                        if (tab.Success)
                        {
                            activeTab = MainTab.TextureEdits;
                            DrawTextureSessions();
                        }
                    }

                    if (ShowAnimationsTab)
                    {
                        using var tab = ImRaii.TabItem("Animations");
                        if (tab.Success)
                        {
                            activeTab = MainTab.Animations;
                            animationsTabActive = true;
                            DrawAnimations();
                        }
                    }
                }
            }
        }

        _activeTab = activeTab;
        if (!animationsTabActive)
            animations?.StopObservation();
        DrawTextureDialogs();
        DrawFeedback(GetFeedback(activeTab));
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

    private void DrawOnScreenTab()
    {
        ImGui.Spacing();
        var refreshing = _onScreen.IsRefreshing;
        using (ImRaii.Disabled(refreshing))
        {
            if (ImGui.SmallButton(refreshing ? "Refreshing…##refresh-character-list" : "Refresh character list##refresh-character-list"))
                RequestRefresh();
        }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##resource-filter", "Search", ref _filter, 256);
        var includeVanilla = _config.IncludeVanillaResources;
        if (ImGui.Checkbox("Include Vanilla", ref includeVanilla))
        {
            _config.IncludeVanillaResources = includeVanilla;
            _saveConfig();
        }
        Widgets.HelpTip("Show resources loaded directly from game data alongside Penumbra-modified resources.");
        var actors = ReadActors();
        DrawResourceTypeFilters(actors);
        ImGui.Spacing();
        DrawResources(actors);
    }

    private void DrawModsTab()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##mod-filter", "Search", ref _modFilter, 256);

        var mods = ReadMods();
        var filteredMods = mods.Where(ModMatches).ToArray();
        var listHeight = Math.Min(Theme.Scaled(180), Math.Max(Theme.Scaled(72), filteredMods.Length * ImGui.GetFrameHeightWithSpacing() + Theme.Scaled(8)));
        using (var list = ImRaii.Child("##mod-list", new Vector2(0, listHeight), true))
        {
            if (list.Success)
            {
                if (filteredMods.Length == 0)
                    ImGui.TextColored(Theme.Muted, "No matching Penumbra mods.");
                else
                    foreach (var mod in filteredMods)
                    {
                        var selected = string.Equals(_selectedModDirectory, mod.Directory, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable($"{mod.Name}##mod:{SafeId(mod.Directory)}", selected))
                        {
                            CancelModLoad();
                            _selectedModDirectory = mod.Directory;
                            _loadedModDirectory = null;
                            _loadedModView = null;
                        }
                    }
            }
        }

        var selectedMod = mods.FirstOrDefault(mod =>
            string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase));
        if (selectedMod is null)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Select a Penumbra mod to browse its resources.");
            return;
        }

        var modView = GetModView(selectedMod);
        if (modView is null)
        {
            ImGui.Spacing();
            bool loading;
            bool failed;
            lock (_stateLock)
            {
                loading = _modLoading;
                failed = _modLoadFailed;
            }
            var message = loading ? "Scanning the selected Penumbra mod..." :
                failed ? "Could not read the selected Penumbra mod." : "No supported resources found.";
            ImGui.TextColored(failed ? Theme.Offline : Theme.Muted, message);
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Label, selectedMod.Name);
        DrawResourceTypeFilters([modView]);
        ImGui.Spacing();
        DrawResources([modView], "No supported models, textures, or materials found in this mod.");
    }

    private void DrawHeader()
    {
        ImGui.TextColored(Theme.Accent, "XIV INSTANT EDIT"); ImGui.SameLine(); ImGui.TextColored(Theme.Muted, "On Screen");
        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        StartBlenderCheckIfNeeded(); BlenderConnectionState blender; lock (_stateLock) blender = _blenderState;
        Widgets.StatusDot("Penumbra", penumbra ? Theme.Online : Theme.Offline, penumbra ? "OK" : "Unavailable"); ImGui.SameLine(0, Theme.Scaled(10));
        var blenderMessage = blender switch
        {
            BlenderConnectionState.Online => "Online",
            BlenderConnectionState.VersionMismatch => BlenderClient.VersionMismatchMessage(_pluginVersion),
            _ => "Offline",
        };
        Widgets.StatusDot("Blender", ConnectionColour(blender), blenderMessage);
        ImGui.Separator();
        DrawOptions();
    }

    private static Vector4 ConnectionColour(BlenderConnectionState state)
        => state switch
        {
            BlenderConnectionState.Online => Theme.Online,
            BlenderConnectionState.VersionMismatch => Theme.Mismatch,
            _ => Theme.Offline,
        };

    private void DrawOptions()
    {
        if (!ImGui.CollapsingHeader("OPTIONS", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Separator();
            return;
        }

        ImGui.TextColored(Theme.Label, "Models");
        DrawModelOptions();
        ImGui.Separator();
        ImGui.TextColored(Theme.Label, "Textures");
        DrawTextureOptions();
        ImGui.Separator();
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
            Widgets.Hint("Imported meshes receive an Armature modifier targeting this Blender object:");
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
            Widgets.Hint("Each import creates its own InstantEditArmature. This armature is only safe for import and export and is not suited for posing, animation or scaling.");
        }

        var applyTexturesAndMaterials = _config.ApplyTexturesAndMaterials;
        if (ImGui.Checkbox("Apply textures and materials", ref applyTexturesAndMaterials))
        {
            _config.ApplyTexturesAndMaterials = applyTexturesAndMaterials;
            SaveModelOptions();
        }
        Widgets.Hint("Creates display-only Blender materials. Quick Export still writes model data only.");

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
        Widgets.Hint("Leaves body skin, body-piercing, and pube materials as colored placeholders.");
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
