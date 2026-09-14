using System.Collections.Immutable;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed record RuntimeBinding(int Partial, string Fingerprint, string SkeletonResource, string SkeletonFingerprint,
    SkeletonDescription? Skeleton = null);
internal sealed record RuntimeAnimationSnapshot(ulong ActorId, long Address, ushort ObjectIndex,
    ImmutableArray<ushort> Timelines, ImmutableArray<RuntimeBinding> Bindings);

internal readonly record struct RuntimeControlStamp(int Partial, int Buffer, int Control, nint Binding,
    nint Animation, nint Skeleton, int WeightBits);

internal readonly record struct RuntimeChangeStamp(ulong ActorId, long Address, ushort ObjectIndex,
    int TimelineCount, ulong TimelineFingerprint, int PartialCount, ulong SkeletonFingerprint,
    int ActiveControlCount, ulong ControlFingerprint)
{
    public bool SameAs(RuntimeChangeStamp other)
        => ActorId == other.ActorId && Address == other.Address && ObjectIndex == other.ObjectIndex &&
           TimelineCount == other.TimelineCount && TimelineFingerprint == other.TimelineFingerprint &&
           PartialCount == other.PartialCount && SkeletonFingerprint == other.SkeletonFingerprint &&
           ActiveControlCount == other.ActiveControlCount && ControlFingerprint == other.ControlFingerprint;
}

/// <summary>Short-lived live-runtime cache. Native pointers are cache keys only and never escape in records.</summary>
internal sealed unsafe class RuntimeCaptureCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly Dictionary<SkeletonKey, (SkeletonDescription Description, DateTime At)> skeletons = [];
    private readonly Dictionary<BindingKey, (string Fingerprint, DateTime At)> bindings = [];

    private readonly record struct SkeletonKey(nint Pointer, string Resource, int Bones, int Floats, int Partitions);
    private readonly record struct BindingKey(nint Binding, nint Animation, nint Skeleton, string Resource,
        float Duration, int Type, int TransformTracks, int FloatTracks, int TransformMap, int FloatMap, int Partitions);

    public void Clear()
    {
        lock (sync)
        {
            skeletons.Clear();
            bindings.Clear();
        }
    }

    public bool TryGetSkeleton(hkaSkeleton* skeleton, string resource, out SkeletonDescription description)
    {
        if (skeleton == null) { description = null!; return false; }
        var key = new SkeletonKey((nint)skeleton, resource, skeleton->Bones.Length, skeleton->FloatSlots.Length, skeleton->Partitions.Length);
        lock (sync)
        {
            if (skeletons.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < Lifetime)
            {
                description = cached.Description;
                return true;
            }
            skeletons.Remove(key);
            description = null!;
            return false;
        }
    }

    public void SetSkeleton(hkaSkeleton* skeleton, string resource, SkeletonDescription description)
    {
        if (skeleton == null) return;
        var key = new SkeletonKey((nint)skeleton, resource, skeleton->Bones.Length, skeleton->FloatSlots.Length, skeleton->Partitions.Length);
        lock (sync)
        {
            if (skeletons.Count >= 32 && !skeletons.ContainsKey(key)) skeletons.Remove(skeletons.Keys.First());
            skeletons[key] = (description, DateTime.UtcNow);
        }
    }

    public string BindingFingerprint(hkaAnimationBinding* binding, hkaSkeleton* skeleton, string resource)
    {
        var animation = binding->Animation.ptr;
        var key = new BindingKey((nint)binding, (nint)animation, (nint)skeleton, resource, animation->Duration,
            (int)animation->Type, animation->NumberOfTransformTracks, animation->NumberOfFloatTracks,
            binding->TransformTrackToBoneIndices.Length, binding->FloatTrackToFloatSlotIndices.Length,
            binding->PartitionIndices.Length);
        lock (sync)
            if (bindings.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < Lifetime) return cached.Fingerprint;
        var fingerprint = AnimationNative.Fingerprint(binding);
        lock (sync)
        {
            if (bindings.Count >= 256 && !bindings.ContainsKey(key)) bindings.Remove(bindings.Keys.First());
            bindings[key] = (fingerprint, DateTime.UtcNow);
        }
        return fingerprint;
    }
}

