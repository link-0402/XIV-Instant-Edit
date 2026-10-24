using System.Numerics;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A frame that plays another frame's pose the same way: the mean and worst bone angle between the two
/// poses, in degrees.
/// </summary>
internal sealed record PoseMatch(int Frame, float MeanAngle, float WorstAngle);

/// <summary>
/// Finds the frames of a decoded clip that play a given frame's pose the same way, where a loop could
/// run back to it cleanly. Frames match when the poses from <see cref="Window"/> frames before to as many
/// after line up bone for bone: the pose is the same, and it moves the same way at the same speed, so the
/// same pose passed the other way doesn't count. Angles are over the clip's own tracks (every bone when it
/// has none recorded), at whole timeline frames, as <see cref="LoopSeam"/> measures them.
/// </summary>
internal static class PoseMatches
{
    /// <summary>Frames match when every frame of the window is under this mean bone angle apart: a seam this close is clean.</summary>
    public const float MeanDegrees = LoopSeam.PopDegrees;

    /// <summary>No bone may be further off than this in any frame of the window, so one swinging arm can't hide in a good mean.</summary>
    public const float WorstDegrees = 5f;

    /// <summary>
    /// How many frames before and after each frame are compared too. Two frames each way tell the two
    /// directions apart at any speed over <see cref="LoopSeam.MinSpeed"/>, under which motion barely shows.
    /// </summary>
    public const int Window = 2;

    /// <summary>
    /// The frames that match <paramref name="frame"/>, in order: of each run of matching frames the closest
    /// one, and none from the run around <paramref name="frame"/> itself, nor from the run at the other end when
    /// that run reaches the first or last frame. None when the frame is off the clip.
    /// </summary>
    public static IReadOnlyList<PoseMatch> Find(MotionClip clip, int frame) => Find(Sample(clip), frame);

    /// <summary>
    /// The clip's measured bone rotations at every whole timeline frame from 0 to its length, normalised:
    /// what <see cref="Find(Quaternion[][], int)"/> searches, sampled once per clip.
    /// </summary>
    public static Quaternion[][] Sample(MotionClip clip)
    {
        var bones = LoopSeam.Tracked(clip);
        var poses = new Quaternion[LoopSeam.Frames(clip) + 1][];
        for (var f = 0; f < poses.Length; f++)
        {
            poses[f] = LoopSeam.Rotations(clip, f, bones);
            for (var i = 0; i < bones.Length; i++)
                poses[f][i] = Quaternion.Normalize(poses[f][i]);
        }
        return poses;
    }

    /// <inheritdoc cref="Find(MotionClip, int)"/>
    public static IReadOnlyList<PoseMatch> Find(Quaternion[][] poses, int frame)
    {
        var frames = poses.Length - 1;
        if (frame < 0 || frame > frames || poses[0].Length == 0)
            return [];
        var bones = poses[0].Length;

        // Per frame its match, or null; frames next to a match are about as close as it, so runs of them follow.
        var found = new PoseMatch?[frames + 1];
        for (var f = 0; f <= frames; f++)
            found[f] = f == frame ? new PoseMatch(f, 0, 0) : Compare(poses, f, frame, bones);

        var runs = new List<(int From, int To, PoseMatch Best)>();
        for (var f = 0; f <= frames;)
        {
            if (found[f] == null)
            {
                f++;
                continue;
            }
            var from = f;
            PoseMatch best = found[f]!;
            for (; f <= frames && found[f] is { } match; f++)
                if (match.MeanAngle < best.MeanAngle)
                    best = match;
            runs.Add((from, f - 1, best));
        }
        // The first and last frame are one moment of the loop: when the playhead's run reaches one end, the run at
        // the other end is its own too (keeping the frames between them would keep everything).
        var own = runs.First(r => r.From <= frame && frame <= r.To);
        return [.. runs.Where(r => r != own && !(own.From == 0 && r.To == frames) && !(own.To == frames && r.From == 0)).Select(r => r.Best)];
    }

    /// <summary>
    /// Frame <paramref name="f"/> against <paramref name="p"/> over the window, both ends cut where either
    /// leaves the clip: the centre's angles when every frame is close enough, else null.
    /// </summary>
    private static PoseMatch? Compare(Quaternion[][] poses, int f, int p, int bones)
    {
        // A bone further off than the worst allowed has a quaternion dot under this, so most frames are
        // turned down on their first bones without an angle worked out.
        var minDot = MathF.Cos(WorstDegrees * MathF.PI / 360);
        var limit = MeanDegrees * bones;
        float centreMean = 0, centreWorst = 0;
        for (var o = -Window; o <= Window; o++)
        {
            int a = f + o, b = p + o;
            if (a < 0 || b < 0 || a >= poses.Length || b >= poses.Length)
                continue;
            var left = poses[a];
            var right = poses[b];
            double sum = 0;
            float worst = 0;
            for (var i = 0; i < bones; i++)
            {
                var dot = Math.Abs(Quaternion.Dot(left[i], right[i]));
                if (dot < minDot)
                    return null;
                var angle = 2 * MathF.Acos(Math.Min(dot, 1)) * 180 / MathF.PI;
                sum += angle;
                worst = Math.Max(worst, angle);
                if (sum > limit)
                    return null;
            }
            if (o == 0)
                (centreMean, centreWorst) = ((float)(sum / bones), worst);
        }
        return new PoseMatch(f, centreMean, centreWorst);
    }
}
