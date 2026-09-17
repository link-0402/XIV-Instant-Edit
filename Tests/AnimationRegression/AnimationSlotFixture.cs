using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class AnimationSlotFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string root = "chara/human/c0801/animation/a0001/bt_common";

        var loop = AnimationSlots.Describe($"{root}/emote/pose01_loop.pap");
        var start = AnimationSlots.Describe($"{root}/emote/pose01_start.pap");
        check(loop is { Family: "standing", Index: 1, Startup: false, Root: root } &&
              start is { Family: "standing", Index: 1, Startup: true },
            "a numbered pose path decodes into its family, slot number and loop or startup state");

        // Each family also has an unnumbered base member in another directory, and it
        // swaps like any other slot: jmn.pap is ground-sit 0.
        var ground = AnimationSlots.Describe($"{root}/resident/jmn.pap");
        var chair = AnimationSlots.Describe($"{root}/resident/sit.pap");
        var standing = AnimationSlots.Describe($"{root}/resident/idle.pap");
        check(ground is { Family: "ground", Index: 0, Startup: false } &&
              chair is { Family: "chair", Index: 0 } && standing is { Family: "standing", Index: 0 },
            "the unnumbered base animations decode as slot zero of their own family");
        check(ground!.Group == AnimationSlots.Describe($"{root}/emote/j_pose02_loop.pap")!.Group &&
              chair!.Group == AnimationSlots.Describe($"{root}/emote/s_pose02_loop.pap")!.Group &&
              standing!.Group == loop!.Group,
            "a base animation shares its family's group despite living in another directory");
        check(AnimationSlots.Describe($"{root}/emote/j_pose02_loop.pap")!.Group != loop.Group &&
              AnimationSlots.Describe($"{root}/emote/s_pose02_loop.pap")!.Group != loop.Group,
            "the sitting families stay separate from the standing poses and from each other");

        check(AnimationSlots.Describe($"{root}/emote/POSE03_LOOP.pap") is { Family: "standing", Index: 3 },
            "slot decoding is case-insensitive");
        check(AnimationSlots.Describe($"{root}/emote/zz_pose03_loop.pap") is { Family: "zz_pose" },
            "an unrecognised prefix keeps its own identity rather than joining a family it may not belong to");
        check(AnimationSlots.Describe($"{root}/emote/pose01_end.pap") is null &&
              AnimationSlots.Describe($"{root}/emote/pose1_loop.pap") is null &&
              AnimationSlots.Describe($"{root}/resident/move_a.pap") is null &&
              AnimationSlots.Describe("../pose01_loop.pap") is null,
            "paths that are neither numbered slots nor known base animations decode to nothing");

        // The base member's directory is not assumed; both are offered and discovery
        // keeps whichever resolves.
        var candidates = AnimationSlots.Candidates(ground).Select(slot => slot.PapPath).ToArray();
        check(candidates.Contains($"{root}/resident/jmn.pap") && candidates.Contains($"{root}/emote/jmn.pap") &&
              candidates.Contains($"{root}/emote/j_pose01_loop.pap") &&
              candidates.Contains($"{root}/emote/j_pose{AnimationSlots.MaxProbe:D2}_start.pap") &&
              !candidates.Contains($"{root}/emote/pose01_loop.pap"),
            "a family's probe covers its base animation in either directory and its whole numbered range");

        AnimationSlot Slot(int index, bool startup = false) =>
            new(root, "standing", index, startup, $"{root}/emote/pose{index:D2}_{(startup ? "start" : "loop")}.pap");
        var baseSlot = new AnimationSlot(root, "standing", 0, false, $"{root}/resident/idle.pap");
        var family = new[] { baseSlot, Slot(1), Slot(1, true), Slot(2), Slot(2, true), Slot(6), Slot(6, true) };
        static string Option(AnimationSlot s) => $"Pose {s.Index}";

        var plan = AnimationSlots.Plan(family, [(6, 1)], Option);
        check(plan.Length == 2 && plan[0].Destination.PapPath == $"{root}/emote/pose06_loop.pap" &&
              plan[0].Source.PapPath == $"{root}/emote/pose01_loop.pap" && plan[0].Option == "Pose 1" &&
              plan[1].Destination.Startup && plan[1].Source.Startup && plan[1].Option == plan[0].Option,
            "a slot swap carries the paired startup under the same option as its loop");

        var fromBase = AnimationSlots.Plan(family, [(6, 0)], Option);
        check(fromBase.Length == 1 && fromBase[0].Source.PapPath == $"{root}/resident/idle.pap" &&
              fromBase[0].Destination.PapPath == $"{root}/emote/pose06_loop.pap",
            "the base animation can be swapped into a numbered slot, without inventing a startup for it");
        var ontoBase = AnimationSlots.Plan(family, [(0, 6)], Option);
        check(ontoBase.Length == 1 && ontoBase[0].Destination.PapPath == $"{root}/resident/idle.pap" &&
              ontoBase[0].Source.PapPath == $"{root}/emote/pose06_loop.pap",
            "a numbered slot can be swapped onto the base animation");

        check(AnimationSlots.Plan(family, [(6, 1), (2, 2)], Option).Length == 2,
            "identity mappings are dropped rather than packaged as no-op replacements");
        check(AnimationSlots.Plan(family, [(6, 1), (1, 2)], Option).Select(s => s.Destination.Index).Distinct().Count() == 2,
            "several destinations can be remapped in one plan");
        check(AnimationSlots.Plan([Slot(1), Slot(6)], [(6, 1)], Option).Length == 1,
            "a family without startups does not gain an invented startup path");

        reject(() => AnimationSlots.Plan(family, [(6, 1), (6, 2)], Option), "mapping one destination slot twice is rejected");
        reject(() => AnimationSlots.Plan(family, [(6, 3)], Option), "mapping from a slot outside the family is rejected");
        reject(() => AnimationSlots.Plan(family, [(3, 1)], Option), "mapping onto a slot outside the family is rejected");
        reject(() => AnimationSlots.Plan(family, [(1, 1)], Option), "a plan that swaps nothing is rejected");
        reject(() => AnimationSlots.Plan([], [(6, 1)], Option), "planning a swap with no discovered slots is rejected");
        reject(() => AnimationSlots.Plan([Slot(1), ground with { Index = 6 }], [(6, 1)], Option),
            "a swap crossing two animation families is rejected");

        check(AnimationPresentation.SlotName(Slot(1), 0, "Loop") == "Standing Idle - Loop 1" &&
              AnimationPresentation.SlotName(ground, 0, "Loop") == "Ground Sitting Idle 0 - Loop" &&
              AnimationPresentation.SlotName(chair, 0, "Loop") == "Chair Sitting Idle 0 - Loop" &&
              AnimationPresentation.SlotName(Slot(3) with { Family = "unknown" }, 653, "Loop") == "Ground Sitting Idle 3 - Loop",
            "slot labels come from the same family model the swap planner uses, with the timeline naming unknown families");
    }
}
