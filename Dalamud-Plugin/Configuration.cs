using Dalamud.Configuration;
using InstantEdit.Models;

namespace InstantEdit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 10;

    /// <summary>Whether the first-time setup wizard has been completed.</summary>
    public bool FirstTimeSetupCompleted { get; set; }

    /// <summary>Version of the in-game changelog the user last opened.</summary>
    public string LastSeenChangelogVersion { get; set; } = "";

    /// <summary> Port the Blender add-on listens on for import commands. </summary>
    public int BlenderPort { get; set; } = 42424;

    /// <summary> Port this plugin listens on for export results coming from Blender. </summary>
    public int ListenPort { get; set; } = 42428;

    public string TextureEditorPath { get; set; } = "";

    /// <summary> Show the Substance Painter action in On Screen, its status dot and its Sessions cards. </summary>
    public bool PainterIntegrationEnabled { get; set; }

    /// <summary> Port the XIV Instant Edit plugin inside Substance Painter listens on. </summary>
    public int PainterPort { get; set; } = 42426;

    /// <summary> Substance Painter executable, started when a project is sent while Painter is closed; empty searches the usual folders. </summary>
    public string PainterExecutablePath { get; set; } = "";

    /// <summary>Re-encode texture saves in their original TEX format instead of uncompressed BGRA32.</summary>
    public bool RecompressTextures { get; set; } = true;

    /// <summary>Base directory for the shared XIV Instant Edit cache.</summary>
    public string TextureCacheDirectory { get; set; } = "";

    /// <summary>Automatically remove stale model cache jobs and inactive texture sessions.</summary>
    public bool AutomaticCacheCleanup { get; set; } = true;

    /// <summary>
    /// Legacy cache root written by the first texture-edit implementation. It is
    /// read once during migration and is not written back to plugin settings.
    /// </summary>
    public string TextureCacheRoot { get; set; } = "";

    public bool ShouldSerializeTextureCacheRoot() => false;

    /// <summary>Legacy managed-mod setting retained for configuration compatibility.</summary>

    /// <summary>Bind imported meshes to an existing Blender armature instead of creating one.</summary>
    public bool UseExistingSkeleton { get; set; }

    /// <summary>Name of the scene armature used when <see cref="UseExistingSkeleton"/> is enabled.</summary>
    public string SkeletonObjectName { get; set; } = "Skeleton";

    /// <summary>Blender armature that animations from the Animations tab are keyed onto.</summary>
    public string AnimationArmatureName { get; set; } = "Skeleton";

    /// <summary>
    /// Key bone scale in animations sent to Blender. Off leaves scaling applied in Blender,
    /// such as a Customize+ profile, in place.
    /// </summary>
    public bool AnimationKeyScale { get; set; }

    /// <summary>Length of a live pose recording, in seconds.</summary>
    public float RecordingSeconds { get; set; } = 5f;

    /// <summary>Countdown before a live pose recording starts, in seconds.</summary>
    public float RecordingDelaySeconds { get; set; } = 3f;

    /// <summary>Create display-only Blender materials from the resolved FFXIV resources.</summary>
    public bool ApplyTexturesAndMaterials { get; set; }

    /// <summary>Skip body skin, body-piercing, and pube preview resources.</summary>
    public bool ExcludeBodyAndGeneralMaterials { get; set; }

    /// <summary>Show game-data resources alongside Penumbra-modified resources on screen.</summary>
    public bool IncludeVanillaResources { get; set; }

    /// <summary>Keep the main window visible when the user hides the game UI with Scroll Lock.</summary>
    public bool KeepVisibleWhenUiHidden { get; set; }

    /// <summary>Mirror warnings, errors and model handoff results as Dalamud notifications.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Refresh the On Screen list a second after Penumbra reports a mod-setting change or a redraw.</summary>
    public bool AutoRefreshOnScreen { get; set; } = true;

    /// <summary>Render a small shaded thumbnail of Dawntrail models in the hover card.</summary>
    public bool RenderModelThumbnails { get; set; } = true;

    /// <summary>Legacy v9 context payload retained only for one-time migration or storage fallback.</summary>
    public List<PersistedExportContext> ExportContexts { get; set; } = [];

    // Newtonsoft.Json (used by Dalamud for plugin settings) honors this
    // convention.  Keep the property readable for the one-time v9 migration,
    // but do not put the migrated context payload back into InstantEdit.json.
    public bool ShouldSerializeExportContexts()
        => Version < 10 || ExportContexts.Count > 0;

}
