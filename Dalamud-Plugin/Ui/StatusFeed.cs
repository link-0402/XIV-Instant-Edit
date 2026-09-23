namespace InstantEdit.Ui;

/// <summary> Severity of a status message or banner. </summary>
internal enum FeedbackSeverity
{
    Success,
    Warning,
    Error,
    Info,
}

/// <summary> Which workflow a status message belongs to; each tab shows its own latest message. </summary>
internal enum StatusChannel
{
    Models,
    Textures,
    Animations,
}

internal sealed record StatusEntry(DateTimeOffset Time, StatusChannel Channel, FeedbackSeverity Severity, string Text, string? Detail = null);

/// <summary>
/// Thread-safe status history: the latest message per channel drives the status strip and a
/// bounded ring of recent messages feeds the history popover. Reporting an empty text clears
/// the channel without adding history.
/// </summary>
internal sealed class StatusFeed
{
    private readonly object _lock = new();
    private readonly StatusEntry[] _ring;
    private readonly Dictionary<StatusChannel, StatusEntry> _latest = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _next;
    private int _count;

    public StatusFeed(int capacity = 50, Func<DateTimeOffset>? clock = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new StatusEntry[capacity];
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary> Raised after an entry is recorded, outside the lock. Used for notifications. </summary>
    public event Action<StatusEntry>? Reported;

    public int Capacity => _ring.Length;

    public void Report(StatusChannel channel, FeedbackSeverity severity, string text, string? detail = null)
    {
        StatusEntry? entry = null;
        lock (_lock)
        {
            if (string.IsNullOrEmpty(text))
            {
                _latest.Remove(channel);
            }
            else
            {
                entry = new StatusEntry(_clock(), channel, severity, text, detail);
                _latest[channel] = entry;
                _ring[_next] = entry;
                _next = (_next + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }
        }

        if (entry is not null)
            Reported?.Invoke(entry);
    }

    public void Clear(StatusChannel channel) => Report(channel, FeedbackSeverity.Success, string.Empty);

    public StatusEntry? Latest(StatusChannel channel)
    {
        lock (_lock)
            return _latest.TryGetValue(channel, out var entry) ? entry : null;
    }

    /// <summary> The most recent entries, newest first. </summary>
    public IReadOnlyList<StatusEntry> History(int count = int.MaxValue)
    {
        lock (_lock)
        {
            var take = Math.Min(count, _count);
            var result = new StatusEntry[take];
            for (var i = 0; i < take; i++)
                result[i] = _ring[(_next - 1 - i + _ring.Length * 2) % _ring.Length];
            return result;
        }
    }
}
