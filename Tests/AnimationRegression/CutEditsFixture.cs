using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Cutting a timeline document: the cut list it keeps, and the ripple that moves, shortens or
/// removes its events and bone layer keys when frames go or come back.
/// </summary>
internal static class CutEditsFixture
{
    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    private static TimelineEvent Ev(string key, TimelineLane lane, int start, int duration = 0, bool resizable = false,
        TimelineAccess access = TimelineAccess.Editable, string magic = "C063") =>
        new(key, magic, lane, start, duration, resizable, "", access, access == TimelineAccess.Editable ? null : "Kept as is", false, NoValues);

    /// <summary>
    /// A 100-frame clip: its motion (locked, the whole length), sounds at 10, 45 and 70, an effect over
    /// 30 to 60, a locked entry at 50, and an offset layer over 20 to 90 with keys at 20, 40, 50, 60 and 90.
    /// </summary>
    internal static TimelineDocument Doc(int length)
    {
        var keys = new[] { 20, 40, 50, 60, 90 }
            .Select(f => new MotionKey(f, ImmutableDictionary<string, BoneTransform>.Empty))
            .ToImmutableArray();
        var layer = new MotionLayer("m1", MotionLayerKind.Offset, "Offset", 20, 90, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [], keys);
        return new TimelineDocument(length, length, null,
        [
            Ev("t1", TimelineLane.Motion, 0, length, access: TimelineAccess.Locked, magic: "C009"),
            Ev("t2", TimelineLane.Sound, 10),
            Ev("t3", TimelineLane.Sound, 45),
            Ev("t4", TimelineLane.Effect, 30, 30, resizable: true, magic: "C012"),
            Ev("t5", TimelineLane.Sound, 70),
            Ev("t6", TimelineLane.Other, 50, access: TimelineAccess.Locked, magic: "C031"),
        ], 1) { Motion = [layer] };
    }

    internal static TimelineEvent? Find(TimelineDocument doc, string key) => TimelineEdits.Find(doc, key);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Ripple(check);
    }

    private static void Ripple(Action<bool, string> check)
    {
        var doc = Doc(100);
        var cut = CutEdits.RemoveRange(doc, 40, 60, out var gone);
        check(cut.Length == 80 && cut.Cuts is { Parts: [{ From: 0, To: 40 }, { From: 60, To: 100 }], Joins: [var j] } && j == MotionJoin.ForCut,
            "removing 40-60 leaves two parts and a default join");
        check(Find(cut, "t1")!.Duration == 80 && Find(cut, "t2")!.Start == 10 && Find(cut, "t3") == null &&
              Find(cut, "t4") is { Start: 30, Duration: 10 } && Find(cut, "t5")!.Start == 50 && Find(cut, "t6")!.Start == 40,
            "events ripple: inside goes, spanning shortens, after moves left, locked moves to the cut");
        check(cut.Motion[0] is { Start: 20, End: 70 } m && m.Keys.Select(k => k.Frame).SequenceEqual([20, 40, 70]) && gone == new CutRemoval(1, 2),
            "a key at the range's start goes, the key at its end lands on it");
        check(ReferenceEquals(CutEdits.CutAt(cut, 40), cut) && CutEdits.CutAt(doc, 30).Cuts!.Parts.Length == 2 && CutEdits.CutAt(doc, 30).Length == 100,
            "a cut on a boundary changes nothing; a new cut keeps the length");
        var back = CutEdits.TrimPart(cut, 1, left: true, -20, out _);
        check(back.Cuts == null && back.Length == 100 && Find(back, "t5")!.Start == 70, "trimming back over the removed frames merges the parts");
        var half = CutEdits.TrimPart(cut, 1, left: true, -10, out _);
        check(half.Cuts!.Parts[1] == new MotionPart(50, 100) && half.Length == 90 && Find(half, "t5")!.Start == 60, "a partial trim back inserts frames");
        var stretched = CutEdits.RemoveRange(doc with { Length = 200 }, 80, 120, out _);
        check(stretched.Length == 160 && stretched.Cuts!.Parts[1].From == 60, "a stretch maps output frames to source frames");
        var fixedLoop = CutEdits.FixLoop(doc, new SeamReport(new JoinGap(0, 0, "", .2f, 1.6f), 1.4f, 88, SeamState.Stalls));
        check(fixedLoop.Length == 88 && fixedLoop.Cuts is { Parts: [{ To: 88 }], Seam: var s } && s == MotionJoin.ForSeam, "Fix loop trims the stall and sets the seam");
        check(CutEdits.FixLoop(doc, new SeamReport(new JoinGap(20, 30, "x", 3, 3), 3, null, SeamState.Pops)) is { Length: 100, Cuts.Seam: not null },
            "without a stall Fix loop keeps the length");
        check(ReferenceEquals(CutEdits.RemoveRange(doc, 1, 100, out _), doc), "a cut leaving under 2 frames is refused");
        var expression = doc with { Expression = true };
        check(ReferenceEquals(CutEdits.RemoveRange(expression, 10, 20, out _), expression), "an expression's timeline is never cut");
        check(CutEdits.SetJoin(cut, 0, new MotionJoin(90, JoinPlacement.After)).Cuts!.Joins[0] == new MotionJoin(60, JoinPlacement.After), "blends clamp to 60");
        check(cut is { MotionLength: 100, KeptFrames: 80, Cut: true, Stretched: false } && doc is { Cut: false, Stretched: false, KeptFrames: 100 } &&
              stretched is { KeptFrames: 80, Stretched: true }, "a document knows its motion's length, the frames it keeps and whether it is cut or stretched");
    }
}
