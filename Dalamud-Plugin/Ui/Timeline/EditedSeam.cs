using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The loop seam as the motion plays with its bone layers: the cut motion with the layers applied, measured on a task
/// once per decode, cuts, length and layers, never during a gesture, since pin layers run IK and a long clip takes
/// longer than a frame. A newer key cancels the measure for an older one, and a report is kept only for the key it was
/// measured for.
/// </summary>
internal sealed class EditedSeam
{
    // Guards the fields below: the draw thread asks for reports while a task publishes one.
    private readonly object gate = new();
    // The key being measured and its cancellation; null when no measure runs.
    private string? building;
    private CancellationTokenSource? cancel;
    // The last report a measure finished, the key it was measured for and that key's decode.
    private (string Key, string Decode, SeamReport Report)? finished;
    // The key whose measure failed last; it isn't measured again.
    private string? failedKey;

    /// <summary>Runs a measure off the draw thread; tests run it in place or hold it.</summary>
    internal Func<Action, Task> Start = Task.Run;

    /// <summary>The cancellation of the measure started last, for tests.</summary>
    internal CancellationToken Token { get; private set; }

    /// <summary>
    /// The last report a measure finished when it was measured for <paramref name="decodeKey"/>, whatever cuts, length and
    /// layers; null before the first, or when it was for another decode.
    /// </summary>
    public SeamReport? Last(string decodeKey)
    {
        lock (gate)
            return finished is { } done && done.Decode == decodeKey ? done.Report : null;
    }

    /// <summary>
    /// The seam of <paramref name="cut"/> (the decode's motion as <paramref name="doc"/>'s cuts and length play it) with
    /// <paramref name="doc"/>'s layers, when it is measured for exactly this key; null while it is measured, or when its
    /// measure <paramref name="failed"/>, which isn't tried again for that key. A new key cancels a measure for an older
    /// one and, unless in a <paramref name="gesture"/>, starts its own.
    /// </summary>
    public SeamReport? Report(string decodeKey, MotionClip cut, TimelineDocument doc, bool gesture, out bool failed)
    {
        var key = KeyOf(decodeKey, doc);
        failed = false;
        CancellationToken token;
        lock (gate)
        {
            if (finished is { } done && done.Key == key)
                return done.Report;
            if (failedKey == key)
            {
                failed = true;
                return null;
            }
            if (building == key)
                return null;
            // A measure for another key is no longer wanted. The source isn't disposed: the task may still read its token.
            cancel?.Cancel();
            (cancel, building) = (null, null);
            if (gesture)
                return null;
            cancel = new CancellationTokenSource();
            building = key;
            Token = token = cancel.Token;
        }
        var (layers, length) = (doc.Motion, doc.Length);
        Start(() => Build(key, decodeKey, cut, layers, length, token));
        lock (gate)
        {
            failed = failedKey == key;
            return finished is { } now && now.Key == key ? now.Report : null;
        }
    }

    /// <summary>
    /// What a report is measured for: the decode, and by reference <paramref name="doc"/>'s cuts and layers, which every
    /// edit of them replaces, and its length.
    /// </summary>
    public static string KeyOf(string decodeKey, TimelineDocument doc) =>
        $"{decodeKey}|{RuntimeHelpers.GetHashCode(doc.Cuts)}|{doc.Length}|{RuntimeHelpers.GetHashCode(ImmutableCollectionsMarshal.AsArray(doc.Motion))}";

    /// <summary>
    /// The seam of a copy of <paramref name="cut"/> with <paramref name="layers"/> applied, over its first
    /// <paramref name="length"/> frames. Throws when cancelled.
    /// </summary>
    public static SeamReport Measure(MotionClip cut, ImmutableArray<MotionLayer> layers, int length, CancellationToken token)
    {
        var edited = cut.Clone();
        MotionEvaluator.Apply(edited, layers, token);
        token.ThrowIfCancellationRequested();
        return LoopSeam.Analyze(edited, 0, length);
    }

    /// <summary>
    /// Measures for <paramref name="key"/> (of <paramref name="decodeKey"/>) and publishes the report unless the measure
    /// was cancelled. Nothing escapes it: a measure that fails marks its key failed, which isn't measured again until it changes.
    /// </summary>
    private void Build(string key, string decodeKey, MotionClip cut, ImmutableArray<MotionLayer> layers, int length, CancellationToken token)
    {
        SeamReport report;
        try
        {
            report = Measure(cut, layers, length, token);
        }
        // Cancelled: a newer key took over, so there is nothing to publish.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        // Layers that can't apply (Live says why, so nothing is logged from this thread): the key is failed, and the seam
        // shows the cut motion's for it.
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
            finished = (key, decodeKey, report);
            (cancel, building) = (null, null);
        }
    }
}
