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

    /// <summary>Reads the drawn character at <paramref name="characterAddress"/>; false when its body skeleton has no readable pose.</summary>
    internal unsafe bool Read(nint characterAddress)
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
        if ((nint)havok != skeleton || (nint)havok->Bones.Data != bones || Names.Length != count)
        {
            var names = new string[count];
            for (var i = 0; i < count; i++)
                names[i] = havok->Bones[i].Name.String ?? string.Empty;
            Names = names;
            Index.Clear();
            for (var i = 0; i < count; i++)
                Index.TryAdd(names[i], i);
            Positions = new Vector3[count];
            Rotations = new Quaternion[count];
            skeleton = (nint)havok;
            bones = (nint)havok->Bones.Data;
        }
        for (var i = 0; i < count; i++)
        {
            var bone = AnimationSkeleton.Transform(model->Data[i]);
            Positions[i] = bone.Position;
            Rotations[i] = bone.Rotation;
        }
        var place = body->Transform;
        Position = place.Position;
        Rotation = place.Rotation;
        Scale = place.Scale;
        return true;
    }
}
