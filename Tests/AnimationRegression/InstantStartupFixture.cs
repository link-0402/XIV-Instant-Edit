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
        Document(check);
        Paths(check);
        Mapping(check);
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
        return new AnimationCapture("capture", 1, 1, Guid.Empty, "Collection", "Pose", clip, startup, [], [loop ?? game],
            new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
    }

    private static AnimationBakeRequest Request(AnimationCapture capture, AnimationTimelineEdit timeline) =>
        new(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
            AnimationOperation.EditTimeline, Timeline: timeline);

    private static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static BoneTransform Turned(Quaternion rotation) => Identity with { Rotation = rotation };

    private static SkeletonDescription Rig(string name, params string[] bones) =>
        new(name, name, [.. bones.Select((bone, i) => new SkeletonBone(bone, (short)(i - 1), 0, Identity))], [], [], []);

    private static MotionClip Clip(SkeletonDescription rig, int samples, Func<int, BoneTransform[]> pose) =>
        MotionClip.FromPoses(rig, (samples - 1) / (float)TimelineDocument.Fps, [.. Enumerable.Range(0, samples).Select(pose)]);

    private static bool Close(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 1 - 1e-5f;
}
