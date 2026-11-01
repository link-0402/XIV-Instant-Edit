using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// The listener's recent clips per actor, kept while it follows another one: switching the GPose target between you and
/// a partner loses neither list. Each actor keeps <see cref="PerActor"/> clips, newest first; past <see cref="Actors"/>
/// actors the one set longest ago goes. Not thread-safe: the observer touches it on the framework thread only.
/// </summary>
internal sealed class CaptureHistories
{
    public const int PerActor = 50, Actors = 8;

    private readonly Dictionary<ulong, ImmutableArray<AnimationCapture>> lists = [];
    // The actors by when their list was last set, oldest first.
    private readonly List<ulong> order = [];

    public ImmutableArray<AnimationCapture> Of(ulong actorId) => lists.GetValueOrDefault(actorId, []);

    public void Set(ulong actorId, ImmutableArray<AnimationCapture> captures)
    {
        lists[actorId] = captures.Length > PerActor ? captures.Take(PerActor).ToImmutableArray() : captures;
        order.Remove(actorId);
        order.Add(actorId);
        while (order.Count > Actors)
        {
            lists.Remove(order[0]);
            order.RemoveAt(0);
        }
    }

    /// <summary>Changes every actor's list the same way, such as a save's new file versions; their order stays.</summary>
    public void Map(Func<ImmutableArray<AnimationCapture>, ImmutableArray<AnimationCapture>> change)
    {
        foreach (var actor in order)
            lists[actor] = change(lists[actor]);
    }

    /// <summary>The list whose newest clip was heard on object <paramref name="objectIndex"/>; empty when none was.</summary>
    public ImmutableArray<AnimationCapture> At(ushort objectIndex)
    {
        for (var i = order.Count - 1; i >= 0; i--)
            if (lists[order[i]] is { IsEmpty: false } list && list[0].ActorIndex == objectIndex)
                return list;
        return [];
    }
}
