using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>Whether a link layer follows its partner now, and why not.</summary>
internal enum LinkState : byte
{
    /// <summary>It follows the partner's bone.</summary>
    On,
    /// <summary>The partner's workspace has no clip ready.</summary>
    NotOpen,
    /// <summary>The partner's workspace has another clip open than the one the link names.</summary>
    OtherClip,
    /// <summary>The partner isn't drawn: despawned or still spawning.</summary>
    Despawned,
    /// <summary>Links only work in GPose, where the actors' sizes can be read.</summary>
    OutOfGPose,
    /// <summary>The partner's preset was deleted.</summary>
    PartnerGone,
    /// <summary>The partner's skeleton has no bone of the link's partner bone's name.</summary>
    NoBone,
    /// <summary>The partner's clip couldn't be read for the link.</summary>
    Unreadable,
    /// <summary>This clip's own motion is still being read.</summary>
    Reading,
    /// <summary>This actor isn't drawn: despawned or still spawning.</summary>
    SelfDespawned,
    /// <summary>The link points at its own workspace, as a layer pasted from another workspace can.</summary>
    ItsOwnClip,
    /// <summary>This clip's own motion couldn't be read.</summary>
    SelfUnreadable,
}

/// <summary>
/// What a workspace shows the others' links: its key, its actor's name, the open clip's game path and binding, the clip as it
/// plays (cut and stretched, the basis Live builds from), the document (its reference is its revision), whether edits wait now,
/// and the actor's drawn size, null while it isn't drawn. Built on the framework thread; immutable.
/// </summary>
internal sealed record LinkView(WorkspaceKey Key, string Name, string GamePath, int Binding, MotionClip Played, TimelineDocument Document,
    bool ReadOnly, float? Size);

/// <summary>
/// What a link's track is built from: the partner's document and played clip, compared by reference, and the link, the size ratio
/// (partner over this actor), this workspace's preset (null for You) and whether it is a look-at's track (<see cref="MotionLooks.AsLink"/>,
/// read from <see cref="MotionLooks.PartnerOwn"/>), compared by value. Equal inputs give the same track.
/// </summary>
internal sealed record LinkInputs(TimelineDocument PartnerDocument, MotionClip Played, MotionLink Link, float Ratio, Guid? Self, bool Look = false)
{
    public bool Equals(LinkInputs? other) =>
        other is not null && (ReferenceEquals(this, other) ||
            ReferenceEquals(PartnerDocument, other.PartnerDocument) && ReferenceEquals(Played, other.Played) && SameLink(Link, other.Link) &&
            Ratio.Equals(other.Ratio) && Self == other.Self && Look == other.Look);

    public override int GetHashCode() =>
        HashCode.Combine(RuntimeHelpers.GetHashCode(PartnerDocument), RuntimeHelpers.GetHashCode(Played), Link.Bone, Link.PartnerBone, Ratio, Self, Look);

    /// <summary>The links field by field, holds by sequence (a record compares the holds' arrays by reference).</summary>
    private static bool SameLink(MotionLink a, MotionLink b) => ReferenceEquals(a, b) ||
        SameJoin(a, b) && a.HoldTurn == b.HoldTurn && a.Top == b.Top && a.Bend.Equals(b.Bend) && a.Holds.SequenceEqual(b.Holds);

    /// <summary>
    /// Whether two links join the same bone to the same partner's bone on the same clip: a track built for one is the other's too,
    /// up to how it bends.
    /// </summary>
    public static bool SameJoin(MotionLink a, MotionLink b) =>
        a.Bone == b.Bone && a.Partner == b.Partner && a.PartnerBone == b.PartnerBone && a.PartnerClip == b.PartnerClip && a.PartnerBinding == b.PartnerBinding;
}

