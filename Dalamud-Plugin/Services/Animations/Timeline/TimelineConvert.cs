using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Reads a TMB timeline into the editor's <see cref="TimelineDocument"/> and writes an edited
/// document back over the original bytes. Only what the document changed is written; the
/// rest of every entry, every track, condition and curve is carried over as it was.
/// </summary>
internal static class TimelineConvert
{
    /// <summary>
    /// One event per C-entry in item order, keyed "t" + its id. TMFC curves are not events, and a
    /// looping face (<see cref="LoopChains"/>) is one event, its first play's, without its copies.
    /// <paramref name="motionFrames"/> is how long the clip's motion plays (its Havok duration),
    /// when known. It becomes the length when the header (TMDH) says otherwise by more than a
    /// frame, which only mods do; without it the header's length is used.
    /// </summary>
    public static TimelineDocument Read(TmbFile file, int? motionFrames = null)
    {
        var conditional = file.Tracks.Where(t => t.Conditional).SelectMany(t => t.Entries).ToHashSet();
        var length = motionFrames is { } motion && motion > 0 && !TimelineDocument.SameLength(motion, file.Length) ? motion : file.Length;
        var loops = LoopChains(file);
        var copies = loops.Values.SelectMany(c => c).ToHashSet();
        return new TimelineDocument(length, length, file.FacePack,
            [.. file.Entries.Where(e => e.Magic != "TMFC" && !copies.Contains(e.Id)).Select(e => loops.ContainsKey(e.Id)
                ? Event(e, conditional.Contains(e.Id)) with { Duration = TimelineDocument.KeptDuration, Loop = true }
                : Event(e, conditional.Contains(e.Id)))], 1)
        {
            HeaderLength = file.Length,
        };
    }

    /// <summary>
    /// The looping faces a timeline holds (<see cref="TimelineDocument.Loops"/>), by the id of each
    /// one's first play, with its copies' ids in time order: timed faces on one track, alike but for
    /// their start, back to back by the first play's length up to the last frame a timeline reaches.
    /// The last copy may be kept (-1), as the first loops were saved. A mod's own smile, cheer, then
    /// smile kept is no loop: it stops far before the end.
    /// </summary>
    internal static Dictionary<short, short[]> LoopChains(TmbFile file)
    {
        var faces = file.Entries.Where(e => e.Magic == "C010" && (e.Text ?? "").StartsWith("cfxf_", StringComparison.Ordinal) &&
            (e.Int32(20) & 1) != 0).ToArray();
        var tracks = faces.ToDictionary(e => e.Id, e => file.TrackOf(e.Id)?.Id);
        var used = new HashSet<short>();
        var chains = new Dictionary<short, short[]>();
        foreach (var head in faces.OrderBy(e => e.Time).ThenBy(e => e.Id))
        {
            var length = head.Int32(12);
            if (used.Contains(head.Id) || length is < 1 or > TimelineDocument.MaxFrames || tracks[head.Id] is null) continue;
            var copies = new List<short>();
            for (var time = head.Time + length; ; time += length)
            {
                var next = faces.FirstOrDefault(e => e.Time == time && e.Id != head.Id && !used.Contains(e.Id) && !copies.Contains(e.Id) &&
                    tracks[e.Id] == tracks[head.Id] && SamePlay(head, e) && e.Int32(12) is var played &&
                    (played == length || played == TimelineDocument.KeptDuration));
                if (next == null) break;
                copies.Add(next.Id);
                if (next.Int32(12) == TimelineDocument.KeptDuration) break;
            }
            // A loop runs to the end: no room for another play after its last copy.
            if (copies.Count == 0 || file.Find(copies[^1])!.Time + length <= TimelineDocument.MaxFrames) continue;
            chains[head.Id] = [.. copies];
            used.Add(head.Id);
            used.UnionWith(copies);
        }
        return chains;
    }

    /// <summary>Two faces that play alike: the same motion, flags, frame range and unknown fields.</summary>
    private static bool SamePlay(TmbFile.Entry a, TmbFile.Entry b) =>
        a.Text == b.Text && a.Int32(16) == b.Int32(16) && a.Int32(20) == b.Int32(20) &&
        a.Single(24).Equals(b.Single(24)) && a.Single(28).Equals(b.Single(28)) && a.Int32(36) == b.Int32(36);

