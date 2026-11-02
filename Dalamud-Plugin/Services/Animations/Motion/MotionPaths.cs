using System.Numerics;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// The overlay's motion paths: the frames around the playhead a selected bone's path runs through, and where the
/// selected layer keys it. Pure, so it is tested without ImGui; <see cref="MotionPathCache"/> holds the evaluated frames.
/// </summary>
internal static class MotionPaths
{
    public const int MinRange = 5, MaxRange = 60, DefaultRange = 15;

    /// <summary>How many selected bones get a path; the rest go without.</summary>
    public const int MaxBones = 8;

    /// <summary>A range of frames either side of the playhead the Bones window allows.</summary>
    public static int Range(int range) => Math.Clamp(range, MinRange, MaxRange);

    /// <summary>
    /// The frames a path runs through in time order, from <paramref name="range"/> before <paramref name="playhead"/> to as
    /// many after it, and the playhead's place among them. They stop at 0 and <paramref name="length"/> (the playhead's
    /// own bounds); with <paramref name="loop"/> frames above 0 they wrap round the loop instead, each frame once, its last
    /// frame being its first.
    /// </summary>
    public static (int[] Frames, int At) Window(int playhead, int range, int length, int loop)
    {
        range = Math.Max(0, range);
        if (loop <= 0)
        {
            var from = Math.Max(0, playhead - range);
            var to = Math.Min(Math.Max(0, length), playhead + range);
            return ([.. Enumerable.Range(from, Math.Max(1, to - from + 1))], Math.Clamp(playhead - from, 0, Math.Max(0, to - from)));
        }
        // A range wider than the loop would show frames twice: cut to the loop, the frames before the playhead first.
        var before = Math.Min(range, (loop - 1) / 2);
        var after = Math.Min(range, loop - 1 - before);
        var frames = new int[before + after + 1];
        for (var d = -before; d <= after; d++)
            frames[d + before] = ((playhead + d) % loop + loop) % loop;
        return (frames, before);
    }

    /// <summary>
    /// The frames where <paramref name="layer"/> keys <paramref name="bone"/>, which the path marks with a bigger dot; round
    /// a loop of <paramref name="loop"/> frames a key on its last frame marks its first. None without a layer.
    /// </summary>
    public static HashSet<int> KeyFrames(MotionLayer? layer, string bone, int loop)
    {
        var frames = new HashSet<int>();
        // The kinds the Bones window never shows as keyed (BonePosing.StateOf) mark no frame either.
        if (layer == null || layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle or
                MotionLayerKind.Swing or MotionLayerKind.Smooth or MotionLayerKind.Noise or MotionLayerKind.LookAt or MotionLayerKind.Link or MotionLayerKind.Repeat or
                MotionLayerKind.Spring)
            return frames;
        foreach (var key in layer.Keys)
            if (key.Bones.ContainsKey(bone))
                frames.Add(loop > 0 ? (key.Frame % loop + loop) % loop : key.Frame);
        return frames;
    }
}

/// <summary>
/// Every bone's model-space position at the frames of a path, for one document and clip, evaluated off the draw: the
/// draw shows the frames it wants (<see cref="Show"/>), a worker takes the missing ones nearest the playhead
/// (<see cref="TryNext"/>) and puts each back (<see cref="Put"/>). The frames depend on neither the bones nor the range nor
/// the playhead, so a new selection or a moved playhead reuses them; an edit (a new document) evaluates them again, and
/// the last document's frames show meanwhile so a gizmo drag doesn't blank the path. Safe across threads.
/// </summary>
internal sealed class MotionPathCache
{
    /// <summary>A frame to evaluate, with the document and clip it is for and what the caller passed along (<see cref="Show"/>).</summary>
    public readonly record struct Job(object Doc, object Clip, object? State, int Frame);

    // The store keeps this many windows of frames before letting go of the ones outside the window.
    private const int KeptWindows = 4;

    private readonly object gate = new();
    private object? doc, clip, state;
    private Dictionary<int, Vector3[]> frames = [];
    // The last document's frames, shown where this one's are missing; only for the same clip.
    private Dictionary<int, Vector3[]> fallback = [];
    private int[] wanted = [];
    private int at;
    private bool failed;

