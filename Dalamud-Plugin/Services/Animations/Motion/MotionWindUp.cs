using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A startup that winds up from a pose into its loop. The game cross-fades from a startup into its loop bone by bone, so a
/// loop whose first pose is far from the idle swings and rolls the arms on the way; the wind-up makes that move itself.
/// </summary>
internal static class MotionWindUp
{
    // A bone rolls about its length only from this share of the wind-up on, so it points where it goes first.
    private const float RollFrom = .25f;
    // A child this near its parent says nothing about where the bone points.
    private const float ShortBone = 1e-4f;

    /// <summary>
    /// <paramref name="frames"/> frames from <paramref name="from"/> (one local transform per bone of <paramref name="playing"/>'s
    /// skeleton) toward <paramref name="loop"/>'s own motion counted back from the hand-off, then the loop's first
    /// <paramref name="fade"/> frames: the game starts the loop that many frames before the startup ends, so its fade shows the
    /// same pose on both sides. Bones match by name; one the loop lacks stays at <paramref name="from"/>. Each bone turns to
    /// point where the loop has it from the start and rolls about its length (toward its longest child) from a quarter of the
    /// way in, so arms don't corkscrew. The float tracks hold <paramref name="playing"/>'s first.
    /// </summary>
    public static MotionClip Build(MotionClip playing, MotionTransform[] from, MotionClip loop, int frames, int fade)
    {
        if (frames < 1)
            throw new InvalidDataException("A wind-up needs at least one frame.");
        if (fade < 0)
            throw new InvalidDataException("The fade can't be negative.");
        if (from.Length != playing.Bones)
            throw new InvalidDataException("The pose is for another skeleton.");
        var skeleton = playing.Skeleton;
        var clip = MotionHold.Held(playing, 0, frames + fade);
        var loopIndex = MotionEvaluator.Index(loop.Skeleton);
        var map = new int[playing.Bones];
        for (var b = 0; b < map.Length; b++)
            map[b] = loopIndex.TryGetValue(skeleton.Bones[b].Name, out var l) ? l : -1;
        var axes = Axes(skeleton);
        var length = Math.Max(1, TimelineDocument.MotionFrames(loop.Duration) ?? 1);
        var poses = new Dictionary<int, MotionTransform[]>();
        for (var k = 0; k < clip.Samples; k++)
        {
            var at = ((k - frames) % length + length) % length;
            if (!poses.TryGetValue(at, out var target))
                poses[at] = target = loop.Pose(at);
            var s = Math.Min(k / (float)frames, 1);
            var swingWeight = MotionCutter.Rise(s);
            var rollWeight = MotionCutter.Rise(Math.Clamp((s - RollFrom) / (1 - RollFrom), 0, 1));
            for (var b = 0; b < map.Length; b++)
            {
                var start = from[b];
                if (map[b] < 0)
                {
                    clip.Set(k, b, start);
                    continue;
                }
                var aim = target[map[b]];
                if (s >= 1)
                {
                    clip.Set(k, b, aim);
                    continue;
                }
                var turn = Quaternion.Normalize(Quaternion.Inverse(Quaternion.Normalize(start.Rotation)) *
                                                MotionEvaluator.Align(Quaternion.Normalize(aim.Rotation), start.Rotation));
                Quaternion part;
                if (axes[b] is { } axis)
                {
                    var roll = MotionFixes.Twist(turn, axis);
                    var swing = Quaternion.Normalize(turn * Quaternion.Inverse(roll));
                    part = MotionEvaluator.Pow(swing, swingWeight) * MotionEvaluator.Pow(roll, rollWeight);
                }
                else
                    part = MotionEvaluator.Pow(turn, swingWeight);
                clip.Set(k, b, new MotionTransform(
                    Vector3.Lerp(start.Position, aim.Position, swingWeight),
                    Quaternion.Normalize(start.Rotation * part),
                    Vector3.Lerp(start.Scale, aim.Scale, swingWeight)));
            }
        }
        return clip;
    }

    /// <summary>Each bone's length in its own space: toward its child farthest from it at rest; null without one.</summary>
    private static Vector3?[] Axes(Models.SkeletonDescription skeleton)
    {
        var axes = new Vector3?[skeleton.Bones.Length];
        var longest = new float[skeleton.Bones.Length];
        for (var c = 0; c < skeleton.Bones.Length; c++)
        {
            var parent = skeleton.Bones[c].Parent;
            var offset = skeleton.Bones[c].Reference.Position;
            if (parent < 0 || offset.Length() <= Math.Max(ShortBone, longest[parent]))
                continue;
            longest[parent] = offset.Length();
            axes[parent] = Vector3.Normalize(offset);
        }
        return axes;
    }
}
