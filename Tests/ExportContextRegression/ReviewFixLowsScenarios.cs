using System.IO.Compression;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using InstantEdit.Studio.Services.Sims;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Review fixes, Low findings: Penumbra event matching, Combining defaults, package imports,
/// cross-item checks by place, model string blocks, the graph's replacement snapshot, texture
/// sizes and formats, option ids with '/', the confirmed single-file remove, folder names and
/// Sims cancellation.
/// </summary>
internal static class ReviewFixLowsScenarios
{
    private const string GroupId = "5b1e7d0a-6a43-4c6e-9f39-0c7f4f9e0a01";
    private const string ComboId = "5b1e7d0a-6a43-4c6e-9f39-0c7f4f9e0a02";
    private const string RedId   = "5b1e7d0a-6a43-4c6e-9f39-0c7f4f9e0a03";
    private const string BlueId  = "5b1e7d0a-6a43-4c6e-9f39-0c7f4f9e0a04";
    private const string Texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex";

    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "review-fix-lows");
        Directory.CreateDirectory(root);
        PenumbraEventsMatchTheFullPath(root);                   // glue-3
        CombiningGroupsDefaultToTheFirstContainer(root);        // glue-5
        ImportsOpenOnlyTheModTheyAskedFor(root);                // glue-7
        SamePathInAnotherOptionIsNoConflict();                  // models-3
        ModelStringBlockIsCheckedBeforeAllocating();            // gamedata-3
        GraphBuildsFromACopyOfTheReplacements(root);            // resources-5
        Bc7DdsRecompresses(root);                               // resources-6
        OversizedImagesAreRefused(root);                        // resources-7
        GroupIdsWithSlashesRoundTrip();                         // resources-8
        RemoveTakesOnlyTheConfirmedFile(root);                  // resui-5
        FolderNamesWindowsCanStore();                           // writers-7
        SimsWorkStopsWhenCancelled(root);                       // sims-8
    }

    // ── glue-3 ──────────────────────────────────────────────────────────────

    private static void PenumbraEventsMatchTheFullPath(string root)
    {
        var modRoot = Path.Combine(root, "Penumbra");
        var backup  = Path.Combine(root, "Backups", "MyMod");
        Require(ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), modRoot, "MyMod")
                && ModLibrary.IsModFolder(Path.Combine(modRoot, "mymod") + Path.DirectorySeparatorChar, modRoot + Path.DirectorySeparatorChar, "MyMod"),
            "a mod folder in Penumbra's mod directory matches its event, whatever the case or trailing separator");
        Require(!ModLibrary.IsModFolder(backup, modRoot, "MyMod"),
            "a folder of the same name outside Penumbra's mod directory is not the mod Penumbra deleted or renamed");
        Require(!ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), null, "MyMod") && !ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), modRoot, ""),
            "without Penumbra's mod directory (or a name) nothing matches");
    }

    // ── glue-5 ──────────────────────────────────────────────────────────────

    private static void CombiningGroupsDefaultToTheFirstContainer(string root)
    {
        var mod = WriteMod(Path.Combine(root, "combining"));
        var meta = PenumbraModMeta.Load(mod);
        var none = ActiveSettings.Map(meta, new Dictionary<string, List<string>>());
        Require(none.Contains(new OptionRef(ComboId, "c#0")) && !none.Any(o => o.Group == GroupId),
            "a Combining group missing from the settings applies its first container; a Single group with nothing picked applies nothing");
        var empty = ActiveSettings.Map(meta, new Dictionary<string, List<string>> { ["Combo"] = new() });
        Require(empty.Contains(new OptionRef(ComboId, "c#0")), "a Combining group with nothing picked applies its first container");
        var picked = ActiveSettings.Map(meta, new Dictionary<string, List<string>> { ["Combo"] = new() { "B" }, ["Look"] = new() { "Blue" } });
        Require(picked.Contains(new OptionRef(ComboId, "c#2")) && !picked.Contains(new OptionRef(ComboId, "c#0")) && picked.Contains(new OptionRef(GroupId, BlueId)),
            "picked options still map to their container and option");
    }

    // ── glue-7 ──────────────────────────────────────────────────────────────

    private static void ImportsOpenOnlyTheModTheyAskedFor(string root)
    {
        var package = Path.Combine(root, "cool_top_v2.pmp");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("meta.json").Open()))
            writer.Write(new JObject { ["FileVersion"] = 3, ["Name"] = "Cool Top: Red" }.ToString());
        Require(ModLibrary.PackageModName(package) == "Cool Top: Red", "the mod name is read from the package's meta.json");
        var notZip = Path.Combine(root, "old.ttmp");
        File.WriteAllText(notZip, "{\"Name\":\"x\"}");
        Require(ModLibrary.PackageModName(notZip) == null, "a package that isn't a zip gives no name");

        var import = new ModLibrary.PendingImport("cool_top_v2", "Cool Top: Red", DateTime.UtcNow);
        Require(ModLibrary.MatchesImport(import, "Cool Top_ Red", "Cool Top: Red"), "the imported mod matches by its name");
        Require(ModLibrary.MatchesImport(import, "Cool Top_ Red (2)", "Cool Top: Red"), "a numbered folder of the same mod matches");
        Require(!ModLibrary.MatchesImport(import, "Other Mod", "Other Mod") && !ModLibrary.MatchesImport(import, "Cool Top_ Red (copy)", "Cool Top: Red (copy)"),
            "an unrelated mod, or a copy made meanwhile, is not taken for the import");
        var byFile = new ModLibrary.PendingImport("Cool Hair", null, DateTime.UtcNow);
        Require(ModLibrary.MatchesImport(byFile, "Cool Hair", null) && !ModLibrary.MatchesImport(byFile, "Hat", "Hat"),
            "without a name in the package the file name is matched");
    }

    // ── models-3 ────────────────────────────────────────────────────────────

    private static void SamePathInAnotherOptionIsNoConflict()
    {
        var race = new RaceGender(PlayerRace.Midlander, PlayerGender.Male);
        var first  = new PortItem(new FeatureSubject(SubjectKind.Hair, race, 5));
        var second = new PortItem(new FeatureSubject(SubjectKind.Hair, race, 6));
        const string path = "chara/human/c0101/obj/hair/h0005/material/v0001/mt_c0101h0005_hir_a.mtrl";
        var target = new Target(TargetKind.Material, second.Key, 0);

        var writtenBy = new Dictionary<string, PortItem>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<PortIssue>();
        PortValidator.ValidateAcrossItems(first, [("group/red", path, new Target(TargetKind.Material, first.Key, 0))], writtenBy, paths, issues);
        PortValidator.ValidateAcrossItems(second, [("group/blue", path, target)], writtenBy, paths, issues);
        Require(issues.Count == 0 && paths.Contains(path), "two items writing one path into different options don't overwrite each other");

        PortValidator.ValidateAcrossItems(second, [("group/red", path, target)], writtenBy, paths, issues);
        Require(issues.Count == 1 && issues[0].Severity == Severity.Error, "the same path in the same option is still an error");
    }

    // ── gamedata-3 ──────────────────────────────────────────────────────────

    private static void ModelStringBlockIsCheckedBeforeAllocating()
    {
        var bytes = new byte[68 + 8 + 16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x01000006u);
        BitConverter.TryWriteBytes(bytes.AsSpan(72, 4), 0x40000000u);   // a 1 GB string block in a 92-byte file
        Exception? error = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { ModelMaterialRewriter.ReadMaterialsStrict(bytes); }
        catch (Exception ex) { error = ex; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(error is InvalidDataException && allocated < 16 * 1024 * 1024,
            $"a model whose string block runs past the file is refused before allocating it ({allocated / 1024} KB, {error?.GetType().Name})");
    }

    // ── resources-5 ─────────────────────────────────────────────────────────

    private static void GraphBuildsFromACopyOfTheReplacements(string root)
    {
        var mod = Path.Combine(root, "graph-mod");
        Directory.CreateDirectory(mod);
        var slot = new TextureSlot { Type = TextureType.Diffuse, Postfix = "d", SourcePath = Path.Combine(root, "d.png") };
        var material = new MaterialSetup { Name = "mt_c0101e0001_top_a" };
        material.Textures.Add(slot);
        var live = new ResourceReplacement
        {
            GamePath = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl",
            Authored = material,
            IsNew    = true,
        };

        var snapshot = new ReplacementSnapshot([live]);
        var copy = snapshot.Copies.Single();
        Require(!ReferenceEquals(copy, live) && !ReferenceEquals(copy.Authored, live.Authored) && !ReferenceEquals(copy.Authored!.Textures, live.Authored!.Textures),
            "the worker gets its own copy of each replacement and its slot list");
        live.Authored.Textures.Add(new TextureSlot { Type = TextureType.Normal, Postfix = "n" });
        Require(copy.Authored.Textures.Count == 1, "a slot added while the graph builds doesn't reach the worker's copy");

        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod, Files = [], Swaps = [], Replacements = snapshot.Copies },
            new ModFileFactsCache(), new NoGameFiles(), CancellationToken.None);
        snapshot.Relink(graph);
        var materialNode = graph.Nodes.Values.FirstOrDefault(n => n.GamePath == live.GamePath);
        var textureNode = graph.Nodes.Values.FirstOrDefault(n => n.AuthoredSlot != null);
        Require(materialNode != null && ReferenceEquals(materialNode.Replacement, live), "the built graph points at the live replacement, which the views edit");
        Require(textureNode != null && ReferenceEquals(textureNode.AuthoredSlot, slot), "and its texture at the live slot");
    }

    private sealed class NoGameFiles : IVanillaFiles
    {
        public bool Exists(string gamePath) => false;
        public byte[]? TryReadBytes(string gamePath) => null;
    }

    // ── resources-6 ─────────────────────────────────────────────────────────

    private static void Bc7DdsRecompresses(string root)
    {
        var bc7 = Path.Combine(root, "index.dds");
        File.WriteAllBytes(bc7, Dx10Dds(8, 8, dxgi: 98));
        Require(TextureConverter.Convert(bc7, TextureCompression.Bc5, out var tex, out var error) && tex.Length > 80,
            $"a BC7 .dds is decoded and recompressed to BC5 ({error})");
        Require(TextureConverter.Convert(bc7, TextureCompression.Bc3, out _, out error), $"and to BC3 ({error})");

        var bc6 = Path.Combine(root, "hdr.dds");
        File.WriteAllBytes(bc6, Dx10Dds(8, 8, dxgi: 95));
        Require(!TextureConverter.Convert(bc6, TextureCompression.Bc7, out _, out error) && error.Contains("BC6H", StringComparison.Ordinal),
            $"a BC6H .dds says plainly why it can't be recompressed ({error})");
    }

    /// <summary>A DX10 .dds with one mip of mode-6 BC7/BC6H blocks.</summary>
    private static byte[] Dx10Dds(int width, int height, uint dxgi)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);
        bw.Write(124);
        bw.Write(0x1007u);
        bw.Write(height);
        bw.Write(width);
        bw.Write(0);
        bw.Write(0);
        bw.Write(1);
        bw.Write(new byte[44]);
        bw.Write(32);
        bw.Write(0x4u);
        bw.Write(0x30315844u);          // DX10
        bw.Write(new byte[20]);
        bw.Write(0x1000u);
        bw.Write(new byte[16]);
        bw.Write(dxgi);
        bw.Write(3u);
        bw.Write(0u);
        bw.Write(1u);
        bw.Write(0u);
        for (int i = 0; i < (width / 4) * (height / 4); i++)
        {
            var block = new byte[16];
            block[0] = 0x40;
            bw.Write(block);
        }
        return ms.ToArray();
    }

    // ── resources-7 ─────────────────────────────────────────────────────────

    private static void OversizedImagesAreRefused(string root)
    {
        var wide = Path.Combine(root, "wide.png");
        using (var image = new Image<Rgba32>(TextureConverter.MaxSide + 1, 1))
            image.SaveAsPng(wide);
        Require(!TextureConverter.Convert(wide, TextureCompression.None, out _, out var error) && error.Contains("16384", StringComparison.Ordinal),
            $"an image wider than the game takes is refused with a clear message ({error})");

        var dds = Dx10Dds(4, 4, dxgi: 98);
        BitConverter.TryWriteBytes(dds.AsSpan(12, 4), 70000);   // height past what the .tex header holds
        var big = Path.Combine(root, "big.dds");
        File.WriteAllBytes(big, dds);
        Require(!TextureConverter.Convert(big, TextureCompression.None, out _, out error) && error.Contains("16384", StringComparison.Ordinal),
            $"a .dds too large for the game is refused before its data is read ({error})");

        var fine = Path.Combine(root, "fine.png");
        using (var image = new Image<Rgba32>(8, 8))
            image.SaveAsPng(fine);
        Require(TextureConverter.Convert(fine, TextureCompression.None, out var tex, out error) && tex.Length == 80 + 8 * 8 * 4,
            $"an image of a normal size still converts ({error})");
    }

    // ── resources-8 ─────────────────────────────────────────────────────────

    private static void GroupIdsWithSlashesRoundTrip()
    {
        var slashed = new OptionRef("Top/Bottom", "Red");
        var parsed = OptionRef.Parse(slashed.Serialize());
        Require(parsed.Group == "Top/Bottom" && parsed.Option == "Red", $"a group id with '/' reads back as written ({slashed.Serialize()})");
        var other = new OptionRef("Top", "Bottom/Red");
        Require(slashed.Serialize() != other.Serialize() && OptionRef.Parse(other.Serialize()) == other,
            "it no longer collides with another group's option");
        var percent = new OptionRef("50%2F", "x");
        Require(OptionRef.Parse(percent.Serialize()).Group == "50%2F", "a '%' in a group id survives too");

        Require(OptionRef.Parse($"{GroupId}/{RedId}") == new OptionRef(GroupId, RedId) && OptionRef.Parse("#2/#0") == new OptionRef("#2", "#0")
                && OptionRef.Parse("#1/c#3") == new OptionRef("#1", "c#3"),
            "refs saved before (GUIDs, positions, containers) read back the same");
        Require(new OptionRef(GroupId, RedId).Serialize() == $"{GroupId}/{RedId}", "and are written the same as before");
    }

    // ── resui-5 ─────────────────────────────────────────────────────────────

    private static void RemoveTakesOnlyTheConfirmedFile(string root)
    {
        var mod = WriteMod(Path.Combine(root, "remove"));
        var meta = PenumbraModMeta.Load(mod);
        var red = new OptionRef(GroupId, RedId);
        Require(meta.TryGetFile(red, Texture, out var current), "the fixture's option has the texture");

        var gone = ModSession.RemoveFileIfStill(meta, red, Texture, @"textures\elsewhere.tex", out bool changed);
        Require(gone == null && changed && meta.TryGetFile(red, Texture, out _),
            "an entry that points at another file than the one confirmed is left in place");

        var removed = ModSession.RemoveFileIfStill(meta, red, Texture, current.Replace('\\', '/'), out changed);
        Require(removed == current && !changed && !meta.TryGetFile(red, Texture, out _), "the confirmed file is taken out");
    }

    // ── writers-7 ───────────────────────────────────────────────────────────

    private static void FolderNamesWindowsCanStore()
    {
        Require(PortSession.SanitizeFolderName("Top: Red/Blue") == "Top_ Red_Blue", "invalid characters still become '_'");
        Require(PortSession.SanitizeFolderName("Bunny Suit Ver. ") == "Bunny Suit Ver" && PortSession.SanitizeFolderName("Name...") == "Name",
            "trailing dots and spaces go, as Windows would drop them");
        Require(PortSession.SanitizeFolderName("CON") == "_CON" && PortSession.SanitizeFolderName("nul.txt") == "_nul.txt" && PortSession.SanitizeFolderName("COM1") == "_COM1",
            "device names get a '_' in front");
        Require(PortSession.SanitizeFolderName("Console") == "Console" && PortSession.SanitizeFolderName("...") == "Mod",
            "other names are left alone; a name of only dots falls back");
    }

    // ── sims-8 ──────────────────────────────────────────────────────────────

    private static void SimsWorkStopsWhenCancelled(string root)
    {
        var source = Path.Combine(root, "sims");
        Directory.CreateDirectory(source);
        var package = Path.Combine(source, "swatches.package");
        ReviewFixSimsScenarios.WritePackage(package,
            new ReviewFixSimsScenarios.Res(SimsResourceType.ImgOverlay, 1, ReviewFixSimsScenarios.UncompressedDds(4, 4, pixels: 16)),
            new ReviewFixSimsScenarios.Res(SimsResourceType.ImgOverlay, 2, ReviewFixSimsScenarios.UncompressedDds(4, 4, pixels: 16)),
            new ReviewFixSimsScenarios.Res(SimsResourceType.ImgOverlay, 3, ReviewFixSimsScenarios.UncompressedDds(4, 4, pixels: 16)));

        using (var cts = new CancellationTokenSource())
        {
            Exception? error = null;
            try { SimsPackageInspector.Scan(package, line => { if (line.StartsWith("Decoding", StringComparison.Ordinal)) cts.Cancel(); }, cts.Token); }
            catch (Exception ex) { error = ex; }
            Require(error is OperationCanceledException, $"a scan stops when cancelled ({error?.GetType().Name})");
        }

        var scan = SimsPackageInspector.Scan(package);
        var output = Path.Combine(root, "sims-out");
        using (var cts = new CancellationTokenSource())
        {
            int calls = 0;
            Exception? error = null;
            try { SimsPackageInspector.Extract(scan, output, progress: _ => { if (++calls == 1) cts.Cancel(); }, cancel: cts.Token); }
            catch (Exception ex) { error = ex; }
            var diffuse = Directory.GetFiles(output, "*.png", SearchOption.AllDirectories);
            Require(error is OperationCanceledException && diffuse.Length < 3,
                $"an extraction stops between files when cancelled ({error?.GetType().Name}, {diffuse.Length} written)");
            Require(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
                "what it wrote is still recorded as its own");
        }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>A v4 mod: a Single group "Look" (Red, Blue) and a Combining group "Combo" (A, B).</summary>
    private static string WriteMod(string mod)
    {
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Lows",
            ["DefaultData"] = new JObject { ["Files"] = new JObject() },
            ["Groups"] = new JArray
            {
                new JObject
                {
                    ["Id"] = GroupId, ["Name"] = "Look", ["Type"] = "Single", ["DefaultSettings"] = 0,
                    ["Options"] = new JArray
                    {
                        new JObject { ["Id"] = RedId, ["Name"] = "Red", ["Files"] = new JObject { [Texture] = @"Look\Red\top_d.tex" } },
                        new JObject { ["Id"] = BlueId, ["Name"] = "Blue", ["Files"] = new JObject { [Texture] = @"Look\Blue\top_d.tex" } },
                    },
                },
                new JObject
                {
                    ["Id"] = ComboId, ["Name"] = "Combo", ["Type"] = "Combining", ["DefaultSettings"] = 0,
                    ["Options"] = new JArray { new JObject { ["Name"] = "A" }, new JObject { ["Name"] = "B" } },
                    ["Containers"] = new JArray
                    {
                        new JObject { ["Files"] = new JObject { [Texture] = @"Combo\none.tex" } },
                        new JObject { ["Files"] = new JObject() },
                        new JObject { ["Files"] = new JObject() },
                        new JObject { ["Files"] = new JObject() },
                    },
                },
            },
        }.ToString());
        return mod;
    }
}
