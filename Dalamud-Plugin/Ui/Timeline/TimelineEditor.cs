using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>Editor commands the widget maps its shortcuts to.</summary>
internal enum TimelineKey : byte { Undo, Redo, Delete, Duplicate, Copy, Paste, SelectAll, Left, Right, Home, End, Escape, Fit, PlayPause }

/// <summary>What the pointer is doing between a press and its release.</summary>
internal enum TimelineGesture : byte
{
    None,
    /// <summary>Pressed an event that does not move: selects on release, never edits.</summary>
    Select,
    Move,
    ResizeLeft,
    ResizeRight,
    Length,
    Scrub,
}

/// <summary>
/// All timeline interaction: selection, the playhead, the view, drags and shortcuts. It takes plain
/// values (a hit, modifier flags, the frame under the mouse), so every behaviour is testable
/// without ImGui; the widget only feeds input in and draws. No ImGui or Dalamud dependencies.
/// </summary>
internal sealed class TimelineEditor
{
    public const float DefaultMinPixelsPerFrame = .01f;
    public const float DefaultMaxPixelsPerFrame = 40f;

    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private TimelineLayout? layout;
    private Func<TimelineEvent, int>? visibleLength;
    private IReadOnlyDictionary<string, int>? motionFrames;
    private ImmutableArray<TimelineEvent> clipboard = [];
    private string? clipboardPack;
    // The document as it was loaded: length changes follow and restore from it, and a copy keeps
    // naming the loaded entry it copies only while the same document is loaded.
    private TimelineDocument loaded;
    private TimelineDocument? clipboardLoaded;
    private int playhead;
    private bool fitted;
    private double playFraction;

    private TimelineGesture gesture;
    private TimelineDocument? origin;
    private float pressFrame;
    private string? pressKey;
    private int playheadAtPress;
    private bool dragged;
    private bool collapseOnClick;
    private ImmutableArray<string> gestureKeys = [];
    private int[] anchors = [];
    private int[] targets = [];

    public TimelineEditor(TimelineDocument document, int capacity = TimelineHistory.DefaultCapacity)
    {
        History = new TimelineHistory(document, capacity);
        History.Changed += OnDocumentChanged;
        loaded = document;
        View = new TimelineView(0, 1, 0);
    }

    public TimelineHistory History { get; }
    public TimelineDocument Document => History.Current;
    public bool Dirty => History.Dirty;

    public TimelineView View { get; set; }

    /// <summary>The magnet: drags stick to nearby starts, ends, the length and the playhead. Alt turns it off per drag.</summary>
    public bool Snap { get; set; } = true;

    /// <summary>Whether changing the length moves and stretches the events with it.</summary>
    public bool ScaleEvents { get; set; }

    /// <summary>
    /// The length can't change: an expression's timeline has no motion of its own to resample. The
    /// length handle then only scrubs, and <see cref="SetLength"/> does nothing.
    /// </summary>
    public bool FixedLength => Document.Expression;

    public float MinPixelsPerFrame { get; set; } = DefaultMinPixelsPerFrame;
    public float MaxPixelsPerFrame { get; set; } = DefaultMaxPixelsPerFrame;

    /// <summary>How close, in pixels, the magnet pulls. The widget scales it with the UI.</summary>
    public float SnapTolerance { get; set; } = 8;

    /// <summary>The frame the running drag stuck to, for drawing a guide line.</summary>
    public int? SnapGuide { get; private set; }

    /// <summary>Why the last command did nothing, for a short message; null when it worked.</summary>
    public string? LastError { get; private set; }

    public bool Playing { get; set; }

    public TimelineGesture Gesture => gesture;

    /// <summary>A gesture is running and the pointer has moved since the press.</summary>
    public bool Dragging => gesture != TimelineGesture.None && dragged;

    /// <summary>Extra frames an event visibly takes in its row (see <see cref="TimelineLayout.Build"/>). Set it once, not per frame.</summary>
    public Func<TimelineEvent, int>? VisibleLength
    {
        get => visibleLength;
        set
        {
            if (value == visibleLength)
                return;
            visibleLength = value;
            layout = null;
        }
    }

