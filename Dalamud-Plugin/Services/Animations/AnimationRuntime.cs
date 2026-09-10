using System.Collections.Immutable;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed record RuntimeBinding(int Partial, string Fingerprint, string SkeletonResource, string SkeletonFingerprint);
internal sealed record RuntimeAnimationSnapshot(ulong ActorId, long Address, ushort ObjectIndex,
    ImmutableArray<ushort> Timelines, ImmutableArray<RuntimeBinding> Bindings);

/// <summary>Only this boundary reads player pointers. No native address escapes in a binding or skeleton record.</summary>
internal static unsafe class AnimationRuntime
{
    public static (ulong ActorId, long Address, ushort Index)? Identity(IObjectTable objects)
    {
        if (objects.LocalPlayer is not { } player) return null;
        return (((Character*)player.Address)->ContentId, player.Address, player.ObjectIndex);
    }
    public static void CheckPartial(IObjectTable objects, AnimationClip clip)
    {
        if (objects.LocalPlayer is not { } player) throw new InvalidOperationException("The player is unavailable.");
        var character = ((Character*)player.Address)->GetCharacterBase();
        if (character == null || character->Skeleton == null || clip.Partial < 0 ||
            character->Skeleton->PartialSkeletons == null || character->Skeleton->PartialSkeletonCount <= clip.Partial)
            throw new InvalidOperationException("The captured partial skeleton is unavailable.");
        var partial = &character->Skeleton->PartialSkeletons[clip.Partial];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if (animated == null || SkeletonFingerprint(animated->Skeleton) != clip.SkeletonFingerprint)
            throw new InvalidOperationException("The player's skeleton changed during baking.");
    }
    public static RuntimeAnimationSnapshot? Capture(IObjectTable objects)
    {
        if (objects.LocalPlayer is not { } player) return null;
        var actor = (Character*)player.Address;
        var character = actor->GetCharacterBase();
        if (character == null || character->Skeleton == null || actor->InCombat || actor->IsMounted()) return null;
        var skeleton = character->Skeleton;
        if (skeleton->PartialSkeletonCount is 0 or > 16 || skeleton->PartialSkeletons == null) return null;
        var bindings = ImmutableArray.CreateBuilder<RuntimeBinding>();
        for (var partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
        {
            var p = &skeleton->PartialSkeletons[partial];
            if (p->SkeletonResourceHandle == null) continue;
            for (var buffer = 0; buffer < 2; buffer++)
            {
                var animated = p->GetHavokAnimatedSkeleton(buffer);
                if (animated == null || animated->AnimationControls.Length is < 0 or > 256) continue;
                AnimationNative.ValidateArray(animated->AnimationControls, 256, "animation controls");
                for (var i = 0; i < animated->AnimationControls.Length; i++)
                {
                    var control = animated->AnimationControls[i].Value;
                    if (control == null || control->Weight <= 0 || control->Binding.ptr == null) continue;
                    AnimationNative.Sampler.Validate(animated->Skeleton, control->Binding.ptr);
                    bindings.Add(new RuntimeBinding(partial, AnimationNative.Fingerprint(control->Binding.ptr),
                        p->SkeletonResourceHandle->FileName.ToString(), SkeletonFingerprint(animated->Skeleton)));
                }
            }
        }
        return new RuntimeAnimationSnapshot(actor->ContentId, player.Address, player.ObjectIndex,
            actor->Timeline.TimelineSequencer.TimelineIds.ToArray().ToImmutableArray(), bindings.Distinct().ToImmutableArray());
    }

    public static string SkeletonFingerprint(hkaSkeleton* skeleton)
    {
        if (skeleton == null || skeleton->Bones.Length is < 1 or > 4096 || skeleton->ParentIndices.Length != skeleton->Bones.Length ||
            skeleton->ReferencePose.Length != skeleton->Bones.Length) throw new InvalidDataException("Invalid live skeleton.");
        AnimationNative.ValidateArray(skeleton->Bones, 4096, "bones");
        AnimationNative.ValidateArray(skeleton->ParentIndices, 4096, "parents");
        AnimationNative.ValidateArray(skeleton->ReferencePose, 4096, "reference transforms");
        AnimationNative.ValidateArray(skeleton->FloatSlots, 4096, "float slots");
        AnimationNative.ValidateArray(skeleton->ReferenceFloats, 4096, "reference floats");
        AnimationNative.ValidateArray(skeleton->Partitions, 4096, "skeleton partitions");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(skeleton->Name.String ?? "");
        for (var i = 0; i < skeleton->Bones.Length; i++)
        {
            writer.Write(skeleton->Bones[i].Name.String ?? ""); writer.Write(skeleton->ParentIndices[i]);
            writer.Write(skeleton->Bones[i].LockTranslation);
            var t = skeleton->ReferencePose[i];
            writer.Write(t.Translation.X); writer.Write(t.Translation.Y); writer.Write(t.Translation.Z);
            writer.Write(t.Rotation.X); writer.Write(t.Rotation.Y); writer.Write(t.Rotation.Z); writer.Write(t.Rotation.W);
            writer.Write(t.Scale.X); writer.Write(t.Scale.Y); writer.Write(t.Scale.Z);
        }
        if (skeleton->FloatSlots.Length is < 0 or > 4096 || skeleton->ReferenceFloats.Length != skeleton->FloatSlots.Length || skeleton->Partitions.Length is < 0 or > 4096)
            throw new InvalidDataException("Invalid skeleton floating channels or partitions.");
        writer.Write(skeleton->FloatSlots.Length);
        for (var i = 0; i < skeleton->FloatSlots.Length; i++)
        { writer.Write(skeleton->FloatSlots[i].String ?? ""); writer.Write(skeleton->ReferenceFloats[i]); }
        writer.Write(skeleton->Partitions.Length);
        for (var i = 0; i < skeleton->Partitions.Length; i++)
        {
            writer.Write(skeleton->Partitions[i].Name.String ?? "");
            writer.Write(skeleton->Partitions[i].StartBoneIndex); writer.Write(skeleton->Partitions[i].NumBones);
        }
        return AnimationPap.Hash(stream.ToArray());
    }

    public static ImmutableDictionary<int, string> InspectPap(byte[] bytes)
    {
        using var doc = new AnimationNative.Document(new AnimationPap(bytes).Havok);
        var result = ImmutableDictionary.CreateBuilder<int, string>();
        for (var i = 0; i < doc.Container->Bindings.Length; i++) result[i] = AnimationNative.Fingerprint(doc.Container->Bindings[i].ptr);
        return result.ToImmutable();
    }
    public static void CheckSkeleton(byte[] bytes, string expected)
    {
        if (InspectSkeleton(bytes) != expected)
            throw new InvalidDataException("The effective skeleton does not match the playing partial skeleton.");
    }
    public static string InspectSkeleton(byte[] bytes)
    {
        using var doc = new AnimationNative.Document(AnimationPap.SkeletonHavok(bytes));
        if (doc.Container->Skeletons.Length != 1) throw new InvalidDataException("The skeleton resource has no unique Havok skeleton.");
        return SkeletonFingerprint(doc.Container->Skeletons[0].ptr);
    }
}