    /// <summary>How many frames the current document has evaluated.</summary>
    public int Count
    {
        get { lock (gate) return frames.Count; }
    }

    /// <summary>
    /// The draw's frames this time: <paramref name="window"/> with the playhead at <paramref name="playhead"/>, for
    /// <paramref name="document"/> on <paramref name="motion"/>; <paramref name="pass"/> goes to the worker with each job.
    /// </summary>
    public void Show(object document, object motion, object? pass, int[] window, int playhead)
    {
        lock (gate)
        {
            if (!ReferenceEquals(doc, document) || !ReferenceEquals(clip, motion))
            {
                if (ReferenceEquals(clip, motion))
                {
                    // The frames already evaluated cover the older ones where they have them.
                    var keep = new Dictionary<int, Vector3[]>();
                    foreach (var frame in window)
                        if (frames.TryGetValue(frame, out var shown) || fallback.TryGetValue(frame, out shown))
                            keep[frame] = shown;
                    fallback = keep;
                }
                else
                    fallback = [];
                frames = [];
                doc = document;
                clip = motion;
                failed = false;
            }
            state = pass;
            wanted = window;
            at = Math.Clamp(playhead, 0, Math.Max(0, window.Length - 1));
            if (frames.Count > KeptWindows * Math.Max(1, window.Length))
            {
                var inside = window.ToHashSet();
                foreach (var frame in frames.Keys.Where(frame => !inside.Contains(frame)).ToList())
                    frames.Remove(frame);
            }
        }
    }

    /// <summary>
    /// Evaluates nothing more for <paramref name="document"/> on <paramref name="motion"/>, a frame of which failed; a
    /// failure of a document the draw has moved on from changes nothing.
    /// </summary>
    public void Fail(object document, object motion)
    {
        lock (gate)
            if (ReferenceEquals(doc, document) && ReferenceEquals(clip, motion))
                failed = true;
    }

    /// <summary>Wants no frames: the paths are hidden.</summary>
    public void Hide()
    {
        lock (gate)
            wanted = [];
    }

    /// <summary>The missing frame nearest the playhead, the playhead's own first, then one before and one after in turn.</summary>
    public bool TryNext(out Job job)
    {
        lock (gate)
        {
            if (doc != null && clip != null && !failed)
                for (var step = 0; step <= wanted.Length; step++)
                {
                    if (Missing(at - step) is { } before)
                    {
                        job = new Job(doc, clip, state, before);
                        return true;
                    }
                    if (Missing(at + step) is { } after)
                    {
                        job = new Job(doc, clip, state, after);
                        return true;
                    }
                }
            job = default;
            return false;
        }
    }

    private int? Missing(int slot) => slot >= 0 && slot < wanted.Length && !frames.ContainsKey(wanted[slot]) ? wanted[slot] : null;

    /// <summary>The bones' positions at each of <paramref name="window"/>'s frames into <paramref name="into"/>, as <see cref="At"/>, under one lock for the draw.</summary>
    public void Read(int[] window, Vector3[]?[] into)
    {
        lock (gate)
            for (var i = 0; i < window.Length && i < into.Length; i++)
                into[i] = frames.TryGetValue(window[i], out var positions) || fallback.TryGetValue(window[i], out positions) ? positions : null;
    }

    /// <summary>Keeps the positions evaluated at <paramref name="frame"/>, unless the document or the clip moved on meanwhile.</summary>
    public void Put(object document, object motion, int frame, Vector3[] positions)
    {
        lock (gate)
            if (ReferenceEquals(doc, document) && ReferenceEquals(clip, motion))
                frames[frame] = positions;
    }

    /// <summary>The bones' positions at <paramref name="frame"/>, from the current document or else the last one's; null while neither has it.</summary>
    public Vector3[]? At(int frame)
    {
        lock (gate)
            return frames.TryGetValue(frame, out var positions) || fallback.TryGetValue(frame, out positions) ? positions : null;
    }

    /// <summary>Whether <paramref name="frame"/> is evaluated for the current document.</summary>
    public bool Fresh(int frame)
    {
        lock (gate)
            return frames.ContainsKey(frame);
    }
}
