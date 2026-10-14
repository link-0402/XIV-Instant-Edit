using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Edit again: which saved edits can bring their bone layers back, the motion a save keeps for it, the
/// capture the clip opens with, that the file must still be the one the edit saved, and that journals keep
/// the kept motion's hash across a restart. Writing and reading the kept motion needs the game; it is
/// checked in game.
/// </summary>
internal static class EditAgainFixture
{
    private const string Pap = "chara/human/c0801/animation/a0001/bt_common/emote/dance.pap";
    private static readonly Guid Collection = Guid.NewGuid();

    public static void Run(Action<bool, string> check)
    {
        Rules(check);
        Captures(check);
        Files(check);
        Undos(check);
        Journals(check);
    }

    private static AnimationClip Clip(string name = "loop", int binding = 0, string path = Pap) =>
        new(path, name, binding, 0, 42, "chara/test.sklb");

    private static AnimationCapture Capture(string id, ulong actor, AnimationClip clip, AnimationClip? startup = null, Guid? collection = null,
        AnimationResource? expression = null, ushort index = 0) =>
        new(id, actor, 1, collection ?? Collection, "Test", "Dance", clip, startup, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""),
            DateTime.UtcNow, true, ExpressionTimeline: expression, ActorIndex: index);

    private static MotionLayer Layer(bool enabled = true) => new("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1,
        PoseComponents.All, MotionInterpolation.Smooth, ["j_kubi"],
        [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kubi",
            new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f), Vector3.One)))], enabled);

    private static AnimationEditJournal Journal(AnimationCapture capture, bool startup = false, string state = "Completed",
        float? seconds = null, string? baseHash = "base", bool layers = true, AnimationOperation operation = AnimationOperation.EditTimeline,
        string afterHash = "after", MotionCuts? cuts = null, ImmutableArray<string> excluded = default)
    {
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false,
            ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, operation,
            ExcludedBones: excluded.IsDefault ? [] : excluded,
            Timeline: operation == AnimationOperation.EditTimeline
                ? new AnimationTimelineEdit(startup, 0, seconds, layers ? [Layer()] : [], baseHash, cuts)
                : null);
        var clip = startup ? capture.Startup! : capture.Clip;
        return new AnimationEditJournal
        {
            Id = request.Id,
            Request = request,
            State = state,
            Files = [new AnimationFileChange(clip.GamePath, "target.pap", "Mod", "root", "files/dance.pap", "before", afterHash, "", "0.staged")],
        };
    }

    private static void Rules(Action<bool, string> check)
    {
        var capture = Capture("a", 7, Clip());
        check(AnimationEditService.EditAgainProblem(Journal(capture)) == null,
            "a finished timeline edit with bone layers and the motion from before it can be edited again");
        check(AnimationEditService.EditAgainProblem(Journal(capture, layers: false)) != null &&
              AnimationEditService.EditAgainProblem(Journal(capture, operation: AnimationOperation.BakeOffsets)) != null,
            "a timeline edit without bone layers can't, nor a LivePose bake");
        check(AnimationEditService.EditAgainProblem(Journal(capture, seconds: 2)) == null,
            "an edit that changed the length can too: the motion from before it was kept");
        var cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]);
        check(AnimationEditService.EditAgainProblem(Journal(capture, seconds: 80 / 30f, layers: false, cuts: cuts)) == null &&
              AnimationEditService.EditAgainProblem(Journal(capture, seconds: 80 / 30f, layers: false, cuts: cuts, baseHash: null)) ==
              "This edit kept no copy of the motion from before it." &&
              AnimationEditService.EditAgainProblem(Journal(capture, layers: false)) == "Only edits with bone layers, cuts or a new length can be edited again." &&
              AnimationEditService.EditAgainProblem(Journal(capture, excluded: ["j_kubi"])) ==
              "This edit left out bones or repaired the skeleton, which can't be opened again.",
            "cuts open again without layers, as long as the motion from before them was kept");
        check(AnimationEditService.EditAgainProblem(Journal(capture, baseHash: null)) != null,
            "nor one that kept no motion from before it, as edits saved before Edit again");
        check(AnimationEditService.EditAgainProblem(Journal(capture, state: "Undone")) == "This edit was undone." &&
              AnimationEditService.EditAgainProblem(Journal(capture, state: "FilesUndone")) == "This edit was not saved." &&
              AnimationEditService.EditAgainProblem(Journal(capture, state: "RecoveryRequired")) == "This edit did not finish.",
            "nor an edit that was undone, not saved (refused or rolled back) or did not finish");

        var doc = new TimelineDocument(30, 30, null, [], 1);
        var layered = doc with { Motion = [Layer()] };
        byte[] file = [1, 2, 3];
        var source = new AnimationTimelineSource(capture, false, capture.Clip, new AnimationResource(Pap, "dance.pap", "file"), file, 0, [], doc, null);
        check(AnimationEditService.MotionBase(source, layered, file) == file &&
              AnimationEditService.MotionBase(source with { BaseMotion = [9] }, layered, file) is [9],
            "a save with bone layers keeps the motion they apply to: the file as it is, or the base the window keeps");
        check(AnimationEditService.MotionBase(source, doc, file) == null &&
              AnimationEditService.MotionBase(source, doc with { Motion = [Layer(false)] }, file) == null &&
              AnimationEditService.MotionBase(source with { EntryIndex = -1 }, layered, file) == null &&
              AnimationEditService.MotionBase(source, layered with { ExcludedBones = ["j_kubi"] }, file) == null,
            "a save without working layers, of an expression's timeline, or leaving out bones keeps none");
        check(AnimationEditService.MotionBase(source, doc with { Length = 40 }, file) == file &&
              AnimationEditService.MotionBase(source, doc with { Cuts = new MotionCuts(30, [new(0, 20)], []), Length = 20 }, file) == file,
            "a save with a new length or cuts keeps the motion from before them");
        check(AnimationEditService.SavedCuts(doc with { Length = 40 }) is { Source: 30, Whole: true } && AnimationEditService.SavedCuts(doc) == null,
            "a stretch records the length of the motion it stretched");
    }

    private static void Captures(Action<bool, string> check)
    {
        var clip = Clip();
        var journal = Journal(Capture("saved", 7, clip, index: 201));
        check(AnimationEditService.EditAgainCapture(journal, []).Id == "saved",
            "with nothing heard since, the clip opens with the capture the edit was made from");
        check(AnimationEditService.EditAgainCapture(journal, [Capture("other", 8, clip)]).Id == "other",
            "a newer capture of the clip in the same collection is used, another character's too");
        check(AnimationEditService.EditAgainCapture(journal, [Capture("other", 8, clip), Capture("mine", 7, clip)]).Id == "mine",
            "the same character's capture comes first");
        var smile = new AnimationResource("chara/action/facial/pose/smile.tmb", "smile.tmb", "x");
        check(AnimationEditService.EditAgainCapture(journal,
            [
                Capture("away", 7, clip, collection: Guid.NewGuid()), Capture("entry", 7, Clip(binding: 1)),
                Capture("name", 7, Clip("other")), Capture("face", 7, clip, expression: smile),
            ]).Id == "saved",
            "captures in another collection, of another entry of the file or of an expression are not the clip");
        var start = Clip("start", path: "chara/human/c0801/animation/a0001/bt_common/emote/dance_start.pap");
        var startup = Journal(Capture("saved", 7, Clip(), start), true);
        check(AnimationEditService.EditAgainCapture(startup, [Capture("loop only", 7, Clip()), Capture("with start", 7, Clip(), start)]).Id ==
              "with start",
            "a startup's edit opens with a capture that has the same startup");
    }

    private static void Files(Action<bool, string> check)
    {
        var capture = Capture("a", 7, Clip());
        var journal = Journal(capture, afterHash: "saved");
        var doc = new TimelineDocument(30, 30, null, [], 1);
        AnimationTimelineSource Loaded(string hash, int entry = 0) =>
            new(capture, false, capture.Clip, new AnimationResource(Pap, "dance.pap", hash), [1], entry, [], doc, null);
        check(AnimationEditService.EditAgainFileProblem(journal, Loaded("saved")) == null,
            "the clip's file as the edit saved it takes the layers back");
        check(AnimationEditService.EditAgainFileProblem(journal, Loaded("newer")) != null &&
              AnimationEditService.EditAgainFileProblem(journal, Loaded("saved", 1)) != null,
            "a file changed since, or another entry of it, doesn't");
        var edit = journal.Request.Timeline!;
        var again = AnimationEditService.WithSavedEdit(Loaded("saved"), edit, [4, 5], "print");
        check(again.Document!.Motion.SequenceEqual(edit.Motion) && again.Document.Events.IsEmpty && again.BaseMotion is [4, 5] &&
              again.BaseHash == "base" && again.MotionSource is [4, 5] && again.MotionClip.BindingFingerprint == "print" && again.Pap is [1],
            "edited again, the layers apply to the motion from before the edit, while the events and the file stay as saved");
        AnimationTimelineSource Saved(int length) =>
            new(capture, false, capture.Clip, new AnimationResource(Pap, "dance.pap", "saved"), [1], 0, [], new TimelineDocument(length, length, null, [], 1), null);
        var cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]);
        var cut = AnimationEditService.WithSavedEdit(Saved(80), new AnimationTimelineEdit(false, 0, 80 / 30f, [], "base", cuts), [4], "print").Document!;
        check(cut is { MotionLength: 100, KeptFrames: 80, Stretched: false, Cut: true }, "a cut edit opens again on the whole motion with its cuts");
        var stretch = AnimationEditService.WithSavedEdit(Saved(150), new AnimationTimelineEdit(false, 0, 5, [], "base", MotionCuts.WholeOf(100)), [4], "print").Document!;
        check(stretch is { Stretched: true, Cut: false } && AnimationEditService.RetimeSeconds(stretch) == 5,
            "a stretched edit opens again on the motion it stretched, still stretched");
        var song = new TimelineEvent("t2", "C063", TimelineLane.Sound, 0, 0, false, "sound/instant_edit/song_cut_ab.scd", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty);
        var withSong = Saved(80) with { Document = new TimelineDocument(80, 80, null, [song], 3) };
        var sounds = ImmutableDictionary<string, string>.Empty.Add("sound/instant_edit/song_cut_ab.scd", "sound/test/song.scd");
        var reopened = AnimationEditService.WithSavedEdit(withSong, new AnimationTimelineEdit(false, 0, 80 / 30f, [], "base", cuts, sounds), [4], "print").Document!;
        check(InstantEdit.Ui.Timeline.TimelineEdits.Find(reopened, "t2")!.Path == "sound/test/song.scd" && reopened.SoundCuts["t2"],
            "a sound the edit cut opens again playing its whole file, chosen to be cut, so saving cuts it once");
    }

    // Undo last save: the edit whose layers the file holds again becomes the window's saved state.
    private static void Undos(Action<bool, string> check)
    {
        var capture = Capture("a", 7, Clip());
        var doc = new TimelineDocument(30, 30, null, [], 1);
        AnimationTimelineSource Loaded(string hash, AnimationCapture? from = null) =>
            new(from ?? capture, false, capture.Clip, new AnimationResource(Pap, "dance.pap", hash), [1], 0, [], doc, null);
        AnimationEditJournal With(AnimationEditJournal journal, string state) => new()
        {
            Id = journal.Id, Request = journal.Request, State = state, Files = journal.Files,
        };
        var a = Journal(capture, afterHash: "A1", baseHash: "B0");
        var a2 = Journal(capture, afterHash: "A2", baseHash: "B0");
        check(AnimationEditService.SavedLayersJournal([a2, a], Loaded("A2")) == a2,
            "after saving A2 over A, the file holds A2's layers");
        ImmutableArray<AnimationEditJournal> undone = [With(a2, "Undone"), a];
        check(AnimationEditService.SavedLayersJournal(undone, Loaded("A1")) == a,
            "undoing A2 brings back A, the edit whose saved file it is again, with its layers on its kept motion");
        var a3 = Journal(capture, afterHash: "A3", baseHash: "B0");
        check(AnimationEditService.SavedLayersJournal([a3, .. undone], Loaded("A3")) == a3 &&
              AnimationEditService.SavedLayersJournal([With(a3, "Undone"), .. undone], Loaded("A1")) == a,
            "A3 saved after that undo holds its own layers, and undoing it goes back to A again");
        var again = AnimationEditService.WithSavedEdit(Loaded("A1"), a.Request.Timeline!, [4], "print");
        check(again.Document!.Motion.SequenceEqual(a.Request.Timeline!.Motion) && again.BaseHash == "B0" && again.BaseMotion is [4],
            "the brought back state is A's layers on the motion from before A, as Edit again opens it");
        check(AnimationEditService.SavedLayersJournal(undone, Loaded("B0")) == null &&
              AnimationEditService.SavedLayersJournal([Journal(capture, afterHash: "E", layers: false), .. undone], Loaded("E")) == null &&
              AnimationEditService.SavedLayersJournal([Journal(capture, afterHash: "A1", baseHash: null)], Loaded("A1")) == null,
            "a file no layered edit saved, an events-only save or an edit that kept no motion brings nothing back");
        check(AnimationEditService.SavedLayersJournal(undone, Loaded("A1", Capture("b", 7, Clip(), collection: Guid.NewGuid()))) == null &&
              AnimationEditService.SavedLayersJournal(undone, Loaded("A1") with { EntryIndex = -1 }) == null &&
              AnimationEditService.SavedLayersJournal(undone, Loaded("A1") with { Document = null }) == null,
            "nor one in another collection, an expression's timeline or a clip that can't be edited");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-edit-again-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var journal = Journal(Capture("a", 7, Clip()));
            var store = new AnimationJournalStore(temp);
            store.Save(new AnimationEditJournal
            {
                Id = journal.Id, Request = AnimationCommitService.RecoveryRequest(journal.Request), State = "Completed", Files = journal.Files,
            });
            var reloaded = new AnimationJournalStore(temp).Load().Single();
            check(reloaded.Request.Timeline!.MotionBaseHash == "base" && AnimationEditService.EditAgainProblem(reloaded) == null,
                "a journal keeps the hash of the motion from before its edit across a restart");

            // Journals written before Edit again have no MotionBaseHash.
            var path = Path.Combine(temp, "AnimationEdits", journal.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["Request"]!["Timeline"]!.AsObject().Remove("MotionBaseHash");
            File.WriteAllText(path, json.ToJsonString());
            var legacy = new AnimationJournalStore(temp).Load().Single();
            check(legacy.Request.Timeline!.MotionBaseHash == null && legacy.Request.Timeline.Motion.Length == 1 &&
                  AnimationEditService.EditAgainProblem(legacy) == "This edit kept no copy of the motion from before it.",
                "an older journal loads, and says why its edit can't be opened again");
        }
        finally { Directory.Delete(temp, true); }
    }
}
