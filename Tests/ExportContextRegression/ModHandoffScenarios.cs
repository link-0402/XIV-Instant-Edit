using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's hand-offs: a file of a mod open in a mod window becomes a row of the one
/// option it is listed in, with the memberships Instant Edit's own meta.json reader gives that
/// option, so an import exports back into that option even when several carry the same file.
/// </summary>
internal static class ModHandoffScenarios
{
    public static void Run(string testRoot)
    {
        const string model = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        const string texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex";
        const string shared = "Files/models/top.mdl";
        const string combined = "Files/models/combined.mdl";

        var root = Path.Combine(testRoot, "HandoffMod");
        Directory.CreateDirectory(root);
        var modId = Guid.NewGuid();
        var styleId = Guid.NewGuid();
        var redId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        var featuresId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = modId,
            ["Name"] = "Handoff Mod",
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject { [texture] = @"Files\textures\top_d.tex" } },
            ["Groups"] = new JsonArray(
                new JsonObject
                {
                    ["Type"] = "Single",
                    ["Id"] = styleId,
                    ["Name"] = "Style",
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = redId, ["Name"] = "Red", ["Files"] = new JsonObject { [model] = shared } },
                        new JsonObject { ["Id"] = blueId, ["Name"] = "Blue", ["Files"] = new JsonObject { [model] = shared } }),
                },
                new JsonObject
                {
                    ["Type"] = "Combining",
                    ["Id"] = featuresId,
                    ["Name"] = "Features",
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = firstId, ["Name"] = "First" },
                        new JsonObject { ["Id"] = secondId, ["Name"] = "Second" }),
                    ["Containers"] = new JsonArray(
                        new JsonObject(), new JsonObject(), new JsonObject(),
                        new JsonObject { ["Files"] = new JsonObject { [model] = combined } }),
                }),
        }.ToJsonString());

        // The files as a mod window lists them: one per option and game path.
        var meta = PenumbraModMeta.Load(root);
        var mod = new ModHandoffMod("Handoff Mod", "HandoffMod", root, ModMetadata.ReadModStableIdentifier(root));
        var files = meta.AllFiles()
            .Select(f => ModHandoffBridge.File(meta, f.GamePath, Path.Combine(root, f.RelPath), f.RelPath, f.Option))
            .ToList();
        var view = ModHandoffBridge.Mod(mod, files, 0);
        Require(view.Entity is null && view.Category == "Mod" && view.ImportObjectIndex == 0 && view.Roots.Count == 4,
            "a mod window's hand-off view lists every file of every option once, with no character of its own");
        Require(mod.StableId == modId, "the mod's stable identifier is its v4 meta.json identifier");

        var defaultRow = view.Roots.Single(r => r.GamePath == texture);
        Require(defaultRow.OptionMemberships.SequenceEqual([ModHandoffBridge.DefaultMembership]) &&
                defaultRow.OptionMapping == "Default" &&
                defaultRow.SourceRelativePath == "Files/textures/top_d.tex" &&
                defaultRow.SourceModRootPath == root && defaultRow.SourceModDirectory == "HandoffMod" &&
                defaultRow.SourceState == ResourceSourceState.LoadedMod && defaultRow.SourceModStableId == modId,
            "a default file is a row of the default files, with its path in the mod in forward slashes");
        Require(ModOptionGroups.ReadOptionMembershipsForRegression(root, "Files/textures/top_d.tex").SequenceEqual(defaultRow.OptionMemberships),
            "the default files' membership is the one Instant Edit reads from meta.json");

        // The same file in two options: each row names its own option only.
        var red = $"option:{styleId:D}:{redId:D}";
        var blue = $"option:{styleId:D}:{blueId:D}";
        var sharedRows = view.Roots.Where(r => r.SourceRelativePath == shared).ToList();
        Require(sharedRows.Count == 2 &&
                sharedRows.Select(r => r.OptionMemberships.Single()).Order().SequenceEqual(new[] { red, blue }.Order()) &&
                sharedRows.Select(r => r.OptionMapping).Order().SequenceEqual(new[] { "Style: Blue", "Style: Red" }),
            "a file two options carry is two rows, each with its own single option");
        Require(ModOptionGroups.ReadOptionMembershipsForRegression(root, shared).Order()
                    .SequenceEqual(sharedRows.SelectMany(r => r.OptionMemberships).Order()),
            "together the rows have exactly the memberships Instant Edit reads for the file");
        foreach (var row in sharedRows)
        {
            var resolved = ModOptionGroups.ResolveSourceOptionForRegression(root, model, shared,
                new SourceOptionLocator { Membership = row.OptionMemberships.Single(), GroupName = "", OptionName = "" });
            Require(resolved.Error is null && resolved.Membership == row.OptionMemberships.Single(),
                "each row's option resolves on its own, so an export goes back into that very option");
        }
        Require(ResourceViews.IsSafeModel(sharedRows[0]), "a mod window's model rows can be sent to Blender");

        // A combining group's container belongs to each option it combines.
        var combinedRow = view.Roots.Single(r => r.SourceRelativePath == combined);
        Require(combinedRow.OptionMemberships.Order().SequenceEqual(new[]
                {
                    $"option:{featuresId:D}:{firstId:D}",
                    $"option:{featuresId:D}:{secondId:D}",
                }.Order()) &&
                ModOptionGroups.ReadOptionMembershipsForRegression(root, combined).Order().SequenceEqual(combinedRow.OptionMemberships.Order()),
            "a combining container's row names the options it combines, as Instant Edit reads them");

        // Groups and options without ids (meta.json before version 4) can't be named to the hand-offs.
        Require(ModHandoffBridge.Memberships("#0", "#1", []).Count == 0 &&
                ModHandoffBridge.Memberships(styleId.ToString(), "#1", []).Count == 0 &&
                ModHandoffBridge.Memberships(featuresId.ToString(), "c#0", [firstId.ToString(), secondId.ToString()]).Count == 0,
            "positional ids and the empty combination give no membership");
    }

    /// <summary>
    /// The Material window's "Save as copy into an option": the places a copy can go, where its files
    /// go in the mod folder, and the write that points the option at them while the original file and
    /// every other entry stay as they are.
    /// </summary>
    public static void RunCopies(string testRoot)
    {
        const string material = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl";
        const string texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_fx.tex";
        const string defaultTexture = "chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex";
        const string shared = @"Files\materials\top_a.mtrl";

        var root = Path.Combine(testRoot, "CopyMod");
        Directory.CreateDirectory(Path.Combine(root, "Files", "materials"));
        File.WriteAllText(Path.Combine(root, shared), "original");
        var styleId = Guid.NewGuid();
        var redId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        var greenId = Guid.NewGuid();
        var featuresId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid(),
            ["Name"] = "Copy Mod",
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject { [defaultTexture] = @"Files\textures\top_d.tex" } },
            ["Groups"] = new JsonArray(
                new JsonObject
                {
                    ["Type"] = "Single",
                    ["Id"] = styleId,
                    ["Name"] = "Style",
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = redId, ["Name"] = "Red", ["Files"] = new JsonObject { [material] = shared } },
                        new JsonObject { ["Id"] = blueId, ["Name"] = "Blue", ["Files"] = new JsonObject { [material] = shared } },
                        new JsonObject { ["Id"] = greenId, ["Name"] = "Green" }),
                },
                new JsonObject
                {
                    ["Type"] = "Combining",
                    ["Id"] = featuresId,
                    ["Name"] = "Features",
                    ["Options"] = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "First" }, new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Second" }),
                    ["Containers"] = new JsonArray(new JsonObject(), new JsonObject(), new JsonObject(), new JsonObject()),
                },
                new JsonObject
                {
                    ["Type"] = "Imc",
                    ["Id"] = Guid.NewGuid(),
                    ["Name"] = "Toggles",
                    ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Hide" }),
                }),
        }.ToJsonString());

        var meta = PenumbraModMeta.Load(root);
        var red = new OptionRef(styleId.ToString(), redId.ToString());
        var blue = new OptionRef(styleId.ToString(), blueId.ToString());
        var green = new OptionRef(styleId.ToString(), greenId.ToString());

        // Where a copy can go: every place that carries files, in meta.json's order.
        var options = ModHandoffCopies.Options(meta, [material, texture], "Files/materials/top_a.mtrl", (option, path) => option == green && path == texture);
        Require(options.Select(o => o.Label).SequenceEqual([
                    "Default files", "Style: Red", "Style: Blue", "Style: Green",
                    "Features: (none)", "Features: First", "Features: Second", "Features: First + Second",
                ]),
            "a copy can go into the default files, every option and every combination, but not an IMC group, which carries no files");
        var byKey = options.ToDictionary(o => OptionRef.Parse(o.Key));
        Require(byKey[red] is { HasFile: true, UsesSource: true, Block: null } && byKey[blue] is { HasFile: true, UsesSource: true }
                && byKey[OptionRef.Default] is { HasFile: false, UsesSource: false, Block: null },
            "the options using the opened file say so, and one without the material has no file there");
        Require(byKey[green].Block is { Length: > 0 } && options.Count(o => o.Block != null) == 1,
            "an option where a file of your own is set up for one of the copy's paths can't take the copy");
        Require(ModHandoffCopies.Options(meta, [material], "Files/other.mtrl", (_, _) => false).All(o => !o.UsesSource),
            "an option using another file at the material's path doesn't use the opened file");

        // Where the files go: where the mod window puts that option's files, or beside it as a copy.
        var natural = $@"Style\Red\{material.Replace('/', '\\')}";
        var free = ModHandoffCopies.Plan(meta, red, [material, texture], _ => false);
        Require(free.Select(p => p.RelPath).SequenceEqual([natural, $@"Style\Red\{texture.Replace('/', '\\')}"]),
            "a copy's files go into the option's folder at their game paths when nothing is there");
        var copyName = $@"Style\Red\chara\equipment\e0001\material\v0001\mt_c0101e0001_top_a_copy.mtrl";
        var busy = ModHandoffCopies.Plan(meta, red, [material], rel => PathNormalizer.SameRel(rel, natural));
        var busier = ModHandoffCopies.Plan(meta, red, [material], rel => PathNormalizer.SameRel(rel, natural) || PathNormalizer.SameRel(rel, copyName));
        Require(busy.Single().RelPath == copyName && busier.Single().RelPath == copyName.Replace("_copy.mtrl", "_copy2.mtrl"),
            "a taken place gets name_copy beside it, then name_copy2");
        Require(ModHandoffCopies.Plan(meta, OptionRef.Default, [material], _ => false).Single().RelPath == material.Replace('/', '\\'),
            "a copy into the default files goes at its game path");
        Require(Throws<InvalidOperationException>(() => ModHandoffCopies.Plan(meta, red, ["../outside/x.mtrl"], _ => false)),
            "a game path that would write outside the mod folder is refused");

        // Writing a copy into Blue: new files, Blue pointed at them, nothing else touched.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, natural))!);
        File.WriteAllText(Path.Combine(root, natural), "a file of the mod");
        var written = ModHandoffCopies.Write(root, meta, blue, [(material, "edited"u8.ToArray()), (texture, "pattern"u8.ToArray())], new HashSet<string>());
        meta.Save(root, backupFirst: false);
        var after = PenumbraModMeta.Load(root);
        var blueMaterial = $@"Style\Blue\{material.Replace('/', '\\')}";
        Require(written.Select(f => f.RelPath).SequenceEqual([blueMaterial, $@"Style\Blue\{texture.Replace('/', '\\')}"])
                && written.All(f => f.DiskPath == Path.Combine(root, f.RelPath))
                && File.ReadAllText(written[0].DiskPath) == "edited" && File.ReadAllText(written[1].DiskPath) == "pattern",
            "the copy's files are written into the option's folder, in the order given");
        Require(after.TryGetFile(blue, material, out var blueRel) && PathNormalizer.SameRel(blueRel, blueMaterial)
                && after.TryGetFile(blue, texture, out var blueTex) && PathNormalizer.SameRel(blueTex, written[1].RelPath),
            "the option the copy went into uses the copy and its new texture");
        Require(after.TryGetFile(red, material, out var redRel) && PathNormalizer.SameRel(redRel, shared) && !after.TryGetFile(red, texture, out _)
                && File.ReadAllText(Path.Combine(root, shared)) == "original" && File.ReadAllText(Path.Combine(root, natural)) == "a file of the mod"
                && after.TryGetFile(OptionRef.Default, defaultTexture, out _) && after.AllFiles().Count() == 4,
            "the original file, the other options and every other entry stay as they were");
        Require(!Directory.EnumerateFiles(root, "*" + PenumbraModMeta.TempSuffix, SearchOption.AllDirectories).Any(), "no half-written file is left behind");

        // Saving another copy into the same option never writes over the first one.
        var again = ModHandoffCopies.Write(root, after, blue, [(material, "edited again"u8.ToArray())], after.ReferencedRelKeys());
        Require(again.Single().RelPath.EndsWith("mt_c0101e0001_top_a_copy.mtrl", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(root, blueMaterial)) == "edited" && File.ReadAllText(again.Single().DiskPath) == "edited again"
                && after.TryGetFile(blue, material, out var againRel) && PathNormalizer.SameRel(againRel, again.Single().RelPath),
            "a second copy goes beside the first, which stays, and the option uses the newer one");

        // A copy that fails leaves nothing behind and meta.json as it was.
        var before = PenumbraModMeta.Load(root);
        var greenTexture = Path.Combine(root, "Style", "Green", texture.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(greenTexture);   // a folder where the texture would go: it can't be written
        var failed = Throws<IOException>(() => ModHandoffCopies.Write(root, before, green, [(material, "x"u8.ToArray()), (texture, "y"u8.ToArray())], new HashSet<string>()));
        Require(failed && !File.Exists(Path.Combine(root, "Style", "Green", material.Replace('/', Path.DirectorySeparatorChar)))
                && !before.TryGetFile(green, material, out _) && !Directory.EnumerateFiles(Path.Combine(root, "Style", "Green"), "*", SearchOption.AllDirectories).Any(),
            "when one file can't be written, the ones written are deleted again and the option is left as it was");
        Require(Throws<InvalidOperationException>(() => ModHandoffCopies.Write(root, before, new OptionRef(styleId.ToString(), Guid.NewGuid().ToString()),
                [(material, "x"u8.ToArray())], new HashSet<string>())),
            "an option that is gone takes no copy");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
