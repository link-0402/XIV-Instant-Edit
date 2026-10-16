using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>Which parts of a bone's transform follow the sampling skeleton's reference pose in every sample.</summary>
[Flags]
internal enum ReferenceComponents
{
    None = 0,
    Translation = 1,
    Rotation = 2,
    Scale = 4,
    All = Translation | Rotation | Scale,
}

/// <summary>
/// Finds the channels of a compressed clip that store nothing and follow whatever skeleton plays the clip,
/// so a bake keeps them trackless instead of freezing them at the rest pose of the skeleton it sampled on.
/// <para>
/// Quantized and predictive clips may leave channels unstored; Havok fills those from the sampling
/// skeleton's reference pose (see <see cref="AnimationNative.Sampler.Sample"/>), so in game they follow the
/// player's own rig. A bake writes uncompressed or spline tracks, which store every channel, so a track for
/// such a bone pins it to the bake skeleton's rest (2 cm on YAS Posing's IVCS bones, for instance).
/// </para>
/// <para>
/// The bake samples the source twice: on its skeleton and on a copy whose reference pose is moved, turned and
/// scaled (<see cref="Perturbed"/>). A component follows the reference when both samples equal their
/// skeleton's reference in every sample. Rulings:
/// <list type="bullet">
/// <item>A bone whose translation, rotation and scale all follow loses its track (<see cref="Drop"/>), as long
/// as the bake's output still sits at the output skeleton's reference in every sample. An edit that moves it
/// (offsets, bone layers, cuts, a retarget collapsing a dropped moving ancestor into it) keeps its track.</item>
/// <item>A partially stored bone (say, rotation stored and translation from rest) keeps its track: a Havok
/// track stores whole transforms, so the motion needs one, and its unstored parts are written at the output
/// skeleton's rest. That is the source skeleton's on a timeline save (never a Posing skeleton) and the
/// standard's on a repair.</item>
/// <item>Additive clips are judged on the pose with the blend applied: a channel only drops when that pose is
/// the reference, which is what a trackless bone samples.</item>
/// <item>Other clip types (interleaved, spline) store every channel, so nothing follows and nothing changes.</item>
/// </list>
/// </para>
/// No Dalamud dependencies.
/// </summary>
internal sealed class AnimationReferenceChannels
{
    // Reference channels are copied, not decoded, so a tight match holds; a stored channel is ~0.3 rad, 2 cm
    // and 6% of scale away from the perturbed reference.
    private const float PositionTolerance = 0.00001f;
    private const float RotationTolerance = 0.000001f;
    private static readonly Vector3 Shift = new(0.0173f, -0.0291f, 0.0237f);
    private static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 0.3f);
    private const float Stretch = 1.0625f;

    private readonly ReferenceComponents[] components;
    private int remaining;

    public AnimationReferenceChannels(int bones)
    {
        components = Enumerable.Repeat(ReferenceComponents.All, bones).ToArray();
        remaining = bones;
    }

    /// <summary>Whether a clip of this type can leave channels to the skeleton's reference pose.</summary>
    public static bool Applies(hkaAnimation.AnimationType type) =>
        type is hkaAnimation.AnimationType.QuantizedCompressedAnimation or hkaAnimation.AnimationType.PredictiveCompressedAnimation;

    /// <summary>A reference transform moved, turned and scaled, so a channel copied from it differs from a stored one.</summary>
    public static BoneTransform Perturb(BoneTransform reference) =>
        new(reference.Position + Shift, Quaternion.Normalize(reference.Rotation * Turn), reference.Scale * Stretch);

    /// <summary><paramref name="skeleton"/> with every bone's reference pose perturbed (<see cref="Perturb"/>).</summary>
    public static SkeletonDescription Perturbed(SkeletonDescription skeleton) =>
        skeleton with { Bones = [.. skeleton.Bones.Select(b => b with { Reference = Perturb(b.Reference) })] };

    /// <summary>
    /// Takes one sample of the source: <paramref name="sampled"/> on the source skeleton (reference
    /// <paramref name="reference"/>) and <paramref name="perturbed"/> on its perturbed copy.
    /// </summary>
    public void Observe(ReadOnlySpan<hkQsTransformf> sampled, ReadOnlySpan<hkQsTransformf> reference,
        ReadOnlySpan<hkQsTransformf> perturbed, ReadOnlySpan<hkQsTransformf> perturbedReference)
    {
        if (remaining == 0) return;
        if (sampled.Length != components.Length || reference.Length != components.Length ||
            perturbed.Length != components.Length || perturbedReference.Length != components.Length)
            throw new InvalidDataException("Reference channel samples don't fit the skeleton.");
        for (var b = 0; b < components.Length; b++)
        {
            if (components[b] == ReferenceComponents.None) continue;
            var kept = Matching(sampled[b], reference[b]) & Matching(perturbed[b], perturbedReference[b]);
            if ((components[b] &= kept) == ReferenceComponents.None) remaining--;
        }
    }

    /// <summary>The components of <paramref name="bone"/> that followed the reference in every sample so far.</summary>
    public ReferenceComponents Components(int bone) => components[bone];

    /// <summary>Bones that follow the reference entirely: their tracks can go.</summary>
    public IEnumerable<int> Followers => Enumerable.Range(0, components.Length).Where(b => components[b] == ReferenceComponents.All);

    /// <summary>Bones with some components from the reference and some stored: they keep their tracks.</summary>
    public IEnumerable<int> Partial => Enumerable.Range(0, components.Length)
        .Where(b => components[b] is not (ReferenceComponents.None or ReferenceComponents.All));

    /// <summary>
    /// Removes the tracks of <paramref name="followers"/> (output skeleton bones) whose samples in
    /// <paramref name="expected"/> still sit at <paramref name="reference"/> in every sample, and holds
    /// validation to that reference for them, which is what Havok samples for a bone with no track. A
    /// follower an edit moved keeps its track. Returns the bones whose tracks went.
    /// </summary>
    public static HashSet<int> Drop(List<short> tracks, IReadOnlyList<hkQsTransformf[]> expected,
        ReadOnlySpan<hkQsTransformf> reference, IEnumerable<int> followers)
    {
        var dropped = new HashSet<int>();
        foreach (var bone in followers)
        {
            if (bone < 0 || bone >= reference.Length || !tracks.Contains((short)bone)) continue;
            var still = true;
            foreach (var frame in expected)
                if (Matching(frame[bone], reference[bone]) != ReferenceComponents.All) { still = false; break; }
            if (still) dropped.Add(bone);
        }
        if (dropped.Count == 0) return dropped;
        tracks.RemoveAll(bone => dropped.Contains(bone));
        foreach (var frame in expected)
            foreach (var bone in dropped) frame[bone] = reference[bone];
        return dropped;
    }

    private static ReferenceComponents Matching(hkQsTransformf value, hkQsTransformf reference)
    {
        var result = ReferenceComponents.None;
        if (Vector3.Distance(AnimationNative.Translation(value), AnimationNative.Translation(reference)) <= PositionTolerance)
            result |= ReferenceComponents.Translation;
        if (1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(AnimationNative.Rotation(value)),
                Quaternion.Normalize(AnimationNative.Rotation(reference)))) <= RotationTolerance)
            result |= ReferenceComponents.Rotation;
        if (Vector3.Distance(AnimationNative.Scale(value), AnimationNative.Scale(reference)) <= PositionTolerance)
            result |= ReferenceComponents.Scale;
        return result;
    }
}
