using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// Rebuilding a clip borrowed from another race for the character's own race: which race
/// the character is and the standard skeletons the clip can be rebuilt onto.
/// </summary>
internal static class RaceRebuildFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        ActorRace(check);
    }

    private static void ActorRace(Action<bool, string> check)
    {
        var c0801 = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";
        check(AnimationCatalog.BaseSkeleton([c0801, "chara/human/c0801/skeleton/face/f0002/skl_c0801f0002.sklb"]) == c0801,
            "the character's base skeleton is the one base skeleton among the loaded paths");
        check(AnimationCatalog.BaseSkeleton([c0801, "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"]) == null &&
              AnimationCatalog.BaseSkeleton([]) == null, "no single base skeleton");
        var game = new SkeletonCandidate(new(SkeletonSourceKind.Game, new(c0801, c0801, "")), AnimationBonesFixture.Rigs.Game, 0, "");
        var yas = new SkeletonCandidate(new(SkeletonSourceKind.Collection, new(c0801, "yas.sklb", "")), AnimationBonesFixture.Rigs.Yas, 0, "");
        var (model, standards) = AnimationSkeletonIndex.ActorStandards(c0801, "c0101", [game, yas], [yas], null);
        check(model == "c0801" && standards.Select(s => s.Standard).SequenceEqual([SkeletonStandard.Vanilla, SkeletonStandard.IvcsYas]),
            "a clip of another race gets the character's race and its standards");
        check(AnimationSkeletonIndex.ActorStandards(c0801, "c0801", [game, yas], [yas], null) is (null, { IsEmpty: true }),
            "header names the actor's race: no rebuild");
        check(AnimationSkeletonIndex.ActorStandards(null, "c0101", [game], [], null).Model == null, "no single base skeleton: no race");
        check(AnimationSkeletonIndex.ActorStandards(c0801, "c0101", [yas], [yas], null) is { Model: "c0801", Standards.IsEmpty: true },
            "without the race's game skeleton the race is known but has no standards");
    }
}
