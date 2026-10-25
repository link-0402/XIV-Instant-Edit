using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// The game's conversion of a clip borrowed from another race, undone, so Live can show a motion rebuilt for the
/// character's race while the game still plays the clip's own binding. As measured in game on 2026-10-25, the game
/// copies the clip's rotations unchanged, scales <c>n_hara</c>'s translation by the actor's <c>n_hara</c> rest height
/// over the clip's, and takes every other translation from the actor's own rest.
/// </summary>
internal static class LiveConversion
{
    private const string Hips = "n_hara";

    /// <summary><c>n_hara</c>'s rest height on <paramref name="rebuilt"/> over <paramref name="source"/>'s; 1 when either lacks it or the source's is 0.</summary>
    public static float HipsRatio(SkeletonDescription rebuilt, SkeletonDescription source)
    {
        var to = rebuilt.Bones.FirstOrDefault(bone => bone.Name == Hips);
        var from = source.Bones.FirstOrDefault(bone => bone.Name == Hips);
        return to == null || from == null || from.Reference.Position.Y == 0 ? 1 : to.Reference.Position.Y / from.Reference.Position.Y;
    }

    /// <summary>
    /// <paramref name="rebuilt"/>'s motion on the clip's own skeleton <paramref name="source"/>, which the game turns back into it:
    /// rotations and scales copied by bone name, <c>n_hara</c>'s translation divided by <see cref="HipsRatio"/>, every other
    /// translation at the source's rest. Bones <paramref name="rebuilt"/> lacks sit at the source's rest.
    /// </summary>
    public static MotionClip ToSource(MotionClip rebuilt, SkeletonDescription source, ImmutableArray<short> playedTracks)
    {
        var ratio = HipsRatio(rebuilt.Skeleton, source);
        var index = MotionEvaluator.Index(rebuilt.Skeleton);
        var back = new MotionClip(source, rebuilt.Duration, rebuilt.Samples, rebuilt.TrackFloats, rebuilt.FloatTracks)
            { SourceTracks = playedTracks, BlendHint = rebuilt.BlendHint, Loops = rebuilt.Loops };
        for (var b = 0; b < source.Bones.Length; b++)
        {
            var bone = source.Bones[b];
            var rest = MotionTransform.From(bone.Reference);
            var from = index.TryGetValue(bone.Name, out var found) ? found : -1;
            for (var s = 0; s < rebuilt.Samples; s++)
            {
                if (from < 0)
                {
                    back.Set(s, b, rest);
                    continue;
                }
                var value = rebuilt.Get(s, from);
                back.Set(s, b, value with { Position = bone.Name == Hips ? value.Position / ratio : rest.Position });
            }
        }
        return back;
    }

    /// <summary>
    /// <paramref name="tracks"/>, bones of <paramref name="rebuilt"/>, as bones of <paramref name="source"/> by name, in order; a bone the
    /// source lacks is dropped and counted. Each track's source is its bone's place among <paramref name="playedTracks"/>, the
    /// playing binding's tracks, or -1 when that binding has no track for it.
    /// </summary>
    public static (short[] Tracks, int[] Sources, int Missing) Tracks(SkeletonDescription rebuilt, SkeletonDescription source,
        IReadOnlyList<short> tracks, ImmutableArray<short> playedTracks)
    {
        var index = MotionEvaluator.Index(source);
        var kept = new List<short>();
        var sources = new List<int>();
        var missing = 0;
        foreach (var track in tracks)
        {
            if (!index.TryGetValue(rebuilt.Bones[track].Name, out var bone))
            {
                missing++;
                continue;
            }
            kept.Add(checked((short)bone));
            sources.Add(playedTracks.IndexOf((short)bone));
        }
        return ([.. kept], [.. sources], missing);
    }
}
