using System.Text.Json;
using InstantEdit.Services.Animations.Motion;

/// <summary>
/// Cutting a clip's motion: the cut list and its time map, the offset blends at joins and the loop
/// seam (<see cref="MotionCutter"/>), and the seam check (<see cref="LoopSeam"/>).
/// </summary>
internal static class MotionCutFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Model(check, reject);
    }

    private static void Model(Action<bool, string> check, Action<Action, string> reject)
    {
        var cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]);
        check(cuts.Kept == 80 && !cuts.Whole && MotionCuts.WholeOf(100).Whole, "kept frames and whole");
        check(cuts.SourceAt(0, 80) == 0 && cuts.SourceAt(39.5, 80) == 39.5 && cuts.SourceAt(40, 80) == 60 && cuts.SourceAt(80, 80) == 100,
            "output frames map through the parts; a boundary is the right part's start");
        check(cuts.SourceAt(80, 160) == 60 && cuts.PartAt(80, 160) == 1 && cuts.PartStart(1, 160) == 80, "a stretch scales the map");
        check(cuts.PartAt(40, 80) == 1 && cuts.PartEnd(0, 80) == 40 && cuts.PartAt(80, 80) == 1, "parts by output frame");
        reject(() => new MotionCuts(100, [new(0, 50), new(40, 100)], [MotionJoin.ForCut]).Validate(), "overlapping parts are refused");
        reject(() => new MotionCuts(100, [new(0, 50)], [MotionJoin.ForCut]).Validate(), "a join count that doesn't match is refused");
        reject(() => new MotionCuts(100, [new(0, 100)], [], new MotionJoin(61, JoinPlacement.Before)).Validate(), "a blend over 60 frames is refused");
        var json = JsonSerializer.Serialize(cuts with { Seam = MotionJoin.ForSeam }, new JsonSerializerOptions { IncludeFields = true });
        check(JsonSerializer.Deserialize<MotionCuts>(json, new JsonSerializerOptions { IncludeFields = true }) is
              { Source: 100, Parts.Length: 2, Seam.Placement: JoinPlacement.Before } back && back.Parts[1] == new MotionPart(60, 100),
            "cuts survive a journal's JSON");
    }
}
