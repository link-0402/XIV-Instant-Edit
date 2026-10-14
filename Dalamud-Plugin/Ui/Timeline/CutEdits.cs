using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>What a cut took with it: the events it deleted and the bone layer keys it dropped.</summary>
internal readonly record struct CutRemoval(int Events, int Keys);

/// <summary>
/// Cutting a timeline's motion like a video editor: cut a part in two, remove frames, trim a part's
/// edges, set the blends at joins and at the loop seam, and fix a loop that stalls. Removing frames
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
                var split = part.From + (at - start);
                return With(doc, cuts with
                {
                    Parts = cuts.Parts.SetItem(i, part with { To = split }).Insert(i + 1, new MotionPart(split, part.To)),
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
    /// Moves one edge of part <paramref name="part"/> by <paramref name="delta"/> output frames, right when
    /// positive. It can go back into removed frames up to the neighbouring part, which it then joins
    /// again, and never past the part's other edge. Frames it takes away ripple out; frames it brings
    /// back push what follows to the right.
    /// </summary>
    public static TimelineDocument TrimPart(TimelineDocument doc, int part, bool left, int delta, out CutRemoval removed)
    {
        removed = default;
        var cuts = CutsOf(doc);
        if (doc.Expression || doc.Length <= 0 || part < 0 || part >= cuts.Parts.Length || delta == 0)
            return doc;
        var frames = (int)Math.Round(delta * (double)cuts.Kept / doc.Length, MidpointRounding.AwayFromZero);
        var p = cuts.Parts[part];
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
            // Back to the part before: the two are one again.
            if (part > 0 && cuts.Parts[part - 1].To == from)
            {
                parts = parts.SetItem(part - 1, cuts.Parts[part - 1] with { To = p.To }).RemoveAt(part);
                joins = joins.RemoveAt(part - 1);
            }
            return Insert(doc, cuts, cuts with { Parts = parts, Joins = joins }, start, -frames);
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
            if (part < cuts.Parts.Length - 1 && cuts.Parts[part + 1].From == to)
            {
                parts = parts.SetItem(part, p with { To = cuts.Parts[part + 1].To }).RemoveAt(part + 1);
                joins = joins.RemoveAt(part);
            }
            return Insert(doc, cuts, cuts with { Parts = parts, Joins = joins }, end, frames);
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

    /// <summary><paramref name="cut"/>, with its frames brought back at cut frame <paramref name="at"/> of <paramref name="cuts"/>, rippled in.</summary>
    private static TimelineDocument Insert(TimelineDocument doc, MotionCuts cuts, MotionCuts cut, int at, int frames)
    {
        var outAt = Output(doc, cuts, at);
        var outFrames = Math.Max(1, (int)Math.Round(frames * (double)doc.Length / cuts.Kept, MidpointRounding.AwayFromZero));
        var rippled = Ripple(doc, outAt, outFrames, out _);
        return ReferenceEquals(rippled, doc) ? doc : With(rippled, cut);
    }

    /// <summary>The document with <paramref name="cuts"/>; a cut list that keeps the whole loaded motion is none.</summary>
    private static TimelineDocument With(TimelineDocument doc, MotionCuts cuts) =>
        doc with { Cuts = cuts.Whole && cuts.Source == doc.SourceLength ? null : cuts };

    /// <summary>The cut frame (a frame of the kept motion, 0 to its length) output frame <paramref name="output"/> plays.</summary>
    private static int CutFrame(TimelineDocument doc, MotionCuts cuts, double output) =>
        Math.Clamp((int)Math.Round(output * cuts.Kept / doc.Length, MidpointRounding.AwayFromZero), 0, cuts.Kept);

    /// <summary>The output frame cut frame <paramref name="cut"/> plays at, rounded.</summary>
    private static int Output(TimelineDocument doc, MotionCuts cuts, int cut) =>
        (int)Math.Round(cut * (double)doc.Length / cuts.Kept, MidpointRounding.AwayFromZero);

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
