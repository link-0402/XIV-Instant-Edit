using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Cutting a timeline document: the cut list it keeps, and the ripple that moves, shortens or
/// removes its events and bone layer keys when frames go or come back.
/// </summary>
internal static class CutEditsFixture
{
    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    private static TimelineEvent Ev(string key, TimelineLane lane, int start, int duration = 0, bool resizable = false,
        TimelineAccess access = TimelineAccess.Editable, string magic = "C063") =>
        new(key, magic, lane, start, duration, resizable, "", access, access == TimelineAccess.Editable ? null : "Kept as is", false, NoValues);

    /// <summary>
    /// A 100-frame clip: its motion (locked, the whole length), sounds at 10, 45 and 70, an effect over
    /// 30 to 60, a locked entry at 50, and an offset layer over 20 to 90 with keys at 20, 40, 50, 60 and 90.
    /// </summary>
    internal static TimelineDocument Doc(int length)
    {
        var keys = new[] { 20, 40, 50, 60, 90 }
            .Select(f => new MotionKey(f, ImmutableDictionary<string, BoneTransform>.Empty))
            .ToImmutableArray();
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "Offset", 20, 90, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [], keys);
        return new TimelineDocument(length, length, null,
        [
            Ev("t1", TimelineLane.Motion, 0, length, access: TimelineAccess.Locked, magic: "C009"),
            Ev("t2", TimelineLane.Sound, 10),
            Ev("t3", TimelineLane.Sound, 45),
            Ev("t4", TimelineLane.Effect, 30, 30, resizable: true, magic: "C012"),
            Ev("t5", TimelineLane.Sound, 70),
            Ev("t6", TimelineLane.Other, 50, access: TimelineAccess.Locked, magic: "C031"),
        ], 1) { Motion = [layer] };
    }

    internal static TimelineEvent? Find(TimelineDocument doc, string key) => TimelineEdits.Find(doc, key);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Ripple(check);
        Editor(check);
        Bake(check);
    }

    private static void Bake(Action<bool, string> check)
    {
        var doc = Doc(100);
        var cutDoc = CutEdits.RemoveRange(doc, 40, 60, out _);
        check(AnimationBakeRules.ChangesMotion(cutDoc) && AnimationEditService.RetimeSeconds(cutDoc) == 80 / 30f &&
              AnimationEditService.RetimeSeconds(CutEdits.SetSeam(doc, MotionJoin.ForSeam)) == 100 / 30f &&
              AnimationEditService.RetimeSeconds(doc) == null && !AnimationBakeRules.ChangesMotion(doc),
            "cuts bake and always carry the output length; an untouched document bakes nothing");
        check(AnimationBakeRules.ChangesMotion(CutEdits.SetEase(doc, 30)) && AnimationEditService.RetimeSeconds(CutEdits.SetEase(doc, 30)) == 100 / 30f,
            "an eased start bakes at the same length");
        // A save that cuts or stretches leaves a timeline in other frames than the motion from before it: the
        // window reads the saved file back as it is, and Edit again brings the cuts and layers back.
        check(AnimationBakeRules.KeepsLayersOverSave(doc) && !AnimationBakeRules.KeepsLayersOverSave(cutDoc) &&
              !AnimationBakeRules.KeepsLayersOverSave(doc with { Length = 120 }) && !AnimationBakeRules.KeepsLayersOverSave(CutEdits.SetEase(doc, 30)) &&
              !AnimationBakeRules.KeepsLayersOverSave(doc with { ExcludedBones = ["j_kosi"] }),
            "only a save that keeps the motion's frames goes on editing its layers on the motion from before");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var journalJson = JsonSerializer.Serialize(new AnimationTimelineEdit(false, 0, 80 / 30f, [], null, cutDoc.Cuts), options);
        check(JsonSerializer.Deserialize<AnimationTimelineEdit>(journalJson, options)!.Cuts!.Parts.Length == 2 &&
              JsonSerializer.Deserialize<AnimationTimelineEdit>("{\"Startup\":false,\"EntryIndex\":0,\"DurationSeconds\":null}", options)!.Cuts == null,
            "journals carry cuts, and older ones load without");
        var cutSounds = ImmutableDictionary<string, string>.Empty.Add("sound/instant_edit/song_cut_ab.scd", "sound/test/song.scd");
        var withSounds = JsonSerializer.Serialize(new AnimationTimelineEdit(false, 0, 80 / 30f, [], null, cutDoc.Cuts, cutSounds), options);
        check(JsonSerializer.Deserialize<AnimationTimelineEdit>(withSounds, options)!.CutSounds!["sound/instant_edit/song_cut_ab.scd"] == "sound/test/song.scd" &&
              JsonSerializer.Deserialize<AnimationTimelineEdit>(journalJson, options)!.CutSounds == null,
            "journals carry the sounds a save cut and what they were cut from, and older ones load without");
    }

    private static void Editor(Action<bool, string> check)
    {
        var doc = Doc(100);
        var g = new TimelineGeometry(100, 20, 20, 5, 9, 0, 0, 30);
        var view = new TimelineView(0, 4, 400);                          // 4 px per frame
        var two = CutEdits.RemoveRange(doc, 40, 60, out _);              // boundary at output 40 = x 260
        TimelineHit At(TimelineDocument d, float x, float y = 35, bool loops = false) =>
            g.HitTest(d, TimelineLayout.Build(d), view, new Vector2(x, y), loops);
        check(At(two, 150) is { Part: TimelinePart.ClipPart, Cut: 0 } && At(two, 258) is { Part: TimelinePart.ClipRight, Cut: 0 } &&
              At(two, 262) is { Part: TimelinePart.ClipLeft, Cut: 1 }, "parts and their edges");
        check(At(two, 260 - 24) is { Part: TimelinePart.JoinEdge, Cut: 0 } && At(two, 245) is { Part: TimelinePart.Join, Cut: 0 },
            "a centered 12-frame join: its edge 6 frames out, its body");
        var looped = CutEdits.SetSeam(two, MotionJoin.ForSeam);         // Before, frames 68-80 = x 372-420
        check(At(looped, 372, loops: true).Part == TimelinePart.SeamEdge && At(looped, 385, loops: true).Part == TimelinePart.Seam &&
              At(looped, 385, loops: false).Part == TimelinePart.ClipPart && At(looped, 419, loops: true) is { Part: TimelinePart.ClipRight, Cut: 1 },
            "the seam is hit only on a looping clip; the clip's end stays a trim edge");
        check(g.RowTop(0) == 50 && At(two, 150, 55).Part != TimelinePart.ClipPart, "rows start under the clip band");
        var editor = new TimelineEditor(doc) { Loops = false };
        editor.Playhead = 30;
        check(editor.Key(TimelineKey.Cut) && editor.Document.Cuts!.Parts.Length == 2, "C cuts at the playhead");
        editor.Playhead = 70;
        check(editor.Key(TimelineKey.RemoveAfter) && editor.Document.Length == 70, "W removes from the playhead to the part's end");
        check(!editor.SetSeam(MotionJoin.ForSeam) && !editor.FixLoop(new SeamReport(new JoinGap(0, 0, "", 0, 0), 1, null, SeamState.Clean)) &&
              !editor.SetEase(30), "a clip that doesn't loop takes no seam");
        editor.SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, 0));
        check(editor.Key(TimelineKey.Delete) && editor.Document.Length == 40 && editor.Undo() && editor.Document.Length == 70, "Delete removes a part; undo brings it back");
        // Q at 50, on the part playing 30 to 70, removes 30 to 50: the events starting at 30 and 45, and the key at 40.
        editor.Playhead = 50;
        check(editor.Key(TimelineKey.RemoveBefore) && editor.Document.Length == 50 && editor.LastRemoval == new CutRemoval(2, 1),
            "Q removes from the part's start to the playhead and says what went");
        editor.Press(At(editor.Document, 150, 200));
        check(editor.LastRemoval == null, "the next click forgets what the last cut removed");

        var dragging = new TimelineEditor(two) { Snap = false };
        dragging.Press(At(two, 419));                                     // the clip's end, frame 80
        dragging.Drag(70);
        dragging.Release();
        check(dragging.Document is { Length: 70, Cuts.Parts: [_, { To: 90 }] } && dragging.Undo() && dragging.Document.Length == 80,
            "dragging the clip's end trims it as one undo step");
        dragging.Press(At(two, 236));                                     // the join's left edge, frame 34
        dragging.Drag(31);
        dragging.Release();
        check(dragging.Document.Cuts!.Joins[0].Frames == 18 && dragging.SelectedCut == new TimelineCutSelection(TimelineCutTarget.Join, 0),
            "dragging a centred blend's edge 3 frames out makes it 6 frames longer");

        var eased = CutEdits.SetEase(two, 20);                            // frames 0-20 = x 100-180
        check(At(eased, 180, loops: true).Part == TimelinePart.EaseEdge && At(eased, 150, loops: true).Part == TimelinePart.Seam &&
              At(eased, 180).Part == TimelinePart.ClipPart, "the ease's edge and body are hit only on a looping clip");
        var easing = new TimelineEditor(eased) { Snap = false, Loops = true };
        var steps = easing.History.UndoCount;
        easing.Press(At(eased, 180, loops: true));
        easing.Drag(26);
        easing.Release();
        check(easing.Document.Cuts!.Ease == 26 && easing.History.UndoCount == steps + 1 &&
              easing.SelectedCut == new TimelineCutSelection(TimelineCutTarget.Seam, -1),
            "dragging the ease's edge sets its length as one undo step and selects the seam");
        var endEased = CutEdits.SetEase(two, 20, atEnd: true);            // output frames 60-80 = x 340-420
        check(At(endEased, 340, loops: true).Part == TimelinePart.EaseEdge && At(endEased, 380, loops: true).Part == TimelinePart.Seam,
            "an end's ease is hit at the end");
        var easingEnd = new TimelineEditor(endEased) { Snap = false, Loops = true };
        easingEnd.Press(At(endEased, 340, loops: true));
        easingEnd.Drag(56);
        easingEnd.Release();
        check(easingEnd.Document.Cuts is { Ease: 24, EaseEnd: true }, "dragging an end's ease edge left makes it longer");
    }

    private static void Ripple(Action<bool, string> check)
    {
        var doc = Doc(100);
        var cut = CutEdits.RemoveRange(doc, 40, 60, out var gone);
        check(cut.Length == 80 && cut.Cuts is { Parts: [{ From: 0, To: 40 }, { From: 60, To: 100 }], Joins: [var j] } && j == MotionJoin.ForCut,
            "removing 40-60 leaves two parts and a default join");
        check(Find(cut, "t1")!.Duration == 80 && Find(cut, "t2")!.Start == 10 && Find(cut, "t3") == null &&
              Find(cut, "t4") is { Start: 30, Duration: 10 } && Find(cut, "t5")!.Start == 50 && Find(cut, "t6")!.Start == 40,
            "events ripple: inside goes, spanning shortens, after moves left, locked moves to the cut");
        check(cut.Motion[0] is { Start: 20, End: 70 } m && m.Keys.Select(k => k.Frame).SequenceEqual([20, 40, 70]) && gone == new CutRemoval(1, 2),
            "a key at the range's start goes, the key at its end lands on it");
        check(ReferenceEquals(CutEdits.CutAt(cut, 40), cut) && CutEdits.CutAt(doc, 30).Cuts!.Parts.Length == 2 && CutEdits.CutAt(doc, 30).Length == 100,
            "a cut on a boundary changes nothing; a new cut keeps the length");
        var back = CutEdits.TrimPart(cut, 1, left: true, -20, out _);
        check(back.Cuts == null && back.Length == 100 && Find(back, "t5")!.Start == 70, "trimming back over the removed frames merges the parts");
        var half = CutEdits.TrimPart(cut, 1, left: true, -10, out _);
        check(half.Cuts!.Parts[1] == new MotionPart(50, 100) && half.Length == 90 && Find(half, "t5")!.Start == 60, "a partial trim back inserts frames");
        var stretched = CutEdits.RemoveRange(doc with { Length = 200 }, 80, 120, out _);
        check(stretched.Length == 160 && stretched.Cuts!.Parts[1].From == 60, "a stretch maps output frames to source frames");
        var slowEnd = CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", .2f, 1.6f), 1.4f, null, SeamState.Stalls));
        check(slowEnd is { Length: 100, Cuts: { Parts: [{ From: 0, To: 100 }], Seam: null, Ease: MotionCuts.DefaultEase } },
            "Fix loop eases the start of a loop that arrives slower than it leaves, trimming nothing");
        var held = CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", 0, 1.6f), 1.4f, 88, SeamState.Stalls));
        check(held is { Length: 88, Cuts: { Parts: [{ To: 88 }], Seam: null, Ease: MotionCuts.DefaultEase } },
            "Fix loop trims an end that holds frame 0's pose, then eases the start");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(20, 30, "x", 3, 3), 3, null, SeamState.Pops)) is
              { Length: 100, Cuts: { Seam: not null, Ease: 0 } },
            "a pose jump gets a blend and keeps the length");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(20, 30, "x", .2f, 3), 3, null, SeamState.Stalls)) is
              { Cuts: { Seam: not null, Ease: 0 } },
            "a pose jump into a faster start gets only the blend, which matches the speeds too");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", 3, .5f), 1.4f, null, SeamState.Stalls)) is
              { Length: 100, Cuts: { Ease: MotionCuts.DefaultEase, EaseEnd: true, Seam: null } } &&
              slowEnd.Cuts!.EaseEnd == false, "a loop that arrives faster than it leaves eases its end instead");
        // A blend and an ease fight over the speed at the seam: Fix loop keeps one of them.
        check(CutEdits.FixLoop(slowEnd, new SeamReport(new JoinGap(20, 30, "x", 3, 3), 3, null, SeamState.Pops)).Cuts is { Seam: not null, Ease: 0 } &&
              CutEdits.FixLoop(CutEdits.SetSeam(doc, MotionJoin.ForSeam), new SeamReport(new JoinGap(0, 0, "", .2f, 1.6f), 1.4f, null, SeamState.Stalls)).Cuts is
                  { Seam: null, Ease: MotionCuts.DefaultEase },
            "Fix loop sets a blend or an ease, never both");
        check(CutEdits.SetEase(doc, 90).Cuts!.Ease == 60 && CutEdits.SetEase(CutEdits.SetEase(doc, 20), 0).Cuts == null &&
              CutEdits.SetEase(doc, 20, atEnd: true).Cuts is { Ease: 20, EaseEnd: true } && CutEdits.SetEase(slowEnd, 30, atEnd: true).Cuts!.EaseEnd,
            "an ease clamps to 60, takes a side, and none leaves the motion whole");
        check(ReferenceEquals(CutEdits.RemoveRange(doc, 1, 100, out _), doc), "a cut leaving under 2 frames is refused");
        var expression = doc with { Expression = true };
        check(ReferenceEquals(CutEdits.RemoveRange(expression, 10, 20, out _), expression), "an expression's timeline is never cut");
        check(CutEdits.SetJoin(cut, 0, new MotionJoin(90, JoinPlacement.After)).Cuts!.Joins[0] == new MotionJoin(60, JoinPlacement.After), "blends clamp to 60");
        check(cut is { MotionLength: 100, KeptFrames: 80, Cut: true, Stretched: false } && doc is { Cut: false, Stretched: false, KeptFrames: 100 } &&
              stretched is { KeptFrames: 80, Stretched: true }, "a document knows its motion's length, the frames it keeps and whether it is cut or stretched");
    }
}