/// <summary>Only this boundary reads player pointers. No native address escapes in a binding or skeleton record.</summary>
internal static unsafe class AnimationRuntime
{
    private const ulong StampOffset = 14695981039346656037UL;

    private static ulong StampMix(ulong hash, ulong value)
        => (hash ^ value) * 1099511628211UL;

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

    public static RuntimeChangeStamp? CaptureChangeStamp(IObjectTable objects)
    {
        if (objects.LocalPlayer is not { } player) return null;
        var actor = (Character*)player.Address;
        var character = actor->GetCharacterBase();
        var timelineIds = actor->Timeline.TimelineSequencer.TimelineIds;
        var timelineFingerprint = StampMix(StampOffset, (ulong)timelineIds.Length);
        for (var i = 0; i < timelineIds.Length; i++) timelineFingerprint = StampMix(timelineFingerprint, timelineIds[i]);
        if (character == null || character->Skeleton == null || actor->InCombat || actor->IsMounted())
            return new(actor->ContentId, player.Address, player.ObjectIndex, timelineIds.Length, timelineFingerprint, 0, 0, 0, 0);

        var skeleton = character->Skeleton;
        if (skeleton->PartialSkeletonCount is 0 or > 16 || skeleton->PartialSkeletons == null)
            return new(actor->ContentId, player.Address, player.ObjectIndex, timelineIds.Length, timelineFingerprint, 0, 0, 0, 0);
        var skeletonFingerprint = StampMix(StampOffset, (ulong)skeleton->PartialSkeletonCount);
        var controlFingerprint = StampOffset;
        var activeControls = 0;
        for (var partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
        {
            var p = &skeleton->PartialSkeletons[partial];
            if (p->SkeletonResourceHandle == null) continue;
            skeletonFingerprint = StampMix(skeletonFingerprint, (ulong)partial);
            skeletonFingerprint = StampMix(skeletonFingerprint, unchecked((ulong)(nint)p->SkeletonResourceHandle));
            for (var buffer = 0; buffer < 2; buffer++)
            {
                var animated = p->GetHavokAnimatedSkeleton(buffer);
                if (animated == null || animated->AnimationControls.Length is < 0 or > 256) continue;
                AnimationNative.ValidateArray(animated->AnimationControls, 256, "animation controls");
                for (var i = 0; i < animated->AnimationControls.Length; i++)
                {
                    var control = animated->AnimationControls[i].Value;
                    if (control == null || control->Weight <= 0 || control->Binding.ptr == null) continue;
                    var binding = control->Binding.ptr;
                    activeControls++;
                    controlFingerprint = StampMix(controlFingerprint, (ulong)partial);
                    controlFingerprint = StampMix(controlFingerprint, (ulong)buffer);
                    controlFingerprint = StampMix(controlFingerprint, (ulong)i);
                    controlFingerprint = StampMix(controlFingerprint, unchecked((ulong)(nint)binding));
                    controlFingerprint = StampMix(controlFingerprint, unchecked((ulong)(nint)binding->Animation.ptr));
                    controlFingerprint = StampMix(controlFingerprint, unchecked((ulong)(nint)animated->Skeleton));
                    controlFingerprint = StampMix(controlFingerprint, unchecked((uint)BitConverter.SingleToInt32Bits(control->Weight)));
                }
            }
        }
        return new(actor->ContentId, player.Address, player.ObjectIndex, timelineIds.Length, timelineFingerprint,
            skeleton->PartialSkeletonCount, skeletonFingerprint, activeControls, controlFingerprint);
    }

    public static RuntimeAnimationSnapshot? Capture(IObjectTable objects)
        => Capture(objects, null);

    public static RuntimeAnimationSnapshot? Capture(IObjectTable objects, RuntimeCaptureCache? cache)
    {
        if (objects.LocalPlayer is not { } player) return null;
        var actor = (Character*)player.Address;
        var character = actor->GetCharacterBase();
        if (character == null || character->Skeleton == null || actor->InCombat || actor->IsMounted()) return null;
        var skeleton = character->Skeleton;
        if (skeleton->PartialSkeletonCount is 0 or > 16 || skeleton->PartialSkeletons == null) return null;
        var bindings = ImmutableArray.CreateBuilder<RuntimeBinding>();
        var descriptions = new Dictionary<nint, SkeletonDescription>();
        for (var partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
        {
            var p = &skeleton->PartialSkeletons[partial];
            if (p->SkeletonResourceHandle == null) continue;
            for (var buffer = 0; buffer < 2; buffer++)
            {
                var animated = p->GetHavokAnimatedSkeleton(buffer);
                if (animated == null || animated->AnimationControls.Length is < 0 or > 256) continue;
                SkeletonDescription description;
                try
                {
                    AnimationNative.ValidateArray(animated->AnimationControls, 256, "animation controls");
                    if (!descriptions.TryGetValue((nint)animated->Skeleton, out description!))
                    {
                        var resource = p->SkeletonResourceHandle->FileName.ToString();
                        if (cache?.TryGetSkeleton(animated->Skeleton, resource, out description) != true)
                        {
                            description = AnimationSkeleton.Describe(animated->Skeleton);
                            cache?.SetSkeleton(animated->Skeleton, resource, description);
                        }
                        descriptions[(nint)animated->Skeleton] = description;
                    }
                }
                catch (InvalidDataException) { continue; }
                for (var i = 0; i < animated->AnimationControls.Length; i++)
                {
                    var control = animated->AnimationControls[i].Value;
                    if (control == null || control->Weight <= 0 || control->Binding.ptr == null) continue;
                    var resource = p->SkeletonResourceHandle->FileName.ToString();
                    RuntimeBinding? captured;
                    try
                    {
                        var fingerprint = cache?.BindingFingerprint(control->Binding.ptr, animated->Skeleton, resource) ??
                            AnimationNative.Fingerprint(control->Binding.ptr);
                        captured = new(partial, fingerprint, resource, description.Fingerprint, description);
                    }
                    catch (InvalidDataException) { captured = null; }
                    if (captured is { })
                        bindings.Add(captured);
                }
            }
        }
        return new RuntimeAnimationSnapshot(actor->ContentId, player.Address, player.ObjectIndex,
            actor->Timeline.TimelineSequencer.TimelineIds.ToArray().ToImmutableArray(),
            bindings.DistinctBy(b => (b.Partial, b.Fingerprint, b.SkeletonResource, b.SkeletonFingerprint)).ToImmutableArray());
    }

    internal static RuntimeBinding? CaptureBinding(int partial, FFXIVClientStructs.Havok.Animation.Animation.hkaAnimationBinding* binding,
        string resource, SkeletonDescription skeleton)
    {
        try { return new(partial, AnimationNative.Fingerprint(binding), resource, skeleton.Fingerprint, skeleton); }
        catch (InvalidDataException) { return null; }
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
        for (var i = 0; i < doc.Container->Bindings.Length; i++)
            try { result[i] = AnimationNative.Fingerprint(doc.Container->Bindings[i].ptr); }
            catch (InvalidDataException) { }
        return result.ToImmutable();
    }
    public static ImmutableDictionary<int, float> InspectPapDurations(byte[] bytes)
    {
        using var doc = new AnimationNative.Document(new AnimationPap(bytes).Havok);
        var result = ImmutableDictionary.CreateBuilder<int, float>();
        for (var i = 0; i < doc.Container->Bindings.Length; i++)
        {
            try
            {
                var binding = doc.Container->Bindings[i].ptr;
                _ = AnimationNative.Fingerprint(binding);
                var duration = binding->Animation.ptr->Duration;
                if (float.IsFinite(duration) && duration >= 0) result[i] = duration;
            }
            catch (InvalidDataException) { }
        }
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
