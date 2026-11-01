using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline's workspace per actor: which workspace the GPose target shows and which actor yours acts on. Drawing,
/// playing and previewing on the actors are checked in game.
/// </summary>
internal static class PartnerTimelineFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Choice(check);
        SameFile(check);
        Histories(check);
        Retarget(check);
    }

    /// <summary>A respawned partner is a new actor: the open clip's capture is pointed at it, so the save checks the actor there now.</summary>
    private static void Retarget(Action<bool, string> check)
    {
        var heard = Heard("aria", 0x2000000CA, 202) with { CollectionId = Guid.NewGuid(), ActorAddress = 0x1000 };
        var moved = TimelineSession.Retargeted(heard, 203, 0x2000000CB, 0x2000);
        check(moved is { ActorIndex: 203, ActorId: 0x2000000CB, ActorAddress: 0x2000 } && moved.Id == heard.Id && moved.Clip == heard.Clip,
            "respawn: the capture names the new actor and stays the same clip");
        check(moved.CollectionId == heard.CollectionId,
            "respawn: the collection stays the one heard, so a preset whose collection changed is still refused when saving");
    }

    private static AnimationCapture Heard(string id, ulong actor, ushort index)
    {
        var clip = new AnimationClip("chara/human/c0801/animation/a0001/bt_common/emote/dance.pap", "dance", 0, 1, 0, "chara/test.sklb");
        return new AnimationCapture(id, actor, 1, Guid.Empty, "Test", "Dance", clip, null, [clip.GamePath], [], DateTime.UtcNow, true, ActorIndex: index);
    }

    /// <summary>The listener keeps each actor's recent clips while it follows another, so a target switch loses none of them.</summary>
    private static void Histories(Action<bool, string> check)
    {
        var histories = new CaptureHistories();
        histories.Set(1, Enumerable.Range(0, 60).Select(i => Heard($"you {i}", 1, 201)).ToImmutableArray());
        check(histories.Of(1).Length == CaptureHistories.PerActor && histories.Of(1)[0].Id == "you 0", "histories: 50 clips per actor, newest kept");
        histories.Set(2, [Heard("aria", 2, 202)]);
        check(histories.Of(1).Length == 50 && histories.Of(2).Single().Id == "aria", "histories: another actor's list leaves the first one's alone");
        check(histories.At(202).Single().Id == "aria" && histories.At(201).Length == 50 && histories.At(203).IsEmpty,
            "histories: found by the object index of their newest clip");
        for (ulong actor = 3; actor <= 9; actor++)
            histories.Set(actor, [Heard($"actor {actor}", actor, 210)]);
        check(histories.Of(1).IsEmpty && !histories.Of(2).IsEmpty && !histories.Of(9).IsEmpty,
            "histories: past 8 actors the one set longest ago goes");
    }

    /// <summary>Two actors on one collection can't both preview edits of one file: Penumbra gives a collection one file per game path.</summary>
    private static void SameFile(Action<bool, string> check)
    {
        var shared = Guid.NewGuid();
        var other = Guid.NewGuid();
        var aria = new WorkspaceKey(Guid.NewGuid()).Id;
        var self = WorkspaceKey.Self.Id;
        var files = new PreviewFiles();
        files.Set(aria, "Aria Rose", shared, ["chara/human/c0801/animation/a0001/bt_common/emote/dance.pap"]);
        check(files.Holder(shared, ["CHARA/human/c0801/animation/a0001/bt_common/emote/DANCE.pap"], self) == "Aria Rose",
            "same file: another workspace showing it on the collection holds it, whatever the case");
        check(files.Holder(shared, ["chara/human/c0801/animation/a0001/bt_common/emote/dance.pap"], aria) == null,
            "same file: a workspace's own preview doesn't hold it against itself");
        check(files.Holder(other, ["chara/human/c0801/animation/a0001/bt_common/emote/dance.pap"], self) == null,
            "same file: another collection has its own copy");
        check(files.Holder(shared, ["chara/human/c0801/animation/a0001/bt_common/emote/sit.pap"], self) == null,
            "same file: another file on the collection is free");
        files.Clear(aria);
        check(files.Holder(shared, ["chara/human/c0801/animation/a0001/bt_common/emote/dance.pap"], self) == null,
            "same file: a preview that ended holds nothing");
        check(PreviewFiles.Refusal("You") == "You preview this file from the same collection. Use Live." &&
              PreviewFiles.Refusal("Aria Rose") == "Aria Rose previews this file from the same collection. Use Live.",
            "same file: the refusal names who previews it");
    }

    private static void Choice(Action<bool, string> check)
    {
        var aria = Guid.NewGuid();
        Func<ushort, Guid?> partnerAt202 = index => index == 202 ? aria : null;
        check(WorkspaceChoice.Shown(true, 202, partnerAt202) == new WorkspaceKey(aria), "workspace: a targeted partner shows its own workspace");
        check(WorkspaceChoice.Shown(true, 203, partnerAt202) == WorkspaceKey.Self, "workspace: a Brio or Ktisis actor shows yours");
        check(WorkspaceChoice.Shown(true, null, partnerAt202) == WorkspaceKey.Self, "workspace: no target shows yours");
        check(WorkspaceChoice.Shown(false, 202, partnerAt202) == WorkspaceKey.Self, "workspace: out of GPose only yours shows");

        check(WorkspaceChoice.SelfIndex(true, 202, partnerAt202, true) == 201, "your actor: a targeted partner leaves your posed character");
        check(WorkspaceChoice.SelfIndex(true, 203, partnerAt202, true) == 203, "your actor: a targeted Brio actor is yours to act on");
        check(WorkspaceChoice.SelfIndex(true, 150, partnerAt202, true) == 201, "your actor: a target below the GPose actors leaves your posed character");
        check(WorkspaceChoice.SelfIndex(true, null, partnerAt202, false) == null, "your actor: none without a posed character");
        check(WorkspaceChoice.SelfIndex(false, 203, partnerAt202, true) == null, "your actor: none out of GPose");
    }
}
