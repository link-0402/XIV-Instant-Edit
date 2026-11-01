using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// The game's conversion of a clip borrowed from another race, undone, so Live can show a motion rebuilt for the
/// character's race while the game still plays the clip's own binding. As measured in game on 2026-10-25 and 2026-11-02, the
/// game copies the clip's rotations unchanged and maps each translation as the actor's rest plus the clip's move off the clip's
/// rest, times the actor's rest length over the clip's: <c>actorRest + (clip - clipRest) * |actorRest| / |clipRest|</c>. A bone
/// left at the clip's rest lands on the actor's.
/// </summary>
internal static class LiveConversion
{
    private const string Hips = "n_hara";

    /// <summary><c>n_hara</c>'s rest height on <paramref name="rebuilt"/> over <paramref name="source"/>'s; 1 when either lacks it or either height is 0.</summary>
    public static float HipsRatio(SkeletonDescription rebuilt, SkeletonDescription source)
    {
        var to = rebuilt.Bones.FirstOrDefault(bone => bone.Name == Hips);
        var from = source.Bones.FirstOrDefault(bone => bone.Name == Hips);
        return to == null || from == null || to.Reference.Position.Y == 0 || from.Reference.Position.Y == 0 ? 1 : to.Reference.Position.Y / from.Reference.Position.Y;
    }

    /// <summary>
    /// <paramref name="rebuilt"/>'s motion on the clip's own skeleton <paramref name="source"/>, which the game turns back into it:
    /// rotations and scales copied by bone name, translations through the game's mapping undone (<see cref="Unmapped"/>), so
    /// <c>n_hara</c>'s comes out divided by <see cref="HipsRatio"/>. Bones <paramref name="rebuilt"/> lacks sit at the source's
    /// rest. A clip repaired onto a standard of its own race isn't converted by the game (<paramref name="sameRace"/>): every
    /// translation is copied too. The layers' moves of the hips are on <c>n_hara</c> already (<see cref="MotionClip.HipsOnHara"/>),
    /// so they come through.
    /// </summary>
    public static MotionClip ToSource(MotionClip rebuilt, SkeletonDescription source, ImmutableArray<short> playedTracks, bool sameRace = false)
    {
        var index = MotionEvaluator.Index(rebuilt.Skeleton);
        var back = new MotionClip(source, rebuilt.Duration, rebuilt.Samples, rebuilt.TrackFloats, rebuilt.FloatTracks)
            { SourceTracks = playedTracks, BlendHint = rebuilt.BlendHint, Loops = rebuilt.Loops };
        for (var b = 0; b < source.Bones.Length; b++)
        {
            var bone = source.Bones[b];
            var rest = MotionTransform.From(bone.Reference);
            var from = index.TryGetValue(bone.Name, out var found) ? found : -1;
            var actorRest = from < 0 ? Vector3.Zero : rebuilt.Skeleton.Bones[from].Reference.Position;
            for (var s = 0; s < rebuilt.Samples; s++)
            {
                if (from < 0)
                {
                    back.Set(s, b, rest);
                    continue;
                }
                var value = rebuilt.Get(s, from);
                back.Set(s, b, sameRace ? value : value with { Position = Unmapped(value.Position, actorRest, rest.Position) });
            }
        }
        return back;
    }

    /// <summary>
    /// The clip translation the game maps onto <paramref name="position"/>: <paramref name="clipRest"/> plus the move off
    /// <paramref name="actorRest"/>, times the clip's rest length over the actor's. Unscaled when either rest is about 0 long.
    /// </summary>
    private static Vector3 Unmapped(Vector3 position, Vector3 actorRest, Vector3 clipRest)
    {
        var (actor, clip) = (actorRest.Length(), clipRest.Length());
        return clipRest + (position - actorRest) * (actor < 1e-5f || clip < 1e-5f ? 1 : clip / actor);
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