    /// <summary>
    /// Facial motions' own lengths in frames by motion name, from the player's face files (see
    /// <see cref="TimelineDocument.PlayedFrames"/>); null while unknown. Set a new instance when they change.
    /// </summary>
    public IReadOnlyDictionary<string, int>? MotionFrames
    {
        get => motionFrames;
        set
        {
            if (ReferenceEquals(value, motionFrames))
                return;
            motionFrames = value;
            layout = null;
        }
    }

    /// <summary>The layout of the current document, rebuilt only when the document instance changes.</summary>
    public TimelineLayout Layout =>
        layout is { } current && ReferenceEquals(current.Document, Document) ? current
            : layout = TimelineLayout.Build(Document, visibleLength, motionFrames);

    public IReadOnlySet<string> Selected => selected;

    /// <summary>The event the properties panel shows and shift-ranges start from.</summary>
    public string? Primary { get; private set; }

    public TimelineEvent? PrimaryEvent => TimelineEdits.Find(Document, Primary);

    public bool IsSelected(string key) => selected.Contains(key);

    /// <summary>The frame being previewed, 0 to the length.</summary>
    public int Playhead
    {
        get => playhead;
        set
        {
            var frame = Math.Clamp(value, 0, Math.Max(0, Document.Length));
            if (frame == playhead)
                return;
            playhead = frame;
            PlayheadChanged?.Invoke(frame);
        }
    }

    /// <summary>Raised with the new frame whenever the playhead moves, so a preview can follow.</summary>
    public event Action<int>? PlayheadChanged;

    public bool CanPaste => !clipboard.IsEmpty;

    public TimelineHit HitTest(TimelineGeometry geometry, Vector2 point) => geometry.HitTest(Document, Layout, View, point);

    // Pointer gestures.

    /// <summary>
    /// The mouse went down on <paramref name="hit"/>. Events: a click selects (Ctrl toggles, Shift
    /// selects the range from the primary by start), a body starts a move of the selection and an edge
    /// a resize (with Ctrl or Shift an edge selects like the body). The length handle starts a length
    /// drag, the ruler scrubs, empty space clears the selection unless Ctrl or Shift is held. Alt
    /// turns the magnet off.
    /// </summary>
    public void Press(TimelineHit hit, bool ctrl = false, bool shift = false, bool alt = false)
    {
        if (gesture != TimelineGesture.None)
            Release();
        LastError = null;
        SnapGuide = null;
        dragged = false;
        collapseOnClick = false;
        pressFrame = hit.Frame;
        pressKey = null;
        playheadAtPress = playhead;
        var doc = Document;
        switch (hit.Part)
        {
            case TimelinePart.LengthHandle when FixedLength:
                gesture = TimelineGesture.Scrub;
                targets = TimelineSnap.Targets(doc, null);
                Scrub(hit.Frame, alt);
                break;
            case TimelinePart.LengthHandle:
            {
                var lasted = Events(loaded).Where(ev => ev.Lane == TimelineLane.Motion && loaded.LastsWhole(ev.Duration))
                    .Select(ev => ev.Key).ToHashSet(StringComparer.Ordinal);
                var followers = Events(doc).Where(ev => ScaleEvents || lasted.Contains(ev.Key))
                    .Select(ev => ev.Key).ToHashSet(StringComparer.Ordinal);
                BeginEdit(TimelineGesture.Length, [], [doc.Length],
                    TimelineSnap.Targets(doc, playhead, followers, length: false, sourceLength: true));
                break;
            }
            case TimelinePart.Ruler:
                gesture = TimelineGesture.Scrub;
                targets = TimelineSnap.Targets(doc, null);
                Scrub(hit.Frame, alt);
                break;
            case TimelinePart.Body or TimelinePart.LeftEdge or TimelinePart.RightEdge
                when hit.Key is { } key && TimelineEdits.Find(doc, key) is { } ev:
                pressKey = key;
                if (hit.Part != TimelinePart.Body && !ctrl && !shift && TimelineEdits.CanResize(ev))
                {
                    if (!selected.Contains(key))
                        SelectOnly(key);
                    Primary = key;
                    var left = hit.Part == TimelinePart.LeftEdge;
                    BeginEdit(left ? TimelineGesture.ResizeLeft : TimelineGesture.ResizeRight, [key],
                        [left ? ev.Start : TimelineEdits.End(ev)], TimelineSnap.Targets(doc, playhead, new HashSet<string> { key }));
                    break;
                }
                if (ctrl)
                    Toggle(key);
                else if (shift)
                    SelectRange(key);
                else if (selected.Contains(key))
                {
                    // Keep the selection so the group can be dragged; a plain click narrows it on release.
                    collapseOnClick = !GroupOf(doc, key).SetEquals(selected);
                    Primary = key;
                }
                else
                    SelectOnly(key);
                var moving = Events(doc).Where(item => selected.Contains(item.Key) && TimelineEdits.CanMove(item)).ToList();
                if (!selected.Contains(key) || !TimelineEdits.CanMove(ev) || moving.Count == 0)
                {
                    gesture = TimelineGesture.Select;
                    break;
                }
                var keys = moving.Select(item => item.Key).ToImmutableArray();
                BeginEdit(TimelineGesture.Move, keys,
                    [.. moving.SelectMany(item => item.Duration > 0 && !TimelineDocument.OwnPace(item)
                        ? new[] { item.Start, TimelineEdits.End(item) } : [item.Start])],
                    TimelineSnap.Targets(doc, playhead, keys.ToHashSet(StringComparer.Ordinal)));
                break;
            case TimelinePart.Lane or TimelinePart.None:
                if (!ctrl && !shift)
                    ClearSelection();
                break;
        }
    }

