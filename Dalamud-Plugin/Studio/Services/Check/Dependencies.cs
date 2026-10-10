using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>The player's collection: what other mods provide that this mod lacks, and which of its files they override.</summary>
internal sealed partial class ModCheck
{
    private readonly List<(CheckMapping Mapping, string Provider)> _overridden = new();

    private void ResolveExternal()
    {
        if (_input.Collection is not { } live)
            return;
        var pending = _requests.Where(r => r.Resolution.Source is ResolutionSource.Unresolved or ResolutionSource.Conditional or ResolutionSource.Exclusive)
            .Select(r => r.Path)
            .Concat(SkinRequests().Select(s => s.Path).Where(path => !_game.Exists(path)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var checkOverrides = live.Context.ModEnabled == true;
        var defaults = checkOverrides
            ? _model.Mappings.Where(m => ReferenceEquals(m.Container, _default) && !m.Shadowed && ResourceNaming.HasValidRoot(m.Key)).Take(2000).ToList()
            : new List<CheckMapping>();
        var all = pending.Concat(defaults.Select(m => m.Key)).Distinct(StringComparer.Ordinal).ToList();
        if (all.Count == 0)
            return;

        IReadOnlyDictionary<string, string?> resolved;
        try
        {
            resolved = live.Resolve(all);
        }
        catch (Exception ex)
        {
            _notes.Add($"Your Penumbra collection could not be checked: {ex.Message}");
            return;
        }

        foreach (var path in pending)
            _external[path] = resolved.GetValueOrDefault(path) is { } target ? live.ModOf(target) ?? $"swapped to {target}" : null;
        foreach (var request in _requests.Where(r => r.Resolution.Source == ResolutionSource.Unresolved))
            if (_external.GetValueOrDefault(request.Path) is { } provider)
                request.Resolution = new PathResolution(ResolutionSource.External, request.Path, Detail: provider);

        var root = Path.GetFullPath(_input.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var mapping in defaults)
        {
            if (resolved.GetValueOrDefault(mapping.Key) is not { } target || !Path.IsPathRooted(target))
                continue;
            if (Path.GetFullPath(target).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            _overridden.Add((mapping, live.ModOf(target) ?? target));
        }
    }

    private void LiveFindings()
    {
        if (_input.Collection?.Context is not { } live)
            return;
        if (live.ModEnabled == false)
            AddFinding("X003", "X003", CheckCategory.Dependencies, Severity.Warning, "The mod is off in your collection",
                $"This mod is not on in {live.Name}, so nothing in it is used for your character.");
        foreach (var group in _overridden.GroupBy(o => o.Provider))
            AddFinding($"X004:{group.Key}", "X004", CheckCategory.Dependencies, Severity.Warning, "Another mod replaces some of its files",
                $"{group.Key} replaces {group.Count()} of this mod's files in {live.Name} (for example {string.Join(", ", group.Take(3).Select(o => o.Mapping.Key))}). Change the priorities in Penumbra if this mod should win.",
                group.First().Mapping.Key);
    }
}
