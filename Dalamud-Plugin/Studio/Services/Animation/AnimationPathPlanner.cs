using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Animation;

/// <summary>One file an animation item writes: the source, the game path, and the ticked races that path serves.</summary>
internal sealed record AnimationFilePlan(string Source, string GamePath, IReadOnlyList<string> Races)
{
    /// <summary>Races not ticked whose game loads this same file (the PAP load table sends them here), so they play it too.</summary>
    public IReadOnlyList<string> AlsoServes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Where an animation item's files go. A body timeline's PAP is requested per race from that
/// race's bt_common folder, but the game's PAP load table sends many races to another race's
/// copy, so each ticked race's path is resolved through it and one file is written per distinct
/// path. A TMB is one file for every race.
/// </summary>
internal static class AnimationPathPlanner
{
    public static string PapPath(string raceCode, string key) => $"chara/human/c{raceCode}/animation/a0001/bt_common/{key}.pap";

    public static string TmbPath(string key) => $"chara/action/{key}.tmb";

    /// <param name="allRaceCodes">Every race, used when the set-up ticks none by hand.</param>
    /// <param name="resolve">The path the game loads for a requested PAP path (the PAP load table; identity without it).</param>
    public static IReadOnlyList<AnimationFilePlan> Plan(AnimationSetup setup, IReadOnlyList<string> allRaceCodes, Func<string, string> resolve)
    {
        var plans = new List<AnimationFilePlan>();
        foreach (var timeline in setup.Timelines)
        {
            // The key comes from the game's sheet, but it is still checked like any path this writes.
            if (string.IsNullOrWhiteSpace(timeline.Key) || !InstantEdit.Services.Animations.AnimationDependencies.SafeGamePath(PapPath("0101", timeline.Key)))
                continue;
            var races = timeline.Races ?? (setup.Races.Count == 0 ? allRaceCodes.ToList() : setup.Races);

            if (!string.IsNullOrWhiteSpace(timeline.PapSource))
            {
                // Every race the game sends to a path, ticked or not: an unticked race whose copy is
                // borrowed from a ticked race's path plays the new file too.
                var servedBy = allRaceCodes.Concat(races).Distinct(StringComparer.Ordinal)
                    .ToLookup(race => resolve(PapPath(race, timeline.Key)), StringComparer.OrdinalIgnoreCase);
                foreach (var group in races.Distinct(StringComparer.Ordinal)
                             .GroupBy(race => resolve(PapPath(race, timeline.Key)), StringComparer.OrdinalIgnoreCase))
                {
                    var ticked = group.ToList();
                    plans.Add(new AnimationFilePlan(timeline.PapSource, group.Key, ticked)
                    {
                        AlsoServes = servedBy[group.Key].Where(r => !ticked.Contains(r, StringComparer.Ordinal)).ToList(),
                    });
                }
            }
            if (!string.IsNullOrWhiteSpace(timeline.TmbSource))
                plans.Add(new AnimationFilePlan(timeline.TmbSource, TmbPath(timeline.Key), races.ToList()));
        }
        return plans.OrderBy(p => p.GamePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The PAPs planned at paths the game has no file at: the game never asks for them, so the mod would
    /// change nothing there (idle and movement play from resident PAPs, for example). TMBs are not checked.
    /// </summary>
    public static IReadOnlyList<AnimationFilePlan> MissingFromGame(IReadOnlyList<AnimationFilePlan> plans, Func<string, bool> exists)
        => plans.Where(p => p.GamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) && !exists(p.GamePath)).ToList();
}