    /// <summary>
    /// The mouse moved to <paramref name="frameAtMouse"/> while held. Edits are computed from the
    /// document at the press plus a whole-frame offset, so a long drag never drifts.
    /// </summary>
    public void Drag(float frameAtMouse, bool alt = false)
    {
        if (gesture is TimelineGesture.None or TimelineGesture.Select || !float.IsFinite(frameAtMouse))
            return;
        dragged = true;
        var magnet = Snap && !alt;
        if (gesture == TimelineGesture.Scrub)
        {
            Scrub(frameAtMouse, alt);
            return;
        }
        if (origin is not { } start)
            return;
        var delta = TimelineSnap.SnapDelta(frameAtMouse - pressFrame, anchors, targets, SnapTolerance, View.Scale, magnet, out var guide);
        TimelineDocument next;
        int landed;
        switch (gesture)
        {
            case TimelineGesture.Move:
                var applied = TimelineEdits.ClampMoveDelta(start, gestureKeys, delta);
                next = applied == 0 ? start : TimelineEdits.Move(start, gestureKeys, applied);
                landed = applied == delta ? guide ?? -1 : -1;
                break;
            case TimelineGesture.ResizeLeft or TimelineGesture.ResizeRight:
                var left = gesture == TimelineGesture.ResizeLeft;
                next = TimelineEdits.Resize(start, gestureKeys[0], left, delta);
                var resized = TimelineEdits.Find(next, gestureKeys[0]);
                landed = resized == null ? -1 : left ? resized.Start : TimelineEdits.End(resized);
                break;
            case TimelineGesture.Length:
                next = TimelineEdits.SetLength(start, start.Length + delta, ScaleEvents, loaded);
                landed = next.Length;
                break;
            default:
                return;
        }
        SnapGuide = guide == landed ? guide : null;
        History.Preview(next);
    }

    /// <summary>The mouse was released: a drag becomes one undo step; a click without a drag only selects.</summary>
    public void Release()
    {
        if (gesture is TimelineGesture.Move or TimelineGesture.ResizeLeft or TimelineGesture.ResizeRight or TimelineGesture.Length)
            History.End();
        if (!dragged && collapseOnClick && pressKey != null && TimelineEdits.Find(Document, pressKey) != null)
            SelectOnly(pressKey);
        EndGesture();
        ClampView();
    }

    /// <summary>Escape during a drag: back to the document and playhead from before the press.</summary>
    public void Cancel()
    {
        if (gesture is TimelineGesture.Move or TimelineGesture.ResizeLeft or TimelineGesture.ResizeRight or TimelineGesture.Length)
            History.Cancel();
        if (gesture != TimelineGesture.None)
            Playhead = playheadAtPress;
        EndGesture();
        ClampView();
    }

