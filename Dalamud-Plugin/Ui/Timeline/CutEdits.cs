using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>What a cut took with it: the events it deleted and the bone layer keys it dropped.</summary>
internal readonly record struct CutRemoval(int Events, int Keys);

/// <summary>
/// Cutting a timeline's motion like a video editor: cut a part in two, remove frames, trim a part's
/// edges, set the blends at joins and at the loop seam, play a blend in a part's place, and fix a loop that stalls. Removing frames
/// ripples: what played after them moves left, what started inside goes (a locked entry can't, so it
/// moves to the cut), and bone layers lose their keys inside. Frames are output frames, as the
/// timeline shows them; with a stretch they map to the motion's own frames, rounded to whole ones.
/// Each edit returns the same document when nothing changes, and never cuts an expression's timeline.
/// </summary>
internal static class CutEdits
{
    /// <summary>The document's cut list, or one that keeps its whole motion.</summary>
    public static MotionCuts CutsOf(TimelineDocument doc) => doc.Cuts ?? MotionCuts.WholeOf(doc.MotionLength);

    /// <summary>Cuts the part under output frame <paramref name="frame"/> in two there. The new join gets the default blend.</summary>
    public static TimelineDocument CutAt(TimelineDocument doc, int frame)
    {
        if (doc.Expression || doc.Length <= 0)
            return doc;
        var cuts = CutsOf(doc);
        var at = CutFrame(doc, cuts, frame);
        var start = 0;
        for (var i = 0; i < cuts.Parts.Length; i++)
        {
            var part = cuts.Parts[i];
            if (at <= start)
                return doc;
            if (at < start + part.Frames)
            {
                // A blend part plays one blend from end to end: cutting it in two would make two next to each other.
                if (part.Blend)
                    return doc;
                var split = part.From + (at - start);
                return With(doc, cuts with
                {
                    Parts = cuts.Parts.SetItem(i, part with { To = split }).Insert(i + 1, part with { From = split }),
                    Joins = cuts.Joins.Insert(i, MotionJoin.ForCut),
                });
            }
            start += part.Frames;
        }
        return doc;
    }

    /// <summary>Removes output frames <paramref name="from"/> up to <paramref name="to"/> and ripples.</summary>
    public static TimelineDocument RemoveRange(TimelineDocument doc, int from, int to, out CutRemoval removed)
    {
        removed = default;
        if (doc.Expression || doc.Length <= 0)
            return doc;
        var cuts = CutsOf(doc);
        from = Math.Clamp(from, 0, doc.Length);
        to = Math.Clamp(to, 0, doc.Length);
        return Remove(doc, cuts, CutFrame(doc, cuts, from), CutFrame(doc, cuts, to), out removed);
    }

    /// <summary>
    /// Keeps only output frames <paramref name="from"/> up to <paramref name="to"/> (either way round): what
    /// plays after them and before them goes, and ripples. Nothing changes when that would keep everything
    /// or leave under <see cref="TimelineDocument.MinLength"/> frames.
    /// </summary>
    public static TimelineDocument KeepRange(TimelineDocument doc, int from, int to, out CutRemoval removed)
    {
        removed = default;
        if (from > to)
            (from, to) = (to, from);
        from = Math.Clamp(from, 0, doc.Length);
        to = Math.Clamp(to, 0, doc.Length);
        if (doc.Expression || to - from < TimelineDocument.MinLength || from == 0 && to == doc.Length)
            return doc;
        // The end goes first, so the start's frames are still where the caller counted them.
        var next = doc;
        CutRemoval after = default, before = default;
        if (to < doc.Length)
        {
            next = RemoveRange(doc, to, doc.Length, out after);
            if (ReferenceEquals(next, doc))
                return doc;
        }
        if (from > 0)
        {
            var trimmed = RemoveRange(next, 0, from, out before);
            if (ReferenceEquals(trimmed, next))
                return doc;
            next = trimmed;
        }
        removed = new CutRemoval(after.Events + before.Events, after.Keys + before.Keys);
        return next;
    }

