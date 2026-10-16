using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Models;

/// <summary>The models an item from the game brings into a mod window, and the items a mod window can't take.</summary>
internal static class AddedItemModels
{
    /// <summary>Why <paramref name="subject"/> can't be added to a mod window, or null when it can.</summary>
    public static string? WhyNot(PortSubject subject) => subject is AnimationSubject
        ? $"{subject.DisplayName} is an animation: set it up in the Build window, which writes its files for each race."
        : null;

    /// <summary>
    /// The model paths to list: one per race with its own model (gear, hair), the feature's own race,
    /// or a weapon's one model, which every race shares.
    /// </summary>
    public static List<string> Paths(PortSubject subject, IEnumerable<RaceGender> nativeRaces)
    {
        if (WhyNot(subject) != null)
            return new List<string>();
        IEnumerable<RaceGender> races = !subject.HasRaces ? new[] { RaceInfo.BaseFor(PlayerGender.Male) }
            : subject.MultiRace ? nativeRaces.OrderBy(r => r.RaceCode, StringComparer.Ordinal)
            : subject.BaseRace is { } race ? new[] { race }
            : Array.Empty<RaceGender>();
        return races.Select(subject.ModelGamePath).Select(PathNormalizer.GamePath).Distinct(StringComparer.Ordinal).ToList();
    }
}
