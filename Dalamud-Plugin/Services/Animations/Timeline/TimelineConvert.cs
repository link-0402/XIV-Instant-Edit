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
    /// One event per C-entry in item order, keyed "t" + its id. TMFC curves are not events.
    /// <paramref name="motionFrames"/> is how long the clip's motion plays (its Havok duration),
    /// when known. It becomes the length when the header (TMDH) says otherwise by more than a
    /// frame, which only mods do; without it the header's length is used.
    /// </summary>
    public static TimelineDocument Read(TmbFile file, int? motionFrames = null)
    {
        var conditional = file.Tracks.Where(t => t.Conditional).SelectMany(t => t.Entries).ToHashSet();
        var length = motionFrames is { } motion && motion > 0 && !TimelineDocument.SameLength(motion, file.Length) ? motion : file.Length;
        return new TimelineDocument(length, length, file.FacePack,
            [.. file.Entries.Where(e => e.Magic != "TMFC").Select(e => Event(e, conditional.Contains(e.Id)))], 1)
        {
            HeaderLength = file.Length,
        };
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
    /// time control whose end frame was its duration keeps ending at its duration; a holding face
    /// given time control gets the range 0 to 1 when its own was empty; a curve (TMFC) stretches
    /// with the entry that uses it. The header's length (TMDH) changes only with the
    /// length (see <see cref="TimelineDocument.SavedHeaderLength"/>), so a mod's own otherwise stays.
    /// Returns <paramref name="originalTmb"/> itself when nothing changed, and refuses an output that
    /// does not read back as the edit.
    /// </summary>
    public static byte[] Apply(byte[] originalTmb, TimelineDocument edited)
    {
        var file = TmbFile.Parse(originalTmb);
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
        }
        var curves = new Dictionary<short, double>();
        foreach (var e in edited.Events)
        {
            if (!e.Key.StartsWith('t'))
            {
                if (e.CloneOf is not { } from || !before.ContainsKey(from))
                    expected.Add((e with { Conditional = false }, Add(file, e)));
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
            Update(file, id, old, target, scaled);
            expected.Add((target with { Conditional = old.Conditional }, id));
            var entry = file.Find(id)!;
            // A holding face ignores its frame range. Given time control it plays it, so an empty range
            // (VFXEditor's default 0 to 0) becomes the 0 to 1 every vanilla timed face uses.
            if (TimelineDocument.Holds(old) && target.Values.GetValueOrDefault(TmbEntrySchema.TimeControlField) != 0 &&
                !(entry.Single(28) > entry.Single(24)))
            {
                file.SetSingle(id, 24, 0);
                file.SetSingle(id, 28, 1);
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
        foreach (var e in removed) file.RemoveEntry(Id(e.Key));
        if (edited.FacePack != source.FacePack) file.FacePack = edited.FacePack;
        if (edited.Length != source.Length) file.SetLength(edited.SavedHeaderLength);
        if (!file.Edited) return originalTmb;

        var output = file.ToArray(out var ids);
        Verify(output, edited, expected, ids);
        return output;
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
            RequireFrames(e.Duration, "length");
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
        RequireFrames(e.Duration, "length");
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
            actual.Path != expected.Path || actual.Conditional != expected.Conditional || actual.Values.Count != expected.Values.Count)
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

    private static void RequireFrames(int frames, string what)
    {
        if (frames is < 0 or > TimelineDocument.MaxFrames) throw new InvalidDataException($"A {what} of {frames} frames is out of range.");
    }

    private static short Id(string key) =>
        short.TryParse(key.AsSpan(1), out var id) ? id : throw new InvalidDataException($"The timeline has no event '{key}'.");

    private static string Describe(TimelineEvent e) => $"The {TmbEntrySchema.Find(e.Magic)?.Name ?? e.Magic} at frame {e.Start}";
}
