using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Animations tab's tools as Timeline functions: LivePose adjustments (live or from a .livepose
/// file) as an offset layer, the loader's animation list, unticked bones and skeleton repair as part
/// of a timeline save, and what such a save refuses. Live display, playback and the bake need the game.
/// </summary>
internal static class TimelineToolsFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        LivePose(check);
        LivePoseFiles(check, reject);
        Library(check);
        Edits(check);
        Saves(check);
        Problems(check);
    }

    private static BoneTransform T(float y = 0) => new(new Vector3(0, y, 0), Quaternion.Identity, Vector3.One);

    // n_root ─ j_kosi ─ j_sebo_a ─ j_kubi
    //                 └ j_asi_a_l
    private static readonly SkeletonDescription Rig = new("rig", "rig", [
        new("n_root", -1, 0, T()), new("j_kosi", 0, 0, T(1)), new("j_sebo_a", 1, 0, T(.1f)),
        new("j_kubi", 2, 0, T(.4f)), new("j_asi_a_l", 1, 0, T(-.1f))], [], [], []);

    private static PoseStack Stack(Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null,
        PoseComponents propagate = PoseComponents.Position | PoseComponents.Rotation, bool ik = false) =>
        new(position ?? Vector3.Zero, rotation ?? Quaternion.Identity, scale ?? Vector3.Zero, propagate,
            new PoseIk(ik, 0, false, 0, 0, 0, 0, 0, Vector3.Zero));

    private static PoseBone Bone(string name, int partial = 0, params PoseStack[] stacks) => new(new PoseBoneId(name, partial), [.. stacks]);

    private static bool Near(Quaternion a, Quaternion b) => 1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) < 1e-6f;

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;

    private static void LivePose(Action<bool, string> check)
    {
        var turnA = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var turnB = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f);
        var result = LivePoseLayer.Offsets([
            Bone("j_kubi", 0, Stack(rotation: turnA), Stack(rotation: turnB, position: new Vector3(0, .01f, 0))),
        ], Rig);
        check(result.Adjusted == 1 && result.Bones.Count == 1 && Near(result.Bones["j_kubi"].Rotation, turnA * turnB) &&
              Near(result.Bones["j_kubi"].Position, new Vector3(0, .01f, 0)) && result.Bones["j_kubi"].Scale == Vector3.One,
            "a bone's stacks add up in order: turns one after the other in its own axes, moves summed in character space");

        var scaled = LivePoseLayer.Offsets([Bone("j_sebo_a", 0, Stack(scale: new Vector3(.25f, 0, 0)))], Rig);
        check(Near(scaled.Bones["j_sebo_a"].Scale, new Vector3(1.25f, 1, 1)) && Near(scaled.Bones["j_kubi"].Scale, new Vector3(.8f, 1, 1)),
            "LivePose's added scale becomes a factor, and with scale kept from the children (its default) the child scales back");

        var kept = LivePoseLayer.Offsets([Bone("j_kosi", 0, Stack(position: new Vector3(.1f, 0, 0), propagate: PoseComponents.Rotation))], Rig);
        check(Near(kept.Bones["j_kosi"].Position, new Vector3(.1f, 0, 0)) && Near(kept.Bones["j_sebo_a"].Position, new Vector3(-.1f, 0, 0)) &&
              Near(kept.Bones["j_asi_a_l"].Position, new Vector3(-.1f, 0, 0)) && !kept.Bones.ContainsKey("j_kubi"),
            "a move kept from the children moves every direct child back, so the bones below stay put");

        var turned = LivePoseLayer.Offsets([Bone("j_sebo_a", 0, Stack(rotation: turnA, propagate: PoseComponents.Position))], Rig);
        check(turned.TurnsChildren.SequenceEqual(["j_sebo_a"]) && Near(turned.Bones["j_sebo_a"].Rotation, turnA),
            "a turn kept from the children is still baked, and named as one that now carries them");

        var leftOut = LivePoseLayer.Offsets([
            Bone("iv_nothere", 0, Stack(rotation: turnA)), Bone("j_kubi", 0, Stack(rotation: turnA, ik: true)),
            Bone("j_kosi", 1, Stack(rotation: turnA)), Bone("j_kosi", 0, Stack()),
        ], Rig, partial: 0);
        check(leftOut.Bones.IsEmpty && leftOut.Missing.SequenceEqual(["iv_nothere"]) && leftOut.Ik.SequenceEqual(["j_kubi"]) && leftOut.Adjusted == 0,
            "bones the skeleton lacks and IK stacks are left out and named, another partial and empty stacks are skipped");

        var doc = new TimelineDocument(60, 60, null, [], 1);
        var added = MotionEdits.AddOffsets(doc, "LivePose", result.Bones, out var key, out var error);
        var layer = added.Motion.Single();
        check(error == null && layer.Key == key && layer.Kind == MotionLayerKind.Offset && layer.Name == "LivePose" &&
              layer.Start == 0 && layer.End == 60 && layer.FadeIn == 0 && layer.Keys.Single().Frame == 0 &&
              layer.Keys[0].Bones["j_kubi"] == result.Bones["j_kubi"] && added.MotionChanged,
            "the adjustments become one offset layer over the whole clip with one key, so they hold all the way through");
        check(ReferenceEquals(MotionEdits.AddOffsets(doc, "x", ImmutableDictionary<string, BoneTransform>.Empty, out _, out var none), doc) && none != null,
            "no adjustments add no layer and say so");

        // Applied to a clip, the layer turns the bone by the stacks' turn on top of its motion.
        var clip = new MotionClip(Rig, 1, 2);
        for (var f = 0; f < 2; f++)
            for (var b = 0; b < Rig.Bones.Length; b++)
                clip.Set(f, b, MotionTransform.From(Rig.Bones[b].Reference));
        MotionEvaluator.Apply(clip, added.Motion);
        check(Near(clip.Get(1, 3).Rotation, turnA * turnB) && Near(clip.Get(1, 3).Position, new Vector3(0, .41f, 0)),
            "baked, the bone turns as LivePose turned it and moves by its offset");
    }

    private static void LivePoseFiles(Action<bool, string> check, Action<Action, string> reject)
    {
        const string file = """
        {
          "TypeName": "Live Pose",
          "Data": [
            { "BonePoseInfoId": { "BoneName": "j_te_l", "Partial": 0, "Slot": 0 },
              "Stacks": [ { "Transform": { "Position": { "X": 0.0, "Y": 0.0, "Z": 0.0 },
                "Rotation": { "X": -0.12590674, "Y": 0.0035848762, "Z": -0.019161014, "W": 0.99185055, "IsIdentity": false },
                "Scale": { "X": 7.1525574E-07, "Y": 1.5497208E-06, "Z": 1.1920929E-07 } }, "Propogate": 3 } ] },
            { "BonePoseInfoId": { "BoneName": "j_kao", "Partial": 0, "Slot": 0 }, "Stacks": [] },
            { "BonePoseInfoId": { "BoneName": "j_kubi", "Partial": 0, "Slot": 0 },
              "Stacks": [ { "Transform": { "Position": { "X": 0.1, "Y": 0, "Z": 0 }, "Rotation": { "X": 0, "Y": 0, "Z": 0, "W": 0 },
                "Scale": { "X": 0, "Y": 0, "Z": 0 } }, "IK_Enabled": true } ] }
          ]
        }
        """;
        var bones = LivePoseLayer.ReadFile(file);
        check(bones.Length == 2 && bones[0].Id == new PoseBoneId("j_te_l", 0) && bones[0].Stacks[0].Propagate == (PoseComponents)3 &&
              Math.Abs(bones[0].Stacks[0].Rotation.W - .99185055f) < 1e-5f,
            "a .livepose file reads as LivePose's own stacks; a bone without stacks is skipped");
        check(bones[1].Stacks[0].Rotation == Quaternion.Identity && bones[1].Stacks[0].Ik.Enabled &&
              bones[1].Stacks[0].Propagate == (PoseComponents.Position | PoseComponents.Rotation),
            "an all-zero rotation reads as none, IK is kept as on, and a missing Propogate reaches the children as LivePose's default");
        reject(() => LivePoseLayer.ReadFile("{ \"Bones\": {} }"), "a .pose file is not a LivePose file");
        reject(() => LivePoseLayer.ReadFile("not json"), "text that isn't JSON");
        reject(() => LivePoseLayer.ReadFile("""{ "Data": [ { "BonePoseInfoId": { "BoneName": "a" }, "Stacks": [ { "Transform": { "Rotation": { "X": "x" } } } ] } ] }"""),
            "an adjustment that can't be read");
    }

    private static void Library(Action<bool, string> check)
    {
        AnimationLibrary.TimelineRow[] timelines =
        [
            new(3, "normal/idle", 2), new(783, "normal/idle", 2),
            new(3124, "emote/pose01_loop", 2), new(3123, "emote/pose01_start", 2), new(3126, "emote/pose02_loop", 2),
            new(643, "emote/sit", 2), new(642, "event_base/event_base_chair_start", 2), new(3132, "emote/s_pose01_loop", 2),
            new(654, "emote/jmn", 2), new(3136, "emote/j_pose01_loop", 2), new(3771, "emote/j_pose03_loop", 2),
            new(585, "emote/bed_liedown_loop", 2), new(3142, "emote/l_pose01_loop", 2),
            new(13, "normal/walk", 2), new(22, "normal/run", 2),
            new(703, "emote/doze", 2), new(657, "emote/j_doze", 2), new(646, "emote/s_doze", 2), new(679, "emote/u_doze", 2),
            new(605, "facial/pose/smile", 0), new(919, "event_base/event_base_idle_celeb", 2), new(5, "", 2),
        ];
        static AnimationLibrary.EmoteRow Emote(uint id, string name, string command, string category, params ushort[] lines) =>
            new(id, name, command, category, [.. lines.Concat(Enumerable.Repeat((ushort)0, 7 - lines.Length))]);
        AnimationLibrary.EmoteRow[] emotes =
        [
            Emote(13, "Doze", "/doze", "General", 703, 0, 657, 646, 679),
            Emote(50, "Sit", "/lounge", "Special", 643, 642, 0, 643),
            Emote(52, "Sit on Ground", "/groundsit", "Special", 654),
            Emote(88, "Sleep", "", "Special", 585),
            Emote(69, "Smile", "/smile", "Expressions", 605),
            Emote(91, "", "", "", 3124, 3123), Emote(92, "", "", "", 3126), Emote(95, "", "", "", 3132),
            Emote(97, "", "", "", 3136), Emote(117, "", "", "", 3771), Emote(100, "", "", "", 3142),
        ];
        var items = AnimationLibrary.Build(timelines, emotes);
        string[] Names(AnimationLibraryGroup group) => [.. items.Where(i => i.Group == group).Select(i => i.Name)];
        check(Names(AnimationLibraryGroup.Idles).SequenceEqual(["Idle", "Idle pose 1", "Idle pose 2"]) &&
              items.First(i => i.Group == AnimationLibraryGroup.Idles).Timeline == 3,
            "idles: the standing idle (the first of its rows), then every /changepose idle numbered as its file is");
        check(Names(AnimationLibraryGroup.ChairSits).SequenceEqual(["Chair sit", "Chair sit pose 1"]) &&
              Names(AnimationLibraryGroup.GroundSits).SequenceEqual(["Ground sit", "Ground sit pose 1", "Ground sit pose 3"]) &&
              Names(AnimationLibraryGroup.Lying).SequenceEqual(["Lying in bed", "Lying pose 1"]),
            "chair, ground and bed poses: the emote's own pose, then the numbered ones, gaps kept");
        check(Names(AnimationLibraryGroup.Emotes).SequenceEqual(["Doze", "Doze (on the ground)", "Doze (in a chair)", "Doze (upper body)",
                  "Sit", "Sit (start)", "Sit on Ground"]),
            "emotes by name, with their start, sitting and upper body versions; a version that is the emote itself is not repeated");
        check(items.Single(i => i.Group == AnimationLibraryGroup.Expressions) is { Name: "Smile", Face: true, Detail: "/smile" },
            "an expression plays on the face");
        check(Names(AnimationLibraryGroup.Movement).SequenceEqual(["Walk", "Run"]),
            "movement lists every track by its name");
        var other = items.Where(i => i.Group == AnimationLibraryGroup.Other).ToArray();
        check(other.Any(i => i.Timeline == 783) && other.Any(i => i.Timeline == 919 && i.Name == "Idle celeb") &&
              other.All(i => i.Timeline is not (3 or 703 or 605 or 5)),
            "every other timeline is listed once by a name from its key, rows without a key are not");
        var withEmpty = AnimationLibrary.Build([.. timelines, new(3188, "emote/dance01_loop", 2), new(3187, "emote/dance01_start", 2)],
            [.. emotes, Emote(102, "Bomb Dance", "/bombdance", "General", 3188, 3187)], new HashSet<ushort> { 3187 });
        check(withEmpty.Any(i => i.Name == "Bomb Dance") && withEmpty.All(i => i.Timeline != 3187) && withEmpty.Any(i => i.Name == "Sit (start)"),
            "a version whose timeline holds nothing (Bomb Dance's start) is left out, also from Other; other starts stay");
        check(AnimationLibrary.KeyName("event_base/event_base_chair_talk") == "Chair talk" && AnimationLibrary.KeyName("emote/dance") == "Dance" &&
              AnimationLibrary.KeyName("x") == "X",
            "a key's name drops its folder and the folder's prefix");
        check(AnimationLibrary.Matches(items.First(i => i.Name == "Doze (in a chair)"), "doze chair") &&
              AnimationLibrary.Matches(items.First(i => i.Name == "Doze"), "/doze") &&
              !AnimationLibrary.Matches(items.First(i => i.Name == "Doze"), "doze chair"),
            "a search matches every word in the name, the command or the key");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = new TimelineDocument(30, 30, null, [], 1);
        var one = MotionEdits.SetExcluded(doc, ["j_kami_b", "j_kami_a"], true);
        check(one.ExcludedBones.SequenceEqual(["j_kami_a", "j_kami_b"]) && ReferenceEquals(MotionEdits.SetExcluded(one, ["j_kami_a"], true), one),
            "unticked bones are kept sorted, and unticking one again changes nothing");
        check(MotionEdits.SetExcluded(one, ["j_kami_a"], false).ExcludedBones.SequenceEqual(["j_kami_b"]) &&
              MotionEdits.ExcludeOnly(one, ["n_sippo_a"]).ExcludedBones.SequenceEqual(["n_sippo_a"]) &&
              ReferenceEquals(MotionEdits.ExcludeOnly(one, ["j_kami_b", "j_kami_a"]), one),
            "a bone ticked again comes back, and a preset leaves out exactly its bones");
        check(new TimelineDocument(30, 30, null, [], 1) { ExcludedBones = default }.ExcludedBones.IsEmpty,
            "a document read from a file leaves no bone out");
        var editor = new TimelineEditor(doc);
        editor.Change(MotionEdits.SetExcluded(doc, ["j_kami_a"], true));
        check(editor.Dirty && editor.Document.ExcludedBones.Length == 1 && editor.Undo() && editor.Document.ExcludedBones.IsEmpty && !editor.Dirty,
            "unticking a bone is one undo step, and makes the timeline unsaved");
    }

    private static readonly Guid Collection = Guid.NewGuid();

    private static AnimationClip Clip(SkeletonDescription skeleton, params StandardSkeleton[] standards)
    {
        var resource = new AnimationResource("chara/test.pap", "chara/test.pap", "hash");
        var candidate = new SkeletonCandidate(new(SkeletonSourceKind.Mod, resource), skeleton, 0, "");
        return new AnimationClip("chara/human/c0801/animation/a0001/bt_common/emote/dance.pap", "loop", 0, 0, 42, "chara/test.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate,
                TrackBones: [.. Enumerable.Range(0, skeleton.Bones.Length).Select(i => (short)i)], Standards: [.. standards]));
    }

    private static AnimationTimelineSource Source(AnimationClip clip, TimelineDocument doc, int entry = 0)
    {
        var capture = new AnimationCapture("a", 7, 1, Collection, "Test", "Dance", clip, null, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""),
            DateTime.UtcNow, true);
        return new AnimationTimelineSource(capture, false, clip, new AnimationResource(clip.GamePath, "dance.pap", "file"), [1], entry, [], doc, null);
    }

    private static MotionLayer Layer(string bone) => new("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1,
        PoseComponents.All, MotionInterpolation.Smooth, [],
        [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone,
            new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f), Vector3.One)))]);

    private static void Saves(Action<bool, string> check)
    {
        var game = new SkeletonDescription("game", "game", [.. Rig.Bones.Take(4)], [], [], []);
        var clip = Clip(Rig, new StandardSkeleton(SkeletonStandard.Vanilla, game, "game"));
        var doc = new TimelineDocument(30, 30, null, [], 1);
        var left = doc with { ExcludedBones = ["j_kubi"] };
        var request = AnimationEditService.TimelineRequest(Source(clip, left), left, AnimationDestination.NewMod, "Mod", SkeletonStandard.Vanilla);
        check(request.Operation == AnimationOperation.EditTimeline && request.ExcludedBones.SequenceEqual(["j_kubi"]) &&
              request.RepairTarget == SkeletonStandard.Vanilla && AnimationBakeRules.Repairs(request),
            "a timeline save carries its unticked bones and the skeleton to repair onto, and repairs in its own bake");
        var expression = AnimationEditService.TimelineRequest(Source(clip, left, -1), left, AnimationDestination.NewMod, "Mod", SkeletonStandard.Vanilla);
        check(expression.ExcludedBones.IsEmpty && expression.RepairTarget == null && !AnimationBakeRules.Repairs(expression),
            "an expression's timeline keeps every bone and its own skeleton");
        check(!AnimationBakeRules.Repairs(request with { RepairTarget = null }) &&
              AnimationBakeRules.Repairs(request with { Operation = AnimationOperation.RepairSkeleton, Timeline = null }) &&
              !AnimationBakeRules.Repairs(request with { Operation = AnimationOperation.ExcludeBones }),
            "only a repair, or a timeline save naming a skeleton, retargets");
        check(AnimationBakeRules.ChangesMotion(left) && AnimationBakeRules.ChangesMotion(doc, SkeletonStandard.Ivcs) &&
              !AnimationBakeRules.ChangesMotion(doc) && AnimationEditService.ChangesMotion(left),
            "unticked bones and a repair bake the motion; a timeline with neither keeps it byte for byte");

        var layered = doc with { Motion = [Layer("j_kosi")] };
        byte[] file = [1, 2, 3];
        check(AnimationEditService.MotionBase(Source(clip, layered), layered, file) == file &&
              AnimationEditService.MotionBase(Source(clip, layered), layered with { ExcludedBones = ["j_kubi"] }, file) == null &&
              AnimationEditService.MotionBase(Source(clip, layered), layered, file, SkeletonStandard.Vanilla) == null,
            "a save that leaves bones out or repairs bakes its layers in for good and keeps no motion for Edit again");
        var journal = new AnimationEditJournal
        {
            Request = request with { Timeline = new AnimationTimelineEdit(false, 0, null, [Layer("j_kosi")], "base") },
            State = "Completed",
        };
        check(AnimationEditService.EditAgainProblem(journal) is { } refused && refused.Contains("can't be opened again") &&
              AnimationEditService.EditAgainProblem(new AnimationEditJournal
              {
                  Request = journal.Request with { ExcludedBones = [], RepairTarget = null }, State = "Completed",
              }) == null,
            "such an edit can't be opened again with its layers; the same edit without them can");
    }

    private static void Problems(Action<bool, string> check)
    {
        var game = new SkeletonDescription("game", "game", [.. Rig.Bones.Take(4)], [], [], []);
        var clip = Clip(Rig, new StandardSkeleton(SkeletonStandard.Vanilla, game, "game"));
        var doc = new TimelineDocument(30, 30, null, [], 1);
        check(TimelineWindowViews.BonesProblem(doc, clip, null) == null &&
              TimelineWindowViews.BonesProblem(doc with { ExcludedBones = ["j_kubi"] }, clip, null) == null,
            "an animated bone can be left out of a save");
        check(TimelineWindowViews.BonesProblem(doc with { ExcludedBones = [.. Rig.Bones.Select(b => b.Name)] }, clip, null) == "Keep at least one bone animated.",
            "leaving out every bone is refused");
        check(TimelineWindowViews.BonesProblem(doc with { ExcludedBones = ["j_kubi"], Motion = [Layer("j_kubi")] }, clip, null) is { } moved &&
              moved.Contains("j_kubi") && TimelineWindowViews.BonesProblem(doc with { ExcludedBones = ["j_kubi"], Motion = [Layer("j_kosi")] }, clip, null) == null,
            "a bone layer on an unticked bone blocks the save, one on another bone doesn't");
        check(TimelineWindowViews.BonesProblem(doc, clip, SkeletonStandard.Vanilla) == null &&
              TimelineWindowViews.BonesProblem(doc, clip, SkeletonStandard.Ivcs) is { } missing && missing.Contains("IVCS"),
            "a repair onto a standard that was found can save; one that wasn't found can't");
        var already = Clip(game, new StandardSkeleton(SkeletonStandard.Vanilla, game, "game"));
        check(TimelineWindowViews.BonesProblem(doc, already, SkeletonStandard.Vanilla) is { } same && same.Contains("already"),
            "a repair onto the skeleton the clip was already made for changes nothing and is refused");
    }
}
