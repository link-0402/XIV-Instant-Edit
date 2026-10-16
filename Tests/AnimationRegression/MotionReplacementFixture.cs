using System.Collections.Immutable;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>A motion from Blender in the timeline document: the swap, its length, undo, and what saving does with it.</summary>
internal static class MotionReplacementFixture
{
    private static MotionReplacement Replacement(int frames) =>
        new([1, 2, 3], "blender-print", "blender-hash", frames, "Walk", "Body", new BlenderMotionReport(4, 1, 0, false));

    private static TimelineEvent Find(TimelineDocument doc, string key) => doc.Events.First(ev => ev.Key == key);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // A 100-frame clip with its whole-length motion entry, sounds at 10, 45 and 70, a bone layer and cuts.
        var loaded = CutEditsFixture.Doc(100);
        var doc = loaded with { Cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]) };

        var kept = TimelineEdits.ReplaceMotion(doc, Replacement(150), false, loaded);
        check(kept.Length == 150 && kept.MotionLength == 150 && !kept.Stretched && !kept.Cut && kept.Cuts == null && kept.Motion.IsEmpty &&
              kept.Replacement?.Hash == "blender-hash" && Find(kept, "t3").Start == 45 && Find(kept, "t1").Duration == 150,
            "bringing a longer motion in clears layers and cuts and keeps events in place");

        var scaled = TimelineEdits.ReplaceMotion(doc, Replacement(150), true, loaded);
        check(Find(scaled, "t5").Start == 105 && Find(scaled, "t1").Duration == 150, "with Scale events on, events move with the new length");

        var editor = new TimelineEditor(doc);
        var before = editor.Document;
        var replaced = editor.ReplaceMotion(Replacement(150));
        var after = editor.Document;
        editor.Undo();
        var undone = editor.Document;
        editor.Redo();
        check(replaced && ReferenceEquals(undone, before) && !undone.Motion.IsEmpty && undone.Cuts != null &&
              ReferenceEquals(editor.Document, after) && editor.Document.Replacement != null,
            "the swap is one undo step");

        check(editor.DropReplacement() && editor.Document.Replacement == null && editor.Document.Length == 100 &&
              editor.Document.MotionLength == 100 && !editor.Document.Stretched,
            "Use the clip's motion goes back to the file's");

        var longer = TimelineEdits.SetLength(kept, 200, false);
        check(Find(longer, "t1").Duration == 200 && longer.Stretched && longer.Replacement != null,
            "a later length change still stretches the replaced motion");

        check(AnimationBakeRules.NewMotion(kept) && !AnimationBakeRules.ChangesMotion(kept) && !AnimationBakeRules.NewMotion(loaded with { Motion = [] }),
            "a replacement alone is saved without baking again");

        var face = new TimelineEditor(new TimelineDocument(150, 150, null, [], 1) { Expression = true });
        check(!face.ReplaceMotion(Replacement(150)) && face.Document.Replacement == null, "an expression's timeline takes no motion");

        // Blender moved no bone and kept the length: nothing to build while the timeline plays the clip's motion as it is.
        var plain = new TimelineDocument(100, 100, null, [], 1);
        BlenderMotionReport Moved(int changed) => new(4, changed, 0, false);
        check(BlenderMotion.ChangesNothing(Moved(0), 1, 1 + 5e-7f, plain) &&
              !BlenderMotion.ChangesNothing(Moved(1), 1, 1, plain) &&
              !BlenderMotion.ChangesNothing(Moved(0), 1.5f, 1, plain) &&
              !BlenderMotion.ChangesNothing(Moved(0), 1, 1, loaded) &&
              !BlenderMotion.ChangesNothing(Moved(0), 1, 1, plain with { Cuts = doc.Cuts }) &&
              !BlenderMotion.ChangesNothing(Moved(0), 1, 1, TimelineEdits.SetLength(plain, 120, false)),
            "a motion from Blender that changes nothing builds nothing, unless layers, cuts or a stretch go with it");

        Session(check);
    }

    /// <summary>The window keeps the clip's skeleton match every frame; the motion from Blender must stay the document's alone.</summary>
    private static void Session(Action<bool, string> check)
    {
        var session = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!) { Source = BlenderMotionFixture.Source() };
        session.Editor = new TimelineEditor(session.Source.Document!);
        session.Editor.ReplaceMotion(Replacement(45));
        // What the window does every frame.
        session.KeepMatch();
        var replaced = session.Fresh(session.Source);
        session.Editor.Undo();
        var undone = session.Fresh(session.Source);
        check(replaced.BaseHash == "blender-hash" && undone.BaseMotion == null && undone.BaseHash == null,
            "undoing the motion from Blender brings the clip's own motion back");

        // A looping 1 s clip given a 45-frame motion from Blender: layers wrap their keys at the new motion's end.
        var looping = BlenderMotionFixture.Source() is var source ? source with { Clip = source.Clip with { Duration = 1, IsLoop = true } } : null!;
        var longer = TimelineEdits.ReplaceMotion(looping.Document!, Replacement(45), false);
        check(BonePosing.MotionEnd(looping, longer) == 45 && BonePosing.LoopFrames(looping, longer) == 45 &&
              BonePosing.MotionEnd(looping, looping.Document!) == 30,
            "bone layers wrap at the end of the motion from Blender");

        Live(check);
    }

    /// <summary>Live in GPose: the game still plays the clip's own file, so a motion from Blender is always swapped in.</summary>
    private static void Live(Action<bool, string> check)
    {
        check(InstantEdit.Services.Animations.Preview.MotionLiveDisplay.Copies([], [], null, 0, baseDiffers: true) &&
              !InstantEdit.Services.Animations.Preview.MotionLiveDisplay.Copies([], [], null, 0, baseDiffers: false),
            "Live shows a motion from Blender even without layers or cuts");
        short[] original = [0, 3, 5];
        check(InstantEdit.Services.Animations.Preview.MotionLiveDisplay.TracksFit(original, [0, 3, 5, 7]) &&
              InstantEdit.Services.Animations.Preview.MotionLiveDisplay.TracksFit([0, 3, 5, 7, 9], [0, 3, 5]) &&
              !InstantEdit.Services.Animations.Preview.MotionLiveDisplay.TracksFit(original, [0, 5, 3, 7]),
            "Live takes the playing clip whose tracks begin the motion from Blender's, or the other way round");
    }
}