    /// <summary>How a face entry plays as loaded: its length, time control and frame range.</summary>
    private readonly record struct Play(int Duration, bool Timed, float From, float To)
    {
        public static Play Of(TmbFile.Entry entry) => new(entry.Int32(12), (entry.Int32(20) & 1) != 0, entry.Single(24), entry.Single(28));
    }

    private static TimelineEvent Event(TmbFile.Entry entry, bool conditional)
    {
        var kind = TmbEntrySchema.Get(entry.Magic);
        var path = entry.Text ?? "";
        var (lane, access, reason) = TmbEntrySchema.Classify(entry.Magic, path);
        return new TimelineEvent("t" + entry.Id, entry.Magic, lane, entry.Time, TmbEntrySchema.Duration(entry),
            access == TimelineAccess.Editable && kind.HasDuration, path, access, reason, conditional, TmbEntrySchema.ReadValues(entry));
    }

    /// <summary>
    /// Writes <paramref name="edited"/> (read from <paramref name="originalTmb"/>, then edited) back.
    /// Deleted "t" events are removed, kept ones get their changed start, duration, path and values,
    /// "n" events are added from <see cref="TimelineTemplates"/>. Only Editable events change path,
    /// values or (on their own) length; Movable ones move; Locked ones keep their place unless the
    /// timeline's length changed, when the editor scales every start and length.
    /// Automatic follow-ups: a C009 that lasted the whole clip keeps lasting it; a C010 without
    /// time control whose end frame was its duration keeps ending at its duration; a motion at its
    /// own speed given time control gets a range that plays it whole (its own length from
    /// <paramref name="motionFrames"/>, by motion name); a curve (TMFC) stretches
    /// with the entry that uses it. The header's length (TMDH) changes only with the
    /// length (see <see cref="TimelineDocument.SavedHeaderLength"/>), so a mod's own otherwise stays.
    /// A kept face has the endless length; a looping one is written as its plays back to back
    /// (<see cref="Loop"/>), and a loop the edit left alone stays as it was.
    /// Returns <paramref name="originalTmb"/> itself when nothing changed, and refuses an output that
    /// does not read back as the edit.
    /// </summary>
    public static byte[] Apply(byte[] originalTmb, TimelineDocument edited, IReadOnlyDictionary<string, int>? motionFrames = null)
    {
        // A look lives in a copy of its effect; one still on an event would be lost.
        if (!edited.Events.IsDefault && edited.Events.Any(e => e.Look is { IsEmpty: false }))
            throw new InvalidDataException("An effect's look was not written into a copy of the effect.");
        var file = TmbFile.Parse(originalTmb);
        var chains = LoopChains(file);
        // How every face played as loaded: a loop is rebuilt from its first play.
        var plays = file.Entries.Where(e => e.Magic == "C010").ToDictionary(e => e.Id, Play.Of);
        Play? PlayOf(short id) => plays.TryGetValue(id, out var play) ? play : null;
        var loops = new List<(TimelineEvent Event, short Id, Play? Loaded)>();
        // Kept faces the edit added or changed; a mod's own kept faces stay exactly as they are.
        var keeps = new List<short>();
        var source = Read(file, edited.SourceLength);
        if (edited.HeaderLength != file.Length || edited.SourceLength != source.Length)
            throw new InvalidDataException("The edit was made on a different timeline.");
        if (edited.Length != source.Length && edited.Length is < TimelineDocument.MinLength or > TimelineDocument.MaxLength)
            throw new InvalidDataException($"A length of {edited.Length} frames is out of range.");
        var before = source.Events.ToDictionary(e => e.Key);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in edited.Events)
        {
            if (!keys.Add(e.Key)) throw new InvalidDataException($"Two timeline events share the key '{e.Key}'.");
            if (e.Key.StartsWith('t') ? !before.ContainsKey(e.Key) : !e.Key.StartsWith('n') || e.Key.Length < 2)
                throw new InvalidDataException($"The timeline has no event '{e.Key}'.");
        }

        var removed = source.Events.Where(e => !keys.Contains(e.Key)).ToList();
        foreach (var e in removed.Where(e => e.Access == TimelineAccess.Locked))
            throw new InvalidDataException($"{Describe(e)} cannot be deleted.");

