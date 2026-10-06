using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services.Physics;

/// <summary>
/// The local player as the physics builder needs it: name, race, body skeleton with rest poses, every
/// bone name of its other skeletons, and the body skeleton and physics files the game loaded.
/// </summary>
internal sealed record PhysicsCharacter(
    int ObjectIndex,
    string Name,
    ushort Race,
    PhysicsSkeleton Skeleton,
    string SkeletonFile,
    string LoadedPhysicsFile);

/// <summary> Reads the local player's skeleton and loaded physics; must run on the framework thread. </summary>
internal static unsafe class PhysicsLiveReader
{
    private const int MaximumPartials = 16;

    /// <summary> The local player, or why it can't be read. </summary>
    public static (PhysicsCharacter? Character, string? Problem) ReadLocalPlayer(IObjectTable objects)
    {
        if (objects.LocalPlayer is not ICharacter player || player.Address == 0)
            return (null, "Your character isn't loaded.");
        var drawObject = ((Character*)player.Address)->GetCharacterBase();
        if (drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human)
            return (null, "Your character has no human body right now.");
        var race = ((Human*)drawObject)->RaceSexId;
        if (GameFiles.GameRaces.Find(race) is null)
            return (null, $"Race c{race:D4} isn't one the builder knows.");
        var skeleton = drawObject->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is 0 or > MaximumPartials)
            return (null, "Your character's skeleton isn't loaded.");

        SkeletonDescriptionResult body = default;
        var others = new List<string>();
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var pose = partial->GetHavokPose(0);
            hkaSkeleton* havok = pose != null ? pose->Skeleton : null;
            if (havok == null && partial->SkeletonResourceHandle != null)
                havok = partial->SkeletonResourceHandle->HavokSkeleton;
            if (havok == null)
            {
                if (p == 0)
                    return (null, "Your character's body skeleton isn't loaded.");
                continue;
            }
            try
            {
                var description = AnimationSkeleton.Describe(havok);
                if (p == 0)
                    body = new SkeletonDescriptionResult(description,
                        partial->SkeletonResourceHandle != null ? partial->SkeletonResourceHandle->FileName.Utf8() : string.Empty);
                else
                    others.AddRange(description.Bones.Select(bone => bone.Name));
            }
            catch (InvalidDataException e)
            {
                if (p == 0)
                    return (null, $"Your character's body skeleton can't be read: {e.Message}");
            }
        }

        // The body's physics file is the first of the module's five (body, then the face, hair, headgear and top skeletons').
        var physics = string.Empty;
        var module = drawObject->BonePhysicsModule;
        if (module != null)
        {
            var handle = module->BonePhysicsResourceHandles[0].Value;
            if (handle != null)
                physics = handle->FileName.Utf8();
        }
        return (new PhysicsCharacter(player.ObjectIndex, player.Name.TextValue, race, PhysicsSkeleton.From(body.Description!, others),
            body.File, physics), null);
    }

    private readonly record struct SkeletonDescriptionResult(Models.SkeletonDescription? Description, string File);
}
