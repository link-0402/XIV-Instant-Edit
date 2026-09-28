using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// Unticking a bone under Animated Bones rebakes the animation without that bone's
/// track, so nothing in it drives the bone and in-game physics can move it again.
/// These fixtures cover the managed half: which bones a clip animates, what may be
/// left out, and what the output's validation must then expect for those bones.
/// </summary>
internal static class AnimationBonesFixture
{
    public static void Run(Action<bool, string> check)
    {
        static BoneTransform T(float x) => new(new(x, 0, 0), Quaternion.Identity, Vector3.One);
        var skeleton = new SkeletonDescription("rig", "source", [
            new("n_root", -1, 0, T(0)), new("j_kosi", 0, 0, T(1)),
            new("j_kami_a", 1, 0, T(2)), new("j_kami_b", 2, 0, T(3))], [], [], []);
        var resource = new AnimationResource("chara/test.pap", "chara/test.pap", "hash");
        var candidate = new SkeletonCandidate(new(SkeletonSourceKind.Game, resource), skeleton, 0, "");
        // Tracks arrive in the animation's own order, not the skeleton's.
        var resolution = new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [3, 0, 2]);
        var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 1, "chara/test.sklb", Resolution: resolution);

        check(AnimationBones.Animated(clip).SequenceEqual(["n_root", "j_kami_a", "j_kami_b"]),
            "the animated bone list names every tracked bone once, in skeleton order, and skips bones without a track");
        check(AnimationBones.Animated(clip with { Resolution = resolution with { State = SkeletonResolutionState.Ambiguous, Selected = null } }).IsEmpty &&
              AnimationBones.Animated(clip with { Resolution = null }).IsEmpty,
            "no bones are listed until a source skeleton names the tracks");
        check(new SkeletonResolution(SkeletonResolutionState.Searching, []).TrackBones is { IsDefault: false, IsEmpty: true },
            "a resolution without recorded tracks reads as none rather than an uninitialized array");

        check(AnimationBones.Problem(clip, []) == null && AnimationBones.Problem(clip, ["j_kami_a"]) == null,
            "an animated bone can be left out of a rebake");
        check(AnimationBones.Problem(clip, ["j_kosi"]) is { } stale && stale.Contains("j_kosi"),
            "a bone the animation does not move cannot be unticked, and the refusal names it");
        check(AnimationBones.Problem(clip, ["n_root", "j_kami_a", "j_kami_b"]) == "Keep at least one bone animated.",
            "leaving out every animated bone is refused");
        check(AnimationBones.Problem(clip with { Resolution = null }, ["j_kami_a"]) != null,
            "unticked bones are refused while the animation's bones are still unknown");

        check(AnimationBones.OffsetConflict(["j_kami_a"], ["j_te_l", "j_kami_a"]) is { } conflict &&
              conflict.Contains("j_kami_a") && !conflict.Contains("j_te_l"),
            "a LivePose offset on an unticked bone blocks the bake instead of being cleared without being baked");
        check(AnimationBones.OffsetConflict(["j_kami_a"], ["j_te_l"]) == null && AnimationBones.OffsetConflict([], ["j_te_l"]) == null,
            "offsets on bones that stay animated do not conflict");

        check(AnimationBones.Indices(skeleton, ["j_kami_b", "not_in_this_rig"]).SetEquals([3]),
            "unticked names resolve on the output skeleton, skipping names it does not have");

        // Two sampled frames of a four-bone rig. Bone 2 is left out: its track goes,
        // and validation must expect its reference pose, which is what Havok samples
        // for a bone with no track. Every other bone keeps its sampled value.
        static hkQsTransformf Q(float x) => AnimationSkeleton.Transform(T(x));
        hkQsTransformf[] reference = [Q(0), Q(1), Q(2), Q(3)];
        List<hkQsTransformf[]> frames = [[Q(10), Q(11), Q(12), Q(13)], [Q(20), Q(21), Q(22), Q(23)]];
        List<short> tracks = [3, 0, 2];
        AnimationBones.Exclude(tracks, frames, reference, new HashSet<int> { 2 });
        check(tracks.SequenceEqual(new short[] { 3, 0 }),
            "an unticked bone's track is removed while the remaining tracks keep their order");
        check(frames.All(frame => AnimationSkeleton.Transform(frame[2]) == T(2)) &&
              AnimationSkeleton.Transform(frames[0][3]) == T(13) && AnimationSkeleton.Transform(frames[1][0]) == T(20),
            "validation expects the unticked bone at its reference pose and every other bone as sampled");
        List<short> untouched = [3, 0, 2];
        AnimationBones.Exclude(untouched, frames, reference, new HashSet<int>());
        check(untouched.SequenceEqual(new short[] { 3, 0, 2 }) && AnimationSkeleton.Transform(frames[0][3]) == T(13),
            "a rebake with nothing unticked changes no track and no expected sample");

        Standards(check);

        var temp = Path.Combine(Path.GetTempPath(), "ie-animated-bones-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Emote", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Animated bones", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.ExcludeBones, ExcludedBones: ["j_kami_a"]);
            var store = new AnimationJournalStore(temp);
            store.Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request;
            check(loaded.Operation == AnimationOperation.ExcludeBones && loaded.ExcludedBones.SequenceEqual(["j_kami_a"]) &&
                  loaded.Capture.Clip.Resolution!.TrackBones.SequenceEqual(new short[] { 3, 0, 2 }),
                "recovery keeps which bones were left out and which bones the clip animated");

            // Journals written before bones could be left out carry neither field.
            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["Request"]!.AsObject().Remove("ExcludedBones");
            json["Request"]!["Capture"]!["Clip"]!["Resolution"]!.AsObject().Remove("TrackBones");
            File.WriteAllText(path, json.ToJsonString());
            var legacy = new AnimationJournalStore(temp).Load().Single().Request;
            check(legacy.ExcludedBones is { IsDefault: false, IsEmpty: true } &&
                  legacy.Capture.Clip.Resolution!.TrackBones is { IsDefault: false, IsEmpty: true },
                "an older journal loads with no bones left out and no recorded tracks");
        }
        finally { Directory.Delete(temp, true); }
    }

    /// <summary>
    /// The shapes found in real skeleton mods, shrunk: the game's own skeleton ends with
    /// n_hara_noanim_trans, IVCS appends iv_ bones and carries that game bone last, YAS
    /// appends ya_ bones after IVCS's and lacks it, and an all-in-one rig like NFLB + YAS
    /// moves IVCS's physics bones, YAS's bones and n_hara_noanim_trans far behind its own.
    /// </summary>
    internal static class Rigs
    {
        static BoneTransform T(float x) => new(new(x, 0, 0), Quaternion.Identity, Vector3.One);
        static SkeletonDescription S(string fingerprint, params string[] names) => new("skeleton", fingerprint,
            [.. names.Select((name, i) => new SkeletonBone(name, (short)(i == 0 ? -1 : 0), 0, T(i)))], [], [], []);
        public static readonly string[] Legacy = ["n_root", "n_hara", "j_kosi", "j_sebo_a", "j_kubi", "j_kao"];
        public static readonly SkeletonDescription Game = S("game", [.. Legacy, "n_hara_noanim_trans"]);
        public static readonly SkeletonDescription Ivcs = S("ivcs", [.. Legacy, "iv_ko_c_l", "iv_shiri_l", "iv_kyokin_phys_l", "n_hara_noanim_trans"]);
        public static readonly SkeletonDescription Yas = S("yas", [.. Legacy, "iv_ko_c_l", "iv_shiri_l", "iv_kyokin_phys_l", "ya_shiri_phys_l"]);
        public static readonly SkeletonDescription AllInOne = S("nflb", [.. Legacy, "iv_ko_c_l", "iv_shiri_l", "nf_ear_a", "nf_ear_b",
            "iv_kyokin_phys_l", "nf_tail", "ya_shiri_phys_l", "n_hara_noanim_trans"]);
    }

    private static void Standards(Action<bool, string> check)
    {
        var game = Rigs.Game;
        check(AnimationBones.IsStandardLayout(game, game, SkeletonStandard.Vanilla) &&
              AnimationBones.IsStandardLayout(Rigs.Ivcs, game, SkeletonStandard.Ivcs) &&
              AnimationBones.IsStandardLayout(Rigs.Yas, game, SkeletonStandard.IvcsYas),
            "the game's skeleton, IVCS with the game's late bone last, and YAS without it are standard layouts");
        check(!AnimationBones.IsStandardLayout(Rigs.Ivcs, game, SkeletonStandard.IvcsYas) &&
              !AnimationBones.IsStandardLayout(Rigs.Yas, game, SkeletonStandard.Ivcs) &&
              !AnimationBones.IsStandardLayout(Rigs.Ivcs, game, SkeletonStandard.Vanilla),
            "a layout counts only as the standard whose bone groups it has");
        check(Enum.GetValues<SkeletonStandard>().All(s => !AnimationBones.IsStandardLayout(Rigs.AllInOne, game, s)),
            "an all-in-one rig with bones outside every group is no standard layout");
        var shuffled = Rigs.Ivcs with { Fingerprint = "shuffled", Bones = [Rigs.Ivcs.Bones[0], Rigs.Ivcs.Bones[6], .. Rigs.Ivcs.Bones.RemoveAt(6).Skip(1)] };
        check(!AnimationBones.IsStandardLayout(shuffled, game, SkeletonStandard.Ivcs),
            "iv_ bones placed among the game's bones move them off the game's indices, so the layout is refused");

        var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose05_loop.pap", "idle.pap", "hash");
        var source = new SkeletonCandidate(new(SkeletonSourceKind.Mod, resource), Rigs.AllInOne, 0, "");
        StandardSkeleton[] all = [new(SkeletonStandard.Vanilla, game, "Game data"), new(SkeletonStandard.Ivcs, Rigs.Ivcs, "IVCS"),
            new(SkeletonStandard.IvcsYas, Rigs.Yas, "YAS")];
        // An earlier repair onto the all-in-one live rig tracked every one of its bones.
        var everyBone = new SkeletonResolution(SkeletonResolutionState.Matched, [source], source,
            TrackBones: [.. Enumerable.Range(0, Rigs.AllInOne.Bones.Length).Select(i => (short)i)], Standards: [.. all]);
        var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 1, "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", Resolution: everyBone);

        check(AnimationBones.Outside(clip, SkeletonStandard.Vanilla)!.Value.SequenceEqual(
                  ["iv_ko_c_l", "iv_shiri_l", "nf_ear_a", "nf_ear_b", "iv_kyokin_phys_l", "nf_tail", "ya_shiri_phys_l"]) &&
              AnimationBones.Outside(clip, SkeletonStandard.Ivcs)!.Value.SequenceEqual(["nf_ear_a", "nf_ear_b", "nf_tail", "ya_shiri_phys_l"]) &&
              AnimationBones.Outside(clip, SkeletonStandard.IvcsYas)!.Value.SequenceEqual(["nf_ear_a", "nf_ear_b", "nf_tail"]),
            "each preset unticks the animated bones outside its groups, keeping the game's late bone as vanilla");
        check(AnimationBones.Outside(clip with { Resolution = everyBone with { Standards = [] } }, SkeletonStandard.Vanilla) == null,
            "presets are unavailable until the game's own skeleton names the vanilla bones");

        var vanillaOnly = AnimationBones.Outside(clip, SkeletonStandard.Vanilla)!.Value;
        check(AnimationBones.HighestIndex(clip, []) == 13 && AnimationBones.HighestIndex(clip, vanillaOnly) == 13,
            "trimming in the source's own layout keeps the game's late bone at the all-in-one rig's high index");
        check(AnimationBones.HighestIndex(clip, vanillaOnly.Add("n_hara_noanim_trans")) == 5 &&
              AnimationBones.HighestIndex(clip, [.. AnimationBones.Animated(clip)]) == -1,
            "the highest index follows the kept bones, and is -1 when none is kept");

        check(AnimationBones.DefaultRepairTarget(clip, []) == SkeletonStandard.IvcsYas,
            "with bones no standard has, repair defaults to the largest standard found");
        check(AnimationBones.DefaultRepairTarget(clip, vanillaOnly) == SkeletonStandard.Vanilla &&
              AnimationBones.DefaultRepairTarget(clip, AnimationBones.Outside(clip, SkeletonStandard.Ivcs)!.Value) == SkeletonStandard.Ivcs,
            "after a preset, repair defaults to the smallest standard that has every kept bone");
        check(AnimationBones.DefaultRepairTarget(clip with { Resolution = everyBone with { Standards = [] } }, []) == null,
            "without standard skeletons there is no repair target");

        var onVanilla = AnimationBones.Plan(clip, SkeletonStandard.Vanilla, [])!;
        check(onVanilla.Kept.SequenceEqual([.. Rigs.Legacy, "n_hara_noanim_trans"]) && onVanilla.Dropped.Length == 7 && onVanilla.HighestIndex == 6,
            "a repair onto the game's skeleton keeps its bones at the game's indices and leaves the rest out");
        var onYas = AnimationBones.Plan(clip, SkeletonStandard.IvcsYas, ["iv_shiri_l"])!;
        check(onYas.Kept.Length == 9 && !onYas.Kept.Contains("iv_shiri_l") && onYas.Dropped.SequenceEqual(["nf_ear_a", "nf_ear_b", "nf_tail", "n_hara_noanim_trans"]) &&
              onYas.HighestIndex == 9,
            "a repair onto IVCS + YAS moves YAS's bone back to its standard index, skips unticked bones and drops bones YAS lacks");
        check(AnimationBones.Plan(clip with { Resolution = everyBone with { Standards = [all[0]] } }, SkeletonStandard.Ivcs, []) == null,
            "a standard that was not found has no repair plan");
    }
}
