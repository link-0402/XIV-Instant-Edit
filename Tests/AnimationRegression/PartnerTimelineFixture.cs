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
