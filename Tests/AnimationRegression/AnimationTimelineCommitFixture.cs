using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

/// <summary>
/// A timeline edit can add files, such as an imported sound, next to the animation it edits.
/// In place, every option that maps the animation must map the added file too, or the sound
/// would play in one option and be missing in another; undo removes exactly those mappings.
/// </summary>
internal static class AnimationTimelineCommitFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(AnimationCommitService.AddedGamePath("sound/instant_edit/drama_b8124175.scd") &&
              AnimationCommitService.AddedGamePath("vfx/instant_edit/bow_0011aabb.avfx") &&
              AnimationCommitService.AddedGamePath("vfx/axie/texture/erp.atex"),
            "imported sounds, effects and their textures can be added");
        check(!AnimationCommitService.AddedGamePath("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl") &&
              !AnimationCommitService.AddedGamePath("sound/../x.scd") && !AnimationCommitService.AddedGamePath("sound/x y.scd"),
            "models, escaping paths and unsafe names are never added");

        var root = Path.Combine(Path.GetTempPath(), "InstantEditTimelineCommitRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string pap = "chara/human/c0101/animation/a0001/bt_common/emote/laugh.pap";
            const string other = "chara/human/c0101/animation/a0001/bt_common/emote/clap.pap";
            const string sound = "sound/instant_edit/drama_b8124175.scd";
            const string relative = "instant_edit/sound/instant_edit/drama_b8124175.scd";
            var metaPath = Path.Combine(root, "meta.json");
            var meta = PenumbraService.CreateV4ModMetadata("Timeline Test", "Author", "Description", "1.0", new JsonObject
            {
                ["Files"] = new JsonObject { [pap] = "files/laugh.pap" },
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            });
            meta["Groups"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Style",
                ["Options"] = new JsonArray(
                    new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Loud", ["Files"] = new JsonObject { [pap] = "files/loud.pap" } },
                    new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Other", ["Files"] = new JsonObject { [other] = "files/clap.pap" } }),
            }, new JsonObject
            {
                ["Type"] = "Multi", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Toggles",
                ["Options"] = new JsonArray(
                    new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Music", ["Files"] = new JsonObject { ["vfx/mod/bgm.avfx"] = "toggles/bgm.avfx" } }),
            });
            File.WriteAllText(metaPath, meta.ToJsonString());

            PenumbraService.AddAnimationResourceMapping(root, pap, sound, relative);
            var added = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
            check(added["DefaultData"]!["Files"]![sound]!.GetValue<string>() == relative &&
                  added["Groups"]![0]!["Options"]![0]!["Files"]![sound]!.GetValue<string>() == relative,
                "an added sound is mapped in the default files and in the option that maps the animation");
            check(added["Groups"]![0]!["Options"]![1]!["Files"]![sound] is null,
                "an option that does not map the animation does not get the sound");
            reject(() => PenumbraService.AddAnimationResourceMapping(root, pap, sound, "instant_edit/other.scd"),
                "a game path the mod already maps is never mapped twice");
            reject(() => PenumbraService.AddAnimationResourceMapping(root, "chara/missing.pap", "sound/instant_edit/x_00000000.scd", "instant_edit/x.scd"),
                "a file can't be added for an animation the mod no longer maps");

            PenumbraService.RemoveAnimationResourceMapping(root, sound, relative);
            var removed = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
            check(removed["DefaultData"]!["Files"]![sound] is null && removed["Groups"]![0]!["Options"]![0]!["Files"]![sound] is null &&
                  removed["DefaultData"]!["Files"]![pap]!.GetValue<string>() == "files/laugh.pap",
                "undo removes every mapping of the added file and keeps the animation's own");
            var before = File.ReadAllText(metaPath);
            PenumbraService.RemoveAnimationResourceMapping(root, sound, relative);
            check(File.ReadAllText(metaPath) == before, "removing a mapping that is already gone changes nothing");

            // A looping copy of an effect follows its original's toggle, not the animation's options.
            const string copy = "vfx/instant_edit/bgm_loop_0a1b2c3d.avfx";
            const string copyRelative = "instant_edit/vfx/instant_edit/bgm_loop_0a1b2c3d.avfx";
            check(PenumbraService.ModMapsGamePath(root, "VFX/Mod/BGM.avfx") && !PenumbraService.ModMapsGamePath(root, copy) &&
                  !PenumbraService.ModMapsGamePath(Path.Combine(root, "missing"), "vfx/mod/bgm.avfx"),
                "a mod maps a game path when any of its options does");
            PenumbraService.AddAnimationResourceMapping(root, "vfx/mod/bgm.avfx", copy, copyRelative);
            var beside = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
            check(beside["Groups"]![1]!["Options"]![0]!["Files"]![copy]!.GetValue<string>() == copyRelative &&
                  beside["DefaultData"]!["Files"]![copy] is null && beside["Groups"]![0]!["Options"]![0]!["Files"]![copy] is null,
                "a copy is mapped only in the options that map its original");
            PenumbraService.RemoveAnimationResourceMapping(root, copy, copyRelative);
            check(JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]![1]!["Options"]![0]!["Files"]!.AsObject().Count == 1,
                "undo removes the copy's mapping and keeps the original's");
            check(PenumbraService.AnimationMappedFiles(root, "vfx/mod/bgm.avfx").SequenceEqual(["toggles/bgm.avfx"]) &&
                  AnimationEditService.OwnCopies(root, "vfx/mod/bgm.avfx", Path.Combine(root, "toggles", "bgm.avfx"), Reverse, _ => { }) == null,
                "an original the mod maps to one file keeps one copy, mapped wherever it is, as before");

            PerFileCopies(root, check, reject);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Reverse(byte[] bytes) => [.. bytes.Reverse()];

    /// <summary>
    /// An effect the mod maps to a different file in each option ("Gold", "Silver"): each option gets a copy
    /// made from its own file, at the copy's one game path, and an option without the effect gets none.
    /// </summary>
    private static void PerFileCopies(string parent, Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(parent, "PerFile");
        Directory.CreateDirectory(Path.Combine(root, "fx"));
        const string effect = "vfx/mod/aura.avfx";
        const string copy = "vfx/instant_edit/aura_loop_0a1b2c3d.avfx";
        const string gold = "instant_edit/vfx/instant_edit/aura_loop_0a1b2c3d.avfx";
        const string silver = "instant_edit/vfx/instant_edit/aura_loop_0a1b2c3d_2.avfx";
        File.WriteAllBytes(Path.Combine(root, "fx", "gold.avfx"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "fx", "silver.avfx"), [4, 5, 6]);
        var meta = PenumbraService.CreateV4ModMetadata("Per File", "Author", "Description", "1.0", new JsonObject
        {
            ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
        });
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Effect",
            ["Options"] = new JsonArray(
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Gold", ["Files"] = new JsonObject { [effect] = "fx\\gold.avfx" } },
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Silver", ["Files"] = new JsonObject { [effect] = "fx/silver.avfx" } },
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "None", ["Files"] = new JsonObject() }),
        });
        var metaPath = Path.Combine(root, "meta.json");
        File.WriteAllText(metaPath, meta.ToJsonString());

        var skipped = new List<string>();
        var copies = AnimationEditService.OwnCopies(root, effect, Path.Combine(root, "fx", "gold.avfx"), Reverse, skipped.Add);
        check(PenumbraService.AnimationMappedFiles(root, effect).SequenceEqual(["fx/gold.avfx", "fx/silver.avfx"]) &&
              copies is { From: "fx/gold.avfx" } && copies.Others.Count == 1 &&
              copies.Others["fx/silver.avfx"].SequenceEqual(new byte[] { 6, 5, 4 }) && skipped.Count == 0,
            "a copy made from the gold file is the gold options' own; the silver file gets a copy made the same way");
        var none = AnimationEditService.OwnCopies(root, effect, Path.Combine(root, "fx", "gold.avfx"), null, skipped.Add);
        check(none is { From: "fx/gold.avfx", Others.Count: 0 } && skipped.Single().Contains("fx/silver.avfx"),
            "without a way to make it again, the silver options get no copy rather than the gold one");

        PenumbraService.AddAnimationResourceMapping(root, effect, copy, gold, "fx/gold.avfx");
        PenumbraService.AddAnimationResourceMapping(root, effect, copy, silver, "fx/silver.avfx");
        var options = JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]![0]!["Options"]!;
        check(options[0]!["Files"]![copy]!.GetValue<string>() == gold && options[1]!["Files"]![copy]!.GetValue<string>() == silver &&
              options[2]!["Files"]![copy] is null,
            "each option maps the copy's one game path to the copy of its own file; an option without the effect gets none");
        reject(() => PenumbraService.AddAnimationResourceMapping(root, effect, copy, "instant_edit/other.avfx", "fx/gold.avfx"),
            "an option that maps the copy already is never mapped twice");
        reject(() => PenumbraService.AddAnimationResourceMapping(root, effect, "vfx/instant_edit/x_00000000.avfx", "instant_edit/x.avfx", "fx/bronze.avfx"),
            "a copy of a file no option maps any more is refused");
        PenumbraService.RemoveAnimationResourceMapping(root, copy, silver);
        options = JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]![0]!["Options"]!;
        check(options[0]!["Files"]![copy]!.GetValue<string>() == gold && options[1]!["Files"]![copy] is null,
            "undo removes each option's copy by its own file");

        // Activation: the copy's game path resolves to the copy of the file in use; the others are only checked intact.
        AnimationFileChange Added(string relative, string from) => new(copy, Path.Combine(root, relative), "Per File", root, relative,
            "", "after", "", "staged", AddedFor: effect, AddedForFile: from);
        var validated = new List<string>();
        AnimationCommitService.VerifyActivationAsync([Added(gold, "fx/gold.avfx"), Added(silver, "fx/silver.avfx")],
            _ => Task.FromResult(Path.Combine(root, gold)), file => validated.Add(file.RelativePath), () => Task.CompletedTask).GetAwaiter().GetResult();
        check(validated.SequenceEqual([gold, silver]), "activation resolves the copy once, to the copy of the file in use, and checks every copy");
        reject(() => AnimationCommitService.VerifyActivationAsync([Added(gold, "fx/gold.avfx")],
                _ => Task.FromResult(Path.Combine(root, silver)), _ => { }, () => Task.CompletedTask).GetAwaiter().GetResult(),
            "activation still fails when the copy resolves to another option's copy");
    }
}
