namespace InstantEdit.Services.Animations.Preview;

/// <summary>A Timeline workspace's playback as the play group sees and drives it, in seconds. Framework thread.</summary>
internal interface IPlayGroupMember
{
    /// <summary>The actor, for the ruler's "Partner One ends here".</summary>
    string ActorName { get; }

    /// <summary>How long it plays (a Preview's built length, Live's cut or stretched one, else the clip's); null while it plays nothing through the Timeline.</summary>
    float? PlayedSeconds { get; }

    /// <summary>
    /// Where its newest round stands: the one the latest play (the group's, or the game's own repeat of a clip that loops)
    /// started. The round it blends in from keeps the most weight for about 0.17 s, still on its old end after a play again.
    /// </summary>
    float? TimeSeconds { get; }

    /// <summary>Whether its newest round played to its end; null when no control holds it (the game dropped a clip that doesn't loop).</summary>
    bool? AtEnd { get; }

    bool Paused { get; }

    /// <summary>True once after it started again by itself (a look or sound reload, a new Preview); then false.</summary>
    bool TakeStarted();

    void Seek(float seconds);

    /// <summary>Pauses on <paramref name="seconds"/>, its last frame, until the group plays everything again.</summary>
    void Hold(float seconds);

    /// <summary>A real play of its timeline from frame 0, so sounds and effects start over too.</summary>
    void PlayAgain();

    void Pause();

    void Resume();
}

/// <summary>
/// The one playhead over every workspace's clip that plays through the Timeline (<see cref="PlayGroupRules"/>). The Timeline
/// window updates it on the framework update after every workspace's own, so every member's play again lands on one
/// framework frame, and a collapsed window doesn't let them drift. With fewer than two members it does nothing: a lone
/// clip plays as it always did.
/// </summary>
internal sealed class TimelinePlayGroup
{
    private readonly Dictionary<IPlayGroupMember, int> keys = new(ReferenceEqualityComparer.Instance);
    private int nextKey;
    // The members that played last update, and those holding their last frame.
    private HashSet<int> known = [];
    private readonly HashSet<int> held = [];
    // The members seen playing before their end since they last started: only their end counts.
    private readonly HashSet<int> armed = [];

    /// <summary>Two or more members play: the group holds the shared frame.</summary>
    public bool Grouped { get; private set; }

    public bool Playing { get; private set; } = true;

    public float SharedSeconds { get; private set; }

    /// <summary>The leader's length: where the group ends and starts over.</summary>
    public float EndSeconds { get; private set; }

    /// <summary>Where each member shorter than the leader ends, by its actor.</summary>
    public IReadOnlyList<(string Actor, float End)> ShorterEnds { get; private set; } = [];


    public void Add(IPlayGroupMember member)
    {
        if (!keys.ContainsKey(member))
            keys[member] = nextKey++;
    }

    public void Remove(IPlayGroupMember member)
    {
        if (keys.Remove(member, out var key))
        {
            known.Remove(key);
            held.Remove(key);
            armed.Remove(key);
        }
    }

    private List<IPlayGroupMember> Active() => keys.Keys.Where(member => member.PlayedSeconds != null).ToList();

    private GroupMember Fact(IPlayGroupMember member)
    {
        var key = keys[member];
        var started = member.TakeStarted() || !known.Contains(key);
        if (started)
            armed.Remove(key);
        var atEnd = member.AtEnd;
        if (atEnd == false)
            armed.Add(key);
        return new GroupMember(key, member.PlayedSeconds!.Value, member.TimeSeconds, atEnd, started, held.Contains(key), armed.Contains(key));
    }

    public void Update()
    {
        var active = Active();
        var facts = active.Select(member => (Member: member, Fact: Fact(member))).ToList();
        known = facts.Select(pair => pair.Fact.Key).ToHashSet();
        held.IntersectWith(known);
        armed.IntersectWith(known);
        Grouped = facts.Count >= 2;
        if (!Grouped)
        {
            // Alone it plays as it always did; the group takes its state, so one joining finds it as it is.
            if (facts.Count == 1)
            {
                Playing = !facts[0].Member.Paused;
                SharedSeconds = facts[0].Fact.Time ?? SharedSeconds;
            }
            held.Clear();
            EndSeconds = facts.Count == 1 ? facts[0].Fact.Length : 0;
            ShorterEnds = [];
            return;
        }
        var (shared, orders) = PlayGroupRules.Step(Playing, SharedSeconds, facts.Select(pair => pair.Fact).ToList());
        SharedSeconds = shared;
        var byKey = facts.ToDictionary(pair => pair.Fact.Key, pair => pair.Member);
        foreach (var order in orders)
            Apply(byKey[order.Key], order);
        EndSeconds = facts.Max(pair => pair.Fact.Length);
        ShorterEnds = facts.Where(pair => pair.Fact.Length < EndSeconds - PlayGroupRules.Drift)
            .Select(pair => (pair.Member.ActorName, pair.Fact.Length)).ToList();
    }

    private void Apply(IPlayGroupMember member, GroupOrder order)
    {
        switch (order.Act)
        {
            case GroupAct.Seek:
                member.Seek(order.Seconds);
                break;
            case GroupAct.Hold:
                member.Hold(order.Seconds);
                held.Add(order.Key);
                break;
            case GroupAct.PlayAgain:
                member.PlayAgain();
                held.Remove(order.Key);
                armed.Remove(order.Key);
                break;
            case GroupAct.Pause:
                member.Pause();
                break;
        }
    }

    /// <summary>Play or Pause: every member pauses, or every one not holding its end plays on.</summary>
    public void TogglePlay()
    {
        Playing = !Playing;
        foreach (var member in Active())
        {
            if (!Playing)
                member.Pause();
            else if (!held.Contains(keys[member]))
                member.Resume();
        }
    }

    /// <summary>A step or a pause from one workspace pauses them all.</summary>
    public void Pause()
    {
        if (!Playing)
            return;
        Playing = false;
        foreach (var member in Active())
            member.Pause();
    }

    /// <summary>From the start: every member plays again from frame 0.</summary>
    public void Replay()
    {
        Playing = true;
        SharedSeconds = 0;
        held.Clear();
        armed.Clear();
        foreach (var member in Active())
            member.PlayAgain();
    }

    /// <summary>A scrub or step to <paramref name="seconds"/>: every member pauses there, one shorter on its last frame.</summary>
    public void SetFrame(float seconds)
    {
        Playing = false;
        SharedSeconds = Math.Max(0, seconds);
        foreach (var member in Active())
        {
            var key = keys[member];
            var length = member.PlayedSeconds!.Value;
            member.Pause();
            if (SharedSeconds >= length - PlayGroupRules.Drift)
            {
                member.Hold(length);
                held.Add(key);
            }
            else
            {
                member.Seek(SharedSeconds);
                held.Remove(key);
            }
        }
    }
}
