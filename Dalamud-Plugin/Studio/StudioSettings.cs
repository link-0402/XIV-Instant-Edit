using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models;
using Newtonsoft.Json;

namespace InstantEdit.Studio;

/// <summary>
/// The Mod Browser's settings, kept in the plugin's configuration: the modpack being built, the
/// mods opened recently, the panes' layout and the defaults for new content.
/// </summary>
[Serializable]
public sealed class StudioSettings
{
    /// <summary>Last-selected equipment slot (index into <see cref="SlotInfo.AllSlots"/>).</summary>
    public int LastSlotIndex { get; set; } = 1;

    /// <summary>Last-used item search text.</summary>
    public string LastItemSearch { get; set; } = string.Empty;

    /// <summary>
    /// Key of the last-selected port subject: an Item-sheet row id for gear, or an encoded
    /// character feature key (top bit set, see <c>FeatureSubject</c>).
    /// </summary>
    public uint LastItemRowId { get; set; }

    /// <summary>Kind shown in the browser (index into <c>SubjectKind</c>).</summary>
    public int LastBrowserKind { get; set; }

    /// <summary>Race code ("0101", and so on) last picked in the character-feature browser.</summary>
    public string LastFeatureRace { get; set; } = "0101";

    // ── The modpack being built ──────────────────────────────────────────────
    // One pack at a time, kept here rather than in any mod folder: the build window turns it into a
    // mod, and the mod it builds is a plain Penumbra mod. Editing that mod afterwards (or any other)
    // is done on the mod itself and never comes back here.

    /// <summary>Material set-ups keyed by item row ID.</summary>
    public Dictionary<uint, List<MaterialSetup>> MaterialsByItem { get; set; } = new();

    /// <summary>Per-race model set-ups keyed by item row ID.</summary>
    public Dictionary<uint, List<RaceModelEntry>> ModelsByItem { get; set; } = new();

    /// <summary>Dummy-model material slot assignments keyed by item row ID.</summary>
    public Dictionary<uint, List<ModelMaterialSlot>> ModelMaterialSlotsByItem { get; set; } = new();

    /// <summary>Penumbra metadata overrides (EQP, EST) keyed by item row ID.</summary>
    public Dictionary<uint, ItemMeta> MetaByItem { get; set; } = new();

    /// <summary>Animation items' files and races, keyed by their emote or timeline key.</summary>
    public Dictionary<uint, AnimationSetup> AnimationsByItem { get; set; } = new();

    /// <summary>Subject keys included in the modpack currently being built, in display/build order.</summary>
    public List<uint> ModpackItems { get; set; } = new();

    /// <summary>The groups of options the modpack's mod has; what goes into each option is set on its items and files.</summary>
    public List<PackGroup> PackGroups { get; set; } = new();

    /// <summary>Whether the build window's left pane lists the modpack by option (default files, then each group's options) rather than item by item.</summary>
    public bool PackByOption { get; set; } = true;

    /// <summary>User-edited mod name for the whole pack. Falls back to an auto-generated name when empty.</summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>Description of the whole pack; empty is written as empty.</summary>
    public string ModDescription { get; set; } = string.Empty;

    /// <summary>
    /// Penumbra mod identifier for the pack's build (and the source of its group/option ids).
    /// Cleared once the build succeeds: a build only makes new mods, so the next one gets its own.
    /// </summary>
    public Guid? ModId { get; set; }

    // ── Defaults for new mods ────────────────────────────────────────────────

    /// <summary>Author written into mods built here.</summary>
    public string ModAuthor { get; set; } = string.Empty;

    /// <summary>Version written into mods built here.</summary>
    public string ModVersion { get; set; } = "1.0";

    /// <summary>Website written into mods built here.</summary>
    public string ModWebsite { get; set; } = string.Empty;

    // ── Mod windows ──────────────────────────────────────────────────────────

    /// <summary>Mod folders opened recently, newest first.</summary>
    public List<string> RecentProjects { get; set; } = new();