    // Shortcuts and commands.

    /// <summary>
    /// Runs a shortcut. Left and Right move the playhead a frame (Shift: ten); with Alt they nudge
    /// the selection instead. Returns whether it did anything. Only Escape works during a drag.
    /// </summary>
    public bool Key(TimelineKey key, bool shift = false, bool alt = false)
    {
        if (gesture != TimelineGesture.None)
        {
            if (key != TimelineKey.Escape)
                return false;
            Cancel();
            return true;
        }
        LastError = null;
        switch (key)
        {
            case TimelineKey.Undo: return Undo();
            case TimelineKey.Redo: return Redo();
            case TimelineKey.Delete: return DeleteSelection();
            case TimelineKey.Duplicate: return DuplicateSelection();
            case TimelineKey.Copy: return Copy();
            case TimelineKey.Paste: return Paste();
            case TimelineKey.SelectAll:
                SelectAll();
                return selected.Count > 0;
            case TimelineKey.Left or TimelineKey.Right:
                var step = (shift ? 10 : 1) * (key == TimelineKey.Left ? -1 : 1);
                if (alt)
                    return Nudge(step);
                var before = playhead;
                Playhead = playhead + step;
                return playhead != before;
            case TimelineKey.Home:
                Playhead = 0;
                return true;
            case TimelineKey.End:
                Playhead = Document.Length;
                return true;
            case TimelineKey.Escape:
                var had = selected.Count > 0;
                ClearSelection();
                return had;
            case TimelineKey.Fit:
                Fit();
                return true;
            case TimelineKey.PlayPause:
                Playing = !Playing;
                playFraction = 0;
                return true;
            default:
                return false;
        }
    }

    public bool Undo() => gesture == TimelineGesture.None && History.Undo();

    public bool Redo() => gesture == TimelineGesture.None && History.Redo();

    /// <summary>Makes <paramref name="doc"/> current as one undo step; false when it is the current one.</summary>
    public bool Commit(TimelineDocument doc)
    {
        if (gesture != TimelineGesture.None || ReferenceEquals(doc, Document))
            return false;
        History.Commit(doc);
        return true;
    }

    /// <summary>
    /// Adds events built for an Add and selects them; false with <see cref="LastError"/> when refused.
    /// Events that would start past the end move back together, so an add at the end still plays.
    /// </summary>
    public bool Add(IEnumerable<TimelineEvent> events, string? pack)
    {
        LastError = null;
        var doc = Document;
        var added = events.ToList();
        if (added.Count > 0)
        {
            var first = added.Min(ev => ev.Start);
            var shift = Math.Max(-first, Math.Min(0, Math.Min(doc.Length - 1, TimelineDocument.MaxFrames) - added.Max(ev => ev.Start)));
            if (first >= 0 && shift != 0)
                added = added.ConvertAll(ev => ev with { Start = ev.Start + shift });
        }
        var next = TimelineEdits.Add(doc, added, pack, out var created, out var error);
        LastError = error;
        if (!Commit(next))
            return false;
        SelectKeys(created);
        return true;
    }

    /// <summary>Sets the length as one undo step, scaling the events when <see cref="ScaleEvents"/> is on.</summary>
    public bool SetLength(int frames) => !FixedLength && Commit(TimelineEdits.SetLength(Document, frames, ScaleEvents, loaded));

    /// <summary>Keeps the face <paramref name="key"/> on, or stops keeping it (see <see cref="TimelineEdits.Keep"/>).</summary>
    public bool Keep(string key, bool keep) => Commit(TimelineEdits.Keep(Document, key, keep, motionFrames));

    public bool DeleteSelection() => Commit(TimelineEdits.Delete(Document, selected));

    /// <summary>Copies the selected editable events right after themselves and selects the copies.</summary>
    public bool DuplicateSelection()
    {
        var doc = Document;
        var rows = Layout;
        int first = int.MaxValue, end = int.MinValue;
        var events = Events(doc);
        for (var index = 0; index < events.Length; index++)
            if (selected.Contains(events[index].Key) && TimelineEdits.CanEdit(events[index]))
            {
                first = Math.Min(first, events[index].Start);
                end = Math.Max(end, rows.EndOf(index));
            }
        if (first == int.MaxValue)
            return false;
        var next = TimelineEdits.Duplicate(doc, selected, end - first, out var created);
        if (!Commit(next))
            return false;
        SelectKeys(created);
        return true;
    }

