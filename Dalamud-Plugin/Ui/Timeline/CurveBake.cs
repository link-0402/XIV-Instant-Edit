using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The motion with every bone layer applied over the whole clip, for the curve view's "after layers" curve: baked on a
/// task once per output motion and layers (by reference, which every edit of them replaces), never during a gesture,
/// as <see cref="EditedSeam"/> measures the seam. A newer pair cancels the bake of an older one; the last bake of the same
/// output motion stands while the next one builds.
/// </summary>
internal sealed class CurveBake
{
    // Guards the fields below: the draw thread asks while a task publishes.
    private readonly object gate = new();
    private (MotionClip Output, ImmutableArray<MotionLayer> Layers)? building;
    private CancellationTokenSource? cancel;
    private (MotionClip Output, ImmutableArray<MotionLayer> Layers, MotionClip Baked)? finished;
    // The pair whose bake failed last; it isn't baked again.
    private (MotionClip Output, ImmutableArray<MotionLayer> Layers)? failedPair;

    /// <summary>Runs a bake off the draw thread; tests run it in place or hold it.</summary>
    internal Func<Action, Task> Start = Task.Run;

    /// <summary>
    /// <paramref name="output"/> with <paramref name="layers"/> applied, when baked for exactly these; <paramref name="output"/>
    /// itself when no layer changes the motion; else null, with <paramref name="stale"/> the last bake of the same output
    /// motion (other layers), and <paramref name="failed"/> when the layers can't apply to it. A new pair starts its bake
    /// unless in a <paramref name="gesture"/>.
    /// </summary>
    public MotionClip? After(MotionClip output, ImmutableArray<MotionLayer> layers, bool gesture, out MotionClip? stale, out bool failed)
    {
        stale = null;
        failed = false;
        if (!MotionEvaluator.Changes(layers))
            return output;
        CancellationToken token;
        lock (gate)
        {
            if (Same(finished, output, layers))
                return finished!.Value.Baked;
            stale = finished is { } last && ReferenceEquals(last.Output, output) ? last.Baked : null;
            if (failedPair is { } broken && ReferenceEquals(broken.Output, output) && broken.Layers == layers)
            {
                failed = true;
                return null;
            }
            if (building is { } now && ReferenceEquals(now.Output, output) && now.Layers == layers)
                return null;
            // A bake for another pair is no longer wanted. The source isn't disposed: the task may still read its token.
            cancel?.Cancel();
            (cancel, building) = (null, null);
            if (gesture)
                return null;
            cancel = new CancellationTokenSource();
            building = (output, layers);
            token = cancel.Token;
        }
        Start(() => Build(output, layers, token));
        lock (gate)
        {
            failed = failedPair is { } broken && ReferenceEquals(broken.Output, output) && broken.Layers == layers;
            return Same(finished, output, layers) ? finished!.Value.Baked : null;
        }
    }

    /// <summary>Drops the bakes and any bake running, for another clip.</summary>
    public void Forget()
    {
        lock (gate)
        {
            cancel?.Cancel();
            (cancel, building, finished, failedPair) = (null, null, null, null);
        }
    }

    private static bool Same((MotionClip Output, ImmutableArray<MotionLayer> Layers, MotionClip Baked)? done, MotionClip output, ImmutableArray<MotionLayer> layers) =>
        done is { } value && ReferenceEquals(value.Output, output) && value.Layers == layers;

    /// <summary>Bakes and publishes unless cancelled. Nothing escapes it: a bake that fails marks its pair failed.</summary>
    private void Build(MotionClip output, ImmutableArray<MotionLayer> layers, CancellationToken token)
    {
        MotionClip baked;
        try
        {
            baked = output.Clone();
            MotionEvaluator.Apply(baked, layers, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        // Layers that can't apply (Live says why): the pair is failed, and the view shows the motion before them.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (gate)
            {
                if (token.IsCancellationRequested || building is not { } now || !ReferenceEquals(now.Output, output) || now.Layers != layers)
                    return;
                failedPair = (output, layers);
                (cancel, building) = (null, null);
            }
            return;
        }
        lock (gate)
        {
            if (token.IsCancellationRequested || building is not { } now || !ReferenceEquals(now.Output, output) || now.Layers != layers)
                return;
            finished = (output, layers, baked);
            (cancel, building) = (null, null);
        }
    }
}
