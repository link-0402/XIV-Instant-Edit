using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// A loop and its startup from the same mod travel together when a timeline save makes a new mod:
/// saving one brings the other, unchanged, so the new mod doesn't pair an edited loop with the
/// game's own startup once the original mod is off.
/// </summary>
internal static class CompanionClipFixture
{
    private const string Loop = "chara/human/c0801/animation/a0001/bt_common/emote/pose04_loop.pap";
    private const string Start = "chara/human/c0801/animation/a0001/bt_common/emote/pose04_start.pap";
    private const string Mod = "Unholy Idle C Adjusted - Idle 4";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Rule(check);
        Outputs(check, reject);
    }

    private static AnimationResource Resource(string gamePath, string? mod) => mod == null
        ? new(gamePath, gamePath, "hash-" + gamePath)
        : new(gamePath, $@"G:\Penumbra\{mod}\{Path.GetFileName(gamePath)}", "hash-" + gamePath, mod, $@"G:\Penumbra\{mod}", Path.GetFileName(gamePath), mod);

    private static AnimationCapture Capture(string? loopMod, string? startMod, bool hasStartup = true, string start = Start) =>
        new("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", new AnimationClip(Loop, "loop", 0, 0, 1, "s.sklb", IsLoop: true),
            hasStartup ? new AnimationClip(start, "start", 0, 0, 1, "s.sklb") : null, [Loop, start],
            [Resource(Loop, loopMod), .. start == Loop ? Array.Empty<AnimationResource>() : [Resource(start, startMod)]],
            new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);

    private static void Rule(Action<bool, string> check)
    {
        var both = Capture(Mod, Mod);
        check(AnimationBakeRules.CompanionClip(both, false, Resource(Loop, Mod), AnimationDestination.NewMod) == Start,
            "saving a loop to a new mod brings its startup from the same mod");
        check(AnimationBakeRules.CompanionClip(both, true, Resource(Start, Mod), AnimationDestination.NewMod) == Loop,
            "saving a startup to a new mod brings its loop from the same mod");
        check(AnimationBakeRules.CompanionClip(both, false, Resource(Loop, Mod), AnimationDestination.InPlace) == null,
            "saving in place writes only the edited clip");
        check(AnimationBakeRules.CompanionClip(Capture(Mod, "Other mod"), false, Resource(Loop, Mod), AnimationDestination.NewMod) == null &&
              AnimationBakeRules.CompanionClip(Capture(Mod, null), false, Resource(Loop, Mod), AnimationDestination.NewMod) == null,
            "a startup from another mod or the game stays where it is");
        check(AnimationBakeRules.CompanionClip(Capture(null, null), false, Resource(Loop, null), AnimationDestination.NewMod) == null,
            "a game loop brings nothing");
        check(AnimationBakeRules.CompanionClip(Capture(Mod, Mod, hasStartup: false), false, Resource(Loop, Mod), AnimationDestination.NewMod) == null,
            "a loop without a startup brings nothing");
        check(AnimationBakeRules.CompanionClip(Capture(Mod, Mod, start: Loop), false, Resource(Loop, Mod), AnimationDestination.NewMod) == null,
            "a startup in the loop's own file is already saved with it");
        check(AnimationBakeRules.CompanionClip(Capture(Mod, Mod.ToUpperInvariant()), false, Resource(Loop, Mod), AnimationDestination.NewMod) == Start,
            "mod folders match whatever their case, as Windows paths do");
        check(AnimationBakeRules.CompanionNote(false) == "Its startup goes into the new mod too." &&
              AnimationBakeRules.CompanionNote(true) == "Its loop goes into the new mod too.",
            "the save footer says which clip comes along");
    }

    private static void Outputs(Action<bool, string> check, Action<Action, string> reject)
    {
        var manifest = new AnimationDependencyManifest([Resource(Loop, Mod), Resource(Start, Mod)],
            ImmutableDictionary<string, byte[]>.Empty.Add(Loop, [1]).Add(Start, [2]));
        var outputs = ImmutableDictionary<string, byte[]>.Empty.Add(Loop, [3]);
        var carried = AnimationBakeRules.WithCompanion(outputs, manifest, Resource(Loop, Mod), Start);
        check(carried.Count == 2 && carried[Loop].SequenceEqual(new byte[] { 3 }) && carried[Start].SequenceEqual(new byte[] { 2 }),
            "the companion goes into the save unchanged, beside the edited clip");
        check(AnimationCommitService.SelectNewModFiles(manifest, carried).Keys.Order().SequenceEqual([Loop, Start]),
            "a new mod gets both clips");
        check(ReferenceEquals(AnimationBakeRules.WithCompanion(outputs, manifest, Resource(Loop, Mod), null), outputs),
            "with no companion the save is unchanged");
        var moved = manifest with { Resources = [Resource(Loop, Mod), Resource(Start, "Other mod")] };
        reject(() => AnimationBakeRules.WithCompanion(outputs, moved, Resource(Loop, Mod), Start),
            "a companion that no longer comes from the same mod stops the save");
    }
}
