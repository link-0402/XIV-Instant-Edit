using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class AnimationSlotFixture
{
    public static void Run(Action<bool, string> check)
    {
        const string root = "chara/human/c0801/animation/a0001/bt_common";

        var loop = AnimationSlots.Describe($"{root}/emote/pose01_loop.pap");
        var start = AnimationSlots.Describe($"{root}/emote/pose01_start.pap");
        check(loop is { Family: "standing", Index: 1, Startup: false } &&
              start is { Family: "standing", Index: 1, Startup: true },
            "a numbered pose path decodes into its family, slot number and loop or startup state");

        // Each family also has an unnumbered base member in another directory: jmn.pap
        // is ground-sit 0.
        var ground = AnimationSlots.Describe($"{root}/resident/jmn.pap");
        var chair = AnimationSlots.Describe($"{root}/resident/sit.pap");
        var standing = AnimationSlots.Describe($"{root}/resident/idle.pap");
        check(ground is { Family: "ground", Index: 0, Startup: false } &&
              chair is { Family: "chair", Index: 0 } && standing is { Family: "standing", Index: 0 },
            "the unnumbered base animations decode as slot zero of their own family");
        check(AnimationSlots.Describe($"{root}/emote/j_pose02_loop.pap") is { Family: "ground" } &&
              AnimationSlots.Describe($"{root}/emote/s_pose02_loop.pap") is { Family: "chair" },
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

        check(AnimationPresentation.SlotName(new AnimationSlot("standing", 1, false), 0, "Loop") == "Standing Idle - Loop 1" &&
              AnimationPresentation.SlotName(ground!, 0, "Loop") == "Ground Sitting Idle 0 - Loop" &&
              AnimationPresentation.SlotName(chair!, 0, "Loop") == "Chair Sitting Idle 0 - Loop" &&
              AnimationPresentation.SlotName(new AnimationSlot("unknown", 3, false), 653, "Loop") == "Ground Sitting Idle 3 - Loop",
            "slot labels come from the family model, with the timeline naming unknown families");
    }
}