        var expected = new List<(TimelineEvent Event, short Id)>();
        var scaled = edited.Length != source.Length;
        // Copies of loaded entries first, while every entry still holds the bytes it was loaded
        // with: a copy takes all of its source's bytes, then the fields the document changed.
        foreach (var e in edited.Events.Where(e => !e.Key.StartsWith('t') && e.CloneOf is { } from && before.ContainsKey(from)))
        {
            var origin = before[e.CloneOf!];
            if (origin.Magic != e.Magic || origin.Access != TimelineAccess.Editable)
                throw new InvalidDataException($"Cannot copy {Describe(origin)}.");
            var copy = file.CloneEntry(Id(origin.Key));
            Update(file, copy, origin, e, scaled);
            expected.Add((e with { Conditional = false }, copy));
            if (TimelineDocument.Loops(e)) loops.Add((e, copy, PlayOf(Id(origin.Key))));
            else if (TimelineDocument.Kept(e)) keeps.Add(copy);
        }
        var curves = new Dictionary<short, double>();
        foreach (var e in edited.Events)
        {
            if (!e.Key.StartsWith('t'))
            {
                if (e.CloneOf is not { } from || !before.ContainsKey(from))
                {
                    var added = Add(file, e);
                    expected.Add((e with { Conditional = false }, added));
                    if (TimelineDocument.Loops(e)) loops.Add((e, added, null));
                    else if (TimelineDocument.Kept(e)) keeps.Add(added);
                }
                continue;
            }
            var id = Id(e.Key);
            var old = before[e.Key];
            var target = e;
            // The clip's own motion lasts the whole clip, whatever length it now has.
            if (old.Magic == "C009" && old.Lane == TimelineLane.Motion && scaled && source.LastsWhole(old.Duration))
                target = e with { Duration = edited.Length };
            // Back at the loaded length, what only length scaling moves is where it was loaded.
            if (!scaled && old.Access == TimelineAccess.Locked) target = target with { Start = old.Start, Duration = old.Duration };
            else if (!scaled && old.Access == TimelineAccess.Movable) target = target with { Duration = old.Duration };
            // A loop the edit changed is written again; one it left alone keeps its copies.
            var chained = chains.TryGetValue(id, out var copies);
            var changed = !Same(target with { Conditional = old.Conditional }, old);
            if (chained && changed)
            {
                foreach (var copy in copies!) file.RemoveEntry(copy);
                // The first play stores its own length; the document has the loop's endless one.
                if (!TimelineDocument.Loops(target)) file.SetInt32(id, 12, old.Duration);
            }
            Update(file, id, old, target, scaled);
            expected.Add((target with { Conditional = old.Conditional }, id));
            if (TimelineDocument.Loops(target) && (!chained || changed)) loops.Add((target, id, PlayOf(id)));
            else if (TimelineDocument.Kept(target) && changed) keeps.Add(id);
            var entry = file.Find(id)!;
            // A motion at its own speed ignores its frame range. Given time control it plays the range
            // over its length, so the range becomes the whole motion: 0 to its own length. Unknown, an
            // empty range (VFXEditor's default 0 to 0) becomes 0 to 1 for a face (a pose, as vanilla
            // timed faces have it) or 0 to the length stored for anything else; another is kept.
            if (TimelineDocument.OwnPace(old) && target.Values.GetValueOrDefault(TmbEntrySchema.TimeControlField) != 0)
            {
                float? end = motionFrames != null && motionFrames.TryGetValue(target.Path, out var frames) && frames > 0 ? frames
                    : entry.Single(28) > entry.Single(24) ? null
                    : TimelineDocument.Holds(old) ? 1 : Math.Max(1, old.Duration);
                if (end is { } value)
                {
                    file.SetSingle(id, 24, 0);
                    file.SetSingle(id, 28, value);
                }
            }
            if (target.Duration == old.Duration) continue;
            if (old.Magic == "C010" && (entry.Int32(20) & 1) == 0 && entry.Single(28) == old.Duration)
                file.SetSingle(id, 28, target.Duration);
            if (TmbFile.CurveUsers.Contains(old.Magic) && old.Duration > 0 &&
                entry.Int32(TmbFile.CurveIdOffset) is var curve and >= short.MinValue and <= short.MaxValue &&
                file.Find((short)curve) is { Magic: "TMFC" })
            {
                var factor = (double)target.Duration / old.Duration;
                if (curves.TryGetValue((short)curve, out var other) && other != factor)
                    throw new InvalidDataException("Two entries stretch one curve differently.");
                curves[(short)curve] = factor;
            }
        }
        foreach (var (curve, factor) in curves) file.ScaleCurve(curve, factor);
        foreach (var id in keeps) Keep(file, id);
        foreach (var (e, id, loaded) in loops) Loop(file, id, e, loaded, motionFrames);
        foreach (var e in removed)
        {
            file.RemoveEntry(Id(e.Key));
            if (chains.TryGetValue(Id(e.Key), out var gone))
                foreach (var copy in gone) file.RemoveEntry(copy);
        }
        if (edited.FacePack != source.FacePack) file.FacePack = edited.FacePack;
        if (edited.Length != source.Length) file.SetLength(edited.SavedHeaderLength);
        if (!file.Edited) return originalTmb;

