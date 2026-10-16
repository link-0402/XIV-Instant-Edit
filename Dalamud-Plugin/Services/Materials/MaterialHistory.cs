namespace InstantEdit.Services.Materials;

/// <summary>
/// Undo and redo of a material by whole-document snapshots, as the Timeline's history does. A
/// gesture (a slider drag) shows many documents and becomes one undo step when it ends. No ImGui
/// or Dalamud dependencies.
/// </summary>
internal sealed class MaterialHistory
{
    public const int DefaultCapacity = 100;

    private readonly LinkedList<(MaterialDocument Doc, string Label)> undo = new();
    private readonly Stack<(MaterialDocument Doc, string Label)> redo = new();
    private readonly int capacity;
    private MaterialDocument? gestureStart;
    private string gestureLabel = string.Empty;

    public MaterialHistory(MaterialDocument initial, int capacity = DefaultCapacity)
    {
        this.capacity = Math.Max(1, capacity);
        Current = Saved = initial;
    }

    public MaterialDocument Current { get; private set; }

    /// <summary>The document as it was last saved or opened.</summary>
    public MaterialDocument Saved { get; private set; }

    public bool Dirty => !ReferenceEquals(Current, Saved);

    public bool InGesture => gestureStart != null;

    public bool CanUndo => undo.Count > 0 && gestureStart == null;
    public bool CanRedo => redo.Count > 0 && gestureStart == null;

    /// <summary>What the next undo takes back, or empty.</summary>
    public string UndoLabel => undo.Last?.Value.Label ?? string.Empty;
    public string RedoLabel => redo.TryPeek(out var next) ? next.Label : string.Empty;

    /// <summary>Makes <paramref name="next"/> current: one undo step, or part of the running gesture.</summary>
    public void Push(MaterialDocument next, string label)
    {
        if (ReferenceEquals(next, Current))
            return;
        if (gestureStart != null)
            gestureLabel = label;
        else
            AddUndo(Current, label);
        Current = next;
    }

    public void BeginGesture()
    {
        if (gestureStart != null)
            return;
        gestureStart = Current;
        gestureLabel = string.Empty;
    }

    /// <summary>Ends the gesture as one step, or none if it ended where it began.</summary>
    public void EndGesture()
    {
        if (gestureStart is not { } start)
            return;
        gestureStart = null;
        if (!ReferenceEquals(start, Current))
            AddUndo(start, gestureLabel);
    }

    public bool Undo()
    {
        EndGesture();
        if (undo.Last is not { } last)
            return false;
        undo.RemoveLast();
        redo.Push((Current, last.Value.Label));
        Current = last.Value.Doc;
        return true;
    }

    public bool Redo()
    {
        EndGesture();
        if (!redo.TryPop(out var next))
            return false;
        undo.AddLast((Current, next.Label));
        Trim();
        Current = next.Doc;
        return true;
    }

    public void MarkSaved() => Saved = Current;

    /// <summary>Starts over from <paramref name="doc"/>, saved, with no steps.</summary>
    public void Reset(MaterialDocument doc)
    {
        gestureStart = null;
        undo.Clear();
        redo.Clear();
        Current = Saved = doc;
    }

    private void AddUndo(MaterialDocument doc, string label)
    {
        undo.AddLast((doc, label));
        Trim();
        redo.Clear();
    }

    private void Trim()
    {
        while (undo.Count > capacity)
            undo.RemoveFirst();
    }
}