    /// <summary>Whether to redraw every character after each change written into an open mod, so it shows at once.</summary>
    public bool RedrawAfterBuild { get; set; }

    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>Width of a mod window's contents pane, at 100% UI scale.</summary>
    public float ContentsWidth { get; set; } = 330;

    /// <summary>Whether a mod window's contents pane lists the mod by option (default files, then each group's options) rather than by kind of file.</summary>
    public bool ContentsByOption { get; set; } = true;

    /// <summary>Size multiplier for tiles in the tree and the mod library.</summary>
    public float TileScale { get; set; } = 1f;

    /// <summary>Width of the modpack list (left of the build window), at 100% UI scale.</summary>
    public float BrowserWidth { get; set; } = 300;

    /// <summary>Width of the inspector pane, at 100% UI scale.</summary>
    public float InspectorWidth { get; set; } = 460;

    /// <summary>The build window's stage tab last shown (Browse, Details, Mod Info, Build).</summary>
    public int LastStage { get; set; } = (int)StageId.ModInfo;

    /// <summary>Height of the metadata section under the Details tree, at 100% UI scale.</summary>
    public float MetaPaneHeight { get; set; } = 220;

    // ── Defaults for new content ─────────────────────────────────────────────

    /// <summary>The compression new texture slots start on.</summary>
    public TextureCompression DefaultCompression { get; set; } = TextureCompression.None;

    /// <summary>Whether texture thumbnails are shown in the library and the inspectors.</summary>
    public bool ShowThumbnails { get; set; } = true;

    /// <summary>Last .package file opened in the Sims 4 importer.</summary>
    public string LastSimsPackagePath { get; set; } = string.Empty;

    /// <summary>Last folder the Sims 4 importer extracted into.</summary>
    public string LastSimsOutputDir { get; set; } = string.Empty;

    /// <summary>Folders of loose files opened in the import window's stash; remembered only, never written to.</summary>
    public List<string> StashOpenedFolders { get; set; } = new();

    [JsonIgnore]
    private Action? _save;

    /// <summary>Hands the settings the plugin's save, which writes the whole configuration.</summary>
    internal void AttachSave(Action save) => _save = save;

    public void Save() => _save?.Invoke();

    private volatile bool _saveSoon;

    /// <summary>Asks for a save on the next frame: workers don't save the configuration while the render thread may be changing it.</summary>
    public void SaveSoon() => _saveSoon = true;

    /// <summary>True once after <see cref="SaveSoon"/>. Render thread.</summary>
    internal bool TakeSaveRequest()
    {
        if (!_saveSoon)
            return false;
        _saveSoon = false;
        return true;
    }

    /// <summary>Files clean-up or repairs couldn't delete because something held them; deleted when they are free.</summary>
    /// <summary>Replaced whole, never changed in place (see <see cref="Services.CleanUp.PendingCleanup"/>), so a save reading it while a worker adds a file sees one list or the other.</summary>
    public List<PendingFile> PendingCleanup { get; set; } = new();

    /// <summary>Whether an item has any saved models or materials, regardless of modpack membership.</summary>
    public bool HasWork(uint key)
        => (MaterialsByItem.TryGetValue(key, out var m) && m.Count > 0)
        || (ModelsByItem.TryGetValue(key, out var r) && r.Any(x => !string.IsNullOrWhiteSpace(x.SourcePath) || x.UseVariants || x.UseDummy))
        || (AnimationsByItem.TryGetValue(key, out var a) && a.HasWork);

    /// <summary>Puts a folder at the top of the recent list.</summary>
    public void TouchRecent(string folder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > 12)
            RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
    }
}

/// <summary>A file of a mod that couldn't be deleted because something held it.</summary>
[Serializable]
public sealed class PendingFile
{
    public string ModFolder { get; set; } = string.Empty;
    public string RelPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime Since { get; set; }

    /// <summary>The file's size and last write when it was left; a file that differs later is something else and is kept. Null in older settings.</summary>
    public long? Size { get; set; }
    public DateTime? LastWriteUtc { get; set; }
}
