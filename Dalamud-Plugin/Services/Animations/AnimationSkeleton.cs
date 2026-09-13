using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.String;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed record AnimationChannels(string OriginalSkeleton, ImmutableArray<short> Bones,
    ImmutableArray<short> Floats, ImmutableArray<short> Partitions, int? ReferenceBones, int? ReferenceFloats);

/// <summary>Owned managed descriptions are the only skeleton data allowed across framework ticks.</summary>
internal static unsafe class AnimationSkeleton
{
    internal sealed record Variant(string Name, SkeletonDescription Skeleton);

    // hkaSkeletonMapper begins with hkReferencedObject (0x10), followed by
    // hkaSkeletonMapperData's two hkRefPtr<hkaSkeleton> fields. We only read
    // these references; all transforms and ownership remain in the loaded graph.
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    internal struct MapperSkeletons
    {
        [FieldOffset(0x10)] public hkaSkeleton* A;
        [FieldOffset(0x18)] public hkaSkeleton* B;
    }

    internal static ImmutableArray<Variant> DescribeSources(hkRootLevelContainer* root, hkaAnimationContainer* container)
    {
        if (root == null || container == null) throw new InvalidDataException("Missing skeleton container.");
        AnimationNative.ValidateArray(container->Skeletons, 16, "skeletons");
        AnimationNative.ValidateArray(root->NamedVariants, 256, "named variants");
        var result = ImmutableArray.CreateBuilder<Variant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(hkaSkeleton* skeleton, string name)
        {
            if (skeleton == null) return;
            try
            {
                var description = Describe(skeleton);
                if (seen.Add(description.Fingerprint)) result.Add(new(name, description));
            }
            catch (InvalidDataException) { } // A bad embedded variant must not hide valid sources.
        }
        for (var i = 0; i < container->Skeletons.Length; i++)
            Add(container->Skeletons[i].ptr, i == 0 ? "" : $"Skeleton {i + 1}");
        for (var i = 0; i < root->NamedVariants.Length; i++)
        {
            var variant = root->NamedVariants[i];
            if (variant.ClassName.String != "hkaSkeletonMapper" || variant.Variant.ptr == null) continue;
            var mapper = (MapperSkeletons*)variant.Variant.ptr;
            Add(mapper->A, $"Mapper {variant.Name.String} A (entry {i + 1})");
            Add(mapper->B, $"Mapper {variant.Name.String} B (entry {i + 1})");
        }
        if (result.Count == 0) throw new InvalidDataException("No valid source skeleton in resource.");
        return result.ToImmutable();
    }

    public static ImmutableArray<Variant> InspectSources(byte[] bytes)
    {
        using var doc = new AnimationNative.Document(AnimationPap.SkeletonHavok(bytes));
        return DescribeSources(doc.Root, doc.Container);
    }

    internal static SkeletonDescription SelectSource(ImmutableArray<Variant> variants, string fingerprint)
        => variants.FirstOrDefault(v => v.Skeleton.Fingerprint == fingerprint)?.Skeleton
            ?? throw new InvalidDataException("The selected source skeleton is no longer present in its SKLB. Rescan skeletons.");

