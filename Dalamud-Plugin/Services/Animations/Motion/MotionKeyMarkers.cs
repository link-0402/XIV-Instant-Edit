using System.Collections.Immutable;
using System.Numerics;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A frame where many bones have a marker at once: a key pose of the motion. <paramref name="Bones"/> is how many.</summary>
internal readonly record struct KeyPose(int Frame, int Bones);

/// <summary>
/// Where a motion keeps its own key frames: each moving bone's markers (whole timeline frames, sorted, with its
/// first and last) and the key poses they add up to. Bones that never move have none.
/// </summary>
internal sealed class MotionMarkerSet(IReadOnlyDictionary<string, ImmutableArray<int>> bones, ImmutableArray<KeyPose> poses, int length, bool loops, MarkerDetail detail)
{
    public IReadOnlyDictionary<string, ImmutableArray<int>> Bones { get; } = bones;
    public ImmutableArray<KeyPose> Poses { get; } = poses;

    /// <summary>The timeline length in frames the markers were found for.</summary>
    public int Length { get; } = length;
    public bool Loops { get; } = loops;
    public MarkerDetail Detail { get; } = detail;

    /// <summary>A bone's marker frames, sorted; empty for a still or unknown bone.</summary>
    public ImmutableArray<int> Of(string bone) => Bones.TryGetValue(bone, out var frames) ? frames : [];
}

/// <summary>
/// Finds the key frames inside a decoded motion: per bone, the samples a straight line between its neighbours would
/// miss by more than the detail's tolerance, then the frames where many bones key together.
/// </summary>
internal static class MotionKeyMarkers
{
    /// <summary>How far a bone may stray from a straight line between two markers: degrees, metres and scale fraction.</summary>
    public static (float Degrees, float Metres, float Scale) Tolerance(MarkerDetail detail) => detail switch
    {
        MarkerDetail.Fewer => (2f, 0.005f, 0.02f),
        MarkerDetail.More => (0.1f, 0.0002f, 0.001f),
        _ => (0.5f, 0.001f, 0.005f),
    };

    /// <summary>
    /// Every moving bone's markers for a motion played over <paramref name="length"/> timeline frames, and the key poses.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="token"/> is cancelled.
    /// </summary>
    public static MotionMarkerSet Find(MotionClip motion, int length, MarkerDetail detail, CancellationToken token = default)
    {
        length = Math.Max(0, length);
        var tolerance = Tolerance(detail);
        var last = motion.Samples - 1;
        var frames = new int[motion.Samples];
        for (var s = 0; s < frames.Length; s++)
            frames[s] = s == 0 ? 0 : s == last ? length : Math.Clamp((int)MathF.Round(motion.Frame(s)), 0, length);

        var bones = new Dictionary<string, ImmutableArray<int>>(StringComparer.Ordinal);
        for (var bone = 0; bone < motion.Bones; bone++)
        {
            token.ThrowIfCancellationRequested();
            if (!Moves(motion, bone, tolerance))
                continue;
            var kept = new SortedSet<int>();
            foreach (var sample in Simplify(motion, bone, tolerance))
                kept.Add(frames[sample]);
            bones[motion.Skeleton.Bones[bone].Name] = [.. kept];
        }
        return new MotionMarkerSet(bones, Poses(bones, length), length, motion.Loops, detail);
    }

    /// <summary>
    /// The key poses: frames where at least a quarter of the moving bones have a marker within a frame, those within
    /// two frames of each other merged into the one with the most bones (the earliest on a tie), and always frames 0 and <paramref name="length"/>.
    /// </summary>
    public static ImmutableArray<KeyPose> Poses(IReadOnlyDictionary<string, ImmutableArray<int>> bones, int length)
    {
        length = Math.Max(0, length);
        var threshold = Math.Max(1, (int)Math.Ceiling(bones.Count / 4.0));
        var count = new int[length + 1];
        var covered = new bool[length + 1];
        foreach (var markers in bones.Values)
        {
            Array.Clear(covered);
            foreach (var marker in markers)
                for (var f = Math.Max(0, marker - 1); f <= Math.Min(length, marker + 1); f++)
                    covered[f] = true;
            for (var f = 0; f <= length; f++)
                if (covered[f])
                    count[f]++;
        }

        var candidates = new List<int>();
        for (var f = 0; f <= length; f++)
            if (count[f] >= threshold || f == 0 || f == length)
                candidates.Add(f);

        var poses = ImmutableArray.CreateBuilder<KeyPose>();
        for (var i = 0; i < candidates.Count;)
        {
            var end = i + 1;
            while (end < candidates.Count && candidates[end] - candidates[end - 1] <= 2)
                end++;
            var ends = false;
            var best = candidates[i];
            for (var k = i; k < end; k++)
            {
                var f = candidates[k];
                if (f == 0 || f == length)
                {
                    ends = true;
                    poses.Add(new KeyPose(f, count[f]));
                }
                else if (count[f] > count[best])
                    best = f;
            }
            if (!ends)
                poses.Add(new KeyPose(best, count[best]));
            i = end;
        }
        return poses.ToImmutable();
    }

    private static bool Moves(MotionClip motion, int bone, (float Degrees, float Metres, float Scale) tolerance)
    {
        var first = motion.Get(0, bone);
        for (var s = 1; s < motion.Samples; s++)
            if (Error(first, motion.Get(s, bone), tolerance) > 1)
                return true;
        return false;
    }

    /// <summary>The samples to keep: the ends, and each one a straight line between its kept neighbours misses by more than the tolerance.</summary>
    private static List<int> Simplify(MotionClip motion, int bone, (float Degrees, float Metres, float Scale) tolerance)
    {
        var last = motion.Samples - 1;
        var kept = new List<int> { 0, last };
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, last));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            if (to - from < 2)
                continue;
            var (a, b) = (motion.Get(from, bone), motion.Get(to, bone));
            var (worst, at) = (1f, -1);
            for (var k = from + 1; k < to; k++)
            {
                var t = (k - from) / (float)(to - from);
                var line = new MotionTransform(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Scale, b.Scale, t));
                var error = Error(motion.Get(k, bone), line, tolerance);
                if (error > worst)
                    (worst, at) = (error, k);
            }
            if (at < 0)
                continue;
            kept.Add(at);
            stack.Push((from, at));
            stack.Push((at, to));
        }
        return kept;
    }

    /// <summary>How far two transforms are apart in tolerances: the largest of the turn, the move and the scale change.</summary>
    private static float Error(MotionTransform a, MotionTransform b, (float Degrees, float Metres, float Scale) tolerance)
    {
        var dot = Math.Min(1.0, Math.Abs((double)a.Rotation.X * b.Rotation.X + (double)a.Rotation.Y * b.Rotation.Y + (double)a.Rotation.Z * b.Rotation.Z + (double)a.Rotation.W * b.Rotation.W));
        var degrees = 2 * Math.Acos(dot) * 180 / Math.PI;
        var error = (float)(degrees / tolerance.Degrees);
        error = Math.Max(error, Vector3.Distance(a.Position, b.Position) / tolerance.Metres);
        return Math.Max(error, Math.Max(Ratio(a.Scale.X, b.Scale.X), Math.Max(Ratio(a.Scale.Y, b.Scale.Y), Ratio(a.Scale.Z, b.Scale.Z))) / tolerance.Scale);
    }

    /// <summary>How far <paramref name="a"/> is from <paramref name="b"/> as a fraction of <paramref name="b"/>.</summary>
    private static float Ratio(float a, float b) => b == 0 ? (a == 0 ? 0 : float.PositiveInfinity) : MathF.Abs(a / b - 1);
}
