using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The face a face session's bone layers edit: where its motion starts on the timeline (<paramref name="Anchor"/>, an
/// expression's first face event), how long it was when loaded and how long it is now, in frames. A still face
/// (<see cref="Still"/>) is one shape, as every vanilla face is: a pose of <see cref="TimelineDocument.PoseFrames"/>
/// frames or fewer that the game holds. A moving one plays its own length from the anchor.
/// </summary>
internal sealed record TimelineFace(int Anchor, int SourceFrames, int Frames)
{
    /// <summary>One shape the game holds, not a face that moves.</summary>
    public bool Still => Frames <= TimelineDocument.PoseFrames;

    /// <summary>Whether the face's own length changed since it was loaded.</summary>
    public bool LengthChanged => Frames != SourceFrames;

    /// <summary>The timeline frame the face's motion ends at.</summary>
    public int End => Anchor + Frames;

    /// <summary>The face's own frame at <paramref name="timelineFrame"/>: always its first for a still face.</summary>
    public float FaceFrame(float timelineFrame) => Still ? 0 : Math.Clamp(timelineFrame - Anchor, 0, Frames);

    /// <summary>
    /// How long the canvas is with this face over a timeline of <paramref name="baseLength"/> frames: a still face is held
    /// as long as an expression lasts, a moving one is shown whole even past the timeline, as the game plays it.
    /// </summary>
    public int ShownLength(int baseLength) => Math.Max(baseLength, Still ? Anchor + TimelineDocument.ExpressionLength : End);

    /// <summary>
    /// The face file a face event playing <paramref name="motion"/> plays from, for the character's race and face folder
    /// (<paramref name="key"/>): the always-loaded face.pap when it holds the motion (<paramref name="resident"/>), else
    /// the timeline's face pack (<paramref name="pack"/>); null when neither has it.
    /// </summary>
    public static string? FacePath(TimelineCatalogKey key, string motion, string? pack, IReadOnlySet<string> resident)
    {
        var folder = $"chara/human/c{key.Race:D4}/animation/{key.Face}";
        return resident.Contains(motion) ? $"{folder}/resident/face.pap"
            : !string.IsNullOrEmpty(pack) && TmbFile.IsSafePackName(pack) ? $"{folder}/nonresident/{pack}.pap"
            : null;
    }

    /// <summary>
    /// The timeline length a face is shown over: an expression's header length, or for a face opened on its own (whose
    /// own timeline says 0) the game's expression length.
    /// </summary>
    public static int BaseLength(TimelineDocument doc) => TimelineDocument.ExpressionFrames(doc.HeaderLength);

    /// <summary>Whether a face takes a layer of <paramref name="kind"/> (and <paramref name="fix"/>): the ones that rely on body bones are left out.</summary>
    public static bool Allows(MotionLayerKind kind, MotionFixKind? fix) => kind switch
    {
        MotionLayerKind.Offset or MotionLayerKind.Amplitude or MotionLayerKind.Pose or MotionLayerKind.Symmetry or MotionLayerKind.Swing
            or MotionLayerKind.Smooth or MotionLayerKind.Noise => true,
        MotionLayerKind.Fix => fix == MotionFixKind.SmoothJitter,
        _ => false,
    };

