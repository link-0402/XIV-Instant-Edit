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
        Group(check);
    }

    /// <summary>A clip that logs what the group tells it.</summary>
    private sealed class Fake(string name, List<string> calls) : IPlayGroupMember
    {
        public string ActorName => name;
        public float? PlayedSeconds { get; set; }
        public float? TimeSeconds { get; set; }
        public bool? AtEnd { get; set; } = false;
        public bool Paused { get; set; }
        public bool Restarted;
        public bool TakeStarted()
        {
            var was = Restarted;
            Restarted = false;
            return was;
        }
        public void Seek(float seconds) => calls.Add(FormattableString.Invariant($"{name} seek {seconds:0.##}"));
        public void Hold(float seconds) => calls.Add(FormattableString.Invariant($"{name} hold {seconds:0.##}"));
        public void PlayAgain() => calls.Add($"{name} again");
        public void Pause() => calls.Add($"{name} pause");
        public void Resume() => calls.Add($"{name} resume");
        public void SetSpeed(float speed) => calls.Add(FormattableString.Invariant($"{name} speed {speed:0.##}"));
    }

    private static void Group(Action<bool, string> report)
    {
        void check(bool ok, string message, List<string> calls) => report(ok, ok ? message : $"{message} ({string.Join(", ", calls)})");

        var calls = new List<string>();
        var group = new TimelinePlayGroup();
        var you = new Fake("you", calls) { PlayedSeconds = 7.33f, TimeSeconds = 1 };
        var aria = new Fake("aria", calls) { PlayedSeconds = 2.33f, TimeSeconds = 1 };
        group.Add(you);
        group.Update();
        check(calls.Count == 0 && !group.Grouped, "play group: one clip alone is never told anything", calls);

        group.Add(aria);
        group.Update();
        check(group.Grouped && calls.Where(call => !call.Contains("speed")).SequenceEqual(["you again", "aria again"]) && calls[0] == "aria speed 1",
            "play group: a second clip joining while playing takes the group's speed and restarts both together", calls);

        calls.Clear();
        you.TimeSeconds = 2;
        aria.TimeSeconds = 2;
        group.Update();
        you.TimeSeconds = 7.33f;
        you.AtEnd = true;
        aria.TimeSeconds = 2.33f;
        group.Update();
        check(calls.Where(call => !call.Contains("hold")).SequenceEqual(["you again", "aria again"]),
            "play group: at the leader's end both play again on one update", calls);

        calls.Clear();
        group.Update();
        check(!calls.Contains("you again"), "play group: the old control still at its end the frame after doesn't restart them again", calls);

        calls.Clear();
        you.AtEnd = false;
        group.SetFrame(4);
        check(calls.SequenceEqual(["you pause", "you seek 4", "aria pause", "aria hold 2.33"]) && !group.Playing && group.SharedSeconds == 4,
            "play group: a scrub pauses both and puts each on the frame, a shorter one on its end", calls);

        calls.Clear();
        group.TogglePlay();
        check(calls.SequenceEqual(["you resume"]) && group.Playing, "play group: Play resumes the clips not holding their end", calls);

        calls.Clear();
        group.SpeedPercent = 50;
        check(calls.SequenceEqual(["you speed 0.5", "aria speed 0.5"]) && group.SpeedPercent == 50, "play group: one speed for every clip", calls);

        check(group.EndSeconds == 7.33f && group.ShorterEnds.SequenceEqual([("aria", 2.33f)]), "play group: the group's end and where the shorter clip ends", calls);

        calls.Clear();
        group.Remove(aria);
        group.Update();
        check(!group.Grouped && calls.Count == 0, "play group: a clip that leaves drops out; the one left plays alone", calls);
    }

    private static GroupMember Member(int key, float length, float? time, bool? atEnd = false, bool started = false, bool held = false, bool armed = true) =>
        new(key, length, time, atEnd, started, held, armed);

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

        var dropped = PlayGroupRules.Step(true, 7.3f, [Member(0, 7.33f, null, atEnd: null), Member(1, 2.33f, 2.33f, held: true)]);
        check(Text(dropped.Orders) == "0:PlayAgain:0 1:PlayAgain:0",
            "play group: a leader that doesn't loop and that the game dropped at its end counts as ended", Text(dropped.Orders));

        var unarmed = PlayGroupRules.Step(true, 0.1f, [Member(0, 7.33f, 7.33f, atEnd: true, armed: false), Member(1, 2.33f, 0.1f)]);
        check(!unarmed.Orders.Any(order => order.Act == GroupAct.PlayAgain),
            "play group: an end not seen playing since the last start (the old control, a GPose freeze) restarts nothing", Text(unarmed.Orders));
    }
}
