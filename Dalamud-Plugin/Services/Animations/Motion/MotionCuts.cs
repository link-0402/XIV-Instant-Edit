using System.Collections.Immutable;
using System.Text.Json.Serialization;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Which side of a join its blend changes: the frames before it, half each side, or the frames after
/// it. Saved in journals as a number, so new values only ever go at the end.
/// </summary>
internal enum JoinPlacement : byte { Before, Centered, After }

/// <summary>
/// A kept range of the source motion, in source timeline frames: <see cref="From"/> up to, not including,
/// <see cref="To"/>. A <see cref="Blend"/> part keeps its frames but plays, instead of its own motion, a
/// blend from the end of the part before it into the start of the part after it. It is never the first
/// or last part and never next to another one, and as it plays no source frames, only its count of
/// frames matters: it may be longer than the source frames between its neighbours. <see cref="Stretch"/>
/// is how many times as long the part plays: 1 normal, 3 at a third of the speed; a blend part's is always 1.
/// </summary>
internal sealed record MotionPart(int From, int To, bool Blend = false, double Stretch = 1)
{
    /// <summary>The shortest stretch: four times the speed.</summary>
    public const double MinStretch = 0.25;

    /// <summary>The longest stretch: a tenth of the speed.</summary>
    public const double MaxStretch = 10;

    [JsonIgnore]
    public int Frames => To - From;
}

/// <summary>The blend where two parts meet, or where a loop's end runs back into its start: its length in frames and its side.</summary>
internal sealed record MotionJoin(int Frames, JoinPlacement Placement)
{
    /// <summary>The longest blend, in frames.</summary>
    public const int MaxFrames = 60;

    /// <summary>A new cut's blend: 12 frames, half each side.</summary>
    public static MotionJoin ForCut { get; } = new(12, JoinPlacement.Centered);

    /// <summary>A loop seam's blend: 12 frames before the end, so the loop's first frames stay as they are.</summary>
    public static MotionJoin ForSeam { get; } = new(12, JoinPlacement.Before);
}

