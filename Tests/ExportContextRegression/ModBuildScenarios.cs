using System.Text.Json.Nodes;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Stash;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's Build mode: a build only makes new mods, variant files of different items never
/// share a file, explicit mesh-part material picks follow their material when the materials list
/// changes, two items may write one path into different options, and mod folder names Windows can store.
/// </summary>
internal static class ModBuildScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "ModBuild");
        Directory.CreateDirectory(root);

        ExistingFolderIsRefused(root);              // writers-1/-2
        LeftoversAreRemoved(root);
        MetaIsReplacedWhole(root);
        EmptyDescriptionStaysEmpty(root);
        WeaponPaths();
        WeaponKeys();
        WeaponImcIsWrittenAsWeapon(root);
        UnknownKeyTagsResolveToNothing();
        WeaponVanillaMaterialFallsBackToV1();
        WeaponHintsDontMentionRaces();
        ModWindowsTakeWeaponsNotAnimations();
        VariantNamesAreBuildWide();              // writers-3
        MaterialLinksFollowTheirMaterials();        // models-1
        SharedMaterialsAreCopiedForTheItem();
        TextureVariantsArePickedFiles(root);
        StashRowsAreMarkedByKind();
        SamePathInAnotherOptionIsNoConflict();      // models-3
        FolderNamesWindowsCanStore();               // writers-7
        VanillaModelsCanBeSwitchedOff();
        PartTagsMakeAnImcGroup(root);
        MaterialsAreFittedToTheModel(root);
        OtherSubjectsFitWithTheirPreset(root);
        FitsTakeTheFirstModel();
        FitsApplyOnlyWhileStillWanted(root);
        FitReadsTheModelAndTheStash(root);
        LaterFitReplacesTheEarlierOne();
        FitLooksInTheStashFolders();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Materials fitted to a model
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<TexturePurpose, string> NoTextures = new Dictionary<TexturePurpose, string>();

    private static string FitFile(string root, string name)
    {
        var path = Path.Combine(root, "fit", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private static string Types(MaterialSetup material) => string.Join(", ", material.Textures.Select(t => t.Type));

    private static bool Placeholder(MaterialSetup material, TextureType type)
        => material.Textures.Single(t => t.Type == type) is { UseWhiteDummy: true, DummyPreset: "", SourcePath: "" };

    /// <summary>
    /// A model dropped on gear gets one material per material its meshes name, in mesh order, without
    /// the shared body groups: "Gear with Diffuse" where the stash has a diffuse for it, else "Gear",
    /// and placeholders in the slots nothing fills. They line up with the meshes the way Auto links do.
    /// </summary>
    private static void MaterialsAreFittedToTheModel(string root)
    {
        var topDiffuse = FitFile(root, "top_d.png");
        var topNormal  = FitFile(root, "top_n.png");
        var glovesNormal = FitFile(root, "gloves_n.png");
        var found = new Dictionary<string, IReadOnlyDictionary<TexturePurpose, string>>
        {
            ["/Top.mtrl"]    = new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = topDiffuse, [TexturePurpose.Normal] = topNormal },
            ["/Gloves.mtrl"] = new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = Path.Combine(root, "fit", "gone.png"), [TexturePurpose.Normal] = glovesNormal },
        };
        var asked = new List<string>();
        IReadOnlyDictionary<TexturePurpose, string> TexturesFor(string name)
        {
            asked.Add(name);
            return found.GetValueOrDefault(name) ?? NoTextures;
        }

        var subject = new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body });
        var female = RaceInfo.BaseFor(PlayerGender.Female);
        var meshes = new[] { "/Top.mtrl", "/bibo.mtrl", "/Gloves.mtrl", "/top.mtrl", "/Plain.mtrl" };
        var fit = MaterialFit.Fit(meshes, TexturesFor, subject, female, otherPreset: null);

        Require(fit.ModelNames.SequenceEqual(new[] { "/Top.mtrl", "/Gloves.mtrl", "/Plain.mtrl" }) && asked.SequenceEqual(fit.ModelNames),
            $"one material per name the meshes use, in mesh order, once each, without shared body groups ({string.Join(", ", fit.ModelNames)})");
        Require(fit.Materials.Select(m => m.Name).SequenceEqual(new[] { "mt_c0201e6000_top_a", "mt_c0201e6000_top_b", "mt_c0201e6000_top_c" }),
            $"named as + Material names them, for the naming race ({string.Join(", ", fit.Materials.Select(m => m.Name))})");

        var top = fit.Materials[0];
        Require(top.ShaderType == ShaderType.Character && Types(top) == "Diffuse, Normal, Mask, Index",
            $"a diffuse in the stash makes \"Gear with Diffuse\" ({Types(top)})");
        Require(top.Textures[0].SourcePath == topDiffuse && top.Textures[1].SourcePath == topNormal && top.Textures.Take(2).All(t => !t.UseWhiteDummy)
                && Placeholder(top, TextureType.Mask) && Placeholder(top, TextureType.Index),
            "its diffuse and normal come from the stash; the mask and index get the role's default placeholder");
        Require(top.Textures.All(t => t.Compression == TextureCompression.Auto), "every slot starts on Auto");

        var gloves = fit.Materials[1];
        Require(Types(gloves) == "Normal, Mask, Index" && gloves.Textures[0].SourcePath == glovesNormal && Placeholder(gloves, TextureType.Mask),
            $"a diffuse that isn't on disk makes \"Gear\"; the normal still comes from the stash ({Types(gloves)})");
        var plain = fit.Materials[2];
        Require(Types(plain) == "Normal, Mask, Index" && plain.Textures.All(t => t.UseWhiteDummy),
            "nothing found makes \"Gear\" with placeholders in every slot");
        Require(fit.WithDiffuse == 1 && fit.WithTextures == 2, $"the counts for the message ({fit.WithDiffuse} with diffuse, {fit.WithTextures} with textures)");

        var written = ModelMaterialLinks.WrittenNames(subject, fit.Materials, female);
        var plan = ModelMaterialLinks.Plan(meshes, null, written);
        Require(plan.SequenceEqual(new[] { written[0], null, written[1], written[0], written[2] }),
            $"left on Auto, each mesh lands on the material fitted to its own name ({string.Join(", ", plan.Select(p => p ?? "keep"))})");

        Require(MaterialFit.Fit(new[] { "/bibo.mtrl", "/bibopube.mtrl" }, TexturesFor, subject, female, null).Materials.Count == 0,
            "a model naming only shared body groups gets no materials");
        var weapon = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        var hair = MaterialPresets.All.First(p => p.Name == "Hair");
        var weaponFit = MaterialFit.Fit(new[] { "/Top.mtrl" }, TexturesFor, weapon, null, hair);
        Require(weaponFit.Materials.Single() is { Name: "mt_w0201b0001_a", ShaderType: ShaderType.Character } only && Types(only) == "Diffuse, Normal, Mask, Index",
            "a weapon fits like gear, whatever preset is passed for other subjects");
    }

    /// <summary>Hair, faces, tails and ears get the preset the + Material menu suggests for them, passed in; without one, the gear rule.</summary>
    private static void OtherSubjectsFitWithTheirPreset(string root)
    {
        var normal  = FitFile(root, "hair_n.png");
        var diffuse = FitFile(root, "hair_d.png");
        IReadOnlyDictionary<TexturePurpose, string> TexturesFor(string name)
            => new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = diffuse, [TexturePurpose.Normal] = normal };
        var female = RaceInfo.BaseFor(PlayerGender.Female);
        var hairSubject = new FeatureSubject(SubjectKind.Hair, female, 5);

        var hair = MaterialFit.Fit(new[] { "/Hair.mtrl" }, TexturesFor, hairSubject, female, MaterialPresets.All.First(p => p.Name == "Hair")).Materials.Single();
        Require(hair.ShaderType == ShaderType.Hair && Types(hair) == "Normal, Mask" && hair.Name == hairSubject.DefaultMaterialName(1, female),
            $"a hair takes the preset passed in, and no diffuse slot is added for the stash's diffuse ({Types(hair)}, {hair.Name})");
        Require(hair.Textures[0].SourcePath == normal && Placeholder(hair, TextureType.Mask), "its normal comes from the stash");

        var tail = MaterialFit.Fit(new[] { "/Tail.mtrl" }, TexturesFor, new FeatureSubject(SubjectKind.Tail, female, 1), female, otherPreset: null);
        Require(tail.Materials.Single() is { ShaderType: ShaderType.Character } fallback && Types(fallback) == "Diffuse, Normal, Mask, Index" && tail.WithDiffuse == 1,
            "a subject with no suggested preset fits like gear");
    }

    // ── The fit asked for on a Build item, worked out off the render thread ──

    private static GearSubject FitGear() => new(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body });

    private static MaterialFitRequest FitRequest(PortItem item, string model)
        => new(item.Key, model, item.Materials.ToArray(), item.Subject, RaceInfo.BaseFor(PlayerGender.Female), null);

    private static MaterialFitResult Fitted(int materials, int withDiffuse)
        => new(Enumerable.Range(0, materials).Select(i => new MaterialSetup { Name = $"m{i}" }).ToList(),
               Enumerable.Range(0, materials).Select(i => $"/m{i}.mtrl").ToList(), withDiffuse, withDiffuse);

    /// <summary>A model dropped or picked on an item without materials fits them, from the first file of several; "Fit to model" uses the first race's file.</summary>
    private static void FitsTakeTheFirstModel()
    {
        var item = new PortItem(FitGear());
        Require(MaterialFitJobs.FileToFit(item, new[] { @"C:\m\Top.mdl", @"C:\m\Top_b.mdl" }) == @"C:\m\Top.mdl",
            "of several model files set at once, the fit uses the first");
        Require(MaterialFitJobs.FileToFit(item, Array.Empty<string>()) == null, "no file, no fit");
        item.Materials.Add(new MaterialSetup { Name = "mine" });
        Require(MaterialFitJobs.FileToFit(item, new[] { @"C:\m\Top.mdl" }) == null, "an item with materials keeps them when a model is set");

        var models = new PortItem(FitGear());
        Require(MaterialFitJobs.ModelFile(models) == null, "without a race model, Fit to model has no file");
        models.Models.Add(new RaceModelEntry { UseDummy = true, SourcePath = @"C:\m\Old.mdl" });
        models.Models.Add(new RaceModelEntry());
        models.Models.Add(new RaceModelEntry { UseVariants = true });
        Require(MaterialFitJobs.ModelFile(models) == null, "a generated dummy, a race without a file and an empty variant set give none");
        models.Models.Add(new RaceModelEntry { UseVariants = true, VariantPaths = { @"C:\m\A.mdl", @"C:\m\B.mdl" } });
        models.Models.Add(new RaceModelEntry { SourcePath = @"C:\m\Top.mdl" });
        Require(MaterialFitJobs.ModelFile(models) == @"C:\m\A.mdl", "the first race with a file gives it: of variants, the first");
    }

    /// <summary>
    /// A finished fit goes in only while its item still wants it: still in the pack, with the very materials it had
    /// when the fit was asked for, and still built from that model, unchanged since it was read.
    /// </summary>
    private static void FitsApplyOnlyWhileStillWanted(string root)
    {
        const string model = @"C:\m\Top.mdl";
        var item = new PortItem(FitGear());
        item.Models.Add(new RaceModelEntry { SourcePath = model });
        var request = FitRequest(item, model);
        Require(MaterialFitJobs.StillWanted(request, item), "a fit goes in while its item has no materials and builds from the model");
        Require(MaterialFitJobs.StillWanted(request with { ModelPath = model.ToUpperInvariant() }, item), "whatever the path's case");
        Require(!MaterialFitJobs.StillWanted(request, null), "not once the item left the pack");
        Require(!MaterialFitJobs.StillWanted(request with { Item = 99 }, item), "not on another item");
        Require(MaterialFitJobs.TakesSelection(request, new Target(TargetKind.Item, item.Key))
                && MaterialFitJobs.TakesSelection(request, new Target(TargetKind.Model, item.Key, 0)),
            "a fit that goes in selects its first material while the item, or something in it, is selected");
        Require(!MaterialFitJobs.TakesSelection(request, new Target(TargetKind.Item, 99)) && !MaterialFitJobs.TakesSelection(request, new Target(TargetKind.Build))
                && !MaterialFitJobs.TakesSelection(request, null),
            "a late fit never pulls the selection back from elsewhere");

        item.Materials.Add(new MaterialSetup { Name = "mine" });
        Require(!MaterialFitJobs.StillWanted(request, item), "not once the item has a material of its own");

        // "Fit to model" over two materials: it replaces those two, and only while they are still the item's.
        item.Materials.Add(new MaterialSetup { Name = "theirs" });
        var replace = FitRequest(item, model);
        var asked = item.Materials.ToList();
        Require(MaterialFitJobs.StillWanted(replace, item), "Fit to model replaces the materials it was asked to replace");
        asked[0].ShaderType = ShaderType.Skin;
        Require(MaterialFitJobs.StillWanted(replace, item), "the same materials, edited in place, are still the ones it replaces");
        item.Materials.Add(new MaterialSetup { Name = "another" });
        Require(!MaterialFitJobs.StillWanted(replace, item), "not once one was added meanwhile");
        item.Materials.RemoveAt(0);
        Require(!MaterialFitJobs.StillWanted(replace, item), "nor once one was removed and another added, the count the same");
        item.Materials.Clear();
        item.Materials.AddRange(new[] { asked[0].Clone(), asked[1].Clone() });
        Require(!MaterialFitJobs.StillWanted(replace, item), "nor once they were replaced by others alike (Match vanilla materials, a paste)");
        item.Materials.Clear();
        item.Materials.AddRange(new[] { asked[1], asked[0] });
        Require(!MaterialFitJobs.StillWanted(replace, item), "nor once they were moved");
        item.Materials.Clear();

        item.Models[0].SourcePath = @"C:\m\Other.mdl";
        Require(!MaterialFitJobs.StillWanted(request, item), "not once the race builds from another file");
        item.Models[0].UseVariants = true;
        item.Models[0].VariantPaths.Add(model);
        Require(MaterialFitJobs.StillWanted(request, item), "a variant set holding the file still builds from it");
        item.Models[0].UseDummy = true;
        Require(!MaterialFitJobs.StillWanted(request, item), "a generated dummy doesn't");

        // Why a wanted fit isn't applied, and what it says once it is.
        var file = Path.Combine(root, "fit", "Stamped.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        var stamp = ModelStamp.Of(file);
        var three = new MaterialFitOutcome(Fitted(3, 2), null, stamp);
        Require(stamp != null && MaterialFitJobs.Problem(three, file, ModelStamp.Of(file)) == null, "a fit of a file unchanged since it was read goes in");
        File.AppendAllText(file, "more");
        var written = MaterialFitJobs.Problem(three, file, ModelStamp.Of(file));
        Require(written == "Stamped.mdl is still being written. Try again in a moment.", $"a file written again since it was read isn't fitted ({written})");
        Require(MaterialFitJobs.Problem(new MaterialFitOutcome(null, "Could not read Top.mdl (file not found).", null), model, null) == "Could not read Top.mdl (file not found).",
            "a read that failed says why");
        var none = MaterialFitJobs.Problem(new MaterialFitOutcome(Fitted(0, 0), null, stamp), file, stamp);
        Require(none == "No materials found in Stamped.mdl.", $"a model naming no materials of its own says so ({none})");

        var messages = new[] { MaterialFitJobs.Message(Fitted(3, 2), model), MaterialFitJobs.Message(Fitted(1, 1), model), MaterialFitJobs.Message(Fitted(2, 0), model) };
        Require(messages[0] == "Set up 3 materials from Top.mdl, 2 with diffuse.", $"the message names the counts and the file ({messages[0]})");
        Require(messages[1] == "Set up 1 material from Top.mdl, 1 with diffuse.", $"one material is one ({messages[1]})");
        Require(messages[2] == "Set up 2 materials from Top.mdl, none with diffuse.", $"no diffuse is none ({messages[2]})");
        var starting = MaterialFitJobs.Starting(model);
        Require(starting == "Setting up materials from Top.mdl.", $"while it runs, the status says so ({starting})");
        Require(messages.Concat(new[] { written, none, starting }).All(m => m != null && !m.Contains('…') && !m.Contains("...")), "no message has an ellipsis");
    }

    /// <summary>
    /// The fit off the render thread: the model's own material names, the stash's textures for them; a model
    /// that can't be read (being written, broken, gone) fits nothing and says why.
    /// </summary>
    private static void FitReadsTheModelAndTheStash(string root)
    {
        var stash = Path.Combine(root, "fit-stash");
        var outfit = Path.GetFullPath(Path.Combine(stash, "Outfit"));
        if (Directory.Exists(outfit))
            Directory.Delete(outfit, true);
        var diffuse = Path.Combine(outfit, "Textures", "top_d.png");
        Directory.CreateDirectory(Path.GetDirectoryName(diffuse)!);
        File.WriteAllBytes(diffuse, new byte[] { 1 });
        var manifest = new StashManifest { Source = @"C:\p\Outfit.unitypackage", SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };
        manifest.Entries.Add(new StashEntry { Path = "Textures/top_d.png", Kind = StashKind.Texture, Purpose = TexturePurpose.Diffuse });
        manifest.Materials.Add(new StashMaterial { Name = "Top", Textures = { [TexturePurpose.Diffuse] = "Textures/top_d.png" } });
        manifest.Write(outfit);
        var model = Path.Combine(outfit, "Blender", "Top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, ModCheckFixtures.Mdl(["/Top.mtrl", "/bibo.mtrl", "/Plain.mtrl"]));

        var item = new PortItem(FitGear());
        item.Models.Add(new RaceModelEntry { SourcePath = model });
        var request = FitRequest(item, model);
        MaterialFitOutcome Run(string path) => MaterialFitJobs.Run(request with { ModelPath = path }, new[] { stash }, Array.Empty<string>(), CancellationToken.None);

        var outcome = Run(model);
        Require(outcome is { Problem: null, Fit: { Materials.Count: 2, WithDiffuse: 1 } } && outcome.Stamp == ModelStamp.Of(model),
            $"the model's own materials are fitted, with the stash's textures ({outcome.Problem ?? $"{outcome.Fit?.Materials.Count} materials"})");
        Require(outcome.Fit!.Materials[0].Textures.Any(t => t.Type == TextureType.Diffuse && t.SourcePath == diffuse)
                && outcome.Fit.Materials.Select(m => m.Name).SequenceEqual(new[] { "mt_c0201e6000_top_a", "mt_c0201e6000_top_b" }),
            "named for the naming race, the stash's diffuse in the first");
        Require(MaterialFitJobs.Problem(outcome, model, ModelStamp.Of(model)) == null && MaterialFitJobs.Message(outcome.Fit, model) == "Set up 2 materials from Top.mdl, 1 with diffuse.",
            "it goes in, and says so");

        using (new FileStream(model, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var held = Run(model);
            Require(held is { Fit: null, Problem: "Top.mdl is still being written. Try again in a moment." },
                $"a model another program is writing isn't fitted, and says so in short ({held.Problem})");
        }
        var broken = Path.Combine(outfit, "Blender", "Broken.mdl");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3 });
        Require(Run(broken).Problem?.StartsWith("Could not read Broken.mdl (", StringComparison.Ordinal) == true, "nor one that isn't a model");
        var gone = Run(Path.Combine(outfit, "Gone.mdl")).Problem;
        Require(gone == "Could not read Gone.mdl (file not found).", $"nor one that isn't there ({gone})");

        // Dropped or picked, only a stash model is fitted: one from anywhere else keeps its own material paths.
        var loose = Path.Combine(root, "fit-loose", "Top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllBytes(loose, ModCheckFixtures.Mdl(["/mt_c0201e6111_top_a.mtrl"]));
        var dropped = MaterialFitJobs.Run(request with { ModelPath = loose, OnlyFromStash = true }, new[] { stash }, Array.Empty<string>(), CancellationToken.None);
        Require(dropped is { NotInStash: true, Fit: null, Problem: null },
            $"a model dropped from outside the stash is left alone, quietly ({dropped.Problem ?? $"{dropped.Fit?.Materials.Count} materials"})");
        var fitAnyway = MaterialFitJobs.Run(request with { ModelPath = loose }, new[] { stash }, Array.Empty<string>(), CancellationToken.None);
        Require(fitAnyway is { NotInStash: false, Problem: null, Fit.Materials.Count: 1 }, "Fit to model fits it all the same");
        var fromStash = MaterialFitJobs.Run(request with { OnlyFromStash = true }, new[] { stash }, Array.Empty<string>(), CancellationToken.None);
        Require(fromStash is { NotInStash: false, Fit.Materials.Count: 2 }, "a stash model dropped is fitted");
        var brokenLoose = Path.Combine(root, "fit-loose", "Broken.mdl");
        File.WriteAllBytes(brokenLoose, new byte[] { 1, 2, 3 });
        Require(MaterialFitJobs.Run(request with { ModelPath = brokenLoose, OnlyFromStash = true }, new[] { stash }, Array.Empty<string>(), CancellationToken.None)
                is { NotInStash: true, Problem: null },
            "nor does a broken model from outside the stash warn when dropped");
    }

    private static FinishedFit? NextFit(MaterialFitJobs jobs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (jobs.TakeFinished(out var done))
                return done;
            Thread.Sleep(5);
        }
        return null;
    }

    /// <summary>One fit per item: asked again, the earlier one is cancelled and never comes back. Another item's runs beside it.</summary>
    private static void LaterFitReplacesTheEarlierOne()
    {
        var jobs = new MaterialFitJobs();
        Require(!jobs.TakeFinished(out _), "nothing comes back while no fit runs");
        var top = new PortItem(FitGear());
        var hair = new PortItem(new FeatureSubject(SubjectKind.Hair, RaceInfo.BaseFor(PlayerGender.Female), 5));
        var found = new MaterialFitOutcome(Fitted(1, 0), null, null);
        using var earlierStarted = new ManualResetEventSlim();
        using var earlierStopped = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        jobs.Start(FitRequest(top, "first.mdl"), cancel =>
        {
            earlierStarted.Set();
            while (!cancel.IsCancellationRequested)
                Thread.Sleep(5);
            earlierStopped.Set();
            cancel.ThrowIfCancellationRequested();
            return found;
        });
        // Once it runs: one cancelled before it started would just never run.
        Require(earlierStarted.Wait(TimeSpan.FromSeconds(10)), "a fit runs off the render thread");
        var second = FitRequest(top, "second.mdl");
        jobs.Start(second, _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return found;
        });
        var other = FitRequest(hair, "hair.mdl");
        jobs.Start(other, _ => found);

        Require(NextFit(jobs) is { Outcome: not null } first && first.Request == other, "another item's fit runs beside it and comes back");
        Require(earlierStopped.Wait(TimeSpan.FromSeconds(10)), "asking again for the same item cancels the earlier fit");
        release.Set();
        Require(NextFit(jobs) is { } later && later.Request == second && later.Outcome == found, "the later one comes back");
        Require(!jobs.TakeFinished(out _), "the earlier one never does");

        var failing = FitRequest(top, "broken.mdl");
        jobs.Start(failing, _ => throw new InvalidDataException("bad"));
        Require(NextFit(jobs) is { Outcome: null, Error: InvalidDataException } failed && failed.Request == failing, "a fit that throws comes back with what it threw");
    }

    /// <summary>The fit looks in the folders the stash lists: the importer's output folder and the cache's package-imports.</summary>
    private static void FitLooksInTheStashFolders()
    {
        const string cache = @"C:\Cache\InstantEdit", temp = @"C:\Temp\";
        const string imports = @"C:\Cache\InstantEdit\package-imports";
        Require(PackageImportPaths.StashRoots("", cache, temp).SequenceEqual(new[] { imports }), "without a picked output folder, the cache's package-imports");
        Require(PackageImportPaths.StashRoots(@"D:\Extracted", cache, temp).SequenceEqual(new[] { @"D:\Extracted", imports }),
            "a picked output folder, and package-imports, where Blender saves");
        Require(PackageImportPaths.StashRoots(imports + @"\", cache, temp).Count == 1, "package-imports picked by hand is listed once");
        Require(PackageImportPaths.StashRoots("", null, temp).SequenceEqual(new[] { @"C:\Temp\InstantEdit\package-imports" }),
            "without a cache, the folder the import window falls back to");
    }

    private static ModImcOverride Variant(EquipSlot slot, byte variant, byte vfx = 0)
        => new() { Slot = slot, SetId = 6000, Variant = variant, DecalId = 0, VfxId = vfx, MaterialAnimationId = 0, AttributeMask = 0x3FF, SoundId = 0 };

    /// <summary>
    /// A gear model's parts tagged with the slot's variant attributes get an IMC group, as the Blender
    /// add-on's Attribute Toggle Group makes: one option per tag, all on, the group over the mod's own entries.
    /// </summary>
    private static void PartTagsMakeAnImcGroup(string root)
    {
        var variants = new List<ModImcOverride> { Variant(EquipSlot.Body, 1), Variant(EquipSlot.Body, 2) };
        var plan = PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000,
            new[] { "atr_tv_c", "atr_tv_a", "atr_nv_b", "atr_top", "atr_tv_k", "atrx_glow" }, variants, forcesMaterial: true, priority: 1);
        var group = plan.Group;
        Require(group is { Imc: { OnlyAttributes: false, Slot: EquipSlot.Body, SetId: 6000 }, Priority: 1, DefaultSettings: 3 } && plan.Problem == null,
            "a body's atr_tv_ tags make an IMC group with every part on, which points the variants at v0001 like the rest of the build");
        Require(group!.Options.Select(o => (o.Name, o.AttributeMask)).SequenceEqual(new[] { ("A", (ushort)1), ("C", (ushort)4) }),
            "one option per tag, named after its suffix and switching that suffix's bit");
        Require(plan.OtherSlotTags.SequenceEqual(new[] { "atr_nv_b" }) && PartToggleGroups.Renamed(EquipSlot.Body, "atr_nv_b") == "atr_tv_b",
            "another slot's tag is reported with the name it needs; the game's own and custom tags are left alone");
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, variants, true, 1).Group!.Id == group.Id,
            "the group keeps its id from build to build");

        var mixed = new List<ModImcOverride> { Variant(EquipSlot.Body, 1), Variant(EquipSlot.Body, 2, vfx: 3) };
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, mixed, true, 1) is { Group: null, Problem: not null },
            "variants with different effects get no group when the build points them at v0001: it would undo them");
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, variants, true, 1, inOption: true) is { Group: null, Problem: not null } &&
                PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, variants, false, 1, inOption: true).Group?.Imc?.OnlyAttributes == true,
            "an item in an option gets no group when the build points the variants at v0001: the always-on group would do it with the option off; without materials it still gets one");
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, mixed, false, 1).Group?.Imc?.OnlyAttributes == true,
            "without materials of its own the group changes only the mask, and every variant keeps the game's entry");
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_tv_a" }, null, false, 1) is { Group: null, Problem: not null },
            "an item without an IMC file gets no group, and says why");
        Require(PartToggleGroups.Plan("Top Parts", EquipSlot.Body, 6000, new[] { "atr_top" }, variants, true, 1) is { Group: null, Problem: null },
            "a model without part tags gets no group and no warning");
        Require(PartToggleGroups.Plan("Ring Parts", EquipSlot.RingLeft, 6000, new[] { "atr_rv_b" }, new List<ModImcOverride> { Variant(EquipSlot.RingLeft, 1) }, true, 1).Group != null,
            "a left ring toggles atr_rv_ tags, as both rings do in game");

        var mod = Path.Combine(root, "Part Toggles");
        Directory.CreateDirectory(mod);
        ModMetaWriter.Write(mod, "Part Toggles", new ModCarrier(), new[] { group });
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!["Groups"]![0]!;
        Require((string?)written["Type"] == "Imc" && (bool?)written["AllVariants"] == true && (bool?)written["OnlyAttributes"] == false
                && (int?)written["Priority"] == 1 && (int?)written["DefaultSettings"] == 3,
            "the group is written as Penumbra's IMC group, over every variant");
        var identifier = written["Identifier"]!;
        Require((int?)identifier["PrimaryId"] == 6000 && (string?)identifier["ObjectType"] == "Equipment" && (string?)identifier["EquipSlot"] == "Body"
                && (string?)identifier["BodySlot"] == "Unknown" && (int?)identifier["Variant"] == 1,
            "its identifier names the item's set and slot");
        Require((int?)written["DefaultEntry"]?["MaterialId"] == 1 && (int?)written["DefaultEntry"]?["AttributeMask"] == 0,
            "its entry points at v0001 and leaves the mask to the options");
        Require((int?)written["Options"]?[1]?["AttributeMask"] == 4 && written["Options"]?[1]?["Files"] == null,
            "its options carry masks and no files");
    }

    /// <summary>
    /// A gear race with its own vanilla model (Miqo'te female) can be switched off, so it falls back
    /// to the Midlander female model the port writes (the build's Eqdp entry: BlenderStatusRegression).
    /// </summary>
    private static void VanillaModelsCanBeSwitchedOff()
    {
        var miqoF = new RaceGender(PlayerRace.Miqote, PlayerGender.Female);
        var midF  = new RaceGender(PlayerRace.Midlander, PlayerGender.Female);
        var midM  = RaceInfo.BaseFor(PlayerGender.Male);
        var native = new HashSet<RaceGender> { midM, midF, miqoF };

        var item = new PortItem(new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body }));
        item.Models.Add(new RaceModelEntry { Race = midF.Race, Gender = midF.Gender });
        Require(!VanillaModels.IsOff(item, miqoF), "every vanilla model starts on");
        Require(!VanillaModels.CanTurnOff(item, midF) && !VanillaModels.CanTurnOff(item, midM),
            "a race the port writes, and Midlander male, can't be switched off");

        item.Meta.VanillaModelsOff.Add(miqoF.RaceCode);
        Require(VanillaModels.IsOff(item, miqoF) && VanillaModels.Off(item, native).SequenceEqual(new[] { miqoF }),
            "unticked, Miqo'te female's vanilla model is off");
        Require(VanillaModels.FallbackRace(item, miqoF, native) == midF, "and it falls back to the port's Midlander female model");
        Require(item.Meta.Clone().VanillaModelsOff.Contains(miqoF.RaceCode) && !item.Meta.IsEmpty, "the choice is saved with the item's metadata");
    }

    private static void ExistingFolderIsRefused(string root)
    {
        var fresh = Path.Combine(root, "Not Built Yet");
        Require(BuildTarget.Problem(fresh) == null, "a build may write into a mod folder that does not exist yet");

        var empty = Path.Combine(root, "Empty Folder");
        Directory.CreateDirectory(empty);
        Require(BuildTarget.Problem(empty) != null, "a build refuses an existing folder even when it is empty");

        var built = Path.Combine(root, "Built Mod");
        Directory.CreateDirectory(built);
        File.WriteAllText(Path.Combine(built, "meta.json"), "{\"FileVersion\":4,\"Name\":\"Built Mod\"}");
        var problem = BuildTarget.Problem(built);
        Require(problem != null && problem.Contains("Open it from the library", StringComparison.Ordinal),
            "a build refuses an existing mod (its own earlier build included) and points to editing it");
        Require(!problem!.Contains('…'), "the refusal has no ellipsis character");
    }

    private static void LeftoversAreRemoved(string root)
    {
        var half = Path.Combine(root, "Half Built");
        var nested = Path.Combine(half, "chara", "equipment", "e0001", "texture");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "a.tex"), new byte[] { 1, 2, 3 });
        var readOnly = Path.Combine(half, "copied.mdl");
        File.WriteAllBytes(readOnly, new byte[] { 4 });
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        var error = ModBuilder.DeleteLeftovers(half);
        Require(error == null && !Directory.Exists(half), "a cancelled or failed build removes its half-built folder, read-only copies included");
        Require(ModBuilder.DeleteLeftovers(half) == null, "removing a folder that is already gone is not an error");
    }

    private static void MetaIsReplacedWhole(string root)
    {
        var mod = Path.Combine(root, "Meta Mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), "old");

        var defaults = new ModCarrier();
        defaults.Files.Add(new KeyValuePair<string, string>("chara/x.tex", "chara\\x.tex"));
        ModMetaWriter.Write(mod, "Meta Mod", defaults, Array.Empty<ModGroupOut>());

        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        Require((string?)meta["Name"] == "Meta Mod" && meta["DefaultData"]?["Files"]?["chara/x.tex"] != null,
            "meta.json is written whole");
        Require(!File.Exists(Path.Combine(mod, "meta.json.tmp")), "no temp file is left beside meta.json");
    }

    private static void EmptyDescriptionStaysEmpty(string root)
    {
        var mod = Path.Combine(root, "No Description");
        Directory.CreateDirectory(mod);
        ModMetaWriter.Write(mod, "No Description", new ModCarrier(), Array.Empty<ModGroupOut>(), new ModMetaInfo { Description = "" });
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        Require((string?)meta["Description"] == "", "an empty pack description is written empty, not as \"Port of ...\"");
    }

    private static void WeaponPaths()
    {
        Require(WeaponNaming.ModelGamePath(201, 1) == "chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl", "a weapon's model path");
        Require(WeaponNaming.MaterialGamePath(201, 1, "mt_w0201b0001_a") == "chara/weapon/w0201/obj/body/b0001/material/v0001/mt_w0201b0001_a.mtrl",
            "a weapon's material path, in the v0001 folder the build always writes");
        Require(WeaponNaming.ImcGamePath(201, 1) == "chara/weapon/w0201/obj/body/b0001/b0001.imc", "a weapon's IMC path");
        Require(WeaponNaming.DefaultMaterialName(201, 1, 2) == "mt_w0201b0001_b", "a weapon's second material is named _b");
        Require(WeaponNaming.TextureGamePath(201, 1, "mt_w0201b0001_a_n") == "chara/weapon/w0201/obj/body/b0001/texture/mt_w0201b0001_a_n.tex",
            "a weapon's texture path");

        var subject = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        Require(subject.ModelGamePath(RaceInfo.BaseFor(PlayerGender.Female)) == WeaponNaming.ModelGamePath(201, 1),
            "a weapon has one model whatever the race");
        Require(subject is { Kind: SubjectKind.Weapon, UsesEqdp: false, UsesImc: true, MultiRace: false, HasRaces: false, IdDisplay: "w0201b0001" },
            "a weapon uses IMC but no EQDP and no races");
        Require(subject.MaterialRaces(new List<RaceModelEntry>()).SequenceEqual(new RaceGender?[] { null }), "a weapon writes one race-shared material set");
    }

    private static void WeaponVanillaMaterialFallsBackToV1()
    {
        Require(WeaponNaming.VanillaMaterialCandidates(201, 1, 3, "mt_w0201b0001_a").SequenceEqual(new[]
            {
                "chara/weapon/w0201/obj/body/b0001/material/v0003/mt_w0201b0001_a.mtrl",
                "chara/weapon/w0201/obj/body/b0001/material/v0001/mt_w0201b0001_a.mtrl",
            }), "a weapon's vanilla material is looked for in its variant's folder, then in v0001, where most variants keep theirs");
        Require(WeaponNaming.VanillaMaterialCandidates(201, 1, 1, "mt_w0201b0001_a").Count == 1, "variant 1 looks in v0001 once");
        Require(WeaponNaming.VanillaMaterialCandidates(201, 1, 0, "mt_w0201b0001_a").Single().Contains("/v0001/", StringComparison.Ordinal),
            "a variant of 0 counts as 1");
        var subject = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        Require(subject.VanillaMaterialPaths("/mt_w0201b0001_a.mtrl", RaceInfo.BaseFor(PlayerGender.Male)).Count() == 2,
            "the weapon subject offers both folders");
    }

    private static void WeaponHintsDontMentionRaces()
    {
        var weapon = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        Require(!weapon.NoVanillaModelHint.Contains("race", StringComparison.OrdinalIgnoreCase) && weapon.NoVanillaModelHint.Contains("weapon", StringComparison.Ordinal),
            "a weapon without a vanilla model is described without races");
        var hair = new FeatureSubject(SubjectKind.Hair, RaceInfo.BaseFor(PlayerGender.Female), 5);
        Require(hair.NoVanillaModelHint.Contains("race", StringComparison.Ordinal), "a feature without a vanilla model still names the race");
    }

    private static void ModWindowsTakeWeaponsNotAnimations()
    {
        var weapon = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        Require(AddedItemModels.WhyNot(weapon) == null && AddedItemModels.Paths(weapon, Array.Empty<RaceGender>()).SequenceEqual(new[] { WeaponNaming.ModelGamePath(201, 1) }),
            "a weapon dropped on a mod window is added with its one model");
        var emote = new AnimationSubject(new AnimationTarget(0x2000_0010, "Joy", 0, "/joy", new[] { new AnimationTimelineRef(3, "emote/joy", "joy", true, null) }));
        Require(AddedItemModels.WhyNot(emote) is { } why && why.Contains("Build", StringComparison.Ordinal) && !why.Contains("no model", StringComparison.Ordinal),
            "an animation dropped on a mod window says it is set up in the Build window, not that the game has no model");
        var race = RaceInfo.BaseFor(PlayerGender.Female);
        var hair = new FeatureSubject(SubjectKind.Hair, race, 5);
        Require(AddedItemModels.Paths(hair, new[] { race }).SequenceEqual(new[] { hair.ModelGamePath(race) }), "a hairstyle is added for its native races");
    }

    private static void WeaponKeys()
    {
        var main = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        var off  = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, true));
        Require(main.Key == 4321u, "a main-hand weapon is keyed by its item row id, like gear");
        Require(off.Key == (0x4000_0000u | 4321u), "an off-hand model sets the off-hand bit");
        Require((main.Key & 0x8000_0000u) == 0 && (off.Key & 0x8000_0000u) == 0, "weapon keys never look like feature keys");
        Require(off.DisplayName == "Bronze Gladius (off hand)", "an off-hand model says so in its name");
    }

    private static void WeaponImcIsWrittenAsWeapon(string root)
    {
        var mod = Path.Combine(root, "Weapon Imc");
        Directory.CreateDirectory(mod);
        var defaults = new ModCarrier();
        defaults.Imc.Add(new ModImcOverride
        {
            Object = ImcObject.OffHand, SetId = 201, SecondaryId = 1, Variant = 3,
            DecalId = 0, VfxId = 2, MaterialAnimationId = 0, AttributeMask = 0x3FF, SoundId = 0,
        });
        ModMetaWriter.Write(mod, "Weapon Imc", defaults, Array.Empty<ModGroupOut>());
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        var m = meta["DefaultData"]?["Manipulations"]?[0]?["Manipulation"];
        Require(m != null && (string?)m["ObjectType"] == "Weapon" && (string?)m["EquipSlot"] == "Unknown" && (string?)m["BodySlot"] == "Body" &&
                (int?)m["PrimaryId"] == 201 && (int?)m["SecondaryId"] == 1 && (int?)m["Variant"] == 3 &&
                (int?)m["Entry"]?["MaterialId"] == 1 && (int?)m["Entry"]?["VfxId"] == 2,
            "a weapon's IMC entry is written as Penumbra writes one: weapon object type, body and variant, no equipment slot");

        Require(WeaponNaming.ImcOwnerId(351) == 301 && WeaponNaming.ImcOwnerId(1651) == 1601 && WeaponNaming.ImcOwnerId(3151) == 3101,
            "off hands of dual-wield classes use their main hand's IMC (id - 50), as the game and Penumbra do");
        Require(WeaponNaming.ImcOwnerId(301) == 301 && WeaponNaming.ImcOwnerId(1650) == 1650 && WeaponNaming.ImcOwnerId(251) == 251 &&
                WeaponNaming.ImcOwnerId(201) == 201,
            "every other weapon uses its own IMC");
    }

    private static void UnknownKeyTagsResolveToNothing()
    {
        Require(PortSubject.KeyTag(12345) == KeyTagKind.Gear, "a plain row id is gear or a main-hand weapon");
        Require(PortSubject.KeyTag(0x4000_3039) == KeyTagKind.OffHand, "the off-hand bit marks an off-hand weapon");
        Require(PortSubject.KeyTag(0x2000_0010) == KeyTagKind.Emote, "0x20 marks an emote");
        Require(PortSubject.KeyTag(0x2100_0010) == KeyTagKind.Timeline, "0x21 marks another animation timeline");
        Require(PortSubject.KeyTag(0x8000_0000 | 0x1001_0005) == KeyTagKind.Feature, "the top bit marks a character feature");
        Require(PortSubject.KeyTag(0x1000_0001) == KeyTagKind.Unknown, "any other tag is unknown, not an item");
    }

    private static void VariantNamesAreBuildWide()
    {
        var names = new VariantFileNames();
        // Two items whose variant sources share a stem (red.mdl in two folders) for the same race.
        var first  = names.Claim("modelvariants", "c0101_red", ".mdl");
        var second = names.Claim("modelvariants", "c0101_red", ".mdl");
        Require(first == "modelvariants/c0101_red.mdl" && second == "modelvariants/c0101_red_2.mdl",
            "model variants of two items with the same file name get files of their own");

        // Two items sharing a model id share material and texture names; their texture variants must not.
        var a = names.Claim("variants", "mt_c0101e0001_top_a_d_Red", ".tex");
        var b = names.Claim("variants", "MT_C0101E0001_TOP_A_D_RED", ".tex");
        Require(!string.Equals(a, b, StringComparison.OrdinalIgnoreCase), "texture variant names are unique across the build, ignoring case");

        var other = names.Claim("variants", "c0101_red", ".mdl");
        Require(other == "variants/c0101_red.mdl", "the same name in another folder is a different file");
    }

    private static void MaterialLinksFollowTheirMaterials()
    {
        const int Auto = ModelMaterialLinks.Auto;
        const int Keep = ModelMaterialLinks.Keep;

        // Materials A, B, C. Part 0 is set to C, part 1 to B, part 2 keeps its own, part 3 is automatic.
        RaceModelEntry Model() => new()
        {
            MaterialLinks = new List<int> { 2, 1, Keep, Auto },
            VariantMaterialLinks = new List<List<int>> { new() { 0, 2 }, new() },
        };

        var removed = Model();
        ModelMaterialLinks.Remap(new[] { removed }, ModelMaterialLinks.Removed(0));   // A removed: B, C
        Require(removed.MaterialLinks.SequenceEqual(new[] { 1, 0, Keep, Auto }),
            "removing a material keeps the other parts on the materials they were set to");
        Require(removed.VariantMaterialLinks[0].SequenceEqual(new[] { Keep, 1 }),
            "a variant's part set to the removed material keeps its own name; the others follow theirs");

        var moved = Model();
        ModelMaterialLinks.Remap(new[] { moved }, ModelMaterialLinks.Swapped(1, 2));   // B and C swap: A, C, B
        Require(moved.MaterialLinks.SequenceEqual(new[] { 1, 2, Keep, Auto }) && moved.VariantMaterialLinks[0].SequenceEqual(new[] { 0, 1 }),
            "moving a material up or down keeps every part on the material it was set to");

        var pasted = Model();
        ModelMaterialLinks.Remap(new[] { pasted }, ModelMaterialLinks.Inserted(1));    // pasted after A: A, P, B, C
        Require(pasted.MaterialLinks.SequenceEqual(new[] { 3, 2, Keep, Auto }) && pasted.VariantMaterialLinks[0].SequenceEqual(new[] { 0, 3 }),
            "pasting a material in the middle keeps every part on the material it was set to");

        var matched = Model();
        ModelMaterialLinks.Remap(new[] { matched }, ModelMaterialLinks.Reset);        // replaced by the vanilla layout
        Require(matched.MaterialLinks.SequenceEqual(new[] { Auto, Auto, Keep, Auto }) && matched.VariantMaterialLinks[0].SequenceEqual(new[] { Auto, Auto }),
            "replacing the materials with the vanilla ones puts every pick back on automatic");

        // What the build resolves after the removal: part 1 (was B) still on B, not on C.
        var written = new List<string> { "/b.mtrl", "/c.mtrl" };
        var referenced = new List<string> { "/x.mtrl", "/y.mtrl", "/z.mtrl", "/w.mtrl" };
        var plan = ModelMaterialLinks.Plan(referenced, removed.MaterialLinks, written);
        Require(plan[0] == "/c.mtrl" && plan[1] == "/b.mtrl" && plan[2] == null,
            "the build writes the remapped picks onto the right .mtrl files");
    }

    private static void SharedMaterialsAreCopiedForTheItem()
    {
        var race = new RaceGender(PlayerRace.Midlander, PlayerGender.Male);
        var top    = new FeatureSubject(SubjectKind.Hair, race, 5);
        var bottom = new FeatureSubject(SubjectKind.Hair, race, 6);

        var bikini = new MaterialSetup { Name = "Bikini" };
        bikini.Textures.Add(new TextureSlot { Type = TextureType.Diffuse, Postfix = "d", SourcePath = "bikini.png" });
        bikini.Textures.Add(new TextureSlot { Type = TextureType.Normal, Postfix = "n", SharedGamePath = "chara/common/texture/shared_n.tex" });
        var other = new MaterialSetup { Name = "Other" };

        // The bottoms have one material of their own, also called "Bikini"; parts 0 and 2 use the top's.
        var materials = new List<MaterialSetup> { new() { Name = "Bikini" } };
        var model = new RaceModelEntry
        {
            MaterialLinks = new List<int> { ModelMaterialLinks.Shared, 0, ModelMaterialLinks.Shared, ModelMaterialLinks.Keep },
            VariantMaterialLinks = new List<List<int>> { new() { ModelMaterialLinks.Shared } },
        };
        model.SharedLinks.Add(new SharedMaterialLink { Slot = 0, Item = top.Key, Material = bikini.Id });
        model.SharedLinks.Add(new SharedMaterialLink { Slot = 2, Item = top.Key, Material = bikini.Id });
        model.SharedLinks.Add(new SharedMaterialLink { Slot = 3, Item = top.Key, Material = other.Id });             // part 3 was set to Keep since
        model.SharedLinks.Add(new SharedMaterialLink { Variant = 0, Slot = 0, Item = top.Key, Material = Guid.NewGuid() });   // gone

        (PortSubject, MaterialSetup)? Find(uint key, Guid id)
            => key == top.Key && id == bikini.Id ? (top, bikini) : key == top.Key && id == other.Id ? (top, other) : null;
        var copies = MaterialCopy.AddShared(bottom, materials, new[] { model }, Find, "place");

        Require(copies.Count == 1 && materials.Count == 2 && materials[1] == copies[0].Copy,
            "one copy per material linked to, however many mesh parts use it, appended to the item's materials");
        var copy = materials[1];
        Require(copy.Name == "Bikini_b" && copy.Id != bikini.Id && copy.Option == "place",
            "the copy is named uniquely within the item, is a material of its own and goes where the item goes");
        Require(copy.Textures[0].SharedGamePath == top.TextureGamePath(MaterialNaming.ComposeTextureName("Bikini", "d"))
                && copy.Textures[1].SharedGamePath == "chara/common/texture/shared_n.tex",
            "the copy points at the textures the original writes, and at what an already shared slot points at");
        Require(bikini.Textures[0].SharedGamePath == string.Empty, "the original is left as it is");
        Require(model.MaterialLinks.SequenceEqual(new[] { 1, 0, 1, ModelMaterialLinks.Keep }),
            "parts set to the shared material point at its copy; the others stay");
        Require(model.VariantMaterialLinks[0][0] == ModelMaterialLinks.Keep, "a part set to a material that is gone keeps the file's own");
        Require(model.SharedLinks.Count == 0, "the build's copy of the model has no shared links left");

        List<MaterialCopy.SharedCopy> CopyWith(string originalPlace)
        {
            var linked = new RaceModelEntry { MaterialLinks = new List<int> { ModelMaterialLinks.Shared } };
            linked.SharedLinks.Add(new SharedMaterialLink { Slot = 0, Item = top.Key, Material = bikini.Id });
            return MaterialCopy.AddShared(bottom, new List<MaterialSetup>(), new[] { linked }, Find, "place", (_, _, _) => originalPlace);
        }
        Require(CopyWith("other") is [{ Copy.Textures: [{ IsShared: false, Option: null }, { SharedGamePath: "chara/common/texture/shared_n.tex" }] }],
            "a texture the original writes into another option is written again with the copy, which can be on without it");
        Require(CopyWith("") is [{ Copy.Textures: [{ IsShared: true }, _] }] && CopyWith("place") is [{ Copy.Textures: [{ IsShared: true }, _] }],
            "a texture in the default files or in the copy's own place stays shared");

        var setUp = new RaceModelEntry();
        setUp.SetSharedLink(-1, 1, top.Key, bikini.Id);
        setUp.SetSharedLink(0, 0, top.Key, bikini.Id);
        setUp.SetLink(-1, 1, 0);
        Require(setUp.MaterialLinks[1] == 0 && setUp.SharedLink(-1, 1) == null && setUp.SharedLink(0, 0) != null,
            "picking one of the item's own materials drops the part's shared link, and only that one");
        setUp.VariantPaths.AddRange(new[] { "a.mdl", "b.mdl" });
        setUp.SetSharedLink(1, 2, top.Key, other.Id);
        setUp.RemoveVariant(0);
        Require(setUp.SharedLinks.Count == 1 && setUp.SharedLink(0, 2)?.Material == other.Id,
            "removing a variant file drops its shared links and moves the later files' up");
        Require(setUp.Clone().SharedLinks.Single() is { Variant: 0, Slot: 2 } cloned && cloned != setUp.SharedLinks[0],
            "a model's clone carries its shared links as copies");
    }

    private static void TextureVariantsArePickedFiles(string root)
    {
        var folder = Path.Combine(root, "Variant Images");
        var other  = Path.Combine(root, "More Images");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        foreach (var name in new[] { "red.png", "blue.png", "notes.txt" })
            File.WriteAllBytes(Path.Combine(folder, name), new byte[] { 1 });
        foreach (var name in new[] { "Red.dds", "green.tga" })
            File.WriteAllBytes(Path.Combine(other, name), new byte[] { 1 });

        var legacy = new TextureSlot { UseVariants = true, SourcePath = folder };
        Require(TextureSources.VariantImages(legacy).Select(Path.GetFileName).SequenceEqual(new[] { "blue.png", "red.png" }),
            "a slot set up with a folder still reads the folder's images");

        var tex = new TextureSlot { UseVariants = true };
        Require(TextureSources.VariantImages(tex).Count == 0, "a slot with no files and no folder has no variants");
        int added = TextureSources.AddVariantImages(tex, new[] { Path.Combine(folder, "red.png"), Path.Combine(folder, "notes.txt") });
        Require(added == 1 && tex.VariantPaths.Count == 1, "picked image files are added; other files are not");
        added = TextureSources.AddVariantImages(tex, new[] { folder, other });
        Require(added == 2 && tex.VariantPaths.Select(Path.GetFileName).SequenceEqual(new[] { "red.png", "blue.png", "green.tga" }),
            "a dropped folder adds its images to the list, skipping files already there and files named like one (each name is an option)");
        Require(TextureSources.VariantImages(tex).Count == 3, "the picked files are the variants");

        added = TextureSources.AddVariantImages(legacy, new[] { Path.Combine(other, "green.tga") });
        Require(added == 1 && legacy.VariantPaths.Count == 3 && legacy.SourcePath.Length == 0,
            "adding to a slot that read a folder makes the folder's images its list first");
        Require(new TextureSlot { VariantPaths = { "a.png" } }.Clone().VariantPaths.SequenceEqual(new[] { "a.png" }),
            "a slot's clone keeps its variant files");
    }

    private static void StashRowsAreMarkedByKind()
    {
        const StashKind Tex = StashKind.Texture, Mdl = StashKind.Model;
        var order = new List<(string, StashKind)> { ("a.png", Tex), ("m.mdl", Mdl), ("b.png", Tex), ("c.png", Tex), ("d.png", Tex) };
        var marks = new InstantEdit.Studio.Services.Stash.StashMarks();

        marks.Click("a.png", Tex, ctrl: false, shift: false, order);
        marks.Click("c.png", Tex, ctrl: true, shift: false, order);
        Require(marks.Files.SequenceEqual(new[] { "a.png", "c.png" }), "Ctrl + click adds a row to the marked ones");
        marks.Click("A.PNG", Tex, ctrl: true, shift: false, order);
        Require(marks.Files.SequenceEqual(new[] { "c.png" }), "Ctrl + click on a marked row takes it away");

        marks.Click("a.png", Tex, ctrl: false, shift: false, order);
        marks.Click("c.png", Tex, ctrl: false, shift: true, order);
        Require(marks.Files.SequenceEqual(new[] { "a.png", "b.png", "c.png" }), "Shift + click marks the range, skipping rows of another kind");
        marks.Click("b.png", Tex, ctrl: false, shift: true, order);
        Require(marks.Files.SequenceEqual(new[] { "a.png", "b.png" }), "another Shift + click changes the range from the same start");

        marks.Click("m.mdl", Mdl, ctrl: true, shift: false, order);
        Require(marks.Kind == Mdl && marks.Files.SequenceEqual(new[] { "m.mdl" }), "marking a file of another kind starts over with it");
        Require(!marks.Contains("a.png", Tex) && marks.Contains("M.MDL", Mdl), "a row counts as marked only for the kind marked");

        marks.Click("d.png", Tex, ctrl: false, shift: false, order);
        Require(marks.Count == 1, "a plain click marks just that row");
    }

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
}
