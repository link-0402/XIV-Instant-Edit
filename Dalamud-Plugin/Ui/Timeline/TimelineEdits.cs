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

    public static bool CanResize(TimelineEvent ev) => ev.Access == TimelineAccess.Editable && ev.Resizable;

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
    /// that lasted the whole old length last the new one. With <paramref name="scaleEvents"/> every
    /// start and every non-zero duration scales with the length (rounded half away from zero), and a
    /// start that played before still plays; without it nothing else moves. Durations past
    /// <see cref="TimelineDocument.MaxFrames"/> mean "until the end" in mods and never scale.
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
        var restore = loaded != null && length == loaded.Length;
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
                if (duration is > 0 and <= TimelineDocument.MaxFrames)
                    duration = Math.Clamp(TimelineSnap.Round(ev.Duration * ratio), 1, TimelineDocument.MaxFrames);
            }
            if (ev.Lane == TimelineLane.Motion && (original == null
                    ? old > 0 && ev.Duration == old
                    : original.TryGetValue(ev.Key, out var motion) && motion.Duration == loaded!.Length))
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

    /// <summary>Events that start at or after the end of the timeline, so they would never play.</summary>
    public static ImmutableArray<TimelineEvent> PastEnd(TimelineDocument doc) => [.. Events(doc).Where(ev => ev.Start >= doc.Length)];

    /// <summary>
    /// Appends <paramref name="events"/> with new "n" keys. Events sharing a group keep sharing one,
    /// new for this Add. A <paramref name="pack"/> (the expression library they need) becomes the
    /// timeline's when it has none; a timeline loads only one, so a different one is refused. Added
    /// events are never conditional: saving puts each on a new track without a condition, even a copy
    /// of a conditional one.
    /// </summary>
    public static TimelineDocument Add(TimelineDocument doc, IEnumerable<TimelineEvent> events, string? pack, out string? error) =>
        Add(doc, events, pack, out _, out error);

    public static TimelineDocument Add(TimelineDocument doc, IEnumerable<TimelineEvent> events, string? pack,
        out ImmutableArray<string> created, out string? error)
    {
        created = [];
        error = null;
        var added = events.ToList();
        if (added.Count == 0)
            return doc;
        if (added.Any(ev => ev.Start < 0 || ev.Start > TimelineDocument.MaxFrames || ev.Duration < 0))
        {
            error = "The event is outside the timeline.";
            return doc;
        }
        var facePack = doc.FacePack;
        if (!string.IsNullOrEmpty(pack))
        {
            if (string.IsNullOrEmpty(facePack))
                facePack = pack;
            else if (!string.Equals(facePack, pack, StringComparison.OrdinalIgnoreCase))
            {
                error = $"This timeline already uses the {facePack} expressions. It can only use one set.";
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
            // A copy of a loaded entry remembers it, so saving copies all of its bytes.
            builder.Add(ev with { Key = key, Group = group, Conditional = false, CloneOf = ev.Key.StartsWith('t') ? ev.Key : ev.CloneOf });
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
    /// kept exactly). Starts stay inside the TMB's range.
    /// </summary>
    public static TimelineDocument Replace(TimelineDocument doc, TimelineEvent ev)
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
            },
            TimelineAccess.Movable => old with { Start = start },
            _ => old,
        };
        return Same(old, updated) ? doc : doc with { Events = doc.Events.SetItem(index, updated) };
    }

    /// <summary>Field-by-field equality, comparing values by content rather than by dictionary instance.</summary>
    public static bool Same(TimelineEvent a, TimelineEvent b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Key != b.Key || a.Magic != b.Magic || a.Lane != b.Lane || a.Start != b.Start || a.Duration != b.Duration ||
            a.Resizable != b.Resizable || a.Path != b.Path || a.Access != b.Access || a.LockReason != b.LockReason ||
            a.Conditional != b.Conditional || a.Group != b.Group || a.CloneOf != b.CloneOf)
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