/// <summary>
/// What a workspace shows before its motion is read: its key, its actor's name, the open clip's game path and binding, the document,
/// whether edits wait now, the skeleton it is edited on when that is known, and whether reading its motion failed. Null without an
/// editable body clip. Reads nothing.
/// </summary>
internal sealed record LinkCandidate(WorkspaceKey Key, string Name, string GamePath, int Binding, TimelineDocument Document, bool ReadOnly,
    SkeletonDescription? Skeleton, bool ReadFailed = false);

/// <summary>
/// A workspace as <see cref="PartnerLinks"/> sees it: its key, its <see cref="LinkCandidate"/> (null without an editable body clip),
/// how to get its full view, which starts reading its motion and so is asked only of a workspace a link or the picker wants, and
/// the session it hands tracks to.
/// </summary>
internal readonly record struct LinkTarget(WorkspaceKey Key, LinkCandidate? Candidate, Func<LinkView?> View, TimelineSession Session);

/// <summary>
/// The partner tracks of every workspace's link layers, on the Timeline window's framework update after the workspaces' own: it
/// plans each link from the workspaces' views, builds the tracks whose inputs changed on a worker, at most as often as Live builds
/// a copy of that size, and hands each session its tracks, states and whether a build runs. The newest inputs always build in the
/// end. Every call comes from the framework thread; no ImGui.
/// </summary>
internal sealed class PartnerLinks : IDisposable
{
    /// <summary>No link states: the one empty instance, so a session without links keeps it.</summary>
    public static readonly ImmutableDictionary<string, LinkState> NoStates = ImmutableDictionary<string, LinkState>.Empty.WithComparers(StringComparer.Ordinal);

    // Logs why a track couldn't be built, once per inputs.
    private readonly Action<Exception, string> warn;
    private readonly Dictionary<(WorkspaceKey Workspace, string Layer), Entry> entries = [];
    // Milliseconds, as Environment.TickCount64 counts them.
    private readonly Func<long> clock;
    // Each picker asking for the others' views, until when (see Want).
    private readonly Dictionary<WorkspaceKey, long> pickers = [];

