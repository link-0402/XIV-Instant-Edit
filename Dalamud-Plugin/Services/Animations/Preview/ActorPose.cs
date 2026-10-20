using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// A character's body skeleton as the game shows it this frame: every bone's name and its place in
/// model space, physics and pose plugins included, and where the skeleton stands in the world. Read
/// on the framework thread into buffers kept between frames; the names are read again only when the
/// skeleton changes.
/// </summary>
internal sealed class ActorPose
{
    // The Havok skeleton and its bone array the names were read from: identities only, never followed.
    private nint skeleton;
    private nint bones;
    // The same for the other partial skeletons read along (face, hair, gear), in order.
    private nint[] partialIdentities = [];

    // The other partial skeletons' bones this frame: partial, its bone index, and the index they're stored at.
    private readonly List<(int Partial, int Bone)> partialBones = [];

    /// <summary>The bones' names, by index.</summary>
    public string[] Names { get; private set; } = [];

    /// <summary>Bone indices by name.</summary>
    public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);

    /// <summary>Each bone's position in model space.</summary>
    public Vector3[] Positions { get; private set; } = [];

    /// <summary>Each bone's rotation in model space.</summary>
    public Quaternion[] Rotations { get; private set; } = [];

    /// <summary>Where the skeleton stands in the world: model space is scaled by <see cref="Scale"/>, turned, then moved here.</summary>
    public Vector3 Position { get; private set; }

    public Quaternion Rotation { get; private set; } = Quaternion.Identity;

    public Vector3 Scale { get; private set; } = Vector3.One;

    /// <summary>
    /// Reads the drawn character at <paramref name="characterAddress"/>; false when its body skeleton has no readable pose.
    /// With <paramref name="allPartials"/> the bones of its other skeletons (face, hair, gear) follow the body's, each name
    /// once and the body's first; their poses share the body's model space. Without it the indices are the body skeleton's.
    /// </summary>
    internal unsafe bool Read(nint characterAddress, bool allPartials = false)
    {
        var drawn = ((Character*)characterAddress)->GetCharacterBase();
        if (drawn == null || drawn->Skeleton == null)
            return false;
        var body = drawn->Skeleton;
        if (body->PartialSkeletonCount < 1 || body->PartialSkeletons == null)
            return false;
        var pose = body->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null)
            return false;
        var havok = pose->Skeleton;
        var count = havok->Bones.Length;
        if (count is < 1 or > AnimationTake.MaximumBones || havok->Bones.Data == null)
            return false;
        // Synced, the model pose holds what physics and pose plugins wrote this frame.
        var model = pose->GetSyncedPoseModelSpace();
        if (model == null || model->Length != count || model->Data == null)
            return false;

        // The other partials with a readable pose, by index; null where one has none this frame.
        var others = allPartials ? body->PartialSkeletonCount - 1 : 0;
        var poses = new FFXIVClientStructs.Havok.Animation.Rig.hkaPose*[Math.Max(0, others)];
        var identities = new nint[poses.Length * 2];
        for (var p = 0; p < poses.Length; p++)
        {
            var other = body->PartialSkeletons[p + 1].GetHavokPose(0);
            if (other == null || other->Skeleton == null || other->Skeleton->Bones.Data == null)
                continue;
            var otherModel = other->GetSyncedPoseModelSpace();
            if (otherModel == null || otherModel->Length != other->Skeleton->Bones.Length || otherModel->Data == null)
                continue;
            poses[p] = other;
            identities[p * 2] = (nint)other->Skeleton;
            identities[p * 2 + 1] = (nint)other->Skeleton->Bones.Data;
        }

        if ((nint)havok != skeleton || (nint)havok->Bones.Data != bones || !identities.AsSpan().SequenceEqual(partialIdentities) ||
            Names.Length != count + partialBones.Count)
        {
            var names = new List<string>(count);
            Index.Clear();
            partialBones.Clear();
            for (var i = 0; i < count; i++)
            {
                names.Add(havok->Bones[i].Name.String ?? string.Empty);
                Index.TryAdd(names[i], i);
            }
            for (var p = 0; p < poses.Length; p++)
            {
                if (poses[p] == null)
                    continue;
                var otherHavok = poses[p]->Skeleton;
                for (var i = 0; i < otherHavok->Bones.Length && names.Count < AnimationTake.MaximumBones; i++)
                {
                    var name = otherHavok->Bones[i].Name.String;
                    if (string.IsNullOrEmpty(name) || !Index.TryAdd(name, names.Count))
                        continue;
                    names.Add(name);
                    partialBones.Add((p, i));
                }
            }
            Names = [.. names];
            Positions = new Vector3[Names.Length];
            Rotations = new Quaternion[Names.Length];
            skeleton = (nint)havok;
            bones = (nint)havok->Bones.Data;
            partialIdentities = identities;
        }
        for (var i = 0; i < count; i++)
        {
            var bone = AnimationSkeleton.Transform(model->Data[i]);
            Positions[i] = bone.Position;
            Rotations[i] = bone.Rotation;
        }
        for (var k = 0; k < partialBones.Count; k++)
        {
            var (p, i) = partialBones[k];
            var bone = AnimationSkeleton.Transform(poses[p]->GetSyncedPoseModelSpace()->Data[i]);
            Positions[count + k] = bone.Position;
            Rotations[count + k] = bone.Rotation;
        }
        var place = body->Transform;
        Position = place.Position;
        Rotation = place.Rotation;
        Scale = place.Scale;
        return true;
    }
}
