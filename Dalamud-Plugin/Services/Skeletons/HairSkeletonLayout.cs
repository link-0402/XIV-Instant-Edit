using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// The bones of a custom hair skeleton: the race's <c>j_kao</c>, then the rig bones with reference
/// transforms relative to their parents. Hair models can't be race-exported, so every race keeps the
/// rig's model-space rest unchanged and only the reference relative to <c>j_kao</c> differs.
/// </summary>
internal static class HairSkeletonLayout
{
    /// <summary>
    /// Index 0 is <paramref name="templateKao"/> as given (the race's <c>j_kao</c>, parent -1), then the
    /// rig bones in request order, each relative to its parent's model-space rest, with unit scale.
    /// Expects a request that passed <see cref="HairSkeletonRequest.Validate"/>.
    /// </summary>
    public static ImmutableArray<SkeletonBone> Bones(HairSkeletonRequest rig, SkeletonBone templateKao)
    {
        var rigBones = rig.Bones ?? throw new ArgumentException("A hair skeleton needs bones.", nameof(rig));
        var bones = ImmutableArray.CreateBuilder<SkeletonBone>(rigBones.Count + 1);
        bones.Add(templateKao);
        var index = new Dictionary<string, short>(StringComparer.Ordinal) { [HairSkeletonRequest.Root] = 0 };
        var models = new List<(Vector3 Position, Quaternion Rotation)>
        {
            (templateKao.Reference.Position, Quaternion.Normalize(templateKao.Reference.Rotation)),
        };
        foreach (var bone in rigBones)
        {
            var parent = index[bone.Parent!];
            var model = bone.Model!;
            var position = new Vector3(model[0], model[1], model[2]);
            var rotation = Quaternion.Normalize(new Quaternion(model[3], model[4], model[5], model[6]));
            var inverse = Quaternion.Conjugate(models[parent].Rotation);
            var reference = new BoneTransform(Vector3.Transform(position - models[parent].Position, inverse),
                Quaternion.Normalize(inverse * rotation), Vector3.One);
            index[bone.Name!] = (short)bones.Count;
            bones.Add(new SkeletonBone(bone.Name!, parent, 0, reference));
            models.Add((position, rotation));
        }
        return bones.MoveToImmutable();
    }

    /// <summary>Model space from rotations and positions alone, composed down the parents.</summary>
    public static IReadOnlyList<(Vector3 Position, Quaternion Rotation)> ModelSpace(ImmutableArray<SkeletonBone> bones)
    {
        var result = new (Vector3 Position, Quaternion Rotation)[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var reference = bones[i].Reference;
            var rotation = Quaternion.Normalize(reference.Rotation);
            var parent = bones[i].Parent;
            result[i] = parent < 0
                ? (reference.Position, rotation)
                : (result[parent].Position + Vector3.Transform(reference.Position, result[parent].Rotation),
                    Quaternion.Normalize(result[parent].Rotation * rotation));
        }
        return result;
    }
}
