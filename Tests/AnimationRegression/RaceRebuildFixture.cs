using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Studio.Models;
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
        BorrowedBy(check);
        FooterChoices(check);
        LayersOverBake(check);
        LoopAndLive(check);
        RepairEditing(check);
    }

    /// <summary>
    /// A repair onto a standard of the clip's own race (a rebuild without a race): the bones are edited and decoded on
    /// that standard, the save repairs onto it without changing the race, and Live keeps every translation.
    /// </summary>
    private static void RepairEditing(Action<bool, string> check)
    {
        var (clip, _, _, ownIvcs) = Borrowed();
        var repair = new RaceRebuild(null, ownIvcs);
        var moved = ownIvcs with { Origin = "another IVCS" };
        check(ReferenceEquals(AnimationBones.CurrentRebuild(clip, "c0101", repair), repair) &&
              AnimationBones.CurrentRebuild(clip, "c0101", repair with { Target = moved }) == repair &&
              AnimationBones.CurrentRebuild(clip, "c0801", repair) == repair &&
              AnimationBones.CurrentRebuild(clip with { Resolution = clip.Resolution! with { Standards = [] } }, "c0101", repair) == null,
            "a repair follows the clip's own standards, whatever race the character is");

        var doc = new TimelineDocument(30, 30, null, [], 1);
        var source = TimelineToolsFixture.Source(clip, doc);
        var session = (TimelineSession)RuntimeHelpers.GetUninitializedObject(typeof(TimelineSession));
        session.Source = source;
        session.Rebuild = repair;
        check(session.EditingSkeleton(source) == ownIvcs.Skeleton, "the bones are edited on the repair's standard");
        session.Rebuild = null;
        check(session.EditingSkeleton(source) == clip.Resolution!.Selected!.Skeleton, "Keep its own edits on the clip's own skeleton");

        check(MotionDecode.KeyOf(source with { Rebuild = repair }) != MotionDecode.KeyOf(source), "a repair is another decode of the same clip");
        var request = AnimationEditService.TimelineRequest(source with { Rebuild = repair }, doc, AnimationDestination.InPlace, "Mod");
        check(request is { RepairTarget: SkeletonStandard.Ivcs, RepairModel: null } &&
              AnimationBakeRules.RepairedModel(request, clip, new AnimationPap(TimelineToolsFixture.Pap(101))) == null,
            "the save repairs onto the standard and keeps the clip's race");
        check(LoopPoseReader.LoopRebuild(source with { FileModel = "c0101" }, repair) == repair, "the loop is read on the same standard");
        var game = AnimationBonesFixture.Rigs.Game;
        var widerOnGame = AnimationBones.WiderChoices(clip, game, null);
        var gameNames = game.Bones.Select(bone => bone.Name).ToHashSet();
        string[] ivcsOnly = [.. ownIvcs.Skeleton.Bones.Select(bone => bone.Name).Where(name => !gameNames.Contains(name))];
        check(ivcsOnly.Length > 0 && widerOnGame.Count == ivcsOnly.Length && ivcsOnly.All(bone => widerOnGame[bone] == "Repair onto IVCS"),
            "on the game skeleton the map greys out the IVCS bones, pointing at Repair onto IVCS");
        check(AnimationBones.WiderChoices(clip, ownIvcs.Skeleton, null).Count == 0, "on the biggest standard nothing is greyed out");
        check(AnimationBones.WiderChoices(clip, game, "c0801").GetValueOrDefault("iv_ko_c_l") == AnimationBones.RebuildName("c0801", SkeletonStandard.Ivcs),
            "a rebuild's greyed bones point at the rebuild onto the character's race");
        check(TimelineWindowViews.WiderBoneHint("Groin", "Repair onto IVCS + YAS") ==
              "Groin\nNot on this animation's skeleton. Choose Repair onto IVCS + YAS under Skeleton in the Timeline window to pose it.",
            "a greyed-out bone's hover names the skeleton choice that adds it");

        string[] rig = ["n_root", "n_hara", "j_kosi", "nf_sho_j_asi_e_l"];
        check(!TimelineWindowViews.LandsElsewhere("j_kosi", 2, rig) && TimelineWindowViews.LandsElsewhere("iv_kyokin_phys_l", 3, rig) &&
              TimelineWindowViews.LandsElsewhere("ya_kyo_l", 4, rig) && !TimelineWindowViews.LandsElsewhere("ya_kyo_l", 4, null),
            "a bone is marked when the character's skeleton has another bone, or none, at its index; never while that skeleton is unknown");
        check(TimelineWindowViews.LandsElsewhereHint(3, rig) ==
                  $"At index 3 your character's skeleton has {AnimationPresentation.BoneName("nf_sho_j_asi_e_l")}. Keys here would move that bone instead." &&
              TimelineWindowViews.LandsElsewhereHint(4, rig) == "Your character's skeleton has no bone at index 4. Keys here won't play.",
            "the hover says which bone the index is on the character");

        static SkeletonBone B(string name, short parent, Vector3 position) => new(name, parent, 0, new(position, Quaternion.Identity, Vector3.One));
        var own = new SkeletonDescription("skeleton", "game", [B("n_root", -1, Vector3.Zero), B("n_hara", 0, new(0, 1, 0)), B("j_kosi", 1, new(0, .1f, 0))], [], [], []);
        var standard = own with { Fingerprint = "ivcs", Bones = [.. own.Bones, B("iv_ko_c_l", 2, Vector3.Zero)] };
        var motion = new MotionClip(standard, 1f / 30, 2) { SourceTracks = [1, 2, 3] };
        for (var s = 0; s < motion.Samples; s++)
        {
            for (var b = 0; b < motion.Bones; b++)
                motion.Set(s, b, MotionTransform.From(standard.Bones[b].Reference));
            motion.Set(s, 2, motion.Get(s, 2) with { Position = new(0, .2f, .05f) });
        }
        var back = LiveConversion.ToSource(motion, own, [1, 2], sameRace: true);
        var converted = LiveConversion.ToSource(motion, own, [1, 2]);
        check(back.Get(0, 2).Position == new Vector3(0, .2f, .05f) && converted.Get(0, 2).Position == new Vector3(0, .1f, 0),
            "Live keeps a repaired clip's translations, as the game plays it unconverted");
    }

    private static MotionLayer Offset(string bone, Vector3 move, bool enabled = true) => new("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1,
        PoseComponents.All, MotionInterpolation.Smooth, [],
        [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone,
            new BoneTransform(move, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .5f), Vector3.One)))], enabled);

    private static void FooterChoices(Action<bool, string> check)
    {
        var (clip, _, actorIvcs, _) = Borrowed();
        check(AnimationBones.RebuildName("c0801", SkeletonStandard.Ivcs) == "Rebuild for Miqo'te (Female), IVCS", "the rebuild entry names the race and standard");
        check(AnimationBones.AutomaticRebuild(clip, "c0101", []) == new RaceRebuild("c0801", actorIvcs) &&
              AnimationBones.AutomaticRebuild(clip, "c0801", []) == null && AnimationBones.AutomaticRebuild(clip, "c0101", [], holdsOthers: true) == null &&
              AnimationBones.AutomaticRebuild(clip with { Resolution = clip.Resolution! with { State = SkeletonResolutionState.Searching } }, "c0101", []) == null,
            "a borrowed clip is rebuilt for you once matched, onto the smallest standard holding its bones");
        var rebuild = new RaceRebuild("c0801", actorIvcs);
        var moved = actorIvcs with { Origin = "another IVCS" };
        check(ReferenceEquals(AnimationBones.CurrentRebuild(clip, "c0101", rebuild), rebuild) &&
              AnimationBones.CurrentRebuild(clip, "c0101", rebuild with { Target = moved }) == rebuild &&
              AnimationBones.CurrentRebuild(clip, "c0801", rebuild) == null && AnimationBones.CurrentRebuild(clip, "c0101", rebuild, holdsOthers: true) == null &&
              AnimationBones.CurrentRebuild(clip with { Resolution = clip.Resolution! with { ActorStandards = [] } }, "c0101", rebuild) == null,
            "a rebuild follows the match: kept, moved onto the standard found now, or dropped");

        check(AnimationBones.RebuildRace(clip, "c0101", holdsOthers: true) == null && AnimationBones.DefaultRebuild(clip, "c0101", [], holdsOthers: true) == null &&
              AnimationBones.RebuildNote(clip, "c0101", [], false, holdsOthers: true) == "Can't rebuild for Miqo'te (Female): this file holds other animations." &&
              AnimationBones.RebuildNote(clip with { Resolution = clip.Resolution! with { ActorStandards = [] } }, "c0101", [], false, holdsOthers: true) ==
                  "Can't rebuild for Miqo'te (Female): its skeleton wasn't found.",
            "a file holding other animations offers no rebuild, and says so when one would be possible");
        check(AnimationEditService.HoldsOthers(new AnimationPap(TimelineToolsFixture.Pap(101, bindings: 2)), 0) &&
              !AnimationEditService.HoldsOthers(new AnimationPap(TimelineToolsFixture.Pap(101)), 0),
            "a PAP holds others when an entry binds another animation");
        check(AnimationBones.RebuildFailedNote("c0801", "A bone is missing.") == "Can't rebuild for Miqo'te (Female): A bone is missing.",
            "a rebuild whose motion can't be read says why");
    }

    private static void LayersOverBake(Action<bool, string> check)
    {
        var (clip, _, actorIvcs, _) = Borrowed();
        var empty = new TimelineDocument(30, 30, null, [], 1);
        var layered = empty with { Motion = [Offset("j_kosi", Vector3.Zero)] };
        const string note = "Saving bakes the layers in. Undo last save brings them back as unsaved edits.";
        check(AnimationBakeRules.BakeInNote(layered, SkeletonStandard.Ivcs) == note && AnimationBakeRules.BakeInNote(layered with { ExcludedBones = ["j_kosi"] }, null) == note &&
              AnimationBakeRules.BakeInNote(layered, null) == null && AnimationBakeRules.BakeInNote(empty, SkeletonStandard.Ivcs) == null &&
              AnimationBakeRules.BakeInNote(layered with { Length = 60 }, SkeletonStandard.Ivcs) == "Saving bakes the layers in.",
            "the footer says when a save bakes the layers in, and whether Undo brings them back");

        var source = TimelineToolsFixture.Source(clip, layered) with { Rebuild = new("c0801", actorIvcs) };
        var rebuilt = AnimationEditService.TimelineRequest(source, layered, AnimationDestination.InPlace, "Mod");
        var plain = AnimationEditService.TimelineRequest(source with { Rebuild = null }, layered, AnimationDestination.InPlace, "Mod");
        var excluded = AnimationEditService.TimelineRequest(source with { Rebuild = null }, layered with { ExcludedBones = ["iv_ko_c_l"] }, AnimationDestination.InPlace, "Mod");
        var stretched = AnimationEditService.TimelineRequest(source, layered with { Length = 60 }, AnimationDestination.InPlace, "Mod");
        check(AnimationBakeRules.LayersBackAfterUndo(rebuilt).SequenceEqual(layered.Motion) && AnimationBakeRules.LayersBackAfterUndo(excluded).SequenceEqual(layered.Motion) &&
              AnimationBakeRules.LayersBackAfterUndo(plain).IsEmpty && AnimationBakeRules.LayersBackAfterUndo(stretched).IsEmpty,
            "undoing a save that baked the layers in brings them back, unless the save retimed them");
    }

    private static void LoopAndLive(Action<bool, string> check)
    {
        var (clip, _, actorIvcs, _) = Borrowed();
        var loop = TimelineToolsFixture.Source(clip, new TimelineDocument(30, 30, null, [], 1)) with { FileModel = "c0101" };
        var rebuild = new RaceRebuild("c0801", actorIvcs);
        check(LoopPoseReader.LoopRebuild(loop, rebuild) == rebuild && LoopPoseReader.LoopRebuild(loop, null) == null &&
              LoopPoseReader.LoopRebuild(loop with { FileModel = "c0801" }, rebuild) == null &&
              LoopPoseReader.LoopRebuild(loop with { Clip = clip with { Resolution = clip.Resolution! with { ActorStandards = [] } } }, rebuild) == null,
            "the loop is read rebuilt only when the game plays it borrowed the same way");

        var doc = new TimelineDocument(30, 30, null, [], 1);
        var pose = new MotionLayer("m2", MotionLayerKind.Pose, "Pose 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Smooth, [],
            [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kosi", new BoneTransform(Vector3.One, Quaternion.Identity, Vector3.One)), ["j_kosi"])]);
        check(TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Offset("j_kosi", Vector3.UnitY)] }) &&
              TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [pose] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [pose with { Keys = [pose.Keys[0] with { Moved = [] }] }] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Offset("n_hara", Vector3.UnitY)] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Offset("j_kosi", Vector3.Zero)] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [Offset("j_kosi", Vector3.UnitY, enabled: false)] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc),
            "Live says when a rebuilt clip's moves on bones other than the hips show only after the save");
        // Review: a swing that only turns moves nothing.
        var swing = new MotionLayer("m3", MotionLayerKind.Swing, "Swing 1", 0, 30, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Bones: ["j_kosi"], Swing: new MotionSwing());
        check(!TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [swing] }) &&
              TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [swing with { Swing = new MotionSwing(Slide: .05f) }] }) &&
              !TimelineWindowViews.LiveRebuiltPositions(doc with { Motion = [swing with { Bones = ["n_hara"], Swing = new MotionSwing(Slide: .05f) }] }),
            "a swing counts for Live's move line only when it slides a bone other than the hips");
        check(TimelineWindowViews.LiveRebuiltPositionsNote == "Live shows position changes only on the hips while rebuilt. Preview, then Live, shows them all.",
            "the Live line for moves it can't show");
    }

    private static void BorrowedBy(Action<bool, string> check)
    {
        // c0801 and c0201 borrow c0101's loop; c0101 and c1101 have no record, so each plays its own.
        PapLoadTableFixture.Remap Lent() => new(101, 1, 0);
        PapLoadTableFixture.Remap Own(uint race) => new(race, 1, 0);
        var table = new PapLoadTable(PapLoadTableFixture.Table(["bt_common"], [(0, "emote_sp/sp10_loop")],
            [new(801, 1, 0, [0]), new(201, 1, 1, [0])], [Lent(), Own(801), Own(801), Own(801), Lent(), Own(201), Own(201), Own(201)]));
        var path = "chara/human/c0101/animation/a0001/bt_common/emote_sp/sp10_loop.pap";
        check(table.PlayedBy(path, [101, 201, 801, 1101]).SequenceEqual<ushort>([101, 201, 801]), "the races that load this file");
        check(table.PlayedBy("chara/monster/m0001/animation/a0001/bt_common/emote_sp/sp10_loop.pap", [101, 801]).IsEmpty,
            "a path outside the human layout is played by no race");
        check(AnimationBakeRules.BorrowNote([801], 801) == null, "no note when only the character's race plays it");
        check(AnimationBakeRules.BorrowNote([101, 801], 801) == $"{RaceInfo.NameOf(101)} plays it converted from {RaceInfo.NameOf(801)}.",
            "one other race plays it");
        check(AnimationBakeRules.BorrowNote([101, 201, 801], 801) ==
              $"{RaceInfo.NameOf(101)} and {RaceInfo.NameOf(201)} play it converted from {RaceInfo.NameOf(801)}." &&
              AnimationBakeRules.BorrowNote([101, 201, 801, 1101], 801) ==
              $"{RaceInfo.NameOf(101)}, {RaceInfo.NameOf(201)} and {RaceInfo.NameOf(1101)} play it converted from {RaceInfo.NameOf(801)}.",
            "the others that play the file are named");
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
                  "A motion from Blender can't go onto a rebuilt or repaired clip yet. Choose Keep its own first." &&
              Refusal(() => service.HoldStillAsync(source, null!, 0, 30, _ => { }, default)) ==
                  "A still pose can't go onto a rebuilt or repaired clip yet. Choose Keep its own first.",
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
        check(plain != rebuilt && MotionDecode.ClipPart(plain) == MotionDecode.ClipPart(rebuilt),
            "a rebuild is another decode of the same clip, so Live reads it again and starts again on it");
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
