using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Which of a report's repairs are ticked, and which candidate each repair with a choice uses. Starts with the report's own ticks.</summary>
internal sealed class RepairSelection
{
    private readonly ModCheckReport _report;
    private readonly HashSet<string> _ticked;
    private readonly Dictionary<string, string> _choices = new(StringComparer.Ordinal);

    public RepairSelection(ModCheckReport report)
    {
        _report = report;
        _ticked = report.Repairs.Where(repair => repair.Preselected).Select(repair => repair.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Also delete the files a removed or redirected mapping leaves unused (off unless asked for).</summary>
    public bool DeleteReleasedFiles { get; set; }

    public bool IsTicked(string id) => _ticked.Contains(id);

    public void Set(string id, bool ticked)
    {
        if (ticked) _ticked.Add(id);
        else _ticked.Remove(id);
    }

    public string? Choice(string id) => _choices.GetValueOrDefault(id);

    public void Choose(string id, string label) => _choices[id] = label;

    /// <summary>The ticked repairs whose prerequisites are ticked too, one per exclusive group.</summary>
    public IReadOnlyList<Repair> Effective()
    {
        var chosen = _report.Repairs.Where(repair => _ticked.Contains(repair.Id)).ToList();
        bool removed;
        do
        {
            var ids = chosen.Select(repair => repair.Id).ToHashSet(StringComparer.Ordinal);
            removed = chosen.RemoveAll(repair => repair.DependsOn.Any(dependency => !ids.Contains(dependency))) > 0;
        }
        while (removed);
        return chosen.GroupBy(repair => repair.ExclusiveGroup ?? repair.Id).Select(group => group.First()).ToArray();
    }

    /// <summary>How many repairs applying would make.</summary>
    public int Count => Effective().Count;

    /// <summary>A copy of the ticks and choices as they are now, for an apply on a worker while the view stays editable.</summary>
    public RepairSelection Snapshot()
    {
        var copy = new RepairSelection(_report) { DeleteReleasedFiles = DeleteReleasedFiles };
        copy._ticked.Clear();
        copy._ticked.UnionWith(_ticked);
        foreach (var (id, label) in _choices)
            copy._choices[id] = label;
        return copy;
    }
}
