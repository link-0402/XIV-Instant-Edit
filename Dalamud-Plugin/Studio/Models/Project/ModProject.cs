using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Models.Project;

/// <summary>
/// One Penumbra mod opened in Edit mode: its folder, its meta.json, and this plugin's sidecar beside
/// it (empty when it has none). The render thread owns it; an update of the mod works on copies,
/// writes meta.json and the sidecar's ledger itself, and hands its result back.
///
/// meta.json can change underneath it — the user edits the mod in Penumbra while it is open here,
/// or an update writes it — so the file is watched, its timestamp is checked before every write and
/// on a slow tick, and a changed file is read again. Descriptive fields edited here and not yet
/// written are re-applied on top, so neither side's edit is lost.
/// </summary>
internal sealed class ModProject
{
    private (DateTime Stamp, long Length) _metaStamp;

    /// <summary>Top-level meta.json fields edited here since meta.json was last written.</summary>
    private readonly Dictionary<string, JToken?> _pendingFields = new(StringComparer.Ordinal);

    private ModProject(string folderPath, PenumbraModMeta meta, StudioProject sidecar)
    {
        FolderPath = folderPath;
        Meta       = meta;
        Sidecar    = sidecar;
        _metaStamp = MetaStamp();
    }

    public string FolderPath { get; private set; }
    public string FolderName => Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public PenumbraModMeta Meta    { get; private set; }
    public StudioProject      Sidecar { get; }

    public bool CreatedHere => Sidecar.CreatedHere;

    /// <summary>Bumped whenever the meta or the sidecar changes, so views know to refresh.</summary>
    public int Revision { get; private set; }

    public bool MetaDirty    { get; private set; }
    public bool SidecarDirty { get; private set; }

    /// <summary>Set when the ledger in <see cref="Sidecar"/> was set here and must be written too (normally an update writes it).</summary>
    private bool _ledgerDirty;

    /// <summary>Set when meta.json was written since the last <see cref="TakeMetaWritten"/>, so Penumbra can be asked to reload the mod.</summary>
    private bool _metaWritten;

