using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// Skeleton files are read for their main skeleton only. The skeletons that hkaSkeletonMapper
/// entries embed are conversion endpoints: never library entries or source choices. A
/// predictive animation made before a skeleton mod appended bones uses the main skeleton's
/// first bones instead, which are the skeleton those mappers held.
/// </summary>
internal static unsafe class MainSkeletonFixture
{
    private const string GamePath = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        using var arena = new AnimationNative.Arena();
        var rest = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        // The skeleton before the mod appended n_hara_noanim_trans, as a mapper held it.
        var older = new SkeletonDescription("skeleton", "", Enumerable.Range(0, 167)
            .Select(i => new SkeletonBone($"bone{i}", (short)(i == 0 ? -1 : 0), 0, rest)).ToImmutableArray(), [], [], []);
        var olderFingerprint = AnimationRuntime.SkeletonFingerprint(AnimationSkeleton.Materialize(older, arena));
        var main = AnimationSkeleton.Materialize(older with { Bones = older.Bones.Add(new("n_hara_noanim_trans", 0, 0, rest)) }, arena);
        var container = arena.Alloc<hkaAnimationContainer>();
        container->Skeletons = arena.Array<FFXIVClientStructs.Havok.Common.Base.Types.hkRefPtr<hkaSkeleton>>(1);
        container->Skeletons.Data[0].ptr = main;

        var described = AnimationSkeleton.DescribeMain(container);
        check(described.Bones.Length == 168 && described.Fingerprint == AnimationRuntime.SkeletonFingerprint(main),
            "a skeleton file is read for its main skeleton");
        check(AnimationSkeleton.SelectSource(container, described.Fingerprint).Fingerprint == described.Fingerprint,
            "the chosen main skeleton is selected again from its file");
        reject(() => AnimationSkeleton.SelectSource(container, olderFingerprint),
            "a source chosen from a mapper skeleton before cannot silently fall back to the whole main skeleton");

        var candidate = new SkeletonCandidate(new(SkeletonSourceKind.Mod, new(GamePath, "fixture.sklb", "hash")), described, 0, "");
        var predictive = new AnimationChannels("skeleton", Enumerable.Range(1, 145).Select(i => (short)i).ToImmutableArray(), [], [], 167, 0);
        var resolution = AnimationSkeletonIndex.Rank(predictive, [candidate], described, GamePath);
        var selected = resolution.Selected;
        check(resolution.State == SkeletonResolutionState.Matched && selected is { Source.LeadingBones: 167 } &&
              selected.Skeleton.Bones.Length == 167 && selected.Skeleton.Fingerprint == olderFingerprint,
            "a predictive animation made before bones were appended matches the longer skeleton's first bones, the skeleton it was made for");
        var source = AnimationSkeleton.SelectSource(container, selected!.Skeleton.Fingerprint, 167);
        check(source.Bones.Length == 167 && source.Fingerprint == olderFingerprint, "baking takes the same first bones from the file again");
        reject(() => AnimationSkeleton.SelectSource(container, selected.Skeleton.Fingerprint),
            "the first bones' fingerprint does not select the whole skeleton");
        reject(() => AnimationSkeleton.SelectSource(container, described.Fingerprint, 167),
            "the whole skeleton's fingerprint does not select its first bones");

        var binding = Predictive(predictive, arena);
        reject(() => AnimationNative.Sampler.Validate(main, binding), "the whole skeleton still fails the predictive decoder guard");
        AnimationNative.Sampler.Validate(AnimationSkeleton.Materialize(source, arena), binding);
        check(true, "the first bones pass the unchanged predictive decoder guard");
        var pose = source.Bones.Select(b => b.Reference).ToArray();
        pose[1] = pose[1] with { Position = new(0, 2, 0) };
        var mapped = new AnimationRetarget(source, described, predictive).Map(pose);
        check(mapped.Length == 168 && mapped[1].Position == new Vector3(0, 2, 0) && mapped[167] == rest,
            "the decoded motion retargets onto the whole skeleton, whose appended bone keeps its rest pose");

        var otherMod = candidate with
        {
            Source = candidate.Source with { Resource = candidate.Source.Resource with { ResolvedPath = "other.sklb" } },
            Skeleton = described with
            {
                Fingerprint = "other",
                Bones = described.Bones.SetItem(1, described.Bones[1] with { Reference = rest with { Position = Vector3.UnitX } }),
            },
        };
        check(AnimationSkeletonIndex.Rank(predictive, [candidate, otherMod], described, GamePath).State == SkeletonResolutionState.Ambiguous,
            "different first bones that fit equally well need a choice");
        var live = candidate with { Source = new(SkeletonSourceKind.Collection, new(GamePath, GamePath, "live-hash")) };
        check(AnimationSkeletonIndex.Rank(predictive, [otherMod, live], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected.Source.Kind: SkeletonSourceKind.Collection },
            "among first bones that fit equally well, the character's own skeleton file is chosen");
        var renamedLive = live with { Skeleton = described with { Name = "nflb rig" } };
        check(AnimationSkeletonIndex.Rank(predictive, [otherMod, candidate, renamedLive], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected.Source.Kind: SkeletonSourceKind.Collection },
            "the character's own skeleton file wins over other files' first bones whose skeleton name matches the animation's");

