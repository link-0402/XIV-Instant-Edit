using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Models.Project;

/// <summary>
/// What Edit mode keeps about one mod beyond what Penumbra's meta.json can hold: the resources the
/// user is replacing or adding from files of their own, and a ledger of what it last wrote into the
/// mod, so each update only rewrites what a replacement changed and tidies up after itself, without
/// ever touching anything it did not write or anything changed elsewhere since.
///
/// Stored as <see cref="FileName"/> inside the mod folder, so it travels with the mod. Penumbra
/// ignores it (it lists it among the folder's unused files). A mod only has one while something of
/// the user's own is in it; Build mode never writes one. The local files the replacements come from
/// are not written into it (only their file names): their paths stay on this PC, in
/// <see cref="LocalSources"/>, so a shared mod carries no user name or folder layout.
/// </summary>
[Serializable]
public class StudioProject
{
    public const string FileName = ".instant-edit-project.json";

    /// <summary>2 since port set-ups live in Build mode only; 1 could also hold them (see <see cref="ReleasePortSetUps"/>).</summary>
    public int FormatVersion { get; set; } = 2;

    /// <summary>Whether XIV Instant Edit made this mod folder, so writing into it needs no backup first.</summary>
    public bool CreatedHere { get; set; }

    /// <summary>
    /// Names the file in Instant Edit's config folder that keeps the local source paths of this
    /// sidecar's replacements (see <see cref="LocalSources"/>). Random, so it says nothing about the
    /// user; it stays with the folder through a rename. Null until the sidecar is first written.
    /// </summary>
    public Guid? LocalId { get; set; }

    /// <summary>Resources of the mod being replaced, or added, from local files.</summary>
    public List<ResourceReplacement> Replacements { get; set; } = new();

    /// <summary>
    /// What the mod was last updated with. Written by the update itself (see
    /// <see cref="StudioProjectStore.SaveLedger"/>); saving the set-ups keeps whatever is on disk.
    /// </summary>
    public WriteLedger Ledger { get; set; } = new();

    /// <summary>
    /// Gear and character features added to the mod from the game: their game files are listed in the
    /// mod's tree so they can be replaced, made or edited like the mod's own. Only what the game item
    /// is and where its files go is kept — whatever is put there is an ordinary replacement.
    /// </summary>
    public List<AddedItem> AddedItems { get; set; } = new();

    public ProjectUiState Ui { get; set; } = new();

    // ── Read from older sidecars only ────────────────────────────────────────
    // Test builds of 0.3 made new mods in Edit mode and kept their port set-ups here ("Items", and
    // "Stash" for removed ones), mapping the mod's files to local source files for good. They are
    // read only so that they can be let go of; nothing writes them again.

    [JsonProperty("Items")]
    public JToken? LegacyItems { get; set; }

    [JsonProperty("Stash")]
    public JToken? LegacyStash { get; set; }

    /// <summary>Set when reading let go of port set-ups (see <see cref="ReleasePortSetUps"/>), so the file is written again without them.</summary>
    [JsonIgnore]
    public bool ReleasedPorts { get; private set; }

    /// <summary>Set when the file read still held local paths of this PC (written before they were kept in <see cref="LocalSources"/>), so it is written again without them.</summary>
    [JsonIgnore]
    public bool HadLocalPaths { get; internal set; }

    /// <summary>Whether there is nothing left worth keeping: no replacement, and nothing written by one.</summary>
    [JsonIgnore]
    public bool IsEmpty => Replacements.Count == 0 && Ledger.Files.Count == 0 && AddedItems.Count == 0;

    /// <summary>
    /// Lets go of port set-ups an older sidecar holds: their local source paths are forgotten, and
    /// what they wrote becomes the mod's own — dropped from the ledger, so no update ever takes it
    /// back out of the mod. Only what the replacements wrote stays recorded (the groups and
    /// manipulations older ledgers listed were written by ports alone, and are no longer read).
    /// Returns whether there was anything to let go of.
    /// </summary>
    public bool ReleasePortSetUps()
    {
        if (LegacyItems == null && LegacyStash == null)
            return false;
        LegacyItems = null;
        LegacyStash = null;

        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var replacement in Replacements)
        {
            var option = OptionRef.Parse(replacement.Option);
            owned.Add(WriteConflict.KeyFor(option, replacement.GamePath));
            if (replacement.Authored is { } material)
                foreach (var tex in material.Textures)
                    owned.Add(WriteConflict.KeyFor(option, ResourceNaming.SlotTexturePath(replacement.GamePath, tex)));
        }
        Ledger.Files.RemoveAll(f => !owned.Contains(WriteConflict.KeyFor(OptionRef.Parse(f.Option), f.GamePath)));
        FormatVersion = 2;
        ReleasedPorts = true;
        return true;
    }
}

