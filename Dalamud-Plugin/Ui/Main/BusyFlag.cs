namespace InstantEdit.Ui.Main;

/// <summary> A thread-safe "one at a time" flag: a background task enters it, and others wait or are refused until it exits. </summary>
internal sealed class BusyFlag
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary> Sets the flag; false when it was already set. </summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}
