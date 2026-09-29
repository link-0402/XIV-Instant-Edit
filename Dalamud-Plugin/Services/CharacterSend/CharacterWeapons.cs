using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using GraphicsTransform = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace InstantEdit.Services.CharacterSend;

/// <summary>
/// Where a character's weapon hangs as the game draws it now: its model, the character bone that
/// holds it, and the weapon root's place relative to that bone (translation, rotation x/y/z/w,
/// scale, in the game's model space). No bone when none could be told.
/// </summary>
internal sealed record CharacterWeaponPlacement(string ModelPath, string? Bone, float[]? Offset, string Method, float Error = 0)
{
    /// <summary> The attach's own bone: its child transform puts the weapon where the game draws it. </summary>
    public const string AttachMethod = "attach";
    /// <summary> The weapon bone (<c>n_buki_*</c>, <c>j_buki_*</c>) nearest to the weapon, when the attach didn't say. </summary>
    public const string NearestMethod = "nearest weapon bone";
    public const string NoneMethod = "none";
}

/// <summary>
/// Picks the bone that holds a weapon, from the character's posed bones in model space, the
/// weapon root's place in that space, and the child transform of the game's attach. Dalamud-free.
/// </summary>
internal static class CharacterWeaponPlacer
{
    /// <summary> How close the attach's bone and child transform must land the weapon to where the game draws it. </summary>
    public const float MatchDistance = 0.005f;
    public const float MatchDegrees = 2f;
    /// <summary> How far the nearest weapon bone may be from the weapon's root. </summary>
    public const float NearestDistance = 0.5f;

    /// <summary>
    /// The bones the game hangs weapons from: in the hands (<c>n_buki_r</c>, <c>n_buki_l</c>), and
    /// sheathed on the hips, back or shield side (<c>j_buki_kosi_*</c>, <c>j_buki_sebo_*</c>, <c>n_buki_tate_*</c>).
    /// </summary>
    public static bool IsWeaponBone(string name)
        => name.StartsWith("n_buki", StringComparison.Ordinal) || name.StartsWith("j_buki", StringComparison.Ordinal);

    /// <param name="bones">Each bone of the character's body skeleton, posed, in model space.</param>
    /// <param name="weapon">The weapon's root in the character's model space (row vectors, as System.Numerics).</param>
    /// <param name="child">The game's attach child transform, or null when the weapon isn't attached to a bone.</param>
    public static CharacterWeaponPlacement Place(string modelPath, IReadOnlyList<(string Name, BoneTransform Model)> bones,
        Matrix4x4 weapon, BoneTransform? child)
    {
        if (!Matrix4x4.Decompose(weapon, out _, out var weaponRotation, out var weaponPosition))
            return new(modelPath, null, null, CharacterWeaponPlacement.NoneMethod);
        var index = -1;
        var error = float.MaxValue;
        if (child is { } attached)
        {
            // The game hangs the weapon's root from one bone by the child transform; that bone is the
            // one this lands where the game draws the weapon.
            var childMatrix = Matrix(attached, rigid: false);
            for (var i = 0; i < bones.Count; i++)
            {
                if (!Matrix4x4.Decompose(childMatrix * Matrix(bones[i].Model, rigid: false), out _, out var rotation, out var position))
                    continue;
                var distance = Vector3.Distance(position, weaponPosition);
                if (distance > MatchDistance || Degrees(rotation, weaponRotation) > MatchDegrees || distance >= error)
                    continue;
                (index, error) = (i, distance);
            }
        }
        var method = CharacterWeaponPlacement.AttachMethod;
        if (index < 0)
        {
            method = CharacterWeaponPlacement.NearestMethod;
            error = NearestDistance;
            for (var i = 0; i < bones.Count; i++)
            {
                if (!IsWeaponBone(bones[i].Name))
                    continue;
                var distance = Vector3.Distance(bones[i].Model.Position, weaponPosition);
                if (distance < error)
                    (index, error) = (i, distance);
            }
        }
        if (index < 0)
            return new(modelPath, null, null, CharacterWeaponPlacement.NoneMethod);
        // Relative to the bone as Blender poses it: rotation and position only.
        return Matrix4x4.Invert(Matrix(bones[index].Model, rigid: true), out var inverse) &&
               Offset(weapon * inverse) is { } offset
            ? new(modelPath, bones[index].Name, offset, method, error)
            : new(modelPath, null, null, CharacterWeaponPlacement.NoneMethod);
    }

