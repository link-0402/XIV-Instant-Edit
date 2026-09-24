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
}
