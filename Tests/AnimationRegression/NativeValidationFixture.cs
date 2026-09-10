// Exercises managed guards over owned native-shaped memory; no game function is called.
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;
using InstantEdit.Services.Animations;

internal static unsafe class NativeValidationFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        using var arena = new AnimationNative.Arena();
        var animation = arena.Alloc<AnimationNative.Interleaved>();
        animation->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
        animation->Animation.Duration = 1;
        animation->Animation.NumberOfTransformTracks = 1;
        animation->Animation.NumberOfFloatTracks = 1;
        animation->Transforms = arena.Array<hkQsTransformf>(2);
        animation->Floats = arena.Copy<float>([0.1f, 0.9f]);
        var binding = arena.Alloc<hkaAnimationBinding>();
        binding->Animation = new hkRefPtr<hkaAnimation> { ptr = &animation->Animation };
        binding->TransformTrackToBoneIndices = arena.Copy<short>([0]);
        binding->FloatTrackToFloatSlotIndices = arena.Copy<short>([0]);
        var original = AnimationNative.Fingerprint(binding);
        animation->Floats[1] = .8f;
        check(AnimationNative.Fingerprint(binding) != original, "binding identity includes float channel samples");
        animation->Floats.Length = 1;
        reject(() => AnimationNative.Fingerprint(binding), "mismatched native transform/float sample counts are rejected");
        animation->Floats.Length = 2;
        animation->Transforms.Data = null;
        reject(() => AnimationNative.Fingerprint(binding), "missing native track buffers fail before dereferencing");
        animation->Transforms = arena.Array<hkQsTransformf>(2);
        binding->FloatTrackToFloatSlotIndices.Data = null;
        reject(() => AnimationNative.MetadataFingerprint(binding), "missing metadata index buffers fail before dereferencing");
        binding->FloatTrackToFloatSlotIndices = arena.Copy<short>([0]);
        animation->Transforms.Length = animation->Floats.Length = 0;
        reject(() => AnimationNative.Fingerprint(binding), "declared animation tracks without samples cannot reach the sampler");
    }
}