    /// <summary>Removes part <paramref name="part"/> and ripples.</summary>
    public static TimelineDocument RemovePart(TimelineDocument doc, int part, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length)
            return doc;
        var start = Start(cuts, part);
        return Remove(doc, cuts, start, start + cuts.Parts[part].Frames, out removed);
    }

    /// <summary>
    /// Makes part <paramref name="part"/> play a blend from the part before it into the part after it
    /// (<paramref name="blend"/>), or its own motion again. Its frames and the length stay, unless a blend
    /// longer than the source frames between its neighbours goes back: it gives up the frames that don't
    /// fit, and what follows moves up. Only a part <see cref="CanBlend"/> allows becomes one.
    /// </summary>
    public static TimelineDocument SetBlend(TimelineDocument doc, int part, bool blend)
    {
        var cuts = CutsOf(doc);
        if (doc.Expression || part < 0 || part >= cuts.Parts.Length || cuts.Parts[part].Blend == blend || blend && !CanBlend(cuts, part))
            return doc;
        if (!blend)
        {
            var room = cuts.Parts[part + 1].From - cuts.Parts[part - 1].To;
            if (room <= 0)
                return RemovePart(doc, part, out _);
            if (cuts.Parts[part].Frames > room)
            {
                var fitted = Resize(doc, cuts, part, false, room - cuts.Parts[part].Frames, out _);
                if (ReferenceEquals(fitted, doc))
                    return doc;
                (doc, cuts) = (fitted, CutsOf(fitted));
            }
        }
        var p = cuts.Parts[part];
        // A blend's frames are only a count: back as motion they must lie between its neighbours' source frames.
        if (!blend)
        {
            var from = Math.Clamp(p.From, cuts.Parts[part - 1].To, cuts.Parts[part + 1].From - p.Frames);
            p = p with { From = from, To = from + p.Frames };
        }
        // A blend plays at normal speed: it takes as many frames as the stretched part played, so the length stays.
        if (blend && p.Stretch != 1)
            p = p with { To = p.From + Math.Clamp((int)Math.Round(p.Frames * p.Stretch, MidpointRounding.AwayFromZero), 1, TimelineDocument.MaxLength), Stretch = 1 };
        return With(doc, cuts with { Parts = cuts.Parts.SetItem(part, p with { Blend = blend }) });
    }

    /// <summary>
    /// Makes blend part <paramref name="part"/> <paramref name="frames"/> output frames long, at least one,
    /// growing or shrinking at its end; what follows moves with it and the length changes as much.
    /// </summary>
    public static TimelineDocument SetBlendLength(TimelineDocument doc, int part, int frames, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length || !cuts.Parts[part].Blend)
            return doc;
        var target = Math.Max(1, (int)Math.Round(ToSource(cuts, part, doc.Length, frames), MidpointRounding.AwayFromZero));
        return Resize(doc, cuts, part, false, target - cuts.Parts[part].Frames, out removed);
    }

    /// <summary>
    /// Makes part <paramref name="part"/> play over <paramref name="frames"/> output frames by changing its
    /// stretch, within <see cref="MotionPart.MinStretch"/> to <see cref="MotionPart.MaxStretch"/> and the
    /// clip's length limits. Every other part keeps its speed; the length changes by the difference. What
    /// starts inside the part stretches with it, what follows moves; keys a squeeze puts on one frame merge,
    /// counted in <paramref name="removed"/>. A blend part has no speed.
    /// </summary>
    public static TimelineDocument SetPartLength(TimelineDocument doc, int part, int frames, out CutRemoval removed)
    {
        var cuts = CutsOf(doc);
        var length = doc.Length > 0 && part >= 0 && part < cuts.Parts.Length ? cuts.PartEnd(part, doc.Length) - cuts.PartStart(part, doc.Length) : 0;
        // From the length the field shows, so a part of a half-frame length shows the number typed.
        return StretchBy(doc, part, frames - TimelineSnap.Round(length), out removed);
    }

    /// <summary>
    /// Makes part <paramref name="part"/> play <paramref name="delta"/> output frames longer, shorter when
    /// negative, as <see cref="SetPartLength"/> does. A drag of its end uses this, so a part whose output
    /// length is a fraction never changes on a drag that hasn't moved.
    /// </summary>
    public static TimelineDocument StretchPart(TimelineDocument doc, int part, int delta, out CutRemoval removed) =>
        StretchBy(doc, part, delta, out removed);

    /// <summary>The stretch edit of <see cref="SetPartLength"/> and <see cref="StretchPart"/>, by <paramref name="wanted"/> output frames before the clamps.</summary>
    private static TimelineDocument StretchBy(TimelineDocument doc, int part, int wanted, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length || cuts.Parts[part].Blend)
            return doc;
        var p = cuts.Parts[part];
        double from = cuts.PartStart(part, doc.Length), to = cuts.PartEnd(part, doc.Length);
        // The stretch S + D * Span / (Length * F) stays in range for D within these, rounded inward.
        var low = Math.Max((int)Math.Ceiling((MotionPart.MinStretch - p.Stretch) * doc.Length * p.Frames / cuts.Span - Tolerance),
            TimelineDocument.MinLength - doc.Length);
        var high = Math.Min((int)Math.Floor((MotionPart.MaxStretch - p.Stretch) * doc.Length * p.Frames / cuts.Span + Tolerance),
            TimelineDocument.MaxLength - doc.Length);
        if (low > high)
            return doc;
        var delta = Math.Clamp(wanted, low, high);
        if (delta == 0)
            return doc;
        var stretch = Snap(p.Stretch + delta * cuts.Span / ((double)doc.Length * p.Frames));
        var retimed = Retime(doc, from, to, delta, out var merged);
        if (ReferenceEquals(retimed, doc))
            return doc;
        removed = new CutRemoval(0, merged);
        return With(retimed, cuts with { Parts = cuts.Parts.SetItem(part, p with { Stretch = stretch }) });
    }

    /// <summary>
    /// Plays part <paramref name="part"/> at normal speed again: its stretch exactly 1, and the length
    /// <c>round(Length * Span' / Span)</c> so the clip's even stretch stays, within the clip's length limits.
    /// What starts inside the part scales with it and what follows moves, as <see cref="SetPartLength"/> does;
    /// keys a squeeze puts on one frame merge, counted in <paramref name="removed"/>. A blend part, a part
    /// already at normal speed and an expression's are left alone.
    /// </summary>
    public static TimelineDocument ResetPartSpeed(TimelineDocument doc, int part, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length || cuts.Parts[part].Blend || cuts.Parts[part].Stretch == 1)
            return doc;
        var normal = cuts with { Parts = cuts.Parts.SetItem(part, cuts.Parts[part] with { Stretch = 1 }) };
        var length = Math.Clamp((int)Math.Round(doc.Length * normal.Span / cuts.Span, MidpointRounding.AwayFromZero),
            TimelineDocument.MinLength, TimelineDocument.MaxLength);
        var delta = length - doc.Length;
        var retimed = doc;
        if (delta != 0)
        {
            retimed = Retime(doc, cuts.PartStart(part, doc.Length), cuts.PartEnd(part, doc.Length), delta, out var merged);
            if (ReferenceEquals(retimed, doc))
                return doc;
            removed = new CutRemoval(0, merged);
        }
        return With(retimed, normal);
    }

    /// <summary>How far a stretch or a frame bound may drift in floating point and still count as its exact value.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>A new stretch held in range, exactly at a limit or at normal speed when float drift left it a hair off.</summary>
    private static double Snap(double stretch)
    {
        stretch = Math.Clamp(stretch, MotionPart.MinStretch, MotionPart.MaxStretch);
        foreach (var exact in (ReadOnlySpan<double>)[MotionPart.MinStretch, 1, MotionPart.MaxStretch])
            if (Math.Abs(stretch - exact) < Tolerance)
                return exact;
        return stretch;
    }

    /// <summary>Whether part <paramref name="part"/> can become a blend part: it has a part on both sides, neither of them one.</summary>
    public static bool CanBlend(MotionCuts cuts, int part) =>
        part > 0 && part < cuts.Parts.Length - 1 && !cuts.Parts[part - 1].Blend && !cuts.Parts[part + 1].Blend;

    /// <summary>
    /// Moves one edge of part <paramref name="part"/> by <paramref name="delta"/> output frames, right when
    /// positive. It can go back into removed frames up to the neighbouring part, which it then joins
    /// again, and never past the part's other edge. Frames it takes away ripple out; frames it brings
    /// back push what follows to the right. A blend part's own edges make the blend longer or shorter, and
    /// what follows moves. A neighbour's edge at a blend part rolls instead: the one part grows as much
    /// as the other shrinks, both keep a frame, and the length stays.
    /// </summary>
    public static TimelineDocument TrimPart(TimelineDocument doc, int part, bool left, int delta, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length || delta == 0)
            return doc;
        var frames = (int)Math.Round(ToSource(cuts, part, doc.Length, delta), MidpointRounding.AwayFromZero);
        var p = cuts.Parts[part];
        if (p.Blend)
            return Resize(doc, cuts, part, left, left ? -frames : frames, out removed);
        var other = left ? part - 1 : part + 1;
        if (other >= 0 && other < cuts.Parts.Length && cuts.Parts[other].Blend)
            return Roll(doc, cuts, Math.Min(part, other), frames);
        var start = Start(cuts, part);
        if (left)
        {
            var from = Math.Clamp(p.From + frames, part > 0 ? cuts.Parts[part - 1].To : 0, p.To - 1);
            frames = from - p.From;
            if (frames > 0)
                return Remove(doc, cuts, start, start + frames, out removed);
            if (frames == 0)
                return doc;
            var parts = cuts.Parts.SetItem(part, p with { From = from });
            var joins = cuts.Joins;
            // Back to the part before at the same speed: the two are one again.
            if (part > 0 && cuts.Parts[part - 1].To == from && cuts.Parts[part - 1].Stretch == p.Stretch)
            {
                parts = parts.SetItem(part - 1, cuts.Parts[part - 1] with { To = p.To }).RemoveAt(part);
                joins = joins.RemoveAt(part - 1);
            }
            return Insert(doc, cuts, cuts with { Parts = parts, Joins = joins }, start, -frames, part);
        }
        else
        {
            var to = Math.Clamp(p.To + frames, p.From + 1, part < cuts.Parts.Length - 1 ? cuts.Parts[part + 1].From : cuts.Source);
            frames = to - p.To;
            var end = start + p.Frames;
            if (frames < 0)
                return Remove(doc, cuts, end + frames, end, out removed);
            if (frames == 0)
                return doc;
            var parts = cuts.Parts.SetItem(part, p with { To = to });
            var joins = cuts.Joins;
            if (part < cuts.Parts.Length - 1 && cuts.Parts[part + 1].From == to && cuts.Parts[part + 1].Stretch == p.Stretch)
            {
                parts = parts.SetItem(part, p with { To = cuts.Parts[part + 1].To }).RemoveAt(part + 1);
                joins = joins.RemoveAt(part);
            }
            return Insert(doc, cuts, cuts with { Parts = parts, Joins = joins }, end, frames, part);
        }
    }

    /// <summary>Sets join <paramref name="join"/>'s blend, its length clamped to 0 to <see cref="MotionJoin.MaxFrames"/>.</summary>
    public static TimelineDocument SetJoin(TimelineDocument doc, int join, MotionJoin value)
    {
        var cuts = CutsOf(doc);
        if (doc.Expression || join < 0 || join >= cuts.Joins.Length)
            return doc;
        var clamped = Clamp(value)!;
        return clamped == cuts.Joins[join] ? doc : With(doc, cuts with { Joins = cuts.Joins.SetItem(join, clamped) });
    }

    /// <summary>Sets the loop seam's blend, clamped as a join's is; null removes it.</summary>
    public static TimelineDocument SetSeam(TimelineDocument doc, MotionJoin? seam)
    {
        var cuts = CutsOf(doc);
        if (doc.Expression)
            return doc;
        seam = Clamp(seam);
        return seam == cuts.Seam ? doc : With(doc, cuts with { Seam = seam });
    }

    /// <summary>
    /// Sets over how many frames the loop's seam eases, clamped to 0 to <see cref="MotionJoin.MaxFrames"/>
    /// (0 removes it), and on which side: the end when <paramref name="atEnd"/>, the start when not, the
    /// side it has when null.
    /// </summary>
    public static TimelineDocument SetEase(TimelineDocument doc, int frames, bool? atEnd = null)
    {
        var cuts = CutsOf(doc);
        if (doc.Expression)
            return doc;
        frames = Math.Clamp(frames, 0, MotionJoin.MaxFrames);
        var end = atEnd ?? cuts.EaseEnd;
        return frames == cuts.Ease && end == cuts.EaseEnd ? doc : With(doc, cuts with { Ease = frames, EaseEnd = end });
    }

    /// <summary>
    /// Fixes a loop's seam by <paramref name="report"/>: an end that holds the start's pose is trimmed
    /// off (unless that leaves the clip too short), a jump in pose gets the seam's default blend, which
    /// matches the speeds too, and a jump in speed alone the default ease of the faster side. The blend
    /// and the ease keep the length.
    /// </summary>
    public static TimelineDocument FixLoop(TimelineDocument doc, SeamReport report) => FixLoop(doc, report, out _);

    /// <inheritdoc cref="FixLoop(TimelineDocument, SeamReport)"/>
    public static TimelineDocument FixLoop(TimelineDocument doc, SeamReport report, out CutRemoval removed)
    {
        removed = default;
        if (doc.Expression)
            return doc;
        var next = doc;
        var cuts = CutsOf(doc);
        if (report.StallFrom is { } stall && cuts.Parts[^1] is var last && stall > last.From && stall < last.To)
            next = Remove(doc, cuts, cuts.Kept - (last.To - stall), cuts.Kept, out removed);
        // One or the other: the blend matches the speeds already, and an ease beside it changes what it aims at.
        if (report.Gap.MeanAngle >= LoopSeam.PopDegrees)
            next = SetEase(SetSeam(next, MotionJoin.ForSeam), 0);
        else if (report.State == SeamState.Stalls)
            next = SetEase(SetSeam(next, null), MotionCuts.DefaultEase, atEnd: report.Gap.SpeedIn > report.Gap.SpeedOut);
        return next;
    }

    /// <summary>
    /// Moves events and bone layers for <paramref name="delta"/> output frames that go (negative: from
    /// <paramref name="at"/> up to <paramref name="at"/> - <paramref name="delta"/>) or come (positive, at
    /// <paramref name="at"/>), and the length with them. The cut list is left as it is.
    /// </summary>
    public static TimelineDocument Ripple(TimelineDocument doc, int at, int delta, out CutRemoval removed)
    {
        removed = default;
        var length = doc.Length + delta;
        if (delta == 0 || doc.Expression || length < TimelineDocument.MinLength || length > TimelineDocument.MaxLength)
            return doc;
        var events = (doc.Events.IsDefault ? [] : doc.Events).ToBuilder();
        var layers = doc.Motion.ToBuilder();
        int deletedEvents = 0, droppedKeys = 0;
        if (delta < 0)
        {
            int y = at, z = at - delta, gap = z - y;
            for (var i = events.Count - 1; i >= 0; i--)
            {
                var ev = events[i];
                var start = ev.Start;
                if (start >= y && start < z)
                {
                    if (ev.Access != TimelineAccess.Locked)
                    {
                        events.RemoveAt(i);
                        deletedEvents++;
                        continue;
                    }
                    start = Math.Min(y, length - 1);
                }
                else if (start >= z)
                    start -= gap;
                var duration = ev.Duration;
                if (ev.Lane == TimelineLane.Motion && doc.LastsWhole(ev.Duration))
                    duration = length;
                else if (ev.Resizable && ev.Duration is > 0 and <= TimelineDocument.MaxFrames && ev.Start < y && TimelineEdits.End(ev) > y)
                    duration = Math.Max(1, ev.Duration - (Math.Min(TimelineEdits.End(ev), z) - y));
                if (start != ev.Start || duration != ev.Duration)
                    events[i] = ev with { Start = start, Duration = duration };
            }
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                var layer = layers[i];
                // A match or a close loop is anchored to the clip's ends, so it stays there with its length, as far as the clip allows.
                if (MotionEdits.IsAnchored(layer))
                {
                    layers[i] = MotionEdits.Anchored(layer, length);
                    continue;
                }
                if (layer.Start >= y && layer.End <= z)
                {
                    layers.RemoveAt(i);
                    droppedKeys += layer.Keys.Length;
                    continue;
                }
                int Shift(int frame, bool end) => frame < y || end && frame == y ? frame : frame < z || end && frame == z ? y : frame - gap;
                var keys = layer.Keys.Where(k => k.Frame < y || k.Frame >= z).Select(k => k.Frame >= z ? k with { Frame = k.Frame - gap } : k).ToImmutableArray();
                droppedKeys += layer.Keys.Length - keys.Length;
                var next = Fit(layer with { Start = Shift(layer.Start, false), End = Shift(layer.End, true), Keys = keys });
                if (next.End <= next.Start)
                {
                    layers.RemoveAt(i);
                    droppedKeys += keys.Length;
                    continue;
                }
                if (next != layer)
                    layers[i] = next;
            }
        }
        else
        {
            for (var i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                var start = ev.Start >= at ? Math.Min(ev.Start + delta, TimelineDocument.MaxFrames) : ev.Start;
                var duration = ev.Lane == TimelineLane.Motion && doc.LastsWhole(ev.Duration) ? length : ev.Duration;
                if (start != ev.Start || duration != ev.Duration)
                    events[i] = ev with { Start = start, Duration = duration };
            }
            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (MotionEdits.IsAnchored(layer))
                {
                    layers[i] = MotionEdits.Anchored(layer, length);
                    continue;
                }
                var next = layer with
                {
                    Start = layer.Start >= at ? layer.Start + delta : layer.Start,
                    End = layer.End > at ? layer.End + delta : layer.End,
                    Keys = [.. layer.Keys.Select(k => k.Frame >= at ? k with { Frame = k.Frame + delta } : k)],
                };
                layers[i] = next;
            }
        }
        removed = new CutRemoval(deletedEvents, droppedKeys);
        return doc with { Length = length, Events = events.ToImmutable(), Motion = layers.ToImmutable() };
    }

    /// <summary>
    /// Moves events and bone layers for output frames <paramref name="from"/> up to <paramref name="to"/>
    /// now playing over <paramref name="delta"/> frames more (fewer when negative): frames before them stay,
    /// frames inside scale with them, rounded, and frames after them move by <paramref name="delta"/>, as
    /// does the length. Keys that land on one frame merge into the later one, whose bone values win;
    /// <paramref name="merged"/> counts the keys merged away. The cut list is left as it is.
    /// </summary>
    private static TimelineDocument Retime(TimelineDocument doc, double from, double to, int delta, out int merged)
    {
        merged = 0;
        var length = doc.Length + delta;
        var span = to - from;
        if (delta == 0 || span <= 0 || doc.Expression || length < TimelineDocument.MinLength || length > TimelineDocument.MaxLength)
            return doc;
        int Map(int frame) =>
            frame < from ? frame
            : frame < to ? (int)Math.Round(from + (frame - from) * (span + delta) / span, MidpointRounding.AwayFromZero)
            : frame + delta;
        // A squeezed last part can round its last frame onto the new end: what started before the end stays before it, as SetLength keeps it.
        int Inside(int frame) => frame < doc.Length ? Math.Min(Map(frame), length - 1) : Map(frame);
        var events = (doc.Events.IsDefault ? [] : doc.Events).ToBuilder();
        for (var i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            var start = Math.Min(Inside(ev.Start), TimelineDocument.MaxFrames);
            var duration = ev.Duration;
            if (ev.Lane == TimelineLane.Motion && doc.LastsWhole(ev.Duration))
                duration = length;
            else if (ev.Duration is > 0 and <= TimelineDocument.MaxFrames)
                duration = Math.Max(1, Map(TimelineEdits.End(ev)) - Map(ev.Start));
            if (start != ev.Start || duration != ev.Duration)
                events[i] = ev with { Start = start, Duration = duration };
        }
        var layers = doc.Motion.ToBuilder();
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (MotionEdits.IsAnchored(layer))
            {
                layers[i] = MotionEdits.Anchored(layer, length);
                continue;
            }
            var keys = ImmutableArray.CreateBuilder<MotionKey>(layer.Keys.Length);
            foreach (var key in layer.Keys)
            {
                var frame = Inside(key.Frame);
                var moved = frame == key.Frame ? key : key with { Frame = frame };
                if (keys.Count > 0 && keys[^1].Frame == frame)
                {
                    keys[^1] = Merge(keys[^1], moved);
                    merged++;
                }
                else
                    keys.Add(moved);
            }
            var layerStart = Inside(layer.Start);
            var next = Fit(layer with { Start = layerStart, End = Math.Max(Map(layer.End), layerStart + 1), Keys = keys.ToImmutable() });
            layers[i] = next;
        }
        return doc with { Length = length, Events = events.ToImmutable(), Motion = layers.ToImmutable() };
    }

    /// <summary>Two keys on one frame as one: <paramref name="later"/>'s bone values, ease and curve, with the bones only <paramref name="earlier"/> holds.</summary>
    private static MotionKey Merge(MotionKey earlier, MotionKey later) => later with
    {
        Bones = earlier.Bones.SetItems(later.Bones),
        Moved = [.. later.Moved, .. earlier.Moved.Where(bone => !later.Bones.ContainsKey(bone) && !later.Moved.Contains(bone))],
    };

    /// <summary>Removes cut frames <paramref name="from"/> up to <paramref name="to"/> from the parts and ripples the output frames they played at.</summary>
    private static TimelineDocument Remove(TimelineDocument doc, MotionCuts cuts, int from, int to, out CutRemoval removed)
    {
        removed = default;
        if (to <= from || to - from >= cuts.Kept)
            return doc;
        var outFrom = Output(doc, cuts, from);
        var outTo = Output(doc, cuts, to);
        if (doc.Length - (outTo - outFrom) < TimelineDocument.MinLength)
            return doc;
        var pieces = new List<(MotionPart Part, int Index)>();
        var start = 0;
        for (var i = 0; i < cuts.Parts.Length; i++)
        {
            var part = cuts.Parts[i];
            var end = start + part.Frames;
            if (end <= from || start >= to)
                pieces.Add((part, i));
            // A blend plays one blend from end to end: frames taken from its middle leave it one part, shorter.
            else if (part.Blend && from > start && to < end)
                pieces.Add((part with { To = part.To - (to - from) }, i));
            else
            {
                if (from > start)
                    pieces.Add((part with { To = part.From + (from - start) }, i));
                if (to < end)
                    pieces.Add((part with { From = part.From + (to - start) }, i));
            }
            start = end;
        }
        // A part cut in two gets a new join; parts that still meet keep theirs, and so does a part whose neighbours went.
        var joins = Enumerable.Range(1, pieces.Count - 1)
            .Select(k => pieces[k - 1].Index == pieces[k].Index ? MotionJoin.ForCut : cuts.Joins[pieces[k - 1].Index]);
        var cut = cuts with { Parts = [.. pieces.Select(p => p.Part)], Joins = [.. joins] };
        var rippled = outTo > outFrom ? Ripple(doc, outFrom, outFrom - outTo, out removed) : doc;
        if (ReferenceEquals(rippled, doc) && outTo > outFrom)
            return doc;
        return With(rippled, cut);
    }

    /// <summary>
    /// <paramref name="cut"/>, with its frames brought back at cut frame <paramref name="at"/> of <paramref name="cuts"/>, rippled in;
    /// they play at the speed of <paramref name="cuts"/>' part <paramref name="part"/>.
    /// </summary>
    private static TimelineDocument Insert(TimelineDocument doc, MotionCuts cuts, MotionCuts cut, int at, int frames, int part)
    {
        var outAt = Output(doc, cuts, at);
        var outFrames = Math.Max(1, (int)Math.Round(frames * (double)doc.Length * cuts.Parts[part].Stretch / cuts.Span, MidpointRounding.AwayFromZero));
        var rippled = Ripple(doc, outAt, outFrames, out _);
        return ReferenceEquals(rippled, doc) ? doc : With(rippled, cut);
    }

    /// <summary>
    /// Moves the boundary between part <paramref name="join"/> and the next by <paramref name="frames"/> cut
    /// frames of the part that plays its own motion, right when positive: the one part grows as much as the
    /// other shrinks, and each keeps a frame. One of them is a blend part, which gives or takes those frames
    /// times the other's stretch, rounded, so the span and every other part stay; the other grows only into
    /// source frames no other part plays.
    /// </summary>
    private static TimelineDocument Roll(TimelineDocument doc, MotionCuts cuts, int join, int frames)
    {
        MotionPart a = cuts.Parts[join], b = cuts.Parts[join + 1];
        // A blend's frames are only a count, so its source end moves apart from the other part's edge.
        var stretch = b.Blend ? a.Stretch : b.Stretch;
        int low, high;
        if (b.Blend)
        {
            low = 1 - a.Frames;
            high = Math.Min((int)Math.Floor((b.Frames - 1) / stretch + Tolerance), (join + 2 < cuts.Parts.Length ? cuts.Parts[join + 2].From : cuts.Source) - a.To);
        }
        else
        {
            low = Math.Max((int)Math.Ceiling((1 - a.Frames) / stretch - Tolerance), (join > 0 ? cuts.Parts[join - 1].To : 0) - b.From);
            high = b.Frames - 1;
        }
        frames = Math.Clamp(frames, low, high);
        if (frames == 0)
            return doc;
        var blendFrames = (int)Math.Round(frames * stretch, MidpointRounding.AwayFromZero);
        var parts = b.Blend
            ? cuts.Parts.SetItem(join, a with { To = a.To + frames }).SetItem(join + 1, b with
            {
                From = b.From + frames,
                To = b.From + frames + Math.Clamp(b.Frames - blendFrames, 1, TimelineDocument.MaxLength),
            })
            : cuts.Parts.SetItem(join, a with { To = a.From + Math.Clamp(a.Frames + blendFrames, 1, TimelineDocument.MaxLength) })
                .SetItem(join + 1, b with { From = b.From + frames });
        return With(doc, cuts with { Parts = parts });
    }

    /// <summary>
    /// Makes blend part <paramref name="part"/> <paramref name="change"/> cut frames longer (shorter when
    /// negative, keeping a frame) at its start or end: frames it gains push what follows to the right,
    /// frames it loses ripple out.
    /// </summary>
    private static TimelineDocument Resize(TimelineDocument doc, MotionCuts cuts, int part, bool atStart, int change, out CutRemoval removed)
    {
        removed = default;
        var p = cuts.Parts[part];
        change = Math.Max(change, 1 - p.Frames);
        if (change == 0)
            return doc;
        var start = Start(cuts, part);
        var at = atStart ? start : start + p.Frames;
        if (change < 0)
            return atStart ? Remove(doc, cuts, start, start - change, out removed) : Remove(doc, cuts, at + change, at, out removed);
        // Its frames are only a count, so it grows at its end in the source whichever edge moved.
        return Insert(doc, cuts, cuts with { Parts = cuts.Parts.SetItem(part, p with { To = p.To + change }) }, at, change, part);
    }

    /// <summary>
    /// The document with <paramref name="cuts"/>; a cut list that keeps the whole loaded motion is none. A
    /// blend part that an edit left first, last or right after another one plays its own motion again:
    /// the source frames from where it starts, as many as fit between its neighbours' (the rest of the
    /// clip stretching to keep the length), and with none it goes.
    /// </summary>
    private static TimelineDocument With(TimelineDocument doc, MotionCuts cuts)
    {
        var parts = cuts.Parts;
        var joins = cuts.Joins;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (!p.Blend || i > 0 && i < parts.Length - 1 && !parts[i - 1].Blend)
                continue;
            int low = 0, high = cuts.Source;
            for (var j = i - 1; j >= 0; j--)
            {
                if (!parts[j].Blend)
                {
                    low = parts[j].To;
                    break;
                }
            }
            for (var j = i + 1; j < parts.Length; j++)
            {
                if (!parts[j].Blend)
                {
                    high = parts[j].From;
                    break;
                }
            }
            var frames = Math.Min(p.Frames, high - low);
            if (frames <= 0 && parts.Length > 1)
            {
                parts = parts.RemoveAt(i);
                joins = joins.RemoveAt(i == 0 ? 0 : i - 1);
                i--;
                continue;
            }
            var from = Math.Clamp(p.From, low, high - frames);
            parts = parts.SetItem(i, new MotionPart(from, from + frames));
        }
        if (parts != cuts.Parts)
            cuts = cuts with { Parts = parts, Joins = joins };
        return doc with { Cuts = cuts.Whole && cuts.Source == doc.SourceLength ? null : cuts };
    }

    /// <summary>The cut frame (a frame of the kept motion, 0 to its length) output frame <paramref name="output"/> plays.</summary>
    private static int CutFrame(TimelineDocument doc, MotionCuts cuts, double output) =>
        Math.Clamp((int)Math.Round(cuts.CutOf(output, doc.Length), MidpointRounding.AwayFromZero), 0, cuts.Kept);

    /// <summary>The output frame cut frame <paramref name="cut"/> plays at, rounded.</summary>
    private static int Output(TimelineDocument doc, MotionCuts cuts, int cut) =>
        (int)Math.Round(cuts.OutputOf(cut, doc.Length), MidpointRounding.AwayFromZero);

    /// <summary>
    /// How many source frames <paramref name="output"/> output frames of part <paramref name="part"/> play, unrounded.
    /// Multiplied before dividing, so at stretch 1 it is exactly output * Kept / length.
    /// </summary>
    private static double ToSource(MotionCuts cuts, int part, int length, int output) =>
        output * cuts.Span / (length * cuts.Parts[part].Stretch);

    /// <summary>Where part <paramref name="part"/> starts, in cut frames.</summary>
    private static int Start(MotionCuts cuts, int part)
    {
        var start = 0;
        for (var i = 0; i < part; i++)
            start += cuts.Parts[i].Frames;
        return start;
    }

    private static MotionJoin? Clamp(MotionJoin? join) =>
        join == null ? null : join with { Frames = Math.Clamp(join.Frames, 0, MotionJoin.MaxFrames) };

    /// <summary>The layer with its fades fitting its new length.</summary>
    private static MotionLayer Fit(MotionLayer layer)
    {
        var span = Math.Max(0, layer.End - layer.Start);
        var fadeIn = Math.Clamp(layer.FadeIn, 0, span);
        var fadeOut = Math.Clamp(layer.FadeOut, 0, span - fadeIn);
        return fadeIn == layer.FadeIn && fadeOut == layer.FadeOut ? layer : layer with { FadeIn = fadeIn, FadeOut = fadeOut };
    }
}
