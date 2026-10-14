using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Every change the editor makes to a document. Each takes a document and returns a new one, or the
/// same instance when nothing changes, so history can tell a real edit by reference. Locked events
/// are never changed by the user. No ImGui or Dalamud dependencies.
/// </summary>
internal static class TimelineEdits
{
    public static bool CanMove(TimelineEvent ev) => ev.Access is TimelineAccess.Editable or TimelineAccess.Movable;

    /// <summary>
    /// Whether the event's length can change: never for a motion at its own speed, whose length the
    /// game ignores, nor for a kept face, which has none.
    /// </summary>
    public static bool CanResize(TimelineEvent ev) => ev.Access == TimelineAccess.Editable && ev.Resizable &&
        !TimelineDocument.OwnPace(ev) && !TimelineDocument.Kept(ev);

    public static bool CanEdit(TimelineEvent ev) => ev.Access == TimelineAccess.Editable;

    /// <summary>
    /// Start plus duration without overflowing: durations are int32 and mods use values such as
    /// 9999999 for "until the end", which the editor keeps as they are.
    /// </summary>
    public static int End(TimelineEvent ev) => (int)Math.Min(int.MaxValue, (long)ev.Start + Math.Max(0, ev.Duration));

    /// <summary>The index of the event with <paramref name="key"/>, or -1.</summary>
    public static int IndexOf(TimelineDocument doc, string key)
    {
        var events = Events(doc);
        for (var index = 0; index < events.Length; index++)
            if (events[index].Key == key)
                return index;
        return -1;
    }

    public static TimelineEvent? Find(TimelineDocument doc, string? key) =>
        key != null && IndexOf(doc, key) is var index and >= 0 ? doc.Events[index] : null;

    /// <summary>
    /// <paramref name="delta"/> limited so every movable event in <paramref name="keys"/> keeps its
    /// start in [0, Length - 1]. An event already past the end may move back but not further out.
    /// </summary>
    public static int ClampMoveDelta(TimelineDocument doc, IEnumerable<string> keys, int delta)
    {
        var set = Set(keys);
        var first = int.MaxValue;
        var last = int.MinValue;
        foreach (var ev in Events(doc))
            if (set.Contains(ev.Key) && CanMove(ev))
            {
                first = Math.Min(first, ev.Start);
                last = Math.Max(last, ev.Start);
            }
        return first == int.MaxValue ? 0 : ClampGroup(doc, first, last, delta);
    }

    /// <summary>Moves the Editable and Movable events in <paramref name="keys"/> together by <paramref name="delta"/> frames.</summary>
    public static TimelineDocument Move(TimelineDocument doc, IEnumerable<string> keys, int delta)
    {
        var set = Set(keys);
        delta = ClampMoveDelta(doc, set, delta);
        if (delta == 0)
            return doc;
        var events = Events(doc).ToBuilder();
        for (var index = 0; index < events.Count; index++)
            if (set.Contains(events[index].Key) && CanMove(events[index]))
                events[index] = events[index] with { Start = events[index].Start + delta };
        return doc with { Events = events.ToImmutable() };
    }

