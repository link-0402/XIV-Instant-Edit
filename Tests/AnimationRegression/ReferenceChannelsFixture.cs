using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// Quantized and predictive clips leave unstored channels to the sampling skeleton's reference pose, so in
/// game they follow the player's rig. A bake must keep those bones trackless instead of freezing them at the
/// rest of the skeleton it sampled on (a Timeline save pinned 171 YAS tracks to YAS Posing's rests). These
/// fixtures cover the managed half: which bones follow the reference, which are only partly stored, and what
/// the build drops and validates.
/// </summary>
internal static class ReferenceChannelsFixture
{
    public static void Run(Action<bool, string> check)
    {
        static BoneTransform T(float x, float y = 0, float turn = 0) =>
            new(new(x, y, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn), Vector3.One);
        static hkQsTransformf Q(BoneTransform t) => AnimationSkeleton.Transform(t);

        check(AnimationReferenceChannels.Applies(hkaAnimation.AnimationType.QuantizedCompressedAnimation) &&
              AnimationReferenceChannels.Applies(hkaAnimation.AnimationType.PredictiveCompressedAnimation) &&
              !AnimationReferenceChannels.Applies(hkaAnimation.AnimationType.SplineCompressedAnimation) &&
              !AnimationReferenceChannels.Applies(hkaAnimation.AnimationType.InterleavedAnimation),
            "only quantized and predictive clips can leave channels to the reference, so other bakes are unchanged");

        // A YAS-like source skeleton: iv_kuritto at its YAS Posing rest.
        var skeleton = new SkeletonDescription("skeleton", "posing", [
            new("n_root", -1, 0, T(0)), new("j_kosi", 0, 0, T(1)),
            new("iv_kuritto", 1, 0, T(0.162f, -0.022f)), new("iv_asi_oya_a_l", 1, 0, T(0.3f, 0.1f)),
            new("j_asi_a_l", 1, 0, T(0.5f))], [], [], []);
        var perturbedSkeleton = AnimationReferenceChannels.Perturbed(skeleton);
        check(perturbedSkeleton.Bones.Select(b => (b.Name, b.Parent)).SequenceEqual(skeleton.Bones.Select(b => (b.Name, b.Parent))) &&
              perturbedSkeleton.Bones.Zip(skeleton.Bones).All(p =>
                  Vector3.Distance(p.First.Reference.Position, p.Second.Reference.Position) > 0.01f &&
                  1 - Math.Abs(Quaternion.Dot(p.First.Reference.Rotation, p.Second.Reference.Rotation)) > 0.001f &&
                  Vector3.Distance(p.First.Reference.Scale, p.Second.Reference.Scale) > 0.01f),
            "the perturbed copy keeps the hierarchy and moves, turns and scales every bone's reference");
        AnimationSkeleton.Validate(perturbedSkeleton);

        var reference = skeleton.Bones.Select(b => Q(b.Reference)).ToArray();
        var perturbedReference = perturbedSkeleton.Bones.Select(b => Q(b.Reference)).ToArray();
        // What Havok samples on each skeleton, per bone: n_root and j_kosi are stored and move; iv_kuritto is
        // left to the reference; the toe stores its rotation and takes its translation and scale from the
        // reference; j_asi_a_l stores a constant equal to its rest, which must stay a track.
        hkQsTransformf[] Sample(int frame, bool perturbed)
        {
            var rest = perturbed ? perturbedSkeleton : skeleton;
            var toe = rest.Bones[3].Reference;
            return
            [
                Q(T(frame * 0.1f)), Q(T(1, frame * 0.05f)),
                Q(rest.Bones[2].Reference),
                Q(toe with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f * frame) }),
                Q(T(0.5f)),
            ];
        }
        var channels = new AnimationReferenceChannels(skeleton.Bones.Length);
        for (var f = 0; f < 3; f++)
            channels.Observe(Sample(f, false), reference, Sample(f, true), perturbedReference);
        check(channels.Followers.SequenceEqual([2]),
            "a bone left to the reference in every sample on both skeletons follows the reference");
        check(channels.Components(3) == (ReferenceComponents.Translation | ReferenceComponents.Scale) &&
              channels.Partial.SequenceEqual([3]),
            "a toe with a stored rotation and its translation from rest is partly stored, not a follower");
        check(channels.Components(4) == ReferenceComponents.None && channels.Components(0) == ReferenceComponents.None,
            "a stored constant that equals the rest is told apart from the reference by the perturbed sample");

        // One sample where the channel stores a value: it's stored, whatever the other samples did.
        var once = new AnimationReferenceChannels(1);
        once.Observe([reference[2]], [reference[2]], [perturbedReference[2]], [perturbedReference[2]]);
        once.Observe([Q(T(0.2f))], [reference[2]], [Q(T(0.2f))], [perturbedReference[2]]);
        once.Observe([reference[2]], [reference[2]], [perturbedReference[2]], [perturbedReference[2]]);
        check(!once.Followers.Any(), "a channel stored in any sample is not a follower");

        // The build. Output frames as the bake samples them on the source skeleton: every source bone tracked.
        List<hkQsTransformf[]> expected = [Sample(0, false), Sample(1, false), Sample(2, false)];
        List<short> tracks = [0, 1, 2, 3, 4];
        var dropped = AnimationReferenceChannels.Drop(tracks, expected, reference, channels.Followers);
        check(dropped.SetEquals([2]) && tracks.SequenceEqual(new short[] { 0, 1, 3, 4 }),
            "the follower's track goes and every other track keeps its place, the partly stored toe included");
        check(expected.All(frame => AnimationSkeleton.Transform(frame[2]) == skeleton.Bones[2].Reference) &&
              AnimationSkeleton.Transform(expected[2][3]).Rotation == Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f),
            "validation expects the dropped bone at the output skeleton's reference and the toe as sampled");

        // An edit moved the follower in one sample (a bone layer, an offset, a cut's blend or a moving
        // ancestor the retarget collapsed into it): it keeps its track and its edited samples.
        List<hkQsTransformf[]> edited = [Sample(0, false), Sample(1, false), Sample(2, false)];
        edited[1][2] = Q(T(0.162f, -0.002f));
        List<short> editedTracks = [0, 1, 2, 3, 4];
        check(AnimationReferenceChannels.Drop(editedTracks, edited, reference, channels.Followers).Count == 0 &&
              editedTracks.SequenceEqual(new short[] { 0, 1, 2, 3, 4 }) &&
              AnimationSkeleton.Transform(edited[1][2]).Position == new Vector3(0.162f, -0.002f, 0),
            "a follower an edit moved keeps its track and its edited samples");

        // A repair onto a standard whose iv_kuritto rest differs (General Usage): the retarget leaves an
        // unmoved bone at the target's reference, so the follower drops and validation expects that reference.
        var general = Q(T(0.1456f, -0.0001f));
        hkQsTransformf[] targetReference = [reference[0], reference[1], general];
        List<hkQsTransformf[]> repaired = [[Q(T(0)), Q(T(1)), general], [Q(T(0.1f)), Q(T(1, 0.05f)), general]];
        List<short> repairTracks = [0, 1, 2];
        int[] boneMap = [0, 1, 2, -1, -1];
        check(AnimationReferenceChannels.Drop(repairTracks, repaired, targetReference,
                  channels.Followers.Select(b => boneMap[b])).SetEquals([2]) &&
              repairTracks.SequenceEqual(new short[] { 0, 1 }),
            "a follower mapped onto a repair standard drops its track there too");

        // Bones not in the output, or with no track, are ignored.
        List<short> partial = [0, 1];
        check(AnimationReferenceChannels.Drop(partial, expected, reference, [2, -1, 9]).Count == 0 && partial.SequenceEqual(new short[] { 0, 1 }),
            "followers without a track or outside the output skeleton change nothing");
    }
}
