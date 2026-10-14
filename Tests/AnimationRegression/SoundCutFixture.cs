using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Sounds across cuts: which spans of a sound's audio a cut takes out so it stays in step with the
/// motion, and the per-sound choice to cut it or leave it whole.
/// </summary>
internal static class SoundCutFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Spans(check);
    }

    private static void Spans(Action<bool, string> check)
    {
        var d = CutEdits.RemoveRange(CutEditsFixture.Doc(100), 40, 60, out _);                    // join at 40, 20 frames removed
        check(TimelineSoundCuts.Spans(d, 0, 10).SequenceEqual([new TimelineAudioSpan(40 / 30.0, 20 / 30.0)]), "a song from frame 0 loses the removed 20 frames at 1.33 s");
        check(TimelineSoundCuts.Spans(d, 50, 10).IsEmpty && TimelineSoundCuts.Spans(d, 0, 1).IsEmpty, "sounds after the cut or ending before it don't cross");
        var trimmed = CutEdits.RemoveRange(CutEditsFixture.Doc(100), 88, 100, out _);
        check(TimelineSoundCuts.Spans(trimmed, 0, 3.4).SequenceEqual([new TimelineAudioSpan(88 / 30.0, double.PositiveInfinity)]), "a trimmed end cuts the tail");
        check(TimelineSoundCuts.Spans(trimmed, 0, 2.9).IsEmpty, "a sound ending before the new end is whole");
        // Two cuts: the second span starts where the audio is once the first is gone, which is the output's time.
        var twice = CutEdits.RemoveRange(d, 60, 70, out _);                                         // output 60-70 = source 80-90
        check(TimelineSoundCuts.Spans(twice, 0, 10).SequenceEqual([new TimelineAudioSpan(40 / 30.0, 20 / 30.0), new TimelineAudioSpan(60 / 30.0, 10 / 30.0)]),
            "each span starts in the audio as the spans before it left it");
        var song = CutEditsFixture.Find(d, "t2")!;                                                 // a sound at 10
        check(TimelineSoundCuts.Crosses(d, song, 2) && !TimelineSoundCuts.Crosses(d, song, .5), "a sound crosses a cut when it plays past it");

        var editor = new TimelineEditor(d);
        check(editor.SetSoundCut("t2", true) && editor.Document.SoundCuts["t2"] && editor.SetSoundCut("t2", false) && !editor.Document.SoundCuts["t2"] &&
              editor.SetSoundCut("t2", null) && !editor.Document.SoundCuts.ContainsKey("t2") && !editor.SetSoundCut("t2", null) &&
              editor.Undo() && !editor.Document.SoundCuts["t2"], "the choice per sound is one undo step each, and clearing it asks again");
        check(CutEditsFixture.Doc(100).SoundCuts.IsEmpty && (CutEditsFixture.Doc(100) with { SoundCuts = null! }).SoundCuts.IsEmpty,
            "a document starts with no choices");
    }
}
