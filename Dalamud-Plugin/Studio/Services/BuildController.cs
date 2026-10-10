using System;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Build mode's builds: starts them, tracks the running one, and finishes it on the render thread:
/// once the worker is done, Penumbra is told about the mod here rather than from the worker, and
/// the result is kept for the Build stage to show.
///
/// A build writes the whole pack into "&lt;mod directory&gt;/&lt;mod name&gt;" as a plain Penumbra mod —
/// nothing of the pack is kept in the mod. Rebuilding writes the same folder again, keeping the
/// mod's id; a folder of that name that belongs to another mod is never written into.
/// </summary>
internal sealed class BuildController
{
    private readonly StudioHost _host;
    private readonly PortSession _session;

    // Who owns the folder a build would write, looked up again only when its meta.json changes.
    private string? _ownerFolder;
    private (DateTime, long) _ownerStamp;
    private Guid? _ownerId;
    private string? _ownerProblem;

    public BuildController(StudioHost host, PortSession session)
    {
        _host  = host;
        _session = session;
    }

    /// <summary>The build in progress, or null.</summary>
    public BuildJob? Running { get; private set; }

    /// <summary>The most recent finished build — one mod made from every item that was in the pack.</summary>
    public BuildReport? LastReport { get; private set; }

    public bool IsRunning => Running != null;

    /// <summary>The folder name the pack builds into: its mod name, made safe for a folder.</summary>
    public string FolderName
        => PortSession.SanitizeFolderName(string.IsNullOrWhiteSpace(_session.ModName) ? _session.DefaultModName() : _session.ModName.Trim());

    /// <summary>Whether a build can start now, and if not, why.</summary>
    public bool CanBuild(out string reason)
    {
        var items = _session.Items;
        if (Running != null)                     { reason = "A build is already running.";                             return false; }
        if (items.Count == 0)                    { reason = "Add at least one item to the modpack first.";              return false; }
        if (!items.Any(i => i.HasWork))          { reason = "Add a race model or a material to at least one item.";     return false; }
        if (!_host.PenumbraIpc.IsAvailable)    { reason = "Penumbra is not available — cannot resolve the mod directory."; return false; }
        if (_host.AnySyncRunning)              { reason = "Wait a moment, until the mod edited last is written.";     return false; }

        var root = _host.PenumbraIpc.GetModDirectory();
        if (!string.IsNullOrEmpty(root) && FolderProblem(Path.Combine(root, FolderName)) is { } problem)
        {
            reason = problem;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Why the build may not write into <paramref name="folder"/>: it exists, and its meta.json says
    /// it is another mod (another id) — one of the library's own, or one built from an earlier pack.
    /// Null when the folder is free, or holds this pack's earlier build.
    /// </summary>
    private string? FolderProblem(string folder)
    {
        var metaPath = Path.Combine(folder, PenumbraModMeta.FileName);
        (DateTime, long) stamp;
        try
        {
            var info = new FileInfo(metaPath);
            stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1);
        }
        catch (Exception)
        {
            stamp = (default, -1);
        }

        var id = _host.Configuration.ModId;
        if (string.Equals(_ownerFolder, folder, StringComparison.OrdinalIgnoreCase) && _ownerStamp == stamp && _ownerId == id)
            return _ownerProblem;

        _ownerFolder  = folder;
        _ownerStamp   = stamp;
        _ownerId      = id;
        _ownerProblem = null;
        if (stamp.Item2 < 0)
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                _ownerProblem = $"A folder named {Path.GetFileName(folder)} is already in Penumbra's mod directory — give this mod another name in the Overview.";
            return _ownerProblem;
        }

        try
        {
            var meta = PenumbraModMeta.Load(folder);
            if (meta.Identifier == null || meta.Identifier != id)
                _ownerProblem = $"Another mod already lives in {Path.GetFileName(folder)} (\"{meta.Name}\") — give this mod another name in the Overview, so that one is not written over.";
        }
        catch (Exception)
        {
            _ownerProblem = $"{Path.GetFileName(folder)} already holds a mod whose meta.json cannot be read — give this mod another name in the Overview.";
        }
        return _ownerProblem;
    }

    public void Start()
    {
        if (!CanBuild(out var reason))
        {
            _session.Notify(reason, Severity.Warning);
            return;
        }

        var modRoot = _host.PenumbraIpc.GetModDirectory();
        if (string.IsNullOrEmpty(modRoot))
        {
            _session.Notify("Penumbra did not report a mod directory.", Severity.Error);
            return;
        }

        // Save first, so a crash mid-build cannot lose the edits being built.
        _session.Flush();
        var request = _session.CreateBuildRequest(modRoot);
        Running = new BuildJob(request);
        _session.GoToStage(StageId.Build);
    }

    public void Cancel() => Running?.Cancel();

    /// <summary>Called once per frame from the render thread.</summary>
    public void Update()
    {
        var job = Running;
        if (job == null || !job.IsDone)
            return;

        Running = null;
        _ownerFolder = null;   // the folder is the pack's own now
        BuildReport report;
        if (job.Task.IsCompletedSuccessfully)
        {
            report = job.Task.Result;
        }
        else
        {
            var itemsSummary = job.Request.Items.Count switch
            {
                0 => "(no items)",
                1 => job.Request.Items[0].Subject.DisplayName,
                _ => $"{job.Request.Items.Count} items",
            };
            report = new BuildReport { ModName = job.Request.ModName, ModPath = job.Request.ModRoot, ItemsSummary = itemsSummary };
            report.FatalError = job.Task.Exception?.GetBaseException().Message ?? "unknown error";
        }

        // Penumbra reads a rebuilt mod again; a new one is registered with it directly, so it shows
        // up without the user having to run a full mod-directory rediscovery.
        if (report.FatalError == null && !report.Cancelled)
        {
            var ipc = _host.PenumbraIpc;
            var result = ipc.ReloadMod(report.ModName);
            if (result == PenumbraApiEc.ModMissing)
                result = ipc.AddMod(report.ModName);
            report.AddModResult = result;
            if (_host.Configuration.RedrawAfterBuild)
                ipc.RedrawAll();
        }

        LastReport = report;

        _session.Notify(report.Summary(), report.Succeeded ? Severity.Info : report.Failed > 0 || report.FatalError != null
            ? Severity.Error
            : Severity.Warning);
    }
}
