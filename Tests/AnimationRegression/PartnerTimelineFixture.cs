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
