using System.Numerics;
using System.Runtime.CompilerServices;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Rebuilding a clip borrowed from another race for the character's own race: which race
/// the character is and the standard skeletons the clip can be rebuilt onto.
/// </summary>
internal static class RaceRebuildFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        ActorRace(check);
        RaceTarget(check);
        DecodeKey(check);
        LiveHandBack(check);
        EditingProblems(check);
    }

    private static void LiveHandBack(Action<bool, string> check)
    {
        static SkeletonBone B(string name, short parent, Vector3 position, Quaternion? rotation = null) =>
            new(name, parent, 0, new(position, rotation ?? Quaternion.Identity, Vector3.One));
        var source = new SkeletonDescription("skeleton", "c0101", [B("n_root", -1, Vector3.Zero), B("n_hara", 0, new(0, 1.07677f, 0)),
            B("j_kosi", 1, Vector3.Zero), B("j_asi_a_l", 2, new(0.1f, 0, 0))], [], [], []);
        var rebuilt = new SkeletonDescription("skeleton", "c0801", [B("n_root", -1, Vector3.Zero), B("n_hara", 0, new(0, 0.99885f, 0)),
            B("j_kosi", 1, Vector3.Zero), B("j_asi_a_l", 2, new(0.09f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 7.2f * MathF.PI / 180)),
            B("iv_ko_c_l", 2, Vector3.Zero)], [], [], []);
        var clip = new MotionClip(rebuilt, 1f / 30, 2) { SourceTracks = [1, 3, 4], BlendHint = 0, Loops = true };
        for (var s = 0; s < clip.Samples; s++)
        {
            for (var b = 0; b < clip.Bones; b++)
                clip.Set(s, b, MotionTransform.From(rebuilt.Bones[b].Reference));
            clip.Set(s, 1, clip.Get(s, 1) with { Position = new(0.01f, 0.95f, 0.02f) });
            clip.Set(s, 3, clip.Get(s, 3) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6) });
        }

        check(Math.Abs(LiveConversion.HipsRatio(rebuilt, source) - 0.92764f) < 1e-5f, "the hips ratio is the race's n_hara rest height over the clip's");
        var back = LiveConversion.ToSource(clip, source, [1, 3]);
        check(Vector3.Distance(back.Get(0, 1).Position, new Vector3(0.01f, 0.95f, 0.02f) / 0.92764f) < 1e-5f,
            "the hips are divided by the ratio, so the game's scaling gives the rebuilt value");
        check(back.Get(0, 3).Rotation == clip.Get(0, 3).Rotation && back.Get(0, 3).Position == new Vector3(0.1f, 0, 0),
            "rotations are copied, other translations sit at the clip's own rest");
        check(back.Skeleton == source && back.SourceTracks.SequenceEqual([(short)1, (short)3]) && back.Loops && back.Duration == clip.Duration &&
              back.Samples == clip.Samples, "the copy is on the clip's skeleton with its playing tracks");
        var (tracks, sources, missing) = LiveConversion.Tracks(rebuilt, source, [3, 1, 4], [1, 3]);
        check(tracks.SequenceEqual([(short)3, (short)1]) && sources.SequenceEqual([1, 0]) && missing == 1, "missing bones are counted and dropped");
        check(LiveConversion.HipsRatio(rebuilt, source with { Bones = [B("n_root", -1, Vector3.Zero)] }) == 1, "no n_hara: no scaling");
        check(LiveConversion.HipsRatio(rebuilt with { Bones = [B("n_root", -1, Vector3.Zero), B("n_hara", 0, Vector3.Zero)] }, source) == 1,
            "a rebuilt n_hara at height 0: no scaling, so the hand-back never divides by 0");
        check(TimelineWindowViews.LiveUnshown(1) == "Live can't show 1 bone until you save." &&
              TimelineWindowViews.LiveUnshown(3) == "Live can't show 3 bones until you save.", "the Live line counts bones in words");
    }

    private static void EditingProblems(Action<bool, string> check)
    {
        var (clip, _, actorIvcs, _) = Borrowed();
        var doc = new TimelineDocument(30, 30, null, [], 1);
        check(TimelineWindowViews.BonesProblem(doc, clip, actorIvcs.Skeleton, SkeletonStandard.Ivcs, "c0801") == null,
            "a rebuild onto the character's race is never the skeleton the clip was already made for");
        check(TimelineWindowViews.BonesProblem(doc, clip, actorIvcs.Skeleton, SkeletonStandard.Ivcs, "c1101") is { } none && none.StartsWith("No IVCS", StringComparison.Ordinal),
            "the repair's standard is looked up for the race given");

        // Refused before the service touches anything, so a bare instance is enough.
        var service = (AnimationEditService)RuntimeHelpers.GetUninitializedObject(typeof(AnimationEditService));
        var source = TimelineToolsFixture.Source(clip, doc) with { Rebuild = new("c0801", actorIvcs) };
        static string? Refusal(Func<Task> run)
        {
            try { run().GetAwaiter().GetResult(); return null; }
            catch (InvalidOperationException e) { return e.Message; }
        }
        check(Refusal(() => service.ReplaceMotionAsync(source, null!, null!, doc, _ => { }, default)) ==
                  "A motion from Blender can't go onto a rebuilt clip yet. Choose Keep its own first." &&
              Refusal(() => service.HoldStillAsync(source, null!, 0, 30, _ => { }, default)) ==
                  "A still pose can't go onto a rebuilt clip yet. Choose Keep its own first.",
            "Motion from Blender and Still pose refuse while a rebuild is on");
    }

    /// <summary>A c0101 clip on the IVCS rig played by a c0801 character, with c0801's game and IVCS standards.</summary>
    private static (AnimationClip Clip, StandardSkeleton ActorVanilla, StandardSkeleton ActorIvcs, StandardSkeleton OwnIvcs) Borrowed()
    {
        static SkeletonDescription S(string fingerprint, params string[] names) => new("skeleton", fingerprint,
            [.. names.Select((name, i) => new SkeletonBone(name, (short)(i == 0 ? -1 : 0), 0, new(Vector3.Zero, Quaternion.Identity, Vector3.One)))], [], [], []);
        var actorVanilla = new StandardSkeleton(SkeletonStandard.Vanilla, S("c0801", "n_root", "n_hara", "j_kosi", "j_sebo_a"), "c0801 game");
        var actorIvcs = new StandardSkeleton(SkeletonStandard.Ivcs, S("c0801 ivcs", "n_root", "n_hara", "j_kosi", "j_sebo_a", "iv_ko_c_l"), "c0801 IVCS");
        var ownIvcs = new StandardSkeleton(SkeletonStandard.Ivcs, AnimationBonesFixture.Rigs.Ivcs, "c0101 IVCS");
        var resource = new AnimationResource("chara/test.pap", "chara/test.pap", "hash");
        var candidate = new SkeletonCandidate(new(SkeletonSourceKind.Mod, resource), AnimationBonesFixture.Rigs.Ivcs, 0, "");
        // Track bones 2 and 6 of the IVCS rig: j_kosi and iv_ko_c_l.
        var clip = new AnimationClip("chara/human/c0101/animation/a0001/bt_common/emote/cheer.pap", "loop", 0, 0, 42,
            "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [2, 6],
                Standards: [new(SkeletonStandard.Vanilla, AnimationBonesFixture.Rigs.Game, "c0101 game"), ownIvcs],
                ActorModel: "c0801", ActorStandards: [actorVanilla, actorIvcs]));
        return (clip, actorVanilla, actorIvcs, ownIvcs);
    }

    private static void DecodeKey(Action<bool, string> check)
    {
        var (clip, _, actorIvcs, _) = Borrowed();
        var source = TimelineToolsFixture.Source(clip, new TimelineDocument(30, 30, null, [], 1));
        var plain = MotionDecode.KeyOf(source);
        var rebuilt = MotionDecode.KeyOf(source with { Rebuild = new("c0801", actorIvcs) });
        check(plain != rebuilt && MotionDecode.ClipPart(plain) != MotionDecode.ClipPart(rebuilt),
            "a rebuild is another decode, and Live starts again on it");
        // j_kosi and iv_ko_c_l sit at 2 and 4 in the c0801 IVCS skeleton.
        check(AnimationBones.TracksOn(clip, AnimationBonesFixture.Rigs.Ivcs).SequenceEqual(clip.Resolution!.TrackBones) &&
              AnimationBones.TracksOn(clip, actorIvcs.Skeleton).SequenceEqual([(short)2, (short)4]),
            "tracks on the editing skeleton follow the bones' names");
    }

    private static void RaceTarget(Action<bool, string> check)
    {
        var (clip, actorVanilla, actorIvcs, ownIvcs) = Borrowed();

        check(AnimationBones.Standard(clip, SkeletonStandard.Ivcs, "c0801") == actorIvcs &&
              AnimationBones.Standard(clip, SkeletonStandard.Ivcs) == ownIvcs && AnimationBones.Standard(clip, SkeletonStandard.Ivcs, "c1101") == null,
            "a standard is looked up for the race asked");
        check(AnimationBones.DefaultRebuild(clip, "c0101", []) == SkeletonStandard.Ivcs, "the smallest standard of the target race holding the moved bones");
        check(AnimationBones.DefaultRebuild(clip, "c0101", ["iv_ko_c_l"]) == SkeletonStandard.Vanilla, "excluded bones don't count");
        check(AnimationBones.DefaultRebuild(clip with { Resolution = clip.Resolution! with { ActorModel = null, ActorStandards = [] } }, "c0101", []) == null,
            "none when the races agree");
        check(AnimationBones.DefaultRebuild(clip, "c0801", []) == null && AnimationBones.RebuildNote(clip, "c0801", [], false) == null &&
              AnimationBones.RebuildRace(clip, "c0801") == null,
            "a file already saved for the race offers no rebuild");
        check(AnimationBones.DefaultRebuild(clip with { Resolution = clip.Resolution! with { ActorStandards = [actorVanilla] } }, "c0101", []) == null,
            "none when no standard holds them");
        var lacks = AnimationPresentation.BoneName("iv_ko_c_l");
        check(AnimationBones.RebuildNote(clip, "c0101", [], true) == "Rebuilt for Miqo'te (Female): your character's skeleton." &&
              AnimationBones.RebuildNote(clip with { Resolution = clip.Resolution! with { ActorStandards = [actorVanilla] } }, "c0101", [], false) ==
                  $"Can't rebuild for Miqo'te (Female): its skeletons lack {lacks}." &&
              AnimationBones.RebuildNote(clip with { Resolution = clip.Resolution! with { ActorStandards = [] } }, "c0101", [], false) ==
                  "Can't rebuild for Miqo'te (Female): its skeleton wasn't found." &&
              AnimationBones.RebuildNote(clip, "c0101", [], false) == null,
            "the line under the footer says what was rebuilt, or why it can't be");

        var doc = new TimelineDocument(30, 30, null, [], 1);
        var source = TimelineToolsFixture.Source(clip, doc);
        var request = AnimationEditService.TimelineRequest(source, doc, AnimationDestination.InPlace, "Mod");
        var chosen = AnimationEditService.TimelineRequest(source with { Rebuild = new RaceRebuild("c0801", actorIvcs) }, doc,
            AnimationDestination.InPlace, "Mod", SkeletonStandard.Vanilla);
        var expression = AnimationEditService.TimelineRequest(TimelineToolsFixture.Source(clip, doc, -1) with { Rebuild = new RaceRebuild("c0801", actorIvcs) },
            doc, AnimationDestination.InPlace, "Mod");
        check(chosen is { RepairTarget: SkeletonStandard.Ivcs, RepairModel: "c0801" } && request is { RepairTarget: null, RepairModel: null } &&
              expression is { RepairTarget: null, RepairModel: null },
            "a save rebuilds onto the chosen race's standard; an expression never does");
        var rebuilt = request with { RepairTarget = SkeletonStandard.Ivcs, RepairModel = "c0801" };
        check(AnimationBakeRules.RepairedModel(rebuilt, clip, new AnimationPap(TimelineToolsFixture.Pap(101))) == 801, "a rebuild names the target race in the header");
        var compact = AnimationCommitService.RecoveryRequest(rebuilt).Capture.Clip.Resolution!;
        check(compact.ActorStandards.Single() == actorIvcs && compact.Standards.IsEmpty, "a journal keeps the standard it rebuilt onto");
        var own = AnimationCommitService.RecoveryRequest(rebuilt with { RepairModel = null }).Capture.Clip.Resolution!;
        check(own.Standards.Single() == ownIvcs && own.ActorStandards.IsEmpty, "a repair onto the clip's own race keeps only that standard");
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
        check(AnimationSkeletonIndex.ActorStandards(c0801, null, [game, yas], [yas], null) is (null, { IsEmpty: true }),
            "a clip naming no human race: no rebuild");
        check(AnimationSkeletonIndex.ActorStandards(null, "c0101", [game], [], null).Model == null, "no single base skeleton: no race");
        check(AnimationSkeletonIndex.ActorStandards(c0801, "c0101", [yas], [yas], null) is { Model: "c0801", Standards.IsEmpty: true },
            "without the race's game skeleton the race is known but has no standards");
    }
}
