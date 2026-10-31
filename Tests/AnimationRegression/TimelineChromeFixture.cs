using InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline and Bones windows' chrome rules that need no game: which status notes show and in what order.
/// How they look is checked in game.
/// </summary>
internal static class TimelineChromeFixture
{
    /// <summary> Runs every chrome check. </summary>
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Status(check);
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
