// Exercises managed guards over owned native-shaped memory; no game function is called.
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Animation.Rig;
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
        binding->MemSizeAndRefCount = 0xFFFF0002; // native object: size unknown, reference count 2
        var borrowed = arena.BorrowBinding(binding);
        check(borrowed != binding && AnimationNative.Fingerprint(borrowed) == original &&
              borrowed->Animation.ptr == binding->Animation.ptr &&
              borrowed->TransformTrackToBoneIndices.Data == binding->TransformTrackToBoneIndices.Data,
            "borrowed bake bindings retain the source animation and metadata without transferring ownership");
        // Independent native contract from the installed control ctor/dtor:
        // word +0xA gates retain/release; word +0x8 is the reference count.
        static bool ControlWouldDeleteBinding(hkaAnimationBinding* value)
        {
            var memorySize = (ushort*)((byte*)value + 0xA);
            var references = (ushort*)((byte*)value + 0x8);
            if (*memorySize == 0) return false;
            ++*references; // control constructed
            --*references; // control destroyed
            return *references == 0;
        }
        borrowed->MemSizeAndRefCount = 0x00010000;
        check(ControlWouldDeleteBinding(borrowed),
            "the former reversed ownership word lets native control cleanup delete an arena binding");
        borrowed = arena.BorrowBinding(binding);
        check(!ControlWouldDeleteBinding(borrowed) && binding->MemSizeAndRefCount == 0xFFFF0002 &&
              AnimationNative.Fingerprint(binding) == original,
            "native control cleanup cannot delete the borrowed binding or release its source graph");
        borrowed->BlendHint.Storage = 1;
        check(binding->BlendHint.Storage == 0 && borrowed->MemSizeAndRefCount >> 16 == 0,
            "isolated binding changes keep source metadata intact and disable Havok ownership");
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

        var predictive = arena.Alloc<AnimationNative.Predictive>();
        predictive->Animation.Type = hkaAnimation.AnimationType.PredictiveCompressedAnimation;
        predictive->Animation.Duration = 1;
        predictive->Animation.NumberOfTransformTracks = predictive->NumBones = 1;
        predictive->Animation.NumberOfFloatTracks = predictive->NumFloatSlots = 1;
        predictive->NumFrames = 61;
        predictive->CompressedData = arena.Copy<byte>([1, 2, 3, 4]);
        predictive->IntData = arena.Copy<ushort>([0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        for (var i = 1; i < 9; i++) predictive->IntArrayOffsets[i] = 4;
        predictive->FloatData = arena.Array<float>(12);
        binding->Animation.ptr = &predictive->Animation;
        original = AnimationNative.Fingerprint(binding);
        check(original.Length == 64 && AnimationNative.SourceFrameCount(&predictive->Animation) == 61 &&
            AnimationPoseRules.SampleCount(predictive->Animation.Duration, AnimationNative.SourceFrameCount(&predictive->Animation)) >= 61,
            "predictive animation identity and bake sample density accept higher source frame rates");
        predictive->Skeleton = arena.Alloc<hkaSkeleton>();
        predictive->MaxCompressedBytesPerFrame = 123;
        predictive->CompressedData.CapacityAndFlags = 1024;
        check(AnimationNative.Fingerprint(binding) == original,
            "predictive identity ignores runtime skeleton pointers, caches, and array capacity after loading");
        void Changed(Action mutate, Action restore, string name)
        {
            mutate(); check(AnimationNative.Fingerprint(binding) != original, name); restore();
        }
        Changed(() => predictive->CompressedData[1]++, () => predictive->CompressedData[1]--,
            "predictive identity detects compressed motion changes");
        Changed(() => predictive->IntData[4]++, () => predictive->IntData[4]--,
            "predictive identity detects integer channel changes");
        Changed(() => predictive->FloatData[0] = .5f, () => predictive->FloatData[0] = 0,
            "predictive identity detects float channel changes");
        Changed(() => predictive->IntArrayOffsets[8]++, () => predictive->IntArrayOffsets[8]--,
            "predictive identity detects integer table changes");
        Changed(() => predictive->FloatArrayOffsets[2]++, () => predictive->FloatArrayOffsets[2]--,
            "predictive identity detects float table changes");
        Changed(() => predictive->NumFrames++, () => predictive->NumFrames--,
            "predictive identity detects source frame count changes");
        Changed(() => predictive->FirstFloatBlockScaleAndOffsetIndex++, () => predictive->FirstFloatBlockScaleAndOffsetIndex--,
            "predictive identity detects float block indexing changes");
        predictive->CompressedData.Data = null;
        reject(() => AnimationNative.Fingerprint(binding), "missing predictive compressed buffers fail before dereferencing");
        predictive->CompressedData = arena.Copy<byte>([1, 2, 3, 4]);
        predictive->IntArrayOffsets[2] = predictive->IntData.Length + 1;
        reject(() => AnimationNative.Fingerprint(binding), "out-of-range predictive offsets are rejected");
        predictive->IntArrayOffsets[2] = 0;
        reject(() => AnimationNative.Fingerprint(binding), "reversed predictive offset tables are rejected");
        predictive->IntArrayOffsets[2] = 4;
        predictive->FloatArrayOffsets[0] = -1;
        reject(() => AnimationNative.Fingerprint(binding), "negative predictive float offsets are rejected");
        predictive->FloatArrayOffsets[0] = 0;
        predictive->IntData.Length = 0;
        reject(() => AnimationNative.Fingerprint(binding), "predictive integer tables require decoder padding");
        predictive->IntData.Length = 16;
        predictive->FloatArrayOffsets[2] = predictive->FloatData.Length;
        reject(() => AnimationNative.Fingerprint(binding), "predictive channel tables cannot point into decoder padding");
        predictive->FloatArrayOffsets[2] = 0;
        var skeleton = arena.Alloc<hkaSkeleton>();
        skeleton->Bones = arena.Array<hkaBone>(1);
        skeleton->Bones.Data[0].Name.StringAndFlag = arena.Copy<byte>("root\0"u8).Data;
        skeleton->ParentIndices = arena.Copy<short>([-1]);
        skeleton->ReferencePose = arena.Array<hkQsTransformf>(1);
        skeleton->ReferencePose.Data[0].Rotation.W = 1;
        skeleton->ReferencePose.Data[0].Scale.X = skeleton->ReferencePose.Data[0].Scale.Y = skeleton->ReferencePose.Data[0].Scale.Z = 1;
        skeleton->FloatSlots = arena.Array<FFXIVClientStructs.Havok.Common.Base.Container.String.hkStringPtr>(1);
        skeleton->ReferenceFloats = arena.Array<float>(1);
        AnimationNative.Sampler.Validate(skeleton, binding);
        check(true, "predictive animations pass the sampler guards with matching reference channels");
        predictive->NumBones = 2;
        reject(() => AnimationNative.Sampler.Validate(skeleton, binding), "predictive reference bone counts must match before sampling");
        var description = AnimationSkeleton.Describe(skeleton);
        var captured = AnimationRuntime.CaptureBinding(0, binding, "example.sklb", description);
        check(captured != null && captured.Fingerprint == AnimationNative.Fingerprint(binding),
            "a predictive skeleton mismatch remains identifiable for the playing animation list");
        check(AnimationRuntime.CaptureBinding(0, null, "example.sklb", description) == null &&
            AnimationRuntime.CaptureBinding(0, binding, "example.sklb", description) != null,
            "one malformed binding cannot suppress a subsequent valid mismatched binding");
        var materialized = AnimationSkeleton.Materialize(description, arena);
        check(AnimationRuntime.SkeletonFingerprint(materialized) == description.Fingerprint,
            "owned destination skeletons preserve the captured native fingerprint");
        predictive->NumBones = 1;
        predictive->NumFloatSlots = 2;
        reject(() => AnimationNative.Sampler.Validate(skeleton, binding), "predictive reference float counts must match before sampling");
        predictive->NumFloatSlots = 1;
        predictive->NumFrames = 0;
        reject(() => AnimationNative.SourceFrameCount(&predictive->Animation), "predictive tracks without frames cannot be baked");
        predictive->NumFrames = 216002;
        reject(() => AnimationNative.SourceFrameCount(&predictive->Animation), "predictive frame counts respect the bake limit");

        var quantized = arena.Alloc<AnimationNative.Quantized>();
        quantized->Animation.Type = hkaAnimation.AnimationType.QuantizedCompressedAnimation;
        quantized->Animation.Duration = 1;
        quantized->Animation.NumberOfTransformTracks = 1;
        quantized->Animation.NumberOfFloatTracks = 1;
        const int quantizedFrames = 31;
        const int quantizedFrameSize = 4;
        quantized->Data = arena.Array<byte>(sizeof(AnimationNative.QuantizedHeader) + quantizedFrames * quantizedFrameSize);
        var quantizedHeader = (AnimationNative.QuantizedHeader*)quantized->Data.Data;
        quantizedHeader->HeaderSize = (ushort)sizeof(AnimationNative.QuantizedHeader);
        quantizedHeader->NumBones = quantizedHeader->NumFloats = 1;
        quantizedHeader->NumFrames = quantizedFrames;
        quantizedHeader->Duration = 1;
        quantizedHeader->FrameSize = quantizedFrameSize;
        binding->Animation.ptr = &quantized->Animation;
        original = AnimationNative.Fingerprint(binding);
        check(original.Length == 64 && AnimationRuntime.CaptureBinding(0, binding, "example.sklb", description) != null &&
              AnimationNative.SourceFrameCount(&quantized->Animation) == quantizedFrames &&
              AnimationSkeleton.Channels(binding).ReferenceBones == 1 && AnimationSkeleton.Channels(binding).ReferenceFloats == 1,
            "quantized animations remain visible to the listener and expose the skeleton channels needed for repair");
        quantized->Skeleton = skeleton;
        quantized->Data.CapacityAndFlags = 4096;
        check(AnimationNative.Fingerprint(binding) == original,
            "quantized identity ignores its runtime skeleton pointer and array capacity");
        quantized->Data[sizeof(AnimationNative.QuantizedHeader)]++;
        check(AnimationNative.Fingerprint(binding) != original, "quantized identity detects compressed motion changes");
        quantized->Data[sizeof(AnimationNative.QuantizedHeader)]--;
        AnimationNative.Sampler.Validate(skeleton, binding);
        quantizedHeader->NumBones = 2;
        reject(() => AnimationNative.Sampler.Validate(skeleton, binding), "quantized reference bone counts must match before sampling");
        quantizedHeader->NumBones = 1;
        quantizedHeader->HeaderSize = 0;
        reject(() => AnimationNative.Fingerprint(binding), "invalid quantized headers are rejected before matching");
        quantizedHeader->HeaderSize = (ushort)sizeof(AnimationNative.QuantizedHeader);
        quantizedHeader->NumFrames = 1;
        reject(() => AnimationNative.SourceFrameCount(&quantized->Animation), "quantized tracks require enough frames for interpolation");

        binding->Animation.ptr = &predictive->Animation;
        predictive->Animation.Type = hkaAnimation.AnimationType.UnknownAnimation;
        reject(() => AnimationNative.Fingerprint(binding), "unknown encodings remain rejected rather than interpreted as predictive data");
    }
}
