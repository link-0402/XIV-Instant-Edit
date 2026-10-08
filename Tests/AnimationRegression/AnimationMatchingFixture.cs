using System.Collections.Immutable;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class AnimationMatchingFixture
{
    public static void Run(Action<bool, string> check)
    {
        const string skeleton = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";
        const string idlePath = "chara/human/c0801/animation/a0001/bt_common/emote/pose05_loop.pap";
        const string residentPath = "chara/human/c0801/animation/a0001/bt_common/resident/idle.pap";
        const string movementAPath = "chara/human/c0801/animation/a0001/bt_common/resident/move_a.pap";
        const string movementBPath = "chara/human/c0801/animation/a0001/bt_common/resident/move_b.pap";
        const string materialPath = "chara/equipment/e0001/animation/a0001/material.pap";
        string[] tree = [skeleton, materialPath, "chara/equipment/e0001/model/c0101e0001_top.mdl"];
        var idle = new AnimationCatalog.Timeline(7405, "emote/pose05_loop", "Idle", 2, true, [7405], []);
        var paths = AnimationCatalog.CandidatePapPaths(idle, tree);
        check(paths.SequenceEqual([idlePath]),
            "idle PAP discovery uses the player skeleton even when Penumbra's tree contains only material PAPs");
        check(AnimationCatalog.CandidatePapPaths(idle, tree.Concat([skeleton, idlePath])).SequenceEqual([idlePath]),
            "known and inferred idle candidates are deduplicated");
        check(AnimationCatalog.SiblingStartupPath(idlePath) == "chara/human/c0801/animation/a0001/bt_common/emote/pose05_start.pap" &&
              AnimationCatalog.SiblingStartupPath("chara/human/c0801/animation/a0001/bt_common/emote/pose05_start.pap") == null,
            "idle loop paths discover only their sibling startup path");
        check(AnimationCatalog.CandidatePapPaths(idle, [materialPath, tree[2]]).IsEmpty,
            "idle discovery cannot guess the player race from equipment or material animation paths");
        var standing = idle with { Id = 3, Key = "normal/idle", Family = [3] };
        check(AnimationCatalog.CandidatePapPaths(standing, tree).SequenceEqual([residentPath]) &&
              AnimationCatalog.PapPath(standing, idlePath, tree) == residentPath &&
              AnimationCatalog.Matches(standing, residentPath, "cbnm_id0"),
            "default standing idle resolves the resident PAP consistently for discovery, matching, and packaging");
        check(AnimationCatalog.CandidatePapPaths(idle with { Key = "../escape" }, tree).IsEmpty &&
              AnimationCatalog.CandidatePapPaths(idle with { Key = "emote/[dynamic]" }, tree).IsEmpty,
            "unresolved or unsafe timeline paths do not become PAP candidates");
        check(AnimationCatalog.CandidatePapPaths(idle with { LoadType = 1 }, tree).IsEmpty,
            "job-specific timelines do not assume the common animation directory");
        var walking = standing with { Id = 13, Key = "normal/walk", Family = [13] };
        check(AnimationCatalog.CandidatePapPaths(walking, tree).SequenceEqual([movementAPath, movementBPath]) &&
              AnimationCatalog.PapPath(walking, movementAPath, tree) == movementAPath &&
              AnimationCatalog.Matches(walking, movementBPath, "cbnm_01f_lp0"),
            "normal walk timelines resolve the shared movement PAPs for discovery, matching, and packaging");
        var face = idle with { Id = 50, Key = "facial/pose/smile", LoadType = 0 };
        string[] faceTree = [.. tree, "chara/human/c0801/skeleton/face/f0003/skl_c0801f0003.sklb"];
        check(face.IsExpression && !idle.IsExpression && !(face with { LoadType = 2 }).IsExpression &&
              AnimationCatalog.CandidatePapPaths(face, faceTree).SequenceEqual([
                  "chara/human/c0801/animation/f0003/nonresident/smile.pap", "chara/human/c0801/animation/f0003/resident/face.pap"]),
            "facial animation discovery uses the loaded face skeleton variant, and the face's resident set");
        // Shut Eyes names no library: cfxf_bow is one of the motions every face has loaded.
        var shutEyes = face with { Key = "facial/pose/bow" };
        check(AnimationCatalog.CandidatePapPaths(shutEyes, faceTree).Contains("chara/human/c0801/animation/f0003/resident/face.pap"),
            "an expression without a face library is found in the face's resident set");
        // A mod's expression may load another library than the one named like the emote.
        check(AnimationCatalog.CandidatePapPaths(face, faceTree, null, ["pandacatte", "../escape", ""])
                .SequenceEqual([
                    "chara/human/c0801/animation/f0003/nonresident/pandacatte.pap",
                    "chara/human/c0801/animation/f0003/nonresident/smile.pap",
                    "chara/human/c0801/animation/f0003/resident/face.pap"]),
            "an expression's own face library is a candidate; unsafe library names are not");
        check(AnimationCatalog.CandidatePapPaths(idle, faceTree, null, ["pandacatte"]).SequenceEqual([idlePath]),
            "face libraries only widen an expression's search, never a body animation's");

        static AnimationPap Pap(string clip)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)1);
            writer.Write((short)801); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(26); writer.Write(66); writer.Write(74);
            var name = Encoding.UTF8.GetBytes(clip);
            writer.Write(name); writer.Write(new byte[32 - name.Length]);
            writer.Write((short)0); writer.Write((short)0); writer.Write(0); writer.Write(new byte[8]);
            return new AnimationPap(stream.ToArray());
        }
        const string motion = "cbem_pose05_2lp";
        var candidate = new AnimationPapCandidate(new AnimationResource(idlePath, "modded-pose05.pap", "source-hash"),
            Pap(motion), ImmutableDictionary<int, string>.Empty.Add(0, "live-fingerprint"));
        var references = new Dictionary<ushort, ImmutableHashSet<string>> { [idle.Id] = [motion] };
        check(AnimationMatching.Find("live-fingerprint", [candidate], [idle], references) is [{ Entry.Binding: 0, Timeline.Id: 7405 }],
            "a modded pose05 loop matches its active timeline and live binding");
        check(AnimationMatching.Find("different-fingerprint", [candidate], [idle], references).IsEmpty,
            "a matching idle path cannot bypass live animation verification");
        var unrelated = candidate with
        {
            Resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap", "other.pap", "other-hash"),
            Pap = Pap("cbem_pose01_2lp"),
        };
        check(AnimationMatching.Find("live-fingerprint", [candidate, unrelated], [idle], references).Length == 1,
            "an identical motion in an unrelated idle does not make the current idle ambiguous");
        var alias = candidate with { Resource = new AnimationResource(idlePath.Replace("c0801", "c0101"), "alias.pap", "source-hash") };
        check(AnimationMatching.Find("live-fingerprint", [candidate, alias], [idle], references).Length == 2,
            "multiple eligible PAP providers remain ambiguous");
        check(AnimationMatching.Find("live-fingerprint", [candidate, candidate], [idle], references).Length == 1,
            "the same candidate discovered twice counts as one match");
        var referenceOnly = candidate with { Resource = candidate.Resource with { GamePath = "chara/custom/idle.pap" } };
        check(AnimationMatching.Find("live-fingerprint", [referenceOnly], [idle], references).Length == 1 &&
              AnimationMatching.Find("live-fingerprint", [referenceOnly], [idle], new Dictionary<ushort, ImmutableHashSet<string>>()).IsEmpty,
            "timeline motion references can identify a custom PAP without guessing from its filename");
        var secondTimeline = idle with { Id = 7407, Key = "emote/pose06_loop" };
        references[secondTimeline.Id] = [motion];
        check(AnimationMatching.Find("live-fingerprint", [candidate], [idle, secondTimeline], references).Length == 2,
            "a binding shared by two active timelines remains ambiguous");

        // move_a holds the walk, run, sprint and jump; each movement row's TMB names its own clip.
        static AnimationPap Paps(params string[] clips)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var havok = 26 + 40 * clips.Length;
            writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)clips.Length);
            writer.Write((short)801); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(26); writer.Write(havok); writer.Write(havok + 8);
            for (var i = 0; i < clips.Length; i++)
            {
                var name = Encoding.UTF8.GetBytes(clips[i]);
                writer.Write(name); writer.Write(new byte[32 - name.Length]);
                writer.Write((short)0); writer.Write((short)i); writer.Write(0);
            }
            writer.Write(new byte[8]);
            return new AnimationPap(stream.ToArray());
        }
        var moveA = new AnimationPapCandidate(new AnimationResource(movementAPath, "move_a.pap", "move-hash"),
            Paps("cbnm_01f_lp0", "cbnm_02f_f", "cbnm_02f_lp0", "cbnm_jump_1", "cbnm_sprint_lp0"),
            ImmutableDictionary<int, string>.Empty.Add(0, "walk").Add(1, "run-start").Add(2, "run").Add(3, "jump").Add(4, "sprint"));
        var run = walking with { Id = 22, Key = "normal/run", Family = [22] };
        var runStart = walking with { Id = 25, Key = "normal/run_start", Family = [25] };
        var jump = walking with { Id = 31, Key = "normal/jump_start", Family = [31] };
        var moves = new Dictionary<ushort, ImmutableHashSet<string>>
        {
            [walking.Id] = ["cbnm_01f_lp0"], [run.Id] = ["cbnm_02f_lp0"], [runStart.Id] = ["cbnm_02f_f"], [jump.Id] = ["cbnm_jump_1", "cfxf_clench"],
        };
        check(AnimationCatalog.IsMovement(run) && AnimationCatalog.IsMovement(jump) && !AnimationCatalog.IsMovement(standing) &&
              AnimationCatalog.CandidatePapPaths(run, tree).SequenceEqual([movementAPath, movementBPath]) &&
              AnimationCatalog.PapPath(jump, movementAPath, tree) == movementAPath,
            "run, sprint and jump rows resolve the shared movement PAPs like the walk");
        check(AnimationMatching.Find("run", [moveA], [run], moves) is [{ Entry.Name: "cbnm_02f_lp0", Timeline.Id: 22 }] &&
              AnimationMatching.Find("jump", [moveA], [jump], moves) is [{ Entry.Name: "cbnm_jump_1", Timeline.Id: 31 }],
            "a running or jumping character is matched to its own clip of move_a");
        check(AnimationMatching.Find("run", [moveA], [run, runStart], moves) is [{ Timeline.Id: 22 }] &&
              AnimationMatching.Find("run-start", [moveA], [run, runStart], moves) is [{ Entry.Name: "cbnm_02f_f", Timeline.Id: 25 }],
            "two movement rows playing at once each keep their own clip instead of both claiming the file");
        check(AnimationMatching.Find("walk", [moveA], [run], moves).IsEmpty &&
              AnimationMatching.Find("sprint", [moveA], [walking, run], moves).IsEmpty,
            "a clip of move_a another movement row plays is not taken for the active row");
        check(AnimationMatching.Find("sprint", [moveA], [run], new Dictionary<ushort, ImmutableHashSet<string>>()) is [{ Entry.Name: "cbnm_sprint_lp0" }] &&
              AnimationMatching.Find("sprint", [moveA], [run], new Dictionary<ushort, ImmutableHashSet<string>> { [run.Id] = ["custom_run"] }) is [{ Entry.Name: "cbnm_sprint_lp0" }],
            "without a TMB naming one of its clips, a movement row still matches any clip of the shared files");
    }
}
