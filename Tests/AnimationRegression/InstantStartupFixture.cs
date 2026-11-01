using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;

/// <summary>
/// Make startup instant: a loop's startup replaced by the game's idle held for the shortest length, saved in
/// the loop's mod. The idle's pose goes onto the startup's skeleton by bone name, the startup's later events
/// go, and the new startup may be added to the loop's mod beside the loop, nowhere else.
/// </summary>
internal static class InstantStartupFixture
{
    private const string Loop = "chara/human/c0101/animation/a0001/bt_common/emote/pose01_loop.pap";
    private const string Startup = "chara/human/c0101/animation/a0001/bt_common/emote/pose01_start.pap";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Pose(check, reject);
        WindUp(check, reject);
        Document(check);
        Paths(check);
        Mapping(check);
        UndoOrder(check);
        LoopRace(check, reject);
    }

    /// <summary>
    /// A loop whose file is another race's (a Midlander loop under a Miqo'te path) is read rebuilt for the startup's race, as
    /// the game converts it: read as it is, its longer bones went into the startup and the character grew into the loop.
    /// </summary>
    private static void LoopRace(Action<bool, string> check, Action<Action, string> reject)
    {
        var skeleton = new SkeletonDescription("skeleton", "print", [new("n_root", -1, 0, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One))], [], [], []);
        var standard = new StandardSkeleton(SkeletonStandard.Vanilla, skeleton, "game");
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("skeleton.sklb", "skeleton.sklb", "hash")), skeleton, 1, "");
        var clip = Capture().Clip with
        {
            Resolution = new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, ActorModel: "c0801", ActorStandards: [standard]),
        };
        var loop = new AnimationTimelineSource(Capture() with { Clip = clip }, false, clip, new AnimationResource(Loop, Loop, "hash"), [], 0, [], null, null)
            { FileModel = "c0101" };
        check(AnimationEditService.WindUpLoopRebuild(loop, "c0801") is { Model: "c0801" } rebuild && ReferenceEquals(rebuild.Target, standard),
            "a wind-up reads another race's loop rebuilt for the startup's race");
        check(AnimationEditService.WindUpLoopRebuild(loop with { FileModel = "c0801" }, "c0801") == null &&
              AnimationEditService.WindUpLoopRebuild(loop with { FileModel = null }, "c0801") == null,
            "a loop of the startup's race, or one whose header names no race, is read as it is");
        var searching = clip with { Resolution = clip.Resolution! with { State = SkeletonResolutionState.Searching } };
        check(AnimationEditService.WindUpLoopRebuild(loop with { Clip = searching }, "c0801") == null,
            "a loop whose skeleton is still being matched is left to the read, which says so");
        string? refused = null;
        try { AnimationEditService.WindUpLoopRebuild(loop, "c1101"); }
        catch (InvalidOperationException e) { refused = e.Message; }
        check(refused != null, "a loop that can't be rebuilt for the startup's race stops the wind-up");
    }

    /// <summary>An instant startup added to the loop's mod waits for a later in-place save of that startup to be undone first.</summary>
    private static void UndoOrder(Action<bool, string> check)
    {
        static AnimationFileChange Change(string root, string relative, string? addedFor) =>
            new(Startup, root + "/" + relative, "Mod", root, relative, "", "hash", "", "staged", AddedFor: addedFor);
        var at = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        AnimationEditJournal Journal(AnimationFileChange file, int minutes, string state = "Completed") =>
            new() { Id = Guid.NewGuid(), State = state, Files = [file], CreatedUtc = at.AddMinutes(minutes) };
        var instant = Journal(Change("root", "instant_edit/start.pap", Loop), 0);
        var savedOver = Journal(Change("root", "instant_edit/start_1.pap", null) with { RenamedFromRelativePath = "instant_edit/start.pap" }, 5);
        check(AnimationEditService.LaterEditUsesAdded(instant, [instant, savedOver]),
            "undoing an instant startup waits for a later in-place save of that startup");
        check(!AnimationEditService.LaterEditUsesAdded(instant, [instant, Journal(savedOver.Files[0], 5, "Undone")]) &&
              !AnimationEditService.LaterEditUsesAdded(instant, [instant, Journal(savedOver.Files[0], -5)]) &&
              !AnimationEditService.LaterEditUsesAdded(instant, [instant, Journal(Change("other", "start.pap", null), 5)]),
            "an undone save, an earlier one, or one of the same startup in another mod doesn't hold it up");
    }

    private static void Pose(Action<bool, string> check, Action<Action, string> reject)
    {
        // The startup's skeleton has a bone the game's lacks (a skeleton mod's iv_ bone).
        var rig = Rig("startup", "n_root", "j_kosi", "iv_extra");
        var game = Rig("game", "n_root", "j_kosi");
        var bent = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f);
        var own = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f);
        var startup = Clip(rig, 31, f => [Identity, Turned(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, f / 30)), Turned(own)]);
        var idle = Clip(game, 61, _ => [Identity, Turned(bent)]);
        var pose = MotionHold.Borrowed(startup, idle);
        check(Close(pose[1].Rotation, bent) && Close(pose[2].Rotation, own) && Close(pose[0].Rotation, Quaternion.Identity),
            "the idle's pose goes onto the startup's bones by name; a bone the game lacks keeps the startup's first pose");
        var still = MotionHold.Still(startup, pose, 0, TimelineDocument.MinLength);
        check(still.Samples == TimelineDocument.MinLength + 1 && Math.Abs(still.Duration - TimelineDocument.MinLength / 30f) < 1e-6f &&
              Enumerable.Range(0, still.Samples).All(s => Close(still.Get(s, 1).Rotation, bent)),
            "the idle's pose is held over the shortest length");
        reject(() => MotionHold.Still(startup, [MotionTransform.Identity], 0, 2), "a pose for another skeleton is refused");
    }

    /// <summary>The wind-up: from the idle into the loop, turning first and rolling later, ending on the loop's own first frames.</summary>
    private static void WindUp(Action<bool, string> check, Action<Action, string> reject)
    {
        // The startup's arm has its forearm along X and a helper bone just off its origin; iv_extra only the startup has.
        SkeletonDescription Arm(string name, bool extra) => new(name, name,
        [
            new("n_root", -1, 0, Identity),
            new("j_kosi", 0, 0, Identity),
            new("j_ude_a", 1, 0, Identity),
            new("n_hkata", 2, 0, Identity with { Position = new Vector3(0, 0, .001f) }),
            new("j_ude_b", 2, 0, Identity with { Position = new Vector3(.3f, 0, 0) }),
            .. extra ? [new SkeletonBone("iv_extra", 1, 0, Identity)] : Array.Empty<SkeletonBone>(),
        ], [], [], []);
        var rig = Arm("startup", true);
        var swing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 80 * MathF.PI / 180);
        var reached = swing * roll;
        var kept = Turned(Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f));
        // The loop: the arm raised and rolled, the hips breathing over its 30 frames.
        var loop = Clip(Arm("loop", false), 31, f =>
            [Identity, Turned(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f * MathF.Sin(f / 30f * MathF.Tau))), Turned(reached), Identity, Identity]);
        var startup = Clip(rig, 3, _ => [Identity, Identity, Identity, Identity, Identity, kept]);
        var from = MotionHold.Borrowed(startup, Clip(rig, 2, _ => [Identity, Identity, Identity, Identity, Identity, kept]));
        const int frames = 20, fade = 10;
        var wound = MotionWindUp.Build(startup, from, loop, frames, fade);
        check(wound.Samples == frames + fade + 1 && Math.Abs(wound.Duration - (frames + fade) / 30f) < 1e-6f,
            "the wind-up lasts its frames and the fade");
        var loopIndex = MotionEvaluator.Index(loop.Skeleton);
        bool Matches(int sample, float loopFrame) => rig.Bones.Select((bone, b) => (bone, b)).All(pair =>
            !loopIndex.TryGetValue(pair.bone.Name, out var l) || Close(wound.Get(sample, pair.b).Rotation, loop.Pose(loopFrame)[l].Rotation));
        check(Enumerable.Range(0, rig.Bones.Length).All(b => Close(wound.Get(0, b).Rotation, from[b].Rotation)),
            "it starts on the idle");
        check(Enumerable.Range(0, fade + 1).All(k => Matches(frames + k, k)),
            "it reaches the loop's first frame and then plays the loop's first frames, so the game's fade shows the same pose");
        check(Close(wound.Get(frames + fade, 5).Rotation, kept.Rotation), "a bone the loop lacks stays where the idle has it");

        // Rolled about the forearm's line (from j_ude_b, not the helper just off its origin): none in the first quarter.
        float Roll(int sample)
        {
            var q = wound.Get(sample, 2).Rotation;
            var twist = MotionFixes.Twist(q, Vector3.UnitX);
            return 2 * MathF.Acos(Math.Clamp(MathF.Abs(twist.W), 0, 1)) * 180 / MathF.PI;
        }
        var quarter = frames / 4;
        check(Enumerable.Range(1, quarter).All(k => Roll(k) < .5f) && Roll(quarter) < .5f &&
              2 * MathF.Acos(Math.Clamp(MathF.Abs(wound.Get(quarter, 2).Rotation.W), 0, 1)) > 5 * MathF.PI / 180,
            "the arm turns from the start but rolls only after a quarter of the wind-up");
        check(Roll(frames) > 79 && Roll(frames) < 81, "and has rolled all the way when the loop takes over");

        // Longer than the loop: the target is the loop counted back from the hand-off, wrapping round.
        var longer = MotionWindUp.Build(startup, from, loop, 45, fade);
        check(Enumerable.Range(0, fade + 1).All(k => Close(longer.Get(45 + k, 1).Rotation, loop.Pose(k)[1].Rotation)) &&
              Close(longer.Get(44, 1).Rotation, MotionEvaluator.Slerp(Quaternion.Identity, loop.Pose(29)[1].Rotation, MotionCutter.Rise(44 / 45f))),
            "a wind-up longer than the loop aims at the loop's frames before the hand-off, wrapping round");

        reject(() => MotionWindUp.Build(startup, from, loop, 0, fade), "a wind-up of no frames is refused");
        reject(() => MotionWindUp.Build(startup, [MotionTransform.Identity], loop, frames, fade), "a pose for another skeleton is refused");
    }

    private static void Document(Action<bool, string> check)
    {
        TimelineEvent Event(string key, string magic, TimelineLane lane, int start, int duration, TimelineAccess access) =>
            new(key, magic, lane, start, duration, true, "", access, null, false, ImmutableDictionary<string, double>.Empty);
        var document = new TimelineDocument(90, 90, null,
        [
            Event("t1", "C009", TimelineLane.Motion, 0, 90, TimelineAccess.Locked),
            Event("t2", "C063", TimelineLane.Prop, 0, 90, TimelineAccess.Editable),
            Event("t3", "C012", TimelineLane.Sound, 40, 0, TimelineAccess.Editable),
            Event("t4", "C197", TimelineLane.Other, 60, 0, TimelineAccess.Locked),
        ], 1);
        var still = new MotionReplacement([], "", "", TimelineDocument.MinLength, AnimationEditService.InstantStartupAction, "", null);
        var instant = AnimationEditService.InstantStartupDocument(document, still);
        var keys = instant.Events.Select(e => e.Key).ToArray();
        check(instant.Length == TimelineDocument.MinLength && instant.Replacement == still && !instant.Stretched &&
              !AnimationBakeRules.ChangesMotion(instant) && AnimationBakeRules.NewMotion(instant),
            "the instant startup lasts the shortest length, played by the held idle without a bake");
        check(keys.SequenceEqual(["t1", "t2", "t4"]) && instant.Events.All(e => e.Start < instant.Length) &&
              instant.Events[0].Duration == instant.Length,
            "the startup's first events and the locked ones stay, squeezed into it, and its motion lasts all of it; later sounds go");
    }

    private static void Paths(Action<bool, string> check)
    {
        var (idle, skeleton) = AnimationEditService.VanillaIdlePaths("c1401");
        check(idle == "chara/human/c1401/animation/a0001/bt_common/resident/idle.pap" &&
              skeleton == "chara/human/c1401/skeleton/base/b0001/skl_c1401b0001.sklb",
            "the race's own idle and body skeleton are read from the game");

        // c0201 borrows c0801's pose02; the game has no pose04 for it.
        var idles = AnimationEditService.GameIdles("c0201", path => path.Replace("c0201/animation/a0001/bt_common/emote/pose02", "c0801/animation/a0001/bt_common/emote/pose02"),
            path => !path.Contains("pose04") && (path.Contains("resident/idle") || path.Contains("emote/pose0")));
        check(idles.Select(i => i.Name).SequenceEqual(["Standing idle", "Idle pose 1", "Idle pose 2", "Idle pose 3", "Idle pose 5", "Idle pose 6", "Idle pose 7", "Idle pose 8", "Idle pose 9"]) &&
              idles[2].GamePath == "chara/human/c0801/animation/a0001/bt_common/emote/pose02_loop.pap" &&
              idles[0].GamePath == "chara/human/c0201/animation/a0001/bt_common/resident/idle.pap",
            "the idle layer lists the race's standing idle and each /changepose idle the game has, at the copy the race loads");
        AnimationPap.Entry[] standing = [new("cbna_add_dmg_f", 0, 0, 0), new("cbnm_id0", 0, 1, 0)];
        check(AnimationEditService.DonorEntry(standing, AnimationEditService.StandingIdleEntry)?.Name == "cbnm_id0" &&
              AnimationEditService.DonorEntry(standing, null)?.Name == "cbnm_id0" &&
              AnimationEditService.DonorEntry([new("face", 0, 0, 1), new("cbem_pose01_2lp", 0, 1, 0)], null)?.Name == "cbem_pose01_2lp",
            "the game's idle decodes the idle, not the damage flinch listed before it; a face entry is never the donor");
        var watched = Capture() with { LoadedResourcePaths = ["chara/human/c0101/skeleton/face/f0001/skl_c0101f0001.sklb", "chara/human/c1401/skeleton/base/b0001/skl_c1401b0001.sklb"] };
        var source = new AnimationTimelineSource(watched, false, watched.Clip, new AnimationResource(Loop, Loop, "hash"), [], 0, [], null, null) { FileModel = "c0101" };
        check(AnimationEditService.CharacterRace(source) == "c1401" && AnimationEditService.CharacterRace(source with { Capture = Capture() }) == "c0101",
            "the game's idles are listed for the watched character's body skeleton, else the clip's race");

        var timeline = new AnimationTimelineEdit(true, 0, null);
        var request = Request(Capture(), timeline);
        check(AnimationCommitService.AddedGamePath(Startup, request) && !AnimationCommitService.AddedGamePath(Startup) &&
              !AnimationCommitService.AddedGamePath(Loop, request) &&
              !AnimationCommitService.AddedGamePath(Startup, request with { Timeline = timeline with { Startup = false } }),
            "only a startup edit adds an animation, and only its own startup");

        var mod = new AnimationResource(Loop, @"C:\Mods\Pose\files\loop.pap", "hash", "Pose", @"C:\Mods\Pose", "files/loop.pap", "Pose");
        check(AnimationEditService.InstantStartupBlock(Capture(mod)) == null, "a loop from a mod with a matched startup can be made instant");
        check(AnimationEditService.InstantStartupBlock(Capture(mod) with { Startup = null }) != null &&
              AnimationEditService.InstantStartupBlock(Capture()) != null,
            "a loop without a startup, or from the game's files, can't");
    }

    private static void Mapping(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEditInstantStartupRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var meta = ModMetadata.CreateV4ModMetadata("Pose", "Author", "Description", "1.0", new JsonObject
            {
                ["Files"] = new JsonObject(),
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            });
            meta["Groups"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Style", ["Type"] = "Single",
                ["Options"] = new JsonArray(
                    new JsonObject { ["Name"] = "A", ["Files"] = new JsonObject { [Loop] = "a/loop.pap" } },
                    new JsonObject { ["Name"] = "B", ["Files"] = new JsonObject { [Loop] = "b/loop.pap" } },
                    new JsonObject { ["Name"] = "None", ["Files"] = new JsonObject() }),
            });
            File.WriteAllText(Path.Combine(root, "meta.json"), meta.ToJsonString());
            const string relative = AnimationCommitService.AddedFolder + Startup;
            ModMetadata.AddAnimationResourceMapping(root, Loop, Startup, relative);
            var options = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!["Groups"]![0]!["Options"]!.AsArray();
            check(options.Take(2).All(o => o!["Files"]![Startup]?.GetValue<string>() == relative) && options[2]!["Files"]![Startup] == null &&
                  ModMetadata.ModMapsGamePath(root, Startup),
                "the new startup plays in every option that plays the loop, and nowhere else");
            ModMetadata.RemoveAnimationResourceMapping(root, Startup, relative);
            check(!ModMetadata.ModMapsGamePath(root, Startup), "undo takes the startup's mappings away again");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static AnimationCapture Capture(AnimationResource? loop = null)
    {
        var matched = new SkeletonResolution(SkeletonResolutionState.Matched, []);
        var clip = new AnimationClip(Loop, "pose01_loop", 0, 0, 1, "", IsLoop: true);
        var startup = new AnimationClip(Startup, "pose01_start", 0, 0, 2, "", Resolution: matched);
        var game = new AnimationResource(Loop, Loop, "hash");
        return new AnimationCapture("capture", 1, 1, Guid.Empty, "Collection", "Pose", clip, startup, [], [loop ?? game], DateTime.UtcNow, true);
    }

    private static AnimationBakeRequest Request(AnimationCapture capture, AnimationTimelineEdit timeline) =>
        new(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false, AnimationOperation.EditTimeline, Timeline: timeline);

    private static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static BoneTransform Turned(Quaternion rotation) => Identity with { Rotation = rotation };

    private static SkeletonDescription Rig(string name, params string[] bones) =>
        new(name, name, [.. bones.Select((bone, i) => new SkeletonBone(bone, (short)(i - 1), 0, Identity))], [], [], []);

    private static MotionClip Clip(SkeletonDescription rig, int samples, Func<int, BoneTransform[]> pose) =>
        MotionClip.FromPoses(rig, (samples - 1) / (float)TimelineDocument.Fps, [.. Enumerable.Range(0, samples).Select(pose)]);

    private static bool Close(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 1 - 1e-5f;
}
