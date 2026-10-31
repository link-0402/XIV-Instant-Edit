using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline and Bones windows' chrome rules that need no game: which status notes show and in what order, the Start button's
/// label and which selection the Edit menu shows.
/// How they look is checked in game.
/// </summary>
internal static class TimelineChromeFixture
{
    /// <summary> Runs every chrome check. </summary>
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Status(check);
        Start(check);
        Selection(check);
    }

    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    /// <summary> An event shaped like TimelineEditorFixture's. </summary>
    private static TimelineEvent Ev(string key, TimelineLane lane, int start, int duration = 0, bool resizable = false,
        TimelineAccess access = TimelineAccess.Editable, string? group = null, string magic = "C063", string path = "") =>
        new(key, magic, lane, start, duration, resizable, path, access, access == TimelineAccess.Editable ? null : "Kept as is", false, NoValues, group);

    /// <summary> An offset layer "m1" over frames 0 to 60 with one key, as LayerWeightFixture's. </summary>
    private static MotionLayer Offset() =>
        new("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kosi", new(Vector3.Zero, Quaternion.Identity, Vector3.One)))]);

    /// <summary> A 100-frame document with <paramref name="events"/> on the lanes and <paramref name="layers"/> as its bone layers. </summary>
    internal static TimelineDocument Doc(TimelineEvent[] events, params MotionLayer[] layers) =>
        new TimelineDocument(100, 100, null, [.. events], 1) { Motion = [.. layers], NextMotionKey = 2 };

    /// <summary> The Edit menu shows the selection's items: a cut first, then a bone layer, then events, else nothing. </summary>
    private static void Selection(Action<bool, string> check)
    {
        var doc = Doc([Ev("e1", TimelineLane.Sound, 10, 20, true)], Offset());
        var editor = new TimelineEditor(doc);
        check(TimelineCanvas.SelectionMenuOf(editor) == SelectionMenu.None, "Edit menu: nothing selected");
        editor.Select("e1");
        check(TimelineCanvas.SelectionMenuOf(editor) == SelectionMenu.Events, "Edit menu: an event");
        editor.SelectLayer("m1");
        check(TimelineCanvas.SelectionMenuOf(editor) == SelectionMenu.Layer, "Edit menu: a bone layer");
        editor.SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
        check(TimelineCanvas.SelectionMenuOf(editor) == SelectionMenu.Cut, "Edit menu: the loop seam");
        editor.ClearSelection();
        check(TimelineCanvas.SelectionMenuOf(editor) == SelectionMenu.None, "Edit menu: cleared");
    }

    /// <summary> The header's Start button names the unfinished edits. </summary>
    private static void Start(Action<bool, string> check)
    {
        check(TimelineWindow.StartLabel(0) == "Start" && TimelineWindow.StartLabel(2) == "Start (2)", "Start shows the unfinished edits");
    }

    /// <summary> The status line: notes sort by kind, a dismissed note stays hidden until its text changes, work running never hides. </summary>
    private static void Status(Action<bool, string> check)
    {
        var work = new StatusNote("decode", StatusKind.Working, "Reading the motion 40%");
        var error = new StatusNote("error", StatusKind.Error, "Nothing to paste.");
        var warn = new StatusNote("effects", StatusKind.Warning, "2 effects stop after the first loop.");
        var note = new StatusNote("notice", StatusKind.Notice, "Copied 3 bones.");
        var shown = StatusLine.Shown([note, warn, error, work], new Dictionary<string, string>());
        check(shown.Select(n => n.Key).SequenceEqual(["decode", "error", "effects", "notice"]), "Status notes order: working, errors, warnings, notices");
        var two = StatusLine.Shown([new StatusNote("a", StatusKind.Notice, "A"), new StatusNote("b", StatusKind.Notice, "B")], new Dictionary<string, string>());
        check(two.Select(n => n.Key).SequenceEqual(["a", "b"]), "Status notes keep their order within a kind");
        check(StatusLine.Shown([note], new Dictionary<string, string> { ["notice"] = "Copied 3 bones." }).IsEmpty, "A dismissed note stays hidden while its text is the same");
        check(StatusLine.Shown([note with { Text = "Copied 4 bones." }], new Dictionary<string, string> { ["notice"] = "Copied 3 bones." }).Length == 1, "A dismissed note comes back when its text changes");
        check(StatusLine.Shown([work], new Dictionary<string, string> { ["decode"] = "Reading the motion 40%" }).Length == 1, "Work running is never dismissed");
        check(StatusLine.MoreText(1) == "+1 note" && StatusLine.MoreText(2) == "+2 notes", "More notes read +1 note and +2 notes");

        var line = new StatusLine();
        line.Dismiss(note);
        var hiddenFirst = line.Visible([note]).IsEmpty;
        var changed = line.Visible([note with { Text = "Copied 4 bones." }]).Length == 1;
        check(hiddenFirst && changed && line.Visible([note]).Length == 1, "A dismissal ends when the text changes, so the old text shows again");
        var paste = new StatusNote("error", StatusKind.Error, "Nothing to paste.");
        line.Dismiss(paste);
        var hiddenPaste = line.Visible([paste]).IsEmpty;
        line.Visible([]);
        check(hiddenPaste && line.Visible([paste]).Length == 1, "A dismissal ends when the note goes away, so the same text shows again");

        check(StatusLine.TextRoom(760, 300, 200) == 300, "A first note that fits keeps all its text");
        check(StatusLine.TextRoom(760, 700, 200) == 560, "A long first note is cut to the room its buttons and +N notes leave");
        check(StatusLine.TextRoom(150, 700, 200) == 0, "A first note never gets less than no room");
    }
}
