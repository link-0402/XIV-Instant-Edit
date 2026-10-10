using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>
/// One mod window's check: the run on a worker, the last report and its ticks, and the operations
/// (repairs, clean-up) started from the Check view. The render thread calls <see cref="Update"/>
/// every frame, which is when finished work is taken in. Dalamud-free.
/// </summary>
internal sealed class CheckSession
{
    private Task<ModCheckReport>? _run;
    private CancellationTokenSource? _cancel;
    private CheckProgress? _progress;
    private Task<(bool Ok, string Message)>? _operation;
    private bool _checkAfter;
    private volatile bool _operating;
    private bool _checkRequested;
    private (ModCheckReport Report, ResourceGraph Graph, IReadOnlyDictionary<string, (Severity Severity, string Title)> Markers)? _markers;

    public ModCheckReport? Report { get; private set; }
    public RepairSelection? Selection { get; private set; }

    /// <summary>The running check's or operation's last progress line.</summary>
    public CheckProgress? Progress => Volatile.Read(ref _progress);

    public bool Running => _run is not null;

    /// <summary>Why the last check failed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Check against the player's collection too ("Use my collection").</summary>
    public bool UseCollection { get; set; }

    /// <summary>A check or an operation is under way.</summary>
    public bool Busy => _run is not null || _operation is not null;

    /// <summary>What the operation under way is ("Applying fixes"), or null.</summary>
    public string? OperationLabel { get; private set; }

    /// <summary>How the last operation went.</summary>
    public (bool Ok, string Message)? LastOperation { get; private set; }

    /// <summary>Starts a check. <paramref name="capture"/> reads the player's collection first, when the check should use it.</summary>
    public void Start(CheckInput input, Func<Task<ICollectionView?>>? capture = null)
    {
        if (Busy)
            return;
        Error = null;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Volatile.Write(ref _progress, new CheckProgress("Starting the check", 0, 5));
        _run = Task.Run(async () =>
        {
            var collection = capture is null ? null : await capture().ConfigureAwait(false);
            return ModCheck.Run(collection is null ? input : input.WithCollection(collection), new SyncProgress(p => Volatile.Write(ref _progress, p)), token);
        }, token);
    }

    /// <summary>Stops the running check, or the operation under way where it can still stop (a batch between files; repairs before meta.json is written).</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Where an operation reports how far it is; shown as <see cref="Progress"/>.</summary>
    public IProgress<CheckProgress> OperationProgress => new SyncProgress(p => { if (_operating) Volatile.Write(ref _progress, p); });
    /// <summary>Starts a change to the mod. With <paramref name="checkAfter"/>, a new check is asked for once it is done.</summary>
    public void StartOperation(string label, Func<CancellationToken, Task<(bool Ok, string Message)>> work, bool checkAfter)
    {
        if (Busy)
            return;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        OperationLabel = label;
        _checkAfter = checkAfter;
        Volatile.Write(ref _progress, null);
        _operating = true;
        _operation = Task.Run(() => work(token), token);
    }

    /// <summary>Takes in finished work. Render thread, every frame.</summary>
    public void Update()
    {
        if (_run is { IsCompleted: true } run)
        {
            _run = null;
            if (run.IsCompletedSuccessfully)
            {
                Report = run.Result;
                Selection = new RepairSelection(Report);
                _markers = null;
                IsStale = false;
                _sinceWatched.Restart();
            }
            else
            {
                Error = run.IsCanceled ? "The check was cancelled." : $"The check failed: {run.Exception?.GetBaseException().Message}";
            }
        }

        if (_operation is { IsCompleted: true } operation)
        {
            _operation = null;
            OperationLabel = null;
            _operating = false;
            Volatile.Write(ref _progress, null);
            LastOperation = operation.IsCompletedSuccessfully
                ? operation.Result
                : (false, operation.IsCanceled ? "Cancelled." : $"It failed: {operation.Exception?.GetBaseException().Message}");
            if (_checkAfter)
                _checkRequested = true;
        }
    }

    /// <summary>True once after an operation that asked for a new check finished.</summary>
    public bool TakeCheckRequest()
    {
        var requested = _checkRequested;
        _checkRequested = false;
        return requested;
    }

    private readonly System.Diagnostics.Stopwatch _sinceWatched = new();

    /// <summary>Whether the last report no longer matches the mod, as of the last <see cref="Watch"/>.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Looks at meta.json at most every two seconds, to keep <see cref="IsStale"/> current without reading it every frame.</summary>
    public void Watch(ModProject? project)
    {
        if (_sinceWatched.IsRunning && _sinceWatched.Elapsed < TimeSpan.FromSeconds(2))
            return;
        _sinceWatched.Restart();
        IsStale = Stale(project);
    }

    /// <summary>Whether the mod changed since the last report was made (or moved, or is gone).</summary>
    public bool Stale(ModProject? project)
        => Report is { } report && (project is null || MetaFingerprint.Of(project.FolderPath) != report.Fingerprint);

    /// <summary>The Resources tree's marks for the last report in a graph, worked out once per report and graph.</summary>
    public IReadOnlyDictionary<string, (Severity Severity, string Title)> Markers(ResourceGraph graph)
    {
        if (Report is not { } report)
            return EmptyMarkers;
        if (_markers is { } cached && ReferenceEquals(cached.Report, report) && ReferenceEquals(cached.Graph, graph))
            return cached.Markers;
        var markers = CheckMarkers.Build(report, graph);
        _markers = (report, graph, markers);
        return markers;
    }

    private static readonly IReadOnlyDictionary<string, (Severity Severity, string Title)> EmptyMarkers = new Dictionary<string, (Severity, string)>();

    /// <summary>Progress reported on the worker's own thread, not posted anywhere.</summary>
    private sealed class SyncProgress(Action<CheckProgress> report) : IProgress<CheckProgress>
    {
        public void Report(CheckProgress value) => report(value);
    }
}

/// <summary>Which Resources nodes a report has findings about, with the worst one each.</summary>
internal static class CheckMarkers
{
    public static IReadOnlyDictionary<string, (Severity Severity, string Title)> Build(ModCheckReport report, ResourceGraph graph)
    {
        var result = new Dictionary<string, (Severity Severity, string Title)>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in report.Findings)
        {
            if (string.IsNullOrEmpty(finding.GamePath) || graph.Find(finding.GamePath, finding.Option ?? Project.OptionRef.Default) is not { } node)
                continue;
            if (!result.TryGetValue(node.Id, out var current) || finding.Severity > current.Severity)
                result[node.Id] = (finding.Severity, finding.Title);
        }
        return result;
    }
}
