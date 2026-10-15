using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Reads a clip's timeline on a task, after the preview's temporary mod is gone, and hands it over
/// once it is read: a fresh read, a read again after a save or undo that keeps the view, Edit again,
/// or a read after an undo that may bring back the layers of the edit it went back to.
/// </summary>
internal sealed class TimelineReader(TimelineSession session) : IDisposable
{
    private Task<AnimationTimelineSource>? task;
    private CancellationTokenSource? cancel;
    private bool keepsView;
    // The saved edit a read brings back with its bone layers (Edit again), until it ends.
    private AnimationEditJournal? again;
    // The read follows an undo and may bring back the layers of the edit it went back to.
    private bool afterUndo;

    /// <summary>A read runs.</summary>
    public bool Busy => task != null;

    /// <summary>The clip being read, until the read ends.</summary>
    public (AnimationCapture Capture, bool Startup)? Reading { get; private set; }

    /// <summary>Why the last read failed.</summary>
    public string? Error { get; private set; }

    /// <summary>The failed read was an Edit again.</summary>
    public bool ErrorAgain { get; private set; }

    /// <summary>
    /// Reads <paramref name="capture"/>'s clip, or its startup, once <paramref name="ending"/> (the preview's
    /// temporary mod going) is done, or the read would get the previewed file. A read still running stops.
    /// </summary>
    public void Start(AnimationEditService service, AnimationCapture capture, bool startup, bool keepView, AnimationEditJournal? editAgain,
        bool undone, Task ending)
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        Reading = (capture, startup);
        again = editAgain;
        afterUndo = undone;
        keepsView = keepView;
        Error = null;
        var preview = session.Preview;
        task = Task.Run(async () =>
        {
            await ending;
            await preview.StopAsync();
            return editAgain != null ? await service.LoadTimelineAgainAsync(editAgain, capture, token)
                : undone ? await service.LoadTimelineAfterUndoAsync(capture, startup, token)
                : await service.LoadTimelineAsync(capture, startup, token);
        }, token);
    }

    /// <summary>Every frame: the clip once it is read, with how it was asked for; null until then, and when it failed.</summary>
    public (AnimationTimelineSource Loaded, bool KeepView, AnimationEditJournal? Again, bool AfterUndo)? Poll()
    {
        if (task is not { IsCompleted: true } done)
            return null;
        task = null;
        Reading = null;
        var editAgain = again;
        again = null;
        var undone = afterUndo;
        afterUndo = false;
        if (done.IsCanceled)
            return null;
        if (done.IsFaulted)
        {
            Error = TimelineSession.ErrorText(done.Exception);
            ErrorAgain = editAgain != null;
            return null;
        }
        return (done.Result, keepsView, editAgain, undone);
    }

    /// <summary>Stops the read and forgets it, when the window closes.</summary>
    public void Stop()
    {
        cancel?.Cancel();
        task = null;
        Reading = null;
        again = null;
        afterUndo = false;
        Error = null;
    }

    public void Dispose()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
    }
}
