using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class ChartSkeletonFixture
{
    public static void Run(Action<bool, string> check)
    {
        BoneTransform T() => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        SkeletonDescription Skeleton(string fingerprint, int bones = 2) =>
            new("rig", fingerprint,
                Enumerable.Range(0, bones).Select(i => new SkeletonBone(i == 0 ? "root" : $"bone{i}", (short)(i == 0 ? -1 : 0), 0, T())).ToImmutableArray(),
                [], [], []);
        SkeletonCandidate Candidate(string model, SkeletonDescription skeleton, IEnumerable<string>? aliases = null)
        {
            var path = $"chara/human/{model}/skeleton/base/b0001/skl_{model}b0001.sklb";
            var mapped = aliases?.ToImmutableArray() ?? [path];
            return new(new(SkeletonSourceKind.Collection, new(path, path, skeleton.Fingerprint), "",
                mapped, model, AnimationSkeletonIndex.MappingFingerprint(mapped)), skeleton, 0, "");
        }

        const string target = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";
        var aliases = new[]
        {
            "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb",
            "chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb",
            target,
            "chara/human/c0701/skeleton/base/b0001/skl_c0701b0001.sklb",
        };
        var identity = AnimationSkeletonIndex.SourceIdentity([
            "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap",
            "chara/human/c0201/animation/a0001/bt_common/resident/idle.pap",
            "chara/human/c0801/animation/a0001/bt_common/resident/idle.pap",
        ], "c0801");
        check(identity.MappedModel == "c0101" && identity.CanonicalModel == "c0101" && !string.IsNullOrEmpty(identity.MappingFingerprint),
            "mapped base animation aliases resolve to their broadest chart ancestor");

        var channels = new AnimationChannels("rig", [0, 1], [], [], 2, 0);
        var c0101 = Candidate("c0101", Skeleton("c0101"), aliases);
        var c0201 = Candidate("c0201", Skeleton("c0201"));
        var c0801 = Candidate("c0801", Skeleton("c0801"));
        var mapped = AnimationSkeletonIndex.Rank(channels, [c0801, c0201, c0101], null, target, sourceIdentity: identity);
        check(mapped.Selected?.Source.CanonicalModel == "c0101" && mapped.State == SkeletonResolutionState.Matched,
            "one mapped c0101 skeleton is selected for the live female Miqo'te target");

        var shortSource = Candidate("c0101", Skeleton("short", 2), aliases);
        var completeRace = Candidate("c0801", Skeleton("complete", 3));
        var longerChannels = new AnimationChannels("rig", [0, 1, 2], [], [], 3, 0);
        var fallback = AnimationSkeletonIndex.Rank(longerChannels, [shortSource, completeRace], null, target, sourceIdentity: identity);
        check(fallback.Selected?.Source.CanonicalModel == "c0801",
            "a race-specific source remains selected when the mapped ancestor cannot satisfy binding requirements");

        var papOnly = AnimationSkeletonIndex.SourceIdentity([], "c0801");
        var papFallback = AnimationSkeletonIndex.Rank(channels, [c0101, c0801], null, target, sourceIdentity: papOnly);
        check(papFallback.Selected?.Source.CanonicalModel == "c0801",
            "PAP model metadata provides a source fallback when mapping aliases are unavailable");

        var contradictory = AnimationSkeletonIndex.SourceIdentity([
            "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb",
            "chara/human/c0701/skeleton/base/b0001/skl_c0701b0001.sklb",
        ], "c0801");
        check(contradictory.MappedModel == null && contradictory.CanonicalModel == "c0801",
            "contradictory mapping evidence falls back to PAP identity without inventing a chart edge");
        var changed = AnimationSkeletonIndex.SourceIdentity(aliases[..3], "c0801");
        check(changed.MappingFingerprint != identity.MappingFingerprint,
            "mapping changes produce a different source identity fingerprint for cache and manual-choice invalidation");

        var faceTarget = "chara/human/c0801/skeleton/face/f0001/skl_c0801f0001.sklb";
        var faceSource = new SkeletonCandidate(new(SkeletonSourceKind.Game,
            new("chara/human/c0101/skeleton/face/f0001/skl_c0101f0001.sklb", "chara/human/c0101/skeleton/face/f0001/skl_c0101f0001.sklb", "face-a")),
            Skeleton("face-a"), 0, "");
        var faceRace = new SkeletonCandidate(new(SkeletonSourceKind.Game,
            new(faceTarget, faceTarget, "face-b")), Skeleton("face-b"), 0, "");
        var faceIdentity = AnimationSkeletonIndex.SourceIdentity([
            "chara/human/c0101/animation/f0001/bt_common/resident/face.pap",
            "chara/human/c0801/animation/f0001/bt_common/resident/face.pap",
        ], "c0801");
        var facial = AnimationSkeletonIndex.Rank(channels, [faceSource, faceRace], null, faceTarget, sourceIdentity: faceIdentity);
        check(facial.Selected?.Source.Resource.GamePath == faceTarget,
            "facial skeleton resolution remains exact to the live race path");

        var unlistedTarget = "chara/human/c1601/skeleton/base/b0001/skl_c1601b0001.sklb";
        var unlisted = AnimationSkeletonIndex.Rank(channels, [Candidate("c0101", Skeleton("midlander")), Candidate("c1601", Skeleton("hrothgar"))],
            null, unlistedTarget, sourceIdentity: AnimationSkeletonIndex.SourceIdentity([], "c1601"));
        check(unlisted.Selected?.Source.CanonicalModel == "c1601",
            "unlisted model IDs retain exact PAP/manual handling instead of inheriting from the chart");
    }
}
