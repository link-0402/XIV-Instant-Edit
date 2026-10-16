using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// What a motion from Blender changed: how many bones Blender read, how many of those it moved,
/// how many the armature lacks (they keep the clip's motion), whether the action was made from
/// another animation than the clip, and whether Blender's scale was left out (the motion was sent
/// without it).
/// </summary>
internal sealed record BlenderMotionReport(int FromBlender, int Changed, int Kept, bool OtherClip, bool ScaleIgnored = false);

/// <summary>
/// Puts a motion read from Blender (see <see cref="AnimationRead"/>) onto the clip's skeleton. The
/// fill is the motion as the timeline plays it, the one sent to Blender: bones Blender didn't read
/// keep it, so do the float tracks, and so does every bone Blender left as it was, exactly, which
/// keeps bones the game's physics moves (at their reference pose) without a track.
/// </summary>
internal static class BlenderMotion
{
    public const float PositionTolerance = 1e-4f;
    /// <summary>In degrees.</summary>
    public const float AngleTolerance = 0.01f;
    public const float ScaleTolerance = 1e-4f;

    /// <summary>
    /// <paramref name="playing"/> stretched to the reply's samples and length; a copy when they are the
    /// same. Float tracks are interpolated linearly.
    /// </summary>
    public static MotionClip Fill(MotionClip playing, AnimationRead read)
    {
        var samples = read.Times.Length;
        if (samples == playing.Samples && Math.Abs(read.Duration - playing.Duration) < 1e-6f)
            return playing.Clone();
        var tracks = playing.FloatTracks;
        var floats = playing.TrackFloats == null ? null : new float[(long)samples * tracks];
        var fill = new MotionClip(playing.Skeleton, read.Duration, samples, floats, tracks)
        {
            SourceTracks = playing.SourceTracks,
            BlendHint = playing.BlendHint,
            Loops = playing.Loops,
        };
        for (var k = 0; k < samples; k++)
        {
            var at = (double)k / (samples - 1) * (playing.Samples - 1);
            var low = Math.Clamp((int)Math.Floor(at), 0, playing.Samples - 1);
            var high = Math.Min(low + 1, playing.Samples - 1);
            var t = (float)(at - low);
            for (var b = 0; b < playing.Bones; b++)
                fill.Set(k, b, high == low || t <= 0 ? playing.Get(low, b) : MotionEvaluator.Lerp(playing.Get(low, b), playing.Get(high, b), t));
            if (floats != null)
                for (var f = 0; f < tracks; f++)
                {
                    var from = playing.TrackFloats![low * tracks + f];
                    floats[k * tracks + f] = from + (playing.TrackFloats[high * tracks + f] - from) * t;
                }
        }
        return fill;
    }

    /// <summary>
    /// The clip's new motion: <paramref name="fill"/> with the bones Blender read, each put back relative to
    /// its game parent through the fill's motion of any bone in between, and made continuous for compression.
    /// </summary>
    public static (MotionClip Motion, BlenderMotionReport Report) Build(MotionClip fill, AnimationRead read, string gamePath, string clipName)
    {
        if (read.Times.Length != fill.Samples)
            throw new InvalidDataException("Blender's reply doesn't fit this animation.");
        var skeleton = fill.Skeleton;
        var count = skeleton.Bones.Length;
        var slots = Enumerable.Repeat(-1, count).ToArray();
        for (var slot = 0; slot < read.Bones.Length; slot++)
            slots[read.Bones[slot].Index] = slot;
        var motion = fill.Clone();
        var local = new MotionTransform[count];
        var model = new MotionTransform[count];
        for (var t = 0; t < fill.Samples; t++)
        {
            for (var b = 0; b < count; b++)
            {
                var parent = skeleton.Bones[b].Parent;
                local[b] = fill.Get(t, b);
                if (slots[b] is var slot and >= 0)
                {
                    var value = read.Sample(t, slot);
                    var ancestor = read.Bones[slot].Ancestor;
                    if (ancestor != parent)
                    {
                        var placed = ancestor < 0 ? value : MotionEvaluator.Compose(model[ancestor], value);
                        value = parent < 0 ? placed : MotionEvaluator.Relative(model[parent], placed);
                    }
                    if (!read.KeyedScale)
                        value = value with { Scale = local[b].Scale };
                    local[b] = value;
                    motion.Set(t, b, value);
                }
                model[b] = parent < 0 ? local[b] : MotionEvaluator.Compose(model[parent], local[b]);
            }
        }
        var changed = 0;
        foreach (var bone in read.Bones)
        {
            if (Moved(motion, fill, bone.Index))
            {
                changed++;
                continue;
            }
            for (var t = 0; t < fill.Samples; t++)
                motion.Set(t, bone.Index, fill.Get(t, bone.Index));
        }
        MotionEvaluator.Continuity(motion);
        return (motion, new BlenderMotionReport(read.Bones.Length, changed, count - read.Bones.Length, OtherClip(read.Source, gamePath, clipName),
            read.ScaleIgnored));
    }

    /// <summary>
    /// Whether a motion from Blender changes nothing, so nothing is built: Blender moved no bone, the motion keeps
    /// <paramref name="playingDuration"/>, the length of the motion as the timeline plays it, and that motion is the
    /// clip's own as it is, with no bone layers, cuts or stretch the replacement would take in.
    /// </summary>
    public static bool ChangesNothing(BlenderMotionReport report, float duration, float playingDuration, TimelineDocument document) =>
        report.Changed == 0 && Math.Abs(duration - playingDuration) <= 1e-6f &&
        document.Motion.IsEmpty && !document.Cut && !document.Stretched;

    /// <summary>Whether a bone's motion leaves the fill's by more than the tolerances at any sample.</summary>
    private static bool Moved(MotionClip motion, MotionClip fill, int bone)
    {
        for (var t = 0; t < fill.Samples; t++)
        {
            var a = motion.Get(t, bone);
            var b = fill.Get(t, bone);
            if (Vector3.Distance(a.Position, b.Position) > PositionTolerance || Vector3.Distance(a.Scale, b.Scale) > ScaleTolerance ||
                Degrees(a.Rotation, b.Rotation) > AngleTolerance)
                return true;
        }
        return false;
    }

    /// <summary>The angle between two rotations in degrees; atan2 keeps small angles measurable in single precision.</summary>
    private static float Degrees(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(d.X, d.Y, d.Z).Length(), Math.Abs(d.W)) * 180 / MathF.PI;
    }

    /// <summary>Whether the action says it was made from another clip; an action that says nothing isn't.</summary>
    private static bool OtherClip(IReadOnlyDictionary<string, string> source, string gamePath, string clipName) =>
        source.TryGetValue("gamePath", out var path) && path.Length > 0 && !string.Equals(path, gamePath, StringComparison.OrdinalIgnoreCase) ||
        source.TryGetValue("clip", out var clip) && clip.Length > 0 && !string.Equals(clip, clipName, StringComparison.Ordinal);
}
