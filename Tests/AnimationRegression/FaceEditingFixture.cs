using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Face editing in the Timeline: the face a document's bone layers edit (its anchor and length), the face's
/// sources, saves, edits, Live layers, the face map and the paths Edit face reads. Decoding, baking and playing a
/// face need the game; they are checked in game.
/// </summary>
internal static class FaceEditingFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Model(check, reject);
        Sources(check);
        Saves(check);
        PlayedOnce(check);
        Edits(check);
        Window(check);
        Live(check);
        Paths(check);
        Map(check);
        Carry(check);
        SharedFile(check);
        Anchored(check);
        Lengths(check);
        Refused(check);
        Back(check);
        Kept(check);
    }

    /// <summary>An expression's face layers carried over a save apply to the face from before it, never to the saved face.</summary>
    private static void Carry(Action<bool, string> check)
    {
        const string path = "chara/human/c0801/animation/f0002/nonresident/smile.pap";
        var face = FaceSource(path, 0, "face-print") with { Pap = [7, 7], Resource = new AnimationResource(path, "x", "face-file") };
        var expression = Source(standalone: true) with { Face = face };
        var kept = SaveFooter.KeptMotion(expression);
        check(kept.Bytes is [7, 7] && kept.Hash == "face-file" && kept.Fingerprint == "face-print",
            "an expression's face keeps the face's file over a save, not the timeline's");
        var saved = expression with { Face = face with { Resource = face.Resource with { Hash = "face-saved" } } };
        var reread = SaveFooter.WithKeptMotion(saved, kept);
        check(reread.Face!.BaseMotion is [7, 7] && reread.Face.BaseHash == "face-file" && reread.BaseMotion == null,
            "read again after its face was saved, the face's layers apply to the face from before the save");
        check(SaveFooter.WithKeptMotion(expression, kept).Face!.BaseMotion == null, "a face its save left as it was is its own base");
        var body = Source(standalone: false) with { Pap = [5] };
        var bodyKept = SaveFooter.KeptMotion(body);
        check(bodyKept.Bytes is [5] && SaveFooter.WithKeptMotion(body with { Resource = body.Resource with { Hash = "after" } }, bodyKept).BaseMotion is [5],
            "a clip keeps its own file over a save, as before");
        var longer = TimelineFace.WithFrames(TimelineFace.Open(Empty(150), 0, 1, 150), 300, 150) with { Motion = [Layer(0, 300, 0)] };
        check(!AnimationBakeRules.KeepsLayersOverSave(longer) &&
              AnimationBakeRules.KeepsLayersOverSave(TimelineFace.Open(Empty(150), 0, 1, 150) with { Motion = [Layer(0, 2, 0)] }),
            "a face saved at a new length reads back as saved; one at its own length keeps its layers over the save");
    }

    /// <summary>A face built into a file other faces share must leave every other face in it as the file has it now.</summary>
    private static void SharedFile(Action<bool, string> check)
    {
        var current = ImmutableDictionary<int, string>.Empty.Add(0, "base").Add(1, "blink").Add(2, "angry-saved").Add(3, "smile");
        check(AnimationEditService.OtherFacesProblem(current, current.SetItem(3, "smile-edited"), 3) == null, "the face saved changes alone");
        check(AnimationEditService.OtherFacesProblem(current, current.SetItem(3, "smile-edited").SetItem(2, "angry-before"), 3) != null &&
              AnimationEditService.OtherFacesProblem(current, current.Remove(1), 3) != null,
            "a save that would put another face back as it was is refused");
    }

    /// <summary>A face whose event starts after frame 0: layers from before it bake and play in the face's frames, and the posing samples the face there.</summary>
    private static void Anchored(Action<bool, string> check)
    {
        var early = Layer(0, 150, 10) with { FadeIn = 40, Keys = [Layer(0, 150, 10).Keys[0], Layer(0, 150, 45).Keys[0]] };
        var shifted = TimelineFace.ToFace([early, Layer(0, 20, 5) with { Key = "m2" }], 30);
        check(shifted is [{ Start: 0, End: 120, FadeIn: 10 } first] && first.Keys.Select(k => k.Frame).SequenceEqual([0, 15]),
            "a layer from before the face covers the face from its first frame, its earlier key holding there");
        string? problem = null;
        try { MotionEvaluator.Validate(shifted); }
        catch (InvalidDataException e) { problem = e.Message; }
        check(problem == null, "layers from before the face bake: " + problem);

        var moving = MotionClip.FromPoses(FaceRig(), 2 / 30f, [FacePose(0), FacePose(.2f), FacePose(.4f)]);
        var shown = TimelineFace.Delayed(moving, 30);
        check(Near(shown.Pose(10), moving.Pose(0)) && Near(shown.Pose(31), moving.Pose(1)) && Math.Abs(shown.Frame(shown.Samples - 1) - 32) < .01f &&
              ReferenceEquals(TimelineFace.Delayed(moving, 0), moving),
            "on the timeline a face's motion is held until its event, then plays in its own frames");
        check(BonePosing.MotionEnd(Source(standalone: true), TimelineFace.Open(Empty(150), 30, 90, 150)) == 120,
            "a face's motion ends where the face does on the timeline");
    }

    private static bool Near(MotionTransform[] a, MotionTransform[] b) =>
        a.Length == b.Length && a.Zip(b).All(p => Math.Abs(Quaternion.Dot(p.First.Rotation, p.Second.Rotation)) > .9999f &&
                                                   Vector3.Distance(p.First.Position, p.Second.Position) < 1e-4f);

    /// <summary>A face's length change: its layers follow its end, and Keep follows its length.</summary>
    private static void Lengths(Action<bool, string> check)
    {
        var still = new TimelineEditor(TimelineFace.Open(Empty(150) with { HeaderLength = 150 }, 0, 1, 150) with { Motion = [Layer(0, 2, 0)] });
        check(still.HoldFace(new MotionReplacement([1], "p", "h", 90, AnimationEditService.StillPoseFrame(0), "", null)) &&
              still.Document.Motion.Single().End == 90, "a still face made to move keeps its shape over the new length");
        var moving = new TimelineEditor(TimelineFace.Open(Empty(150) with { HeaderLength = 150 }, 0, 300, 150) with
        {
            Motion = [Layer(0, 300, 0), Layer(0, 100, 0) with { Key = "m2" }],
        });
        check(moving.SetFaceLength(600) && moving.Document.Motion[0].End == 600 && moving.Document.Motion[1].End == 100,
            "layers that lasted the whole face last its new length; the others stay");
        var expression = Source(standalone: true) with { Clip = Source(true).Clip with { Duration = 1 / 30f } };
        var longer = TimelineFace.WithFrames(TimelineFace.Open(Empty(150), 0, 1, 150), 400, 150);
        check(expression.EditedFrames(longer)["cfxf_smile"] == 400 && expression.EditedFrames(null)["cfxf_smile"] == 1,
            "Keep reads a face's edited length");
    }

    /// <summary>A face takes only the layers it can bake: what it refuses is never added, pasted or shown.</summary>
    private static void Refused(Action<bool, string> check)
    {
        var face = TimelineFace.Open(Empty(150), 0, 1, 150);
        var pinned = MotionEdits.Add(face, MotionLayerKind.Pin, 0, 2, out _, out var pinError);
        var hand = MotionEdits.AddFix(face, new MotionFix(MotionFixKind.HandShape, Hand: new MotionHand(MotionHandSide.Right, "relaxed")), out _, out var handError);
        var smooth = MotionEdits.AddFix(face, new MotionFix(MotionFixKind.SmoothJitter), out _, out var smoothError);
        var pasted = MotionEdits.Paste(face, [PinLayer(), Layer(0, 2, 0)], 150, out var created, out _);
        check(pinned.Motion.IsEmpty && pinError != null && hand.Motion.IsEmpty && handError != null && smoothError == null && smooth.Motion.Length == 1 &&
              pasted.Motion is [{ Kind: MotionLayerKind.Offset }] && created.Length == 1, "a face takes only the layers it can bake");
    }

    /// <summary>Back from Edit face works when the face couldn't be read, whatever the timeline it left had.</summary>
    private static void Back(Action<bool, string> check)
    {
        var face = FaceSource("chara/human/c0801/animation/f0002/nonresident/smile.pap", 0, "fp");
        check(TimelineWindow.CanGoBack(null, dirty: true, busy: false) && TimelineWindow.CanGoBack(face with { Document = null }, true, false) &&
              !TimelineWindow.CanGoBack(face, true, false) && TimelineWindow.CanGoBack(face, false, false) && !TimelineWindow.CanGoBack(null, false, true),
            "Back stays open after a face read failed, and waits only for the face's own changes");
    }

    /// <summary>A face session's kept edits name the face's file and base, and come back only onto them.</summary>
    private static void Kept(Action<bool, string> check)
    {
        const string path = "chara/human/c0801/animation/f0002/nonresident/smile.pap";
        var face = FaceSource(path, 0, "fp") with { Resource = new AnimationResource(path, "x", "face-file"), BaseMotion = [3], BaseHash = "face-base" };
        var expression = Source(standalone: true) with { Face = face };
        var kept = TimelineAutosaver.Snapshot(expression, Empty(150), []);
        check(kept is { FileHash: "hash", BaseHash: "face-base", FaceHash: "face-file" } && TimelineAutosaver.Fits(expression, kept) &&
              !TimelineAutosaver.Fits(expression with { Face = face with { Resource = face.Resource with { Hash = "other" } } }, kept) &&
              !TimelineAutosaver.Fits(expression with { Face = face with { BaseMotion = null, BaseHash = null } }, kept),
            "a face session's kept edits come back only onto the same face and its base");
        var body = Source(standalone: false);
        check(TimelineAutosaver.Snapshot(body, Empty(150), []) is { FaceHash: null } clip && TimelineAutosaver.Fits(body, clip),
            "a clip's kept edits come back as before");
    }

    /// <summary>The face map: Brio's head pictures, picked by race, mirrored like the body's, with Viera ears and face groups.</summary>
    private static void Map(Action<bool, string> check)
    {
        static bool FaceBone(string bone) => bone.StartsWith("j_f_", StringComparison.Ordinal) || bone.StartsWith("j_mimi_", StringComparison.Ordinal) ||
            bone.StartsWith("n_ear_", StringComparison.Ordinal) || bone.StartsWith("j_zer", StringComparison.Ordinal) || bone.StartsWith("n_f_", StringComparison.Ordinal);
        check(PoseMap.FaceSections.All(section => section.Spots.All(spot => FaceBone(spot.Bone))), "every face picture's dots are face bones");
        var rig = FaceRig();
        check(PoseMap.FaceSection("c0801", rig).Key == PoseMap.MiqoteHead.Key && PoseMap.FaceSection("c1601", rig).Key == PoseMap.HrothgarHead.Key &&
              PoseMap.FaceSection("c0101", rig).Key == PoseMap.HumanHead.Key && PoseMap.FaceSection("c1801", rig).Key == PoseMap.VieraHead.Key,
            "a face's picture is its race's");
        var floppy = rig with
        {
            Bones = [.. rig.Bones, new("j_zerb_a_l", 0, 0, rig.Bones[0].Reference), new("j_zerb_b_l", 0, 0, rig.Bones[0].Reference)],
        };
        var ears = PoseMap.FaceSection("c1801", floppy).Spots.Where(spot => spot.Bone.StartsWith("j_zer", StringComparison.Ordinal)).Select(spot => spot.Bone).ToArray();
        check(ears.Order(StringComparer.Ordinal).SequenceEqual(["j_zerb_a_l", "j_zerb_b_l"]) && !PoseMap.FaceSection("c1801", rig).Spots.Any(spot => spot.Bone.StartsWith("j_zer")),
            "a Viera face shows the ears its skeleton has");
        var brow = PoseMap.Place(PoseMap.MiqoteHead, false).Single(spot => spot.Bone == "j_f_mayu_r");
        check(brow.Position.X == PoseMap.MiqoteHead.Size.X - 311 && brow.Position.Y == 427, "the right brow mirrors the left across the face");
        check(PoseMap.FaceGroups.Single(group => group.Name == "Jaw and teeth") is var jaw && jaw.Contains("j_f_ago") && jaw.Contains("j_f_hagukiup") &&
              !jaw.Contains("j_f_bero_01"), "Select picks the jaw and teeth together");
        check(PoseMap.FacePlaced.Contains("j_f_ago") && PoseMap.FacePlaced.Contains("j_f_mayu_r") && PoseMap.FacePlaced.Contains("j_zerb_b_r") &&
              !PoseMap.FacePlaced.Contains("j_ude_a_l") && !PoseMap.Placed.Contains("j_f_ago"), "a face session's dots are the face's, a clip's the body's");
        var images = typeof(PoseMap).Assembly.GetManifestResourceNames();
        check(PoseMap.FaceSections.All(section => images.Contains(PoseMap.Resource(section))), "every face picture is in the plugin");
    }

    /// <summary>The face file Edit face opens for a face event: the always-loaded face.pap, else a face pack the timeline or its action timeline loads.</summary>
    private static void Paths(Action<bool, string> check)
    {
        var key = new TimelineCatalogKey(801, "f0002");
        var resident = new HashSet<string>(StringComparer.Ordinal) { "cfxf_base" };
        check(TimelineFace.FacePath(key, "cfxf_smile", "smile", resident) == "chara/human/c0801/animation/f0002/nonresident/smile.pap",
            "a face from a pack opens from the pack's file for the character's face");
        check(TimelineFace.FacePath(key, "cfxf_base", "smile", resident) == "chara/human/c0801/animation/f0002/resident/face.pap",
            "an always-loaded face opens from face.pap");
        check(TimelineFace.FacePath(key, "cfxf_smile", null, resident) == null, "a face in no file the character loads can't open");

        // A vanilla emote's face events sit in its PAP's own timeline, which names no pack: its action timeline loads it (seen in game 2026-11-03).
        const string dance = "chara/human/c0801/animation/f0002/nonresident/emot/dance.pap";
        check(TimelineFace.FacePath(key, "cfxf_bossy", null, resident, ["emot/dance"]) == dance,
            "a body clip's face event opens from the pack its action timeline loads");
        check(TimelineFace.FacePath(key, "cfxf_bossy", null, resident, ["emot/sp33", "emot/dance"], ["bossy", "emot/dance"]) == dance &&
              TimelineFace.FacePath(key, "cfxf_bossy", "smile", resident, ["emot/dance"], ["emot/dance"]) == dance,
            "of the packs the timeline loads, the face opens from one that holds it");
        check(TimelineFace.FacePath(key, "cfxf_smile", "smile", resident, ["emot/dance"]) == "chara/human/c0801/animation/f0002/nonresident/smile.pap" &&
              TimelineFace.FacePath(key, "cfxf_bossy", null, resident, ["emot/x", "emot/dance"], ["bossy"])?.EndsWith("/emot/x.pap") == true,
            "the timeline's own pack comes first, and a face the catalog doesn't place opens from the first pack");
        check(TimelineFace.FacePath(key, "cfxf_bossy", null, resident, ["../emot", "", "emot/dance"]) == dance &&
              TimelineFace.FacePath(key, "cfxf_bossy", null, resident, []) == null &&
              TimelineFace.FacePath(key, "cfxf_base", null, resident, ["emot/dance"])!.EndsWith("/resident/face.pap"),
            "unsafe pack names are skipped, no pack means no file, and an always-loaded face still opens from face.pap");
    }

    /// <summary>What Live shows on a face: its layers in the face's own frames, at the face's own length once it changed.</summary>
    private static void Live(Action<bool, string> check)
    {
        var face = TimelineFace.Open(Empty(150), 30, 100, 150) with { Motion = [Layer(30, 90, 45)] };
        var body = Empty(150) with { Motion = [Layer(30, 90, 45)] };
        check(TimelinePreview.LiveLayers(face).Single() is { Start: 0, End: 60 } && TimelinePreview.LiveLayers(body).Single().Start == 30,
            "Live shows a face's layers in the face's own frames, a clip's as they are");
        check(TimelinePreview.LiveLength(face) == 0 && TimelinePreview.LiveLength(TimelineFace.WithFrames(face, 120, 150)) == 120 &&
              TimelinePreview.LiveLength(Empty(150)) == 0, "Live plays a face at its own length once it changed");
    }

    /// <summary>A face session in the Timeline window: its layer rows and kinds, keys on a still face, mirrored lids and the whole-face pose.</summary>
    private static void Window(Action<bool, string> check)
    {
        var rig = FaceRig();
        var winked = MotionClip.FromPoses(rig, 1 / 30f, [Lid(.3f), Lid(.3f)]);
        var mirrored = winked.Clone();
        var symmetry = new MotionLayer("m1", MotionLayerKind.Symmetry, "Symmetry", 0, 1, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            ["j_f_mabup_01_l"], [], true, MotionSide.Left);
        MotionEvaluator.Apply(mirrored, [symmetry]);
        var right = MotionEvaluator.Index(rig)["j_f_mabup_01_r"];
        var turned = 2 * MathF.Acos(Math.Min(1, Math.Abs(mirrored.Get(0, right).Rotation.W)));
        check(Math.Abs(turned - .3f) < .01f, "a symmetry layer makes the right upper lid follow the left one");

        var face = TimelineFace.Open(Empty(150), 0, 1, 150) with { Motion = [Layer(0, 2, 0)] };
        check(TimelineLayout.Build(face).MotionRows == 2 && TimelineLayout.Build(Empty(150) with { Expression = true, Motion = [Layer(0, 2, 0)] }).MotionRows == 0,
            "a face has bone layer rows and the row to add one from; an expression without one has none");
        var moving = TimelineFace.Open(Empty(150), 0, 300, 150);
        check(MotionEdits.KeyFrame(face, 40) == 0 && MotionEdits.KeyFrame(moving, 40) == 40 && MotionEdits.KeyFrame(Empty(150), 40) == 40,
            "a still face's keys go on its first frame");
        var keyed = MotionEdits.SetKey(face, "m1", 40, "j_f_ago", new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f), Vector3.One));
        check(keyed.Motion.Single().Keys.All(k => k.Frame == 0), "a key set anywhere on a still face lands on its first frame");
        MotionEdits.AddMotionKeys(face, out _, out var markers);
        check(markers != null, "a face has no motion keys layer");
        check(TimelineWindowViews.FaceLayerKinds.Contains(MotionLayerKind.Pose) && TimelineWindowViews.FaceLayerKinds.Contains(MotionLayerKind.Smooth) &&
              !TimelineWindowViews.FaceLayerKinds.Contains(MotionLayerKind.Pin) && !TimelineWindowViews.FaceLayerKinds.Contains(MotionLayerKind.Idle),
            "the Add menu offers a face only the layers it takes");
        check(!PoseEdits.Unposed("j_f_ago", face: true) && PoseEdits.Unposed("j_f_ago") && PoseEdits.Unposed("n_root", face: true),
            "a face's own pose keys its face bones");
        check(!TimelineFace.WithFrames(moving, 600, 150).Stretched && TimelineFace.WithFrames(moving, 600, 150).LengthChanged,
            "a face made longer doesn't stretch the timeline's motion; its own length is the face's");
        var expression = Source(standalone: true) with { Face = Source(standalone: false) };
        var held = new MotionReplacement([1], "held-print", "held-hash", 1, AnimationEditService.NeutralFaceAction, "", null);
        check(MotionDecode.KeyOf(expression) != MotionDecode.KeyOf(expression.WithReplacement(held)), "a hold on an expression's face reads the face again");
    }

    /// <summary>The face rig at rest with its left upper lid turned down by <paramref name="angle"/> radians.</summary>
    private static BoneTransform[] Lid(float angle) =>
        [.. FaceRig().Bones.Select(b => b.Name == "j_f_mabup_01_l" ? b.Reference with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle) } : b.Reference)];

    /// <summary>The rest face, held still the way a face's own clip is, and the editor's face length and Neutral face.</summary>
    private static void Edits(Action<bool, string> check)
    {
        var rig = FaceRig();
        var smile = MotionClip.FromPoses(rig, 1 / 30f, [FacePose(.3f), FacePose(.3f)]);
        var playing = new MotionClip(rig, smile.Duration, smile.Samples) { BlendHint = 1, SourceTracks = [0, 1, 2, 3] };
        playing.CopyFrom(smile);
        var rest = MotionHold.Rest(playing);
        check(rest.Length == rig.Bones.Length && rest.Select((t, b) => t == MotionTransform.From(rig.Bones[b].Reference)).All(same => same),
            "the rest face is each bone's reference pose");
        var neutral = MotionHold.Still(playing, rest, 0, 1);
        check(neutral is { Samples: 2, BlendHint: 1 } && neutral.SourceTracks.SequenceEqual(playing.SourceTracks) &&
              Enumerable.Range(0, 2).All(i => neutral.Get(i, 1) == rest[1]), "the neutral face keeps the face's tracks and additive mode, held for one frame");

        var face = new TimelineEditor(TimelineFace.Open(Empty(150) with { HeaderLength = 150 }, 0, 600, 150));
        check(!face.FixedLength && face.SetFaceLength(300) && face.Document.Face!.Frames == 300 && face.Document.Length == 300,
            "a moving face's length changes in the header");
        face.Undo();
        check(face.Document.Face!.Frames == 600, "a face length change is one undo step");
        check(new TimelineEditor(Empty(150) with { Expression = true }).FixedLength, "an expression without a face keeps its length");
        var still = new TimelineEditor(TimelineFace.Open(Empty(150) with { Expression = true, HeaderLength = 150 }, 0, 1, 150));
        check(!still.SetFaceLength(300) && still.Document.Face!.Frames == 1, "a still face gets a length only through a hold");
        var held = new MotionReplacement([1, 2, 3], "neutral-print", "neutral-hash", 1, "neutral face", "", null);
        check(still.NeutralFace(held) && still.Document.Replacement == held && still.Document.Motion is [{ Kind: MotionLayerKind.Pose, Start: 0 }] &&
              still.SelectedLayer == still.Document.Motion[0].Key && still.Document.Face!.Frames == 1 && still.Document.Length == 150,
            "Neutral face puts the rest face in and a pose layer to key on, the canvas as it was");
        var moved = new MotionReplacement([4], "hold-print", "hold-hash", 300, AnimationEditService.StillPoseFrame(0), "", null);
        check(still.HoldFace(moved) && still.Document.Face!.Frames == 300 && still.Document.Replacement == moved && still.Document.Motion.Length == 1,
            "a still face held for longer moves, its layers kept");
    }

    /// <summary>A face skeleton: the face, the jaw and both upper lids.</summary>
    internal static SkeletonDescription FaceRig() => new("face-rig", "face-rig-fingerprint",
    [
        new("j_f_face", -1, 0, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One)),
        new("j_f_ago", 0, 0, new BoneTransform(new Vector3(0, -.05f, .02f), Quaternion.Identity, Vector3.One)),
        new("j_f_mabup_01_l", 0, 0, new BoneTransform(new Vector3(.03f, .03f, .03f), Quaternion.Identity, Vector3.One)),
        new("j_f_mabup_01_r", 0, 0, new BoneTransform(new Vector3(-.03f, .03f, .03f), Quaternion.Identity, Vector3.One)),
    ], [], [], []);

    /// <summary>The face rig at rest with its jaw open by <paramref name="jaw"/> radians.</summary>
    private static BoneTransform[] FacePose(float jaw) =>
        [.. FaceRig().Bones.Select(b => b.Name == "j_f_ago" ? b.Reference with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, jaw) } : b.Reference)];

    /// <summary>What a face save asks the baker for, when the face is written at all, and when it can be saved in place.</summary>
    private static void Saves(Action<bool, string> check)
    {
        var face = FaceSource("chara/human/c0801/animation/f0002/resident/face.pap", entry: 2, fingerprint: "smile-after");
        var doc = TimelineFace.Open(Empty(150), 0, 1, 150) with { Motion = [Layer(0, 1, 0)], ExcludedBones = ["j_f_hana_l"] };
        var request = AnimationEditService.TimelineRequest(face, doc, AnimationDestination.NewMod, "Faces");
        check(request.ExcludedBones.Single() == "j_f_hana_l" && request.Timeline!.Face is { EntryIndex: 2, Frames: 1 } && request.Timeline.DurationSeconds == null,
            "a face save keeps its left-out bones and records its face");
        var late = TimelineFace.Open(Empty(150), 30, 100, 150) with { Motion = [Layer(30, 90, 45)] };
        var lateRequest = AnimationEditService.TimelineRequest(face, TimelineFace.WithFrames(late, 120, 150), AnimationDestination.NewMod, "Faces");
        check(lateRequest.Timeline!.Motion.Single() is { Start: 0, End: 60 } layer && layer.Keys.Single().Frame == 15 &&
              lateRequest.Timeline.DurationSeconds == 4f && lateRequest.Timeline.Cuts == null,
            "a face under a longer canvas bakes in its own frames and at its own length");
        check(AnimationEditService.FaceChanged(doc) && !AnimationEditService.FaceChanged(TimelineFace.Open(Empty(150), 0, 1, 150)),
            "only a changed face is written");
        check(AnimationEditService.FaceInPlaceProblem(Expression(tmbWritable: true, faceWritable: false), timelineChanged: true) != null &&
              AnimationEditService.FaceInPlaceProblem(Expression(tmbWritable: false, faceWritable: true), timelineChanged: false) == null &&
              AnimationEditService.FaceInPlaceProblem(Expression(tmbWritable: false, faceWritable: true), timelineChanged: true) != null,
            "in place needs every written file to be writable");

        // The footer judges in place as the save does: a face from a mod saves in place over a timeline of the game's
        // until that timeline changes too (its events, face library or the face's length, which retimes it).
        var modFace = Expression(tmbWritable: false, faceWritable: true);
        var keyed = TimelineFace.Open(modFace.Document!, 0, 1, 150) with { Motion = [Layer(0, 2, 0)] };
        check(SaveFooter.CanReplace(modFace, keyed) && !SaveFooter.TimelineChanged(modFace, keyed) &&
              !SaveFooter.CanReplace(modFace, TimelineFace.WithFrames(keyed, 300, 150)) &&
              SaveFooter.CanReplace(Expression(tmbWritable: true, faceWritable: true), TimelineFace.WithFrames(keyed, 300, 150)),
            "a mod's face saves in place over the game's timeline, and with it once the face's length changes");
        var packed = TimelineFace.Open(modFace.Document!, 0, 1, 150) with { FacePack = "emot/other" };
        check(!SaveFooter.CanReplace(modFace, packed) && SaveFooter.CanReplace(Expression(tmbWritable: true, faceWritable: false), packed) &&
              !SaveFooter.CanReplace(modFace, null) && !SaveFooter.CanReplace(Expression(tmbWritable: true, faceWritable: false), keyed),
            "a timeline-only change is judged by the timeline's file, a face change by the face's");
    }

    /// <summary>
    /// An expression's face plays once, though the expression's own row loops: a layer ramping over a moving face holds its
    /// last key at the face's end instead of wrapping round to its first.
    /// </summary>
    private static void PlayedOnce(Action<bool, string> check)
    {
        var expression = Source(standalone: true).Capture;
        var looping = expression with { Clip = expression.Clip with { IsLoop = true } };
        var face = AnimationEditService.ExpressionFaceCapture(looping);
        check(face is { ExpressionTimeline: null, Clip.IsLoop: false } && face.Id == looping.Id && face.Clip.GamePath == looping.Clip.GamePath,
            "an expression's face is read as its own clip, played once");
        static MotionKey Jaw(int frame, float angle) => new(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_f_ago",
            new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle), Vector3.One)));
        float LastJaw(bool loops)
        {
            var rig = FaceRig();
            var clip = new MotionClip(rig, 10f, 301) { Loops = loops, SourceTracks = [0, 1, 2, 3] };
            for (var i = 0; i < clip.Samples; i++)
                for (var b = 0; b < rig.Bones.Length; b++)
                    clip.Set(i, b, MotionTransform.From(rig.Bones[b].Reference));
            MotionEvaluator.Apply(clip, [Layer(0, 300, 0) with { Keys = [Jaw(0, 0), Jaw(299, .5f)] }]);
            return 2 * MathF.Acos(Math.Min(1, Math.Abs(clip.Get(clip.Samples - 1, 1).Rotation.W)));
        }
        check(Math.Abs(LastJaw(face.Clip.IsLoop) - .5f) < .01f && LastJaw(true) < .01f,
            "a jaw opening over a 300-frame face is open at its last frame (a looping clip would close it again)");
    }

    private static AnimationTimelineSource FaceSource(string path, int entry, string fingerprint)
    {
        var clip = new AnimationClip(path, "cfxf_smile", entry, 1, 0, "chara/test.sklb");
        var capture = new AnimationCapture("face", 1, 1, Guid.Empty, "Test", "Smile", clip, null, [path], [], DateTime.UtcNow, true);
        return new AnimationTimelineSource(capture, false, clip, new AnimationResource(path, "x", "hash"), [], entry, [], Empty(150), null)
            { FileFingerprint = fingerprint };
    }

    /// <summary>An expression with its face, each file from a mod (writable in place) or the game.</summary>
    private static AnimationTimelineSource Expression(bool tmbWritable, bool faceWritable)
    {
        var root = Path.Combine(Path.GetTempPath(), "ie-face-mod");
        AnimationResource Resource(string gamePath, string file, bool mod) => mod
            ? new AnimationResource(gamePath, Path.Combine(root, file), "hash", "Mod", root, file)
            : new AnimationResource(gamePath, gamePath, "hash");
        var face = FaceSource("chara/human/c0801/animation/f0002/nonresident/smile.pap", 0, "fp") is var source
            ? source with { Resource = Resource(source.Resource.GamePath, "smile.pap", faceWritable) } : null;
        return Source(standalone: true) with { Resource = Resource("chara/action/facial/pose/smile.tmb", "smile.tmb", tmbWritable), Face = face };
    }

    /// <summary>A face's file, skeleton and clip, and that bone layers go onto the face an expression plays.</summary>
    private static void Sources(Action<bool, string> check)
    {
        check(AnimationEditService.FaceSkeletonPath("chara/human/c0801/animation/f0002/nonresident/smile.pap") ==
              "chara/human/c0801/skeleton/face/f0002/skl_c0801f0002.sklb", "a face's skeleton is its folder's");
        var pap = new AnimationPap(FacePap(("cfxf_base", 17, 0), ("cfxb_blink1", 18, 1), ("cfxf_smile", 17, 2)));
        check(AnimationEditService.FaceEntryProblem(pap, "cfxf_smile") == null && AnimationEditService.FaceEntryProblem(pap, "cfxb_blink1") != null &&
              AnimationEditService.FaceEntryProblem(pap, "cfxf_none") != null, "only a face entry of that name opens");
        var clip = AnimationEditService.FaceClip("chara/human/c0801/animation/f0002/resident/face.pap", "cfxf_smile", pap, 1 / 30f, "fp");
        check(clip is { BindingIndex: 2, Partial: 1, Timeline: 0, BindingFingerprint: "fp" } && clip.SkeletonPath.EndsWith("skl_c0801f0002.sklb") &&
              clip.Duration == 1 / 30f, "a face clip binds its entry on the face skeleton");
        check(AnimationEditService.FaceCaptureId("smile", "chara/x.pap", "cfxf_smile") == "smile|face|chara/x.pap|cfxf_smile",
            "a face opened on its own has a capture of its own");
        var expression = Source(standalone: true) with { Face = Source(standalone: false) };
        check(ReferenceEquals(expression.MotionTarget, expression.Face) && Source(false).MotionTarget.Face == null,
            "layers go onto the face of an expression and onto the clip itself otherwise");
        var held = new MotionReplacement([1, 2, 3], "fp-held", "hash", 1, "neutral face", "", null);
        check(expression.WithReplacement(held).MotionTarget is { BaseMotion: [1, 2, 3], BaseFingerprint: "fp-held" },
            "a hold or Neutral face reaches the face an expression plays");
    }

    private static AnimationTimelineSource Source(bool standalone)
    {
        var clip = new AnimationClip("chara/human/c0801/animation/f0002/nonresident/smile.pap", "cfxf_smile", 0, 1, 50, "chara/test.sklb");
        var capture = new AnimationCapture("smile", 1, 1, Guid.Empty, "Test", "Smile", clip, null, [clip.GamePath], [], DateTime.UtcNow, true);
        var resource = new AnimationResource(standalone ? "chara/action/facial/pose/smile.tmb" : clip.GamePath, "x", "hash");
        return new AnimationTimelineSource(capture, false, clip, resource, [], standalone ? -1 : 0, [], Empty(150), null);
    }

    /// <summary>A PAP of face clips: one entry each, an 8-byte stand-in for the Havok data and an empty timeline per entry.</summary>
    private static byte[] FacePap(params (string Name, short Type, short Binding)[] clips)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)clips.Length);
        writer.Write((short)801); writer.Write((byte)0); writer.Write((byte)0);
        var offsets = stream.Position;
        writer.Write(0); writer.Write(0); writer.Write(0);
        var info = (int)stream.Position;
        foreach (var (name, type, binding) in clips)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            writer.Write(bytes); writer.Write(new byte[32 - bytes.Length]);
            writer.Write(type); writer.Write(binding); writer.Write(1);
        }
        var havok = (int)stream.Position;
        writer.Write(new byte[8]);
        var timeline = (int)stream.Position;
        foreach (var _ in clips)
        {
            writer.Write("TMLB"u8); writer.Write(12); writer.Write(0);
        }
        stream.Position = offsets;
        writer.Write(info); writer.Write(havok); writer.Write(timeline);
        return stream.ToArray();
    }

    /// <summary>The face a document edits: still or moving, its frames on the timeline, the layers it takes and its bake document.</summary>
    private static void Model(Action<bool, string> check, Action<Action, string> reject)
    {
        var still = new TimelineFace(0, 1, 1);
        check(still.Still && !still.LengthChanged && still.FaceFrame(90) == 0 && still.ShownLength(150) == 150,
            "a still face is one shape held over the expression");
        var moving = new TimelineFace(30, 600, 600);
        check(!moving.Still && moving.End == 630 && moving.FaceFrame(330) == 300 && moving.FaceFrame(10) == 0 && moving.ShownLength(150) == 630,
            "a moving face plays from its anchor and the canvas covers all of it");
        check(TimelineFace.Allows(MotionLayerKind.Pose, null) && TimelineFace.Allows(MotionLayerKind.Swing, null) && TimelineFace.Allows(MotionLayerKind.Noise, null) &&
              TimelineFace.Allows(MotionLayerKind.Repeat, null) && TimelineFace.Allows(MotionLayerKind.Spring, null) &&
              TimelineFace.Allows(MotionLayerKind.Fix, MotionFixKind.SmoothJitter) && !TimelineFace.Allows(MotionLayerKind.Pin, null) &&
              !TimelineFace.Allows(MotionLayerKind.Idle, null) && !TimelineFace.Allows(MotionLayerKind.Fix, MotionFixKind.HandShape),
            "a face offers only layers that don't rely on body bones");
        var shifted = TimelineFace.ToFace([Layer(start: 30, end: 90, keyFrame: 45)], 30).Single();
        check(shifted is { Start: 0, End: 60 } && shifted.Keys.Single().Frame == 15, "layers move into the face's own frames");
        var repeat = new MotionLayer("m1", MotionLayerKind.Repeat, "Repeat 1", 60, 120, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            Repeat: new MotionRepeat(30, 60));
        var onFace = TimelineFace.ToFace([repeat], 30).Single();
        var back = TimelineFace.ToFace([onFace], -30).Single();
        check(onFace is { Start: 30, End: 90, Repeat: { From: 0, To: 30, Blend: 4 } } && back.Repeat == repeat.Repeat && MotionEdits.Same(back, repeat),
            "a repeat's source moves into the face's frames with the layer, and back on the timeline when the face is restored");
        // On a face the source starts no earlier than the face, so the panel shows what Live and the save play.
        var faceDoc = TimelineFace.Open(Empty(150), 30, 600, 150) with { Motion = [repeat] };
        var edited = MotionEdits.Find(MotionEdits.Replace(faceDoc, repeat with { Repeat = new MotionRepeat(10, 40, 12) }), "m1")!;
        var before = TimelineFace.ToFace([edited], 30).Single();
        MotionEvaluator.Validate(before);
        check(edited.Repeat == new MotionRepeat(30, 40, 5) && before.Repeat == new MotionRepeat(0, 10, 5),
            "a source set before the face starts at its first frame, its blend fitting what is left");
        var added = MotionEdits.Find(MotionEdits.Add(TimelineFace.Open(Empty(150), 30, 600, 150), MotionLayerKind.Repeat, 45, 120, out var addedKey, out _), addedKey)!;
        var addedOnFace = TimelineFace.ToFace([added], 30).Single();
        check(added.Repeat!.From >= 30 && MotionRepeats.Length(addedOnFace.Repeat!) == 30,
            $"a repeat added on a face takes its 30-frame source from the face (from {added.Repeat.From}, {MotionRepeats.Length(addedOnFace.Repeat!)} face frames)");
        reject(() => TimelineFace.ToFace([PinLayer()], 0), "a pin layer can't go onto a face");
        var opened = TimelineFace.Open(Empty(150), 0, 1, 150);
        check(opened.Face == new TimelineFace(0, 1, 1) && opened is { Length: 150, LengthChanged: false }, "opening a face is not a length change");
        var longer = TimelineFace.WithFrames(opened, 300, 150);
        check(longer.Face!.Frames == 300 && longer.Length == 300 && ReferenceEquals(TimelineFace.WithFrames(longer, 300, 150), longer),
            "a longer face makes the canvas longer");
        var bake = TimelineFace.BakeDocument(longer with { Motion = [Layer(0, 30, 0)], ExcludedBones = ["j_f_hana_l"] }, Empty(1));
        check(bake is { Length: 300, SourceLength: 1 } && bake.Stretched && bake.Motion.Length == 1 && bake.ExcludedBones.Single() == "j_f_hana_l",
            "the face's bake document carries the face's length, layers and left-out bones");

        // A face session kept on disk comes back with its face; one kept before faces, without.
        var dir = Path.Combine(Path.GetTempPath(), "ie-face-autosave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AnimationJournalStore(dir);
            var clip = new AnimationClip("chara/human/c0801/animation/f0002/nonresident/smile.pap", "cfxf_smile", 0, 1, 0, "chara/test.sklb");
            var capture = new AnimationCapture("smile", 1, 1, Guid.NewGuid(), "Test", "Smile", clip, null, [clip.GamePath], [], DateTime.UtcNow, true);
            store.SaveAutosave(new TimelineAutosave(capture, false, "file", null, TimelineFace.Open(Empty(150), 30, 600, 150), [], [], DateTime.UtcNow));
            check(store.LoadAutosave()?.Restored.Face == new TimelineFace(30, 600, 600), "a face session's kept edits come back with its face");
            var file = Path.Combine(dir, "AnimationEdits", "timeline-autosave.json");
            var older = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            older["Document"]!.AsObject().Remove("Face");
            older.AsObject().Remove("FaceHash");
            File.WriteAllText(file, older.ToJsonString());
            check(store.LoadAutosave() is { Restored.Face: null }, "kept edits from before faces load without one");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static TimelineDocument Empty(int length) => new(length, length, null, [], 1);

    private static MotionLayer Layer(int start, int end, int keyFrame) => new("m1", MotionLayerKind.Offset, "Offset 1", start, end, 0, 0, 1,
        PoseComponents.All, MotionInterpolation.Smooth, ["j_f_ago"],
        [new MotionKey(keyFrame, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_f_ago",
            new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f), Vector3.One)))]);

    private static MotionLayer PinLayer() => new("m2", MotionLayerKind.Pin, "Pin 1", 0, 10, 0, 0, 1, PoseComponents.All, MotionInterpolation.Smooth);
}
