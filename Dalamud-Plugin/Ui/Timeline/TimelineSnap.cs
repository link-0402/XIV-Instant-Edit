using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Snapping for drags. Results are always whole frames, because TMB times are whole frames; the
/// magnet additionally pulls to nearby frames that matter. No ImGui or Dalamud dependencies.
/// </summary>
internal static class TimelineSnap
{
    /// <summary>
    /// The frames the magnet pulls to: 0, the length, the playhead and the starts and ends of every
    /// event not in <paramref name="exclude"/> (the dragged ones), but not the stored end of a
    /// motion at its own speed, which the game ignores. With <paramref name="motion"/> also every bone
    /// layer's start, end and keys but <paramref name="exceptLayer"/>'s. Sorted and distinct.
    /// </summary>
    public static int[] Targets(TimelineDocument doc, int? playhead, IReadOnlySet<string>? exclude = null,
        bool length = true, bool sourceLength = false, bool events = true, bool motion = false, string? exceptLayer = null)
    {
        var targets = new List<int> { 0 };
        if (length)
            targets.Add(doc.Length);
        if (sourceLength)
            targets.Add(doc.SourceLength);
        if (playhead is { } frame)
            targets.Add(frame);
        if (events && !doc.Events.IsDefault)
            foreach (var ev in doc.Events)
            {
                if (exclude?.Contains(ev.Key) == true)
                    continue;
                targets.Add(ev.Start);
                if (ev.Duration > 0 && !TimelineDocument.OwnPace(ev))
                    targets.Add(TimelineEdits.End(ev));
            }
        if (motion)
            targets.AddRange(MotionEdits.SnapFrames(doc, exceptLayer));
        targets.Sort();
        var distinct = new List<int>(targets.Count);
        foreach (var target in targets)
            if (distinct.Count == 0 || distinct[^1] != target)
                distinct.Add(target);
        return [.. distinct];
    }

    /// <summary>
    /// <paramref name="frame"/> as a whole frame, or the nearest target within
    /// <paramref name="tolerancePixels"/> when the magnet is on. <paramref name="guide"/> is the target it stuck to.
    /// </summary>
    public static int Snap(float frame, ReadOnlySpan<int> targets, float tolerancePixels, float pixelsPerFrame, bool magnet, out int? guide) =>
        SnapDelta(frame, [0], targets, tolerancePixels, pixelsPerFrame, magnet, out guide);

    /// <summary>
    /// A whole-frame offset for a drag of <paramref name="delta"/> frames. With the magnet on, the
    /// offset that brings any of <paramref name="anchors"/> (the dragged starts and ends as they were
    /// when the drag began) onto the closest target within <paramref name="tolerancePixels"/> wins.
    /// </summary>
    public static int SnapDelta(float delta, ReadOnlySpan<int> anchors, ReadOnlySpan<int> targets, float tolerancePixels,
        float pixelsPerFrame, bool magnet, out int? guide)
    {
        guide = null;
        if (!float.IsFinite(delta))
            return 0;
        var rounded = Round(delta);
        if (!magnet || !(pixelsPerFrame > 0) || !(tolerancePixels > 0))
            return rounded;
        var best = float.MaxValue;
        var result = rounded;
        foreach (var anchor in anchors)
            foreach (var target in targets)
            {
                var distance = MathF.Abs(anchor + delta - target) * pixelsPerFrame;
                if (distance <= tolerancePixels && distance < best)
                {
                    best = distance;
                    result = target - anchor;
                    guide = target;
                }
            }
        return result;
    }

    /// <summary>Rounds half away from zero, the same way length scaling rounds.</summary>
    public static int Round(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue / 2, int.MaxValue / 2) : 0;
}
