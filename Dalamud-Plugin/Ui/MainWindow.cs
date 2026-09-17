using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

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
    private const bool ShowAnimationsTab = false;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider, TextureEditService textures, Action openChangelog)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log;
        _textures = textures;
        _pluginVersion = BlenderClient.CurrentPluginVersion;
        _resourceSources = new ResourceSourceAttributor(penumbra, log);
        _materialPreviews = new MaterialPreviewBundleBuilder(data, log, _resourceSources);
        _saveConfig = saveConfig;
        _openChangelog = openChangelog;
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
        if (ImGui.BeginChild("##instant-edit-tab-region", new Vector2(0, tabRegionHeight), false, ImGuiWindowFlags.NoBackground))
        {
            if (ImGui.BeginTabBar("##instant-edit-tabs"))
            {
                if (ImGui.BeginTabItem("On Screen"))
                {
                    activeTab = MainTab.OnScreen;
                    DrawOnScreenTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Mod Browser"))
                {
                    activeTab = MainTab.ModBrowser;
                    DrawModsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Texture Edit Sessions"))
                {
                    activeTab = MainTab.TextureEdits;
                    DrawTextureSessions();
                    ImGui.EndTabItem();
                }

                if (ShowAnimationsTab && ImGui.BeginTabItem("Animations"))
                {
                    activeTab = MainTab.Animations;
                    animationsTabActive = true;
                    DrawAnimations();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();
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
    /// this callback is registered immediately after the window system draw callback.
    /// </summary>
    public void DrawWindowOptionsExtension()
    {
        if (!ImGui.IsPopupOpen(WindowOptionsPopupName))
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        if (ImGui.BeginPopup(WindowOptionsPopupName, ImGuiWindowFlags.NoMove))
        {
            var keepVisible = _config.KeepVisibleWhenUiHidden;
            if (ImGui.Checkbox("Don't hide the plugin window when hiding UI", ref keepVisible))
            {
                _config.KeepVisibleWhenUiHidden = keepVisible;
                _uiBuilder.DisableUserUiHide = keepVisible;
                _saveConfig();
            }
            ImGuiComponents.HelpMarker("Keep XIV Instant Edit visible when the game UI is hidden with Scroll Lock.");
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private void DrawOnScreenTab()
    {
        ImGui.Spacing();
        if (ImGui.SmallButton("Refresh character list")) RequestRefresh();
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##resource-filter", "Search", ref _filter, 256);
        var includeVanilla = _config.IncludeVanillaResources;
        if (ImGui.Checkbox("Include Vanilla", ref includeVanilla))
        {
            _config.IncludeVanillaResources = includeVanilla;
            _saveConfig();
        }
        ImGuiComponents.HelpMarker("Show resources loaded directly from game data alongside Penumbra-modified resources.");
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
        var listHeight = Math.Min(180, Math.Max(72, filteredMods.Length * (ImGui.GetFrameHeightWithSpacing()) + 8));
        if (ImGui.BeginChild("##mod-list", new Vector2(0, listHeight), true))
        {
            if (filteredMods.Length == 0)
                ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "No matching Penumbra mods.");
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
            ImGui.EndChild();
        }

        var selectedMod = mods.FirstOrDefault(mod =>
            string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase));
        if (selectedMod is null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "Select a Penumbra mod to browse its resources.");
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
            var color = failed ? new Vector4(.9f, .55f, .35f, 1) : new Vector4(.65f, .68f, .75f, 1);
            ImGui.TextColored(color, message);
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), selectedMod.Name);
        DrawResourceTypeFilters([modView]);
        ImGui.Spacing();
        DrawResources([modView], "No supported models, textures, or materials found in this mod.");
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "XIV INSTANT EDIT"); ImGui.SameLine(); ImGui.TextColored(new Vector4(.56f, .58f, .65f, 1), "On Screen");
        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        StartBlenderCheckIfNeeded(); BlenderConnectionState blender; lock (_stateLock) blender = _blenderState;
        Status("Penumbra", penumbra, penumbra ? "OK" : "Unavailable"); ImGui.SameLine(0, 10);
        var blenderMessage = blender switch
        {
            BlenderConnectionState.Online => "Online",
            BlenderConnectionState.VersionMismatch => BlenderClient.VersionMismatchMessage(_pluginVersion),
            _ => "Offline",
        };
        Status("Blender", blender, blenderMessage);
        ImGui.Separator();
        DrawImportOptions();
    }

    private void DrawImportOptions()
    {
        if (!ImGui.CollapsingHeader("IMPORT OPTIONS", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Separator();
            return;
        }

        var useExistingSkeleton = _config.UseExistingSkeleton;
        if (ImGui.Checkbox("Remove imported armature and use existing skeleton", ref useExistingSkeleton))
        {
            _config.UseExistingSkeleton = useExistingSkeleton;
            SaveImportOptions();
        }

        if (_config.UseExistingSkeleton)
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Imported meshes receive an Armature modifier targeting this Blender object:");
            var skeletonName = _config.SkeletonObjectName;
            ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X * 0.45f));
            if (ImGui.InputText("Skeleton object", ref skeletonName, 128))
            {
                _config.SkeletonObjectName = skeletonName;
                SaveImportOptions();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "must be an existing Blender Armature");
        }
        else
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Each import creates its own InstantEditArmature. This armature is only safe for import and export and is not suited for posing, animation or scaling.");
        }

        var applyTexturesAndMaterials = _config.ApplyTexturesAndMaterials;
        if (ImGui.Checkbox("Apply textures and materials", ref applyTexturesAndMaterials))
        {
            _config.ApplyTexturesAndMaterials = applyTexturesAndMaterials;
            SaveImportOptions();
        }
        ImGui.TextColored(
            new Vector4(.55f, .57f, .64f, 1),
            "Creates display-only Blender materials. Quick Export still writes model data only.");

        ImGui.Indent();
        ImGui.BeginDisabled(!applyTexturesAndMaterials);
        var excludeBodyAndGeneralMaterials = _config.ExcludeBodyAndGeneralMaterials;
        if (ImGui.Checkbox("Exclude body and general materials", ref excludeBodyAndGeneralMaterials))
        {
            _config.ExcludeBodyAndGeneralMaterials = excludeBodyAndGeneralMaterials;
            SaveImportOptions();
        }
        ImGui.EndDisabled();
        ImGui.TextColored(
            new Vector4(.55f, .57f, .64f, 1),
            "Leaves body skin, body-piercing, and pube materials as colored placeholders.");
        ImGui.Unindent();
        ImGui.Separator();
    }

    private void SaveImportOptions()
    {
        _config.SkeletonObjectName = string.IsNullOrWhiteSpace(_config.SkeletonObjectName)
            ? "Skeleton"
            : _config.SkeletonObjectName.Trim();
        if (_config.SkeletonObjectName.Length > 128)
            _config.SkeletonObjectName = _config.SkeletonObjectName[..128];
        _saveConfig();
    }

}
