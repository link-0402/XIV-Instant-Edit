using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary> The part of the Animations list a row sits in. </summary>
internal enum AnimationGroup
{
    /// <summary> Playing now, each followed by the startup that leads into it. </summary>
    Playing,
    /// <summary> Played earlier with a matched skeleton, so it can still be sent or edited. </summary>
    Recent,
}

/// <summary>
/// One row of the Animations tab: an animation the listener detected on your character, and the
/// resource row the browser helpers draw for it (source badge, path, row menu).
/// </summary>
internal sealed record AnimationRow(AnimationCapture Capture, bool Startup, AnimationGroup Group, ResourceView View)
{
    /// <summary> The clip the row stands for: the animation's own, or the startup that leads into it. </summary>
    public AnimationClip Clip => Startup && Capture.Startup is { } startup ? startup : Capture.Clip;

    public bool Playing => Capture.Playing && !Startup;

    /// <summary> Identifies the row across the listener's updates of the same animation. </summary>
    public string Key => Capture.Id + (Startup ? ":startup" : ":clip");
}

/// <summary> Builds and searches the Animations tab's rows. No ImGui or Dalamud dependencies. </summary>
internal static class AnimationRows
{
    /// <summary> What is playing, each followed by its startup, then recent animations with a matched skeleton. </summary>
    public static ImmutableArray<AnimationRow> Build(IEnumerable<AnimationCapture> history)
    {
        var rows = ImmutableArray.CreateBuilder<AnimationRow>();
        foreach (var item in AnimationPresentation.ListItems(history))
            rows.Add(new AnimationRow(item.Capture, item.Startup,
                item.Capture.Playing ? AnimationGroup.Playing : AnimationGroup.Recent,
                ResourceViews.FromAnimation(item.Capture, item.Startup, rows.Count)));
        return rows.ToImmutable();
    }

    /// <summary>
    /// The rows the search admits. An animation and its startup stay together, so a startup never
    /// shows without the animation it leads into.
    /// </summary>
    public static IReadOnlyList<AnimationRow> Filter(ImmutableArray<AnimationRow> rows, ResourceSearch search)
    {
        if (!search.Active)
            return rows;
        var matched = rows.Where(row => search.Matches(row.View)).Select(row => row.Capture.Id).ToHashSet(StringComparer.Ordinal);
        return rows.Where(row => matched.Contains(row.Capture.Id)).ToArray();
    }
}