    /// <param name="warn">Logs a build that failed for another reason than a missing bone.</param>
    /// <param name="clock">The time in milliseconds; <see cref="Environment.TickCount64"/> when null.</param>
    public PartnerLinks(Action<Exception, string> warn, Func<long>? clock = null)
    {
        this.warn = warn;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>One link layer's builds: the inputs its track was built from, the build running, and the inputs that failed.</summary>
    private sealed class Entry
    {
        public LinkInputs? Built;
        public LinkTrack? Track;
        public LinkInputs? Failed;
        public LinkState FailedState;
        public LinkInputs? Running;
        public Task<LinkTrack>? Task;
        public CancellationTokenSource? Cancel;
        // When the last build ended (by the clock), and the size of the clip it read.
        public long EndedAt = long.MinValue / 2;
        public long Bytes;
        public bool Seen;
    }

    /// <summary>The workspaces' ready views from the last update, for the picker.</summary>
    public IReadOnlyList<LinkView> Views { get; private set; } = [];

    /// <summary>
    /// How link layer <paramref name="layer"/> of workspace <paramref name="self"/> (whose view is <paramref name="me"/>) stands with
    /// its partner's view <paramref name="partner"/>, and the track's inputs when it is on. A link to its own workspace is never on.
    /// <paramref name="presetExists"/> says whether the partner's preset still exists (always true for You). <paramref name="selfFailed"/>
    /// and <paramref name="partnerFailed"/> say that reading this clip's or the partner's motion failed (<see cref="LinkCandidate.ReadFailed"/>),
    /// so a missing view won't come. This side's states come before the partner's. A look-at layer that follows a partner plans
    /// as its link (<see cref="MotionLooks.AsLink"/>) with the same states and size ratio, its inputs marked as a look: the
    /// partner's bone sits at its model position times the partner's size over this actor's, in this character's space. Pure.
    /// </summary>
    public static (LinkState State, LinkInputs? Inputs) Plan(WorkspaceKey self, MotionLayer layer, LinkView? me, LinkView? partner, bool gposing,
        bool presetExists, bool selfFailed = false, bool partnerFailed = false)
    {
        var look = layer.Link == null && MotionLooks.FollowsPartner(layer);
        if ((layer.Link ?? (look ? MotionLooks.AsLink(layer) : null)) is not { } link)
            return (LinkState.NotOpen, null);
        var partnerKey = new WorkspaceKey(link.Partner);
        if (partnerKey == self)
            return (LinkState.ItsOwnClip, null);
        if (!partnerKey.IsSelf && !presetExists)
            return (LinkState.PartnerGone, null);
        // Outside GPose no actor's size can be read, so this comes before the sizes.
        if (!gposing)
            return (LinkState.OutOfGPose, null);
        if (me == null)
            return (selfFailed ? LinkState.SelfUnreadable : LinkState.Reading, null);
        if (me.Size is not { } mine)
            return (LinkState.SelfDespawned, null);
        if (partner == null || partner.Key != partnerKey)
            return (partnerFailed ? LinkState.Unreadable : LinkState.NotOpen, null);
        if (!string.Equals(partner.GamePath, link.PartnerClip, StringComparison.OrdinalIgnoreCase) || partner.Binding != link.PartnerBinding)
            return (LinkState.OtherClip, null);
        if (partner.Size is not { } theirs)
            return (LinkState.Despawned, null);
        return (LinkState.On, new LinkInputs(partner.Document, partner.Played, link, theirs / mine, self.Preset, look));
    }

    /// <summary>
    /// The link a layer that may change the motion follows its partner by: a link layer's own, or a look at a partner's as a link
    /// (<see cref="MotionLooks.AsLink"/>). Null for every other layer, and for one that is off itself.
    /// </summary>
    private static MotionLink? Followed(MotionLayer layer) =>
        MotionLinks.MayChange(layer) ? layer.Link : MotionLooks.MayChange(layer) ? MotionLooks.AsLink(layer) : null;

    /// <summary>The workspaces with an editable body clip from the last update, whose motion may not be read yet.</summary>
    public IReadOnlyList<LinkCandidate> Candidates { get; private set; } = [];

    /// <summary>Whether a workspace other than <paramref name="self"/> has an editable body clip to link to; reads no motion.</summary>
    public bool HasPartner(WorkspaceKey self) => Candidates.Any(candidate => candidate.Key != self);

    /// <summary>
    /// The picker of workspace <paramref name="self"/> asks for the others' views: they are read on the updates of the next
    /// <see cref="WantFor"/> ms, so an open picker gets them while one that closed stops asking.
    /// </summary>
    public void Want(WorkspaceKey self) => pickers[self] = clock() + WantFor;

    /// <summary>How long, in milliseconds, a picker's ask keeps the other workspaces' views read.</summary>
    public const long WantFor = 2000;

    /// <summary>Every framework update after the workspaces' own: plans, builds and hands out every workspace's links.</summary>
    public void Update(IReadOnlyCollection<TimelineWorkspace> workspaces, bool gposing, Func<Guid, bool> presetExists) =>
        UpdateViews([.. workspaces.Select(workspace => new LinkTarget(workspace.Key, workspace.LinkCandidate(), workspace.LinkView, workspace.Session))],
            gposing, presetExists);

    /// <summary>
    /// The workspaces whose full views are read this update: with two workspaces or more, each with a link layer that may change its
    /// motion, the workspace each such link names, and every workspace but a picker's own while that picker asks. Reading a view
    /// starts reading its clip's motion, so nobody without links or an open picker pays for it.
    /// </summary>
    private HashSet<WorkspaceKey> Wanted(IReadOnlyList<LinkTarget> targets, long now)
    {
        var wanted = new HashSet<WorkspaceKey>();
        if (targets.Count < 2)
        {
            pickers.Clear();
            return wanted;
        }
        foreach (var target in targets)
        {
            if (target.Candidate is not { } candidate)
                continue;
            foreach (var layer in candidate.Document.Motion)
                if (Followed(layer) is { } link)
                {
                    wanted.Add(target.Key);
                    wanted.Add(new WorkspaceKey(link.Partner));
                }
        }
        foreach (var (self, until) in pickers.ToArray())
        {
            if (now >= until)
            {
                pickers.Remove(self);
                continue;
            }
            foreach (var target in targets)
                if (target.Key != self)
                    wanted.Add(target.Key);
        }
        return wanted;
    }

    /// <summary><see cref="Update(IReadOnlyCollection{TimelineWorkspace}, bool, Func{Guid, bool})"/> on the workspaces' candidates and views.</summary>
    internal void UpdateViews(IReadOnlyList<LinkTarget> targets, bool gposing, Func<Guid, bool> presetExists)
    {
        var now = clock();
        Poll(now);
        Candidates = [.. targets.Select(target => target.Candidate).OfType<LinkCandidate>()];
        var wanted = Wanted(targets, now);
        var views = new Dictionary<WorkspaceKey, LinkView>();
        foreach (var target in targets)
            if (target.Candidate != null && wanted.Contains(target.Key) && target.View() is { } view)
                views[target.Key] = view;
        Views = [.. views.Values];
        // The workspaces whose motion couldn't be read: their views won't come.
        var failed = Candidates.Where(candidate => candidate.ReadFailed).Select(candidate => candidate.Key).ToHashSet();
        foreach (var entry in entries.Values)
            entry.Seen = false;

        foreach (var target in targets)
        {
            var tracks = ImmutableDictionary.CreateBuilder<string, LinkTrack>(StringComparer.Ordinal);
            var states = ImmutableDictionary.CreateBuilder<string, LinkState>(StringComparer.Ordinal);
            var building = false;
            // A workspace whose view isn't read (alone, or its motion still read) plans with none: its links are off, saying why.
            if (target.Candidate is { } candidate)
                foreach (var layer in candidate.Document.Motion)
                {
                    if (Followed(layer) is not { } followed)
                        continue;
                    var partnerKey = new WorkspaceKey(followed.Partner);
                    var (state, inputs) = Plan(target.Key, layer, views.GetValueOrDefault(target.Key), views.GetValueOrDefault(partnerKey), gposing,
                        partnerKey.Preset is not { } preset || presetExists(preset), candidate.ReadFailed, failed.Contains(partnerKey));
                    // A view nobody asked for (alone, no partner to link to) isn't being read: the partner's clip isn't open.
                    if (state == LinkState.Reading && !wanted.Contains(target.Key))
                        state = LinkState.NotOpen;
                    // An off link has no track; its entry goes below, with any build of it.
                    if (inputs != null)
                    {
                        var id = (target.Key, layer.Key);
                        if (!entries.TryGetValue(id, out var entry))
                            entries[id] = entry = new Entry();
                        entry.Seen = true;
                        if (inputs.Equals(entry.Failed))
                            state = entry.FailedState;
                        else
                        {
                            // Another bone or partner: the last track is another bone's, so it goes now, with a build for it.
                            if (entry.Built is { } built && !LinkInputs.SameJoin(built.Link, inputs.Link))
                            {
                                entry.Built = null;
                                entry.Track = null;
                            }
                            if (entry.Running is { } running && !LinkInputs.SameJoin(running.Link, inputs.Link))
                                entry.Cancel?.Cancel();
                            if (!inputs.Equals(entry.Built))
                            {
                                if (entry.Task == null && now - entry.EndedAt >= MotionLiveDisplay.BuildInterval(entry.Bytes))
                                    Start(entry, inputs);
                                building = true;
                            }
                            // While a newer track builds the last one stands, so Live doesn't drop the link between builds.
                            if (entry.Track != null)
                                tracks[layer.Key] = entry.Track;
                        }
                        building |= entry.Task != null;
                    }
                    states[layer.Key] = state;
                }
            Hand(target.Session, tracks, states, building);
        }

        List<(WorkspaceKey, string)>? gone = null;
        foreach (var (id, entry) in entries)
            if (!entry.Seen)
                (gone ??= []).Add(id);
        if (gone != null)
            foreach (var id in gone)
            {
                entries[id].Cancel?.Cancel();
                entries.Remove(id);
            }
    }

    /// <summary>
    /// Builds the track of <paramref name="inputs"/> on a worker, at the partner's bone: for a link the partner's own edits above its
    /// link back, for a look every partner layer but links and looks at a partner.
    /// </summary>
    private static void Start(Entry entry, LinkInputs inputs)
    {
        var layers = inputs.PartnerDocument.Motion;
        var own = inputs.Look ? MotionLooks.PartnerOwn(layers) : MotionLinks.Own(layers, MotionLinks.LinkBack(layers, inputs.Link, inputs.Self));
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        entry.Cancel = cancel;
        entry.Running = inputs;
        entry.Bytes = (long)inputs.Played.Samples * inputs.Played.Bones * 40;
        entry.Task = Task.Run(() => MotionLinks.Track(inputs.Played, own, inputs.Link.PartnerBone, inputs.Ratio, token), token);
    }

    /// <summary>Takes the finished builds: a track, or why it failed, which stays until the inputs change.</summary>
    private void Poll(long now)
    {
        foreach (var entry in entries.Values)
        {
            if (entry.Task is not { IsCompleted: true } done || entry.Running is not { } inputs)
                continue;
            entry.Task = null;
            entry.Running = null;
            entry.Cancel?.Dispose();
            entry.Cancel = null;
            entry.EndedAt = now;
            if (done.IsCanceled)
                continue;
            if (done.IsFaulted)
            {
                var error = done.Exception!.GetBaseException();
                var missing = error is InvalidDataException && error.Message == MotionEvaluator.Missing(inputs.Link.PartnerBone).Message;
                entry.Failed = inputs;
                entry.FailedState = missing ? LinkState.NoBone : LinkState.Unreadable;
                entry.Built = null;
                entry.Track = null;
                if (!missing)
                    warn(error, $"A link to {inputs.Link.PartnerBone} couldn't read the partner's clip.");
                continue;
            }
            entry.Built = inputs;
            // A rebuild to the same content keeps the instance, so the set stays and Live and the Bones window redo nothing.
            if (entry.Track?.Print != done.Result.Print)
                entry.Track = done.Result;
            entry.Failed = null;
        }
    }

    /// <summary>Hands <paramref name="session"/> its tracks and states, keeping each instance while nothing in it changed.</summary>
    private static void Hand(TimelineSession session, ImmutableDictionary<string, LinkTrack>.Builder tracks,
        ImmutableDictionary<string, LinkState>.Builder states, bool building)
    {
        var links = session.Links;
        if (links.Tracks.Count != tracks.Count || tracks.Any(pair => !ReferenceEquals(links.For(pair.Key), pair.Value)))
            session.Links = tracks.Count == 0 ? MotionLinkSet.Empty : new MotionLinkSet(tracks.ToImmutable());
        var known = session.LinkStates;
        if (known.Count != states.Count || states.Any(pair => !known.TryGetValue(pair.Key, out var was) || was != pair.Value))
            session.LinkStates = states.Count == 0 ? NoStates : states.ToImmutable();
        session.LinksBuilding = building;
    }

    /// <summary>Drops every track and cancels every build, when the window closes; the sessions get theirs again on the next update.</summary>
    public void Forget()
    {
        foreach (var entry in entries.Values)
            entry.Cancel?.Cancel();
        entries.Clear();
        pickers.Clear();
        Views = [];
        Candidates = [];
    }

    public void Dispose() => Forget();
}
