using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>
/// Keeps the open mod's resource graph current: when the mod's files or its replacements change, a
/// new graph is built on a worker (a moment after the last change, so a burst
/// of edits builds once) and swapped in whole when it is done. The views always draw the last
/// finished graph, so nothing waits on a build.
/// </summary>
internal sealed class ResourceGraphService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly StudioHost _host;
    private readonly ModFileFactsCache _facts = new();

    private Task<ResourceGraph>? _task;
    private ReplacementSnapshot? _snapshot;
    private CancellationTokenSource? _cts;
    private int _wanted  = int.MinValue;
    private int _running = int.MinValue;
    private int _built   = int.MinValue;
    private int _refreshes;
    private DateTime _due;
    private string? _folder;

    public ResourceGraphService(StudioHost host) => _host = host;

    /// <summary>The last finished graph of the open mod (empty while none is open, or before the first build).</summary>
    public ResourceGraph Current { get; private set; } = ResourceGraph.Empty;

    public bool IsBuilding => _task != null;

    /// <summary>Why the last build failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>Builds again, re-reading whatever changed on disk.</summary>
    public void Refresh()
    {
        _facts.Invalidate();
        _refreshes++;
    }

    /// <summary>Called once per frame on the render thread.</summary>
    public void Update(ModSession session)
    {
        var project = session.Project;
        if (project == null)
        {
            Cancel();
            Current = ResourceGraph.Empty;
            _wanted = _built = int.MinValue;
            _folder = null;
            return;
        }

        if (!string.Equals(_folder, project.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            // Another mod: the old graph would only mislead while the new one builds.
            Cancel();
            _folder = project.FolderPath;
            Current = ResourceGraph.Empty;
            _built  = int.MinValue;
        }

        int signature = HashCode.Combine(project.FolderPath, session.Revision, project.Revision, project.Meta.Revision,
            project.Sidecar.Replacements.Count, project.Sidecar.AddedItems.Count, _refreshes, _host.GameData.IsItemCacheReady);
        if (signature != _wanted)
        {
            _wanted = signature;
            _due = Current.Revision == -1 && Current.Roots.Count == 0 ? DateTime.UtcNow : DateTime.UtcNow + Debounce;
        }

        if (_task is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
            {
                _snapshot?.Relink(done.Result);
                Current   = done.Result;
                LastError = null;
            }
            else if (done.IsFaulted)
            {
                LastError = done.Exception?.GetBaseException().Message;
                StudioServices.Log.Warning(done.Exception, "[Studio] Building the resource tree failed");
            }
            _built = _running;
            _task  = null;
        }

        if (_task == null && _built != _wanted && DateTime.UtcNow >= _due)
            Start(session);
    }

    private void Start(ModSession session)
    {
        var project = session.Project!;
        var meta = project.Meta;
        var gameData = _host.GameData;
        var ledger = project.Sidecar.Ledger;
        var snapshot = _snapshot = new ReplacementSnapshot(project.Sidecar.Replacements);
        var input = new GraphInput
        {
            Folder        = project.FolderPath,
            Files         = meta.AllFiles().ToList(),
            Swaps         = meta.AllSwaps().ToList(),
            Manipulations = meta.ManipulationCounts().ToList(),
            Replacements  = snapshot.Copies,
            Pinned        = project.Sidecar.AddedItems
                .SelectMany(item => item.Models.Select(path => (OptionRef.Parse(item.Option), path)))
                .ToList(),
            Carriers      = meta.FileCarriers().Select(o => (o, meta.OptionLabel(o))).ToList(),
            ItemNames     = gameData.IsItemCacheReady ? gameData : null,
            Revision      = _wanted,
            Written       = ledger.Files
                .GroupBy(f => PathNormalizer.RelKey(f.RelPath))
                .ToDictionary(g => g.Key, g => g.First().Clone(), StringComparer.Ordinal),
        };

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var vanilla = gameData.VanillaFiles;
        _running = _wanted;
        _task = Task.Run(() => ResourceGraphBuilder.Build(input, _facts, vanilla, ct), ct);
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        _task = null;
    }

    public void Dispose()
    {
        Cancel();
        _facts.Clear();
    }
}

/// <summary>
/// The replacements a graph is built from, copied on the render thread: the views edit the live
/// ones in place (slots added or removed, material names, images) while the worker reads them. The
/// worker builds from the copies; once the graph is done, <see cref="Relink"/> (render thread again)
/// points its nodes back at the live replacements and texture slots, which is what the views edit.
/// </summary>
internal sealed class ReplacementSnapshot
{
    private readonly Dictionary<ResourceReplacement, ResourceReplacement> _replacements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextureSlot, TextureSlot> _slots = new(ReferenceEqualityComparer.Instance);

    public ReplacementSnapshot(IEnumerable<ResourceReplacement> live)
    {
        var copies = new List<ResourceReplacement>();
        foreach (var replacement in live)
        {
            var copy = replacement.Clone();
            copies.Add(copy);
            _replacements[copy] = replacement;
            if (copy.Authored is { } copied && replacement.Authored is { } original)
                for (int i = 0; i < copied.Textures.Count && i < original.Textures.Count; i++)
                    _slots[copied.Textures[i]] = original.Textures[i];
        }
        Copies = copies;
    }

    /// <summary>What the worker reads.</summary>
    public IReadOnlyList<ResourceReplacement> Copies { get; }

    /// <summary>Points a graph built from <see cref="Copies"/> at the live replacements and slots.</summary>
    public void Relink(ResourceGraph graph)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (node.Replacement is { } copy && _replacements.TryGetValue(copy, out var replacement))
                node.Replacement = replacement;
            if (node.AuthoredSlot is { } slot && _slots.TryGetValue(slot, out var live))
                node.AuthoredSlot = live;
        }
    }
}
