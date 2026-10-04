using System.Text.Json.Nodes;
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
