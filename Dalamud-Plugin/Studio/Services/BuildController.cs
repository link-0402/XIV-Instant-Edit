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
/// nothing of the pack is kept in the mod. A build only ever makes a new mod: it writes into a folder
/// that does not exist yet, and never into one that does, its own earlier build included. A built mod
/// is changed by opening it in Edit mode.
/// </summary>
internal sealed class BuildController
{
    private readonly StudioHost _host;
    private readonly PortSession _session;

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
        if (!string.IsNullOrEmpty(root))
        {
            var folder = Path.Combine(root, FolderName);
            if (LastReport is { FatalError: null, Cancelled: false } last
                && string.Equals(last.ModPath, folder, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(folder))
            {
                reason = "This mod is built. Use \"Edit the built mod\" to change it, or give the modpack another name to build a new mod.";
                return false;
            }
            if (BuildTarget.Problem(folder) is { } problem)
            {
                reason = problem;
                return false;
            }
        }

        reason = string.Empty;
        return true;
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
            report = new BuildReport { ModName = job.Request.ModName, ModPath = job.Request.ModPath, ItemsSummary = itemsSummary };
            report.FatalError = job.Task.Exception?.GetBaseException().Message ?? "unknown error";
        }

        // The new mod is registered with Penumbra directly, so it shows up without the user having
        // to run a full mod-directory rediscovery.
        if (report.FatalError == null && !report.Cancelled)
        {
            var ipc = _host.PenumbraIpc;
            var result = ipc.ReloadMod(report.ModName);
            if (result == PenumbraApiEc.ModMissing)
                result = ipc.AddMod(report.ModName);
            report.AddModResult = result;
            if (_host.Configuration.RedrawAfterBuild)
                ipc.RedrawAll();

            // That folder is a mod of its own now, changed in Edit mode. The next build (under
            // another name) makes another mod, with an id of its own.
            _host.Configuration.ModId = null;
            _host.Configuration.Save();
        }

        LastReport = report;

        _session.Notify(report.Summary(), report.Succeeded ? Severity.Info : report.Failed > 0 || report.FatalError != null
            ? Severity.Error
            : Severity.Warning);
    }

    /// <summary>Whether a build is writing <paramref name="folder"/> right now.</summary>
    public bool IsWriting(string folder)
    {
        if (Running is not { } job)
            return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Request.ModPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Where a build may write: only into a mod folder that does not exist yet.</summary>
internal static class BuildTarget
{
    /// <summary>
    /// Why a build may not write into <paramref name="folder"/>, or null when it may. A build only
    /// makes new mods, so any folder that is already there is refused, empty or not, whoever made it:
    /// a mod that exists is changed by opening it, never by building over it.
    /// </summary>
    public static string? Problem(string folder)
    {
        bool exists;
        try
        {
            exists = Directory.Exists(folder) || File.Exists(folder);
        }
        catch (Exception)
        {
            exists = true;
        }
        return exists
            ? $"{Path.GetFileName(folder)} is already in Penumbra's mod folder. Open it from the library to edit it, or give this mod another name in the Overview."
            : null;
    }
}