        // NFLB keeps IVCS's first bones but puts its own where IVCS has physics bones.
        var nflbLive = live with { Skeleton = described with { Name = "c0801", Bones = described.Bones.SetItem(100, described.Bones[100] with { Name = "nf_bone100" }) } };
        check(AnimationSkeletonIndex.Rank(predictive, [nflbLive, candidate], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected.Source.Kind: SkeletonSourceKind.Mod },
            "the character's own file loses to first bones that name the animated bones as the skeleton the animation names does");
        var nflbUnanimated = live with { Skeleton = described with { Name = "c0801", Bones = described.Bones.SetItem(160, described.Bones[160] with { Name = "nf_bone160" }) } };
        check(AnimationSkeletonIndex.Rank(predictive, [nflbUnanimated, candidate], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected.Source.Kind: SkeletonSourceKind.Collection },
            "the character's own file still wins when only bones the animation leaves alone are named differently");
        // YAS appended five bones to the older IVCS where IVCS 2.3 appended one, and rests a few bones differently.
        var yas = candidate with
        {
            Source = candidate.Source with { Resource = candidate.Source.Resource with { ResolvedPath = "a-yas.sklb" } },
            Skeleton = older with
            {
                Fingerprint = "yas",
                Bones = [.. older.Bones.SetItem(1, older.Bones[1] with { Reference = rest with { Position = Vector3.UnitY } }),
                    .. Enumerable.Range(0, 5).Select(i => new SkeletonBone($"ya_bone{i}", 0, 0, rest))],
            },
        };
        check(AnimationSkeletonIndex.Rank(predictive, [yas, candidate], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected.Source.Resource.ResolvedPath: "fixture.sklb" } &&
              AnimationSkeletonIndex.Rank(predictive, [candidate, yas], described, GamePath).Selected?.Source.Resource.ResolvedPath == "fixture.sklb",
            "among first bones that fit equally well, those of the skeleton that added the fewest bones since are chosen");

        var whole = candidate with
        {
            Source = candidate.Source with { Resource = candidate.Source.Resource with { ResolvedPath = "older.sklb" } },
            Skeleton = older with { Fingerprint = olderFingerprint },
        };
        check(AnimationSkeletonIndex.Rank(predictive, [candidate, whole], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Candidates.Length: 1, Selected.Source.LeadingBones: 0 },
            "a whole skeleton that fits is used without offering any skeleton's first bones");
        // A quantized clip made on IVCS before it appended n_hara_noanim_trans, such as
        // [Noff][IVCS] Idle Pussy Lick [Shiver]: 167 reference bones against IVCS 2.3's 168.
        var quantized = predictive with { ExactReferenceModel = true };
        check(AnimationSkeletonIndex.Rank(quantized, [candidate], described, GamePath) is
              { State: SkeletonResolutionState.Matched, Selected: { Source.LeadingBones: 167, Skeleton.Bones.Length: 167 } } matchedQuantized &&
              matchedQuantized.Selected!.Skeleton.Fingerprint == olderFingerprint,
            "a quantized animation made before bones were appended matches its model's longer skeleton's first bones");
        var otherModel = candidate with
        {
            Source = candidate.Source with
            {
                Resource = candidate.Source.Resource with { GamePath = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb", ResolvedPath = "c0101.sklb" },
            },
        };
        check(AnimationSkeletonIndex.Rank(quantized, [otherModel], described, GamePath).State == SkeletonResolutionState.Incompatible,
            "a quantized animation never takes another model's first bones, whose rest pose it was not made on");
        check(AnimationSkeletonIndex.Rank(quantized, [otherModel, candidate], described, GamePath).Candidates is [{ Source.LeadingBones: 167 } only] &&
              only.Source.Resource.ResolvedPath == "fixture.sklb",
            "among first bones, only those of the quantized animation's own model are offered");
        check(AnimationSkeletonIndex.Rank(quantized, [yas, candidate], described, GamePath).Selected?.Source.Resource.ResolvedPath == "fixture.sklb",
            "a quantized animation also takes the first bones of the skeleton that added the fewest bones since");
        var quantizedBinding = Quantized(quantized, arena);
        reject(() => AnimationNative.Sampler.Validate(main, quantizedBinding), "the whole skeleton still fails the quantized decoder guard");
        AnimationNative.Sampler.Validate(AnimationSkeleton.Materialize(source, arena), quantizedBinding);
        check(true, "the first bones pass the unchanged quantized decoder guard");
        check(AnimationSkeletonIndex.Rank(predictive with { ReferenceBones = 169 }, [candidate], described, GamePath).State ==
              SkeletonResolutionState.Incompatible,
            "an animation declaring more bones than any skeleton has still reports that no skeleton fits");
        check(AnimationSkeleton.Leading(described with { Partitions = [new("body", 0, 168)] }, 167) == null,
            "a skeleton is not cut through a partition");
        check(AnimationSkeletonIndex.Rank(predictive with { ReferenceBones = null, ReferenceFloats = null }, [candidate], described, GamePath)
                .Selected is { Source.LeadingBones: 0, Skeleton.Bones.Length: 168 },
            "an uncompressed clip matches the whole main skeleton");

        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<SkeletonCandidate>(JsonSerializer.Serialize(selected, options), options);
        check(restored?.Source.LeadingBones == 167 && restored.Skeleton.Fingerprint == olderFingerprint,
            "the chosen first bones survive capture and journal serialization");
        var journaled = JsonNode.Parse(JsonSerializer.Serialize(candidate, options))!;
        journaled["Source"]!["Variant"] = "Mapper 0 A (entry 2)";
        var legacy = journaled.Deserialize<SkeletonCandidate>(options);
        check(legacy?.Skeleton.Fingerprint == described.Fingerprint && legacy.Source.Resource == candidate.Source.Resource &&
              legacy.Source.LeadingBones == 0,
            "a candidate journaled with a mapper variant still loads");

        container->Skeletons.Data[0].ptr = null;
        reject(() => AnimationSkeleton.DescribeMain(container), "a skeleton file without a main skeleton is rejected");
        container->Skeletons = arena.Array<FFXIVClientStructs.Havok.Common.Base.Types.hkRefPtr<hkaSkeleton>>(0);
        reject(() => AnimationSkeleton.DescribeMain(container), "a skeleton file with no skeletons is rejected");
    }

