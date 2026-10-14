using System.Collections.Immutable;
using System.Text.Json.Serialization;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Which side of a join its blend changes: the frames before it, half each side, or the frames after
/// it. Saved in journals as a number, so new values only ever go at the end.
/// </summary>
internal enum JoinPlacement : byte { Before, Centered, After }

/// <summary>A kept range of the source motion, in source timeline frames: <see cref="From"/> up to, not including, <see cref="To"/>.</summary>
internal sealed record MotionPart(int From, int To)
{
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
/// between consecutive parts, and the loop seam's blend (null: none). The output plays the kept frames
/// over the document's length, so a stretch scales the whole result.
/// </summary>
internal sealed record MotionCuts(int Source, ImmutableArray<MotionPart> Parts, ImmutableArray<MotionJoin> Joins, MotionJoin? Seam = null)
{
    public ImmutableArray<MotionPart> Parts { get; init; } = Parts.IsDefault ? [] : Parts;
    public ImmutableArray<MotionJoin> Joins { get; init; } = Joins.IsDefault ? [] : Joins;

    /// <summary>How many source frames the parts keep.</summary>
    [JsonIgnore]
    public int Kept => Parts.Sum(p => p.Frames);

    /// <summary>Whether this cuts nothing: one part over the whole source and no seam blend.</summary>
    [JsonIgnore]
    public bool Whole => Parts.Length == 1 && Parts[0].From == 0 && Parts[0].To == Source && Seam == null;

    /// <summary>No cuts on a motion <paramref name="source"/> frames long.</summary>
    public static MotionCuts WholeOf(int source) => new(source, [new MotionPart(0, source)], []);

    public void Validate()
    {
        var fits = Source is >= 2 and <= TimelineDocument.MaxLength && Parts.Length > 0 && Joins.Length == Parts.Length - 1 &&
                   Joins.All(Fits) && (Seam == null || Fits(Seam));
        for (var i = 0; fits && i < Parts.Length; i++)
        {
            var part = Parts[i];
            fits = part != null && part.From >= 0 && part.From < part.To && part.To <= Source && (i == 0 || part.From >= Parts[i - 1].To);
        }
        if (!fits)
            throw new InvalidDataException("The cuts don't fit the motion.");
    }

    private static bool Fits(MotionJoin? join) =>
        join is { Frames: >= 0 and <= MotionJoin.MaxFrames } && Enum.IsDefined(join.Placement);

    /// <summary>
    /// The source frame output frame <paramref name="output"/> plays, for an output
    /// <paramref name="length"/> frames long. A boundary between parts plays the right part's start;
    /// the end plays the last part's end.
    /// </summary>
    public double SourceAt(double output, int length)
    {
        var cut = output * Kept / length;
        double start = 0;
        foreach (var part in Parts)
        {
            if (cut < start + part.Frames)
                return part.From + Math.Max(0, cut - start);
            start += part.Frames;
        }
        return Parts[^1].To;
    }

    /// <summary>The part output frame <paramref name="output"/> lies on: a boundary belongs to the right part, the end to the last.</summary>
    public int PartAt(double output, int length)
    {
        var cut = output * Kept / length;
        double start = 0;
        for (var i = 0; i < Parts.Length; i++)
        {
            if (cut < start + Parts[i].Frames)
                return i;
            start += Parts[i].Frames;
        }
        return Parts.Length - 1;
    }

    /// <summary>The output frame <paramref name="part"/> starts at.</summary>
    public double PartStart(int part, int length) => (double)Parts.Take(part).Sum(p => p.Frames) * length / Kept;

    /// <summary>The output frame <paramref name="part"/> ends at.</summary>
    public double PartEnd(int part, int length) => (double)Parts.Take(part + 1).Sum(p => p.Frames) * length / Kept;
}