    public static BoneTransform Transform(hkQsTransformf t) => new(AnimationNative.Translation(t), AnimationNative.Rotation(t), AnimationNative.Scale(t));
    public static hkQsTransformf Transform(BoneTransform t)
    {
        hkQsTransformf value = default;
        value.Translation.X = t.Position.X; value.Translation.Y = t.Position.Y; value.Translation.Z = t.Position.Z;
        value.Rotation.X = t.Rotation.X; value.Rotation.Y = t.Rotation.Y; value.Rotation.Z = t.Rotation.Z; value.Rotation.W = t.Rotation.W;
        value.Scale.X = t.Scale.X; value.Scale.Y = t.Scale.Y; value.Scale.Z = t.Scale.Z;
        return value;
    }
    public static SkeletonDescription Describe(hkaSkeleton* s)
    {
        var fingerprint = AnimationRuntime.SkeletonFingerprint(s);
        var bones = ImmutableArray.CreateBuilder<SkeletonBone>();
        for (var i = 0; i < s->Bones.Length; i++)
            bones.Add(new(s->Bones[i].Name.String ?? "", s->ParentIndices[i], s->Bones[i].LockTranslation, Transform(s->ReferencePose[i])));
        var floats = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < s->FloatSlots.Length; i++) floats.Add(s->FloatSlots[i].String ?? "");
        var partitions = ImmutableArray.CreateBuilder<SkeletonPartition>();
        for (var i = 0; i < s->Partitions.Length; i++)
            partitions.Add(new(s->Partitions[i].Name.String ?? "", s->Partitions[i].StartBoneIndex, s->Partitions[i].NumBones));
        var result = new SkeletonDescription(s->Name.String ?? "", fingerprint, bones.ToImmutable(), floats.ToImmutable(),
            new ReadOnlySpan<float>(s->ReferenceFloats.Data, s->ReferenceFloats.Length).ToArray().ToImmutableArray(), partitions.ToImmutable());
        Validate(result);
        return result;
    }
    public static void Validate(SkeletonDescription s)
    {
        if (s.Bones.IsDefault || s.FloatNames.IsDefault || s.ReferenceFloats.IsDefault || s.Partitions.IsDefault ||
            s.Bones.Length is < 1 or > 4096 || s.FloatNames.Length > 4096 || s.FloatNames.Length != s.ReferenceFloats.Length || s.Partitions.Length > 4096)
            throw new InvalidDataException("Invalid skeleton channel counts.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < s.Bones.Length; i++)
        {
            var b = s.Bones[i];
            if (b == null || b.Reference == null || string.IsNullOrEmpty(b.Name) || !names.Add(b.Name) || b.Parent < -1 || b.Parent >= i)
                throw new InvalidDataException("Skeleton bone names or hierarchy are ambiguous.");
            AnimationNative.CheckTransform(Transform(b.Reference));
        }
        if (s.FloatNames.Any(n => n == null) || s.ReferenceFloats.Any(f => !float.IsFinite(f)) ||
            s.Partitions.Any(p => p == null || p.Name == null || p.Start < 0 || p.Count < 0 || p.Start + p.Count > s.Bones.Length))
            throw new InvalidDataException("Invalid skeleton floats or partition ranges.");
    }
    public static hkaSkeleton* Materialize(SkeletonDescription s, AnimationNative.Arena arena)
    {
        Validate(s);
        var native = arena.Alloc<hkaSkeleton>();
        native->Name = String(s.Name, arena);
        native->Bones = arena.Array<hkaBone>(s.Bones.Length);
        native->ParentIndices = arena.Array<short>(s.Bones.Length);
        native->ReferencePose = arena.Array<hkQsTransformf>(s.Bones.Length);
        for (var i = 0; i < s.Bones.Length; i++)
        {
            native->Bones.Data[i].Name = String(s.Bones[i].Name, arena); native->Bones.Data[i].LockTranslation = s.Bones[i].LockTranslation;
            native->ParentIndices[i] = s.Bones[i].Parent; native->ReferencePose[i] = Transform(s.Bones[i].Reference);
        }
        native->FloatSlots = arena.Array<hkStringPtr>(s.FloatNames.Length);
        native->ReferenceFloats = arena.Copy<float>(s.ReferenceFloats.AsSpan());
        for (var i = 0; i < s.FloatNames.Length; i++) native->FloatSlots[i] = String(s.FloatNames[i], arena);
        native->Partitions = arena.Array<hkaSkeleton.Partition>(s.Partitions.Length);
        for (var i = 0; i < s.Partitions.Length; i++)
        {
            native->Partitions.Data[i].Name = String(s.Partitions[i].Name, arena);
            native->Partitions.Data[i].StartBoneIndex = s.Partitions[i].Start; native->Partitions.Data[i].NumBones = s.Partitions[i].Count;
        }
        return native;
    }
    public static hkStringPtr String(string text, AnimationNative.Arena arena) => new() { StringAndFlag = arena.Copy<byte>(Encoding.UTF8.GetBytes(text + "\0")).Data };
    public static SkeletonDescription Inspect(byte[] bytes)
    {
        using var doc = new AnimationNative.Document(AnimationPap.SkeletonHavok(bytes));
        if (doc.Container->Skeletons.Length != 1) throw new InvalidDataException("No unique skeleton in resource.");
        return Describe(doc.Container->Skeletons[0].ptr);
    }
    public static AnimationChannels Channels(hkaAnimationBinding* b)
    {
        _ = AnimationNative.Fingerprint(b); // Checks all buffers before copying them.
        var a = b->Animation.ptr;
        if (b->TransformTrackToBoneIndices.Length != a->NumberOfTransformTracks || b->FloatTrackToFloatSlotIndices.Length != a->NumberOfFloatTracks)
            throw new InvalidDataException("Implicit or mismatched animation bindings are unsupported.");
        ImmutableArray<short> Copy(short* values, int count) => new ReadOnlySpan<short>(values, count).ToArray().ToImmutableArray();
        var predictive = a->Type == hkaAnimation.AnimationType.PredictiveCompressedAnimation ? (AnimationNative.Predictive*)a : null;
        return new(b->OriginalSkeletonName.String ?? "", Copy(b->TransformTrackToBoneIndices.Data, b->TransformTrackToBoneIndices.Length),
            Copy(b->FloatTrackToFloatSlotIndices.Data, b->FloatTrackToFloatSlotIndices.Length), Copy(b->PartitionIndices.Data, b->PartitionIndices.Length),
            predictive == null ? null : predictive->NumBones, predictive == null ? null : predictive->NumFloatSlots);
    }
    public static AnimationChannels InspectChannels(byte[] pap, int index)
    {
        using var doc = new AnimationNative.Document(new AnimationPap(pap).Havok);
        if (index < 0 || index >= doc.Container->Bindings.Length) throw new InvalidDataException("Missing animation binding.");
        return Channels(doc.Container->Bindings[index].ptr);
    }
    public static bool Fits(AnimationChannels a, SkeletonDescription s)
    {
        return BindingsFit(a, s) &&
            (a.ReferenceBones == null || a.ReferenceBones == s.Bones.Length) && (a.ReferenceFloats == null || a.ReferenceFloats == s.FloatNames.Length);
    }

    /// <summary>
    /// Validate binding indices independently of predictive reference-channel counts.
    /// </summary>
    private static bool BindingsFit(AnimationChannels a, SkeletonDescription s)
    {
        Validate(s);
        bool Indices(ImmutableArray<short> indices, int count) => indices.All(i => i >= 0 && i < count) && indices.Distinct().Count() == indices.Length;
        return Indices(a.Bones, s.Bones.Length) && Indices(a.Floats, s.FloatNames.Length) && Indices(a.Partitions, s.Partitions.Length);
    }
}
