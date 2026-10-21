using System.Collections.Immutable;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// Writes a custom hair skeleton's <c>.sklb</c> from a vanilla hair skeleton of the same race: the
/// template's header bytes, then its Havok data with the main skeleton's bones replaced. Havok runs
/// only on the framework thread.
/// </summary>
internal static unsafe class HairSkeletonWriter
{
    private const float Tolerance = 1e-5f;

    /// <summary>The template's bone 0, which must be <c>j_kao</c> with no parent.</summary>
    public static SkeletonBone TemplateKao(byte[] templateSklb)
    {
        var kao = AnimationSkeleton.Inspect(templateSklb).Bones[0];
        return kao is { Name: HairSkeletonRequest.Root, Parent: -1 } ? kao
            : throw new InvalidDataException($"The hair skeleton template starts with {kao.Name} (parent {kao.Parent}), not {HairSkeletonRequest.Root}.");
    }

    /// <summary>
    /// The template with <paramref name="bones"/> as its main skeleton's bones, parents and reference
    /// pose. A template with partitions gets one, named as its first, over all bones; float slots stay
    /// as the template has them. The result is read back and must match <paramref name="bones"/>.
    /// </summary>
    public static byte[] Write(byte[] templateSklb, ImmutableArray<SkeletonBone> bones)
    {
        if (bones.IsDefaultOrEmpty) throw new ArgumentException("A hair skeleton needs bones.", nameof(bones));
        var havok = AnimationPap.SkeletonHavok(templateSklb);
        // SkeletonHavok returns everything from the Havok offset on, so the rest is the header.
        var header = templateSklb.AsSpan(0, templateSklb.Length - havok.Length);
        var temp = Path.Combine(Path.GetTempPath(), $"hair_skeleton_{Guid.NewGuid():N}.hkx");
        byte[] result;
        try
        {
            Save(havok, bones, temp);
            result = [.. header, .. File.ReadAllBytes(temp)];
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        ReadBack(result, bones);
        return result;
    }

    // Swaps the loaded skeleton's per-bone arrays for the new ones, saves the whole root container, then
    // puts the originals back so the resource frees only what it allocated.
    private static void Save(byte[] havok, ImmutableArray<SkeletonBone> bones, string path)
    {
        using var doc = new AnimationNative.Document(havok);
        var template = AnimationSkeleton.DescribeMain(doc.Container);
        RequireNothingElseNamesBones(doc);
        ImmutableArray<SkeletonPartition> partitions = template.Partitions.IsEmpty ? []
            : [new SkeletonPartition(template.Partitions[0].Name, 0, (short)bones.Length)];
        AnimationSkeleton.Validate(template with { Bones = bones, Partitions = partitions });

        using var arena = new AnimationNative.Arena();
        var skeleton = doc.Container->Skeletons[0].ptr;
        var (oldBones, oldParents, oldPose) = (skeleton->Bones, skeleton->ParentIndices, skeleton->ReferencePose);
        var (oldPartitions, oldFrames) = (skeleton->Partitions, skeleton->LocalFrames);
        try
        {
            skeleton->Bones = arena.Array<hkaBone>(bones.Length);
            skeleton->ParentIndices = arena.Array<short>(bones.Length);
            skeleton->ReferencePose = arena.Array<hkQsTransformf>(bones.Length);
            for (var i = 0; i < bones.Length; i++)
            {
                skeleton->Bones.Data[i].Name = AnimationSkeleton.String(bones[i].Name, arena);
                skeleton->Bones.Data[i].LockTranslation = bones[i].LockTranslation;
                skeleton->ParentIndices[i] = bones[i].Parent;
                skeleton->ReferencePose[i] = AnimationSkeleton.Transform(bones[i].Reference);
            }
            skeleton->Partitions = arena.Array<hkaSkeleton.Partition>(partitions.Length);
            for (var i = 0; i < partitions.Length; i++)
            {
                skeleton->Partitions.Data[i].Name = AnimationSkeleton.String(partitions[i].Name, arena);
                skeleton->Partitions.Data[i].StartBoneIndex = partitions[i].Start;
                skeleton->Partitions.Data[i].NumBones = partitions[i].Count;
            }
            // Local frames hang on the template's own hair bones, which are gone.
            skeleton->LocalFrames = arena.Array<hkaSkeleton.LocalFrameOnBone>(0);
            AnimationNative.Document.SaveObject(doc.Root, "hkRootLevelContainer", path);
        }
        finally
        {
            (skeleton->Bones, skeleton->ParentIndices, skeleton->ReferencePose) = (oldBones, oldParents, oldPose);
            (skeleton->Partitions, skeleton->LocalFrames) = (oldPartitions, oldFrames);
        }
    }

    // Attachments, skins and skeleton mappers name bones by index, which would point at the wrong bones.
    private static void RequireNothingElseNamesBones(AnimationNative.Document doc)
    {
        if (doc.Container->Attachments.Length != 0 || doc.Container->Skins.Length != 0)
            throw new InvalidDataException("The hair skeleton template has attachments or skins.");
        for (var i = 0; i < doc.Root->NamedVariants.Length; i++)
            if (doc.Root->NamedVariants[i].ClassName.String is var type && type != "hkaAnimationContainer")
                throw new InvalidDataException($"The hair skeleton template holds a {type}.");
    }

    private static void ReadBack(byte[] sklb, ImmutableArray<SkeletonBone> bones)
    {
        var written = AnimationSkeleton.Inspect(sklb).Bones;
        if (written.Length != bones.Length)
            throw new InvalidDataException($"The written hair skeleton has {written.Length} bones, not {bones.Length}.");
        for (var i = 0; i < bones.Length; i++)
        {
            var (a, b) = (written[i], bones[i]);
            if (a.Name != b.Name || a.Parent != b.Parent ||
                !Near(new Vector4(a.Reference.Position, 0), new Vector4(b.Reference.Position, 0)) ||
                !Near(new Vector4(a.Reference.Rotation.X, a.Reference.Rotation.Y, a.Reference.Rotation.Z, a.Reference.Rotation.W),
                    new Vector4(b.Reference.Rotation.X, b.Reference.Rotation.Y, b.Reference.Rotation.Z, b.Reference.Rotation.W)) ||
                !Near(new Vector4(a.Reference.Scale, 0), new Vector4(b.Reference.Scale, 0)))
                throw new InvalidDataException($"The written hair skeleton's bone {i} ({a.Name}) doesn't match {b.Name}.");
        }
    }

    private static bool Near(Vector4 a, Vector4 b)
    {
        var d = Vector4.Abs(a - b);
        return MathF.Max(MathF.Max(d.X, d.Y), MathF.Max(d.Z, d.W)) <= Tolerance;
    }
}