/// <summary>
/// How a timeline's motion is cut: the kept <see cref="Parts"/> of a source motion
/// <see cref="Source"/> frames long, in source order and played back to back, a blend per boundary
/// between consecutive parts, the loop seam's blend (null: none) and the loop start's
/// <see cref="Ease"/>. Each part plays its frames times its stretch, and the output plays that
/// <see cref="Span"/> over the document's length, so the clip's length adds an even stretch on top.
/// </summary>
/// <param name="Ease">
/// Over how many output frames the loop's seam is eased (0: none): one side of it plays at the other's
/// speed and catches up within those frames, so the length and everything outside stay (see <see cref="MotionCutter"/>).
/// </param>
/// <param name="EaseEnd">Whether the ease slows the loop's end into the start, rather than the start out of the end.</param>
internal sealed record MotionCuts(int Source, ImmutableArray<MotionPart> Parts, ImmutableArray<MotionJoin> Joins, MotionJoin? Seam = null, int Ease = 0,
    bool EaseEnd = false)
{
    /// <summary>Fix loop's ease, in frames: long enough that the catch-up stays under about 1.3 times normal speed.</summary>
    public const int DefaultEase = 30;

    public ImmutableArray<MotionPart> Parts { get; init; } = Parts.IsDefault ? [] : Parts;
    public ImmutableArray<MotionJoin> Joins { get; init; } = Joins.IsDefault ? [] : Joins;

    /// <summary>How many source frames the parts keep.</summary>
    [JsonIgnore]
    public int Kept => Parts.Sum(p => p.Frames);

    /// <summary>How long the kept motion plays at its parts' stretches, in normal-speed frames.</summary>
    [JsonIgnore]
    public double Span => Parts.Sum(p => p.Frames * p.Stretch);

    /// <summary>Whether this cuts nothing: one unstretched part over the whole source, no seam blend and no ease.</summary>
    [JsonIgnore]
    public bool Whole => Parts.Length == 1 && Parts[0].From == 0 && Parts[0].To == Source && Parts[0].Stretch == 1 && Seam == null && Ease == 0;

    /// <summary>No cuts on a motion <paramref name="source"/> frames long.</summary>
    public static MotionCuts WholeOf(int source) => new(source, [new MotionPart(0, source)], []);

    public void Validate()
    {
        var fits = Source is >= 2 and <= TimelineDocument.MaxLength && Parts.Length > 0 && Joins.Length == Parts.Length - 1 &&
                   Joins.All(Fits) && (Seam == null || Fits(Seam)) && Ease is >= 0 and <= MotionJoin.MaxFrames;
        // A blend part plays none of the source's frames: only its count matters, and the parts around it keep source order.
        var end = 0;
        for (var i = 0; fits && i < Parts.Length; i++)
        {
            var part = Parts[i];
            // The range check also refuses a stretch that isn't a finite number.
            fits = part != null && part.From >= 0 && part.From < part.To && part.Stretch is >= MotionPart.MinStretch and <= MotionPart.MaxStretch &&
                   (part.Blend
                       ? i > 0 && i < Parts.Length - 1 && !Parts[i - 1].Blend && part.Frames <= TimelineDocument.MaxLength && part.Stretch == 1
                       : part.To <= Source && part.From >= end);
            if (fits && !part.Blend)
                end = part.To;
        }
        if (!fits)
            throw new InvalidDataException("The cuts don't fit the motion.");
    }

    /// <summary>Whether join <paramref name="join"/> is a blend part's edge, where that part lines the motion up and the join's own blend does nothing.</summary>
    public bool BesideBlend(int join) => Parts[join].Blend || Parts[join + 1].Blend;

    private static bool Fits(MotionJoin? join) =>
        join is { Frames: >= 0 and <= MotionJoin.MaxFrames } && Enum.IsDefined(join.Placement);

    /// <summary>
    /// The source frame output frame <paramref name="output"/> plays, for an output
    /// <paramref name="length"/> frames long. A boundary between parts plays the right part's start;
    /// the end plays the last part's end.
    /// </summary>
    public double SourceAt(double output, int length)
    {
        var span = Span;
        var at = output * span / length;
        var drift = Drift(span);
        double start = 0;
        foreach (var part in Parts)
        {
            var end = start + part.Frames * part.Stretch;
            if (at < end - drift)
                return part.From + Math.Max(0, at - start) / part.Stretch;
            start = end;
        }
        return Parts[^1].To;
    }

    /// <summary>The part output frame <paramref name="output"/> lies on: a boundary belongs to the right part, the end to the last.</summary>
    public int PartAt(double output, int length)
    {
        var span = Span;
        var at = output * span / length;
        var drift = Drift(span);
        double start = 0;
        for (var i = 0; i < Parts.Length; i++)
        {
            start += Parts[i].Frames * Parts[i].Stretch;
            if (at < start - drift)
                return i;
        }
        return Parts.Length - 1;
    }

    /// <summary>
    /// How far below a boundary between parts a frame still counts as on it: with stretched parts the
    /// boundary is a float sum, and <see cref="PartStart"/> may land a hair either side of it. Without
    /// stretches every boundary is exact, so none.
    /// </summary>
    private double Drift(double span) => Parts.All(p => p.Stretch == 1) ? 0 : 1e-9 * span;

    /// <summary>The output frame <paramref name="part"/> starts at.</summary>
    public double PartStart(int part, int length) => Parts.Take(part).Sum(p => p.Frames * p.Stretch) * length / Span;

    /// <summary>The output frame <paramref name="part"/> ends at.</summary>
    public double PartEnd(int part, int length) => Parts.Take(part + 1).Sum(p => p.Frames * p.Stretch) * length / Span;

    /// <summary>How many source frames <paramref name="part"/> plays per output frame, for an output <paramref name="length"/> frames long.</summary>
    public double Rate(int part, int length) => Span / (length * Parts[part].Stretch);

    /// <summary>
    /// The output frame cut frame <paramref name="cut"/> plays at: cut frames count the kept parts' frames
    /// from 0. A boundary belongs to the right part; before the start and past the end the first and last
    /// part's speed carries on.
    /// </summary>
    public double OutputOf(double cut, int length)
    {
        var i = 0;
        var from = 0;
        double at = 0;
        for (; i < Parts.Length - 1 && cut >= from + Parts[i].Frames; i++)
        {
            from += Parts[i].Frames;
            at += Parts[i].Frames * Parts[i].Stretch;
        }
        return (at + (cut - from) * Parts[i].Stretch) * length / Span;
    }

    /// <summary>The cut frame output frame <paramref name="output"/> plays, the inverse of <see cref="OutputOf"/>.</summary>
    public double CutOf(double output, int length)
    {
        var at = output * Span / length;
        var i = 0;
        var from = 0;
        double start = 0;
        for (; i < Parts.Length - 1 && at >= start + Parts[i].Frames * Parts[i].Stretch; i++)
        {
            from += Parts[i].Frames;
            start += Parts[i].Frames * Parts[i].Stretch;
        }
        return from + (at - start) / Parts[i].Stretch;
    }

    /// <summary>The output frames the seam's ease covers: the start's first or the end's last <see cref="Ease"/>, within their part.</summary>
    public (double From, double To) EaseSpan(int length)
    {
        var part = EaseEnd ? Parts.Length - 1 : 0;
        var frames = Math.Min(Ease, PartEnd(part, length) - PartStart(part, length));
        return EaseEnd ? (length - frames, length) : (0, frames);
    }
}
