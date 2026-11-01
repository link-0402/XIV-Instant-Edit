using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// One clip of the play group as a frame finds it, in seconds: how long it plays (<paramref name="Length"/>), where its
/// control stands (null when none holds it), whether it played to its end (null when the game dropped it, as it does a clip
/// that doesn't loop), whether it is new or reloaded this frame, whether it already holds its last frame, and whether it was
/// seen playing before its end since it last started (<paramref name="Armed"/>), so an end counts only then.
/// </summary>
internal readonly record struct GroupMember(int Key, float Length, float? Time, bool? AtEnd, bool Started, bool Held, bool Armed = true);

/// <summary>What a member is told: seek to a time, hold its last frame (pause there), play again from 0, or pause.</summary>
internal enum GroupAct : byte
{
    Seek,
    Hold,
    PlayAgain,
    Pause,
}

internal readonly record struct GroupOrder(int Key, GroupAct Act, float Seconds = 0);

/// <summary>
/// The one playhead over every actor's clip that plays through the Timeline. The longest clip leads and the others follow
/// its time, set back on it when more than a frame off; a shorter one holds its last frame until the leader ends, and then
/// every clip plays again from frame 0 together. A clip that starts while the group plays restarts them all; while paused
/// it joins on the shared frame. A lone clip gets no orders and plays as it always did.
/// </summary>
internal static class PlayGroupRules
{
    /// <summary>One frame at 30 fps, in seconds.</summary>
    public const float Drift = 1f / 30;

    // A leader whose time went back this far looped by itself.
    private const float Looped = .5f;

    /// <summary>The member that leads: the longest; a tie goes to the lowest key.</summary>
    public static int Leader(IReadOnlyList<GroupMember> members) =>
        members.OrderByDescending(member => member.Length).ThenBy(member => member.Key).First().Key;

    /// <summary>The shared time after this frame and the orders for the members, in their order.</summary>
    public static (float Shared, ImmutableArray<GroupOrder> Orders) Step(bool playing, float shared, IReadOnlyList<GroupMember> members)
    {
        if (members.Count < 2)
            return (members.Count == 1 && members[0].Time is { } alone ? alone : shared, []);
        var orders = ImmutableArray.CreateBuilder<GroupOrder>();
        if (members.Any(member => member.Started))
        {
            if (playing)
                return (0, [.. members.Select(member => new GroupOrder(member.Key, GroupAct.PlayAgain))]);
            foreach (var member in members.Where(member => member.Started))
            {
                orders.Add(new GroupOrder(member.Key, GroupAct.Seek, Math.Min(shared, member.Length)));
                orders.Add(new GroupOrder(member.Key, GroupAct.Pause));
            }
            return (shared, orders.ToImmutable());
        }
        if (!playing)
            return (shared, []);
        var key = Leader(members);
        var leader = members.First(member => member.Key == key);
        // At its end, or dropped there: only once it was seen playing since it started, as RepeatAtEnd does, so neither a clip
        // not on the character yet nor a GPose freeze at the end restarts every frame.
        if (leader.Armed && leader.AtEnd != false || leader.Time < shared - Looped)
            return (0, [.. members.Select(member => new GroupOrder(member.Key, GroupAct.PlayAgain))]);
        var now = leader.Time ?? shared;
        foreach (var member in members)
        {
            if (member.Key == key || member.Held)
                continue;
            if (now >= member.Length - Drift)
                orders.Add(new GroupOrder(member.Key, GroupAct.Hold, member.Length));
            else if (member.Time is { } time && Math.Abs(time - now) > Drift)
                orders.Add(new GroupOrder(member.Key, GroupAct.Seek, now));
        }
        return (now, orders.ToImmutable());
    }
}
