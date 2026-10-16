using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>One timeline of an animation item: the files that replace it and, if it differs from the item's, which races.</summary>
[Serializable]
public class AnimationTimelineSetup
{
    /// <summary>The ActionTimeline key, e.g. "emote/joy" or "emote/sit_start".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Shown on its row: "Start", "Loop", "End", or the key's last part.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The .pap written to every ticked race's path; empty for none.</summary>
    public string PapSource { get; set; } = string.Empty;

    /// <summary>The .tmb written to the timeline's one race-independent path; empty for none.</summary>
    public string TmbSource { get; set; } = string.Empty;

    /// <summary>Race codes of its own; null follows the item's.</summary>
    public List<string>? Races { get; set; }

    public bool HasFiles => !string.IsNullOrWhiteSpace(PapSource) || !string.IsNullOrWhiteSpace(TmbSource);

    public AnimationTimelineSetup Clone() => new()
    {
        Key = Key, Label = Label, PapSource = PapSource, TmbSource = TmbSource, Races = Races?.ToList(),
    };
}

/// <summary>An animation item's set-up: its timelines and the races its files are written for (empty = every race).</summary>
[Serializable]
public class AnimationSetup
{
    public List<AnimationTimelineSetup> Timelines { get; set; } = new();
    public List<string> Races { get; set; } = new();

    public bool HasWork => Timelines.Any(t => t.HasFiles);

    /// <summary>
    /// What a row without a .pap leaves to the game, once the item has files elsewhere (an item with
    /// none has its own warning). Null for a row with a .pap.
    /// </summary>
    public static string? RowHint(AnimationSetup setup, AnimationTimelineSetup row)
    {
        if (!string.IsNullOrWhiteSpace(row.PapSource) || !setup.HasWork)
            return null;
        return string.IsNullOrWhiteSpace(row.TmbSource)
            ? "Not replaced: the game's own animation plays here."
            : "Only the timeline changes: the game's own motion plays with it.";
    }

    public AnimationSetup Clone() => new()
    {
        Timelines = Timelines.Select(t => t.Clone()).ToList(),
        Races     = Races.ToList(),
    };

    /// <summary>A new set-up for <paramref name="target"/>: one empty row per timeline that can be replaced, every race.</summary>
    internal static AnimationSetup For(AnimationTarget target) => new()
    {
        Timelines = target.Timelines.Where(t => t.Editable)
            .Select(t => new AnimationTimelineSetup { Key = t.Key, Label = t.Label })
            .ToList(),
    };
}

/// <summary>Something whose animations can be replaced: an emote (its timelines, with start and end) or one other timeline.</summary>
internal sealed record AnimationTarget(uint Key, string Name, uint Icon, string Command, IReadOnlyList<AnimationTimelineRef> Timelines);

/// <summary>One ActionTimeline of a target. <paramref name="Reason"/> says why it can't be replaced when <paramref name="Editable"/> is false.</summary>
internal sealed record AnimationTimelineRef(ushort Id, string Key, string Label, bool Editable, string? Reason);
