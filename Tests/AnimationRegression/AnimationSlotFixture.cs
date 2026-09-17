using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class AnimationSlotFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string dir = "chara/human/c0801/animation/a0001/bt_common/emote";

        var loop = AnimationSlots.Describe($"{dir}/pose01_loop.pap");
        var start = AnimationSlots.Describe($"{dir}/pose01_start.pap");
        check(loop is { Prefix: "pose", Index: 1, Startup: false, Directory: dir } &&
              start is { Prefix: "pose", Index: 1, Startup: true },
            "a numbered pose path decodes into its family, slot number and loop or startup state");
        check(loop!.PapPath == $"{dir}/pose01_loop.pap" && loop.At(6).PapPath == $"{dir}/pose06_loop.pap" &&
              loop.Paired.PapPath == start!.PapPath,
            "a slot rebuilds its own path, another slot's path, and its paired startup");

        // The family comes from the filename prefix, so a set the catalog has no
        // entry for still groups correctly and never mixes with its neighbours.
        var chair = AnimationSlots.Describe($"{dir}/j_pose02_loop.pap");
        check(chair is { Prefix: "j_pose", Index: 2 } && chair.Group != loop.Group && chair.Group == $"{dir}/j_pose",
            "a prefixed pose family is identified separately from the unprefixed one beside it");
        check(AnimationSlots.Describe($"{dir}/POSE03_LOOP.pap") is { Prefix: "pose", Index: 3 },
            "slot decoding is case-insensitive and normalizes the family prefix");

        check(AnimationSlots.Describe($"{dir}/pose01_end.pap") is null &&
              AnimationSlots.Describe($"{dir}/pose1_loop.pap") is null &&
              AnimationSlots.Describe("chara/human/c0801/animation/a0001/bt_common/resident/idle.pap") is null &&
              AnimationSlots.Describe("../pose01_loop.pap") is null,
            "paths that are not numbered slots, including unsafe ones, decode to nothing");

        AnimationSlot Slot(int index, bool startup = false) => new(dir, "pose", index, startup);
        var family = new[] { Slot(1), Slot(1, true), Slot(2), Slot(2, true), Slot(6), Slot(6, true) };
        static string Option(AnimationSlot s) => $"Pose {s.Index}";

        var plan = AnimationSlots.Plan(family, [(6, 1)], Option);
        check(plan.Length == 2 && plan[0].Destination.PapPath == $"{dir}/pose06_loop.pap" &&
              plan[0].Source.PapPath == $"{dir}/pose01_loop.pap" && plan[0].Option == "Pose 1" &&
              plan[1].Destination.Startup && plan[1].Source.Startup && plan[1].Option == plan[0].Option,
            "a slot swap carries the paired startup under the same option as its loop");

        // Mapping a slot to itself would package a file that changes nothing.
        check(AnimationSlots.Plan(family, [(6, 1), (2, 2)], Option).Length == 2,
            "identity mappings are dropped rather than packaged as no-op replacements");
        check(AnimationSlots.Plan(family, [(6, 1), (1, 2)], Option).Select(s => s.Destination.Index).Distinct().Count() == 2,
            "several destinations can be remapped in one plan");

        var loopOnly = new[] { Slot(1), Slot(6) };
        check(AnimationSlots.Plan(loopOnly, [(6, 1)], Option).Length == 1,
            "a family without startups does not gain an invented startup path");
        check(AnimationSlots.Plan([Slot(1), Slot(1, true), Slot(6)], [(6, 1)], Option).Length == 1,
            "a startup is paired only when both the source and destination have one");

        reject(() => AnimationSlots.Plan(family, [(6, 1), (6, 2)], Option), "mapping one destination slot twice is rejected");
        reject(() => AnimationSlots.Plan(family, [(6, 3)], Option), "mapping from a slot outside the family is rejected");
        reject(() => AnimationSlots.Plan(family, [(3, 1)], Option), "mapping onto a slot outside the family is rejected");
        reject(() => AnimationSlots.Plan(family, [(1, 1)], Option), "a plan that swaps nothing is rejected");
        reject(() => AnimationSlots.Plan([], [(6, 1)], Option), "planning a swap with no discovered slots is rejected");
        reject(() => AnimationSlots.Plan([Slot(1), new(dir, "j_pose", 6, false)], [(6, 1)], Option),
            "a swap crossing two animation families is rejected");

        // Moving a clip into another slot also renumbers the motion name the
        // destination timeline resolves by, e.g. cbem_pose03_2lp -> cbem_pose06_2lp.
        check(AnimationSlots.Renumber("cbem_pose03_2lp", 3, 6) == "cbem_pose06_2lp" &&
              AnimationSlots.Renumber("cbem_pose03_2lp", 3, 6)!.Length == "cbem_pose03_2lp".Length,
            "renumbering a motion name rewrites only its slot digits and keeps its length");
        check(AnimationSlots.Renumber("cbem_pose03_2lp", 4, 6) is null,
            "renumbering refuses a name that does not carry the source slot number");
        check(AnimationSlots.Renumber("cbem_pose02_02lp", 2, 6) is null,
            "an ambiguous slot number is refused rather than guessed at");
        check(AnimationSlots.Renumber("", 1, 2) is null && AnimationSlots.Renumber("pose01", 1, 100) is null,
            "renumbering rejects empty names and out-of-range slots");

        check(AnimationPresentation.SlotName(Slot(1), 0, "Loop") == "Standing Idle - Loop 1" &&
              AnimationPresentation.SlotName(new(dir, "j_pose", 2, false), 0, "Loop") == "Chair Sitting Idle 2 - Loop" &&
              AnimationPresentation.SlotName(Slot(3), 653, "Loop") == "Ground Sitting Idle 3 - Loop",
            "slot labels are derived from the same family model the swap planner uses");
    }
}