    /// <summary> A transform as a matrix for row vectors: scale, then rotation, then translation. </summary>
    public static Matrix4x4 Matrix(BoneTransform transform, bool rigid)
        => (rigid ? Matrix4x4.Identity : Matrix4x4.CreateScale(transform.Scale)) *
           Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(transform.Rotation)) *
           Matrix4x4.CreateTranslation(transform.Position);

    /// <summary> A matrix as translation, rotation x/y/z/w and scale, or null when it isn't one. </summary>
    public static float[]? Offset(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
            return null;
        rotation = Quaternion.Normalize(rotation);
        float[] values = [translation.X, translation.Y, translation.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, scale.X, scale.Y, scale.Z];
        return values.All(float.IsFinite) && MathF.Abs(scale.X) > 1e-6f && MathF.Abs(scale.Y) > 1e-6f && MathF.Abs(scale.Z) > 1e-6f
            ? values
            : null;
    }

    private static float Degrees(Quaternion a, Quaternion b)
        => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;
}

/// <summary>
/// Reads where a character's drawn weapons hang: the main hand, off hand and system slot. Call on
/// the framework thread.
/// </summary>
internal static unsafe class CharacterWeaponReader
{
    public static IReadOnlyList<CharacterWeaponPlacement> Read(nint characterAddress)
    {
        var result = new List<CharacterWeaponPlacement>();
        if (characterAddress == 0)
            return result;
        var character = (Character*)characterAddress;
        var body = character->GetCharacterBase();
        if (body == null || body->Skeleton == null || body->Skeleton->PartialSkeletonCount < 1 || body->Skeleton->PartialSkeletons == null)
            return result;
        var skeleton = body->Skeleton;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null)
            return result;
        var havok = pose->Skeleton;
        var model = pose->GetSyncedPoseModelSpace();
        var count = havok->Bones.Length;
        if (count is < 1 or > AnimationTake.MaximumBones || model == null || model->Length != count || model->Data == null)
            return result;
        AnimationNative.ValidateArray(havok->Bones, AnimationTake.MaximumBones, "bones");
        var bones = new List<(string, BoneTransform)>(count);
        for (var i = 0; i < count; i++)
            bones.Add((havok->Bones[i].Name.String ?? string.Empty, AnimationSkeleton.Transform(model->Data[i])));
        if (!Matrix4x4.Invert(World(skeleton->Transform), out var toModel))
            return result;

        foreach (var slot in new[] { DrawDataContainer.WeaponSlot.MainHand, DrawDataContainer.WeaponSlot.OffHand, DrawDataContainer.WeaponSlot.System })
        {
            var drawObject = character->DrawData.Weapon(slot).DrawData.DrawObject;
            if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
                continue;
            var weaponBase = (CharacterBase*)drawObject;
            if (weaponBase->GetModelType() != CharacterBase.ModelType.Weapon)
                continue;
            var weapon = (Weapon*)drawObject;
            var path = CharacterSendPlan.WeaponModelPath(weapon->ModelSetId, weapon->SecondaryId);
            if (weaponBase->Skeleton == null)
            {
                result.Add(new(path, null, null, CharacterWeaponPlacement.NoneMethod));
                continue;
            }
            // Both skeletons' world transforms are what the game draws them with.
            var inModel = World(weaponBase->Skeleton->Transform) * toModel;
            // The placer checks the child transform against where the game draws the weapon, so an
            // attach to another skeleton, or of another kind, falls back to the nearest weapon bone.
            BoneTransform? child = null;
            var attach = &weaponBase->Attach;
            if (attach->ExecuteType != 0 && (attach->TargetSkeleton == skeleton || attach->OwnerSkeleton == skeleton) &&
                attach->AttachmentCount > 0 && attach->SkeletonBoneAttachments != null)
                child = AnimationSkeleton.Transform(attach->SkeletonBoneAttachments[0].ChildTransform);
            result.Add(CharacterWeaponPlacer.Place(path, bones, inModel, child));
        }
        return result;
    }

    private static Matrix4x4 World(GraphicsTransform transform)
        => CharacterWeaponPlacer.Matrix(new BoneTransform(transform.Position, transform.Rotation, transform.Scale), rigid: false);
}