    /// <summary>Why the sidecar could not be read, if it could not (a fresh one is used instead, and nothing is overwritten until the user edits).</summary>
    public string? SidecarError { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Opening and creating
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a mod folder. Throws when its meta.json cannot be read.</summary>
    public static ModProject Load(string folder)
    {
        var meta = PenumbraModMeta.Load(folder);
        StudioProject? sidecar = null;
        string? error = null;
        try
        {
            sidecar = StudioProjectStore.Load(folder);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var project = new ModProject(folder, meta, sidecar ?? new StudioProject()) { SidecarError = error, SidecarOnDisk = sidecar != null };
        if (sidecar is { ReleasedPorts: true })
            project.ForgetPortSetUps();
        else if (sidecar is { HadLocalPaths: true })
            project.SidecarDirty = true;   // an older sidecar with local paths in it: written again soon without them
        return project;
    }

    /// <summary>Whether opening it let go of port set-ups an older version kept in its sidecar (see <see cref="StudioProject.ReleasePortSetUps"/>).</summary>
    public bool ReleasedPorts { get; private set; }

    /// <summary>
    /// Writes the sidecar again without the port set-ups it was read with, before anything else reads
    /// it — or removes it, when nothing of the user's own is left in it, so the mod is a plain
    /// Penumbra mod again.
    /// </summary>
    private void ForgetPortSetUps()
    {
        ReleasedPorts = true;
        try
        {
            if (Sidecar.IsEmpty)
            {
                StudioProjectStore.Delete(FolderPath);
                SidecarOnDisk = false;
            }
            else
            {
                StudioProjectStore.Save(FolderPath, Sidecar);
            }
        }
        catch (Exception ex)
        {
            // Every reader lets go of them anyway; the file is written again with the next change.
            StudioServices.Log.Warning(ex, "[Studio] Could not rewrite the sidecar of {0}", FolderPath);
            MarkLedgerDirty();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Editing
    // ─────────────────────────────────────────────────────────────────────────

    public void MarkSidecarDirty()
    {
        SidecarDirty = true;
        Revision++;
    }

    /// <summary>The ledger was changed here (port set-ups let go of), not by an update: write it with the next flush.</summary>
    public void MarkLedgerDirty()
    {
        _ledgerDirty = true;
        MarkSidecarDirty();
    }

    /// <summary>Takes the ledger an update just wrote into the sidecar on disk.</summary>
    public void AdoptLedger(WriteLedger ledger)
    {
        Sidecar.Ledger = ledger;
        _ledgerDirty = false;
        Revision++;
    }

    /// <summary>
    /// Records a change to the remembered view state only: saved, but nothing in the mod changes for it.
    /// Looking at a mod never creates a sidecar in it — view state is only saved into one that exists,
    /// or along with the first real change.
    /// </summary>
    public void MarkUiStateDirty()
    {
        if (SidecarOnDisk)
            SidecarDirty = true;
    }

    /// <summary>Whether this mod folder has a sidecar file yet.</summary>
    public bool SidecarOnDisk { get; private set; }

    /// <summary>Edits top-level meta.json fields through <paramref name="edit"/>, remembering them so they survive a reload from disk.</summary>
    public void EditMeta(Action<PenumbraModMeta> edit, params string[] keys)
    {
        int before = Meta.Revision;
        edit(Meta);
        if (Meta.Revision == before)
            return;
        foreach (var key in keys)
            _pendingFields[key] = Meta.GetTopLevel(key);
        MetaDirty = true;
        Revision++;
    }

    /// <summary>
    /// Changes the structure of meta.json (an option's files, its groups) and writes it at once: read
    /// fresh from disk first, so an edit made in Penumbra meanwhile is not lost, then saved, so a later
    /// re-read cannot drop this edit (unlike descriptive fields, structure is not replayed).
    ///
    /// The edit is made on a copy that only becomes <see cref="Meta"/> once it is written: when
    /// meta.json cannot be written (locked by Penumbra or a scanner, read-only, a full disk) or the
    /// edit throws, this throws and the open mod is left exactly as it was.
    /// </summary>
    public void EditStructure(Action<PenumbraModMeta> edit)
    {
        lock (ModFiles.Gate)
        {
            RefreshFromDisk();
            var edited = Meta.Clone();
            int before = edited.Revision;
            edit(edited);
            if (edited.Revision == before)
                return;
            edited.Save(FolderPath, backupFirst: !CreatedHere);
            Meta = edited;
            _pendingFields.Clear();
            MetaDirty    = false;
            _metaWritten = true;
            _metaStamp   = MetaStamp();
            Revision++;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Files the mod still needs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every file of the mod folder something still needs, as comparison keys: what meta.json points
    /// at (except the entry <paramref name="skipOption"/> / <paramref name="skipGamePath"/>, when
    /// given), and what the ledger says this plugin wrote or will point an entry back at once its
    /// replacement goes. With <paramref name="readDisk"/> the ledger on disk counts too (an update
    /// may have written a newer one). Nothing in it is ever deleted.
    /// </summary>
    public HashSet<string> RelKeysInUse(OptionRef? skipOption = null, string? skipGamePath = null, bool readDisk = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Meta.AllFiles())
        {
            if (file.IsAbsolute)
                continue;
            if (skipOption is { } option && file.Option == option && skipGamePath != null && PathNormalizer.SameGamePath(file.GamePath, skipGamePath))
                continue;
            keys.Add(PathNormalizer.RelKey(file.RelPath));
        }

        var ledgers = new List<WriteLedger> { Sidecar.Ledger };
        if (readDisk && StudioProjectStore.LoadLedger(FolderPath) is { } onDisk)
            ledgers.Add(onDisk);
        foreach (var file in ledgers.SelectMany(l => l.Files))
        {
            if (!string.IsNullOrWhiteSpace(file.RelPath))
                keys.Add(PathNormalizer.RelKey(file.RelPath));
            if (!string.IsNullOrWhiteSpace(file.OriginalRelPath))
                keys.Add(PathNormalizer.RelKey(file.OriginalRelPath));
        }
        return keys;
    }

    /// <summary>
    /// Deletes those of <paramref name="rels"/> (paths inside the mod folder) that nothing still needs
    /// (see <see cref="RelKeysInUse"/>). Returns how many were deleted; <paramref name="failed"/> hears
    /// of each one that could not be.
    /// </summary>
    public int DeleteUnused(IEnumerable<string> rels, Action<string, Exception>? failed = null)
    {
        var inUse = RelKeysInUse(readDisk: true);
        int deleted = 0;
        foreach (var rel in rels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathNormalizer.IsAbsolute(rel) || !PathNormalizer.IsSafeRel(rel) || inUse.Contains(PathNormalizer.RelKey(rel)))
                continue;
            try
            {
                var full = PathNormalizer.ToDiskPath(FolderPath, rel);
                if (!File.Exists(full))
                    continue;
                File.Delete(full);
                deleted++;
            }
            catch (Exception ex)
            {
                failed?.Invoke(rel, ex);
            }
        }
        return deleted;
    }

    /// <summary>What <see cref="ForgetOptions"/> changed.</summary>
    public readonly record struct ForgetResult(int DroppedReplacements, int Deleted, string? SaveError);

    /// <summary>
    /// After options left meta.json (already written): what was set up here for them goes too — their
    /// replacements, added items and ledger entries — and whatever was addressed by position follows
    /// the options that moved up. The ledger is written at once (the next update reads it from disk);
    /// only then are <paramref name="deleteFiles"/>, the files they carried, deleted, and only those
    /// nothing else still needs. Nothing is deleted when the ledger could not be written.
    /// </summary>
    public ForgetResult ForgetOptions(IReadOnlyCollection<OptionRef> gone, IReadOnlyDictionary<OptionRef, OptionRef> moved,
        IReadOnlyCollection<string> deleteFiles, Action<string, Exception>? failed = null)
    {
        string? Moved(string serialized)
            => moved.TryGetValue(OptionRef.Parse(serialized), out var now) ? now.Serialize() : null;

        int dropped = Sidecar.Replacements.RemoveAll(r => gone.Contains(OptionRef.Parse(r.Option)));
        int items = Sidecar.AddedItems.RemoveAll(i => gone.Contains(OptionRef.Parse(i.Option)));
        bool changed = dropped + items > 0;
        foreach (var replacement in Sidecar.Replacements)
            if (Moved(replacement.Option) is { } now) { replacement.Option = now; changed = true; }
        foreach (var item in Sidecar.AddedItems)
            if (Moved(item.Option) is { } now) { item.Option = now; changed = true; }

        // The removed options' entries go before the others move up: left behind, they would sit at
        // the address of the option that takes their place.
        bool ledger = Sidecar.Ledger.Files.RemoveAll(f => gone.Contains(OptionRef.Parse(f.Option))) > 0;
        foreach (var file in Sidecar.Ledger.Files)
            if (Moved(file.Option) is { } now) { file.Option = now; ledger = true; }

        string? saveError = null;
        if (ledger)
        {
            MarkLedgerDirty();
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                saveError = ex.Message;   // kept dirty: written with the next flush
            }
        }
        else if (changed)
        {
            MarkSidecarDirty();
        }

        int deleted = saveError == null && deleteFiles.Count > 0 ? DeleteUnused(deleteFiles, failed) : 0;
        return new ForgetResult(dropped, deleted, saveError);
    }

    /// <summary>Points the project at a folder Penumbra moved it to.</summary>
    public void Relocate(string newFolder)
    {
        FolderPath = newFolder;
        _metaStamp = MetaStamp();
        Revision++;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what changed: meta.json read fresh first, with the fields edited here put on top; the
    /// sidecar's set-ups, keeping the ledger the file on disk has (an update writes that itself).
    /// </summary>
    public void Flush()
    {
        if (MetaDirty)
        {
            lock (ModFiles.Gate)
            {
                RefreshFromDisk();
                Meta.Save(FolderPath, backupFirst: !CreatedHere);
                _pendingFields.Clear();
                MetaDirty    = false;
                _metaWritten = true;
                _metaStamp   = MetaStamp();
            }
        }

        if (SidecarDirty)
        {
            // A sidecar that could not be read is replaced whole once something is changed here.
            StudioProjectStore.SaveSetUps(FolderPath, Sidecar, withLedger: _ledgerDirty || SidecarError != null);
            SidecarDirty  = false;
            SidecarError  = null;
            SidecarOnDisk = true;
            _ledgerDirty  = false;
        }
    }

    /// <summary>True once after meta.json was written, so Penumbra can be told to reload the mod.</summary>
    public bool TakeMetaWritten()
    {
        bool written = _metaWritten;
        _metaWritten = false;
        return written;
    }

    /// <summary>
    /// Reads meta.json again if it changed on disk since this project last read or wrote it (or
    /// whatever its timestamp says, with <paramref name="force"/>), putting pending edits back on
    /// top. Returns whether it was re-read.
    /// </summary>
    public bool RefreshFromDisk(bool force = false)
    {
        var stamp = MetaStamp();
        if (stamp == _metaStamp && !force)
            return false;

        PenumbraModMeta fresh;
        try
        {
            fresh = PenumbraModMeta.Load(FolderPath);
        }
        catch (Exception)
        {
            return false;   // mid-write by someone else, or damaged: keep what is open and look again later
        }

        foreach (var (key, value) in _pendingFields)
            fresh.SetTopLevel(key, value);
        Meta = fresh;
        _metaStamp = stamp;
        Revision++;
        return true;
    }

    private (DateTime, long) MetaStamp()
    {
        try
        {
            var info = new FileInfo(Path.Combine(FolderPath, PenumbraModMeta.FileName));
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1);
        }
        catch (Exception)
        {
            return (default, -1);
        }
    }

    public bool FolderExists => Directory.Exists(FolderPath);
}