    /// <summary>Keeps the selected editable events, relative to the earliest, for <see cref="Paste"/>.</summary>
    public bool Copy()
    {
        var copied = Events(Document).Where(ev => selected.Contains(ev.Key) && TimelineEdits.CanEdit(ev)).ToList();
        if (copied.Count == 0)
            return false;
        var first = copied.Min(ev => ev.Start);
        clipboard = [.. copied.Select(ev => ev with { Start = ev.Start - first })];
        clipboardLoaded = loaded;
        clipboardPack = copied.Any(ev => ev.Lane == TimelineLane.Expression) ? Document.FacePack : null;
        return true;
    }

    /// <summary>Pastes the copied events at the playhead (earlier if they would start past the end) and selects them.</summary>
    public bool Paste()
    {
        if (clipboard.IsEmpty)
            return false;
        // Copied from another clip (or before a save reloaded this one), the entries they name are
        // not this document's: those copies are built like new ones.
        var same = ReferenceEquals(clipboardLoaded, loaded);
        return Add(clipboard.Select(ev => same ? ev with { Start = ev.Start + playhead }
            : ev with { Start = ev.Start + playhead, Key = "", CloneOf = null }), clipboardPack);
    }

    // Properties panel and context menu.

    /// <summary>A properties field became active: its changes until <see cref="EndFieldEdit"/> make one undo step.</summary>
    public void BeginFieldEdit()
    {
        if (gesture == TimelineGesture.None)
            History.Begin();
    }

    /// <summary>The properties field was left.</summary>
    public void EndFieldEdit()
    {
        if (gesture == TimelineGesture.None)
            History.End();
    }

    /// <summary>
    /// The properties panel's start for a selection of several events: they all move, as a drag
    /// moves them, so the primary one starts at <paramref name="frame"/>. Live during a field edit,
    /// else one undo step. False when nothing moved or a drag is running.
    /// </summary>
    public bool MoveSelectionStart(int frame)
    {
        if (gesture != TimelineGesture.None || PrimaryEvent is not { } primary)
            return false;
        var next = TimelineEdits.Move(Document, selected, frame - primary.Start);
        if (ReferenceEquals(next, Document))
            return false;
        if (History.InGesture)
            History.Preview(next);
        else
            History.Commit(next);
        return true;
    }

    /// <summary>
    /// A properties-panel change to the event with <paramref name="ev"/>'s key, as far as its access
    /// allows (see <see cref="TimelineEdits.Replace"/>): live during a field edit, else one undo step.
    /// False when nothing changed or a drag is running.
    /// </summary>
    public bool Replace(TimelineEvent ev)
    {
        if (gesture != TimelineGesture.None)
            return false;
        var next = TimelineEdits.Replace(Document, ev, motionFrames);
        if (ReferenceEquals(next, Document))
            return false;
        if (History.InGesture)
            History.Preview(next);
        else
            History.Commit(next);
        return true;
    }

    /// <summary>
    /// A right click on <paramref name="hit"/>, before the widget opens its menu: an event outside the
    /// selection becomes the selection, so the menu acts on what it was opened on, while one inside
    /// keeps it. Returns the whole frame under the pointer, snapped, for "add here" or "set length here".
    /// </summary>
    public int ContextMenu(TimelineHit hit, bool alt = false)
    {
        if (gesture == TimelineGesture.None && hit.OnEvent && TimelineEdits.Find(Document, hit.Key) != null)
        {
            if (selected.Contains(hit.Key!))
                Primary = hit.Key;
            else
                SelectOnly(hit.Key!);
        }
        return SnappedFrame(hit.Frame, alt);
    }

