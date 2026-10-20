using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The frames that play the playhead's pose the same way (<see cref="PoseMatches"/>), which the paused
/// Timeline marks for finding a loop, and keeping only the frames between two of them (<see cref="CutEdits.KeepRange"/>).
/// </summary>
internal static class PoseMatchFixture
{
    public static void Run(Action<bool, string> check)
    {
        // A swing with a 50-frame period: frame 20 comes back at 70, 120, 170, 220 and 270, falling each time.
        // Its pose also comes back at 5, 55, ... on the way up: the same pose, the other way, never marked.
        var swing = MotionCutFixture.Spin(301, f => 60 * MathF.Sin(2 * MathF.PI * f / 50));
        var marks = PoseMatches.Find(swing, 20);
        check(marks.Select(m => m.Frame).SequenceEqual([70, 120, 170, 220, 270]), "a repeating swing marks every frame a period away");
        check(marks.All(m => m.MeanAngle < .01f && m.WorstAngle < .01f), "an exact repeat is 0 degrees apart");
        check(!marks.Any(m => (m.Frame - 5) % 50 == 0), "the same pose passed the other way is not marked");

        // A slow swing: frames next to a match are as close as it, so each run is one mark, at its closest
        // frame, and the run around the playhead is none.
        var slow = MotionCutFixture.Spin(201, f => 10 * MathF.Sin(2 * MathF.PI * f / 50));
        check(PoseMatches.Find(slow, 0).Select(m => m.Frame).SequenceEqual([50, 100, 150, 200]), "one mark per run of close frames, none around the playhead");

        // Eight bones, one of which comes back 6 degrees off: the mean stays under a degree, the worst bone doesn't.
        var off = Many(101, (f, bone) => 30 * MathF.Sin(2 * MathF.PI * f / 50) + (bone == 7 && f >= 40 ? 6 : 0));
        check(PoseMatches.Find(off, 10).Count == 0, "one bone far off is no match, however close the rest are");
        var near = Many(101, (f, bone) => 30 * MathF.Sin(2 * MathF.PI * f / 50) + (bone == 7 && f >= 40 ? 2 : 0));
        check(PoseMatches.Find(near, 10) is [{ Frame: 60, MeanAngle: > .2f and < .3f, WorstAngle: > 1.9f and < 2.1f }],
            "a bone a little off still matches, and the mark says how far");

        // The cut motion, as the timeline plays it: 20 frames off the start move every mark 20 frames left.
        var cut = MotionCutter.Apply(swing, new MotionCuts(300, [new(20, 300)], []), 280);
        check(PoseMatches.Find(cut, 0).Select(m => m.Frame).SequenceEqual([50, 100, 150, 200, 250]), "marks are in the frames the cut motion plays");
        check(PoseMatches.Find(swing, -5).Count == 0 && PoseMatches.Find(swing, 400).Count == 0, "a playhead off the clip marks nothing");

        Keep(check);
        // The Timeline samples the motion once and searches it at each playhead.
        var table = PoseMatches.Sample(swing);
        check(table.Length == 301 && PoseMatches.Find(table, 20).SequenceEqual(marks), "a sampled motion finds the same marks");
    }

    private static void Keep(Action<bool, string> check)
    {
        // Keep 20 to 70 of the 100-frame clip: sounds at 10 and 70 go, the one at 45 plays at 25.
        var doc = CutEditsFixture.Doc(100);
        var kept = CutEdits.KeepRange(doc, 20, 70, out var gone);
        check(kept is { Length: 50, Cuts.Parts: [{ From: 20, To: 70 }] } && CutEditsFixture.Find(kept, "t3")?.Start == 25 &&
              CutEditsFixture.Find(kept, "t2") == null && CutEditsFixture.Find(kept, "t5") == null && gone.Events >= 2,
            "keeping a range removes what is before and after it");
        check(CutEdits.KeepRange(doc, 70, 20, out _) is { Length: 50, Cuts.Parts: [{ From: 20, To: 70 }] }, "the range's ends can come either way round");
        check(ReferenceEquals(CutEdits.KeepRange(doc, 0, 100, out _), doc) && ReferenceEquals(CutEdits.KeepRange(doc, 40, 41, out _), doc),
            "keeping everything, or under 2 frames, changes nothing");
        var editor = new TimelineEditor(doc);
        var steps = editor.History.UndoCount;
        check(editor.KeepOnly(20, 70) && editor.Document.Length == 50 && editor.History.UndoCount == steps + 1 &&
              editor.Undo() && editor.Document.Length == 100, "Keep only this loop is one undo step");
    }

    /// <summary>A root and seven arm bones, each turned about Z by <paramref name="degrees"/> of the frame and bone; all tracked.</summary>
    private static MotionClip Many(int samples, Func<float, int, float> degrees)
    {
        var bones = new List<SkeletonBone> { new("n_root", -1, 0, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One)) };
        for (var b = 1; b < 8; b++)
            bones.Add(new($"j_bone_{b}", 0, 0, new BoneTransform(Vector3.UnitX, Quaternion.Identity, Vector3.One)));
        var rig = new SkeletonDescription("match-rig", "match-rig-fingerprint", [.. bones], [], [], []);
        var poses = Enumerable.Range(0, samples).Select(f => Enumerable.Range(0, 8).Select(b => b == 0
            ? new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One)
            : new BoneTransform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees(f, b) * MathF.PI / 180), Vector3.One)).ToArray()).ToArray();
        var clip = MotionClip.FromPoses(rig, (samples - 1) / 30f, poses);
        var copy = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples, null, 0) { SourceTracks = [0, 1, 2, 3, 4, 5, 6, 7] };
        copy.CopyFrom(clip);
        return copy;
    }
}