    private static hkaAnimationBinding* Predictive(AnimationChannels channels, AnimationNative.Arena arena)
    {
        var a = arena.Alloc<AnimationNative.Predictive>();
        a->Animation.Type = hkaAnimation.AnimationType.PredictiveCompressedAnimation;
        a->Animation.Duration = 1;
        a->Animation.NumberOfTransformTracks = channels.Bones.Length;
        a->Animation.NumberOfFloatTracks = channels.Floats.Length;
        a->NumBones = channels.ReferenceBones!.Value; a->NumFloatSlots = channels.ReferenceFloats!.Value; a->NumFrames = 2;
        a->CompressedData = arena.Copy<byte>([1]); a->IntData = arena.Array<ushort>(8); a->FloatData = arena.Array<float>(8);
        var b = arena.Alloc<hkaAnimationBinding>(); b->Animation.ptr = &a->Animation;
        b->TransformTrackToBoneIndices = arena.Copy<short>(channels.Bones.AsSpan());
        b->FloatTrackToFloatSlotIndices = arena.Copy<short>(channels.Floats.AsSpan());
        return b;
    }

    private static hkaAnimationBinding* Quantized(AnimationChannels channels, AnimationNative.Arena arena)
    {
        var a = arena.Alloc<AnimationNative.Quantized>();
        a->Animation.Type = hkaAnimation.AnimationType.QuantizedCompressedAnimation;
        a->Animation.Duration = 1;
        a->Animation.NumberOfTransformTracks = channels.Bones.Length;
        a->Animation.NumberOfFloatTracks = channels.Floats.Length;
        var header = new AnimationNative.QuantizedHeader
        {
            HeaderSize = (ushort)sizeof(AnimationNative.QuantizedHeader), NumBones = (ushort)channels.ReferenceBones!.Value,
            NumFloats = (ushort)channels.ReferenceFloats!.Value, NumFrames = 2, Duration = 1, FrameSize = 1,
        };
        var data = new byte[sizeof(AnimationNative.QuantizedHeader) + 2];
        new ReadOnlySpan<byte>(&header, sizeof(AnimationNative.QuantizedHeader)).CopyTo(data);
        a->Data = arena.Copy<byte>(data);
        var b = arena.Alloc<hkaAnimationBinding>(); b->Animation.ptr = &a->Animation;
        b->TransformTrackToBoneIndices = arena.Copy<short>(channels.Bones.AsSpan());
        b->FloatTrackToFloatSlotIndices = arena.Copy<short>(channels.Floats.AsSpan());
        return b;
    }
}
