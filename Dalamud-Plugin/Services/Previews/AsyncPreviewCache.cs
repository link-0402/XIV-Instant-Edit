using System.Collections.Concurrent;

namespace InstantEdit.Services.Previews;

/// <summary>
/// Identity of a cached preview: the file (or game path) plus its size and write time, so a
/// texture overwritten in place by a texture edit session gets a new key and a fresh load.
/// Game data uses zero size and time.
/// </summary>
public readonly record struct PreviewKey(string Path, bool IsGameData, long Size, long MtimeTicks);

public enum PreviewState
{
    Loading,
    Ready,
    Failed,
}

/// <summary> One cache slot. <see cref="Value"/> is set before <see cref="State"/> becomes Ready. </summary>
public sealed class PreviewEntry<T> where T : class
{
    public PreviewState State { get; internal set; } = PreviewState.Loading;
    public T? Value { get; internal set; }
    public string? Error { get; internal set; }
    public long Bytes { get; internal set; }
    internal long LastUse;
    internal CancellationTokenSource? Cancellation;
}

/// <summary>
/// A bounded, least-recently-used cache of asynchronously loaded previews. <see cref="Get"/> is
/// called from the draw thread and never blocks: a missing key starts a background load and
/// returns a Loading entry. Failed loads stay cached (without retrying) until the key changes or
/// the path is invalidated. Values evicted or invalidated from other threads are queued and
/// disposed on the next <see cref="Pump"/> or <see cref="Get"/>, so GPU resources are released on
/// the caller's thread.
/// </summary>
public sealed class AsyncPreviewCache<T> : IDisposable where T : class
{
    private readonly Func<PreviewKey, CancellationToken, Task<T>> _load;
    private readonly Func<T, long> _sizeOf;
    private readonly Action<T> _dispose;
    private readonly int _maxEntries;
    private readonly long _byteBudget;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lock = new();
    private readonly Dictionary<PreviewKey, PreviewEntry<T>> _entries = new();
    private readonly ConcurrentQueue<T> _disposals = new();
    private long _bytes;
    private long _tick;
    private bool _disposed;

    public AsyncPreviewCache(
        Func<PreviewKey, CancellationToken, Task<T>> load,
        Func<T, long> sizeOf,
        Action<T> dispose,
        int maxEntries = 24,
        long byteBudget = 64L << 20,
        int maxConcurrent = 2)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxConcurrent < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        _load = load;
        _sizeOf = sizeOf;
        _dispose = dispose;
        _maxEntries = maxEntries;
        _byteBudget = byteBudget;
        _concurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public long Bytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary> The entry for the key, starting a load when it is not cached. Draw thread only. </summary>
    public PreviewEntry<T> Get(PreviewKey key)
    {
        Pump();
        lock (_lock)
        {
            if (_disposed)
                return new PreviewEntry<T> { State = PreviewState.Failed, Error = "The preview cache is closed." };
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastUse = ++_tick;
                return existing;
            }

            var entry = new PreviewEntry<T> { LastUse = ++_tick, Cancellation = new CancellationTokenSource() };
            _entries[key] = entry;
            EvictLocked();
            _ = LoadAsync(key, entry, entry.Cancellation.Token);
            return entry;
        }
    }

    public bool TryPeek(PreviewKey key, out PreviewEntry<T>? entry)
    {
        lock (_lock)
            return _entries.TryGetValue(key, out entry);
    }

    /// <summary> Drops every entry for the path, whatever its size or write time. Safe from any thread. </summary>
    public int Invalidate(string path)
    {
        lock (_lock)
        {
            var victims = _entries.Where(pair => string.Equals(pair.Key.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var (key, entry) in victims)
                RemoveLocked(key, entry);
            return victims.Length;
        }
    }

    /// <summary> Disposes values that were evicted or invalidated since the last call. Call from the draw thread. </summary>
    public void Pump()
    {
        while (_disposals.TryDequeue(out var value))
        {
            try
            {
                _dispose(value);
            }
            catch
            {
                // A wrap that fails to release must not take the frame down.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var (key, entry) in _entries.ToArray())
                RemoveLocked(key, entry);
            _entries.Clear();
        }
        Pump();
    }

    private async Task LoadAsync(PreviewKey key, PreviewEntry<T> entry, CancellationToken token)
    {
        T? value = null;
        string? error = null;
        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            try
            {
                value = await _load(key, token).ConfigureAwait(false);
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException)
        {
            error = "The preview load was cancelled.";
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        lock (_lock)
        {
            var current = _entries.TryGetValue(key, out var live) && ReferenceEquals(live, entry);
            if (_disposed || !current || token.IsCancellationRequested)
            {
                if (value is not null)
                    _disposals.Enqueue(value);
                return;
            }

            if (value is null)
            {
                entry.Error = error ?? "The preview could not be loaded.";
                entry.State = PreviewState.Failed;
                return;
            }

            entry.Value = value;
            entry.Bytes = Math.Max(0, _sizeOf(value));
            _bytes += entry.Bytes;
            entry.State = PreviewState.Ready;
            EvictLocked();
        }
    }

    private void EvictLocked()
    {
        while (_entries.Count > _maxEntries || _bytes > _byteBudget)
        {
            PreviewKey victimKey = default;
            PreviewEntry<T>? victim = null;
            var oldest = long.MaxValue;
            foreach (var (key, entry) in _entries)
            {
                // Never evict the entry touched most recently: it is the one being drawn.
                if (entry.LastUse == _tick || entry.LastUse >= oldest)
                    continue;
                oldest = entry.LastUse;
                victim = entry;
                victimKey = key;
            }

            if (victim is null)
                return;
            RemoveLocked(victimKey, victim);
        }
    }

    private void RemoveLocked(PreviewKey key, PreviewEntry<T> entry)
    {
        _entries.Remove(key);
        try
        {
            entry.Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (entry.State == PreviewState.Ready && entry.Value is { } value)
        {
            _bytes -= entry.Bytes;
            entry.Value = null;
            _disposals.Enqueue(value);
        }
        entry.State = PreviewState.Failed;
        entry.Error = "The preview was evicted.";
    }
}
