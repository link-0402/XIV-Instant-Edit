using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Other mods in the library that use the same custom hair skeleton ID: when both are on, one hair gets the other's bones.</summary>
internal sealed partial class ModCheck
{
    // [0-9], not \d: \d matches any Unicode digit, which int.Parse refuses.
    [GeneratedRegex("skeleton/hair/h([0-9]{4})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HairSkeletonKey();

    private void HairSkeletonFindings()
    {
        if (_input.HairSkeletonUsers is not { } readUsers)
            return;
        var firstKeys = new Dictionary<int, string>();
        foreach (var mapping in _model.Mappings)
            if (HairSkeletonKey().Match(mapping.Key) is { Success: true } match && int.Parse(match.Groups[1].Value) is var id and >= HairSkeletonIds.Minimum)
                firstKeys.TryAdd(id, mapping.Key);
        if (firstKeys.Count == 0)
            return;

        IReadOnlyDictionary<int, IReadOnlyList<string>> users;
        try
        {
            users = readUsers();
        }
        catch (Exception ex)
        {
            _notes.Add($"Other mods' hair skeletons could not be checked: {ex.Message}");
            return;
        }
        _token.ThrowIfCancellationRequested();

        foreach (var (id, key) in firstKeys.OrderBy(pair => pair.Key))
        {
            if (users.GetValueOrDefault(id) is not { Count: > 0 } mods)
                continue;
            var sorted = mods.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var names = sorted.Length <= 3 ? string.Join(", ", sorted) : $"{string.Join(", ", sorted.Take(3))} and {sorted.Length - 3} more";
            AddFinding($"X005:{id}", "X005", CheckCategory.Dependencies, Severity.Warning, "Another mod uses the same hair skeleton",
                $"{names} also {(sorted.Length == 1 ? "uses" : "use")} hair skeleton h{id:D4}. When both are on, one hair gets the other's bones. If Instant Edit made it, export this hair again from Blender to move it to a free ID.",
                key);
        }
    }
}
