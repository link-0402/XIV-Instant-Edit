using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services;

/// <summary>Where the open mod stands against what is set up for it here.</summary>
internal enum SyncState
{
    /// <summary>No mod open.</summary>
    Idle,

    /// <summary>The mod has everything set up here.</summary>
    UpToDate,

    /// <summary>The replacements changed a moment ago; the mod is written once the edits settle.</summary>
    Waiting,

    /// <summary>The mod is being written.</summary>
    Writing,

    /// <summary>Files changed elsewhere that the replacements here want to change too: waiting for the user.</summary>
    Conflicts,

    /// <summary>The last update could not be done at all.</summary>
    Failed,
}

/// <summary>
/// Keeps a mod open in Edit mode in step with the files of the user's own replacing or adding some
/// of its resources. Every change is written into the mod a moment after the edits settle — and
/// again whenever one of those files is saved — on a worker (<see cref="ModWriter"/>), one update at
/// a time; Penumbra is told to read the mod again afterwards. Edit mode has nothing to build: what
/// is set up here is what the mod has. (Build mode builds a new mod in one go, see <see cref="BuildController"/>.)
///
/// An update only rewrites what a replacement changed since the last one, so whatever was changed
/// in the mod elsewhere (in Penumbra's editor, say) stays. Where both sides changed the same file
/// the update leaves it alone and lists it in <see cref="Conflicts"/> until the user picks a side
/// (<see cref="Resolve"/>).
///
/// A mod closed (or switched away from) with changes not yet written is updated after it closed:
/// its request is taken while the session still has its replacements (<see cref="Closing"/>).
/// </summary>
internal sealed class ModSync : IDisposable
{
    /// <summary>How long the replacements must be still before they are written.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);

    /// <summary>How often the replacements' local files are looked at for changes.</summary>
    private static readonly TimeSpan SourceCheckEvery = TimeSpan.FromSeconds(2);

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;

    private WriteJob? _running;

    /// <summary>The open mod the running update is for; null for a mod already closed.</summary>
    private ModProject? _runningFor;

    /// <summary>Updates of mods closed before their changes were written.</summary>
    private readonly Queue<WriteRequest> _queued = new();

    // The open mod, and how far the mod on disk has caught up with it.
    private ModProject? _project;
    private int _writtenRevision = int.MinValue;
    private string? _writtenSources;
    private string? _sources;
    private int _sourcesRevision = int.MinValue;
    private Task<string>? _sourceCheck;
    private DateTime _nextSourceCheck;
    private bool _retry;

    /// <summary>Choices made for conflicts, waiting for the next update; and those it is carrying out.</summary>
    private readonly Dictionary<string, ConflictChoice> _resolutions = new(StringComparer.Ordinal);
    private Dictionary<string, ConflictChoice> _sending = new(StringComparer.Ordinal);

    private readonly Dictionary<string, WriteConflict> _conflicts = new(StringComparer.Ordinal);
    private string? _toldError;
    private int _toldConflicts;

    public ModSync(ModEditor editor, ModSession session)
    {
        _host  = editor.Host;
        _editor = editor;
        _session = session;
    }

    /// <summary>The update running now, for the open mod or one closed before it was written.</summary>
    public WriteJob? Running => _running;

    /// <summary>Whether the update running now is the open mod's.</summary>
    public bool WritingOpenMod => _running != null && ReferenceEquals(_runningFor, _session.Project);

    /// <summary>The last finished update of the open mod.</summary>
    public WriteReport? LastReport { get; private set; }

    /// <summary>Files changed elsewhere that the replacements here want to change too, still waiting for a choice.</summary>
    public IReadOnlyCollection<WriteConflict> Conflicts => _conflicts.Values;

    public WriteConflict? ConflictFor(OptionRef option, string gamePath)
        => _conflicts.GetValueOrDefault(WriteConflict.KeyFor(option, gamePath));

    /// <summary>Whether there is anything not yet written into the open mod, or being written.</summary>
    public bool Busy => _running != null || _queued.Count > 0 || _session.Project != null && Due;

    public SyncState State
    {
        get
        {
            if (_session.Project == null)
                return SyncState.Idle;
            if (WritingOpenMod)
                return SyncState.Writing;
            if (Due)
                return SyncState.Waiting;
            if (LastReport?.FatalError != null)
                return SyncState.Failed;
            return _conflicts.Count > 0 ? SyncState.Conflicts : SyncState.UpToDate;
        }
    }

