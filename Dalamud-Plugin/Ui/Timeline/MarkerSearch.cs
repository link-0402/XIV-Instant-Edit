using System.Runtime.CompilerServices;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The motion's key markers (<see cref="MotionKeyMarkers.Find"/>), searched on a task once per decode, cuts, length and
/// detail, never during a gesture, since a long clip on a big skeleton takes longer than a frame. The layers don't
/// matter: the markers are on the motion before them, so editing a layer keeps them. A newer key cancels the search for
/// an older one, and a set is given only for the key it was found for; the last one stays available to show.
/// </summary>
internal sealed class MarkerSearch
{
    // Guards the fields below: the draw thread asks for markers while a task publishes them.
    private readonly object gate = new();
    // The key being searched and its cancellation; null when no search runs.
    private string? building;
    private CancellationTokenSource? cancel;
    // The last set a search finished, the key it was found for and that key's decode.
    private (string Key, string Decode, MotionMarkerSet Set)? finished;
    // The key whose search failed last; it isn't searched again.
    private string? failedKey;

    /// <summary>Runs a search off the draw thread; tests run it in place or hold it.</summary>
    internal Func<Action, Task> Start = Task.Run;

    /// <summary>The cancellation of the search started last, for tests.</summary>
    internal CancellationToken Token { get; private set; }

    /// <summary>
    /// The last set a search finished when it was found for <paramref name="decodeKey"/>, whatever cuts, length and
    /// detail: the markers to show while newer ones are searched. Null before the first, or when it was for another decode.
    /// </summary>
    public MotionMarkerSet? Last(string decodeKey)
    {
        lock (gate)
            return finished is { } done && done.Decode == decodeKey ? done.Set : null;
    }

    /// <summary>
    /// The markers of <paramref name="output"/> (the decode's motion as <paramref name="doc"/>'s cuts and length play it)
    /// at <paramref name="detail"/>, when they were found for exactly this key; null while they are searched, during a
    /// <paramref name="gesture"/> that changed the key, or when the search for this key failed, which isn't tried again.
    /// A new key cancels a search for an older one and, unless in a gesture, starts its own.
    /// </summary>
    public MotionMarkerSet? Find(string decodeKey, MotionClip output, TimelineDocument doc, MarkerDetail detail, bool gesture)
    {
        var key = KeyOf(decodeKey, doc, detail);
        CancellationToken token;
        lock (gate)
        {
            if (finished is { } done && done.Key == key)
                return done.Set;
            if (failedKey == key || building == key)
                return null;
            // A search for another key is no longer wanted. The source isn't disposed: the task may still read its token.
            cancel?.Cancel();
            (cancel, building) = (null, null);
            if (gesture)
                return null;
            cancel = new CancellationTokenSource();
            building = key;
            Token = token = cancel.Token;
        }
        var length = doc.Length;
        Start(() => Build(key, decodeKey, output, length, detail, token));
        lock (gate)
            return finished is { } now && now.Key == key ? now.Set : null;
    }

    /// <summary>
    /// What a set is found for: the decode, by reference <paramref name="doc"/>'s cuts, which every edit of them replaces,
    /// its length and the detail. Not the layers.
    /// </summary>
    public static string KeyOf(string decodeKey, TimelineDocument doc, MarkerDetail detail) =>
        $"{decodeKey}|{RuntimeHelpers.GetHashCode(doc.Cuts)}|{doc.Length}|{detail}";

    /// <summary>
    /// Searches for <paramref name="key"/> (of <paramref name="decodeKey"/>) and publishes the set unless the search was
    /// cancelled. Nothing escapes it: a search that fails marks its key failed, which isn't searched again until it changes.
    /// </summary>
    private void Build(string key, string decodeKey, MotionClip output, int length, MarkerDetail detail, CancellationToken token)
    {
        MotionMarkerSet set;
        try
        {
            set = MotionKeyMarkers.Find(output, length, detail, token);
        }
        // Cancelled: a newer key took over, so there is nothing to publish.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        // Nothing is logged from this thread: the key is failed, and edits go through without zero keys for it.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (gate)
            {
                if (token.IsCancellationRequested || building != key)
                    return;
                failedKey = key;
                (cancel, building) = (null, null);
            }
            return;
        }
        lock (gate)
        {
            if (token.IsCancellationRequested || building != key)
                return;
            finished = (key, decodeKey, set);
            (cancel, building) = (null, null);
        }
    }
}
