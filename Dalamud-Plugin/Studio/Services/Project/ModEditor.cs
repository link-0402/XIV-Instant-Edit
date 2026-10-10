using System;
using System.IO;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// One mod open in a mod window, and everything that works on it: the session the views edit, the
/// sync that writes their changes into the mod, the resource graph built from its files, how the
/// player has it set in Penumbra, and a watch on its folder for changes made elsewhere.
///
/// Closing it lets go of the mod at once. Changes not yet written are still written afterwards:
/// the host keeps updating a closed editor until its sync has nothing left (<see cref="Finished"/>).
/// </summary>
internal sealed class ModEditor : IDisposable
{
    /// <summary>How long the mod's folder must be quiet before changes in it are read.</summary>
    private static readonly TimeSpan WatchQuiet = TimeSpan.FromMilliseconds(400);

    /// <summary>How often the mod's files are looked at when its folder cannot be watched.</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    private readonly ModFolderWatcher _watcher = new();
    private DateTime _nextPoll;

    private Task? _copying;
    private string _copyFolder = string.Empty;

    public StudioHost Host { get; }
    public ModSession Session { get; }
    public ModSync Sync { get; }
    public ResourceGraphService Graphs { get; }

    /// <summary>The Check view's check of the mod, its report and the repairs and clean-up started from it.</summary>
    public InstantEdit.Studio.Services.Check.CheckSession Check { get; } = new();

    /// <summary>How the mod is set for the player in Penumbra (enabled, which options).</summary>
    public ActiveSettings Settings { get; }

    /// <summary>The mod, as it was opened; it stays the same object when Penumbra renames its folder.</summary>
    public ModProject Project { get; }

    private ModEditor(StudioHost host, ModProject project)
    {
        Host     = host;
        Project  = project;
        Session  = new ModSession(this);
        Sync     = new ModSync(this, Session);
        Graphs   = new ResourceGraphService(host);
        Settings = new ActiveSettings(host.PenumbraIpc);

        _watcher.Watch(project.FolderPath);
        Session.Attach(project);
        Session.Focus(new Target(TargetKind.Project));   // opens on the overview

        if (project.SidecarError != null)
            Session.Notify($"This mod's {StudioProject.FileName} could not be read ({project.SidecarError}). It is left as it is until you change something here.", Severity.Warning);
    }

    /// <summary>Opens a mod folder. Null, with the reason, when it is not a mod that can be read.</summary>
    public static ModEditor? TryOpen(StudioHost host, string folder, out string error)
    {
        error = string.Empty;
        if (!Directory.Exists(folder))
        {
            error = "That folder does not exist.";
            return null;
        }
        if (!File.Exists(Path.Combine(folder, PenumbraModMeta.FileName)) && !LegacyMetaReader.HasLegacyLayout(folder, null))
        {
            error = "That folder is not a Penumbra mod (it has no meta.json).";
            return null;
        }

        try
        {
            return new ModEditor(host, ModProject.Load(folder));
        }
        catch (Exception ex)
        {
            error = $"Its meta.json could not be read: {ex.Message}";
            StudioServices.Log.Warning(ex, "[Studio] Could not open {0}", folder);
            return null;
        }
    }

    /// <summary>Whether the mod is still open here (not closed, and not deleted in Penumbra).</summary>
    public bool IsOpen => Session.Project != null;

    /// <summary>Closed, with nothing left to write into the mod (a repair or clean-up included): the host can let go of it.</summary>
    public bool Finished => !IsOpen && !Sync.Busy && _copying == null && !Check.Busy;

    /// <summary>The mod's name, or its folder's when it has none.</summary>
    public string Name => string.IsNullOrWhiteSpace(Project.Meta.Name) ? Project.FolderName : Project.Meta.Name;

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Called once per frame on the render thread, whether its window is drawn or not.</summary>
    public void Update()
    {
        var open = Session.Project;
        Settings.Update(open);
        FollowFolder();
        FinishCopy();

        // meta.json written from here (the mod's name or description edited): let Penumbra read it again.
        if (open != null && open.TakeMetaWritten() && Host.PenumbraIpc.IsAvailable)
            Host.PenumbraIpc.ReloadMod(open.FolderName);

        Sync.Update();
        Check.Update();
        Check.Watch(open);
        Graphs.Update(Session);
        Session.Graph = Graphs.Current;
        Session.FlushIfDue();
    }