    /// <summary>
    /// <paramref name="layers"/> in the face's own frames: their spans and keys moved back by <paramref name="anchor"/>,
    /// as the face's motion starts there. What lies before the face's first frame never plays: a layer starting earlier
    /// covers the face from its first frame, its fade in shortened by as much, and the last of its keys before it holds
    /// there; a layer over earlier frames only is left out. A layer a face doesn't take is refused.
    /// </summary>
    public static ImmutableArray<MotionLayer> ToFace(ImmutableArray<MotionLayer> layers, int anchor)
    {
        foreach (var layer in layers)
            if (!Allows(layer.Kind, layer.Fix?.Kind) || !layer.Pins.IsEmpty || layer.Idle != null || layer.Markers != null)
                throw new InvalidDataException($"{layer.Name} can't go onto a face.");
        if (anchor == 0)
            return layers;
        var face = ImmutableArray.CreateBuilder<MotionLayer>(layers.Length);
        foreach (var layer in layers)
        {
            var end = layer.End - anchor;
            if (end <= 0)
                continue;
            var start = Math.Max(0, layer.Start - anchor);
            var keys = layer.Keys.Select(key => key with { Frame = key.Frame - anchor }).ToList();
            if (keys.FindLastIndex(key => key.Frame < 0) is var early and >= 0)
            {
                var held = keys.Any(key => key.Frame == 0) ? null : keys[early] with { Frame = 0 };
                keys.RemoveRange(0, early + 1);
                if (held != null)
                    keys.Insert(0, held);
            }
            var fadeIn = Math.Clamp(layer.FadeIn - (start - (layer.Start - anchor)), 0, end - start);
            // Weight points move back with the keys; those before the face's first frame become one point there with the weight it had.
            var weights = layer.Weights.Select(point => point with { Frame = point.Frame - anchor }).Where(point => point.Frame >= 0).ToList();
            if (layer.Weights.Any(point => point.Frame < anchor) && !weights.Any(point => point.Frame == 0))
                weights.Insert(0, new MotionWeight(0, MotionEvaluator.WeightAt(layer, anchor)));
            var moved = layer with
            {
                Start = start, End = end, FadeIn = fadeIn, FadeOut = Math.Min(layer.FadeOut, end - start - fadeIn), Keys = [.. keys],
            };
            face.Add(moved with { Weights = MotionEvaluator.FitWeights(moved, weights) });
        }
        return face.ToImmutable();
    }

    /// <summary>
    /// <paramref name="motion"/>, the face's motion in its own frames, on the timeline: held at its first frame until
    /// <paramref name="anchor"/>, where the face starts, so the timeline's frames sample it as Live and the save play it.
    /// The motion itself for a face that starts at frame 0.
    /// </summary>
    public static MotionClip Delayed(MotionClip motion, int anchor)
    {
        if (anchor <= 0)
            return motion;
        var frames = Math.Max(1, (int)TimelineDocument.Frames(motion.Duration));
        var shown = new MotionClip(motion.Skeleton, (anchor + frames) / (float)TimelineDocument.Fps, anchor + frames + 1)
        {
            SourceTracks = motion.SourceTracks, ReferenceFollowers = motion.ReferenceFollowers, BlendHint = motion.BlendHint, Loops = motion.Loops,
        };
        for (var sample = 0; sample < shown.Samples; sample++)
        {
            var pose = motion.Pose(Math.Max(0, sample - anchor));
            for (var bone = 0; bone < pose.Length; bone++)
                shown.Set(sample, bone, pose[bone]);
        }
        return shown;
    }

    /// <summary>
    /// <paramref name="doc"/> as a face session: its face starts at <paramref name="anchor"/> and lasts
    /// <paramref name="faceFrames"/> frames, over a timeline of <paramref name="baseLength"/>. Opening isn't a length change.
    /// </summary>
    public static TimelineDocument Open(TimelineDocument doc, int anchor, int faceFrames, int baseLength)
    {
        var face = new TimelineFace(anchor, faceFrames, faceFrames);
        var length = face.ShownLength(baseLength);
        return doc with { Face = face, Length = length, SourceLength = length };
    }

    /// <summary>
    /// <paramref name="doc"/> with its face lasting <paramref name="frames"/> frames (1 to <see cref="TimelineDocument.MaxLength"/>)
    /// and the canvas as long as it shows it over <paramref name="baseLength"/>; the same document when nothing changes.
    /// </summary>
    public static TimelineDocument WithFrames(TimelineDocument doc, int frames, int baseLength)
    {
        if (doc.Face is not { } face)
            return doc;
        var next = face with { Frames = Math.Clamp(frames, 1, TimelineDocument.MaxLength) };
        var length = next.ShownLength(baseLength);
        return next == face && length == doc.Length ? doc : doc with { Face = next, Length = length };
    }

    /// <summary>
    /// The document the face's own clip is baked with: <paramref name="faceFile"/> (the face entry's embedded timeline,
    /// as read when loaded) at the face's length, with <paramref name="session"/>'s layers in the face's frames, its
    /// left-out bones and its motion from a hold.
    /// </summary>
    public static TimelineDocument BakeDocument(TimelineDocument session, TimelineDocument faceFile)
    {
        var face = session.Face ?? throw new InvalidOperationException("This timeline has no face.");
        return faceFile with
        {
            Face = null,
            Length = face.Frames,
            SourceLength = face.SourceFrames,
            Motion = ToFace(session.Motion, face.Anchor),
            NextMotionKey = session.NextMotionKey,
            ExcludedBones = session.ExcludedBones,
            Replacement = session.Replacement,
        };
    }
}