    /// <summary>
    /// <paramref name="frame"/> as a whole frame from 0 on, pulled to 0, the length, the playhead and
    /// every event's start and end when the magnet is on; for adding at the pointer (double click).
    /// </summary>
    public int SnappedFrame(float frame, bool alt = false) =>
        Math.Max(0, TimelineSnap.Snap(frame, TimelineSnap.Targets(Document, playhead), SnapTolerance, View.Scale, Snap && !alt, out _));

    /// <summary>Moves the selection by <paramref name="delta"/> frames as one undo step.</summary>
    public bool Nudge(int delta) => Commit(TimelineEdits.Move(Document, selected, delta));

    /// <summary>Moves the selection so the primary event starts at <paramref name="frame"/>.</summary>
    public bool MoveSelectionTo(int frame) =>
        PrimaryEvent is { } primary && Commit(TimelineEdits.Move(Document, selected, frame - primary.Start));

    // Selection.

    /// <summary>Selects an event the way a click does (Ctrl toggles, Shift selects a range), for lists and menus.</summary>
    public void Select(string key, bool ctrl = false, bool shift = false)
    {
        if (TimelineEdits.Find(Document, key) == null)
            return;
        if (ctrl)
            Toggle(key);
        else if (shift)
            SelectRange(key);
        else
            SelectOnly(key);
    }

    public void SelectAll()
    {
        foreach (var ev in Events(Document))
            selected.Add(ev.Key);
        if (Primary == null || !selected.Contains(Primary))
            Primary = Events(Document).OrderBy(ev => ev.Start).Select(ev => ev.Key).FirstOrDefault();
    }

    public void ClearSelection()
    {
        selected.Clear();
        Primary = null;
    }

    // View and playback.

    /// <summary>Zooms around <paramref name="x"/> (track pixels) within the zoom limits.</summary>
    public void Zoom(float x, float factor) =>
        View = View.ZoomAt(x, factor, MinPixelsPerFrame, MaxPixelsPerFrame).Clamp(TimelineEdits.Extent(Document));

    public void Pan(float dx) => View = View.Pan(dx).Clamp(TimelineEdits.Extent(Document));

    /// <summary>Shows the whole timeline, within the zoom limits.</summary>
    public void Fit()
    {
        var fit = TimelineView.Fit(Document.Length, View.Width);
        View = (fit with { PixelsPerFrame = Math.Clamp(fit.PixelsPerFrame, MinPixelsPerFrame, Math.Max(MinPixelsPerFrame, MaxPixelsPerFrame)) })
            .Clamp(TimelineEdits.Extent(Document));
        fitted = View.Width > 0;
    }

    /// <summary>
    /// Called every frame with the track width; the first real width fits the timeline. The view is
    /// not clamped during a drag: a shrinking length would scroll the view under the pointer, which
    /// would shrink it further.
    /// </summary>
    public void SetWidth(float width)
    {
        width = float.IsFinite(width) ? Math.Max(0, width) : 0;
        if (width != View.Width)
            View = View with { Width = width };
        if (!fitted && width > 0)
            Fit();
        else if (gesture == TimelineGesture.None)
            ClampView();
    }

    /// <summary>Scrolls just enough to show <paramref name="frame"/>, for following the playhead.</summary>
    public void Reveal(int frame)
    {
        var view = View;
        if (view.Width <= 0 || frame >= view.First && frame <= view.Last)
            return;
        var margin = view.Width * .1f / view.Scale;
        View = (frame < view.First ? view with { First = frame - margin } : view with { First = frame + margin - view.Width / view.Scale })
            .Clamp(TimelineEdits.Extent(Document));
    }

    /// <summary>
    /// Moves the playhead by <paramref name="seconds"/> of playback while playing, keeping the
    /// fraction of a frame for the next call. At the end it wraps when <paramref name="loop"/>, else stops.
    /// </summary>
    public void Advance(double seconds, bool loop = true)
    {
        if (!Playing || !double.IsFinite(seconds) || seconds <= 0 || gesture == TimelineGesture.Scrub)
            return;
        playFraction += seconds * TimelineDocument.Fps;
        var whole = (int)Math.Floor(playFraction);
        if (whole == 0)
            return;
        playFraction -= whole;
        var length = Math.Max(1, Document.Length);
        var next = playhead + whole;
        if (next >= length)
        {
            if (loop)
                next %= length;
            else
            {
                next = length;
                Playing = false;
            }
        }
        Playhead = next;
    }