    /// <summary>
    /// Drags one edge of an Editable, Resizable event by <paramref name="delta"/> frames. The other
    /// edge stays put; the duration stays at least one frame and the start at frame 0 or later. The
    /// right edge grows to at most <see cref="TimelineDocument.MaxFrames"/>, or the duration it had.
    /// </summary>
    public static TimelineDocument Resize(TimelineDocument doc, string key, bool leftEdge, int delta)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || delta == 0 || !CanResize(doc.Events[index]))
            return doc;
        var ev = doc.Events[index];
        int start, duration;
        if (leftEdge)
        {
            var end = (long)ev.Start + Math.Max(0, ev.Duration);
            start = (int)Math.Clamp((long)ev.Start + delta, 0, Math.Max(0, Math.Min(TimelineDocument.MaxFrames, end - 1)));
            // "Until the end" (past MaxFrames) stays that: only the start moves.
            duration = ev.Duration > TimelineDocument.MaxFrames ? ev.Duration : (int)Math.Clamp(end - start, 1, int.MaxValue);
        }
        else
        {
            start = ev.Start;
            duration = (int)Math.Clamp((long)ev.Duration + delta, 1, Math.Max(Math.Max(1, TimelineDocument.MaxFrames - start), ev.Duration));
        }
        if (start == ev.Start && duration == ev.Duration)
            return doc;
        return doc with { Events = doc.Events.SetItem(index, ev with { Start = start, Duration = duration }) };
    }

    /// <summary>
    /// Changes the length to <paramref name="frames"/>, clamped to the editor's limits. Motion clips
    /// that lasted the whole old length (give or take a frame) last the new one. With
    /// <paramref name="scaleEvents"/> every start and every non-zero duration scales with the length
    /// (rounded half away from zero), and a start that played before still plays; without it nothing
    /// else moves. Durations past <see cref="TimelineDocument.MaxFrames"/> mean "until the end" in
    /// mods and never scale, nor do those of motions at their own speed, which the game ignores.
    /// With <paramref name="loaded"/> (the document as it was loaded), the motions that follow are
    /// the ones that lasted the loaded length, and going back to that length puts what the user
    /// can't move (Locked starts and lengths, Movable lengths) back exactly where it was.
    /// </summary>
    public static TimelineDocument SetLength(TimelineDocument doc, int frames, bool scaleEvents, TimelineDocument? loaded = null)
    {
        var length = Math.Clamp(frames, TimelineDocument.MinLength, TimelineDocument.MaxLength);
        var old = doc.Length;
        if (length == old)
            return doc;
        var ratio = old > 0 ? (double)length / old : 1;
        var original = loaded == null ? null : Events(loaded).ToDictionary(ev => ev.Key, StringComparer.Ordinal);
        // A cut moved what the user can't, with the motion: back at the loaded length it stays where the cut put it.
        var restore = loaded != null && length == loaded.Length && !doc.Cut;
        var events = Events(doc).ToBuilder();
        for (var index = 0; index < events.Count; index++)
        {
            var ev = events[index];
            var start = ev.Start;
            var duration = ev.Duration;
            if (scaleEvents && old > 0)
            {
                start = TimelineSnap.Round(ev.Start * ratio);
                if (ev.Start < old)
                    start = Math.Min(start, length - 1);
                start = Math.Clamp(start, 0, TimelineDocument.MaxFrames);
                if (duration is > 0 and <= TimelineDocument.MaxFrames && !TimelineDocument.OwnPace(ev))
                    duration = Math.Clamp(TimelineSnap.Round(ev.Duration * ratio), 1, TimelineDocument.MaxFrames);
            }
            if (ev.Lane == TimelineLane.Motion && (original == null
                    ? doc.LastsWhole(ev.Duration)
                    : original.TryGetValue(ev.Key, out var motion) && loaded!.LastsWhole(motion.Duration)))
                duration = length;
            if (restore && original!.TryGetValue(ev.Key, out var before))
            {
                if (ev.Access == TimelineAccess.Locked)
                    (start, duration) = (before.Start, before.Duration);
                else if (ev.Access == TimelineAccess.Movable)
                    duration = before.Duration;
            }
            if (start != ev.Start || duration != ev.Duration)
                events[index] = ev with { Start = start, Duration = duration };
        }
        return doc with { Length = length, Events = events.ToImmutable() };
    }

    /// <summary>Events that start at or after the end of the clip's motion. Vanilla has none.</summary>
    public static ImmutableArray<TimelineEvent> PastEnd(TimelineDocument doc) => [.. Events(doc).Where(ev => ev.Start >= doc.Length)];

    /// <summary>The frames the view scrolls over: the length, or up to the last start when events start past it.</summary>
    public static int Extent(TimelineDocument doc)
    {
        var extent = doc.Length;
        foreach (var ev in Events(doc))
            extent = Math.Max(extent, Math.Min(TimelineDocument.MaxFrames, ev.Start) + 1);
        return extent;
    }

    /// <summary>
    /// Whether <paramref name="ev"/> plays a face or lip motion from the timeline's expression library
    /// (TMB TMPP): one on the expression or lip sync lane that isn't among the motions the face always
    /// has loaded (<paramref name="resident"/>, by motion name). Without that list every face counts,
    /// so a library is never dropped while it might be needed.
    /// </summary>
    public static bool FromPack(TimelineDocument doc, TimelineEvent ev, IReadOnlySet<string>? resident) =>
        !string.IsNullOrEmpty(doc.FacePack) && ev.Lane is TimelineLane.Expression or TimelineLane.LipSync && ev.Path.Length > 0 &&
        resident?.Contains(ev.Path) != true;

    /// <summary>Whether an event of <paramref name="doc"/>, other than <paramref name="except"/>, plays a motion from its library (<see cref="FromPack"/>).</summary>
    public static bool UsesPack(TimelineDocument doc, IReadOnlySet<string>? resident, string? except = null) =>
        Events(doc).Any(ev => ev.Key != except && FromPack(doc, ev, resident));

    /// <summary>
    /// <paramref name="next"/>, an edit of <paramref name="before"/>, without its expression library once
    /// the edit took away the last face that played from it: a timeline loads only one, so another
    /// library's faces can come in. A library the timeline never used keeps its place until replaced.
    /// </summary>
    public static TimelineDocument ReleasePack(TimelineDocument before, TimelineDocument next, IReadOnlySet<string>? resident) =>
        !string.IsNullOrEmpty(next.FacePack) && string.Equals(before.FacePack, next.FacePack, StringComparison.OrdinalIgnoreCase) &&
        UsesPack(before, resident) && !UsesPack(next, resident)
            ? next with { FacePack = null }
            : next;

    /// <summary>
    /// Appends <paramref name="events"/> with new "n" keys. Events sharing a group keep sharing one,
    /// new for this Add. A <paramref name="pack"/> (the expression library they need) becomes the
    /// timeline's when it has none, or one no face plays from (<see cref="UsesPack"/> with
    /// <paramref name="resident"/>); a timeline loads only one, so a different one in use is refused.
    /// Added events are never conditional: saving puts each on a new track without a condition, even
    /// a copy of a conditional one.
    /// </summary>
    public static TimelineDocument Add(TimelineDocument doc, IEnumerable<TimelineEvent> events, string? pack, out string? error,
        IReadOnlySet<string>? resident = null) =>
        Add(doc, events, pack, out _, out error, resident);

    public static TimelineDocument Add(TimelineDocument doc, IEnumerable<TimelineEvent> events, string? pack,
        out ImmutableArray<string> created, out string? error, IReadOnlySet<string>? resident = null)
    {
        created = [];
        error = null;
        var added = events.ToList();
        if (added.Count == 0)
            return doc;
        if (added.Any(ev => ev.Start < 0 || ev.Start > TimelineDocument.MaxFrames || ev.Duration < 0 && !TimelineDocument.Kept(ev)))
        {
            error = "The event is outside the timeline.";
            return doc;
        }
        var facePack = doc.FacePack;
        if (!string.IsNullOrEmpty(pack))
        {
            if (string.IsNullOrEmpty(facePack) || !string.Equals(facePack, pack, StringComparison.OrdinalIgnoreCase) && !UsesPack(doc, resident))
                facePack = pack;
            else if (!string.Equals(facePack, pack, StringComparison.OrdinalIgnoreCase))
            {
                error = PackInUse(facePack);
                return doc;
            }
        }

        var existing = Events(doc);
        var keys = existing.Select(ev => ev.Key).ToHashSet(StringComparer.Ordinal);
        var groups = existing.Where(ev => ev.Group != null).Select(ev => ev.Group!).ToHashSet(StringComparer.Ordinal);
        var newGroups = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = Math.Max(0, doc.NextKey);
        var builder = existing.ToBuilder();
        var names = ImmutableArray.CreateBuilder<string>(added.Count);
        foreach (var ev in added)
        {
            string key;
            do key = "n" + (next++).ToString(CultureInfo.InvariantCulture);
            while (!keys.Add(key));
            string? group = null;
            if (ev.Group != null && !newGroups.TryGetValue(ev.Group, out group))
            {
                group = "g" + key[1..];
                for (var suffix = 2; !groups.Add(group); suffix++)
                    group = "g" + key[1..] + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                newGroups[ev.Group] = group;
            }
            // A copy of a loaded entry remembers it, so saving copies all of its bytes. Faces loop
            // only on an expression's timeline.
            builder.Add(ev with { Key = key, Group = group, Conditional = false, CloneOf = ev.Key.StartsWith('t') ? ev.Key : ev.CloneOf,
                Loop = ev.Loop && doc.Expression });
            names.Add(key);
        }
        created = names.MoveToImmutable();
        return doc with { Events = builder.ToImmutable(), NextKey = next, FacePack = facePack };
    }

    /// <summary>
    /// Copies the Editable events in <paramref name="keys"/> <paramref name="offset"/> frames later,
    /// with new keys and groups. The offset is limited like a move so the copies start inside the timeline.
    /// </summary>
    public static TimelineDocument Duplicate(TimelineDocument doc, IEnumerable<string> keys, int offset, out ImmutableArray<string> created)
    {
        var set = Set(keys);
        var sources = Events(doc).Where(ev => set.Contains(ev.Key) && CanEdit(ev)).ToList();
        if (sources.Count == 0)
        {
            created = [];
            return doc;
        }
        offset = ClampGroup(doc, sources.Min(ev => ev.Start), sources.Max(ev => ev.Start), offset);
        return Add(doc, sources.Select(ev => ev with { Start = ev.Start + offset }), null, out created, out _);
    }

    /// <summary>Removes the Editable and Movable events in <paramref name="keys"/>. The expression library stays.</summary>
    public static TimelineDocument Delete(TimelineDocument doc, IEnumerable<string> keys)
    {
        var set = Set(keys);
        var events = Events(doc);
        var kept = events.Where(ev => !(set.Contains(ev.Key) && CanMove(ev))).ToImmutableArray();
        return kept.Length == events.Length ? doc : doc with { Events = kept };
    }

    /// <summary>
    /// Replaces the event with <paramref name="ev"/>'s key by <paramref name="ev"/>, as far as its access
    /// allows: an Editable event takes everything but its key, kind, lane and access; a Movable one
    /// only its start; a Locked one nothing. What the loader derives from the entry's kind and track
    /// (Resizable, LockReason, Conditional) is kept too, since saving checks it against the written
    /// file, and only a resizable event takes a new duration (at least one frame; an unchanged one is
    /// kept exactly). Starts stay inside the TMB's range. A motion at its own speed that gets time
    /// control and no new duration lasts what it played (<see cref="TimelineDocument.PlayedFrames"/>
    /// with <paramref name="motionFrames"/>), so it keeps playing the same. Only an effect keeps a
    /// look, and only while it keeps its file: a look is made for one effect's colours. A looping face
    /// that becomes one that can't loop (<see cref="CanLoop"/>), such as a pose, stops looping. Only a
    /// sound keeps a pin, and only while it plays the same sound of the same file.
    /// </summary>
    public static TimelineDocument Replace(TimelineDocument doc, TimelineEvent ev, IReadOnlyDictionary<string, int>? motionFrames = null)
    {
        var index = IndexOf(doc, ev.Key);
        if (index < 0)
            return doc;
        var old = doc.Events[index];
        var start = Math.Clamp(ev.Start, 0, TimelineDocument.MaxFrames);
        var updated = old.Access switch
        {
            TimelineAccess.Editable => ev with
            {
                Key = old.Key, Magic = old.Magic, Lane = old.Lane, Access = old.Access, Start = start,
                Resizable = old.Resizable, LockReason = old.LockReason, Conditional = old.Conditional, CloneOf = old.CloneOf,
                Duration = !old.Resizable || ev.Duration == old.Duration ? old.Duration : Math.Max(1, ev.Duration),
                Values = ev.Values ?? old.Values,
                Look = old.Lane == TimelineLane.Effect && ev.Path == old.Path && ev.Look is { IsEmpty: false } look ? look : null,
                // A pin names an alternative of one sound of one file.
                Pin = old.Lane == TimelineLane.Sound && TimelineSoundKey.Of(ev) is { } key && key == TimelineSoundKey.Of(old) ? ev.Pin : null,
            },
            TimelineAccess.Movable => old with { Start = start },
            _ => old,
        };
        // Another face loops only when it moves too: a pose has no length to repeat.
        if (updated.Loop && updated.Path != old.Path && !CanLoop(doc, updated, motionFrames))
            updated = updated with { Loop = false };
        if (TimelineDocument.OwnPace(old) && old.Resizable && updated.Duration == old.Duration &&
            updated.Values.GetValueOrDefault(TmbEntrySchema.TimeControlField) != 0)
            updated = updated with { Duration = Math.Max(1, doc.PlayedFrames(old, motionFrames)) };
        return Same(old, updated) ? doc : doc with { Events = doc.Events.SetItem(index, updated) };
    }

    /// <summary>
    /// Keeps the face <paramref name="key"/> on, or stops keeping it. Kept, it gets time control and
    /// the endless length (<see cref="TimelineDocument.KeptDuration"/>), so the game shows it after the
    /// timeline ends until another face replaces it; a moving face (<paramref name="motionFrames"/>
    /// knows its length) on an expression's timeline loops (<see cref="TimelineDocument.Loops"/>).
    /// Keeping a kept moving face that doesn't loop yet makes it loop. No longer kept, a moving face
    /// lasts its own length, a pose holds until the next face or the end of the timeline.
    /// </summary>
    public static TimelineDocument Keep(TimelineDocument doc, string key, bool keep, IReadOnlyDictionary<string, int>? motionFrames = null)
    {
        var index = IndexOf(doc, key);
        if (index < 0 || !TimelineDocument.CanKeep(doc.Events[index]))
            return doc;
        var ev = doc.Events[index];
        var loop = keep && CanLoop(doc, ev, motionFrames);
        if (keep == TimelineDocument.Kept(ev) && loop == ev.Loop)
            return doc;
        var values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(TmbEntrySchema.TimeControlField, 1);
        var duration = keep ? TimelineDocument.KeptDuration
            : TimelineDocument.Moves(ev, motionFrames) is { } own ? own
            : Math.Clamp(doc.HeldEnd(ev) - ev.Start, 1, TimelineDocument.MaxFrames);
        return doc with { Events = doc.Events.SetItem(index, ev with { Duration = duration, Values = values, Loop = loop }) };
    }

    /// <summary>
    /// Whether keeping <paramref name="ev"/> makes it loop: a moving face on an expression's timeline
    /// that starts early enough for a second play to fit, and long enough to need at most
    /// <see cref="TimelineDocument.MaxLoopCopies"/> copies.
    /// </summary>
    public static bool CanLoop(TimelineDocument doc, TimelineEvent ev, IReadOnlyDictionary<string, int>? motionFrames) =>
        doc.Expression && TimelineDocument.CanKeep(ev) && TimelineDocument.Moves(ev, motionFrames) is { } own &&
        TimelineDocument.LoopCopies(ev.Start, own) is >= 1 and <= TimelineDocument.MaxLoopCopies;

    /// <summary>Whether <paramref name="ev"/> would loop (<see cref="CanLoop"/>) but is too short: it needs more than <see cref="TimelineDocument.MaxLoopCopies"/> copies.</summary>
    public static bool TooShortToLoop(TimelineDocument doc, TimelineEvent ev, IReadOnlyDictionary<string, int>? motionFrames) =>
        doc.Expression && TimelineDocument.CanKeep(ev) && TimelineDocument.Moves(ev, motionFrames) is { } own &&
        TimelineDocument.LoopCopies(ev.Start, own) > TimelineDocument.MaxLoopCopies;

    /// <summary>Why a face from another library can't come in while <paramref name="pack"/>'s faces are on the timeline.</summary>
    public static string PackInUse(string pack) =>
        $"This timeline uses the {pack} expressions, and can only use one set. Remove those first.";

    /// <summary>
    /// Whether two documents are the same: their events by <see cref="Same(TimelineEvent, TimelineEvent)"/>,
    /// their sound edits by content (<see cref="TimelineSoundEdits.Same"/>), the rest as records.
    /// </summary>
    public static bool Same(TimelineDocument a, TimelineDocument b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a with { Events = b.Events, SoundEdits = b.SoundEdits } != b || !TimelineSoundEdits.Same(a.SoundEdits, b.SoundEdits))
            return false;
        var x = Events(a);
        var y = Events(b);
        if (x.Length != y.Length)
            return false;
        for (var i = 0; i < x.Length; i++)
            if (!Same(x[i], y[i]))
                return false;
        return true;
    }

    /// <summary>Field-by-field equality, comparing values by content rather than by dictionary instance.</summary>
    public static bool Same(TimelineEvent a, TimelineEvent b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Key != b.Key || a.Magic != b.Magic || a.Lane != b.Lane || a.Start != b.Start || a.Duration != b.Duration ||
            a.Resizable != b.Resizable || a.Path != b.Path || a.Access != b.Access || a.LockReason != b.LockReason ||
            a.Conditional != b.Conditional || a.Group != b.Group || a.CloneOf != b.CloneOf || a.Loop != b.Loop || !Equals(a.Look, b.Look) ||
            a.Pin != b.Pin)
            return false;
        if (ReferenceEquals(a.Values, b.Values))
            return true;
        if (a.Values is null || b.Values is null || a.Values.Count != b.Values.Count)
            return false;
        foreach (var (name, value) in a.Values)
            if (!b.Values.TryGetValue(name, out var other) || !value.Equals(other))
                return false;
        return true;
    }

    /// <summary>
    /// Sets the changes to the sound <paramref name="key"/>, or drops them with null, in one document
    /// change. Every event playing that sound whose pin names an alternative the new edit no longer
    /// has plays any again; dropping the edit keeps pins on the file's own alternatives only.
    /// Returns <paramref name="doc"/> itself when nothing changes.
    /// </summary>
    public static TimelineDocument SetSound(TimelineDocument doc, TimelineSoundKey key, TimelineSoundEdit? edit)
    {
        var current = doc.SoundEdits.TryGetValue(key, out var found) ? found : null;
        var edits = edit == null ? doc.SoundEdits.Remove(key) : doc.SoundEdits.Remove(key).Add(key, edit);
        var ids = edit?.Alternatives.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        bool Stale(string pin) => ids?.Contains(pin) == false || ids == null && !pin.StartsWith(TimelineSoundAlternative.OwnPrefix, StringComparison.Ordinal);
        var events = Events(doc);
        var unpinned = events.Select(ev => ev.Pin is { } pin && TimelineSoundKey.Of(ev) == key && Stale(pin) ? ev with { Pin = null } : ev).ToImmutableArray();
        var pinsChanged = !unpinned.SequenceEqual(events);
        if (Equals(current, edit) && !pinsChanged)
            return doc;
        return doc with { SoundEdits = edits, Events = pinsChanged ? unpinned : doc.Events };
    }

    /// <summary>A group offset that keeps starts from <paramref name="first"/> to <paramref name="last"/> in [0, Length - 1].</summary>
    private static int ClampGroup(TimelineDocument doc, int first, int last, int delta)
    {
        var low = Math.Min(0, -first);
        var high = Math.Max(0, Math.Min(doc.Length - 1, TimelineDocument.MaxFrames) - last);
        return Math.Clamp(delta, low, high);
    }

    private static ImmutableArray<TimelineEvent> Events(TimelineDocument doc) => doc.Events.IsDefault ? [] : doc.Events;

    private static IReadOnlySet<string> Set(IEnumerable<string> keys) =>
        keys as IReadOnlySet<string> ?? keys.ToHashSet(StringComparer.Ordinal);
}