        var output = file.ToArray(out var ids);
        Verify(output, edited, expected, ids);
        return output;
    }

    /// <summary>
    /// A kept face: the endless length (a copy of a loop's first play still has that play's), and
    /// its frame range. Spread over its endless length, the range plays at a crawl, so a pose shows
    /// its frame 0 for good (frame 0 equals frame 1 in every vanilla face). An empty range
    /// (VFXEditor's 0 to 0) becomes the pose's 0 to 1; any other is kept.
    /// </summary>
    private static void Keep(TmbFile file, short id)
    {
        file.SetInt32(id, 12, TimelineDocument.KeptDuration);
        var entry = file.Find(id)!;
        if (entry.Single(28) > entry.Single(24)) return;
        file.SetSingle(id, 24, 0);
        file.SetSingle(id, 28, 1);
    }

    /// <summary>
    /// A looping face (<see cref="TimelineDocument.Loops"/>): its first play, then copies of it back
    /// to back on its track for as long as a timeline reaches; after the last play the face returns
    /// to neutral. A play is the face's whole frame range: as loaded when it played the whole face
    /// with time control (keeping a mod's own speed), else the face once at its own length.
    /// </summary>
    private static void Loop(TmbFile file, short id, TimelineEvent e, Play? loaded, IReadOnlyDictionary<string, int>? motionFrames)
    {
        var own = TimelineDocument.Moves(e, motionFrames) ?? throw new InvalidDataException($"The length of {e.Path} is unknown, so it can't loop.");
        var (length, from, to) = loaded is { Timed: true, Duration: > 0 and <= TimelineDocument.MaxFrames } play && play.To - play.From >= own - 1
            ? (play.Duration, play.From, play.To)
            : (own, 0f, (float)own);
        if ((long)e.Start + length > TimelineDocument.MaxFrames) throw new InvalidDataException($"{Describe(e)} starts too late to loop.");
        file.SetInt32(id, 12, length);
        file.SetSingle(id, 24, from);
        file.SetSingle(id, 28, to);
        for (var time = e.Start + length; time <= TimelineDocument.MaxFrames; time += length)
            file.SetTime(file.CloneOnTrack(id), time);
    }

    /// <param name="scaled">The timeline's length changed, which lets the editor scale every start and length.</param>
    private static void Update(TmbFile file, short id, TimelineEvent old, TimelineEvent e, bool scaled)
    {
        if (e.Magic != old.Magic) throw new InvalidDataException($"{Describe(old)} cannot change its kind.");
        var kind = TmbEntrySchema.Get(old.Magic);
        if (e.Start != old.Start)
        {
            if (old.Access == TimelineAccess.Locked && !scaled) throw new InvalidDataException($"{Describe(old)} cannot be moved.");
            RequireFrames(e.Start, "start");
            file.SetTime(id, e.Start);
        }
        if (e.Duration != old.Duration)
        {
            if (!kind.HasDuration) throw new InvalidDataException($"{Describe(old)} has no length.");
            if (old.Access != TimelineAccess.Editable && !scaled) throw new InvalidDataException($"{Describe(old)} cannot change its length.");
            RequireLength(e);
            file.SetInt32(id, 12, e.Duration);
        }
        if (e.Path != old.Path)
        {
            if (old.Access != TimelineAccess.Editable || kind.StringOffset < 0) throw new InvalidDataException($"{Describe(old)} cannot change its path.");
            if (TmbEntrySchema.Classify(old.Magic, e.Path).Access != TimelineAccess.Editable)
                throw new InvalidDataException($"'{e.Path}' is not a facial motion.");
            file.SetString(id, e.Path);
        }
        RequireFields(kind, e);
        foreach (var binding in kind.Bindings)
        {
            var name = binding.Field.Name;
            if (SameValue(TmbEntrySchema.Normalize(binding, e.Values[name]), TmbEntrySchema.Normalize(binding, old.Values[name]))) continue;
            if (old.Access != TimelineAccess.Editable) throw new InvalidDataException($"{Describe(old)} cannot change its {binding.Field.Label}.");
            TmbEntrySchema.Write(file, id, binding, e.Values[name]);
        }
    }

    private static short Add(TmbFile file, TimelineEvent e)
    {
        var kind = TmbEntrySchema.Get(e.Magic);
        if (!TimelineTemplates.CanCreate(e.Magic) || TmbEntrySchema.Classify(e.Magic, e.Path).Access != TimelineAccess.Editable)
            throw new InvalidDataException($"Cannot add {Describe(e)}.");
        RequireFrames(e.Start, "start");
        RequireLength(e);
        if (!kind.HasDuration && e.Duration != 0) throw new InvalidDataException($"{Describe(e)} has no length.");
        if (kind.StringOffset < 0 && e.Path.Length > 0) throw new InvalidDataException($"{Describe(e)} has no path.");
        RequireFields(kind, e);
        var id = TimelineTemplates.AddTo(file, e);
        file.SetTime(id, e.Start);
        if (kind.HasDuration) file.SetInt32(id, 12, e.Duration);
        if (kind.StringOffset >= 0) file.SetString(id, e.Path);
        foreach (var binding in kind.Bindings) TmbEntrySchema.Write(file, id, binding, e.Values[binding.Field.Name]);
        return id;
    }

    /// <summary>
    /// Reads the output back: every event must come out as the document has it, by its new id, and
    /// the header's length as <see cref="TimelineDocument.SavedHeaderLength"/>.
    /// </summary>
    private static void Verify(byte[] output, TimelineDocument edited, List<(TimelineEvent Event, short Id)> expected,
        IReadOnlyDictionary<short, short> ids)
    {
        var file = TmbFile.Parse(output);
        var actual = Read(file);
        var events = actual.Events.ToDictionary(e => e.Key);
        if (file.Length != edited.SavedHeaderLength || actual.FacePack != edited.FacePack ||
            events.Count != expected.Count ||
            expected.Any(x => !events.TryGetValue("t" + ids[x.Id], out var e) || !Same(e, x.Event)))
            throw new InvalidDataException("The rebuilt timeline does not match the edit.");
    }

    /// <summary>The same entry data; lane, access and the rest follow from it.</summary>
    private static bool Same(TimelineEvent actual, TimelineEvent expected)
    {
        if (actual.Magic != expected.Magic || actual.Start != expected.Start || actual.Duration != expected.Duration ||
            actual.Path != expected.Path || actual.Conditional != expected.Conditional ||
            TimelineDocument.Loops(actual) != TimelineDocument.Loops(expected) ||
            actual.Values.Count != expected.Values.Count)
            return false;
        foreach (var binding in TmbEntrySchema.Get(actual.Magic).Bindings)
            if (!expected.Values.TryGetValue(binding.Field.Name, out var value) ||
                !SameValue(TmbEntrySchema.Normalize(binding, value), actual.Values[binding.Field.Name]))
                return false;
        return true;
    }

    /// <summary>Equal, or both NaN: a float field a file left as NaN is unchanged, not edited.</summary>
    private static bool SameValue(double a, double b) => a == b || double.IsNaN(a) && double.IsNaN(b);

    private static void RequireFields(TmbEntryKind kind, TimelineEvent e)
    {
        if (e.Values.Count != kind.Bindings.Length || kind.Bindings.Any(b => !e.Values.ContainsKey(b.Field.Name)))
            throw new InvalidDataException($"{Describe(e)} has the wrong fields.");
    }

    /// <summary>A length in the timeline's range, or the endless one of a kept face.</summary>
    private static void RequireLength(TimelineEvent e)
    {
        if (e.Duration == TimelineDocument.KeptDuration && TimelineDocument.Kept(e)) return;
        RequireFrames(e.Duration, "length");
    }

    private static void RequireFrames(int frames, string what)
    {
        if (frames is < 0 or > TimelineDocument.MaxFrames) throw new InvalidDataException($"A {what} of {frames} frames is out of range.");
    }

    private static short Id(string key) =>
        short.TryParse(key.AsSpan(1), out var id) ? id : throw new InvalidDataException($"The timeline has no event '{key}'.");

    private static string Describe(TimelineEvent e) => $"The {TmbEntrySchema.Find(e.Magic)?.Name ?? e.Magic} at frame {e.Start}";
}