    /// <summary>
    /// Whether the open mod lacks something set up here: the set-ups changed, a source file was
    /// saved again, a conflict was decided, or the user asked to try again.
    /// </summary>
    private bool Due
        => _session.SetUpRevision != _writtenRevision
        || _resolutions.Count > 0
        || _retry
        || _sources != null && _sourcesRevision == _writtenRevision && _sources != _writtenSources;

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Called once per frame on the render thread.</summary>
    public void Update()
    {
        Finish();

        var project = _session.Project;
        if (!ReferenceEquals(project, _project))
            Follow(project);

        // Mods closed with changes still to write go first: nothing else will write them.
        if (_running == null && _queued.Count > 0)
        {
            _running = new WriteJob(_queued.Dequeue());
            _runningFor = null;
        }

        if (project == null)
            return;

        CheckSources();

        if (_running != null)
        {
            // Newer set-ups are waiting: stop this update before it writes, and write those instead.
            // A conversion under way finishes first and is kept, so little is lost.
            if (WritingOpenMod && !_running.Committing && _running.Request.Revision != _session.SetUpRevision
                && _session.SinceSetUpChange >= Settle)
                _running.Cancel();
            return;
        }

        if (!Due || _session.SetUpRevision != _writtenRevision && _session.SinceSetUpChange < Settle)
            return;
        Start(project);
    }

    /// <summary>Starts watching a newly opened mod (or none): it gets one update straight away, writing whatever its set-ups have that it lacks.</summary>
    private void Follow(ModProject? project)
    {
        _project          = project;
        _writtenRevision  = int.MinValue;
        _writtenSources   = null;
        _sources          = null;
        _sourcesRevision  = int.MinValue;
        _sourceCheck      = null;
        _nextSourceCheck  = default;
        _retry            = false;
        _resolutions.Clear();
        _sending.Clear();
        _conflicts.Clear();
        LastReport        = null;
        _toldError        = null;
        _toldConflicts    = 0;
    }

    /// <summary>Fingerprints the set-ups' local files every couple of seconds, on a worker, so a file saved again is written into the mod.</summary>
    private void CheckSources()
    {
        if (_sourceCheck is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _sources = done.Result;
            _sourceCheck = null;
        }

        if (_sourceCheck != null || DateTime.UtcNow < _nextSourceCheck)
            return;
        _nextSourceCheck = DateTime.UtcNow + SourceCheckEvery;
        _sourcesRevision = _session.SetUpRevision;
        var paths = _session.SourcePaths();
        _sourceCheck = Task.Run(() => SourceFingerprint.Of(paths));
    }

    private void Start(ModProject project)
    {
        // The set-ups on disk first, so a crash mid-update cannot lose them.
        _session.Flush();

        _sending = new Dictionary<string, ConflictChoice>(_resolutions, StringComparer.Ordinal);
        _resolutions.Clear();
        _retry = false;

        WriteRequest request;
        try
        {
            request = _session.CreateWriteRequest(_sending);
        }
        catch (Exception ex)
        {
            // Not retried on every frame: the next change (or "Try again") starts another. Choices
            // made for conflicts go with this attempt; the conflicts come back with the next update.
            StudioServices.Log.Warning(ex, "[Studio] Could not prepare an update of {0}", project.FolderPath);
            _sending = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
            _writtenRevision = _session.SetUpRevision;
            _writtenSources  = _sources;
            LastReport = new WriteReport
            {
                ModName    = project.FolderName,
                ModPath    = project.FolderPath,
                FolderName = project.FolderName,
                FatalError = ex.Message,
            };
            Tell(LastReport);
            return;
        }

        _running = new WriteJob(request);
        _runningFor = project;
    }

