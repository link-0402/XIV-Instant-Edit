using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>The pure timeline editor model: view, layout, hit testing, snapping, edits, history and gestures.</summary>
internal static class TimelineEditorFixture
{
    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    private static TimelineEvent Ev(string key, TimelineLane lane, int start, int duration = 0, bool resizable = false,
        TimelineAccess access = TimelineAccess.Editable, string? group = null, string magic = "C063", string path = "") =>
        new(key, magic, lane, start, duration, resizable, path, access, access == TimelineAccess.Editable ? null : "Kept as is", false, NoValues, group);

    private static TimelineDocument Doc(int length, params TimelineEvent[] events) => new(length, length, null, [.. events], 1);

    /// <summary>
    /// The editor fixture document, 100 frames: the locked motion, a resizable expression, a sound
    /// marker, a movable footstep in Other, a resizable effect and a locked effect.
    /// Rows: Motion 0, Expression 1, LipSync 2, Sound 3, Effect 4, Prop 5, Other 6.
    /// </summary>
    private static TimelineDocument Sample() => Doc(100,
        Ev("t1", TimelineLane.Motion, 0, 100, true, TimelineAccess.Locked, magic: "C009", path: "cbem_sp01"),
        Ev("t2", TimelineLane.Expression, 10, 30, true, magic: "C010", path: "cfxf_smile"),
        Ev("t3", TimelineLane.Sound, 50, path: "sound/a.scd"),
        Ev("t4", TimelineLane.Other, 70, access: TimelineAccess.Movable, magic: "C042"),
        Ev("t5", TimelineLane.Effect, 80, 30, true, magic: "C012", path: "vfx/a.avfx"),
        Ev("t6", TimelineLane.Effect, 20, 10, true, TimelineAccess.Locked, magic: "C012"));

    // Geometry used with an editor fitted to 440 track pixels: 4 pixels per frame, frame 0 at x 100.
    private static readonly TimelineGeometry Geometry = new(Legend: 100, Ruler: 24, Row: 20, Edge: 5, Marker: 9);

