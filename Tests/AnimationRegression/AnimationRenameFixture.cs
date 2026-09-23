using System.Text.Json.Nodes;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

/// <summary>
/// The game and Penumbra cannot refresh an animation resource from an unchanged
/// physical path once it has been cached, so an in-place rebake with LivePose or
/// Repair skeleton has to land at a fresh path each time. These fixtures cover the
/// two pieces that make that safe: the file name never grows without bound across
/// repeated edits, and the owning mod's own Files mapping is repointed - not just
/// the physical file - so Penumbra actually resolves to the new path.
/// </summary>
internal static class AnimationRenameFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        check(System.Text.RegularExpressions.Regex.IsMatch(
                  AnimationFileRename.NextRelativePath("files/chara/human/c0101/animation/a0001/bt_common/resident/loop_sp.pap"),
                  @"^files/chara/human/c0101/animation/a0001/bt_common/resident/loop_sp\.ie[0-9a-f]{8}\.pap$"),
            "a fresh rebake appends a short marker before the extension, keeping the rest of the path intact");

        var once = AnimationFileRename.NextRelativePath("loop.pap");
        check(System.Text.RegularExpressions.Regex.IsMatch(once, @"^loop\.ie[0-9a-f]{8}\.pap$"),
            "the marker lands directly before the extension for a path with no directory");

        var twice = AnimationFileRename.NextRelativePath(once);
        check(System.Text.RegularExpressions.Regex.IsMatch(twice, @"^loop\.ie[0-9a-f]{8}\.pap$") && twice != once,
            "re-baking an already-renamed file replaces its marker instead of stacking a second one");

        var notAMarker = AnimationFileRename.NextRelativePath("party.iestandard.pap");
        check(System.Text.RegularExpressions.Regex.IsMatch(notAMarker, @"^party\.iestandard\.ie[0-9a-f]{8}\.pap$"),
            "a filename that merely contains '.ie' without an 8-hex-digit token is not mistaken for our own marker");

        var root = Path.Combine(Path.GetTempPath(), "InstantEditAnimationRenameRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string gamePath = "chara/human/c0101/animation/a0001/bt_common/resident/loop_sp.pap";
            const string otherGamePath = "chara/human/c0101/animation/a0001/bt_common/resident/other.pap";
            const string oldRelative = "files/loop_sp.pap";
            const string newRelative = "files/loop_sp.ie12345678.pap";
            var metaPath = Path.Combine(root, "meta.json");

            var defaultData = new JsonObject
            {
                ["Files"] = new JsonObject { [gamePath] = oldRelative },
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            };
            var meta = PenumbraService.CreateV4ModMetadata("Rename Test", "Author", "Description", "1.0", defaultData);
            var optionFiles = new JsonObject { [gamePath] = oldRelative };
            meta["Groups"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Variant",
                ["Options"] = new JsonArray(new JsonObject
                {
                    ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "On", ["Files"] = optionFiles,
                }),
            });
            File.WriteAllText(metaPath, meta.ToJsonString());

            var stillReferenced = PenumbraService.RenameAnimationFileMapping(root, gamePath, oldRelative, newRelative);
            check(!stillReferenced, "nothing else pointed at the old file, so it is reported safe to delete");
            var updated = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
            check(updated["DefaultData"]!["Files"]![gamePath]!.GetValue<string>() == newRelative,
                "the default mapping is repointed at the new path");
            check(updated["Groups"]![0]!["Options"]![0]!["Files"]![gamePath]!.GetValue<string>() == newRelative,
                "a Single group's option offering the same override is repointed too, since it shares the same physical file");

            reject(() => PenumbraService.RenameAnimationFileMapping(root, gamePath, oldRelative, "files/loop_sp.ieabcdef01.pap"),
                "renaming again from the path it no longer maps to is refused rather than silently doing nothing");

            // A second game path sharing the same physical file must stop the old file from being deleted.
            var sharedRoot = Path.Combine(root, "shared");
            Directory.CreateDirectory(sharedRoot);
            var sharedMeta = PenumbraService.CreateV4ModMetadata("Shared Test", "Author", "Description", "1.0", new JsonObject
            {
                ["Files"] = new JsonObject { [gamePath] = oldRelative, [otherGamePath] = oldRelative },
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            });
            File.WriteAllText(Path.Combine(sharedRoot, "meta.json"), sharedMeta.ToJsonString());
            var sharedStillReferenced = PenumbraService.RenameAnimationFileMapping(sharedRoot, gamePath, oldRelative, newRelative);
            check(sharedStillReferenced, "a different game path still mapped to the old physical file blocks its deletion");
            var sharedUpdated = JsonNode.Parse(File.ReadAllText(Path.Combine(sharedRoot, "meta.json")))!.AsObject();
            check(sharedUpdated["DefaultData"]!["Files"]![gamePath]!.GetValue<string>() == newRelative &&
                  sharedUpdated["DefaultData"]!["Files"]![otherGamePath]!.GetValue<string>() == oldRelative,
                "only the edited game path's own mapping moves; the unrelated one keeps pointing at the original file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
