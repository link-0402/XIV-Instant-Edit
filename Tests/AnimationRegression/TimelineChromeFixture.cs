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
/// label, the Animations button's tooltip, the start page's entries, which selection the Edit menu and the side panel show,
/// the clip view's Bones kept line, the layer settings' summary line, the Bones window's layer strip and which layers get its
/// breakdown row.
/// How they look is checked in game.
/// </summary>
internal static class TimelineChromeFixture
{
    /// <summary> Runs every chrome check. </summary>
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Status(check);
        Start(check);
        Unfinished(check);
        StartEntries(check);
        Selection(check);
        SideViews(check);
        Kept(check);
        Summary(check);
        Strip(check);
        Breakdown(check);
        FreeCamera(check);
    }

    /// <summary>
    /// The free camera's motion: its pose from the game camera's spot and look-at, turns, moves and the wheel's speed steps.
    /// The hook and the keys are checked in game.
    /// </summary>
    private static void FreeCamera(Action<bool, string> check)
    {
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        var at = new Vector3(1, 2, 3);
        var look = Vector3.Normalize(new Vector3(-2, 1, -4));
        var pose = FreeCameraMotion.From(at, at + look * 3);
        check(pose.Position == at && Near(FreeCameraMotion.Forward(pose), look), "Free camera: looks where the game camera looked");
        check(Near(FreeCameraMotion.LookAt(pose), at + look), "Free camera: its look-at is a metre ahead");
        check(FreeCameraMotion.From(at, at) == new FreeCameraPose(at, 0, 0), "Free camera: a look-at on the spot looks level along +Z");
        var level = pose with { Pitch = 0 };
        check(Near(FreeCameraMotion.Right(level), Vector3.Normalize(Vector3.Cross(FreeCameraMotion.Forward(level), Vector3.UnitY))),
            "Free camera: right is forward × up, as on the game's camera");

        var turned = FreeCameraMotion.Turn(pose, 0.5f, 0.1f);
        check(MathF.Abs(turned.Yaw - pose.Yaw - 0.5f) < 1e-5f && MathF.Abs(turned.Pitch - pose.Pitch - 0.1f) < 1e-5f, "Free camera: turns as the game's angles do");
        check(FreeCameraMotion.Turn(pose, 0, 10).Pitch == FreeCameraMotion.MaxPitch && FreeCameraMotion.Turn(pose, 0, -10).Pitch == -FreeCameraMotion.MaxPitch,
            "Free camera: never looks straight up or down");
        check(MathF.Abs(FreeCameraMotion.Turn(pose with { Yaw = 3 }, 1, 0).Yaw - (4 - MathF.Tau)) < 1e-5f, "Free camera: the yaw stays within a turn");
        check(FreeCameraMotion.Turn(pose, float.NaN, 0) == pose, "Free camera: a broken turn is ignored");

        var ahead = FreeCameraMotion.Step(pose, new FreeCameraInput(1, 0, 0, false), 2, 1);
        check(Near(ahead.Position, at + look * 2), "Free camera: W flies where it looks");
        check(Near(FreeCameraMotion.Step(pose, new FreeCameraInput(1, 0, 0, true), 2, 1).Position, at + look * 2 * FreeCameraMotion.FastFactor), "Free camera: Shift goes faster");
        check(Near(FreeCameraMotion.Step(pose, new FreeCameraInput(0, 0, -1, false), 2, 0.5f).Position, at - Vector3.UnitY), "Free camera: Ctrl goes straight down");
        var slant = FreeCameraMotion.Step(pose, new FreeCameraInput(1, 1, 1, false), 2, 1);
        check(MathF.Abs(Vector3.Distance(slant.Position, at) - 2) < 1e-4f, "Free camera: a slant isn't faster");
        check(FreeCameraMotion.Step(pose, new FreeCameraInput(1, 0, 0, false), 2, -1) == pose, "Free camera: no time, no move");

        check(MathF.Abs(FreeCameraMotion.Speed(2, 1) - 2 * FreeCameraMotion.WheelStep) < 1e-5f && FreeCameraMotion.Speed(2, 0) == 2, "Free camera: a wheel notch steps the speed");
        check(FreeCameraMotion.Speed(40, 10) == FreeCameraMotion.MaxSpeed && FreeCameraMotion.Speed(0.2f, -10) == FreeCameraMotion.MinSpeed, "Free camera: the speed keeps to its limits");
        check(!TimelineToolbar.FreeCameraTip(1.5f).Contains("...") && !TimelineToolbar.FreeCameraTip(1.5f).Contains('…'), "Free camera: no ellipsis in its tooltip");
    }

    /// <summary> The Bones window's breakdown row shows only for the layers whose keys it reads. </summary>
    private static void Breakdown(Action<bool, string> check)
    {
        check(BoneEditor.ShowsBreakdown(MotionLayerKind.Offset) && BoneEditor.ShowsBreakdown(MotionLayerKind.Pose) && !BoneEditor.ShowsBreakdown(MotionLayerKind.Pin) && !BoneEditor.ShowsBreakdown(MotionLayerKind.LookAt), "Breakdowns only for offset and pose layers");
    }

    /// <summary> The Bones window's layer strip: its arrows step between keys, and a click near a key picks it. </summary>
    private static void Strip(Action<bool, string> check)
    {
        var layer = Offset() with
        {
            Keys = [.. new[] { 10, 20, 40 }.Select(frame =>
                new MotionKey(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kosi", new(Vector3.Zero, Quaternion.Identity, Vector3.One))))],
        };
        check(LayerStrip.Previous(layer, 20) == 10 && LayerStrip.Next(layer, 20) == 40, "Strip: steps around a key");
        check(LayerStrip.Previous(layer, 10) == null && LayerStrip.Next(layer, 40) == null, "Strip: nothing past the ends");
        check(LayerStrip.KeyNear(layer, 22, 3) == 20, "Strip: a click near a key picks it");
        check(LayerStrip.KeyNear(layer, 30, 3) == null, "Strip: a click between keys picks none");
        check(LayerStrip.KeyNear(layer, 15, 5) == 10, "Strip: a tie picks the earlier key");
    }

    /// <summary> The layer settings' line on a layer's bones, by kind; one bone or pin reads singular. </summary>
    private static void Summary(Action<bool, string> check)
    {
        check(LayerPanel.Summary(Kind(MotionLayerKind.Amplitude, bones: ["a", "b"])) == "Limited to 2 bones", "Summary: amplitude");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Amplitude)) == "Every bone", "Summary: amplitude without a list");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Offset, keyed: ["a", "b", "c", "a"])) == "3 bones keyed", "Summary: offset counts keyed bones once");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Symmetry, bones: ["a", "b"])) == "Copies 2 bones", "Summary: symmetry");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Pin, pins: 3)) == "3 pins", "Summary: pins");
        check(LayerPanel.Summary(Kind(MotionLayerKind.LookAt, bones: ["a", "b", "c"])) == "Turns 3 bones", "Summary: look at");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Noise, bones: ["a"])) == "Wobbles 1 bone", "Summary: noise, one bone");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Swing)) == "Swings no bones", "Summary: swing without a list");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Smooth)) == "Smooths every bone", "Summary: smooth without a list");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Repeat)) == "Repeats every bone" && LayerPanel.Summary(Kind(MotionLayerKind.Repeat, bones: ["a"])) == "Repeats 1 bone",
            "Summary: repeat, with and without a list");
        check(LayerPanel.Summary(Kind(MotionLayerKind.Spring)) == "Springs no bones" && LayerPanel.Summary(Kind(MotionLayerKind.Spring, bones: ["a"])) == "Springs 1 bone",
            "Summary: spring, with and without a list");
    }

    /// <summary>
    /// A layer of <paramref name="kind"/> shaped like <see cref="Offset"/>: <paramref name="bones"/> as its bones, each
    /// <paramref name="keyed"/> name a one-bone key at frames 0, 1, 2 and so on, and <paramref name="pins"/> pins.
    /// </summary>
    private static MotionLayer Kind(MotionLayerKind kind, string[]? bones = null, string[]? keyed = null, int pins = 0) =>
        new("m1", kind, "Layer 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            Bones: bones is null ? default : [.. bones],
            Keys: keyed is null ? default : [.. keyed.Select((bone, frame) =>
                new MotionKey(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone, new(Vector3.Zero, Quaternion.Identity, Vector3.One))))],
            Pins: [.. Enumerable.Range(0, pins).Select(i => new MotionPin($"p{i}"))]);

    /// <summary> The clip view's Bones kept line: all or some kept, then the highest index when one is known. </summary>
    private static void Kept(Action<bool, string> check)
    {
        check(ClipView.KeptText(40, 40, 103) == "All 40 kept · highest index 103", "Bones kept: all");
        check(ClipView.KeptText(37, 40, 103) == "37 of 40 kept · highest index 103", "Bones kept: some");
        check(ClipView.KeptText(37, 40, -1) == "37 of 40 kept", "Bones kept: no index known");
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

    /// <summary> The side panel's view: a cut first, then a bone layer, then one event or one group, then several, else the clip. </summary>
    private static void SideViews(Action<bool, string> check)
    {
        var doc = Doc([Ev("e1", TimelineLane.Sound, 10, 20, true), Ev("e2", TimelineLane.Sound, 40, 10, true, group: "g"),
            Ev("e3", TimelineLane.Sound, 60, 10, true, group: "g")], Offset());
        var editor = new TimelineEditor(doc);
        check(SidePanel.ViewFor(editor) == SideView.Clip, "Side panel: nothing selected shows the clip");
        editor.Select("e1");
        check(SidePanel.ViewFor(editor) == SideView.Event, "Side panel: one event");
        editor.Select("e2", ctrl: true);
        check(SidePanel.ViewFor(editor) == SideView.Several, "Side panel: events from different groups");
        // Selecting one event of a group selects the whole group: e2 and e3.
        editor.Select("e2");
        check(editor.Selected.Count == 2 && SidePanel.ViewFor(editor) == SideView.Event, "Side panel: one group counts as one event");
        editor.SelectLayer("m1");
        check(SidePanel.ViewFor(editor) == SideView.Layer, "Side panel: a bone layer");
        editor.SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
        check(SidePanel.ViewFor(editor) == SideView.Cut, "Side panel: the seam");
    }

    /// <summary> The header's Start button names the unfinished edits. </summary>
    private static void Start(Action<bool, string> check)
    {
        check(TimelineWindow.StartLabel(0) == "Start" && TimelineWindow.StartLabel(2) == "Start (2)", "Start shows the unfinished edits");
    }

    /// <summary> The main window's Animations button's tooltip names the unfinished edits. </summary>
    private static void Unfinished(Action<bool, string> check)
    {
        check(TimelineWindowViews.UnfinishedText(1) == "1 edit didn't finish. Undo it in the Timeline.", "One unfinished edit");
        check(TimelineWindowViews.UnfinishedText(3) == "3 edits didn't finish. Undo them in the Timeline.", "Several unfinished edits");
    }

    /// <summary> The start page's sidebar: Partners only once a spawner is attached, Record only with the recorder. </summary>
    private static void StartEntries(Action<bool, string> check)
    {
        check(StartPage.Entries(partners: false, record: true).SequenceEqual([StartEntry.Playing, StartEntry.Game, StartEntry.Saved, StartEntry.Record]), "Start page without partners");
        check(StartPage.Entries(partners: true, record: true).SequenceEqual([StartEntry.Playing, StartEntry.Game, StartEntry.Saved, StartEntry.Partners, StartEntry.Record]), "Start page with partners");
        check(StartPage.Entries(partners: false, record: false).SequenceEqual([StartEntry.Playing, StartEntry.Game, StartEntry.Saved]), "Start page without a recorder");
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
        Steady(check);
    }

    /// <summary>
    /// The status line's work notes: one shows only after 0.3 s, keeps showing 0.5 s after it went (without its button),
    /// and a short gap keeps it on; other notes show at once.
    /// </summary>
    private static void Steady(Action<bool, string> check)
    {
        var work = new StatusNote("live-work", StatusKind.Working, "Applying the bone layers", "Cancel", () => { });
        var notice = new StatusNote("notice", StatusKind.Notice, "Copied 3 bones.");
        bool Shows(Dictionary<string, WorkTiming> timings, double now, params StatusNote[] notes) =>
            StatusLine.Debounced(notes, timings, now).Any(n => n.Key == work.Key);

        var brief = new Dictionary<string, WorkTiming>();
        var briefShown = Shows(brief, 0, work) | Shows(brief, .1, work) | Shows(brief, .2, work) | Shows(brief, .25) | Shows(brief, .6);
        check(!briefShown, "Steady status: work there less than 0.3 s never shows");

        var timings = new Dictionary<string, WorkTiming>();
        var early = Shows(timings, 0, work) || Shows(timings, .29, work);
        check(!early && Shows(timings, .3, work), "Steady status: work there for 0.3 s shows");
        var kept = StatusLine.Debounced([], timings, 1.0);
        var keptNote = kept.FirstOrDefault(n => n.Key == work.Key);
        check(keptNote is { Button: null, Act: null, Text: "Applying the bone layers" }, "Steady status: work just gone keeps its text, without its button");
        check(Shows(timings, 1.49) && !Shows(timings, 1.5), "Steady status: work gone shows for 0.5 s, then not");

        var gap = new Dictionary<string, WorkTiming>();
        Shows(gap, 0, work);
        Shows(gap, .3, work);
        var during = Shows(gap, .5) && Shows(gap, .9, work);
        check(during && Shows(gap, 1.2) && Shows(gap, 1.6), "Steady status: a gap shorter than 0.5 s keeps work shown");

        check(StatusLine.Debounced([notice], new Dictionary<string, WorkTiming>(), 0).SequenceEqual([notice]), "Steady status: other notes show at once");

        var result = new Dictionary<string, WorkTiming>();
        Shows(result, 0, work);
        Shows(result, .3, work);
        var done = StatusLine.Debounced([work with { Kind = StatusKind.Notice, Text = "Done." }], result, .4);
        check(done.Length == 1 && done[0].Kind == StatusKind.Notice, "Steady status: a result with the work's key takes its place");
    }
}
