// Havok load/save and animation construction adapted from VFXEditor (GPL-3.0).
// IK setup/solver calls adapted from Caraxi/LivePose (GPL-3.0). See THIRD-PARTY-NOTICES.md.
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal unsafe sealed class AnimationNative
{
    private readonly nint interleavedVtbl;
    private readonly delegate* unmanaged<Spline*, Interleaved*, Spline*> compress;
    private readonly delegate* unmanaged<void*, int, float, void> ccdCtor;
    private readonly delegate* unmanaged<void*, byte*, hkArray<CcdConstraint>*, hkaPose*, byte*> ccdSolve;
    private readonly delegate* unmanaged<byte*, TwoJointSetup*, hkaPose*, byte*> twoJointSolve;
    public string? UnavailableReason { get; }

    public AnimationNative(ISigScanner scanner)
    {
        try
        {
            var relative = scanner.ScanText("48 89 07 48 8B CD 48 89 77 38") - 4;
            interleavedVtbl = relative + 4 + Marshal.ReadInt32(relative);
            compress = (delegate* unmanaged<Spline*, Interleaved*, Spline*>)scanner.ScanText(
                "48 89 5C 24 ?? 57 48 83 EC 40 48 8B DA 48 8B F9 E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ??");
            ccdCtor = (delegate* unmanaged<void*, int, float, void>)scanner.ScanText("E8 ?? ?? ?? ?? 48 8D 43 ?? 48 C7 43");
            ccdSolve = (delegate* unmanaged<void*, byte*, hkArray<CcdConstraint>*, hkaPose*, byte*>)scanner.ScanText(
                "E8 ?? ?? ?? ?? 8B 44 24 ?? 48 8B 5C 24 ?? 48 3B 5C 24");
            twoJointSolve = (delegate* unmanaged<byte*, TwoJointSetup*, hkaPose*, byte*>)scanner.ScanText("E8 ?? ?? ?? ?? 0F 28 55 ?? 41 0F 28 D8");
            if (interleavedVtbl == 0 || compress == null || ccdCtor == null || ccdSolve == null || twoJointSolve == null)
                throw new InvalidOperationException("An animation runtime signature was not resolved.");
        }
        catch (Exception e) { UnavailableReason = "This game build is not compatible with animation baking: " + e.Message; }
    }

    public void EnsureAvailable()
    {
        if (UnavailableReason != null) throw new InvalidOperationException(UnavailableReason);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Interleaved { public hkaAnimation Animation; public hkArray<hkQsTransformf> Transforms; public hkArray<float> Floats; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Spline
    {
        public hkaAnimation Animation;
        public int NumFrames, NumBlocks, MaxFramesPerBlock, MaskAndQuantizationSize;
        public float BlockDuration, BlockInverseDuration, FrameDuration;
        public uint Padding;
        public hkArray<uint> BlockOffsets, FloatBlockOffsets, TransformOffsets, FloatOffsets;
        public hkArray<byte> Data;
        public int Endian;
        public uint Padding2;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct CcdConstraint
    {
        [FieldOffset(0)] public short Start;
        [FieldOffset(2)] public short End;
        [FieldOffset(0x10)] public Vector4 Target;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x90)]
    private struct TwoJointSetup
    {
        [FieldOffset(0)] public short First;
        [FieldOffset(2)] public short Second;
        [FieldOffset(4)] public short End;
        [FieldOffset(6)] public short FirstTwist;
        [FieldOffset(8)] public short SecondTwist;
        [FieldOffset(0x10)] public Vector4 Axis;
        [FieldOffset(0x20)] public float MaxAngle;
        [FieldOffset(0x24)] public float MinAngle;
        [FieldOffset(0x28)] public float FirstGain;
        [FieldOffset(0x2c)] public float SecondGain;
        [FieldOffset(0x30)] public float EndGain;
        [FieldOffset(0x40)] public Vector4 Target;
        [FieldOffset(0x50)] public Quaternion TargetRotation;
        [FieldOffset(0x60)] public Vector4 Offset;
        [FieldOffset(0x70)] public Quaternion OffsetRotation;
        [FieldOffset(0x80)] public byte EnforcePosition;
        [FieldOffset(0x81)] public byte EnforceRotation;
    }

    internal sealed class Arena : IDisposable
    {
        private readonly List<nint> allocations = [];
        public T* Alloc<T>(int count = 1) where T : unmanaged
        {
            if (count < 0 || (long)count * sizeof(T) > AnimationPap.MaxFileSize) throw new InvalidDataException("Native allocation limit exceeded.");
            var size = (nuint)Math.Max(16, checked(count * sizeof(T)));
            size = (size + 15) & ~(nuint)15;
            var ptr = NativeMemory.AlignedAlloc(size, 16);
            if (ptr == null) throw new OutOfMemoryException();
            NativeMemory.Clear(ptr, size);
            allocations.Add((nint)ptr);
            return (T*)ptr;
        }
        public hkArray<T> Array<T>(int count) where T : unmanaged => new()
        { Data = Alloc<T>(count), Length = count, CapacityAndFlags = unchecked((int)0x80000000) | count };
        public hkArray<T> Copy<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var array = Array<T>(values.Length);
            values.CopyTo(new Span<T>(array.Data, array.Length));
            return array;
        }
        public void Dispose() { foreach (var a in allocations) NativeMemory.AlignedFree((void*)a); allocations.Clear(); }
    }

    internal sealed class Document : IDisposable
    {
        private hkResource* resource;
        private GCHandle input;
        public hkRootLevelContainer* Root { get; private set; }
        public hkaAnimationContainer* Container { get; private set; }
        public Document(byte[] bytes)
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            if (registry == null) throw new InvalidOperationException("Havok registry is unavailable.");
            var options = new hkSerializeUtil.LoadOptions { TypeInfoRegistry = registry->GetTypeInfoRegistry(),
                ClassNameRegistry = registry->GetClassNameRegistry() };
            // Retain a stable input buffer for the entire resource lifetime, including loaders
            // that retain references to serialized data rather than copying every section.
            input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                resource = hkSerializeUtil.LoadFromBuffer((byte*)input.AddrOfPinnedObject(), bytes.Length, null, &options);
                if (resource == null) throw new InvalidDataException("Havok could not load the animation resource.");
                Root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer", registry->GetTypeInfoRegistry());
                if (Root == null) throw new InvalidDataException("Missing Havok root container.");
                Container = (hkaAnimationContainer*)Root->findObjectByName("hkaAnimationContainer", null);
                if (Container == null || Container->Bindings.Length is < 0 or > 4096 || Container->Animations.Length is < 0 or > 4096 ||
                    Container->Skeletons.Length is < 0 or > 16)
                    throw new InvalidDataException("Invalid Havok animation container.");
                ValidateArray(Container->Bindings, 4096, "animation bindings");
                ValidateArray(Container->Animations, 4096, "animations");
                ValidateArray(Container->Skeletons, 16, "skeletons");
            }
            catch { Dispose(); throw; }
        }
        public void Save(string path)
            => SaveObject(Root, "hkRootLevelContainer", path);

        public static void SaveObject(void* value, string type, string path)
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            var klass = registry->GetClassNameRegistry()->GetClassByName(type);
            if (klass == null) throw new InvalidOperationException("Havok root type is unavailable.");
            hkOstream stream = default;
            stream.Ctor(path);
            try
            {
                if (stream.StreamWriter.ptr == null) throw new IOException("Could not open staged Havok output.");
                hkResult result = default;
                hkSerializeUtil.Save(&result, value, klass, stream.StreamWriter.ptr, new hkSerializeUtil.SaveOptions());
                if (result.Result != hkResult.hkResultEnum.Success) throw new IOException("Havok serialization failed.");
            }
            finally { stream.Dtor(); }
        }
        public void Dispose()
        {
            // The resource owns the loaded graph. Do not release its container a second time.
            try { if (resource != null) resource->RemoveReference(); }
            finally
            {
                resource = null; Root = null; Container = null;
                if (input.IsAllocated) input.Free();
            }
        }
    }

    public static string Fingerprint(hkaAnimationBinding* binding)
    {
        if (binding == null || binding->Animation.ptr == null) throw new InvalidDataException("Missing animation binding.");
        var a = binding->Animation.ptr;
        ValidateCounts(a);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(a->Duration));
        hash.AppendData(BitConverter.GetBytes((int)a->Type));
        hash.AppendData(BitConverter.GetBytes(a->NumberOfTransformTracks));
        hash.AppendData(BitConverter.GetBytes(a->NumberOfFloatTracks));
        hash.AppendData(Encoding.UTF8.GetBytes(MetadataFingerprint(binding)));
        Append(binding->TransformTrackToBoneIndices);
        Append(binding->FloatTrackToFloatSlotIndices);
        if (a->Type == hkaAnimation.AnimationType.SplineCompressedAnimation)
        {
            var spline = (Spline*)a;
            hash.AppendData(new ReadOnlySpan<byte>(&spline->NumFrames, 28));
            hash.AppendData(BitConverter.GetBytes(spline->Endian));
            Append(spline->BlockOffsets); Append(spline->FloatBlockOffsets); Append(spline->TransformOffsets); Append(spline->FloatOffsets);
            Append(spline->Data);
        }
        else if (a->Type == hkaAnimation.AnimationType.InterleavedAnimation)
        { Append(((Interleaved*)a)->Transforms); Append(((Interleaved*)a)->Floats); }
        else throw new InvalidDataException($"Unsupported animation encoding: {a->Type}.");
        return Convert.ToHexString(hash.GetHashAndReset());
        void Append<T>(hkArray<T> array) where T : unmanaged
        {
            if (array.Length < 0 || (long)array.Length * sizeof(T) > AnimationPap.MaxFileSize || (array.Length > 0 && array.Data == null))
                throw new InvalidDataException("Invalid animation array.");
            hash.AppendData(new ReadOnlySpan<byte>(array.Data, checked(array.Length * sizeof(T))));
        }
    }
    public static string MetadataFingerprint(hkaAnimationBinding* binding)
    {
        if (binding == null || binding->Animation.ptr == null) throw new InvalidDataException("Missing animation binding.");
        ValidateArray(binding->PartitionIndices, 4096, "binding partitions");
        ValidateArray(binding->FloatTrackToFloatSlotIndices, 4096, "float binding indices");
        ValidateArray(binding->Animation.ptr->AnnotationTracks, 4096, "annotation tracks");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(binding->OriginalSkeletonName.String ?? ""); writer.Write(binding->BlendHint.Storage);
        writer.Write(binding->PartitionIndices.Length);
        if (binding->PartitionIndices.Length is < 0 or > 4096) throw new InvalidDataException("Invalid binding partitions.");
        for (var i = 0; i < binding->PartitionIndices.Length; i++) writer.Write(binding->PartitionIndices[i]);
        var animation = binding->Animation.ptr;
        writer.Write(animation->Duration); writer.Write(animation->NumberOfFloatTracks);
        writer.Write(binding->FloatTrackToFloatSlotIndices.Length);
        for (var i = 0; i < binding->FloatTrackToFloatSlotIndices.Length; i++) writer.Write(binding->FloatTrackToFloatSlotIndices[i]);
        if (animation->AnnotationTracks.Length is < 0 or > 4096) throw new InvalidDataException("Invalid annotation tracks.");
        writer.Write(animation->AnnotationTracks.Length);
        for (var i = 0; i < animation->AnnotationTracks.Length; i++)
        {
            var track = animation->AnnotationTracks[i]; writer.Write(track.TrackName.String ?? "");
            ValidateArray(track.Annotations, 100000, "annotations");
            writer.Write(track.Annotations.Length);
            for (var j = 0; j < track.Annotations.Length; j++)
            { writer.Write(track.Annotations[j].Time); writer.Write(track.Annotations[j].Text.String ?? ""); }
        }
        return AnimationPap.Hash(stream.ToArray());
    }
    public static string ExtractedMotionFingerprint(hkaAnimationBinding* binding, string temporaryPath)
    {
        var motion = binding->Animation.ptr->ExtractedMotion.ptr;
        if (motion == null) return "none";
        if (motion->FrameType != FFXIVClientStructs.Havok.Animation.Motion.hkaAnimatedReferenceFrame.hkaReferenceFrameTypeEnum.Default)
            throw new InvalidDataException("The extracted-motion class is unsupported for lossless verification.");
        try
        {
            Document.SaveObject(motion, "hkaDefaultAnimatedReferenceFrame", temporaryPath);
            return AnimationPap.Hash(File.ReadAllBytes(temporaryPath));
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
    private static void ValidateCounts(hkaAnimation* a)
    {
        if (!float.IsFinite(a->Duration) || a->Duration < 0 || a->Duration > 3600 ||
            a->NumberOfTransformTracks is < 0 or > 4096 || a->NumberOfFloatTracks is < 0 or > 4096)
            throw new InvalidDataException("Invalid animation track counts or duration.");
        if (a->Type == hkaAnimation.AnimationType.InterleavedAnimation)
        {
            var value = (Interleaved*)a;
            ValidateArray(value->Transforms, AnimationPap.MaxFileSize / sizeof(hkQsTransformf), "interleaved transforms");
            ValidateArray(value->Floats, AnimationPap.MaxFileSize / sizeof(float), "interleaved floats");
            if (value->Transforms.Length < 0 || value->Floats.Length < 0 ||
                a->NumberOfTransformTracks == 0 && value->Transforms.Length != 0 ||
                a->NumberOfFloatTracks == 0 && value->Floats.Length != 0 ||
                a->NumberOfTransformTracks > 0 && value->Transforms.Length % a->NumberOfTransformTracks != 0 ||
                a->NumberOfFloatTracks > 0 && value->Floats.Length % a->NumberOfFloatTracks != 0 ||
                a->NumberOfTransformTracks > 0 && a->NumberOfFloatTracks > 0 &&
                    value->Transforms.Length / a->NumberOfTransformTracks != value->Floats.Length / a->NumberOfFloatTracks)
                throw new InvalidDataException("Interleaved track frame counts disagree.");
            var frames = Math.Max(value->Transforms.Length / Math.Max(1, a->NumberOfTransformTracks),
                value->Floats.Length / Math.Max(1, a->NumberOfFloatTracks));
            if (frames > 216001 || frames == 0 && (a->NumberOfTransformTracks > 0 || a->NumberOfFloatTracks > 0))
                throw new InvalidDataException("Interleaved tracks have no samples or exceed the sampling limit.");
        }
        else if (a->Type == hkaAnimation.AnimationType.SplineCompressedAnimation)
        {
            var value = (Spline*)a;
            ValidateArray(value->Data, AnimationPap.MaxFileSize, "spline data");
            ValidateArray(value->BlockOffsets, 216001, "spline block offsets");
            ValidateArray(value->FloatBlockOffsets, 216001, "spline float block offsets");
            ValidateArray(value->TransformOffsets, AnimationPap.MaxFileSize / sizeof(uint), "spline transform offsets");
            ValidateArray(value->FloatOffsets, AnimationPap.MaxFileSize / sizeof(uint), "spline float offsets");
            if (value->NumFrames is < 1 or > 216001 || value->NumBlocks is < 1 or > 216001 ||
                value->Data.Length is < 1 or > AnimationPap.MaxFileSize || !float.IsFinite(value->FrameDuration) || value->FrameDuration < 0)
                throw new InvalidDataException("Invalid spline frame/block data.");
        }
        else throw new InvalidDataException($"Unsupported animation encoding: {a->Type}.");
    }

    internal static void ValidateArray<T>(hkArray<T> array, int maximum, string name) where T : unmanaged
    {
        if (array.Length < 0 || array.Length > maximum || array.Length > 0 && array.Data == null)
            throw new InvalidDataException($"Invalid Havok {name} array.");
    }

    internal sealed class Sampler : IDisposable
    {
        private readonly Arena arena = new();
        private hkaAnimatedSkeleton* animated;
        private hkaAnimationControl* control;
        private bool animatedConstructed, controlConstructed, controlAdded;
        public hkaSkeleton* Skeleton { get; }
        public hkaPose* Pose { get; }
        public hkQsTransformf* Transforms { get; }
        public float* Floats { get; }
        public int BoneCount => Skeleton->Bones.Length;
        public Sampler(hkaSkeleton* skeleton, hkaAnimationBinding* binding)
        {
            Skeleton = skeleton;
            Validate(skeleton, binding);
            try
            {
                animated = arena.Alloc<hkaAnimatedSkeleton>();
                animated->Ctor1(skeleton);
                animatedConstructed = true;
                control = arena.Alloc<hkaAnimationControl>();
                control->Ctor1(binding);
                controlConstructed = true;
                control->Weight = 1;
                animated->addAnimationControl(control);
                controlAdded = true;
                Transforms = arena.Alloc<hkQsTransformf>(BoneCount);
                Floats = arena.Alloc<float>(skeleton->FloatSlots.Length);
                // Supply all pose buffers ourselves. SetPoseLocalSpace initializes Havok's dirty flags.
                Pose = arena.Alloc<hkaPose>();
                Pose->Skeleton = skeleton;
                Pose->LocalPose = arena.Array<hkQsTransformf>(BoneCount);
                Pose->ModelPose = arena.Array<hkQsTransformf>(BoneCount);
                Pose->BoneFlags = arena.Array<uint>(BoneCount);
                Pose->FloatSlotValues = arena.Array<float>(skeleton->FloatSlots.Length);
            }
            catch { Dispose(); throw; }
        }
        public void Sample(float time)
        {
            control->LocalTime = time;
            animated->sampleAndCombineAnimations(Transforms, Floats);
            var values = new hkArray<hkQsTransformf> { Data = Transforms, Length = BoneCount,
                CapacityAndFlags = unchecked((int)0x80000000) | BoneCount };
            Pose->SetPoseLocalSpace(&values);
        }
        public static void Validate(hkaSkeleton* s, hkaAnimationBinding* b)
        {
            if (s == null || b == null || b->Animation.ptr == null || s->Bones.Length is < 1 or > 4096 ||
                s->FloatSlots.Length is < 0 or > 4096 || s->ReferenceFloats.Length != s->FloatSlots.Length ||
                s->Partitions.Length is < 0 or > 4096 || b->PartitionIndices.Length is < 0 or > 4096 ||
                s->ParentIndices.Length != s->Bones.Length || s->ReferencePose.Length != s->Bones.Length)
                throw new InvalidDataException("Invalid animation skeleton.");
            ValidateCounts(b->Animation.ptr);
            ValidateArray(s->Bones, 4096, "bones");
            ValidateArray(s->ParentIndices, 4096, "parents");
            ValidateArray(s->ReferencePose, 4096, "reference transforms");
            ValidateArray(s->FloatSlots, 4096, "float slots");
            ValidateArray(s->ReferenceFloats, 4096, "reference floats");
            ValidateArray(s->Partitions, 4096, "skeleton partitions");
            ValidateArray(b->PartitionIndices, 4096, "binding partitions");
            ValidateArray(b->TransformTrackToBoneIndices, 4096, "transform bindings");
            ValidateArray(b->FloatTrackToFloatSlotIndices, 4096, "float bindings");
            if (b->TransformTrackToBoneIndices.Length != b->Animation.ptr->NumberOfTransformTracks ||
                b->FloatTrackToFloatSlotIndices.Length != b->Animation.ptr->NumberOfFloatTracks)
                throw new InvalidDataException("Implicit or mismatched animation bindings are unsupported.");
            var names = new HashSet<string>();
            for (var i = 0; i < s->Bones.Length; i++)
            {
                if (s->ParentIndices[i] < -1 || s->ParentIndices[i] >= i || string.IsNullOrEmpty(s->Bones[i].Name.String) || !names.Add(s->Bones[i].Name.String!))
                    throw new InvalidDataException("Skeleton hierarchy or bone names are ambiguous.");
                CheckTransform(s->ReferencePose[i]);
            }
            var boundBones = new HashSet<short>();
            for (var i = 0; i < b->TransformTrackToBoneIndices.Length; i++)
                if (b->TransformTrackToBoneIndices[i] < 0 || b->TransformTrackToBoneIndices[i] >= s->Bones.Length || !boundBones.Add(b->TransformTrackToBoneIndices[i]))
                    throw new InvalidDataException("Animation track does not fit the resolved skeleton.");
            var boundFloats = new HashSet<short>();
            for (var i = 0; i < b->FloatTrackToFloatSlotIndices.Length; i++)
                if (b->FloatTrackToFloatSlotIndices[i] < 0 || b->FloatTrackToFloatSlotIndices[i] >= s->FloatSlots.Length ||
                    !boundFloats.Add(b->FloatTrackToFloatSlotIndices[i]))
                    throw new InvalidDataException("Animation float track does not fit the resolved skeleton.");
            for (var i = 0; i < b->PartitionIndices.Length; i++)
                if (b->PartitionIndices[i] < 0 || b->PartitionIndices[i] >= s->Partitions.Length)
                    throw new InvalidDataException("Animation partition does not fit the resolved skeleton.");
            for (var i = 0; i < s->Partitions.Length; i++)
                if (s->Partitions[i].StartBoneIndex < 0 || s->Partitions[i].NumBones < 0 ||
                    s->Partitions[i].StartBoneIndex + s->Partitions[i].NumBones > s->Bones.Length)
                    throw new InvalidDataException("Invalid skeleton partition range.");
        }
        public void Dispose()
        {
            if (controlAdded) animated->removeAnimationControl(control);
            if (animatedConstructed) animated->Dtor();
            if (controlConstructed) control->VirtDtor(0);
            animatedConstructed = controlConstructed = controlAdded = false;
            animated = null; control = null;
            arena.Dispose();
        }
    }

    public void Apply(Sampler sampler, ImmutableArray<PoseBone> bones, int partial, PoseComponents components,
        ImmutableHashSet<PoseBoneId> selected)
    {
        var byName = bones.Where(b => b.Id.Slot == 0 && b.Id.Partial == partial && selected.Contains(b.Id))
            .ToDictionary(b => b.Id.Name, StringComparer.Ordinal);
        for (var i = 0; i < sampler.BoneCount; i++)
        {
            if (!byName.Remove(sampler.Skeleton->Bones[i].Name.String!, out var bone)) continue;
            foreach (var original in bone.Stacks)
            {
                var s = AnimationPoseRules.Filter(original, components);
                AnimationPoseRules.Validate(s);
                var pose = sampler.Pose;
                var model = pose->AccessBoneModelSpace(i, Prop(s.Propagate, PoseComponents.Position));
                var position = Translation(*model) + s.Position;
                if (s.Ik.Enabled) SolveIk(pose, i, s.Ik, position, bone.IkChain);
                if (!s.Ik.Enabled || !s.Ik.EnforceConstraints)
                {
                    model = pose->AccessBoneModelSpace(i, Prop(s.Propagate, PoseComponents.Position));
                    model->Translation.X = position.X; model->Translation.Y = position.Y; model->Translation.Z = position.Z;
                }
                model = pose->AccessBoneModelSpace(i, Prop(s.Propagate, PoseComponents.Rotation));
                SetRotation(model, Quaternion.Normalize(Rotation(*model) * s.Rotation));
                model = pose->AccessBoneModelSpace(i, Prop(s.Propagate, PoseComponents.Scale));
                model->Scale.X += s.Scale.X; model->Scale.Y += s.Scale.Y; model->Scale.Z += s.Scale.Z;
            }
        }
        if (byName.Count != 0) throw new InvalidDataException("Selected pose bones are missing from the animation skeleton: " + string.Join(", ", byName.Keys));
        sampler.Pose->SyncLocalSpace();
    }
    private static hkaPose.PropagateOrNot Prop(PoseComponents flags, PoseComponents component) =>
        flags.HasFlag(component) ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate;
    private void SolveIk(hkaPose* pose, int bone, PoseIk ik, Vector3 target, ImmutableArray<string> capturedChain)
    {
        using var scratch = new Arena();
        var chain = new List<short>();
        if (capturedChain.IsDefaultOrEmpty) throw new InvalidDataException("The captured IK hierarchy is missing.");
        for (var cursor = bone; cursor >= 0 && chain.Count < capturedChain.Length; cursor = pose->Skeleton->ParentIndices[cursor])
        {
            if (pose->Skeleton->Bones[cursor].Name.String != capturedChain[chain.Count])
                throw new InvalidDataException("The captured IK hierarchy does not match the isolated skeleton.");
            chain.Add((short)cursor);
        }
        if (chain.Count != capturedChain.Length) throw new InvalidDataException("The captured IK chain crosses skeletons.");
        byte result = 0;
        if (ik.Type == 0)
        {
            if (ik.Depth <= 0 || chain.Count <= 1) return;
            var solver = scratch.Alloc<byte>(0x20);
            ccdCtor(solver, ik.Iterations, 1f);
            var constraints = scratch.Array<CcdConstraint>(1);
            constraints.Data->Start = chain[Math.Min(ik.Depth, chain.Count - 1)];
            constraints.Data->End = chain[0]; constraints.Data->Target = new Vector4(target, 0);
            ccdSolve(solver, &result, &constraints, pose);
        }
        else
        {
            if (ik.First >= chain.Count || ik.Second >= chain.Count || ik.End >= chain.Count ||
                ik.First <= ik.Second || ik.Second <= ik.End || ik.Axis.LengthSquared() < 1e-12f)
                throw new InvalidDataException("Two-joint IK chain is not valid for this skeleton.");
            var setup = scratch.Alloc<TwoJointSetup>();
            *setup = new TwoJointSetup { First = chain[ik.First], Second = chain[ik.Second], End = chain[ik.End],
                FirstTwist = -1, SecondTwist = -1, Axis = new Vector4(ik.Axis, 0), MaxAngle = -1, MinAngle = 1,
                FirstGain = 1, SecondGain = 1, EndGain = 1, Target = new Vector4(target, 0),
                TargetRotation = Quaternion.Identity, OffsetRotation = Quaternion.Identity, EnforcePosition = 1 };
            twoJointSolve(&result, setup, pose);
        }
    }
    public static Vector3 Translation(hkQsTransformf t) => new(t.Translation.X, t.Translation.Y, t.Translation.Z);
    public static Quaternion Rotation(hkQsTransformf t) => new(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
    public static Vector3 Scale(hkQsTransformf t) => new(t.Scale.X, t.Scale.Y, t.Scale.Z);
    public static void SetRotation(hkQsTransformf* t, Quaternion q)
    { t->Rotation.X = q.X; t->Rotation.Y = q.Y; t->Rotation.Z = q.Z; t->Rotation.W = q.W; }
    public static void CheckTransform(hkQsTransformf t)
    {
        if (!float.IsFinite(Translation(t).LengthSquared()) || !float.IsFinite(Scale(t).LengthSquared()) ||
            !float.IsFinite(Rotation(t).LengthSquared()) || Math.Abs(Rotation(t).LengthSquared() - 1) > 0.01f)
            throw new InvalidDataException("The baked animation contains invalid transforms.");
    }
    public static bool Near(hkQsTransformf a, hkQsTransformf b, float tolerance = 0.0002f) =>
        Vector3.Distance(Translation(a), Translation(b)) <= tolerance && Vector3.Distance(Scale(a), Scale(b)) <= tolerance &&
        1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(Rotation(a)), Quaternion.Normalize(Rotation(b)))) <= tolerance;

    public hkaAnimation* Compress(Arena arena, Interleaved* animation)
    {
        EnsureAvailable();
        var spline = arena.Alloc<Spline>();
        if (compress(spline, animation) != spline || *(nint*)spline == 0)
            throw new InvalidDataException("The native spline compressor did not construct an animation.");
        return (hkaAnimation*)spline;
    }
    public void Initialize(Interleaved* animation, hkaAnimation* original)
    {
        animation->Animation = *original;
        *(nint*)animation = interleavedVtbl;
        // Keep a reference owned by the arena so native consumers cannot delete arena allocations.
        animation->Animation.MemSizeAndRefCount = 0x00010000;
        animation->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
    }
}
