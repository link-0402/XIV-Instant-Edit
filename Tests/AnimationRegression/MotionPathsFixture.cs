using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

/// <summary>
/// The overlay's motion paths without ImGui: the frames around the playhead a path runs through (<see cref="MotionPaths.Window"/>),
/// where the selected layer keys a bone, and the cache the frames are evaluated into off the draw (<see cref="MotionPathCache"/>).
/// </summary>
internal static class MotionPathsFixture
{
    private const string Hand = "j_te_l", Twin = "j_te_r";

    public static void Run(Action<bool, string> check)
    {
        Windows(check);
        Keys(check);
        Cache(check);
    }

    private static void Windows(Action<bool, string> check)
    {
        var (frames, at) = MotionPaths.Window(50, 15, 100, 0);
        check(frames.SequenceEqual(Enumerable.Range(35, 31)) && at == 15, "a path runs from range before the playhead to range after it");
        (frames, at) = MotionPaths.Window(4, 15, 100, 0);
        check(frames.SequenceEqual(Enumerable.Range(0, 20)) && at == 4, "a path stops at the clip's first frame");
        (frames, at) = MotionPaths.Window(95, 15, 100, 0);
        check(frames.SequenceEqual(Enumerable.Range(80, 21)) && at == 15, "a path stops at the clip's last frame");
        (frames, at) = MotionPaths.Window(4, 10, 120, 120);
        check(frames.SequenceEqual(Enumerable.Range(114, 6).Concat(Enumerable.Range(0, 15))) && at == 10 && frames[at] == 4,
            "a loop's path wraps round its seam in time order");
        (frames, at) = MotionPaths.Window(118, 10, 120, 120);
        check(frames.SequenceEqual(Enumerable.Range(108, 12).Concat(Enumerable.Range(0, 9))) && frames[at] == 118, "a path wraps past a loop's end too");
        (frames, at) = MotionPaths.Window(120, 5, 120, 120);
        check(frames[at] == 0 && frames.Length == 11, "a loop's last frame is its first");
        (frames, at) = MotionPaths.Window(10, 60, 40, 40);
        check(frames.Length == 40 && frames.Distinct().Count() == 40 && frames[at] == 10, "a range wider than the loop holds each frame once");
        (frames, at) = MotionPaths.Window(0, 15, 0, 0);
        check(frames.SequenceEqual([0]) && at == 0, "a one-frame clip has a one-frame path");
        check(MotionPaths.Range(1) == MotionPaths.MinRange && MotionPaths.Range(500) == MotionPaths.MaxRange && MotionPaths.Range(20) == 20,
            "the range stays between 5 and 60 frames");
    }

    private static void Keys(Action<bool, string> check)
    {
        var still = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "L", 0, 120, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [],
            [Key(5, (Hand, still)), Key(20, (Hand, still), (Twin, still)), Key(40, (Twin, still)), Key(120, (Hand, still))]);
        check(MotionPaths.KeyFrames(layer, Hand, 0).SetEquals([5, 20, 120]), "a path's key frames are the layer's keys on that bone");
        check(MotionPaths.KeyFrames(layer, Twin, 0).SetEquals([20, 40]), "another bone has its own key frames");
        check(MotionPaths.KeyFrames(layer, Hand, 120).SetEquals([5, 20, 0]), "a key on a loop's last frame shows on its first");
        check(MotionPaths.KeyFrames(null, Hand, 0).Count == 0, "without a selected layer no frame is a key frame");
    }

    private static MotionKey Key(int frame, params (string Bone, BoneTransform Value)[] bones) =>
        new(frame, bones.ToImmutableDictionary(b => b.Bone, b => b.Value, StringComparer.Ordinal));

    private static Vector3[] Pose(float x) => [new(x, 0, 0), new(0, x, 0)];

    private static void Cache(Action<bool, string> check)
    {
        object doc = new(), clip = new(), state = "s";
        var cache = new MotionPathCache();
        var (frames, at) = MotionPaths.Window(10, 2, 100, 0);
        cache.Show(doc, clip, state, frames, at);
        var order = new List<int>();
        while (cache.TryNext(out var job))
        {
            check(ReferenceEquals(job.Doc, doc) && ReferenceEquals(job.Clip, clip) && ReferenceEquals(job.State, state), "a job carries what it is evaluated from");
            order.Add(job.Frame);
            cache.Put(job.Doc, job.Clip, job.Frame, Pose(job.Frame));
        }
        check(order.SequenceEqual([10, 9, 11, 8, 12]), "the playhead's frame is evaluated first, then outward from it");
        check(cache.At(12) is { } twelve && twelve[0].X == 12 && cache.At(13) == null, "evaluated frames are read back, missing ones are null");

        // Moving the playhead evaluates only the frames it hasn't got.
        (frames, at) = MotionPaths.Window(12, 2, 100, 0);
        cache.Show(doc, clip, state, frames, at);
        order.Clear();
        while (cache.TryNext(out var job))
        {
            order.Add(job.Frame);
            cache.Put(job.Doc, job.Clip, job.Frame, Pose(job.Frame));
        }
        check(order.SequenceEqual([13, 14]), "moving the playhead reuses the frames already evaluated");

        // An edit is a new document: every frame is evaluated again, the last ones show meanwhile.
        object edited = new();
        cache.Show(edited, clip, state, frames, at);
        check(cache.TryNext(out var first) && first.Frame == 12 && ReferenceEquals(first.Doc, edited), "an edit evaluates the frames again, playhead first");
        check(cache.At(14) is { } old && old[0].X == 14 && !cache.Fresh(14), "until then the frames from before the edit show");
        cache.Put(doc, clip, 12, Pose(99));
        check(!cache.Fresh(12), "a frame evaluated for the document before the edit is dropped");
        cache.Put(edited, clip, 12, Pose(50));
        check(cache.Fresh(12) && cache.At(12)![0].X == 50, "a frame evaluated for the edit replaces the old one");

        // Another clip shares nothing: its bones are others.
        cache.Show(new object(), new object(), state, frames, at);
        check(cache.At(14) == null && cache.At(12) == null, "another clip shows none of the last clip's frames");

        // Nothing is wanted once the paths are hidden.
        cache.Hide();
        check(!cache.TryNext(out _), "hidden paths evaluate nothing");

        // The store keeps at most a few windows of frames.
        var big = new MotionPathCache();
        for (var playhead = 0; playhead <= 1000; playhead += 5)
        {
            (frames, at) = MotionPaths.Window(playhead, 2, 1000, 0);
            big.Show(doc, clip, state, frames, at);
            while (big.TryNext(out var job))
                big.Put(job.Doc, job.Clip, job.Frame, Pose(job.Frame));
        }
        check(big.Count <= 4 * 5 + 5 && big.At(1000) != null && big.At(0) == null, "frames far behind the playhead are let go");
    }
}