    /// <summary>
    /// Reads the mod again when its folder changed (in Penumbra's editor, in Explorer, or by an
    /// update from here), so what is shown is the mod as it is on disk. Penumbra already knows about
    /// changes it made itself, so it is not asked to read the mod again for these.
    /// </summary>
    private void FollowFolder()
    {
        if (Session.Project is not { } project)
            return;

        if (_watcher.TakeChanges(WatchQuiet, out var meta, out _))
        {
            if (meta)
                project.RefreshFromDisk();
            Graphs.Refresh();
        }
        else if (_watcher.Failed && DateTime.UtcNow >= _nextPoll)
        {
            _nextPoll = DateTime.UtcNow + PollEvery;
            Graphs.Refresh();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Closing, and Penumbra's events
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Writes pending edits now.</summary>
    public void Flush() => Session.Flush();

    /// <summary>Closes the mod here, writing what is pending (the sync finishes it after the window is gone).</summary>
    public void Close()
    {
        Session.Detach();
        _watcher.Stop();
    }

    /// <summary>Penumbra deleted the mod: closed here, writing nothing more into it.</summary>
    public void OnDeleted()
    {
        var name = Project.FolderName;
        Session.Detach(flush: false);
        _watcher.Stop();
        StudioServices.Log.Information("[Studio] {0} was deleted in Penumbra, so its window was closed.", name);
    }

    /// <summary>Penumbra renamed the mod's folder to <paramref name="to"/>.</summary>
    public void OnMoved(string to)
    {
        var parent = Path.GetDirectoryName(Project.FolderPath.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        Project.Relocate(Path.Combine(parent, to));
        _watcher.Watch(Project.FolderPath);
        Host.Configuration.TouchRecent(Project.FolderPath);
        Host.Configuration.Save();
        Session.Notify($"Penumbra renamed the mod's folder to {to}.", Severity.Info);
    }

    public void Dispose()
    {
        // A repair or clean-up under way is given a moment to finish what it changed (and record it in the backups).
        Check.Cancel();
        Check.WaitForOperation(TimeSpan.FromSeconds(5));
        Sync.Dispose();
        Graphs.Dispose();
        _watcher.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Copying the mod
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a copy of the mod is being made.</summary>
    public bool IsCopying => _copying != null;

    /// <summary>Whether the mod can be copied now, and if not, why.</summary>
    public bool CanDuplicate(out string reason)
    {
        reason = !IsOpen                         ? "The mod is closed."
               : _copying != null                ? "A copy is being made."
               : Sync.Busy                       ? "Wait a moment, until every change is written into the mod."
               : !Host.PenumbraIpc.IsAvailable   ? "Penumbra is not available."
               : string.Empty;
        return reason.Length == 0;
    }

    /// <summary>
    /// Copies the mod (as it is on disk, every change written) into a new folder beside it
    /// ("Name (copy)"), then has Penumbra add it and opens the copy in a window of its own. The
    /// original stays as it is.
    /// </summary>
    public void Duplicate()
    {
        if (!CanDuplicate(out var reason))
        {
            Session.Notify(reason, Severity.Warning);
            return;
        }

        Session.Flush();
        var root = Path.GetDirectoryName(Project.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(root))
        {
            Session.Notify("The mod's folder has no parent to put a copy in.", Severity.Error);
            return;
        }

        var source = Project.FolderPath;
        var target = Path.Combine(root, CopyFolderName(root, Project.FolderName));
        _copyFolder = target;
        _copying = Task.Run(() => CopyMod(source, target));
        Session.Notify($"Copying the mod to {Path.GetFileName(target)}.", Severity.Info);
    }

    private void FinishCopy()
    {
        if (_copying is not { IsCompleted: true } copy)
            return;

        _copying = null;
        var name = Path.GetFileName(_copyFolder);
        if (!copy.IsCompletedSuccessfully)
        {
            Session.Notify($"The mod could not be copied: {copy.Exception?.GetBaseException().Message}", Severity.Error);
            return;
        }

        var added = Host.PenumbraIpc.AddMod(name);
        Host.Library.MarkStale();
        if (!Host.OpenMod(_copyFolder, out var error))
            Session.Notify($"Copied the mod to {name}, but the copy could not be opened: {error}", Severity.Warning);
        else if (added is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
            Session.Notify($"Copied the mod to {name} and opened the copy. The original is as it was.", Severity.Info);
        else
            Session.Notify($"Copied the mod to {name} and opened the copy. Penumbra did not pick it up ({added}); it will after a mod rediscovery.", Severity.Warning);
    }

    /// <summary>
    /// Copies a mod folder (leaving out half-written files and the meta.json backup), and makes the
    /// copy a mod of its own: a new id and a name that tells it apart.
    /// </summary>
    private static void CopyMod(string source, string target)
    {
        if (Directory.Exists(target))
            throw new IOException($"{Path.GetFileName(target)} already exists.");

        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                if (rel.EndsWith(".instant-edit-tmp", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(PenumbraModMeta.BackupSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }

            var meta = PenumbraModMeta.Load(target);
            meta.Identifier = Guid.NewGuid();
            meta.Name = $"{(string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(source) : meta.Name)} (copy)";
            meta.Save(target, backupFirst: false);

            // Replacements of the user's own go with the copy, which needs no backup of its meta.json
            // (the original keeps its own). A mod without any stays without a sidecar.
            StudioProject? sidecar;
            try
            {
                sidecar = StudioProjectStore.Load(target);
            }
            catch (Exception)
            {
                sidecar = null;
            }
            if (sidecar != null)
            {
                sidecar.CreatedHere = true;
                StudioProjectStore.Save(target, sidecar);
            }
        }
        catch (Exception)
        {
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
            catch (Exception)
            {
                // Left behind half-copied; Penumbra does not know it, and it can be deleted by hand.
            }
            throw;
        }
    }

    /// <summary>A folder name next to an existing mod for a copy of it ("Name (copy)", "Name (copy 2)", and so on).</summary>
    public static string CopyFolderName(string root, string folderName)
    {
        var candidate = $"{folderName} (copy)";
        for (int i = 2; Directory.Exists(Path.Combine(root, candidate)); i++)
            candidate = $"{folderName} (copy {i})";
        return candidate;
    }
}
