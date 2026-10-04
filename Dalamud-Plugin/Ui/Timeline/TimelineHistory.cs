using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Undo and redo by whole-document snapshots, which is cheap because documents are small and
/// immutable. A gesture (a drag, typing into a field) previews many documents live and becomes one
/// undo step when it ends. No ImGui or Dalamud dependencies.
/// </summary>
internal sealed class TimelineHistory
{
    public const int DefaultCapacity = 100;

    private readonly LinkedList<TimelineDocument> undo = new();
    private readonly Stack<TimelineDocument> redo = new();
    private readonly int capacity;
    private TimelineDocument? gesture;

    public TimelineHistory(TimelineDocument initial, int capacity = DefaultCapacity)
    {
        this.capacity = Math.Max(1, capacity);
        Current = Saved = initial;
    }

    public TimelineDocument Current { get; private set; }

    /// <summary>The document as it was last saved or loaded.</summary>
    public TimelineDocument Saved { get; private set; }

    /// <summary>Unsaved changes. By reference: records holding ImmutableArray compare arrays by reference anyway.</summary>
    public bool Dirty => !ReferenceEquals(Current, Saved);

    public bool InGesture => gesture != null;

    /// <summary>The document when the running gesture began.</summary>
    public TimelineDocument? GestureStart => gesture;

    public bool CanUndo => undo.Count > 0 && gesture == null;
    public bool CanRedo => redo.Count > 0 && gesture == null;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    /// <summary>Raised after <see cref="Current"/> changes for any reason.</summary>
    public event Action? Changed;

    /// <summary>Makes <paramref name="next"/> current as one undo step; a no-op edit (the same instance) adds none.</summary>
    public void Commit(TimelineDocument next)
    {
        End();
        if (ReferenceEquals(next, Current))
            return;
        Push(Current);
        Set(next);
    }

    /// <summary>Starts a gesture; nothing is recorded until it ends.</summary>
    public void Begin() => gesture ??= Current;

    /// <summary>Shows <paramref name="next"/> while the gesture runs, starting one if needed.</summary>
    public void Preview(TimelineDocument next)
    {
        Begin();
        Set(next);
    }

    /// <summary>Ends the gesture as one undo step, or none if it ended where it began.</summary>
    public void End()
    {
        if (gesture is not { } start)
            return;
        gesture = null;
        if (!ReferenceEquals(start, Current))
            Push(start);
    }

    /// <summary>Drops the gesture and goes back to where it began.</summary>
    public void Cancel()
    {
        if (gesture is not { } start)
            return;
        gesture = null;
        Set(start);
    }

    public bool Undo()
    {
        End();
        if (undo.Last is not { } last)
            return false;
        undo.RemoveLast();
        redo.Push(Current);
        Set(last.Value);
        return true;
    }

    public bool Redo()
    {
        End();
        if (!redo.TryPop(out var next))
            return false;
        undo.AddLast(Current);
        Trim();
        Set(next);
        return true;
    }

    public void MarkSaved() => Saved = Current;

    /// <summary>Starts over with <paramref name="doc"/> as both current and saved, forgetting all steps.</summary>
    public void Reset(TimelineDocument doc)
    {
        gesture = null;
        undo.Clear();
        redo.Clear();
        Saved = doc;
        Set(doc);
    }

    private void Push(TimelineDocument doc)
    {
        undo.AddLast(doc);
        Trim();
        redo.Clear();
    }

    private void Trim()
    {
        while (undo.Count > capacity)
            undo.RemoveFirst();
    }

    private void Set(TimelineDocument doc)
    {
        if (ReferenceEquals(doc, Current))
            return;
        Current = doc;
        Changed?.Invoke();
    }
}
