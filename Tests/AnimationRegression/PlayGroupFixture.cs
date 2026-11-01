using InstantEdit.Services.Animations.Preview;

/// <summary>
/// The one playhead over every actor's clip that plays through the Timeline: the longest leads, a shorter one holds its
/// last frame, all restart together at the leader's end, and a lone clip plays as it always did. The game's side (seeks,
/// plays on one framework frame) is checked in game.
/// </summary>
internal static class PlayGroupFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Rules(check);
    }

    private static GroupMember Member(int key, float length, float? time, bool atEnd = false, bool started = false, bool held = false) =>
        new(key, length, time, atEnd, started, held);

    private static string Text(IEnumerable<GroupOrder> orders) =>
        string.Join(" ", orders.Select(o => FormattableString.Invariant($"{o.Key}:{o.Act}:{o.Seconds:0.##}")));

    private static void Rules(Action<bool, string> report)
    {
        // The orders a failing check got go with its message.
        void check(bool ok, string message, string detail = "") => report(ok, ok || detail.Length == 0 ? message : $"{message} ({detail})");
        var lone = PlayGroupRules.Step(true, 2, [Member(0, 7.33f, 7.33f, atEnd: true, started: true)]);
        check(lone.Orders.IsEmpty && lone.Shared == 7.33f, "play group: a lone clip gets no orders and plays as before");

        check(PlayGroupRules.Leader([Member(0, 7.33f, 1), Member(1, 2.33f, 1)]) == 0 &&
              PlayGroupRules.Leader([Member(3, 2f, 1), Member(1, 2f, 1)]) == 1,
            "play group: the longest clip leads, a tie the lowest key");

        var steady = PlayGroupRules.Step(true, 2.9f, [Member(0, 7.33f, 3.0f), Member(1, 5f, 3.02f)]);
        var drifted = PlayGroupRules.Step(true, 2.9f, [Member(0, 7.33f, 3.0f), Member(1, 5f, 3.1f)]);
        check(steady.Orders.IsEmpty && steady.Shared == 3.0f && Text(drifted.Orders) == "1:Seek:3",
            "play group: a member within a frame is left alone, one further off is set back on the leader", Text(drifted.Orders));

        var hold = PlayGroupRules.Step(true, 2.3f, [Member(0, 7.33f, 2.32f), Member(1, 2.33f, 2.32f)]);
        var held = PlayGroupRules.Step(true, 2.3f, [Member(0, 7.33f, 4f), Member(1, 2.33f, 2.33f, held: true)]);
        check(Text(hold.Orders) == "1:Hold:2.33" && held.Orders.IsEmpty,
            "play group: a shorter clip holds its last frame until the leader ends", Text(hold.Orders));

        var end = PlayGroupRules.Step(true, 7.3f, [Member(0, 7.33f, 7.33f, atEnd: true), Member(1, 2.33f, 2.33f, held: true)]);
        check(Text(end.Orders) == "0:PlayAgain:0 1:PlayAgain:0" && end.Shared == 0, "play group: at the leader's end every clip plays again", Text(end.Orders));

        var looped = PlayGroupRules.Step(true, 7.2f, [Member(0, 7.33f, 0.1f), Member(1, 2.33f, 2.33f, held: true)]);
        check(Text(looped.Orders) == "0:PlayAgain:0 1:PlayAgain:0", "play group: a leader that looped by itself restarts the others with it", Text(looped.Orders));

        var joinPlaying = PlayGroupRules.Step(true, 3f, [Member(0, 7.33f, 3f), Member(1, 2.33f, 0, started: true)]);
        check(Text(joinPlaying.Orders) == "0:PlayAgain:0 1:PlayAgain:0" && joinPlaying.Shared == 0,
            "play group: a clip starting while the group plays restarts every one from frame 0", Text(joinPlaying.Orders));

        var joinPaused = PlayGroupRules.Step(false, 4f, [Member(0, 7.33f, 4f), Member(1, 2.33f, 0, started: true)]);
        check(Text(joinPaused.Orders) == "1:Seek:2.33 1:Pause:0" && joinPaused.Shared == 4f,
            "play group: a clip starting while paused joins paused on the shared frame, at its end when shorter", Text(joinPaused.Orders));

        var paused = PlayGroupRules.Step(false, 4f, [Member(0, 7.33f, 4f), Member(1, 2.33f, 2.33f)]);
        check(paused.Orders.IsEmpty && paused.Shared == 4f, "play group: paused, nothing moves by itself");
    }
}