    /// <summary>Loads another document: history, selection, playhead, view and any drag start over.</summary>
    public void Reset(TimelineDocument doc)
    {
        gesture = TimelineGesture.None;
        origin = null;
        SnapGuide = null;
        LastError = null;
        Playing = false;
        playFraction = 0;
        ClearSelection();
        loaded = doc;
        History.Reset(doc);
        Playhead = 0;
        fitted = false;
        if (View.Width > 0)
            Fit();
    }

    public void MarkSaved() => History.MarkSaved();

    private void ClampView()
    {
        if (View.Width > 0)
            View = View.Clamp(TimelineEdits.Extent(Document));
    }

    private void BeginEdit(TimelineGesture kind, ImmutableArray<string> keys, int[] edgeAnchors, int[] snapTargets)
    {
        History.Begin();
        origin = Document;
        gesture = kind;
        gestureKeys = keys;
        anchors = edgeAnchors;
        targets = snapTargets;
    }

    private void EndGesture()
    {
        gesture = TimelineGesture.None;
        origin = null;
        SnapGuide = null;
        dragged = false;
        collapseOnClick = false;
        pressKey = null;
        gestureKeys = [];
        anchors = [];
        targets = [];
    }

    private void Scrub(float frame, bool alt)
    {
        var snapped = TimelineSnap.Snap(frame, targets, SnapTolerance, View.Scale, Snap && !alt, out var guide);
        Playhead = snapped;
        SnapGuide = guide == playhead ? guide : null;
    }

    private void SelectOnly(string key)
    {
        selected.Clear();
        selected.UnionWith(GroupOf(Document, key));
        Primary = key;
    }

    private void Toggle(string key)
    {
        var group = GroupOf(Document, key);
        if (selected.Contains(key))
        {
            selected.ExceptWith(group);
            if (Primary != null && !selected.Contains(Primary))
                Primary = Events(Document).Where(ev => selected.Contains(ev.Key)).Select(ev => ev.Key).FirstOrDefault();
            return;
        }
        selected.UnionWith(group);
        Primary = key;
    }

    /// <summary>Every event whose start lies between the primary's and this one's, across all lanes.</summary>
    private void SelectRange(string key)
    {
        var doc = Document;
        if (TimelineEdits.Find(doc, Primary) is not { } anchor || TimelineEdits.Find(doc, key) is not { } clicked)
        {
            SelectOnly(key);
            return;
        }
        var low = Math.Min(anchor.Start, clicked.Start);
        var high = Math.Max(anchor.Start, clicked.Start);
        selected.Clear();
        foreach (var ev in Events(doc))
            if (ev.Start >= low && ev.Start <= high)
                selected.UnionWith(GroupOf(doc, ev.Key));
    }

    private void SelectKeys(IEnumerable<string> keys)
    {
        selected.Clear();
        selected.UnionWith(keys);
        Primary = selected.Count == 0 ? null : keys.First();
    }

    /// <summary>An event and every event added together with it, which select and move as one.</summary>
    private static HashSet<string> GroupOf(TimelineDocument doc, string key)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { key };
        if (TimelineEdits.Find(doc, key)?.Group is { } group)
            foreach (var ev in Events(doc))
                if (ev.Group == group)
                    keys.Add(ev.Key);
        return keys;
    }

    /// <summary>Drops selected keys an undo removed and keeps the playhead inside the timeline.</summary>
    private void OnDocumentChanged()
    {
        var doc = Document;
        if (selected.Count > 0)
        {
            var keys = Events(doc).Select(ev => ev.Key).ToHashSet(StringComparer.Ordinal);
            selected.IntersectWith(keys);
            if (Primary != null && !selected.Contains(Primary))
                Primary = Events(doc).Where(ev => selected.Contains(ev.Key)).Select(ev => ev.Key).FirstOrDefault();
        }
        else
            Primary = null;
        Playhead = playhead;
    }

    private static ImmutableArray<TimelineEvent> Events(TimelineDocument doc) => doc.Events.IsDefault ? [] : doc.Events;
}