/// <summary>
/// One resource of the mod being replaced or added from a local file. Textures are converted as they
/// are written into the mod (a .tex is copied as it is); every other kind of file is copied in
/// unchanged. Kept for as long as it is in use, so the mod follows when the local file changes.
/// </summary>
[Serializable]
public class ResourceReplacement
{
    /// <summary>The game path the file is served at.</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>Which option carries it (the persisted form of an OptionRef; empty = the mod's default files).</summary>
    public string Option { get; set; } = string.Empty;

    /// <summary>The local file it comes from.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>What an image is compressed to. Ignored for a .tex source and for anything that is not a texture.</summary>
    public TextureCompression Compression { get; set; } = TextureCompression.Auto;

    /// <summary>True when the option did not have this game path before: it is added rather than repointed.</summary>
    public bool IsNew { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A material made here rather than taken from a file: its shader and texture slots (whose
    /// images are converted beside it). Built from a bundled preset, or from the game's own material
    /// at this path when <see cref="CloneGameMaterial"/> is set. <see cref="SourcePath"/> is unused then.
    /// </summary>
    public MaterialSetup? Authored { get; set; }

    /// <summary>For an authored material: start from the game's own material at this path (or <see cref="TemplatePath"/>) instead of a preset.</summary>
    public bool CloneGameMaterial { get; set; }

    /// <summary>
    /// For an authored material: start from the mod's own material at this path (or
    /// <see cref="TemplatePath"/>) — what the option has there, else the game's — keeping its shader,
    /// colour table and settings. How a material of the mod is edited here.
    /// </summary>
    public bool CloneModMaterial { get; set; }

    /// <summary>For an authored material cloned from another one: that material's game path. Empty = its own path.</summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// For a model: the material each of its material slots is pointed at instead (as a model names
    /// one, "/mt_c0201e6114_top_a.mtrl"), by slot; an empty entry keeps the slot's own. Applied to
    /// <see cref="SourcePath"/> when one is set, else to the mod's own model at this path (or the
    /// game's) — which is how a model of the mod gets its materials reassigned.
    /// </summary>
    public List<string> MaterialNames { get; set; } = new();

    public bool IsAuthored => Authored != null;

    /// <summary>Whether any of the model's material slots is pointed somewhere else.</summary>
    public bool ReassignsMaterials => MaterialNames.Any(n => !string.IsNullOrWhiteSpace(n));

    /// <summary>A model that only has its materials reassigned: no file of the user's own.</summary>
    public bool OnlyReassigns => Authored == null && string.IsNullOrWhiteSpace(SourcePath) && ReassignsMaterials;

    /// <summary>The game path an authored clone starts from.</summary>
    public string TemplateGamePath => string.IsNullOrWhiteSpace(TemplatePath) ? GamePath : TemplatePath;

    public ResourceReplacement Clone()
    {
        var copy = (ResourceReplacement)MemberwiseClone();
        copy.Authored      = Authored?.Clone();
        copy.MaterialNames = MaterialNames.ToList();
        return copy;
    }
}

/// <summary>
/// A piece of gear or a character feature added to a mod from the game: which one, the option its
/// files go into, and the game paths of its models (one per race that has its own). Its models are
/// listed in the mod's tree, with the game's materials and textures under them, until files of the
/// user's own take their place.
/// </summary>
[Serializable]
public class AddedItem
{
    /// <summary>The subject's key (gear: the item's row id; features: see FeatureSubject).</summary>
    public uint Key { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Which option its files go into (persisted OptionRef; empty = the mod's default files).</summary>
    public string Option { get; set; } = string.Empty;

    /// <summary>The game paths of its models.</summary>
    public List<string> Models { get; set; } = new();

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What XIV Instant Edit last wrote into the mod, and what the replacements asked for at the time.
/// An update compares against it: what a replacement still asks for the same way is left as the mod
/// has it, however it was changed since; what no replacement makes any more is taken back out.
/// </summary>
[Serializable]
public class WriteLedger
{
    /// <summary>When the mod was last updated from the replacements.</summary>
    public DateTime? UpdatedAt { get; set; }

    public List<LedgerFile> Files { get; set; } = new();

    public WriteLedger Clone() => new()
    {
        UpdatedAt = UpdatedAt,
        Files     = Files.Select(f => f.Clone()).ToList(),
    };
}

/// <summary>One file this plugin wrote into the mod, and the meta.json entry pointing at it.</summary>
[Serializable]
public class LedgerFile
{
    public string GamePath { get; set; } = string.Empty;
    public string RelPath  { get; set; } = string.Empty;

    /// <summary>The option whose entry points at it (persisted OptionRef; empty = default files).</summary>
    public string Option { get; set; } = string.Empty;

    /// <summary>The file's hash as this plugin last wrote it. A file is only ever overwritten or deleted while it still has this hash.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>What the entry pointed at before this plugin repointed it — restored when the replacement goes. Null when the entry was added.</summary>
    public string? OriginalRelPath { get; set; }

    /// <summary>
    /// What the replacement asked for when the file was last written: a key over its source file and
    /// settings, or over what it produced. An update with the same key has nothing new to write.
    /// Null for files written by versions that kept no key: their bytes are compared instead.
    /// </summary>
    public string? Intent { get; set; }

    /// <summary>Size and timestamp of the file right after it was written, to notice a change made elsewhere without reading it (-1 / 0 when unknown).</summary>
    public long Length { get; set; } = -1;
    public long WrittenTicks { get; set; }

    /// <summary>
    /// The file was changed elsewhere (in Penumbra's editor, say) after it was written here, and that
    /// change is kept: a material made here gets later changes carried over onto it, anything else is
    /// only replaced again when the user says so.
    /// </summary>
    public bool EditedElsewhere { get; set; }

    /// <summary>
    /// The file was written, but meta.json could not be pointed at it then (it couldn't be read or
    /// saved): the next update points the entry at it, even when the file itself has nothing new.
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool EntryNotPointed { get; set; }

    /// <summary>For a material made here: its shader and the texture of each role, so changes made here can be carried over onto a copy edited elsewhere.</summary>
    public LedgerMaterial? Material { get; set; }

    public LedgerFile Clone()
    {
        var copy = (LedgerFile)MemberwiseClone();
        copy.Material = Material?.Clone();
        return copy;
    }
}

/// <summary>A material as it was made here: the shader package, and a texture game path per role.</summary>
[Serializable]
public class LedgerMaterial
{
    public string Shader { get; set; } = string.Empty;
    public Dictionary<string, string> Textures { get; set; } = new();

    public LedgerMaterial Clone() => new() { Shader = Shader, Textures = new Dictionary<string, string>(Textures) };
}

/// <summary>Per-mod view state, so reopening a mod shows it as it was left.</summary>
[Serializable]
public class ProjectUiState
{
    /// <summary>Id of the left-pane entry last selected.</summary>
    public string? SelectedRoot { get; set; }

    /// <summary>Tree nodes the user folded away.</summary>
    public List<string> Collapsed { get; set; } = new();
}

/// <summary>
/// Reads and writes <see cref="StudioProject"/> sidecars. Two writers share one: the render thread
/// saves the replacements, and an update of the mod (on a worker) saves its ledger. Each only
/// replaces its own part of the file on disk, under <see cref="ModFiles.Gate"/>, so neither undoes
/// the other. Port set-ups an older sidecar still holds are let go of as it is read, by every reader.
/// </summary>
internal static class StudioProjectStore
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting             = Formatting.Indented,
        ContractResolver       = new WritableOnlyResolver(),
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        NullValueHandling      = NullValueHandling.Ignore,
        MissingMemberHandling  = MissingMemberHandling.Ignore,
    };

    public static string PathIn(string folder) => Path.Combine(folder, StudioProject.FileName);

    public static bool Exists(string folder) => File.Exists(PathIn(folder));

    /// <summary>Reads the sidecar of a mod folder; null when there is none. Throws on a file that is not a sidecar.</summary>
    public static StudioProject? Load(string folder)
    {
        var path = PathIn(folder);
        if (!File.Exists(path))
            return null;
        var project = JsonConvert.DeserializeObject<StudioProject>(File.ReadAllText(path), Settings)
            ?? throw new InvalidDataException("empty project file");
        project.ReleasePortSetUps();
        project.HadLocalPaths = LocalSources.HasInlinePaths(project);
        LocalSources.Restore(project);
        return project;
    }

    /// <summary>Removes a sidecar that holds nothing any more, so the mod is a plain Penumbra mod again.</summary>
    public static void Delete(string folder)
    {
        lock (ModFiles.Gate)
        {
            var path = PathIn(folder);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>Writes the whole sidecar, ledger included: for a new mod folder, or a copy of one.</summary>
    public static void Save(string folder, StudioProject project)
    {
        lock (ModFiles.Gate)
            Write(folder, project);
    }

    /// <summary>
    /// Writes the set-ups, keeping the ledger the sidecar on disk has: an update may have written a
    /// newer one than this copy holds. With <paramref name="withLedger"/> (or when the file on disk
    /// cannot be read), this copy's ledger is written too.
    /// </summary>
    public static void SaveSetUps(string folder, StudioProject project, bool withLedger)
    {
        lock (ModFiles.Gate)
        {
            var onDisk = withLedger ? null : TryLoad(folder);
            if (onDisk == null)
            {
                Write(folder, project);
                return;
            }

            var ours = project.Ledger;
            try
            {
                project.Ledger = onDisk.Ledger;
                Write(folder, project);
            }
            finally
            {
                project.Ledger = ours;
            }
        }
    }

    /// <summary>
    /// Writes a new ledger into the sidecar on disk, keeping everything else as the file has it now
    /// (the set-ups may have been saved since <paramref name="fallback"/> was taken). A sidecar that
    /// is missing or cannot be read is written whole from <paramref name="fallback"/>.
    /// </summary>
    public static void SaveLedger(string folder, WriteLedger ledger, StudioProject fallback)
    {
        lock (ModFiles.Gate)
        {
            var project = TryLoad(folder) ?? DeepClone(fallback);
            project.Ledger = ledger;
            Write(folder, project);
        }
    }

    /// <summary>The ledger the sidecar on disk has, or null when there is no readable one.</summary>
    public static WriteLedger? LoadLedger(string folder)
    {
        lock (ModFiles.Gate)
            return TryLoad(folder)?.Ledger;
    }

    private static StudioProject? TryLoad(string folder)
    {
        try
        {
            return Load(folder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the sidecar: to a temporary file first, then moved over the old one. The local source
    /// paths go into <see cref="LocalSources"/> first and the sidecar only keeps their file names;
    /// when they cannot be kept there, they stay in the sidecar rather than be lost.
    /// </summary>
    private static void Write(string folder, StudioProject project)
    {
        Directory.CreateDirectory(folder);
        var path = PathIn(folder);
        var written = project;
        if (LocalSources.Root is { } root)
        {
            project.LocalId ??= ExistingLocalId(path) ?? Guid.NewGuid();
            var stripped = DeepClone(project);
            var sources = LocalSources.Take(stripped);
            try
            {
                LocalSources.Save(root, project.LocalId.Value, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), sources);
                written = stripped;
            }
            catch (Exception)
            {
                // Kept in the sidecar this time; moved out with the next write that can.
            }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(written, Settings), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The <see cref="StudioProject.LocalId"/> the sidecar on disk has, so a copy written without one keeps it.</summary>
    private static Guid? ExistingLocalId(string path)
    {
        try
        {
            return File.Exists(path) && JObject.Parse(File.ReadAllText(path))["LocalId"]?.ToString() is { } text && Guid.TryParse(text, out var id) ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static StudioProject DeepClone(StudioProject project)
        => JsonConvert.DeserializeObject<StudioProject>(JsonConvert.SerializeObject(project, Settings), Settings)!;

    /// <summary>Whether two ledgers say the same thing, whenever they were written — so an update that changed nothing leaves the sidecar alone.</summary>
    public static bool SameLedger(WriteLedger a, WriteLedger b)
    {
        static string Text(WriteLedger ledger)
        {
            var copy = ledger.Clone();
            copy.UpdatedAt = null;
            return JsonConvert.SerializeObject(copy, Settings);
        }
        return Text(a) == Text(b);
    }

    /// <summary>Leaves out read-only conveniences (IsAuthored, RaceGender, IsShared …): only what can be read back is written.</summary>
    private sealed class WritableOnlyResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (!property.Writable)
                property.ShouldSerialize = _ => false;
            return property;
        }
    }
}

/// <summary>
/// The local files a mod's replacements are made from (a replacement's file, each image of a material
/// made here), kept on this PC instead of in the mod's sidecar: one small file per sidecar in
/// Instant Edit's config folder, named by <see cref="StudioProject.LocalId"/>. The sidecar keeps each
/// file's name only, so a mod exported from Penumbra carries no user name or folder layout.
///
/// Keyed by the sidecar's own id rather than the mod folder's name or meta.json's Identifier: the id
/// stays with the folder when Penumbra renames it, every sidecar has one (many mods have no
/// Identifier), and a copy of the mod keeps following the same files, as it did when the paths were
/// in the sidecar. A sidecar whose files are not on this PC (a mod from someone else) keeps the bare
/// names: the replacements stay, their files are only not found, so the mod keeps what was written.
///
/// Durable user data: never in %TEMP%, never cleaned up. Written under <see cref="ModFiles.Gate"/>.
/// </summary>
internal static class LocalSources
{
    public const string FolderName = "mod-sources";

    private static readonly object Sync = new();
    private static string? _root;
    private static bool _resolved;

    /// <summary>
    /// The folder the files are kept in, under the plugin's config folder; null while it is not known
    /// (local paths then stay in the sidecar, as before). Tests set it.
    /// </summary>
    public static string? Root
    {
        get
        {
            lock (Sync)
            {
                if (!_resolved)
                    _root = Resolve(out _resolved);
                return _root;
            }
        }
        set
        {
            lock (Sync)
            {
                _root     = value;
                _resolved = true;
            }
        }
    }

    private static string? Resolve(out bool final)
    {
        try
        {
            var dir = ConfigDirectory();
            final = dir != null;
            return dir != null ? Path.Combine(dir, FolderName) : null;
        }
        catch (Exception)
        {
            final = true;   // no Dalamud to ask (offline tools and tests)
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ConfigDirectory() => StudioServices.PluginInterface?.ConfigDirectory.FullName;

    /// <summary>Whether a sidecar still holds local paths of this PC (an older one), so it is written again without them.</summary>
    public static bool HasInlinePaths(StudioProject project)
    {
        if (Root == null)
            return false;
        bool any = false;
        Visit(project, (_, path) =>
        {
            any |= IsLocal(path);
            return path;
        });
        return any;
    }

    /// <summary>Takes the local paths out of <paramref name="project"/> (a copy about to be written), leaving their file names; returns them by entry (null: nothing to keep for it).</summary>
    public static Dictionary<string, string?> Take(StudioProject project)
    {
        var sources = new Dictionary<string, string?>(StringComparer.Ordinal);
        Visit(project, (key, path) =>
        {
            if (!IsLocal(path))
            {
                sources[key] = null;
                return path;
            }
            sources[key] = path;
            return NameOf(path);
        });
        return sources;
    }

    /// <summary>Puts the local paths kept for a sidecar back in place of the file names it holds. Quiet when there is nothing kept for it.</summary>
    public static void Restore(StudioProject project)
    {
        if (project.LocalId is not { } id || Root is not { } root || Read(FileFor(root, id)) is not { } file)
            return;
        Visit(project, (key, path) =>
            !IsLocal(path) && !string.IsNullOrWhiteSpace(path)
            && file.Sources.TryGetValue(key, out var full) && !string.IsNullOrWhiteSpace(full)
            && string.Equals(NameOf(full), path, StringComparison.OrdinalIgnoreCase)
                ? full
                : path);
    }

    /// <summary>
    /// Records the paths of a sidecar's entries. Entries of the file that this sidecar does not have
    /// are kept: a copy of the mod shares the file.
    /// </summary>
    public static void Save(string root, Guid id, string folderName, Dictionary<string, string?> sources)
    {
        var path = FileFor(root, id);
        var file = Read(path) ?? new LocalSourceFile();
        foreach (var (key, source) in sources)
        {
            if (source == null)
                file.Sources.Remove(key);
            else
                file.Sources[key] = source;
        }
        if (file.Sources.Count == 0 && !File.Exists(path))
            return;
        file.Folder = folderName;

        Directory.CreateDirectory(root);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static string FileFor(string root, Guid id) => Path.Combine(root, $"{id:N}.json");

    private static LocalSourceFile? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<LocalSourceFile>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Calls <paramref name="map"/> with each local path field of the sidecar and its key, and sets the field to what it returns.</summary>
    private static void Visit(StudioProject project, Func<string, string, string> map)
    {
        foreach (var replacement in project.Replacements)
        {
            var key = $"{replacement.Option}|{PathNormalizer.GamePath(replacement.GamePath)}";
            replacement.SourcePath = map(key, replacement.SourcePath);
            if (replacement.Authored is not { } material)
                continue;
            for (int i = 0; i < material.Textures.Count; i++)
                material.Textures[i].SourcePath = map($"{key}|{i}|{material.Textures[i].Type}", material.Textures[i].SourcePath);
        }
    }

    private static bool IsLocal(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    /// <summary>What the sidecar keeps of a local path: the file (or folder) name.</summary>
    private static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : "source";
    }

    private sealed class LocalSourceFile
    {
        /// <summary>The mod folder's name when last written, to tell the files apart by hand.</summary>
        public string Folder { get; set; } = string.Empty;

        public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);
    }
}
