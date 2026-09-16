using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>Exact-name, rest-relative retargeting. Missing animated bones are errors, never discarded tracks.</summary>
internal sealed class AnimationRetarget
{
    private readonly SkeletonDescription source, target;
    public int[] BoneMap { get; }
    public short[] FloatMap { get; }
    public short[] PartitionMap { get; }
    private readonly int[] sourceAncestors, targetAncestors;
    private readonly BoneTransform[] sourceRest, targetRest, sourceReferences, targetReferences;
    private readonly Matrix4x4[] targetHelpers;
    public AnimationRetarget(SkeletonDescription source, SkeletonDescription target, AnimationChannels channels)
    {
        AnimationSkeleton.Validate(source); AnimationSkeleton.Validate(target);
        this.source = source; this.target = target;
        var targetNames = target.Bones.Select((b, i) => (b.Name, i)).ToDictionary(b => b.Name, b => b.i, StringComparer.Ordinal);
        BoneMap = source.Bones.Select(b => targetNames.GetValueOrDefault(b.Name, -1)).ToArray();
        FloatMap = channels.Floats.Select(i => Unique(target.FloatNames, source.FloatNames[i], "float channel")).ToArray();
        PartitionMap = channels.Partitions.Select(i => Unique(target.Partitions.Select(p => p.Name).ToImmutableArray(), source.Partitions[i].Name, "partition")).ToArray();
        sourceAncestors = new int[source.Bones.Length]; targetAncestors = new int[source.Bones.Length];
        sourceRest = new BoneTransform[source.Bones.Length]; targetRest = new BoneTransform[source.Bones.Length];
        targetHelpers = new Matrix4x4[source.Bones.Length];
        var sharedTargets = BoneMap.Where(i => i >= 0).ToHashSet();
        sourceReferences = source.Bones.Select(x => x.Reference).ToArray();
        targetReferences = target.Bones.Select(x => x.Reference).ToArray();
        for (var i = 0; i < source.Bones.Length; i++)
        {
            if (BoneMap[i] < 0) continue;
            var a = source.Bones[i].Parent;
            while (a >= 0 && BoneMap[a] < 0) a = source.Bones[a].Parent;
            var b = target.Bones[BoneMap[i]].Parent;
            while (b >= 0 && !sharedTargets.Contains(b)) b = target.Bones[b].Parent;
            sourceAncestors[i] = a; targetAncestors[i] = b;
            sourceRest[i] = Collapse(source, sourceReferences, i, a);
            targetRest[i] = Collapse(target, targetReferences, BoneMap[i], b);
            var helper = Matrix4x4.Identity;
            for (var p = target.Bones[BoneMap[i]].Parent; p != b; p = target.Bones[p].Parent) helper *= Matrix(target.Bones[p].Reference);
            if (!Matrix4x4.Invert(helper, out targetHelpers[i])) throw new InvalidDataException("A target helper bone has singular scale.");
        }
        for (var p = 0; p < channels.Partitions.Length; p++)
        {
            var a = source.Partitions[channels.Partitions[p]]; var b = target.Partitions[PartitionMap[p]];
            for (var i = a.Start; i < a.Start + a.Count; i++)
                if (BoneMap[i] >= 0 && (BoneMap[i] < b.Start || BoneMap[i] >= b.Start + b.Count))
                    throw new InvalidDataException($"Partition '{a.Name}' has incompatible destination membership.");
        }
    }
    public short[] MapTracks(IEnumerable<short> tracks)
    {
        var result = new List<short>();
        foreach (var sourceBone in tracks)
        {
            if (sourceBone < 0 || sourceBone >= BoneMap.Length)
                throw new InvalidDataException("Animation transform track is outside its source skeleton.");
            var targetBone = BoneMap[sourceBone];
            // Missing reference-only source helpers may be omitted. Map()
            // separately rejects any missing helper that actually moves.
            if (targetBone >= 0 && !result.Contains((short)targetBone)) result.Add(checked((short)targetBone));
        }
        return result.ToArray();
    }
    private static short Unique(ImmutableArray<string> names, string name, string kind)
    {
        var matches = names.Select((n, i) => (n, i)).Where(v => v.n == name && name.Length > 0).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Missing or ambiguous target {kind}: {name}.");
        return checked((short)matches[0].i);
    }
    public static bool Meaningful(BoneTransform value, BoneTransform reference) =>
        !AnimationNative.Near(AnimationSkeleton.Transform(value), AnimationSkeleton.Transform(reference), 0.000001f);
    public BoneTransform[] Map(IReadOnlyList<BoneTransform> values)
    {
        if (values.Count != source.Bones.Length) throw new InvalidDataException("Source pose length changed.");
        var missing = Enumerable.Range(0, values.Count).Where(i => BoneMap[i] < 0 && Meaningful(values[i], source.Bones[i].Reference)).Select(i => source.Bones[i].Name).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Target skeleton is missing meaningful animation bones: " + string.Join(", ", missing));
        var result = target.Bones.Select(b => b.Reference).ToArray();
        var sourceForTarget = Enumerable.Repeat(-1, target.Bones.Length).ToArray();
        for (var i = 0; i < BoneMap.Length; i++) if (BoneMap[i] >= 0) sourceForTarget[BoneMap[i]] = i;
        // Destination order guarantees that a reparented bone's target parent
        // has already been mapped before its global transform is made local.
        for (var dest = 0; dest < sourceForTarget.Length; dest++)
        {
            var i = sourceForTarget[dest]; if (i < 0) continue;
            var sample = Collapse(source, values, i, sourceAncestors[i]);
            if (!Meaningful(sample, sourceRest[i])) continue;
            if ((sourceAncestors[i] < 0 ? -1 : BoneMap[sourceAncestors[i]]) != targetAncestors[i])
            {
                var sourceGlobal = Collapse(source, values, i, -1);
                var sourceGlobalRest = Collapse(source, sourceReferences, i, -1);
                var targetGlobalRest = Collapse(target, targetReferences, dest, -1);
                var targetGlobal = Transfer(sourceGlobal, sourceGlobalRest, targetGlobalRest);
                var parent = target.Bones[dest].Parent;
                if (parent < 0) { result[dest] = targetGlobal; continue; }
                var parentGlobal = Collapse(target, result, parent, -1);
                if (!Matrix4x4.Invert(Matrix(parentGlobal), out var inverseParent))
                    throw new InvalidDataException($"Cannot retarget animation bone '{source.Bones[i].Name}' through a singular destination parent.");
                result[dest] = Decompose(Matrix(targetGlobal) * inverseParent);
                continue;
            }
            var retargeted = Transfer(sample, sourceRest[i], targetRest[i]);
            result[dest] = Decompose(Matrix(retargeted) * targetHelpers[i]);
        }
        return result;
    }
    internal static BoneTransform Transfer(BoneTransform value, BoneTransform sourceRest, BoneTransform targetRest)
    {
        if (Math.Abs(sourceRest.Scale.X * sourceRest.Scale.Y * sourceRest.Scale.Z) < 1e-12f)
            throw new InvalidDataException("Cannot retarget from a singular reference pose.");
        var alignment = Quaternion.Normalize(targetRest.Rotation * Quaternion.Inverse(sourceRest.Rotation));
        return new(targetRest.Position + Vector3.Transform(value.Position - sourceRest.Position, alignment),
            Quaternion.Normalize(targetRest.Rotation * Quaternion.Inverse(sourceRest.Rotation) * value.Rotation),
            targetRest.Scale * (value.Scale / sourceRest.Scale));
    }
    internal static BoneTransform Encode(BoneTransform pose, BoneTransform reference, sbyte hint)
    {
        if (hint == 0) return pose;
        if (hint is not (1 or 2)) throw new InvalidDataException("Unsupported animation blend hint.");
        if (Math.Abs(reference.Scale.X * reference.Scale.Y * reference.Scale.Z) < 1e-12f)
            throw new InvalidDataException("Cannot encode additive animation against a singular reference pose.");
        var inverse = Quaternion.Inverse(reference.Rotation);
        return new(pose.Position - reference.Position,
            Quaternion.Normalize(hint == 2 ? inverse * pose.Rotation : pose.Rotation * inverse), pose.Scale / reference.Scale);
    }
    internal static float MapFloat(float value, float sourceReference, float targetReference, sbyte hint) =>
        hint == 0 ? value : value - sourceReference + targetReference;
    private static BoneTransform Collapse(SkeletonDescription skeleton, IReadOnlyList<BoneTransform> values, int bone, int ancestor)
    {
        var matrix = Matrix(values[bone]);
        for (var p = skeleton.Bones[bone].Parent; p != ancestor; p = skeleton.Bones[p].Parent) matrix *= Matrix(values[p]);
        return Decompose(matrix);
    }
    private static Matrix4x4 Matrix(BoneTransform t) => Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Position);
    private static BoneTransform Decompose(Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out var scale, out var rotation, out var position)) throw new InvalidDataException("Retargeting produced a non-decomposable pose.");
        var value = new BoneTransform(position, Quaternion.Normalize(rotation), scale);
        var reconstructed = Matrix(value);
        var error = Math.Abs(m.M11 - reconstructed.M11) + Math.Abs(m.M12 - reconstructed.M12) + Math.Abs(m.M13 - reconstructed.M13) +
            Math.Abs(m.M21 - reconstructed.M21) + Math.Abs(m.M22 - reconstructed.M22) + Math.Abs(m.M23 - reconstructed.M23) +
            Math.Abs(m.M31 - reconstructed.M31) + Math.Abs(m.M32 - reconstructed.M32) + Math.Abs(m.M33 - reconstructed.M33);
        if (error > 0.0001f) throw new InvalidDataException("Retargeting would require shear, which Havok transforms cannot preserve.");
        return value;
    }
}