    private static Vector2 At(float frame, int row, float dx = 0) => new(100 + frame * 4 + dx, 24 + row * 20 + 10);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        View(check);
        Snapping(check);
        Layout(check);
        HitTesting(check);
        Edits(check);
        History(check);
        Gestures(check);
        Keys(check);
        Panel(check);
    }

    private static void Panel(Action<bool, string> check)
    {
        var editor = Editor();
        editor.BeginFieldEdit();
        check(editor.Replace(Event(editor, "t2") with { Start = 11 }) && editor.Replace(Event(editor, "t2") with { Start = 12, Duration = 25 }) &&
              editor.History.InGesture && editor.History.UndoCount == 0 && Event(editor, "t2") is { Start: 12, Duration: 25 },
            "typing into a properties field changes the event live");
        editor.EndFieldEdit();
        check(editor.History.UndoCount == 1 && !editor.History.InGesture && editor.Undo() && Event(editor, "t2") is { Start: 10, Duration: 30 },
            "a whole field edit is one undo step");
        check(editor.Replace(Event(editor, "t3") with { Start = 20 }) && editor.History.UndoCount == 1 && Event(editor, "t3").Start == 20 &&
              !editor.Replace(Event(editor, "t1") with { Start = 5 }) && !editor.Replace(Event(editor, "t3")) && editor.History.UndoCount == 1,
            "a change outside a field edit is one step; locked events and unchanged values change nothing");

        editor.Select("t3");
        editor.Select("t5", ctrl: true);
        check(editor.ContextMenu(Hit(editor, At(85, 4))) == 85 && editor.Selected.Count == 2 && editor.Primary == "t5" &&
              editor.ContextMenu(Hit(editor, At(25, 1))) == 25 && editor.Selected.SetEquals(["t2"]) &&
              editor.ContextMenu(Hit(editor, At(45, 2))) == 45 && editor.Selected.SetEquals(["t2"]),
            "a right click acts on the selection it lands in, else selects the event under it; empty space keeps it");
        editor.Snap = true;
        check(editor.SnappedFrame(79.4f) == 80 && editor.SnappedFrame(79.4f, alt: true) == 79 && editor.SnappedFrame(-3) == 0 &&
              editor.SnappedFrame(60.4f) == 60,
            "frames for adding at the pointer are whole, snapped and never below 0");

        editor.Playhead = 100;
        check(editor.Add([Ev("", TimelineLane.Prop, 100, group: "p"), Ev("", TimelineLane.Prop, 104, group: "p")], null) &&
              editor.Selected.Select(key => Event(editor, key).Start).Order().SequenceEqual([95, 99]) &&
              editor.Add([Ev("", TimelineLane.Sound, 7)], null) && Event(editor, editor.Primary!).Start == 7,
            "an add at the end moves back as a whole so every part plays; one inside stays where it is");
    }

    private static void View(Action<bool, string> check)
    {
        var view = new TimelineView(10, 4, 400);
        check(Math.Abs(view.FrameAt(view.X(37)) - 37) < 1e-4 && Math.Abs(view.X(view.FrameAt(123)) - 123) < 1e-3 && view.Last == 110,
            "timeline view maps frames to pixels and back");
        var zoomed = view.ZoomAt(200, 2, .02f, 40);
        check(zoomed.PixelsPerFrame == 8 && Math.Abs(zoomed.FrameAt(200) - view.FrameAt(200)) < 1e-4,
            "zooming keeps the frame under the cursor in place");
        var capped = view.ZoomAt(123, 1000, .02f, 40);
        check(capped.PixelsPerFrame == 40 && Math.Abs(capped.FrameAt(123) - view.FrameAt(123)) < 1e-3 &&
              view.ZoomAt(50, 1e-6f, .02f, 40).PixelsPerFrame == .02f && view.ZoomAt(50, float.NaN, .02f, 40) == view,
            "zoom stays inside its limits and ignores a broken factor, still around the cursor");
        check(view.Pan(40).First == 0 && view.Pan(-40).First == 20, "panning moves the content with the mouse");
        check(new TimelineView(-30, 4, 400).Clamp(100).First == 0 && new TimelineView(500, 8, 400).Clamp(100).First == 75 &&
              new TimelineView(500, 1, 400).Clamp(100).First == 0,
            "the view scrolls from frame 0 to a quarter past the length and no further");
        var fit = TimelineView.Fit(100, 440);
        check(fit.First == 0 && Math.Abs(fit.PixelsPerFrame - 4) < 1e-5 && fit.Width == 440 && fit.X(100) < fit.Width,
            "fitting shows the whole length with room for the length handle");
        check(new TimelineView(0, 0, 100).FrameAt(50) is var frame && float.IsFinite(frame), "a zero zoom never divides by zero");

        check(view.MajorStep(70) == 30 && view.MinorStep(30, 8) == 2 && view.MinorStep(30, 15) == 5 &&
              new TimelineView(0, .01f, 400).MajorStep(70) == 3600 && new TimelineView(0, 40, 400).MajorStep(70) == 2 &&
              new TimelineView(0, 40, 400).MinorStep(2, 8) == 1 && new TimelineView(0, 1, 400).MinorStep(1, 8) == 1,
            "ruler ticks pick the smallest step that keeps labels and minor ticks apart");
        check(new TimelineView(47, 4, 400).FirstTick(30) == 30 && new TimelineView(-5, 4, 400).FirstTick(10) == 0,
            "the first ruler tick starts at or before the left edge and never below 0");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string[] labels = [TimelineText.Tick(0, 30), TimelineText.Tick(45, 15), TimelineText.Tick(12, 2), TimelineText.Tick(30, 10),
                TimelineText.Tick(300, 150), TimelineText.Seconds(36), TimelineText.Seconds(1)];
            check(labels.SequenceEqual(["0s", "1.5s", "12f", "1s", "10s", "1.2s", "0.03s"]) &&
                  labels.All(label => !label.Contains('…') && !label.Contains("...")),
                "tick labels read 0s, 1.5s or 12f in any culture and never use an ellipsis");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Snapping(Action<bool, string> check)
    {
        int[] targets = [0, 50, 100];
        check(TimelineSnap.Snap(10.4f, [], 8, 4, true, out var none) == 10 && none == null &&
              TimelineSnap.Snap(10.5f, [], 8, 4, true, out _) == 11 && TimelineSnap.Snap(-0.5f, [], 8, 4, false, out _) == -1,
            "snapping always lands on whole frames, rounding halves away from zero");
        check(TimelineSnap.Snap(48.5f, targets, 8, 4, true, out var guide) == 50 && guide == 50,
            "the magnet pulls to a target within its pixel tolerance and reports the guide");
        check(TimelineSnap.Snap(48.5f, targets, 8, 4, false, out var off) == 49 && off == null &&
              TimelineSnap.Snap(45f, targets, 8, 4, true, out var far) == 45 && far == null &&
              TimelineSnap.Snap(48.5f, targets, 8, 1, true, out var zoomedOut) == 50 && zoomedOut == 50 &&
              TimelineSnap.Snap(45f, targets, 8, 1, true, out _) == 50,
            "the magnet can be turned off and its reach is in pixels, so it grows with zooming out");
        check(TimelineSnap.SnapDelta(11.5f, [10, 40], [0, 52, 100], 8, 4, true, out var edge) == 12 && edge == 52 &&
              TimelineSnap.SnapDelta(-9.2f, [10, 40], [0, 52, 100], 8, 4, true, out var start) == -10 && start == 0,
            "a drag snaps whichever dragged start or end comes closest to a target");

        var doc = Sample();
        var all = TimelineSnap.Targets(doc, 33);
        var moving = TimelineSnap.Targets(doc, null, new HashSet<string> { "t2", "t5" });
        check(all.SequenceEqual([0, 10, 20, 30, 33, 40, 50, 70, 80, 100, 110]) && moving.SequenceEqual([0, 20, 30, 50, 70, 100]),
            "snap targets are 0, the length, the playhead and the other events' starts and ends");
        check(TimelineSnap.Targets(doc with { SourceLength = 90 }, null, null, length: false, sourceLength: true, events: false).SequenceEqual([0, 90]),
            "a length drag can snap back to the original length");
    }

    private static void Layout(Action<bool, string> check)
    {
        var doc = Doc(100,
            Ev("m", TimelineLane.Motion, 0, 100, true, TimelineAccess.Locked),
            Ev("e1", TimelineLane.Expression, 10, 30, true),
            Ev("e2", TimelineLane.Expression, 20, 10, true),
            Ev("e3", TimelineLane.Expression, 40, 10, true),
            Ev("s1", TimelineLane.Sound, 5),
            Ev("s2", TimelineLane.Sound, 5),
            Ev("s3", TimelineLane.Sound, 6));
        var layout = TimelineLayout.Build(doc);
        check(layout.Lanes.Select(lane => lane.Lane).SequenceEqual([TimelineLane.Motion, TimelineLane.Expression, TimelineLane.LipSync,
                  TimelineLane.Sound, TimelineLane.Effect, TimelineLane.Prop]) &&
              layout.Lanes.Select(lane => lane.Rows).SequenceEqual([1, 2, 1, 2, 1, 1]) && layout.RowCount == 8,
            "the motion and every addable lane always show; Other only with events; overlaps add rows");
        check(layout.RowOf(layout.IndexOf("e1")) == 1 && layout.RowOf(layout.IndexOf("e2")) == 2 && layout.RowOf(layout.IndexOf("e3")) == 1 &&
              layout.RowOf(layout.IndexOf("s1")) == 4 && layout.RowOf(layout.IndexOf("s2")) == 5 && layout.RowOf(layout.IndexOf("s3")) == 4,
            "events pack greedily into the first free row, a clip ending where another starts sharing it");
        var withOther = TimelineLayout.Build(doc with { Events = doc.Events.Add(Ev("o", TimelineLane.Other, 3)) });
        check(withOther.Lanes[^1] is { Lane: TimelineLane.Other, FirstRow: 8, Rows: 1 } && withOther.LaneAt(8)?.Lane == TimelineLane.Other &&
              withOther.Lane(TimelineLane.Other) != null && layout.Lane(TimelineLane.Other) == null,
            "Other gets a lane at the bottom once it holds an event");
        var visible = TimelineLayout.Build(doc, ev => ev.Lane == TimelineLane.Sound ? 10 : 0);
        check(visible.RowOf(visible.IndexOf("s3")) == 6 && visible.EndOf(visible.IndexOf("s1")) == 15 && visible.Lane(TimelineLane.Sound)?.Rows == 3 &&
              layout.EndOf(layout.IndexOf("s1")) == 6,
            "an event's visible length takes room in its row; a bare marker takes one frame");
        check(layout.RowAt(23, 20, 24) == -1 && layout.RowAt(24, 20, 24) == 0 && layout.RowAt(24 + 20 * 3 + 1, 20, 24) == 3 &&
              layout.RowAt(24 + 20 * 8, 20, 24) == -1 && TimelineLayout.RowTop(2, 20, 24) == 64 && layout.Height(20, 24) == 184 &&
              layout.RowAt(30, 20, 24, scrollY: 40) == 2 && layout.LaneSpan(TimelineLane.Sound, 20, 24) == (104, 144),
            "rows map to y and back below the ruler, with vertical scrolling");
        check(layout.IndexOf("missing") == -1 && TimelineLayout.Build(new TimelineDocument(10, 10, null, default, 0)).RowCount == 6,
            "an empty document still lays out its fixed lanes");
    }

    private static void HitTesting(Action<bool, string> check)
    {
        var doc = Sample();
        var layout = TimelineLayout.Build(doc);
        var view = TimelineView.Fit(100, 440);
        TimelineHit Hit(Vector2 point) => Geometry.HitTest(doc, layout, view, point);

        check(Hit(At(10, 1, 1)) is { Part: TimelinePart.LeftEdge, Key: "t2" } && Hit(At(10, 1, -4)) is { Part: TimelinePart.LeftEdge, Key: "t2" } &&
              Hit(At(40, 1, -1)) is { Part: TimelinePart.RightEdge, Key: "t2" } && Hit(At(40, 1, 4)) is { Part: TimelinePart.RightEdge, Key: "t2" } &&
              Hit(At(25, 1)) is { Part: TimelinePart.Body, Key: "t2", Lane: TimelineLane.Expression, Row: 1 } &&
              Hit(At(45, 1)) is { Part: TimelinePart.Lane, Key: null, Lane: TimelineLane.Expression },
            "a resizable clip has grab zones at both edges, a body between and lane space around it");
        check(Hit(At(25, 4)) is { Part: TimelinePart.Body, Key: "t6" } && Hit(At(20, 4, 1)) is { Part: TimelinePart.Body, Key: "t6" } &&
              Hit(At(70, 6, 4)) is { Part: TimelinePart.Body, Key: "t4" } && Hit(At(70, 6, -4)) is { Part: TimelinePart.Body, Key: "t4" } &&
              Hit(At(70, 6, 6)).Part == TimelinePart.Lane,
            "locked clips and movable markers have no edges; a marker is grabbable a few pixels either side");
        check(Hit(new(100 + 400, 10)).Part == TimelinePart.LengthHandle && Hit(At(100, 0, 3)).Part == TimelinePart.LengthHandle &&
              Hit(At(100, 4, 3)) is { Part: TimelinePart.RightEdge or TimelinePart.Body, Key: "t5" },
            "the length handle sits on the ruler and the motion lane, not on other lanes");
        check(Hit(new(150, 10)) is { Part: TimelinePart.Ruler, Frame: 12.5f, Lane: null } && Hit(At(20, 1) with { X = 50 }) is
              { Part: TimelinePart.Legend, Lane: TimelineLane.Expression } && Hit(new(50, 10)).Part == TimelinePart.None &&
              Hit(At(10, 7)).Part == TimelinePart.None && Hit(At(200, 1)).Part == TimelinePart.None,
            "the ruler, the legend and the space outside the rows are told apart");

        var crowded = Doc(100, Ev("a", TimelineLane.Sound, 50), Ev("b", TimelineLane.Sound, 51));
        var crowdedLayout = TimelineLayout.Build(crowded);
        TimelineHit Crowded(Vector2 point) => Geometry.HitTest(crowded, crowdedLayout, view, point);
        check(Crowded(At(51, 3, -2)) is { Key: "a" } && Crowded(At(51, 3, 1)) is { Key: "b" } && Crowded(At(50, 3, -3)) is { Key: "a" } &&
              Crowded(At(52, 3, .25f)) is { Key: "b" } && Crowded(At(52, 3, 3)).Part == TimelinePart.Lane && Crowded(At(51, 3)) is { Key: "b" },
            "where marker grab zones overlap, the event whose frames are under the pointer wins, the one drawn last on a tie");
        var packed = Doc(100, Ev("a", TimelineLane.Sound, 0), Ev("b", TimelineLane.Sound, 1));
        var packedLayout = TimelineLayout.Build(packed);
        var wide = new TimelineView(0, 1, 440);
        check(Geometry.HitTest(packed, packedLayout, wide, new(100.2f, 94)) is { Key: "a" } && Geometry.HitTest(packed, packedLayout, wide, new(103, 94)) is { Key: "b" },
            "zoomed out, a marker at frame 0 stays grabbable beside the next one");
        var tiny = new TimelineView(0, .5f, 440);
        check(Geometry.HitTest(doc, layout, tiny, new(100 + 25 * .5f, 34 + 20)) is { Part: TimelinePart.Body, Key: "t2" } &&
              Geometry.HitTest(doc, layout, tiny, new(100 + 10 * .5f + 1, 34 + 20)).Part == TimelinePart.LeftEdge,
            "a narrow clip keeps a body to grab between its edge zones");
        var (left, right) = Geometry.EventSpan(doc.Events[2], 51, view);
        check(left == 200 - 4.5f && right == 204.5f && Geometry.EventSpan(doc.Events[1], 40, tiny) is (5, 20),
            "markers are centred on their frame and at least the marker width");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = Doc(100,
            Ev("a", TimelineLane.Sound, 10),
            Ev("b", TimelineLane.Other, 90, access: TimelineAccess.Movable),
            Ev("c", TimelineLane.Effect, 50, 10, true, TimelineAccess.Locked));
        var moved = TimelineEdits.Move(doc, ["a", "b"], 20);
        check(moved.Events[0].Start == 19 && moved.Events[1].Start == 99, "a group move stops when any start would pass the end");
        var back = TimelineEdits.Move(doc, ["a", "b"], -50);
        check(back.Events[0].Start == 0 && back.Events[1].Start == 80, "a group move stops at frame 0");
        check(ReferenceEquals(TimelineEdits.Move(doc, ["c"], 5), doc) && ReferenceEquals(TimelineEdits.Move(doc, ["a"], 0), doc) &&
              ReferenceEquals(TimelineEdits.Move(doc, ["missing"], 5), doc) && TimelineEdits.Move(doc, ["a", "c"], 5).Events[2].Start == 50,
            "locked events never move and a no-op returns the same document");
        var empty = Doc(100);
        check(TimelineEdits.SetLength(empty, 50, true) is { Length: 50, Events.Length: 0 } && ReferenceEquals(TimelineEdits.Move(empty, ["a"], 5), empty) &&
              TimelineEdits.SetLength(new TimelineDocument(100, 100, null, default, 0), 50, false).Length == 50,
            "an empty timeline can still change its length");
        var late = Doc(100, Ev("d", TimelineLane.Sound, 120));
        check(ReferenceEquals(TimelineEdits.Move(late, ["d"], 5), late) && TimelineEdits.Move(late, ["d"], -5).Events[0].Start == 115,
            "an event past the end can move back but not further out");

        var forever = Doc(100, Ev("f", TimelineLane.Expression, 10, 9999999, true), Ev("g", TimelineLane.Effect, 5, int.MaxValue, true));
        var foreverLayout = TimelineLayout.Build(forever);
        var foreverView = TimelineView.Fit(100, 440);
        check(foreverLayout.EndOf(0) == 10000009 && foreverLayout.EndOf(1) == int.MaxValue && TimelineEdits.End(forever.Events[1]) == int.MaxValue &&
              TimelineSnap.Targets(forever, null)[^1] == int.MaxValue &&
              Geometry.HitTest(forever, foreverLayout, foreverView, At(50, 1)) is { Part: TimelinePart.Body, Key: "f" } &&
              Geometry.HitTest(forever, foreverLayout, foreverView, At(50, 4)) is { Part: TimelinePart.Body, Key: "g" },
            "int32 durations meaning until the end lay out and hit test without overflowing");
        check(TimelineEdits.SetLength(forever, 200, true).Events[0] is { Start: 20, Duration: 9999999 } &&
              TimelineEdits.Replace(forever, forever.Events[0] with { Path = "cfxf_a" }).Events[0] is { Path: "cfxf_a", Duration: 9999999 } &&
              TimelineEdits.Duplicate(forever, ["f"], 5, out _).Events[^1] is { Start: 15, Duration: 9999999 } &&
              TimelineEdits.Resize(forever, "f", true, -5).Events[0] is { Start: 5, Duration: 9999999 } &&
              TimelineEdits.Resize(forever, "g", true, -5).Events[1] is { Start: 0, Duration: int.MaxValue } &&
              TimelineEdits.Resize(forever, "f", false, -9999990).Events[0].Duration == 9,
            "durations meaning until the end survive scaling, field edits, duplicates and left-edge drags");
        var clips = Doc(100,
            Ev("v", TimelineLane.Effect, 10, 30, true),
            Ev("w", TimelineLane.Effect, 50, 30),
            Ev("x", TimelineLane.Effect, 70, 10, true, TimelineAccess.Movable));
        check(TimelineEdits.Resize(clips, "v", true, -15).Events[0] is { Start: 0, Duration: 40 } &&
              TimelineEdits.Resize(clips, "v", true, 35).Events[0] is { Start: 39, Duration: 1 } &&
              TimelineEdits.Resize(clips, "v", false, -40).Events[0] is { Start: 10, Duration: 1 } &&
              TimelineEdits.Resize(clips, "v", false, 15).Events[0] is { Start: 10, Duration: 45 },
            "resizing keeps the other edge, a duration of at least one frame and a start of at least 0");
        check(ReferenceEquals(TimelineEdits.Resize(clips, "w", false, 5), clips) && ReferenceEquals(TimelineEdits.Resize(clips, "x", false, 5), clips) &&
              ReferenceEquals(TimelineEdits.Resize(clips, "v", true, 0), clips) && ReferenceEquals(TimelineEdits.Resize(clips, "missing", true, 5), clips),
            "only editable, resizable events resize");

        var timeline = Doc(100,
            Ev("motion", TimelineLane.Motion, 0, 100, true, TimelineAccess.Locked),
            Ev("other-motion", TimelineLane.Motion, 0, 60, true, TimelineAccess.Locked),
            Ev("sound", TimelineLane.Sound, 50),
            Ev("vfx", TimelineLane.Effect, 80, 30, true),
            Ev("face", TimelineLane.Expression, 99, 1, true),
            Ev("past", TimelineLane.Other, 120, access: TimelineAccess.Locked));
        var longer = TimelineEdits.SetLength(timeline, 200, false);
        check(longer.Length == 200 && longer.SourceLength == 100 && longer.LengthChanged && longer.Events[0].Duration == 200 &&
              longer.Events[1].Duration == 60 && longer.Events.Skip(2).SequenceEqual(timeline.Events.Skip(2)),
            "a longer length stretches only the motion that filled the old length");
        var shorter = TimelineEdits.SetLength(timeline, 50, true);
        check(shorter.Length == 50 && shorter.Events[0] is { Start: 0, Duration: 50 } && shorter.Events[1].Duration == 30 &&
              shorter.Events[2].Start == 25 && shorter.Events[3] is { Start: 40, Duration: 15 } && shorter.Events[4] is { Start: 49, Duration: 1 } &&
              shorter.Events[5].Start == 60,
            "scaling with the length moves and stretches every event, keeping playing starts inside");
        var odd = TimelineEdits.SetLength(Doc(100, Ev("a", TimelineLane.Sound, 1), Ev("b", TimelineLane.Sound, 3, 1, true)), 150, true);
        check(odd.Events[0].Start == 2 && odd.Events[1] is { Start: 5, Duration: 2 }, "scaled frames round half away from zero");
        check(TimelineEdits.SetLength(timeline, 1, false).Length == TimelineDocument.MinLength &&
              TimelineEdits.SetLength(timeline, 100000, false).Length == TimelineDocument.MaxLength &&
              ReferenceEquals(TimelineEdits.SetLength(timeline, 100, true), timeline),
            "the length stays inside the editor's limits and an unchanged length is a no-op");
        check(TimelineEdits.PastEnd(timeline).Select(ev => ev.Key).SequenceEqual(["past"]) &&
              TimelineEdits.PastEnd(TimelineEdits.SetLength(timeline, 60, false)).Select(ev => ev.Key).SequenceEqual(["vfx", "face", "past"]),
            "events that start at or past the end are reported");
        check(TimelineEdits.Extent(timeline) == 121 && TimelineEdits.Extent(Doc(100, Ev("a", TimelineLane.Sound, 50))) == 100 &&
              TimelineEdits.Extent(Doc(100)) == 100,
            "the view reaches the last event that starts past the end");
        var counted = Doc(455,
            Ev("long", TimelineLane.Motion, 0, 456, true, TimelineAccess.Locked, magic: "C009"),
            Ev("short", TimelineLane.Motion, 0, 453, true, TimelineAccess.Locked, magic: "C010"));
        var doubled = TimelineEdits.SetLength(counted, 910, false, counted);
        check(doubled.Events[0].Duration == 910 && doubled.Events[1].Duration == 453 &&
              TimelineEdits.SetLength(counted, 910, false).Events[0].Duration == 910 &&
              TimelineEdits.SetLength(doubled, 455, false, counted).Events[0].Duration == 456,
            "a motion one frame longer than the clip still follows its length, and comes back as it was");

        var props = new TimelineDocument(100, 100, null, [Ev("n5", TimelineLane.Sound, 1)], 5);
        var added = TimelineEdits.Add(props, [Ev("", TimelineLane.Prop, 10, group: "prop"), Ev("", TimelineLane.Prop, 10, magic: "C031", group: "prop"),
            Ev("", TimelineLane.Sound, 12)], null, out var created, out var error);
        var group = added.Events[1].Group;
        check(error == null && created.SequenceEqual(["n6", "n7", "n8"]) && added.NextKey == 9 &&
              group != null && added.Events[2].Group == group && group != "prop" && added.Events[3].Group == null,
            "added events get fresh keys, skipping taken ones, and keep their shared group under a new id");
        var again = TimelineEdits.Add(added, [Ev("", TimelineLane.Prop, 30, group: "prop"), Ev("", TimelineLane.Prop, 30, group: "prop")], null, out _);
        check(again.Events[4].Group is { } second && again.Events[5].Group == second && second != group && again.NextKey == 11,
            "each Add makes its own group");
        var face = TimelineEdits.Add(props, [Ev("", TimelineLane.Expression, 5, 30, true, magic: "C010")], "emot/laugh", out var faceError);
        var same = TimelineEdits.Add(face, [Ev("", TimelineLane.Expression, 40, 30, true, magic: "C010")], "EMOT/LAUGH", out var sameError);
        var other = TimelineEdits.Add(same, [Ev("", TimelineLane.Expression, 60, 30, true, magic: "C010")], "bad", out var otherError);
        check(faceError == null && face.FacePack == "emot/laugh" && sameError == null && same.Events.Length == 3 && same.FacePack == "emot/laugh" &&
              ReferenceEquals(other, same) && otherError is { Length: > 0 } && !otherError.Contains('…') && !otherError.Contains("..."),
            "an expression library is set by the first Add that needs one and a different one is refused");
        check(TimelineEdits.Add(face, [Ev("", TimelineLane.Sound, 1)], null, out _).FacePack == "emot/laugh" &&
              ReferenceEquals(TimelineEdits.Add(props, [], "bad", out var emptyError), props) && emptyError == null &&
              ReferenceEquals(TimelineEdits.Add(props, [Ev("", TimelineLane.Sound, -1)], null, out var badError), props) && badError != null,
            "Adds without a library keep it; empty Adds are no-ops; events outside the timeline are refused");
        check(TimelineEdits.Add(props, [Ev("", TimelineLane.Sound, 3) with { Conditional = true }], null, out _).Events[^1] is { Key: "n6", Conditional: false },
            "an added copy of a conditional event plays unconditionally, on its own new track");

        var grouped = Doc(100,
            Ev("p1", TimelineLane.Prop, 10, group: "g1"), Ev("p2", TimelineLane.Prop, 12, group: "g1"),
            Ev("lock", TimelineLane.Effect, 10, 5, true, TimelineAccess.Locked), Ev("move", TimelineLane.Other, 10, access: TimelineAccess.Movable));
        var copies = TimelineEdits.Duplicate(grouped, ["p1", "p2", "lock", "move"], 20, out var copyKeys);
        check(copyKeys.Length == 2 && copies.Events.Length == 6 && copies.Events[4] is { Start: 30, Group: { } copyGroup } &&
              copies.Events[5].Start == 32 && copies.Events[5].Group == copyGroup && copyGroup != "g1" && copies.Events[4].Key == copyKeys[0],
            "duplicating copies editable events only, with new keys and a new group");
        check(TimelineEdits.Duplicate(grouped, ["p1", "p2"], 200, out _).Events[^1].Start == 99 &&
              ReferenceEquals(TimelineEdits.Duplicate(grouped, ["lock"], 5, out var none), grouped) && none.IsEmpty,
            "duplicates stay inside the timeline and locked events are not duplicated");
        var deleted = TimelineEdits.Delete(face with { Events = face.Events.AddRange(grouped.Events) }, ["n5", "lock", "move", "p1"]);
        check(deleted.Events.Select(ev => ev.Key).SequenceEqual(["n6", "p2", "lock"]) && deleted.FacePack == "emot/laugh" &&
              ReferenceEquals(TimelineEdits.Delete(grouped, ["lock"]), grouped),
            "deleting skips locked events and keeps the expression library");

        var values = ImmutableDictionary<string, double>.Empty.Add("SoundIndex", 1);
        var editable = Doc(100, Ev("e", TimelineLane.Sound, 10) with { Values = values },
            Ev("m", TimelineLane.Other, 20, access: TimelineAccess.Movable), Ev("l", TimelineLane.Effect, 30, 5, true, TimelineAccess.Locked));
        var replaced = TimelineEdits.Replace(editable, editable.Events[0] with
        {
            Start = 15, Path = "sound/b.scd", Values = values.SetItem("SoundIndex", 2), Magic = "C012", Lane = TimelineLane.Effect,
            Access = TimelineAccess.Locked,
        });
        check(replaced.Events[0] is { Start: 15, Path: "sound/b.scd", Magic: "C063", Lane: TimelineLane.Sound, Access: TimelineAccess.Editable } &&
              replaced.Events[0].Values["SoundIndex"] == 2,
            "replacing an editable event takes its fields but never its kind, lane or access");
        var movable = TimelineEdits.Replace(editable, editable.Events[1] with { Start = 25, Path = "x", Duration = 4 });
        check(movable.Events[1] is { Start: 25, Path: "", Duration: 0 } &&
              ReferenceEquals(TimelineEdits.Replace(editable, editable.Events[2] with { Start = 1 }), editable),
            "a movable event only takes a new start and a locked one nothing");
        check(ReferenceEquals(TimelineEdits.Replace(editable, editable.Events[0] with { Values = ImmutableDictionary<string, double>.Empty.Add("SoundIndex", 1) }), editable) &&
              TimelineEdits.Replace(editable, editable.Events[0] with { Start = -5 }).Events[0].Start == 0 &&
              ReferenceEquals(TimelineEdits.Replace(editable, Ev("missing", TimelineLane.Sound, 1)), editable),
            "replacing with equal values is a no-op and starts stay at frame 0 or later");
        check(ReferenceEquals(TimelineEdits.Replace(editable, editable.Events[0] with { Duration = 4, Resizable = true, Conditional = true, LockReason = "x" }), editable) &&
              TimelineEdits.Replace(clips, clips.Events[0] with { Duration = 0 }).Events[0].Duration == 1,
            "a field edit never gives a kind a duration, a condition or a lock it does not have");
    }

    private static void History(Action<bool, string> check)
    {
        var start = Doc(100);
        var history = new TimelineHistory(start);
        history.Commit(start);
        check(!history.Dirty && !history.CanUndo, "committing the same document records nothing");
        var a = start with { Length = 90 };
        var b = start with { Length = 80 };
        history.Commit(a);
        check(history.Dirty && history.UndoCount == 1 && ReferenceEquals(history.Current, a), "a commit is one undo step");
        check(history.Undo() && ReferenceEquals(history.Current, start) && !history.Dirty && history.Redo() && ReferenceEquals(history.Current, a),
            "undo and redo walk the snapshots and the saved state is clean again");

        history.Begin();
        history.Preview(start with { Length = 70 });
        history.Preview(start with { Length = 60 });
        history.Preview(b);
        check(history.InGesture && !history.CanUndo && ReferenceEquals(history.GestureStart, a), "a running gesture holds its start");
        history.End();
        check(history.UndoCount == 2 && ReferenceEquals(history.Current, b) && history.Undo() && ReferenceEquals(history.Current, a),
            "a whole gesture is one undo step");
        history.Redo();
        history.Begin();
        history.Preview(start with { Length = 10 });
        history.Cancel();
        history.Begin();
        history.End();
        check(ReferenceEquals(history.Current, b) && history.UndoCount == 2 && !history.InGesture,
            "a cancelled gesture goes back and an empty gesture records nothing");
        history.Undo();
        history.Commit(start with { Length = 5 });
        check(!history.CanRedo && history.UndoCount == 2, "a new edit clears redo");
        history.MarkSaved();
        check(!history.Dirty && ReferenceEquals(history.Saved, history.Current), "saving makes the current document clean");
        history.Commit(start with { Length = 6 });
        history.Undo();
        check(!history.Dirty, "undoing back to the saved document is clean");
        var changes = 0;
        history.Changed += () => changes++;
        history.Reset(start);
        check(!history.CanUndo && !history.CanRedo && !history.Dirty && ReferenceEquals(history.Saved, start) && changes == 1,
            "reset forgets every step and announces the change");

        var capped = new TimelineHistory(start);
        var docs = Enumerable.Range(1, 150).Select(length => start with { Length = length }).ToArray();
        foreach (var doc in docs)
            capped.Commit(doc);
        var undone = 0;
        while (capped.Undo())
            undone++;
        check(capped.UndoCount == 0 && undone == 100 && ReferenceEquals(capped.Current, docs[49]), "history keeps the last 100 steps");
    }

    private static TimelineEditor Editor(TimelineDocument? doc = null)
    {
        var editor = new TimelineEditor(doc ?? Sample()) { Snap = false };
        editor.SetWidth(440);
        return editor;
    }

    private static TimelineHit Hit(TimelineEditor editor, Vector2 point) => editor.HitTest(Geometry, point);

    private static TimelineEvent Event(TimelineEditor editor, string key) => TimelineEdits.Find(editor.Document, key)!;

    private static void Gestures(Action<bool, string> check)
    {
        var editor = Editor();
        check(Math.Abs(editor.View.PixelsPerFrame - 4) < 1e-5 && editor.View.First == 0, "the editor fits the timeline at its first width");
        var layout = editor.Layout;
        check(ReferenceEquals(layout, editor.Layout), "the layout is cached per document");

        // Click without a drag selects and changes nothing.
        var loaded = editor.Document;
        editor.Press(Hit(editor, At(50, 3)));
        editor.Release();
        check(ReferenceEquals(editor.Document, loaded) && !editor.History.CanUndo && editor.Selected.SetEquals(["t3"]) && editor.Primary == "t3",
            "clicking an event selects it without changing the document");

        // Move: whole-frame offsets from the press, no drift, one undo step.
        var press = Hit(editor, At(50, 3, 1));
        editor.Press(press);
        check(editor.Gesture == TimelineGesture.Move, "pressing an editable event's body starts a move");
        editor.Drag(press.Frame + 3.7f);
        editor.Drag(press.Frame - 20.2f);
        editor.Drag(press.Frame + 10.2f);
        check(Event(editor, "t3").Start == 60 && editor.Dragging && editor.History.InGesture, "dragging moves by whole frames from the press");
        editor.Release();
        check(editor.History.UndoCount == 1 && !editor.Layout.Equals(layout) && editor.Undo() && Event(editor, "t3").Start == 50 &&
              editor.Redo() && Event(editor, "t3").Start == 60,
            "a drag is one undo step");

        // Cancel restores the document from before the press.
        var beforeCancel = editor.Document;
        editor.Press(Hit(editor, At(60, 3)));
        editor.Drag(Hit(editor, At(60, 3)).Frame + 15);
        editor.Cancel();
        check(ReferenceEquals(editor.Document, beforeCancel) && editor.History.UndoCount == 1 && editor.Gesture == TimelineGesture.None,
            "cancelling a drag puts everything back");

        // Multi-select move clamps the group.
        editor.Press(Hit(editor, At(25, 1)), ctrl: true);
        editor.Release();
        check(editor.Selected.SetEquals(["t3", "t2"]) && editor.Primary == "t2", "ctrl-clicking adds to the selection");
        var group = Hit(editor, At(25, 1));
        editor.Press(group);
        editor.Drag(group.Frame + 60);
        editor.Release();
        check(Event(editor, "t3").Start == 99 && Event(editor, "t2").Start == 49 && editor.Selected.Count == 2,
            "moving several events clamps them as a group and keeps the selection");
        editor.Press(Hit(editor, At(60, 1)));
        editor.Release();
        check(editor.Selected.SetEquals(["t2"]), "a plain click on a selected event narrows the selection to it");
        editor.Press(Hit(editor, At(60, 1)), ctrl: true);
        editor.Release();
        check(editor.Selected.Count == 0 && editor.Primary == null, "ctrl-clicking a selected event removes it");

        // Locked events select but never move.
        var locked = Hit(editor, At(25, 4));
        editor.Press(locked);
        editor.Drag(locked.Frame + 10);
        editor.Release();
        check(Event(editor, "t6").Start == 20 && editor.Selected.SetEquals(["t6"]) && editor.History.UndoCount == 2,
            "a locked event can be selected but not dragged");

        // Shift selects by start across lanes.
        editor.Press(Hit(editor, At(70, 6)));
        editor.Release();
        editor.Press(Hit(editor, At(85, 4)), shift: true);
        editor.Release();
        check(editor.Selected.SetEquals(["t4", "t5"]) && editor.Primary == "t4",
            "shift-clicking selects every event starting between the primary and the clicked one");
        editor.Press(Hit(editor, At(99, 3)), shift: true);
        editor.Release();
        check(editor.Selected.SetEquals(["t4", "t5", "t3"]) && editor.Primary == "t4",
            "shift-clicking selects every event starting between the primary and the clicked one");

        // Empty lane space clears unless ctrl or shift.
        editor.Press(Hit(editor, At(5, 2)), ctrl: true);
        editor.Release();
        check(editor.Selected.Count == 3, "ctrl-clicking empty space keeps the selection");
        editor.Press(Hit(editor, At(5, 2)));
        editor.Release();
        check(editor.Selected.Count == 0, "clicking empty space clears the selection");

        // Resize from the right edge.
        var edge = Hit(editor, At(110, 4, -1));
        check(edge.Part == TimelinePart.RightEdge, "the effect's right edge is grabbable");
        editor.Press(edge);
        check(editor.Gesture == TimelineGesture.ResizeRight && editor.Selected.SetEquals(["t5"]), "pressing an edge selects and starts a resize");
        editor.Drag(edge.Frame - 12.4f);
        editor.Release();
        check(Event(editor, "t5") is { Start: 80, Duration: 18 } && editor.History.UndoCount == 3, "dragging an edge resizes as one step");
        var leftEdge = Hit(editor, At(80, 4, 1));
        editor.Press(leftEdge);
        editor.Drag(leftEdge.Frame - 5);
        editor.Release();
        check(Event(editor, "t5") is { Start: 75, Duration: 23 }, "dragging the left edge keeps the end");
        editor.Press(Hit(editor, At(98, 4, -1)), ctrl: true);
        editor.Release();
        check(editor.Selected.Count == 0 && Event(editor, "t5").Duration == 23, "ctrl on an edge toggles the selection instead of resizing");

        // Length drag, with motion following and optional scaling.
        var handle = Hit(editor, new(100 + 400, 10));
        editor.Press(handle);
        check(editor.Gesture == TimelineGesture.Length, "pressing the length handle starts a length drag");
        editor.Drag(handle.Frame + 50);
        check(editor.Document.Length == 150 && Event(editor, "t1").Duration == 150 && Event(editor, "t3").Start == 99,
            "dragging the handle changes the length live; the motion follows");
        editor.Release();
        check(editor.History.UndoCount == 5 && editor.Undo() && editor.Document.Length == 100 && editor.Redo(), "a length drag is one undo step");

        editor.Snap = true;
        // The end is past the fitted view now; the widget would have scrolled there.
        handle = new TimelineHit(TimelinePart.LengthHandle, 150);
        editor.Press(handle);
        editor.Drag(handle.Frame - 48.6f);
        check(editor.Document.Length == 100 && editor.SnapGuide == 100, "the length snaps back to the original length");
        editor.Drag(handle.Frame - 48.6f, alt: true);
        check(editor.Document.Length == 101 && editor.SnapGuide == null, "alt drags without the magnet");
        editor.Release();
        check(editor.SnapGuide == null, "the guide disappears on release");

        editor.ScaleEvents = true;
        var scaledFrom = editor.Document;
        handle = Hit(editor, new(100 + 101 * 4, 10));
        editor.Press(handle, alt: true);
        editor.Drag(handle.Frame + 101, alt: true);
        editor.Release();
        check(editor.Document.Length == 202 && Event(editor, "t3").Start == 198 && Event(editor, "t1").Duration == 202 &&
              Event(editor, "t2").Start == 2 * TimelineEdits.Find(scaledFrom, "t2")!.Start,
            "with scaling on, the events stretch with the length");
        editor.ScaleEvents = false;

        // Snapped moves stick to other events.
        var snapping = Editor(Doc(100, Ev("a", TimelineLane.Sound, 50), Ev("b", TimelineLane.Effect, 62, 10, true)));
        snapping.Snap = true;
        var a = Hit(snapping, At(50, 3));
        snapping.Press(a);
        snapping.Drag(a.Frame + 10.4f);
        check(Event(snapping, "a").Start == 62 && snapping.SnapGuide == 62, "a moved event snaps to another event's start");
        snapping.Drag(a.Frame + 10.4f, alt: true);
        check(Event(snapping, "a").Start == 60 && snapping.SnapGuide == null, "alt moves to the plain whole frame");
        snapping.Drag(a.Frame + 49.5f);
        check(Event(snapping, "a").Start == 99 && snapping.SnapGuide == null, "a move clamped short of its snap target shows no guide");
        snapping.Release();

        // Ruler scrubbing.
        var scrub = Editor();
        var frames = new List<int>();
        scrub.PlayheadChanged += frames.Add;
        scrub.Playhead = 5;
        scrub.Press(Hit(scrub, new(100 + 30.4f * 4, 10)));
        check(scrub.Gesture == TimelineGesture.Scrub && scrub.Playhead == 30 && scrub.Selected.Count == 0, "pressing the ruler moves the playhead there");
        scrub.Drag(400);
        check(scrub.Playhead == 100, "scrubbing stops at the end");
        scrub.Cancel();
        check(scrub.Playhead == 5 && frames.SequenceEqual([5, 30, 100, 5]), "cancelling a scrub returns the playhead and every move is announced");
        scrub.Snap = true;
        scrub.Press(Hit(scrub, new(100 + 48.6f * 4, 10)));
        check(scrub.Playhead == 50 && scrub.SnapGuide == 50, "with the magnet on, scrubbing sticks to event starts");
        scrub.Release();

        // Group members select and move together.
        var props = Editor(Doc(100, Ev("p1", TimelineLane.Prop, 10, group: "g"), Ev("p2", TimelineLane.Prop, 30, group: "g"), Ev("s", TimelineLane.Sound, 5)));
        var p1 = Hit(props, At(10, 5));
        props.Press(p1);
        props.Drag(p1.Frame + 5);
        props.Release();
        check(props.Selected.SetEquals(["p1", "p2"]) && Event(props, "p1").Start == 15 && Event(props, "p2").Start == 35,
            "events added together select and move together");
    }

    private static void Keys(Action<bool, string> check)
    {
        var editor = Editor();
        var moves = 0;
        editor.PlayheadChanged += _ => moves++;
        check(editor.Key(TimelineKey.Right) && editor.Playhead == 1 && editor.Key(TimelineKey.Right, shift: true) && editor.Playhead == 11 &&
              editor.Key(TimelineKey.Left) && editor.Playhead == 10 && !new TimelineEditor(Sample()).Key(TimelineKey.Left),
            "arrow keys move the playhead a frame, ten with shift");
        check(editor.Key(TimelineKey.End) && editor.Playhead == 100 && editor.Key(TimelineKey.Home) && editor.Playhead == 0 && moves == 5,
            "home and end jump to the start and the end");

        editor.Select("t3");
        check(editor.Key(TimelineKey.Right, alt: true) && Event(editor, "t3").Start == 51 && editor.Key(TimelineKey.Left, shift: true, alt: true) &&
              Event(editor, "t3").Start == 41 && editor.Playhead == 0 && editor.History.UndoCount == 2,
            "alt with the arrows nudges the selection instead");
        check(editor.Key(TimelineKey.Undo) && Event(editor, "t3").Start == 51 && editor.Key(TimelineKey.Redo) && Event(editor, "t3").Start == 41,
            "undo and redo keys");

        editor.Select("t2");
        editor.Select("t6", ctrl: true);
        check(editor.Key(TimelineKey.Duplicate) && editor.Document.Events.Length == 7 && editor.Selected.Count == 1 &&
              Event(editor, editor.Primary!) is { Start: 40, Duration: 30, Lane: TimelineLane.Expression, Key: "n1" },
            "duplicating places editable copies right after the selection and selects them");
        check(editor.Key(TimelineKey.Delete) && editor.Document.Events.Length == 6 && editor.Selected.Count == 0, "delete removes the selection");

        editor.Select("t3");
        editor.Select("t5", ctrl: true);
        editor.Select("t1", ctrl: true);
        check(editor.Key(TimelineKey.Copy) && editor.CanPaste, "copy keeps the editable selection");
        editor.Playhead = 20;
        check(editor.Key(TimelineKey.Paste) && editor.Selected.Count == 2 && editor.Document.Events.Length == 8 &&
              editor.Selected.Select(key => Event(editor, key).Start).Order().SequenceEqual([20, 59]) &&
              editor.Selected.All(key => key.StartsWith('n')) && editor.Document.Events.Count(ev => ev.Lane == TimelineLane.Motion) == 1,
            "paste puts the copies at the playhead, relative to each other, with new keys and no locked copies");
        editor.Playhead = 90;
        check(editor.Key(TimelineKey.Paste) && editor.Selected.Select(key => Event(editor, key).Start).Order().SequenceEqual([60, 99]),
            "a paste near the end moves back so every copy plays");
        check(editor.Key(TimelineKey.Undo) && editor.Selected.Count == 0 && editor.Document.Events.Length == 8,
            "undoing a paste drops the vanished events from the selection");

        var faces = Editor(Sample() with { FacePack = "emot/laugh" });
        faces.Select("t2");
        faces.Key(TimelineKey.Copy);
        faces.Commit(faces.Document with { FacePack = "bad" });
        check(!faces.Key(TimelineKey.Paste) && faces.LastError != null && faces.Document.FacePack == "bad",
            "pasting an expression into a timeline using another library is refused with a reason");
        check(faces.Add([Ev("", TimelineLane.Sound, 3)], null) && faces.Selected.Count == 1 && faces.LastError == null &&
              !faces.Add([Ev("", TimelineLane.Expression, 3, 10, true)], "emot/laugh") && faces.LastError != null,
            "adding through the editor selects the new events or explains the refusal");

        check(editor.Key(TimelineKey.SelectAll) && editor.Selected.Count == editor.Document.Events.Length && editor.Primary != null, "select all");
        check(editor.Key(TimelineKey.Escape) && editor.Selected.Count == 0 && !editor.Key(TimelineKey.Escape), "escape clears the selection");
        var during = Hit(editor, At(41, 3));
        editor.Press(during);
        editor.Drag(during.Frame + 5);
        check(!editor.Key(TimelineKey.Delete) && editor.Key(TimelineKey.Escape) && editor.Gesture == TimelineGesture.None && Event(editor, "t3").Start == 41,
            "during a drag only escape works, and it cancels the drag");

        var scrolled = Editor(Doc(1000));
        scrolled.View = new TimelineView(900, 4, 440);
        var end = new TimelineHit(TimelinePart.LengthHandle, 1000);
        scrolled.Press(end);
        scrolled.Drag(700);
        scrolled.SetWidth(440);
        scrolled.Drag(700);
        check(scrolled.Document.Length == 700 && scrolled.View.First == 900, "the view holds still during a drag, so a length drag never runs away");
        scrolled.Release();
        check(scrolled.View.First == 700 * TimelineView.Headroom - 110, "the view settles inside the new length after the drag");
        var late = Editor(Doc(100, Ev("late", TimelineLane.Sound, 300)));
        late.Pan(-100000);
        check(late.View.First == 301 * TimelineView.Headroom - 110 && late.View.Last >= 300,
            "the view scrolls far enough to show events that start past the end");

        editor.Zoom(200, 8);
        check(editor.View.PixelsPerFrame == 32 && editor.Key(TimelineKey.Fit) && Math.Abs(editor.View.PixelsPerFrame - 4) < 1e-5 && editor.View.First == 0,
            "fit shows the whole timeline again after zooming");
        editor.Zoom(0, 1000);
        check(editor.View.PixelsPerFrame == TimelineEditor.DefaultMaxPixelsPerFrame, "zooming stops at the editor's limit");
        editor.Reveal(90);
        check(editor.View.First <= 90 && editor.View.Last >= 90, "revealing a frame scrolls it into view");

        check(editor.Key(TimelineKey.PlayPause) && editor.Playing, "play toggles playback");
        editor.Playhead = 95;
        editor.Advance(.1);
        check(editor.Playhead == 98, "playback advances by the elapsed time at 30 frames per second");
        editor.Advance(.2);
        check(editor.Playhead == 4, "playback wraps at the end");
        editor.Advance(.02);
        editor.Advance(.02);
        check(editor.Playhead == 5, "playback keeps fractions of a frame");
        editor.Playhead = 99;
        editor.Advance(1, loop: false);
        check(editor.Playhead == 100 && !editor.Playing, "playback without looping stops at the end");

        editor.Reset(Doc(40));
        check(!editor.Dirty && !editor.History.CanUndo && editor.Playhead == 0 && editor.Selected.Count == 0 && Math.Abs(editor.View.PixelsPerFrame - 10) < 1e-4,
            "loading another document starts over");
    }
}
