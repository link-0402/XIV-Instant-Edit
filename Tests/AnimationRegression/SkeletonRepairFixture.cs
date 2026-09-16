using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class SkeletonRepairFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        BoneTransform T(float x = 0, float y = 0) => new(new(x, y, 0), Quaternion.Identity, Vector3.One);
        SkeletonBone B(string name, short parent, float x = 0, float y = 0) => new(name, parent, 0, T(x, y));
        SkeletonDescription S(string fingerprint, params SkeletonBone[] bones) => new("rig", fingerprint, bones.ToImmutableArray(), [], [], []);
        var source = S("source", B("root", -1), B("hand", 0, 1), B("extra", 0));
        var target = S("target", B("root", -1), B("hand", 0, 2));
        var channels = new AnimationChannels("rig", [0, 1, 2], [], [], 3, 0);
        check(AnimationSkeleton.Fits(channels, source) && !AnimationSkeleton.Fits(channels, target), "predictive reference channels reject the short live skeleton without sampling");
        check(!AnimationSkeleton.Fits(channels with { ReferenceFloats = 1 }, source), "predictive reference float mismatch is rejected");
        check(!AnimationSkeleton.Fits(channels with { Bones = [0, 0] }, source), "duplicate source bindings cannot be used for matching");
        reject(() => AnimationSkeleton.Validate(S("bad", B("root", -1), B("root", 0))), "duplicate skeleton names are rejected");
        reject(() => AnimationSkeleton.Validate(S("bad", B("root", 0))), "cyclic skeleton ancestry is rejected");
        var retarget = new AnimationRetarget(source, target, channels);
        var pose = new[] { T(), T(1), T() };
        check(retarget.Map(pose)[1] == T(2), "reference-only extra tracks can be omitted while retaining target proportions");
        pose[1] = T(1.5f);
        check(Vector3.Distance(retarget.Map(pose)[1].Position, new(2.5f, 0, 0)) < 1e-6, "rest-relative translation retains target bone length");
        pose[0] = T(4);
        check(retarget.Map(pose)[0].Position.X == 4, "root-motion translation distance is retained");
        pose[2] = T(.1f);
        reject(() => retarget.Map(pose), "a constant non-reference transform on a missing bone blocks retargeting");
        pose[2] = T();
        var reordered = S("reordered", B("root", -1), B("extra", 0), B("hand", 0, 2));
        var reorderMap = new AnimationRetarget(source, reordered, channels);
        check(reorderMap.BoneMap.SequenceEqual(new[] { 0, 2, 1 }) && reorderMap.Map(pose)[2].Position.X == 2.5f, "bone names map reordered indices to the correct destination");
        var helpers = S("helpers", B("root", -1), B("helper", 0, 1), B("hand", 1, 1));
        var helperMap = new AnimationRetarget(helpers, target, channels);
        var helperPose = new[] { T(), T(1), T(1.5f) };
        check(Math.Abs(helperMap.Map(helperPose)[1].Position.X - 2.5f) < 1e-6, "reference-only intermediate helpers collapse through the shared ancestor");
        helperPose[1] = T(2);
        reject(() => helperMap.Map(helperPose), "a moving missing helper cannot be silently dropped");
        var inserted = new AnimationRetarget(target, helpers, channels with { Bones = [0, 1], ReferenceBones = 2 });
        check(inserted.MapTracks([0, 1]).SequenceEqual(new short[] { 0, 2 }),
            "repair rewrites numeric animation tracks around inserted destination helper bones");
        check(Math.Abs(inserted.Map(new[] { T(), T(2.5f) })[2].Position.X - 1.5f) < 1e-6, "target reference helpers are removed from collapsed output to recover local transforms");
        var wrongParent = S("parent", B("root", -1), B("extra", 0), B("hand", 1, 2));
        check(Math.Abs(new AnimationRetarget(source, wrongParent, channels).Map(pose)[2].Position.X - 2.5f) < 1e-6,
            "meaningful reparented bones preserve global rest-relative motion in destination-local space");
        var sourcePhysics = S("source-physics", B("root", -1), B("j_sebo_b", 0), B("j_sebo_c", 1), B("iv_kyokin_phys_l", 1));
        var targetPhysics = S("target-physics", B("root", -1), B("j_sebo_b", 0), B("j_sebo_c", 1), B("iv_kyokin_phys_l", 2));
        var physicsMap = new AnimationRetarget(sourcePhysics, targetPhysics, channels with { Bones = [0, 1, 2, 3], ReferenceBones = 4 });
        var physicsPose = new[] { T(), T(.25f), T(), T() };
        check(physicsMap.Map(physicsPose)[3] == targetPhysics.Bones[3].Reference,
            "a reference-only custom physics bone may keep its target rest transform when an animated ancestor differs");
        physicsPose[3] = T(.1f);
        var mappedPhysics = physicsMap.Map(physicsPose);
        check(Math.Abs(mappedPhysics[3].Position.X - .1f) < 1e-6,
            "an animated custom physics bone survives a j_sebo_b to j_sebo_c ancestry change");
        var reversePhysics = new AnimationRetarget(targetPhysics, sourcePhysics,
            channels with { Bones = [], Floats = [], Partitions = [], ReferenceBones = null, ReferenceFloats = null });
        var sourceRoundTrip = reversePhysics.Map(mappedPhysics);
        check(Vector3.Distance(sourceRoundTrip[1].Position, physicsPose[1].Position) < 1e-6 &&
              Vector3.Distance(sourceRoundTrip[3].Position, physicsPose[3].Position) < 1e-6,
            "live-space edits on reparented bones round-trip to canonical source space for game playback");
        var rotatedRest = T(2) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };
        var rotation = AnimationRetarget.Transfer(T(1.5f), T(1), rotatedRest);
        check(Vector3.Distance(rotation.Position, new(2, .5f, 0)) < 1e-5 && Math.Abs(Quaternion.Dot(rotation.Rotation, rotatedRest.Rotation)) > .99999f,
            "reference orientation transforms motion deltas into target axes");
        reject(() => AnimationRetarget.Transfer(T(), T() with { Scale = Vector3.Zero }, T()), "singular rest scales block retargeting");
        var reference = rotatedRest with { Scale = new(2, 3, 4) };
        var modified = new BoneTransform(new(3, 4, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .6f), new(3, 5, 7));
        foreach (sbyte hint in new sbyte[] { 1, 2 })
        {
            var encoded = AnimationRetarget.Encode(modified, reference, hint);
            var rotationAfterBlend = hint == 2 ? reference.Rotation * encoded.Rotation : encoded.Rotation * reference.Rotation;
            check(Vector3.Distance(encoded.Position + reference.Position, modified.Position) < 1e-6 &&
                Vector3.Distance(encoded.Scale * reference.Scale, modified.Scale) < 1e-6 && Math.Abs(Quaternion.Dot(rotationAfterBlend, modified.Rotation)) > .99999f,
                $"blend hint {hint} reconstructs retargeted translation, scale and noncommuting rotation against the destination rest pose");
        }
        check(AnimationRetarget.Encode(modified, reference, 0) == modified, "normal animation output preserves the retargeted pose directly");
        check(AnimationRetarget.MapFloat(.75f, .5f, 1, 0) == .75f && AnimationRetarget.MapFloat(.75f, .5f, 1, 2) == 1.25f,
            "float retargeting preserves normal values and transfers additive deltas to target references");
        reject(() => AnimationSkeleton.Validate(source with { Bones = default }), "incomplete cached skeleton arrays fail validation");

        var sf = source with { FloatNames = ["blink", "smile"], ReferenceFloats = [0f, .5f], Partitions = [new("body", 0, 3)] };
        var tf = reordered with { FloatNames = ["smile", "blink"], ReferenceFloats = [.5f, 0f], Partitions = [new("body", 0, 3)] };
        var fc = channels with { Floats = [0, 1], ReferenceFloats = 2, Partitions = [0] };
        check(new AnimationRetarget(sf, tf, fc).FloatMap.SequenceEqual(new short[] { 1, 0 }), "floating channels map by exact names");
        reject(() => new AnimationRetarget(sf, target, fc), "missing floating channels block output");
        reject(() => new AnimationRetarget(sf, tf with { Partitions = [new("other", 0, 3)] }, fc), "missing destination partitions block output");
        reject(() => new AnimationRetarget(sf, tf with { Partitions = [new("body", 0, 1)] }, fc), "partition membership must cover mapped source bones");

        const string game = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";
        SkeletonCandidate Candidate(SkeletonDescription s, string file) => new(new(SkeletonSourceKind.Mod,
            new(game, file, file, "Example mod", "root", file)), s, 0, "");
        var candidate = Candidate(source, "first.sklb");
        var malformed = Candidate(S("invalid", B("root", 0)), "invalid-container.sklb");
        var failures = new List<string>();
        var loaded = AnimationSkeletonIndex.ReadCandidatesAsync(new[] { malformed.Source, candidate.Source },
            s => s == malformed.Source ? Task.FromException<ImmutableArray<SkeletonCandidate>>(new InvalidDataException("Invalid Havok animation container.")) : Task.FromResult(ImmutableArray.Create(candidate)),
            (s, e) => failures.Add(e.Message), CancellationToken.None).GetAwaiter().GetResult();
        check(loaded.Count == 1 && loaded[0] == candidate && failures.SequenceEqual(new[] { "Invalid Havok animation container." }),
            "invalid Havok container exceptions stay local to the candidate and scanning continues");
        check(AnimationSkeletonIndex.Rank(channels, [malformed, candidate], source, game).Selected?.Skeleton.Fingerprint == source.Fingerprint,
            "a malformed skeleton candidate does not interrupt matching the next valid skeleton");
        var distinctRest = source with { Fingerprint = "different", Bones = source.Bones.SetItem(1, B("hand", 0, 3)) };
        var other = Candidate(distinctRest, "second.sklb");
        var ambiguity = AnimationSkeletonIndex.Rank(channels, [candidate, other], source, game);
        check(ambiguity.State == SkeletonResolutionState.Ambiguous && ambiguity.Selected == null, "equal-ranked skeletons with different reference poses require a picker");
        check(AnimationSkeletonIndex.Rank(channels, [candidate, other], source, game, AnimationSkeletonIndex.SelectionId(other)).Selected == other with
            { Rank = ambiguity.Candidates.Single(c => c.Skeleton.Fingerprint == "different").Rank, Rationale = ambiguity.Candidates.Single(c => c.Skeleton.Fingerprint == "different").Rationale },
            "manual choice selects a compatible tied skeleton");
        check(AnimationSkeletonIndex.Rank(channels, [candidate, candidate with { Source = candidate.Source with { Resource = candidate.Source.Resource with { ResolvedPath = "alias.sklb" } } }], source, game).Candidates.Length == 1,
            "duplicate skeleton content is deduplicated before ambiguity checks");
        var library = AnimationSkeletonIndex.DeduplicateLibrary([candidate,
            candidate with { Source = candidate.Source with { Resource = candidate.Source.Resource with { ResolvedPath = "alias.sklb" } } }]);
        check(library.Length == 1 && library[0].Sources.Length == 2 && library[0].CandidateFor(game).Source.Resource.GamePath == game,
            "the session skeleton library retains aliases without duplicate skeleton entries");
        check(AnimationSkeletonIndex.Rank(channels, [Candidate(target, "short.sklb")], source, game).State == SkeletonResolutionState.Incompatible,
            "no matching source reports per-clip incompatibility");
        var closestRepair = AnimationSkeletonIndex.Rank(channels with { ReferenceBones = source.Bones.Length + 1 }, [candidate], source, game);
        check(closestRepair is { State: SkeletonResolutionState.Incompatible, Selected: null } && closestRepair.Candidates.IsEmpty,
            "an incompatible reference pose cannot be offered as a repair source that the decoder will reject");
        check(AnimationSkeletonIndex.Rank(channels, [candidate, other], source, game, "removed").State == SkeletonResolutionState.Ambiguous,
            "changed source fingerprints invalidate a previous manual selection");
        var changedBytes = candidate with { Source = candidate.Source with { Resource = candidate.Source.Resource with { Hash = "changed-content" } } };
        check(AnimationSkeletonIndex.Rank(channels, [changedBytes, other], source, game, AnimationSkeletonIndex.SelectionId(candidate)).State == SkeletonResolutionState.Ambiguous,
            "changed file content invalidates a manual choice even when the decoded skeleton fingerprint is unchanged");
        var clip = new AnimationClip("example.pap", "walk", 0, 0, 1, game, target.Fingerprint, target, ambiguity, "motion");
        var capture = new AnimationCapture("walk", 1, 1, Guid.NewGuid(), "Test", "Walk", clip, clip,
            ["example.pap"], [], new(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true, PoseUnavailableReason: "LivePose unavailable");
        var recent = capture with { Id = "idle", DisplayName = "Idle" };
        var merged = AnimationObserver.MergeHistory([capture], [recent]);
        check(merged.Length == 2 && merged[0].Playing && !merged[1].Playing && merged[0].Clip.Resolution?.State == SkeletonResolutionState.Ambiguous,
            "a mismatched clip remains listed and playing without LivePose while old clips become recent");
        var staleSource = new AnimationResource(clip.GamePath, "resolved.pap", "before-edit-hash");
        var sourcedCapture = capture with { Sources = [staleSource] };
        var refreshedByEdit = AnimationObserver.ApplyUpdatedSources([sourcedCapture], "walk", [staleSource with { Hash = "after-edit-hash" }]);
        var untouchedCapture = AnimationObserver.ApplyUpdatedSources([sourcedCapture], "other-clip", [staleSource with { Hash = "after-edit-hash" }]);
        check(refreshedByEdit.Single().Sources.Single().Hash == "after-edit-hash" && untouchedCapture.Single().Sources.Single().Hash == "before-edit-hash",
            "an in-place edit's refreshed source hash reaches only its own captured clip, by id and game path");
        var selectedResolution = ambiguity with { State = SkeletonResolutionState.Matched, Selected = ambiguity.Candidates[0] };
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture with { Clip = clip with { Resolution = selectedResolution }, Startup = clip with { Resolution = selectedResolution } },
            AnimationDestination.NewMod, "Repair", true, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.RepairSkeleton);
        var compact = AnimationCommitService.RecoveryRequest(request);
        check(compact.Operation == AnimationOperation.RepairSkeleton && compact.Capture.Clip.Resolution!.Candidates.Length == 1 &&
            compact.Capture.Startup!.Resolution!.Candidates.Length == 1 && compact.Capture.Clip.Resolution.Selected == selectedResolution.Selected &&
            compact.Capture.Clip.TargetSkeleton == target,
            "recovery retains selected source and destination identities without serializing the whole skeleton index");
        var frames = AnimationPoseRules.SampleCount(1, 25);
        check(frames >= 31 && (frames - 1) % 24 == 0, "uniform bake grid includes every source sample even below thirty frames per second");
        // The loop and its startup routinely resolve different source skeletons
        // (a 168- versus 167-bone SKLB, for instance). Only the live rig they are
        // both retargeted onto has to agree, and a reference-pose start has no
        // idle clip at all.
        var startupClip = clip with { GamePath = "example_start.pap", Resolution = selectedResolution };
        var differentSource = startupClip with { SkeletonFingerprint = "other-source" };
        check(AnimationPoseRules.StartupTargetMismatch(clip, differentSource, null, target) == null,
            "a reference-pose startup with a different source skeleton is not a target mismatch");
        check(AnimationPoseRules.StartupTargetMismatch(clip, startupClip, clip, target) == null,
            "a character-idle startup sharing the live rig is accepted");
        check(AnimationPoseRules.StartupTargetMismatch(clip with { TargetSkeleton = source }, startupClip, null, target)
                == "The loop and startup target skeletons are incompatible.",
            "a loop captured against a different live rig is rejected");
        check(AnimationPoseRules.StartupTargetMismatch(clip, startupClip, clip with { Partial = 1 }, target)
                == "The character idle and startup target skeletons are incompatible.",
            "an idle from another partial skeleton is rejected");

        var temp = Path.Combine(Path.GetTempPath(), "ie-skeleton-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllBytes(Path.Combine(temp, "disabled.sklb"), [1]);
            File.WriteAllBytes(Path.Combine(temp, "inactive.sklb"), [2]);
            File.WriteAllText(Path.Combine(temp, "meta.json"), JsonSerializer.Serialize(new { DefaultData = new { Files = new Dictionary<string, string> { [game] = "disabled.sklb" } } }));
            File.WriteAllText(Path.Combine(temp, "group_001.json"), JsonSerializer.Serialize(new { Options = new[] { new { Files = new Dictionary<string, string> { [game] = "inactive.sklb" } } } }));
            var scan = AnimationSkeletonIndex.Scan(new[] { ("Disabled mod", temp) }, CancellationToken.None);
            check(scan.Length == 2 && scan.All(s => s.Resource.GamePath == game && s.Resource.ModDirectory == "Disabled mod"), "registered mod scan includes disabled mods, inactive options and v4 mappings");
            File.Delete(Path.Combine(temp, "inactive.sklb"));
            check(AnimationSkeletonIndex.Scan(new[] { ("Disabled mod", temp) }, CancellationToken.None).Length == 1, "rescan drops removed skeleton files");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { AnimationSkeletonIndex.Scan(new[] { ("Disabled mod", temp) }, cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { check(true, "skeleton scan respects cancellation"); }
            var options = new JsonSerializerOptions { IncludeFields = true };
            var roundtrip = JsonSerializer.Deserialize<SkeletonDescription>(JsonSerializer.Serialize(source, options), options)!;
            check(roundtrip.Bones.SequenceEqual(source.Bones) && roundtrip.Fingerprint == source.Fingerprint, "skeleton cache round-trips reference transforms and hierarchy");
        }
        finally { Directory.Delete(temp, true); }
    }
}