    /// <summary>Takes a finished update: the open mod follows what it wrote, and Penumbra reads the mod again.</summary>
    private void Finish()
    {
        if (_running is not { IsDone: true } job)
            return;

        _running = null;
        var forProject = _runningFor;
        _runningFor = null;
        var request = job.Request;

        WriteReport report;
        if (job.Task.IsCompletedSuccessfully)
        {
            report = job.Task.Result;
        }
        else
        {
            report = new WriteReport
            {
                ModName    = request.ModFolderName,
                ModPath    = request.ModPath,
                FolderName = request.ModFolderName,
                Revision   = request.Revision,
                FatalError = job.Task.Exception?.GetBaseException().Message ?? "unknown error",
            };
        }

        bool openMod = forProject != null && ReferenceEquals(forProject, _session.Project);
        if (report.Cancelled)
        {
            // Nothing in the mod changed. Choices it was carrying out wait for the next one.
            if (openMod)
                foreach (var (key, choice) in _sending)
                    _resolutions.TryAdd(key, choice);
            _sending = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
            return;
        }

        // The open mod — or the same folder opened again meanwhile — takes what was written.
        if (_session.Project is { } open && SameFolder(open.FolderPath, request.ModPath))
        {
            if (report.NewLedger != null)
                open.AdoptLedger(report.NewLedger);
            if (report.MetaWritten)
                open.RefreshFromDisk(force: true);
            if (report.ChangedMod)
                _editor.Graphs.Refresh();
        }

        if (report.ChangedMod && _host.PenumbraIpc.IsAvailable)
        {
            var result = _host.PenumbraIpc.ReloadMod(request.ModFolderName);
            if (result == PenumbraApiEc.ModMissing)
                result = _host.PenumbraIpc.AddMod(request.ModFolderName);
            report.ReloadResult = result;
            if (_host.Configuration.RedrawAfterBuild)
                _host.PenumbraIpc.RedrawAll();
        }

        if (!openMod)
            return;

        _writtenRevision = request.Revision;
        _writtenSources  = report.SourceFingerprint.Length > 0 ? report.SourceFingerprint : _sources;
        _sending = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
        LastReport = report;

        _conflicts.Clear();
        foreach (var conflict in report.Conflicts)
            if (!_resolutions.ContainsKey(conflict.Key))
                _conflicts[conflict.Key] = conflict;
        Tell(report);
    }

    /// <summary>Says what needs the user: an update that could not be done, or files newly waiting for a choice. Everything else shows in the status bar.</summary>
    private void Tell(WriteReport report)
    {
        if (report.FatalError != null)
        {
            if (report.FatalError != _toldError)
                _session.Notify($"The mod could not be updated: {report.FatalError}", Severity.Error);
            _toldError = report.FatalError;
            return;
        }
        _toldError = null;

        if (_conflicts.Count > _toldConflicts)
            _session.Notify($"{_conflicts.Count} file(s) of the mod were changed elsewhere after they were written here, and your change to them was not written over that. " +
                            "Choose which to keep from the status bar.", Severity.Warning);
        _toldConflicts = _conflicts.Count;
    }

    private static bool SameFolder(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // What the user can do
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Settles a file changed both here and elsewhere; the next update (right away) carries it out.</summary>
    public void Resolve(WriteConflict conflict, ConflictChoice choice)
    {
        _resolutions[conflict.Key] = choice;
        _conflicts.Remove(conflict.Key);
        _toldConflicts = _conflicts.Count;
    }

    public void ResolveAll(ConflictChoice choice)
    {
        foreach (var conflict in _conflicts.Values.ToList())
            Resolve(conflict, choice);
    }

    /// <summary>Runs the update again, after one that could not be done.</summary>
    public void Retry() => _retry = true;

    /// <summary>
    /// The session is about to let go of <paramref name="project"/>: changes not yet in the mod are
    /// written after it closed. Called while the session still has its set-ups.
    /// </summary>
    public void Closing(ModProject project)
    {
        if (!ReferenceEquals(project, _project))
            return;
        if (_session.SetUpRevision == _writtenRevision && _resolutions.Count == 0 && !_retry)
            return;   // nothing it lacks
        if (WritingOpenMod && _running!.Request.Revision == _session.SetUpRevision && _resolutions.Count == 0)
            return;   // being written right now; it finishes after the mod closed

        try
        {
            _queued.Enqueue(_session.CreateWriteRequest(_resolutions));
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not write {0} as it closed; it is written when it is opened again", project.FolderPath);
        }
    }

    /// <summary>
    /// The plugin is going away. An update that has started writing is given a moment to finish (it
    /// saves its own ledger); one still preparing is stopped, having changed nothing. Set-ups not yet
    /// written stay saved, and are written the next time the mod is opened.
    /// </summary>
    public void Dispose()
    {
        _queued.Clear();
        if (_running is not { } job)
            return;

        job.Stop();
        try
        {
            job.Task.Wait(job.Committing ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Whatever it was doing, it has said so in its report; nothing more to do here.
        }

        if (job.Task.IsCompletedSuccessfully && job.Task.Result.ChangedMod && _host.PenumbraIpc.IsAvailable)
            _host.PenumbraIpc.ReloadMod(job.Request.ModFolderName);
    }
}
