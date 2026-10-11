using System;
using System.IO;
using System.Threading;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// Watches the open mod's folder for changes made by anything — Penumbra's editor saving a material,
/// a file copied in by hand, or an update from here — so the open mod is read again when its
/// files change, rather than only when asked. Events arrive on the thread pool; they only raise
/// flags, which the render thread takes once the folder has been quiet for a moment.
///
/// When the watcher cannot keep up (its buffer overflowed), stops, or cannot run at all, everything
/// is taken as changed, and <see cref="Failed"/> tells the caller to look on a timer instead.
/// </summary>
internal sealed class ModFolderWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private int _metaChanged;
    private int _filesChanged;
    private long _lastEventTicks;
    private bool _cannotWatch;
    private int _stopped;

    /// <summary>
    /// Whether the folder cannot be watched, or the watcher stopped (a network share dropped, the
    /// folder was replaced), so changes have to be looked for on a timer, and <see cref="Watch"/> tried again.
    /// </summary>
    public bool Failed => _cannotWatch || Volatile.Read(ref _stopped) != 0;

    public void Watch(string folder)
    {
        bool wasFailed = Failed;
        Stop();
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                InternalBufferSize    = 64 * 1024,
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error   += OnError;
            Volatile.Write(ref _watcher, watcher);
            watcher.EnableRaisingEvents = true;
            _cannotWatch = false;
        }
        catch (Exception ex)
        {
            // Tried again on the caller's timer; said once.
            if (!wasFailed)
                StudioServices.Log.Debug(ex, "[Studio] Cannot watch {0}; looking for changes on a timer instead", folder);
            Volatile.Write(ref _watcher, null);
            watcher?.Dispose();
            _cannotWatch = true;
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        Volatile.Write(ref _watcher, null);
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        Interlocked.Exchange(ref _metaChanged, 0);
        Interlocked.Exchange(ref _filesChanged, 0);
        Interlocked.Exchange(ref _stopped, 0);
    }

    /// <summary>
    /// What changed, once the folder has been quiet for <paramref name="quiet"/>: meta.json, and any
    /// other file. False while nothing changed, or changes are still coming in.
    /// </summary>
    public bool TakeChanges(TimeSpan quiet, out bool meta, out bool files)
    {
        meta = files = false;
        if (Volatile.Read(ref _metaChanged) == 0 && Volatile.Read(ref _filesChanged) == 0)
            return false;
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastEventTicks) < quiet.Ticks)
            return false;

        meta  = Interlocked.Exchange(ref _metaChanged, 0) != 0;
        files = Interlocked.Exchange(ref _filesChanged, 0) != 0;
        return meta || files;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Note(e.Name);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Note(e.OldName);
        Note(e.Name);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _watcher)))
            return;   // a watcher already let go of
        NoteError(e.GetException());
    }

    /// <summary>
    /// The watcher reported an error. After a buffer overflow it keeps running, but missed some
    /// changes; after any other error it has stopped, so <see cref="Failed"/> turns on and the
    /// caller looks on a timer and starts it again (on its own thread). Either way, everything is taken as changed.
    /// </summary>
    internal void NoteError(Exception? error)
    {
        if (error is not InternalBufferOverflowException)
            Interlocked.Exchange(ref _stopped, 1);
        Interlocked.Exchange(ref _metaChanged, 1);
        Interlocked.Exchange(ref _filesChanged, 1);
        Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
    }

    private void Note(string? name)
    {
        if (string.IsNullOrEmpty(name) || Ignored(name))
            return;

        if (string.Equals(name, PenumbraModMeta.FileName, StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref _metaChanged, 1);
        else
            Interlocked.Exchange(ref _filesChanged, 1);
        Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>This plugin's own bookkeeping, and files only ever half-written on their way into place.</summary>
    private static bool Ignored(string name)
        => name.StartsWith(Models.Project.StudioProject.FileName, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".instant-edit-tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(PenumbraModMeta.BackupSuffix, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Stop();
}
