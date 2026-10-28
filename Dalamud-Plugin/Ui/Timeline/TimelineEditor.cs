using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>Editor commands the widget maps its shortcuts to.</summary>
internal enum TimelineKey : byte
{
    Undo, Redo, Delete, Duplicate, Copy, Paste, SelectAll, Left, Right, Home, End, Escape, Fit, PlayPause,
    /// <summary>Cuts the motion at the playhead.</summary>
    Cut,
    /// <summary>Removes from the start of the part under the playhead to the playhead.</summary>
    RemoveBefore,
    /// <summary>Removes from the playhead to the end of the part under it.</summary>
    RemoveAfter,
    /// <summary>Turns the selected part into a blend, or back into its own motion (B).</summary>
    Blend,
}

/// <summary>What on the clip band is selected: a part, a join or the loop seam.</summary>
internal enum TimelineCutTarget : byte { Part, Join, Seam }

/// <summary>A selected part or join of the cut motion by its index, or the loop seam.</summary>
internal readonly record struct TimelineCutSelection(TimelineCutTarget Target, int Index);

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
    /// <summary>Moves a bone layer with its keys.</summary>
    LayerMove,
    LayerLeft,
    LayerRight,
    LayerFadeIn,
    LayerFadeOut,
    /// <summary>Moves one key of a bone layer.</summary>
    LayerKey,
    /// <summary>Moves one bone's value out of a key of a bone layer, on that bone's row.</summary>
    BoneKey,
    /// <summary>Moves every picked key and bone value of a bone layer together.</summary>
    LayerKeys,
    /// <summary>Picks a layer's keys and bone values inside a box drawn on its rows; never edits.</summary>
    KeyBox,
    /// <summary>Trims the start of a part of the cut motion.</summary>
    TrimLeft,
    /// <summary>Trims the end of a part of the cut motion.</summary>
    TrimRight,
    /// <summary>Sets a join's or the loop seam's blend length by its outer edge.</summary>
    BlendLength,
    /// <summary>Sets how long the loop seam's ease is, by its inner edge.</summary>
    EaseLength,
    /// <summary>Plays a part slower or faster by its end (Shift on a part's right edge).</summary>
    Stretch,
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
    // The bone layer a layer gesture drags, and for a key drag the key's frame when it began and the bone a bone key drag moves.
    private string? gestureLayer;
    private int gestureKeyFrame;
    private string? gestureBone;
    // The picks a group key drag or a key box began with, the primary pick a plain click narrows to on release, and the
    // rows the box spans (from its pressed row to the last one under the pointer).
    private ImmutableArray<KeyPick> gesturePicks = [];
    // The layer, picks and primary pick before a key box, which Escape goes back to.
    private (string? Layer, ImmutableArray<KeyPick> Picks, KeyPick? Primary) boxBefore = (null, [], null);
    private KeyPick? collapseToPick;
    private int boxRow;
    private int boxLastRow;
    private float boxFrame = float.NaN;
    // A trim's part or a blend drag's join (-1: the seam), and which way a blend edge lengthens it.
    private int gesturePart;
    private int gestureSide;
    // Layers whose bone rows the user hid (every other layer shows them), and the skeleton's bone order for
    // those rows; either changing rebuilds the layout.
    private readonly HashSet<string> folded = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int>? boneOrder;
    // The selected bones, which the Motion keys layer lists as rows even without keys; changing them rebuilds the layout.
    private IReadOnlyList<string> markerBones = [];
    // The zero keys WithReach added during the running gesture (by History.GestureSerial), by frame and bone.
    private (int Gesture, HashSet<(int Frame, string Bone)> Keys)? reached;
    private int layoutStamp;
    private int builtStamp = -1;

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

    /// <summary>What the last add or face change did besides, such as switching expression sets; cleared by the next click or shortcut.</summary>
    public string? LastNotice { get; private set; }

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

    /// <summary>
    /// The face and lip motions the face always has loaded, by motion name: they need no expression
    /// library (see <see cref="TimelineEdits.FromPack"/>). Null while unknown, when every face counts.
    /// </summary>
    public IReadOnlySet<string>? ResidentMotions { get; set; }

    /// <summary>
    /// Every expression library each face and lip motion is in, by motion name, so a face from another
    /// one can switch the timeline over (see <see cref="ChoosePack"/>). Null while unknown.
    /// </summary>
    public IReadOnlyDictionary<string, ImmutableArray<string>>? MotionPacks { get; set; }

    /// <summary>
    /// The skeleton's bone indices by name, so bone rows list parents first; null while unknown, when
    /// they go by name. Set a new instance when the skeleton changes.
    /// </summary>
    public IReadOnlyDictionary<string, int>? BoneOrder
    {
        get => boneOrder;
        set
        {
            if (ReferenceEquals(value, boneOrder))
                return;
            boneOrder = value;
            layoutStamp++;
        }
    }

    /// <summary>
    /// The motion's key markers found for exactly the current cuts, length and detail (see <see cref="KeyMarkers"/>), which
    /// <see cref="WithReach"/> places zero keys by; null while they are searched, after the search failed or when unknown,
    /// when edits add no zero keys.
    /// </summary>
    public MotionMarkerSet? Markers { get; set; }

    /// <summary>
    /// The markers to draw: <see cref="Markers"/>, or while newer ones are searched the last set found for the same
    /// decode, which may be for other cuts, length or detail. Never used to place zero keys.
    /// </summary>
    public MotionMarkerSet? ShownMarkers { get; set; }

    /// <summary>The selected bones, for the Motion keys layer's rows. A copy is kept; a different sequence rebuilds the layout.</summary>
    public IReadOnlyList<string> MarkerBones
    {
        get => markerBones;
        set
        {
            if (markerBones.SequenceEqual(value, StringComparer.Ordinal))
                return;
            markerBones = [.. value];
            layoutStamp++;
        }
    }

    /// <summary>The layout of the current document, rebuilt only when the document instance, the folding or the bone order changes.</summary>
    public TimelineLayout Layout
    {
        get
        {
            if (layout is { } current && ReferenceEquals(current.Document, Document) && builtStamp == layoutStamp)
                return current;
            builtStamp = layoutStamp;
            var unfolded = Document.Motion.Select(layer => layer.Key).Where(key => !folded.Contains(key)).ToHashSet(StringComparer.Ordinal);
            return layout = TimelineLayout.Build(Document, visibleLength, motionFrames, unfolded, boneOrder, markerBones);
        }
    }

    /// <summary>Whether the layer with <paramref name="key"/> shows a row per bone under it.</summary>
    public bool IsUnfolded(string key) => !folded.Contains(key) && MotionEdits.Find(Document, key) != null;

    /// <summary>Shows every layer's bone rows again, for another clip whose layers only share the keys.</summary>
    public void UnfoldAll()
    {
        if (folded.Count == 0)
            return;
        folded.Clear();
        layoutStamp++;
    }

    /// <summary>Shows or hides a layer's bone rows. A view setting: never an undo step.</summary>
    public void SetUnfolded(string key, bool show)
    {
        if (show ? folded.Remove(key) : folded.Add(key))
            layoutStamp++;
    }

    public IReadOnlySet<string> Selected => selected;

    /// <summary>The event the properties panel shows and shift-ranges start from.</summary>
    public string? Primary { get; private set; }

    /// <summary>
    /// The selected bone layer, which the properties panel shows. Selecting a layer clears the event
    /// selection and the other way round.
    /// </summary>
    public string? SelectedLayer { get; private set; }

    /// <summary>The frame of the selected layer's selected key, or null.</summary>
    public int? SelectedKey { get; private set; }

    /// <summary>
    /// The bone whose value in the selected key was chosen on its own row, or null when the whole key
    /// was: deleting and nudging then act on that bone alone.
    /// </summary>
    public string? SelectedBone { get; private set; }

    private readonly HashSet<KeyPick> keyPicks = [];

    /// <summary>
    /// Every picked key and bone value of the selected layer, the selected key among them: Ctrl+click adds one, Shift+click
    /// a row's range, a box on the layer's rows those inside. They move, spread, delete and copy together.
    /// </summary>
    public IReadOnlySet<KeyPick> SelectedKeys => keyPicks;

    // PickedKeys for the document it was built on; cleared wherever keyPicks changes.
    private (TimelineDocument Doc, ImmutableArray<KeyPick> Picks, HashSet<int> Frames)? picked;

    /// <summary>
    /// <see cref="SelectedKeys"/> still in the selected layer, by frame then bone (see <see cref="MotionEdits.Picks"/>), and their
    /// frames: built once per change of the picks or the document, as the canvas and the panel draw them every frame.
    /// </summary>
    public (ImmutableArray<KeyPick> Picks, IReadOnlySet<int> Frames) PickedKeys
    {
        get
        {
            if (picked is not { } cache || !ReferenceEquals(cache.Doc, Document))
            {
                ImmutableArray<KeyPick> picks = SelectedLayerValue is { } layer ? MotionEdits.Picks(layer, keyPicks) : [];
                picked = cache = (Document, picks, picks.Select(pick => pick.Frame).ToHashSet());
            }
            return (cache.Picks, cache.Frames);
        }
    }

    /// <summary>Whether the selected layer's key at <paramref name="frame"/> is picked whole, or with <paramref name="bone"/> that bone's value in it.</summary>
    public bool IsPicked(string layer, int frame, string? bone = null) => SelectedLayer == layer && keyPicks.Contains(new KeyPick(frame, bone));

    /// <summary>The box a key box gesture spans while dragged: frames from and to, first and last row; null otherwise.</summary>
    public (float From, float To, int FirstRow, int LastRow)? KeyBox
    {
        get
        {
            if (gesture != TimelineGesture.KeyBox || !dragged || MotionEdits.IndexOf(Document, gestureLayer) is not (var index and >= 0))
                return null;
            // The box stays on its layer's rows: the layer's own and its bones'.
            var top = Layout.LayerRow(index);
            var bottom = top + Layout.BoneRows(index);
            return (Math.Min(pressFrame, boxFrame), Math.Max(pressFrame, boxFrame),
                Math.Clamp(Math.Min(boxRow, boxLastRow), top, bottom), Math.Clamp(Math.Max(boxRow, boxLastRow), top, bottom));
        }
    }

    public MotionLayer? SelectedLayerValue => MotionEdits.Find(Document, SelectedLayer);

    /// <summary>
    /// The selected part, join or seam of the cut motion, which the properties panel shows. Selecting
    /// one clears the event and layer selection, and the other way round.
    /// </summary>
    public TimelineCutSelection? SelectedCut
    {
        get;
        private set
        {
            field = value;
            selectedCutAt = CutPlace(value);
            var cuts = CutEdits.CutsOf(Document);
            selectedCutCounts = (cuts.Parts.Length, cuts.Joins.Length);
        }
    }

    // Where the selected part starts or the selected join sits, in output frames, and how many parts and joins
    // there were, when it was selected or last kept.
    private double? selectedCutAt;
    private (int Parts, int Joins) selectedCutCounts;

    private double? CutPlace(TimelineCutSelection? cut)
    {
        var cuts = CutEdits.CutsOf(Document);
        return cut switch
        {
            { Target: TimelineCutTarget.Part, Index: var part } when part >= 0 && part < cuts.Parts.Length => cuts.PartStart(part, Document.Length),
            { Target: TimelineCutTarget.Join, Index: var join } when join >= 0 && join < cuts.Joins.Length => cuts.PartEnd(join, Document.Length),
            _ => null,
        };
    }

    /// <summary>Whether the game plays the clip as a loop: only then is there a seam to blend or fix.</summary>
    public bool Loops { get; set; }

    /// <summary>What the last cut command removed with it; cleared by the next click or shortcut.</summary>
    public CutRemoval? LastRemoval { get; private set; }

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

    public TimelineHit HitTest(TimelineGeometry geometry, Vector2 point) => geometry.HitTest(Document, Layout, View, point, Loops, ShownMarkers);

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
        // The canvas draws before the properties panel, so a field edit this click ends is still
        // open: close it as its own step before this press starts another.
        History.End();
        LastError = null;
        LastNotice = null;
        LastRemoval = null;
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
                    // A prop's summon takes its parts along (TimelineEdits.ResizeGroup), so none of them is a snap target.
                    var resizing = TimelineEdits.IsGroupSummon(ev) ? GroupOf(doc, key) : new HashSet<string> { key };
                    BeginEdit(left ? TimelineGesture.ResizeLeft : TimelineGesture.ResizeRight, [key],
                        [left ? ev.Start : TimelineEdits.End(ev)], TimelineSnap.Targets(doc, playhead, resizing));
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
            case TimelinePart.MotionLane or TimelinePart.BoneLane when hit.Row >= 0 && MotionEdits.Find(doc, hit.Layer) != null:
            case TimelinePart.LayerBody when (ctrl || shift) && hit.Row >= 0 && MotionEdits.Find(doc, hit.Layer) != null:
                PressBox(hit, hit.Layer!, ctrl || shift);
                break;
            case TimelinePart.Lane or TimelinePart.None or TimelinePart.MotionLane:
                if (!ctrl && !shift)
                    ClearSelection();
                break;
            case TimelinePart.Legend or TimelinePart.BoneLane when hit.Layer != null && MotionEdits.Find(doc, hit.Layer) != null:
                SelectLayer(hit.Layer);
                gesture = TimelineGesture.Select;
                break;
            case TimelinePart.LayerUnfold when hit.Layer is { } folding && MotionEdits.Find(doc, folding) != null:
                SetUnfolded(folding, !IsUnfolded(folding));
                break;
            case TimelinePart.LayerBody or TimelinePart.LayerLeft or TimelinePart.LayerRight or TimelinePart.LayerFadeIn or
                TimelinePart.LayerFadeOut or TimelinePart.LayerKey or TimelinePart.BoneKey when hit.Layer is { } key && MotionEdits.Find(doc, key) is { } layer:
                PressLayer(hit, key, layer, doc, ctrl, shift);
                break;
            case TimelinePart.ClipRow:
                ClearSelection();
                break;
            case TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight or TimelinePart.Join or TimelinePart.JoinEdge or
                TimelinePart.Seam or TimelinePart.SeamEdge or TimelinePart.EaseEdge when !doc.Expression:
                PressClip(hit, doc, shift);
                break;
        }
    }

    /// <summary>
    /// A press on the clip band: a part, join or the seam becomes the selection (a selected blend's body
    /// pressed again selects the part under it); a part's edge starts a trim, a part's right edge with
    /// <paramref name="shift"/> a change of its speed, and a blend's outer edge a change of its length.
    /// </summary>
    private void PressClip(TimelineHit hit, TimelineDocument doc, bool shift)
    {
        var cuts = CutEdits.CutsOf(doc);
        var snap = TimelineSnap.Targets(doc, playhead);
        switch (hit.Part)
        {
            case TimelinePart.ClipRight when shift && hit.Cut >= 0 && hit.Cut < cuts.Parts.Length && !cuts.Parts[hit.Cut].Blend:
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, hit.Cut));
                gesturePart = hit.Cut;
                BeginEdit(TimelineGesture.Stretch, [], [TimelineSnap.Round(cuts.PartEnd(hit.Cut, doc.Length))], snap);
                break;
            case TimelinePart.ClipLeft or TimelinePart.ClipRight when hit.Cut >= 0 && hit.Cut < cuts.Parts.Length:
            {
                var left = hit.Part == TimelinePart.ClipLeft;
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, hit.Cut));
                gesturePart = hit.Cut;
                var edge = left ? cuts.PartStart(hit.Cut, doc.Length) : cuts.PartEnd(hit.Cut, doc.Length);
                BeginEdit(left ? TimelineGesture.TrimLeft : TimelineGesture.TrimRight, [], [TimelineSnap.Round(edge)], snap);
                break;
            }
            case TimelinePart.JoinEdge when hit.Cut >= 0 && hit.Cut < cuts.Joins.Length:
            {
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Join, hit.Cut));
                var at = cuts.PartEnd(hit.Cut, doc.Length);
                gesturePart = hit.Cut;
                gestureSide = hit.Frame < at ? -1 : 1;
                var (before, after) = MotionCutter.Window(cuts, hit.Cut, doc.Length);
                BeginEdit(TimelineGesture.BlendLength, [], [TimelineSnap.Round(gestureSide < 0 ? at - before : at + after)], snap);
                break;
            }
            case TimelinePart.SeamEdge when Loops && cuts.Seam != null:
            {
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
                var (before, after) = MotionCutter.Window(cuts, -1, doc.Length);
                gesturePart = -1;
                // The before side ends the clip; the after side starts it.
                gestureSide = before > 0 && Math.Abs(hit.Frame - (doc.Length - before)) <= Math.Abs(hit.Frame - after) || after <= 0 ? -1 : 1;
                BeginEdit(TimelineGesture.BlendLength, [], [TimelineSnap.Round(gestureSide < 0 ? doc.Length - before : after)], snap);
                break;
            }
            case TimelinePart.EaseEdge when Loops && cuts.Ease > 0:
            {
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Seam, -1));
                var (from, to) = cuts.EaseSpan(doc.Length);
                BeginEdit(TimelineGesture.EaseLength, [], [TimelineSnap.Round(cuts.EaseEnd ? from : to)], snap);
                break;
            }
            case TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight when hit.Cut >= 0:
                SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, hit.Cut));
                gesture = TimelineGesture.Select;
                break;
            case TimelinePart.Join or TimelinePart.JoinEdge when hit.Cut >= 0:
            case TimelinePart.Seam or TimelinePart.SeamEdge or TimelinePart.EaseEdge when Loops:
                ClickBlend(hit);
                gesture = TimelineGesture.Select;
                break;
        }
    }

    /// <summary>
    /// A press on a layer's empty rows, or on its bar with Ctrl or Shift, starts a key box: dragged, it picks the keys and
    /// bone values inside it, added to the layer's picks with Ctrl or Shift. Pressed on a bone row (or the bar) the layer
    /// becomes the selection as a click always made it; on its own row the selection clears unless Ctrl or Shift is held.
    /// </summary>
    private void PressBox(TimelineHit hit, string key, bool adding)
    {
        var keep = adding && SelectedLayer == key;
        boxBefore = (SelectedLayer, [.. keyPicks], SelectedKey is { } frame ? new KeyPick(frame, SelectedBone) : null);
        gesturePicks = keep ? boxBefore.Picks : [];
        if (!keep && hit.Part is TimelinePart.BoneLane or TimelinePart.LayerBody)
            SelectLayer(key);
        else if (!keep && !adding)
            ClearSelection();
        gestureLayer = key;
        boxRow = boxLastRow = hit.Row;
        boxFrame = hit.Frame;
        gesture = TimelineGesture.KeyBox;
    }

    /// <summary>Picks the keys and bone values inside the key box on its layer's rows, after those it began with.</summary>
    private void BoxPicks()
    {
        var doc = Document;
        if (gestureLayer is not { } key || MotionEdits.IndexOf(doc, key) is not (var index and >= 0) || KeyBox is not var (from, to, first, last))
            return;
        var layer = doc.Motion[index];
        var rows = Layout;
        var top = rows.LayerRow(index);
        var picks = new List<KeyPick>(gesturePicks);
        foreach (var item in layer.Keys)
        {
            if (item.Frame < from || item.Frame > to)
                continue;
            if (first == top)
                picks.Add(new KeyPick(item.Frame));
            for (var row = Math.Max(first, top + 1); row <= last; row++)
                if (rows.BoneAt(row) is { } entry && item.Bones.ContainsKey(entry.Bone))
                    picks.Add(new KeyPick(item.Frame, entry.Bone));
        }
        if (SelectedLayer != key)
            SelectLayer(key);
        SetPicks(picks, null);
    }

    /// <summary>
    /// A press on a bone layer: it becomes the selection and the drag that follows moves it, an end, a
    /// fade, a key or one bone's value in a key. A press on a key also moves the playhead there, so the
    /// panel shows that key. On the selected layer Ctrl+click adds a key to the picks or takes it out and
    /// Shift+click picks the row's keys from the selected one to it; a press on a picked key keeps the picks,
    /// so they drag together, and a plain click without a drag narrows them to that key.
    /// </summary>
    private void PressLayer(TimelineHit hit, string key, MotionLayer layer, TimelineDocument doc, bool ctrl, bool shift)
    {
        var at = hit.OnKey ? MotionEdits.KeyIndex(layer, hit.KeyFrame) : -1;
        var onKey = at >= 0 && (hit.Part == TimelinePart.LayerKey || hit.Bone is { } named && layer.Keys[at].Bones.ContainsKey(named));
        var pick = new KeyPick(hit.KeyFrame, hit.Part == TimelinePart.BoneKey ? hit.Bone : null);
        // A bone's value in a whole picked key is picked with it.
        var held = keyPicks.Contains(pick) ? pick : new KeyPick(pick.Frame);
        if (onKey && (ctrl || shift) && SelectedLayer == key)
        {
            if (ctrl)
                TogglePick(pick);
            else
                PickRange(pick);
            gesture = TimelineGesture.Select;
            return;
        }
        if (onKey && SelectedLayer == key && keyPicks.Count > 1 && keyPicks.Contains(held))
        {
            SelectedKey = held.Frame;
            SelectedBone = held.Bone;
            collapseToPick = pick;
        }
        else
            SelectLayer(key, onKey ? hit.KeyFrame : null, onKey && hit.Part == TimelinePart.BoneKey ? hit.Bone : null);
        if (onKey)
            Playhead = hit.KeyFrame;
        var snap = TimelineSnap.Targets(doc, playhead, motion: true, exceptLayer: key);
        gestureLayer = key;
        // A match is grabbed where it is shown, over the clip's last frames.
        layer = MotionEdits.Shown(layer, doc.Length);
        switch (hit.Part)
        {
            case TimelinePart.LayerBody:
                BeginEdit(TimelineGesture.LayerMove, [], [layer.Start, layer.End], snap);
                break;
            case TimelinePart.LayerLeft:
                BeginEdit(TimelineGesture.LayerLeft, [], [layer.Start], snap);
                break;
            case TimelinePart.LayerRight:
                BeginEdit(TimelineGesture.LayerRight, [], [layer.End], snap);
                break;
            case TimelinePart.LayerFadeIn:
                BeginEdit(TimelineGesture.LayerFadeIn, [], [layer.Start + layer.FadeIn], snap);
                break;
            case TimelinePart.LayerFadeOut:
                BeginEdit(TimelineGesture.LayerFadeOut, [], [layer.End - layer.FadeOut], snap);
                break;
            case TimelinePart.LayerKey or TimelinePart.BoneKey when onKey && keyPicks.Count > 1:
                gestureKeyFrame = hit.KeyFrame;
                gestureBone = held.Bone;
                gesturePicks = [.. keyPicks];
                var picked = keyPicks.Select(item => item.Frame).ToHashSet();
                BeginEdit(TimelineGesture.LayerKeys, [], [hit.KeyFrame],
                    [.. snap.Concat(layer.Keys.Select(item => item.Frame).Where(frame => !picked.Contains(frame))).Distinct().Order()]);
                break;
            case TimelinePart.LayerKey or TimelinePart.BoneKey when onKey:
                gestureKeyFrame = hit.KeyFrame;
                gestureBone = hit.Part == TimelinePart.BoneKey ? hit.Bone : null;
                // The layer's other keys are where this one may land next to; a bone's value joins one it lands on.
                BeginEdit(gestureBone == null ? TimelineGesture.LayerKey : TimelineGesture.BoneKey, [], [hit.KeyFrame],
                    [.. snap.Concat(layer.Keys.Select(item => item.Frame).Where(frame => frame != hit.KeyFrame)).Distinct().Order()]);
                break;
            default:
                gesture = TimelineGesture.Select;
                break;
        }
    }

    /// <summary>
    /// The mouse moved to <paramref name="frameAtMouse"/> while held. Edits are computed from the
    /// document at the press plus a whole-frame offset, so a long drag never drifts.
    /// </summary>
    public void Drag(float frameAtMouse, bool alt = false, int row = -1)
    {
        if (gesture is TimelineGesture.None or TimelineGesture.Select || !float.IsFinite(frameAtMouse))
            return;
        dragged = true;
        if (gesture == TimelineGesture.KeyBox)
        {
            // Off the rows the box keeps its last row.
            if (row >= 0)
                boxLastRow = row;
            boxFrame = frameAtMouse;
            BoxPicks();
            return;
        }
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
                next = TimelineEdits.ResizeGroup(start, gestureKeys[0], left, delta);
                var resized = TimelineEdits.Find(next, gestureKeys[0]);
                landed = resized == null ? -1 : left ? resized.Start : TimelineEdits.End(resized);
                break;
            case TimelineGesture.Length:
                next = TimelineEdits.SetLength(start, start.Length + delta, ScaleEvents, loaded);
                landed = next.Length;
                break;
            case TimelineGesture.LayerMove or TimelineGesture.LayerLeft or TimelineGesture.LayerRight or
                TimelineGesture.LayerFadeIn or TimelineGesture.LayerFadeOut or TimelineGesture.LayerKey or TimelineGesture.BoneKey or TimelineGesture.LayerKeys
                when gestureLayer is { } layerKey:
                (next, landed) = DragLayer(start, layerKey, delta);
                break;
            case TimelineGesture.TrimLeft or TimelineGesture.TrimRight:
                next = CutEdits.TrimPart(start, gesturePart, gesture == TimelineGesture.TrimLeft, delta, out var trimmed);
                LastRemoval = trimmed is { Events: 0, Keys: 0 } ? null : trimmed;
                landed = ReferenceEquals(next, start) ? -1 : anchors[0] + delta;
                break;
            case TimelineGesture.Stretch:
                // The part's start stays; its end follows the pointer.
                next = CutEdits.StretchPart(start, gesturePart, delta, out var merged);
                LastRemoval = merged is { Events: 0, Keys: 0 } ? null : merged;
                landed = ReferenceEquals(next, start) ? -1 : anchors[0] + delta;
                break;
            case TimelineGesture.BlendLength:
            {
                var cuts = CutEdits.CutsOf(start);
                var blend = gesturePart < 0 ? cuts.Seam : gesturePart < cuts.Joins.Length ? cuts.Joins[gesturePart] : null;
                if (blend == null)
                    return;
                // A centred blend spreads both ways, so its edge moves half as far as its length grows.
                var frames = blend.Frames + gestureSide * delta * (blend.Placement == JoinPlacement.Centered ? 2 : 1);
                var changed = blend with { Frames = Math.Clamp(frames, 0, MotionJoin.MaxFrames) };
                next = gesturePart < 0 ? CutEdits.SetSeam(start, changed) : CutEdits.SetJoin(start, gesturePart, changed);
                landed = anchors[0] + delta;
                break;
            }
            case TimelineGesture.EaseLength:
            {
                // The end's ease grows to the left, the start's to the right.
                var cuts = CutEdits.CutsOf(start);
                next = CutEdits.SetEase(start, cuts.Ease + (cuts.EaseEnd ? -delta : delta));
                landed = anchors[0] + delta;
                break;
            }
            default:
                return;
        }
        SnapGuide = guide == landed ? guide : null;
        History.Preview(next);
    }

    /// <summary>The document a layer drag of <paramref name="delta"/> frames makes, and the frame the dragged edge, fade or key lands on.</summary>
    private (TimelineDocument Next, int Landed) DragLayer(TimelineDocument start, string layerKey, int delta)
    {
        switch (gesture)
        {
            case TimelineGesture.LayerMove:
            {
                var next = MotionEdits.Move(start, layerKey, delta);
                return (next, MotionEdits.Find(next, layerKey) is { } moved && MotionEdits.Find(start, layerKey) is { } before &&
                    moved.Start - before.Start == delta ? moved.Start : -1);
            }
            case TimelineGesture.LayerLeft or TimelineGesture.LayerRight:
            {
                var left = gesture == TimelineGesture.LayerLeft;
                var next = MotionEdits.Resize(start, layerKey, left, delta);
                // A match or a close loop lands where it is shown, which a centred close loop's stored start isn't.
                return (next, MotionEdits.Find(next, layerKey) is { } resized && MotionEdits.Shown(resized, next.Length) is var shown
                    ? left ? shown.Start : shown.End : -1);
            }
            case TimelineGesture.LayerFadeIn or TimelineGesture.LayerFadeOut:
            {
                var fadeIn = gesture == TimelineGesture.LayerFadeIn;
                var next = MotionEdits.Fade(start, layerKey, fadeIn, delta);
                return (next, MotionEdits.Find(next, layerKey) is { } faded ? fadeIn ? faded.Start + faded.FadeIn : faded.End - faded.FadeOut : -1);
            }
            case TimelineGesture.LayerKeys:
            {
                var next = MotionEdits.MoveKeys(start, layerKey, gesturePicks, delta, out var landed, out var applied);
                SetPicks(landed, new KeyPick(gestureKeyFrame + applied, gestureBone), next);
                return (next, gestureKeyFrame + applied);
            }
            case TimelineGesture.BoneKey when gestureBone is { } bone:
            {
                var next = MotionEdits.MoveBoneKey(start, layerKey, bone, gestureKeyFrame, delta, out var landed);
                PickOnly(landed, bone);
                return (next, landed);
            }
            default:
            {
                var next = MotionEdits.MoveKey(start, layerKey, gestureKeyFrame, delta, out var landed);
                PickOnly(landed, null);
                return (next, landed);
            }
        }
    }

    /// <summary>Whether <paramref name="kind"/> edits the document, so it is one undo step.</summary>
    private static bool Edits(TimelineGesture kind) => kind is not (TimelineGesture.None or TimelineGesture.Select or TimelineGesture.Scrub or TimelineGesture.KeyBox);

    /// <summary>The mouse was released: a drag becomes one undo step; a click without a drag only selects.</summary>
    public void Release()
    {
        if (Edits(gesture))
            History.End();
        if (!dragged && collapseOnClick && pressKey != null && TimelineEdits.Find(Document, pressKey) != null)
            SelectOnly(pressKey);
        if (!dragged && collapseToPick is { } narrowed && SelectedLayer is { } layerKey)
            SelectLayer(layerKey, narrowed.Frame, narrowed.Bone);
        EndGesture();
        ClampView();
    }

    /// <summary>Escape during a drag: back to the document and playhead from before the press.</summary>
    public void Cancel()
    {
        if (Edits(gesture))
            History.Cancel();
        if (gesture is TimelineGesture.LayerKey or TimelineGesture.BoneKey)
            PickOnly(gestureKeyFrame, gestureBone);
        if (gesture == TimelineGesture.LayerKeys)
            SetPicks(gesturePicks, new KeyPick(gestureKeyFrame, gestureBone));
        if (gesture == TimelineGesture.KeyBox && boxBefore.Layer is { } boxLayer)
        {
            if (SelectedLayer != boxLayer)
                SelectLayer(boxLayer);
            SetPicks(boxBefore.Picks, boxBefore.Primary);
        }
        else if (gesture == TimelineGesture.KeyBox && SelectedLayer != null)
            ClearLayerSelection();
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
        LastNotice = null;
        LastRemoval = null;
        switch (key)
        {
            case TimelineKey.Undo: return Undo();
            case TimelineKey.Redo: return Redo();
            case TimelineKey.Delete when SelectedCut is { Target: TimelineCutTarget.Part }: return RemoveSelectedPart();
            case TimelineKey.Delete: return SelectedLayer != null ? DeleteLayerSelection() : DeleteSelection();
            case TimelineKey.Cut: return CutAtPlayhead();
            case TimelineKey.RemoveBefore: return RemoveBeforePlayhead();
            case TimelineKey.RemoveAfter: return RemoveAfterPlayhead();
            case TimelineKey.Blend: return ToggleSelectedBlend();
            case TimelineKey.Duplicate: return SelectedLayer != null ? DuplicateLayer() : DuplicateSelection();
            case TimelineKey.Copy: return SelectedLayer != null ? CopyKeys() : Copy();
            case TimelineKey.Paste: return SelectedLayer != null && CanPasteKeys ? PasteKeys() : Paste();
            case TimelineKey.SelectAll:
                SelectAll();
                return selected.Count > 0;
            case TimelineKey.Left or TimelineKey.Right:
                var step = (shift ? 10 : 1) * (key == TimelineKey.Left ? -1 : 1);
                if (alt && SelectedLayer is { } nudged)
                    return NudgeLayer(nudged, step);
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
                var had = selected.Count > 0 || SelectedLayer != null;
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

    /// <summary>
    /// Makes <paramref name="doc"/> current as one undo step; false when it is the current one. An edit
    /// that took away the last face of the timeline's expression library drops the library too.
    /// </summary>
    public bool Commit(TimelineDocument doc)
    {
        if (gesture != TimelineGesture.None || ReferenceEquals(doc, Document))
            return false;
        History.Commit(TimelineEdits.ReleasePack(Document, doc, ResidentMotions));
        return true;
    }

    /// <summary>Makes <paramref name="doc"/> current within the last undo step (<see cref="TimelineHistory.Amend"/>); false when it is the current one.</summary>
    public bool Amend(TimelineDocument doc)
    {
        if (gesture != TimelineGesture.None || ReferenceEquals(doc, Document))
            return false;
        History.Amend(TimelineEdits.ReleasePack(Document, doc, ResidentMotions));
        return true;
    }

    /// <summary>
    /// Adds events built for an Add and selects them; false with <see cref="LastError"/> when refused.
    /// Events that would start past the end move back together, so an add at the end still plays.
    /// With <paramref name="switchPack"/> (a <see cref="ChoosePack"/> that switches) the timeline takes
    /// <paramref name="pack"/> in place of its expression library in the same undo step, and says so in
    /// <see cref="LastNotice"/>.
    /// </summary>
    public bool Add(IEnumerable<TimelineEvent> events, string? pack, bool switchPack = false)
    {
        LastError = null;
        LastNotice = null;
        var switched = switchPack && !string.IsNullOrEmpty(pack) && !string.Equals(pack, Document.FacePack, StringComparison.OrdinalIgnoreCase);
        var doc = switched ? Document with { FacePack = pack } : Document;
        var added = events.ToList();
        if (added.Count > 0)
        {
            var first = added.Min(ev => ev.Start);
            var shift = Math.Max(-first, Math.Min(0, Math.Min(doc.Length - 1, TimelineDocument.MaxFrames) - added.Max(ev => ev.Start)));
            if (first >= 0 && shift != 0)
                added = added.ConvertAll(ev => ev with { Start = ev.Start + shift });
        }
        var next = TimelineEdits.Add(doc, added, pack, out var created, out var error, ResidentMotions);
        LastError = error;
        // A switched library alone is no add.
        if (created.IsDefaultOrEmpty || !Commit(next))
            return false;
        SelectKeys(created);
        if (switched)
            LastNotice = TimelineEdits.PackSwitched(pack!);
        return true;
    }

    /// <summary>Sets the length as one undo step, scaling the events when <see cref="ScaleEvents"/> is on.</summary>
    public bool SetLength(int frames) => !FixedLength && Commit(TimelineEdits.SetLength(Document, frames, ScaleEvents, loaded));

    /// <summary>Puts a motion from Blender in place of the clip's, as one undo step (see <see cref="TimelineEdits.ReplaceMotion"/>).</summary>
    public bool ReplaceMotion(MotionReplacement replacement) =>
        !FixedLength && Commit(TimelineEdits.ReplaceMotion(Document, replacement, ScaleEvents, loaded));

    /// <summary>
    /// Starts the clip from a still pose, as one undo step: the pose becomes its motion and a pose layer over
    /// the whole clip, selected, takes the keys that build the new motion.
    /// </summary>
    public bool HoldStill(MotionReplacement still)
    {
        if (FixedLength)
            return false;
        LastError = null;
        var held = TimelineEdits.ReplaceMotion(Document, still, ScaleEvents, loaded);
        var next = MotionEdits.Add(held, MotionLayerKind.Pose, 0, Math.Max(TimelineDocument.MinLength, held.Length), out var key, out var error);
        LastError = error;
        if (!Commit(error == null ? next : held))
            return false;
        if (error == null)
            SelectLayer(key);
        return true;
    }

    /// <summary>Goes back to the clip's own motion, as one undo step.</summary>
    public bool DropReplacement() => !FixedLength && Commit(TimelineEdits.DropReplacement(Document, ScaleEvents, loaded));

    // Cuts.

    /// <summary>Cuts the motion at the playhead, as one undo step.</summary>
    public bool CutAtPlayhead() => CutAt(playhead);

    /// <summary>Cuts the motion at <paramref name="frame"/>, as one undo step.</summary>
    public bool CutAt(int frame) => Commit(CutEdits.CutAt(Document, frame));

    /// <summary>Removes from the start of the part under the playhead to the playhead, as one undo step.</summary>
    public bool RemoveBeforePlayhead() => RemoveBefore(playhead);

    /// <summary>Removes from the playhead to the end of the part under it, as one undo step: "end the clip here" on the last part.</summary>
    public bool RemoveAfterPlayhead() => RemoveAfter(playhead);

    /// <summary>Removes from the start of the part under <paramref name="frame"/> to it, as one undo step.</summary>
    public bool RemoveBefore(int frame)
    {
        var cuts = CutEdits.CutsOf(Document);
        var part = cuts.PartAt(frame, Document.Length);
        return RemoveRange(TimelineSnap.Round(cuts.PartStart(part, Document.Length)), frame);
    }

    /// <summary>Removes from <paramref name="frame"/> to the end of the part under it, as one undo step.</summary>
    public bool RemoveAfter(int frame)
    {
        var cuts = CutEdits.CutsOf(Document);
        var part = cuts.PartAt(frame, Document.Length);
        return RemoveRange(frame, TimelineSnap.Round(cuts.PartEnd(part, Document.Length)));
    }

    /// <summary>Keeps only output frames <paramref name="from"/> up to <paramref name="to"/>, as one undo step (see <see cref="CutEdits.KeepRange"/>).</summary>
    public bool KeepOnly(int from, int to)
    {
        if (!Cutting(CutEdits.KeepRange(Document, from, to, out var removed), removed))
            return false;
        Playhead = 0;
        return true;
    }

    /// <summary>Removes the selected part, as one undo step.</summary>
    public bool RemoveSelectedPart()
    {
        if (SelectedCut is not { Target: TimelineCutTarget.Part, Index: var part })
            return false;
        var next = CutEdits.RemovePart(Document, part, out var removed);
        if (!Cutting(next, removed))
            return false;
        SelectedCut = null;
        return true;
    }

    /// <summary>Turns the selected part into a blend part, or a blend part back into its own motion, as one undo step.</summary>
    public bool ToggleSelectedBlend()
    {
        if (SelectedCut is not { Target: TimelineCutTarget.Part, Index: var part })
            return false;
        var cuts = CutEdits.CutsOf(Document);
        return part < cuts.Parts.Length && Commit(CutEdits.SetBlend(Document, part, !cuts.Parts[part].Blend));
    }

    /// <summary>
    /// Makes blend part <paramref name="part"/> <paramref name="frames"/> frames long, moving what follows: live during a field edit,
    /// else one undo step. A field edit sets it from the document the edit began on, so a shorter number typed on the way doesn't
    /// remove the events and keys it passes over.
    /// </summary>
    public bool SetBlendLength(int part, int frames) =>
        Stretching(CutEdits.SetBlendLength(History.GestureStart ?? Document, part, frames, out var removed), removed);

    /// <summary>
    /// Makes part <paramref name="part"/> play over <paramref name="frames"/> output frames, moving what follows: live during a field
    /// edit, else one undo step. A field edit sets it from the document the edit began on, so each keystroke replaces the last
    /// one's stretch instead of stretching its keys and events again.
    /// </summary>
    public bool SetPartLength(int part, int frames) =>
        Stretching(CutEdits.SetPartLength(History.GestureStart ?? Document, part, frames, out var removed), removed);

    /// <summary>Plays part <paramref name="part"/> at normal speed again (stretch exactly 1), moving what follows, as one undo step.</summary>
    public bool ResetPartSpeed(int part) => Stretching(CutEdits.ResetPartSpeed(Document, part, out var removed), removed);

    /// <summary>Applies a part's new speed or blend length (see <see cref="Change"/>) and remembers what it merged or removed.</summary>
    private bool Stretching(TimelineDocument next, CutRemoval removed)
    {
        if (!Change(next))
            return false;
        LastRemoval = removed is { Events: 0, Keys: 0 } ? null : removed;
        return true;
    }

    /// <summary>Removes output frames <paramref name="from"/> up to <paramref name="to"/>, as one undo step.</summary>
    public bool RemoveRange(int from, int to) => Cutting(CutEdits.RemoveRange(Document, from, to, out var removed), removed);

    /// <summary>Sets join <paramref name="index"/>'s blend: live during a field edit, else one undo step.</summary>
    public bool SetJoin(int index, MotionJoin join) => Change(CutEdits.SetJoin(Document, index, join));

    /// <summary>Sets the loop seam's blend, or removes it with null, on a clip that loops: live during a field edit, else one undo step.</summary>
    public bool SetSeam(MotionJoin? seam) => Loops && Change(CutEdits.SetSeam(Document, seam));

    /// <summary>
    /// Sets over how many frames the loop's seam eases (0: none) and, unless null, whether at the end, on a clip
    /// that loops: live during a field edit, else one undo step.
    /// </summary>
    public bool SetEase(int frames, bool? atEnd = null) => Loops && Change(CutEdits.SetEase(Document, frames, atEnd));

    /// <summary>
    /// Fix loop, on a clip that loops, as one undo step. When the cut motion's seam <paramref name="cut"/> isn't clean it
    /// fixes it by the source's <paramref name="raw"/> (see <see cref="CutEdits.FixLoop(TimelineDocument, SeamReport)"/>).
    /// When the seam with the bone layers, <paramref name="edited"/> (null while it is measured), isn't clean and the
    /// layers change the motion, it selects a Close loop layer, turning it on when it is off, or adds one. False when it
    /// changed nothing.
    /// </summary>
    public bool FixLoop(SeamReport raw, SeamReport cut, SeamReport? edited)
    {
        if (!Loops || gesture != TimelineGesture.None)
            return false;
        LastError = null;
        var next = Document;
        CutRemoval? removed = null;
        if (cut.State != SeamState.Clean)
        {
            next = CutEdits.FixLoop(next, raw, out var trimmed);
            removed = trimmed;
        }
        string? close = null;
        if (edited is { State: not SeamState.Clean } && MotionEvaluator.Changes(next.Motion))
        {
            var closes = next.Motion.Where(layer => layer.Fix?.Kind == MotionFixKind.CloseLoop).ToArray();
            if ((closes.FirstOrDefault(layer => layer.Enabled) ?? closes.FirstOrDefault()) is { } found)
                next = MotionEdits.SetEnabled(next, close = found.Key, true);
            else
            {
                var added = MotionEdits.AddFix(next, new MotionFix(MotionFixKind.CloseLoop), out var key, out var error);
                LastError = error;
                if (error == null)
                    (next, close) = (added, key);
            }
        }
        var changed = Commit(next);
        if (changed && removed is { } cutOut)
            LastRemoval = cutOut;
        if (close != null)
            SelectLayer(close);
        return changed;
    }

    /// <summary>
    /// Whether the sound <paramref name="eventKey"/>, which plays across a cut, is cut with the motion
    /// (true), left whole (false) or not decided (null), as one undo step.
    /// </summary>
    public bool SetSoundCut(string eventKey, bool? cut)
    {
        var doc = Document;
        var cuts = doc.SoundCuts;
        if (cuts.TryGetValue(eventKey, out var was) ? was == cut : cut == null)
            return false;
        return Commit(doc with { SoundCuts = cut is { } value ? cuts.SetItem(eventKey, value) : cuts.Remove(eventKey) });
    }

    /// <summary>Selects a part, join or the seam of the cut motion; the event and layer selection clears.</summary>
    public void SelectCut(TimelineCutSelection? cut)
    {
        selected.Clear();
        Primary = null;
        ClearLayerSelection();
        SelectedCut = cut;
    }

    /// <summary>
    /// Selects the join or seam blend that was clicked, or when its body is clicked while it is already
    /// selected, the part under the pointer, so a short part its blends cover can still be picked.
    /// </summary>
    public void ClickBlend(TimelineHit hit)
    {
        var blend = hit.Part is TimelinePart.Join or TimelinePart.JoinEdge
            ? new TimelineCutSelection(TimelineCutTarget.Join, hit.Cut)
            : new TimelineCutSelection(TimelineCutTarget.Seam, -1);
        var doc = Document;
        var under = hit.Part is TimelinePart.Join or TimelinePart.Seam && SelectedCut == blend && hit.Frame >= 0 && hit.Frame <= doc.Length;
        SelectCut(under ? new TimelineCutSelection(TimelineCutTarget.Part, CutEdits.CutsOf(doc).PartAt(hit.Frame, doc.Length)) : blend);
    }

    /// <summary>Commits a cut and remembers what it removed with it.</summary>
    private bool Cutting(TimelineDocument next, CutRemoval removed)
    {
        if (!Commit(next))
            return false;
        LastRemoval = removed;
        return true;
    }

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
        // The last copy is what Ctrl+V pastes, even with a layer selected.
        keyClipboard = null;
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
        // not this document's: those copies are built like new ones, and a pin names that
        // document's alternatives, so they play any.
        var same = ReferenceEquals(clipboardLoaded, loaded);
        return Add(clipboard.Select(ev => same ? ev with { Start = ev.Start + playhead }
            : ev with { Start = ev.Start + playhead, Key = "", CloneOf = null, Pin = null }), clipboardPack);
    }

    // Properties panel and context menu.

    /// <summary>A properties field became active: its changes until <see cref="EndFieldEdit"/> make one undo step.</summary>
    public void BeginFieldEdit()
    {
        // A spread that changed nothing leaves the document as it was, but the picks may have changed since.
        spreadFrom = null;
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
    /// A prop summon's new length takes its parts along (<see cref="TimelineEdits.ResizeGroup"/>), from
    /// where they were when the field edit began, so typing through a short length squeezes nothing;
    /// its new start moves them all. False when nothing changed or a drag is running.
    /// </summary>
    public bool Replace(TimelineEvent ev)
    {
        if (gesture != TimelineGesture.None)
            return false;
        var next = TimelineEdits.Replace(Document, ev, motionFrames);
        if (TimelineEdits.Find(Document, ev.Key) is { } old && TimelineEdits.IsGroupSummon(old) && TimelineEdits.CanResize(old) &&
            ev.Duration != old.Duration && (History.InGesture ? History.GestureStart : Document) is { } from &&
            TimelineEdits.Find(from, ev.Key) is { } began && began.Group == old.Group)
            next = TimelineEdits.ResizeGroup(TimelineEdits.Replace(from, ev with { Duration = began.Duration }, motionFrames), ev.Key, false,
                Math.Max(1, ev.Duration) - began.Duration);
        // A new start moves its parts along, as dragging its left edge does.
        else if (TimelineEdits.Find(Document, ev.Key) is { } moved && TimelineEdits.IsGroupSummon(moved) && ev.Start != moved.Start &&
                 (History.InGesture ? History.GestureStart : Document) is { } origin && TimelineEdits.Find(origin, ev.Key) is { } first &&
                 first.Group == moved.Group)
            next = TimelineEdits.Move(TimelineEdits.Replace(origin, ev with { Start = first.Start }, motionFrames), GroupOf(origin, ev.Key),
                ev.Start - first.Start);
        if (ReferenceEquals(next, Document))
            return false;
        next = TimelineEdits.ReleasePack(Document, next, ResidentMotions);
        if (History.InGesture)
            History.Preview(next);
        else
            History.Commit(next);
        return true;
    }

    /// <summary>
    /// The expression library a face from <paramref name="packs"/> comes from here (see
    /// <see cref="TimelineEdits.ChoosePack"/>); <paramref name="except"/> is the event it would replace.
    /// </summary>
    public TimelinePackChoice ChoosePack(ImmutableArray<string> packs, string? except = null) =>
        TimelineEdits.ChoosePack(Document, packs, ResidentMotions, MotionPacks, except);

    /// <summary>
    /// Gives a face event another motion, from <paramref name="choice"/>'s library: a library the
    /// timeline doesn't load yet comes with it in the same undo step, and a switch says so in
    /// <see cref="LastNotice"/>. False when refused, nothing changed or a drag is running.
    /// </summary>
    public bool ReplaceFace(TimelineEvent ev, TimelinePackChoice choice)
    {
        LastNotice = null;
        if (choice.Refused)
            return false;
        if (choice.Pack is not { } pack || string.Equals(pack, Document.FacePack, StringComparison.OrdinalIgnoreCase))
            return Replace(ev);
        if (!Commit(TimelineEdits.Replace(Document with { FacePack = pack }, ev, motionFrames)))
            return false;
        if (choice.Switches)
            LastNotice = TimelineEdits.PackSwitched(pack);
        return true;
    }

    /// <summary>
    /// Sets or drops the changes to the sound <paramref name="key"/> (see <see cref="TimelineEdits.SetSound"/>):
    /// live during a field edit, else one undo step. False when nothing changed or a drag is running.
    /// </summary>
    public bool SetSound(TimelineSoundKey key, TimelineSoundEdit? edit)
    {
        if (gesture != TimelineGesture.None)
            return false;
        var next = TimelineEdits.SetSound(Document, key, edit);
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
        else if (gesture == TimelineGesture.None && hit.OnKey && IsPicked(hit.Layer!, hit.KeyFrame, hit.Part == TimelinePart.BoneKey ? hit.Bone : null))
            (SelectedKey, SelectedBone) = (hit.KeyFrame, hit.Part == TimelinePart.BoneKey ? hit.Bone : null);
        else if (gesture == TimelineGesture.None && hit.OnKey && IsPicked(hit.Layer!, hit.KeyFrame))
            (SelectedKey, SelectedBone) = (hit.KeyFrame, null);
        else if (gesture == TimelineGesture.None && hit.OnLayer)
            SelectLayer(hit.Layer!, hit.OnKey ? hit.KeyFrame : null, hit.Part == TimelinePart.BoneKey ? hit.Bone : null);
        else if (gesture == TimelineGesture.None && hit.Part is TimelinePart.BoneLane or TimelinePart.BoneMarker or TimelinePart.Legend && hit.Layer != null)
            SelectLayer(hit.Layer);
        else if (gesture == TimelineGesture.None && hit.Part is TimelinePart.ClipPart or TimelinePart.ClipLeft or TimelinePart.ClipRight && hit.Cut >= 0)
            SelectCut(new TimelineCutSelection(TimelineCutTarget.Part, hit.Cut));
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

    /// <summary>
    /// Alt+arrows on a selected layer: moves its selected key (or the selected bone's value in it), else
    /// the whole layer, as one undo step.
    /// </summary>
    private bool NudgeLayer(string key, int step)
    {
        if (SelectedKey is not { } frame)
            return Commit(MotionEdits.Move(Document, key, step));
        // The commit drops a pick whose frame moved, so the bone is kept from before it.
        var selectedBone = SelectedBone;
        if (keyPicks.Count > 1)
        {
            var moved = MotionEdits.MoveKeys(Document, key, keyPicks, step, out var picks, out var applied);
            if (!Commit(moved))
                return false;
            SetPicks(picks, new KeyPick(frame + applied, selectedBone));
            return true;
        }
        int landed;
        var next = selectedBone is { } bone
            ? MotionEdits.MoveBoneKey(Document, key, bone, frame, step, out landed)
            : MotionEdits.MoveKey(Document, key, frame, step, out landed);
        if (!Commit(next))
            return false;
        PickOnly(landed, selectedBone);
        return true;
    }

    // Bone layers.

    /// <summary>
    /// Makes <paramref name="next"/> current, as a field edit's live change while one is running, else
    /// as one undo step; false when nothing changed or a drag is running. For the layer panels.
    /// </summary>
    public bool Change(TimelineDocument next)
    {
        if (gesture != TimelineGesture.None || ReferenceEquals(next, Document))
            return false;
        if (History.InGesture)
            History.Preview(next);
        else
            History.Commit(next);
        return true;
    }

    /// <summary>Adds a bone layer of <paramref name="kind"/> from <paramref name="start"/> to <paramref name="end"/> and selects it.</summary>
    public bool AddLayer(MotionLayerKind kind, int start, int end)
    {
        LastError = null;
        var next = MotionEdits.Add(Document, kind, start, end, out var key, out var error);
        LastError = error;
        if (!Commit(next))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>Make symmetrical: adds a symmetry layer over the whole clip for <paramref name="bones"/> and selects it.</summary>
    public bool AddSymmetry(IEnumerable<string> bones, MotionSide from)
    {
        LastError = null;
        var next = MotionEdits.AddSymmetry(Document, bones, from, out var key, out var error);
        LastError = error;
        if (error != null || !Commit(next))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>
    /// Pin feet: adds a pin layer over the whole clip planting both feet, and selects it. Refused with a
    /// reason on a <paramref name="skeleton"/> without both feet, which the layer couldn't apply to.
    /// </summary>
    public bool AddPinFeet(SkeletonDescription? skeleton = null)
    {
        LastError = null;
        if (skeleton != null && !TimelineWindowViews.CanPinFeet(skeleton))
        {
            LastError = TimelineWindowViews.NoFeet;
            return false;
        }
        var next = MotionEdits.AddPinFeet(Document, out var key, out var error);
        LastError = error;
        if (error != null || !Commit(next))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>
    /// Edit motion keys: adds the Motion keys layer unless there is one (one undo step, none when it is reused, muted or
    /// not), then moves the playhead to <paramref name="frame"/>, selects the layer and shows its bone rows.
    /// </summary>
    public bool EditMotionKeys(int frame)
    {
        LastError = null;
        var next = MotionEdits.AddMotionKeys(Document, out var key, out var error);
        LastError = error;
        if (error != null)
            return false;
        if (!ReferenceEquals(next, Document) && !Commit(next))
            return false;
        Playhead = frame;
        SelectLayer(key);
        SetUnfolded(key, true);
        return true;
    }

    /// <summary>
    /// <paramref name="next"/> with the zero keys a write into the Motion keys layer reaches out to
    /// (<see cref="MotionEdits.WithReach"/>), compared with the document the running gesture began from, so a drag adds
    /// them once; <paramref name="next"/> itself without <see cref="Markers"/>. Zero keys an earlier preview of the same
    /// gesture added, and the next one still holds at zero, count as there before: they never reach out themselves. The
    /// limit: a write of exactly zero where the reach put a zero key earlier in the same gesture doesn't reach out either.
    /// </summary>
    public TimelineDocument WithReach(TimelineDocument next)
    {
        if (Markers is not { } markers || MotionEdits.MotionKeysLayer(next) is not { } layer)
            return next;
        if (!History.InGesture || reached is not { } known || known.Gesture != History.GestureSerial)
            reached = known = (History.GestureSerial, []);
        var before = History.Base;
        foreach (var (frame, bone) in known.Keys)
            if (BoneAt(layer, frame, bone) is { } value && value == ZeroKey)
                before = MotionEdits.SetKey(before, layer.Key, frame, bone, value);
        var result = MotionEdits.WithReach(before, next, markers);
        if (!ReferenceEquals(result, next) && MotionEdits.MotionKeysLayer(result) is { } added)
            foreach (var key in added.Keys)
                foreach (var bone in key.Bones.Keys)
                    if (BoneAt(layer, key.Frame, bone) == null)
                        known.Keys.Add((key.Frame, bone));
        return result;
    }

    /// <summary>
    /// <paramref name="next"/> with the zero keys a write into the Motion keys layer reaches out to, compared with
    /// <paramref name="from"/>, the document the write was made on; for a write that replaces an earlier one (the pose
    /// browser's trial picks), not the document shown. <paramref name="next"/> itself without <see cref="Markers"/>.
    /// </summary>
    public TimelineDocument WithReach(TimelineDocument from, TimelineDocument next) =>
        Markers is { } markers ? MotionEdits.WithReach(from, next, markers) : next;

    private static readonly BoneTransform ZeroKey = MotionTransform.Identity.ToBone();

    private static BoneTransform? BoneAt(MotionLayer layer, int frame, string bone) =>
        MotionEdits.KeyIndex(layer, frame) is var at and >= 0 && layer.Keys[at].Bones.TryGetValue(bone, out var value) ? value : null;

    /// <summary>Adds a fix layer holding <paramref name="fix"/> (<see cref="MotionEdits.AddFix"/>) as one undo step, and selects it.</summary>
    public bool AddFix(MotionFix fix)
    {
        LastError = null;
        var next = MotionEdits.AddFix(Document, fix, out var key, out var error);
        LastError = error;
        if (error != null || !Commit(next))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>
    /// Blend over: sets how many frames the match <paramref name="key"/> blends over, as a field edit's live change
    /// while one runs, else as one undo step, and selects it.
    /// </summary>
    public bool SetFixLength(string key, int frames)
    {
        if (!Change(MotionEdits.SetFixLength(Document, key, frames)))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>Sets where the close loop <paramref name="key"/> blends (<see cref="MotionEdits.SetFixPlacement"/>) as one undo step, and selects it.</summary>
    public bool SetFixPlacement(string key, JoinPlacement place)
    {
        if (!Commit(MotionEdits.SetFixPlacement(Document, key, place)))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>Re-read loop: the match <paramref name="key"/> lands on <paramref name="loopPose"/>'s loop pose, as one undo step, and is selected.</summary>
    public bool SetLoopPose(string key, MotionFix loopPose)
    {
        if (!Commit(MotionEdits.SetLoopPose(Document, key, loopPose)))
            return false;
        SelectLayer(key);
        return true;
    }

    /// <summary>Says why something done off the draw, such as reading a loop, changed nothing, in <see cref="LastError"/>.</summary>
    public void Refuse(string error) => LastError = error;

    /// <summary>Says what an edit made from a panel left out, in <see cref="LastNotice"/>.</summary>
    public void Note(string notice) => LastNotice = notice;

    /// <summary>
    /// Pin selected: pins <paramref name="bones"/> in the selected pin layer, or with none selected in a new
    /// pin layer over the whole clip, in one undo step, and selects the layer.
    /// </summary>
    public bool PinSelected(IEnumerable<string> bones)
    {
        LastError = null;
        var picked = bones.Where(bone => !string.IsNullOrEmpty(bone)).ToArray();
        if (picked.Length == 0)
        {
            LastError = "Select a bone to pin.";
            return false;
        }
        var next = Document;
        var key = SelectedLayer;
        if (MotionEdits.Find(next, key) is not { Kind: MotionLayerKind.Pin })
        {
            next = MotionEdits.Add(next, MotionLayerKind.Pin, 0, Math.Max(TimelineDocument.MinLength, next.Length), out var added, out var error);
            LastError = error;
            if (error != null)
                return false;
            key = added;
        }
        if (!Commit(MotionEdits.PinBones(next, key!, picked)))
            return false;
        SelectLayer(key!);
        return true;
    }

    /// <summary>
    /// Selects a bone layer, with <paramref name="keyFrame"/> its key there, and with <paramref name="bone"/>
    /// that bone's value in the key alone; the event selection clears.
    /// </summary>
    public void SelectLayer(string key, int? keyFrame = null, string? bone = null)
    {
        if (MotionEdits.Find(Document, key) is not { } layer)
            return;
        selected.Clear();
        Primary = null;
        SelectedCut = null;
        SelectedLayer = key;
        var at = keyFrame is { } frame ? MotionEdits.KeyIndex(layer, frame) : -1;
        SelectedKey = at >= 0 ? keyFrame : null;
        SelectedBone = at >= 0 && bone != null && layer.Keys[at].Bones.ContainsKey(bone) ? bone : null;
        keyPicks.Clear();
        picked = null;
        if (SelectedKey is { } first)
            keyPicks.Add(new KeyPick(first, SelectedBone));
    }

    /// <summary>The selected layer's key at <paramref name="frame"/> (with <paramref name="bone"/>, that bone's value) as the only pick.</summary>
    private void PickOnly(int frame, string? bone)
    {
        SelectedKey = frame;
        SelectedBone = bone;
        keyPicks.Clear();
        picked = null;
        keyPicks.Add(new KeyPick(frame, bone));
    }

    /// <summary>
    /// Picks <paramref name="picks"/> in the selected layer as it is in <paramref name="doc"/> (the current document when
    /// null), <paramref name="primary"/> as the selected key while it is picked, else the first.
    /// </summary>
    private void SetPicks(IEnumerable<KeyPick> picks, KeyPick? primary, TimelineDocument? doc = null)
    {
        var kept = MotionEdits.Find(doc ?? Document, SelectedLayer) is { } layer ? MotionEdits.Picks(layer, picks) : [];
        keyPicks.Clear();
        picked = null;
        keyPicks.UnionWith(kept);
        KeyPick? main = primary is { } wanted && keyPicks.Contains(wanted) ? wanted : kept.IsEmpty ? null : kept[0];
        SelectedKey = main?.Frame;
        SelectedBone = main?.Bone;
    }

    private void TogglePick(KeyPick pick)
    {
        var picks = keyPicks.ToList();
        if (picks.Remove(pick))
            SetPicks(picks, SelectedKey is { } frame && new KeyPick(frame, SelectedBone) != pick ? new KeyPick(frame, SelectedBone) : null);
        else
            SetPicks([.. picks, pick], pick);
    }

    /// <summary>Picks the row's keys from the selected key's frame to <paramref name="pick"/>'s: whole keys on the layer's row, that bone's values on a bone's.</summary>
    private void PickRange(KeyPick pick)
    {
        if (SelectedLayerValue is not { } layer)
            return;
        var primary = SelectedKey is { } frame ? new KeyPick(frame, SelectedBone) : pick;
        var (low, high) = (Math.Min(primary.Frame, pick.Frame), Math.Max(primary.Frame, pick.Frame));
        var picks = keyPicks.ToList();
        foreach (var item in layer.Keys)
            if (item.Frame >= low && item.Frame <= high && (pick.Bone == null || item.Bones.ContainsKey(pick.Bone)))
                picks.Add(new KeyPick(item.Frame, pick.Bone));
        SetPicks(picks, primary);
    }

    // Static so keys carry to another animation, as copied layers do; an event copy clears them and they clear it, so Ctrl+V pastes the last copy.
    private static KeyCopy? keyClipboard;

    public static bool CanPasteKeys => keyClipboard != null;

    /// <summary>Keeps the picked keys and bone values for <see cref="PasteKeys"/>.</summary>
    public bool CopyKeys()
    {
        if (SelectedLayer is not { } key || MotionEdits.CopyKeys(Document, key, keyPicks) is not { } copy)
            return false;
        keyClipboard = copy;
        clipboard = [];
        return true;
    }

    /// <summary>Keeps <paramref name="copy"/> for <see cref="PasteKeys"/> in place of the last copied keys, as Copy what you see does.</summary>
    public void CopyKeys(KeyCopy copy)
    {
        keyClipboard = copy;
        clipboard = [];
    }

    /// <summary>Keys the copied keys from the playhead on in the selected layer, which must be of the kind they came from, and picks them.</summary>
    public bool PasteKeys()
    {
        LastError = null;
        if (SelectedLayerValue is not { } layer || keyClipboard is not { } copy)
            return false;
        if (layer.Kind != copy.Kind)
        {
            LastError = $"These keys came from a {MotionEdits.KindName(copy.Kind)} layer: paste them into one.";
            return false;
        }
        var next = MotionEdits.PasteKeys(Document, layer.Key, playhead, copy, out var pasted);
        if (!Commit(next))
            return false;
        SetPicks(pasted, pasted.IsEmpty ? null : pasted[0]);
        return true;
    }

    // The document and picks a field edit of Spread over began with.
    private TimelineDocument? spreadFrom;
    private ImmutableArray<KeyPick> spreadPicks = [];
    private KeyPick? spreadPrimary;

    /// <summary>
    /// Spreads the picked keys over <paramref name="frames"/> frames from the first (see <see cref="MotionEdits.ScaleKeys"/>):
    /// live during a field edit, else one undo step; refused with <see cref="LastError"/> when keys would collide. A field
    /// edit spreads the keys it began with, so each number typed replaces the last one's spread.
    /// </summary>
    public bool SpreadKeys(int frames)
    {
        LastError = null;
        if (SelectedLayer is not { } key)
            return false;
        var from = History.GestureStart ?? Document;
        if (!History.InGesture || !ReferenceEquals(from, spreadFrom))
        {
            spreadFrom = from;
            spreadPicks = MotionEdits.Find(from, key) is { } layer ? MotionEdits.Picks(layer, keyPicks) : [];
            spreadPrimary = SelectedKey is { } frame ? new KeyPick(frame, SelectedBone) : null;
        }
        var next = MotionEdits.ScaleKeys(from, key, spreadPicks, frames, out var landed, out var error);
        LastError = error;
        if (error != null || !Change(next))
            return false;
        var at = spreadPrimary is { } primary ? spreadPicks.IndexOf(primary) : -1;
        SetPicks(landed, at >= 0 ? landed[at] : null);
        return true;
    }

    /// <summary>Deletes the picked keys and bone values, else the selected layer.</summary>
    public bool DeleteLayerSelection()
    {
        if (SelectedLayer is not { } key)
            return false;
        if (keyPicks.Count > 1)
            return Commit(MotionEdits.DeleteKeys(Document, key, keyPicks));
        if (SelectedKey is not { } frame)
            return Commit(MotionEdits.Remove(Document, key));
        return SelectedBone is { } bone
            ? Commit(MotionEdits.SetKey(Document, key, frame, bone, null))
            : Commit(MotionEdits.DeleteKey(Document, key, frame));
    }

    /// <summary>Copies the selected layer right after itself and selects the copy.</summary>
    public bool DuplicateLayer()
    {
        if (SelectedLayer is not { } key || !Commit(MotionEdits.Duplicate(Document, key, out var created)))
            return false;
        SelectLayer(created);
        return true;
    }

    // Static so a layer setup carries to the next animation the window (or another one) opens.
    private static (ImmutableArray<MotionLayer> Layers, int Length) layerClipboard = ([], 0);

    public static bool CanPasteLayers => !layerClipboard.Layers.IsEmpty;

    /// <summary>Keeps the layer <paramref name="key"/>, or every layer when null, for <see cref="PasteLayers"/>.</summary>
    public bool CopyLayers(string? key = null)
    {
        ImmutableArray<MotionLayer> layers = key == null ? Document.Motion : MotionEdits.Find(Document, key) is { } layer ? [layer] : [];
        if (layers.IsEmpty)
            return false;
        layerClipboard = (layers, Document.Length);
        return true;
    }

    /// <summary>Adds the copied layers after this animation's own and selects the first.</summary>
    public bool PasteLayers()
    {
        LastError = null;
        var next = MotionEdits.Paste(Document, layerClipboard.Layers, layerClipboard.Length, out var created, out var error);
        LastError = error;
        if (!Commit(next))
            return false;
        SelectLayer(created[0]);
        return true;
    }

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
        ClearLayerSelection();
        foreach (var ev in Events(Document))
            selected.Add(ev.Key);
        if (Primary == null || !selected.Contains(Primary))
            Primary = Events(Document).OrderBy(ev => ev.Start).Select(ev => ev.Key).FirstOrDefault();
    }

    /// <summary>Clears the selection of events and of bone layers.</summary>
    public void ClearSelection()
    {
        selected.Clear();
        Primary = null;
        ClearLayerSelection();
    }

    /// <summary>Clears the bone layer selection, and the cut's: either gives way to an event selection.</summary>
    private void ClearLayerSelection()
    {
        SelectedLayer = null;
        SelectedKey = null;
        SelectedBone = null;
        keyPicks.Clear();
        picked = null;
        SelectedCut = null;
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
        gestureLayer = null;
        gestureBone = null;
        gesturePicks = [];
        boxBefore = (null, [], null);
        collapseToPick = null;
        boxFrame = float.NaN;
    }

    private void Scrub(float frame, bool alt)
    {
        var snapped = TimelineSnap.Snap(frame, targets, SnapTolerance, View.Scale, Snap && !alt, out var guide);
        Playhead = snapped;
        SnapGuide = guide == playhead ? guide : null;
    }

    private void SelectOnly(string key)
    {
        ClearLayerSelection();
        selected.Clear();
        selected.UnionWith(GroupOf(Document, key));
        Primary = key;
    }

    private void Toggle(string key)
    {
        ClearLayerSelection();
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
        ClearLayerSelection();
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
        ClearLayerSelection();
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
        if (SelectedLayer != null)
        {
            if (MotionEdits.Find(doc, SelectedLayer) is not { } layer)
                ClearLayerSelection();
            else if (keyPicks.Count > 0 && gesture is not (TimelineGesture.LayerKey or TimelineGesture.BoneKey or TimelineGesture.LayerKeys))
                SetPicks([.. keyPicks], SelectedKey is { } frame ? new KeyPick(frame, SelectedBone) : null);
        }
        // Parts and joins are numbered in order, so a cut that comes or goes before the one selected (an undo, say) gives its number to
        // another: then it stays selected only while its number still starts at the same frame (within half a frame: a length rounds).
        // An edit that keeps every cut (a stretch, a blend length, or its undo) keeps the selection wherever the part moved to.
        if (SelectedCut is { Target: not TimelineCutTarget.Seam } cut)
        {
            var cuts = CutEdits.CutsOf(doc);
            var samePlace = CutPlace(cut) is { } at && selectedCutAt is { } was && Math.Abs(at - was) < .5;
            SelectedCut = samePlace || (cuts.Parts.Length, cuts.Joins.Length) == selectedCutCounts ? cut : null;
        }
        if (folded.RemoveWhere(key => MotionEdits.Find(doc, key) == null) > 0)
            layoutStamp++;
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
