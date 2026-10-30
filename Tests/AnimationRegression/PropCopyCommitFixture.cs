using System.Collections.Immutable;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Ui;

/// <summary>
/// Saving a prop from another mod: its private copy (model, material, texture) is added beside the animation, and the
/// material's 255 other version folders are aliases that map to the one staged file. Undo takes every mapping out and
/// deletes each file once. Runs the commit service's staging and the steps its commit and undo take for added files on
/// temporary mod folders; the Penumbra side (activation, reloads) is checked in game.
/// </summary>
internal static class PropCopyCommitFixture
{
    private const string Body = "chara/weapon/w9001/obj/body/b9000/";
    private const string Model = Body + "model/w9001b9000.mdl";
    private const string Material = Body + "material/v0001/mt_w9001b9000_a.mtrl";
    private const string Texture = Body + "texture/w9001b9000_a_d.tex";
    private const string Pap = "chara/human/c0101/animation/a0001/bt_common/emote/laugh.pap";
    private const string OtherPap = "chara/human/c0101/animation/a0001/bt_common/emote/clap.pap";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        AddedPaths(check);
        var root = Path.Combine(Path.GetTempPath(), "InstantEditPropCopyCommitRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            BuiltCopy(root, check);
            NewMod(root, check);
            InPlace(root, check, reject);
            SourceGone(root, check);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AddedPaths(Action<bool, string> check)
    {
        check(AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b9000/model/w9001b9000.mdl") &&
              AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b9000/material/v0007/mt_x_a.mtrl") &&
              AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b9000/texture/x_d.tex"),
            "a private prop body's model, materials and textures can be added");
        check(!AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl") &&
              !AnimationCommitService.AddedGamePath("chara/equipment/e0001/model/c0101e0001_top.mdl") &&
              !AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b9000/model/../../b0200/model/w9001b0200.mdl") &&
              !AnimationCommitService.AddedGamePath("chara/weapon/w9001/obj/body/b9000/w9001b9000.imc"),
            "a shipped body's model, gear models, escaping paths and other kinds of file are never added");
    }

    /// <summary>
    /// Every path a built copy holds can be saved: model, material, texture and its DX11 <c>--</c> twin, and the 255
    /// aliases, as <see cref="PropCopies.Build"/> names them.
    /// </summary>
    private static void BuiltCopy(string parent, Action<bool, string> check)
    {
        var dir = Path.Combine(parent, "Built");
        Directory.CreateDirectory(dir);
        const string stored = "chara/monster/m8044/obj/body/b0001/texture/v01_m8044b0001_d.tex";
        File.WriteAllBytes(Path.Combine(dir, "a.mdl"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "a.mtrl"), PropModelSourcesFixture.Material((stored, true)));
        File.WriteAllBytes(Path.Combine(dir, "d.tex"), [2]);
        var texture = PropModelSources.PrivateTexturePath(Body, stored, ImmutableDictionary<string, string>.Empty);
        var source = new TimelinePropSource("phones", "Phones", "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", 9001, 9000,
            ImmutableDictionary<string, string>.Empty
                .Add(Model, "file:" + Path.Combine(dir, "a.mdl"))
                .Add(Material, "file:" + Path.Combine(dir, "a.mtrl"))
                .Add(texture, "file:" + Path.Combine(dir, "d.tex"))
                .Add(InstantEdit.Services.PathRules.Dx11TexturePath(texture, 0x8000), "file:" + Path.Combine(dir, "d.tex")),
            ImmutableDictionary<string, string>.Empty.Add(stored, texture));
        var copy = PropCopies.Build(source, _ => null);
        var refused = copy.Files.Keys.Concat(copy.Aliases.Keys).Where(path => !AnimationCommitService.AddedGamePath(path)).ToArray();
        check(copy.Files.Count == 4 && copy.Files.Keys.Any(path => path.Contains("/--", StringComparison.Ordinal)) && refused.Length == 0 &&
              AnimationEditService.KeptAliases(copy.Aliases, copy.Files, copy.Files).Count == 255,
            $"every file and alias of a built copy, the DX11 twin included, can be added (refused: {string.Join(", ", refused)})");
    }

    private static ImmutableDictionary<string, byte[]> Files() => ImmutableDictionary<string, byte[]>.Empty
        .Add(Model, [1, 2, 3]).Add(Material, [4, 5, 6]).Add(Texture, [7, 8, 9]);

    private static ImmutableDictionary<string, string> Aliases()
    {
        var aliases = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        for (var version = 0; version <= 255; ++version)
            if (version != 1)
                aliases[Material.Replace("/v0001/", $"/v{version:D4}/")] = Material;
        return aliases.ToImmutable();
    }

    private static string[] MaterialKeys(JsonNode files) =>
        [.. files.AsObject().Where(pair => pair.Key.EndsWith(".mtrl", StringComparison.Ordinal)).Select(pair => pair.Key)];

    /// <summary>A new mod: one material file, 256 default mappings to it.</summary>
    private static void NewMod(string parent, Action<bool, string> check)
    {
        var modRoot = Path.Combine(parent, "New Mod");
        var staging = Path.Combine(parent, "New Staging");
        Directory.CreateDirectory(staging);
        // As the commit service stages a new mod's added files.
        var staged = Files().Select((pair, i) =>
        {
            var file = Path.Combine(staging, i + ".staged");
            File.WriteAllBytes(file, pair.Value);
            return new AnimationFileChange(pair.Key, Path.GetFullPath(Path.Combine(modRoot, "files/" + pair.Key)), "New Mod", modRoot,
                "files/" + pair.Key, "", AnimationPap.Hash(pair.Value), "", file, AddedFor: Pap);
        }).ToList();
        var all = staged.Concat(AnimationCommitService.Aliased(staged, Aliases())).ToList();
        var material = staged.Single(f => f.GamePath == Material);
        check(all.Count == 3 + 255 && all.Where(f => f.AliasOf != null).All(f => f.AliasOf == Material &&
                  f.Target == material.Target && f.RelativePath == "files/" + Material && f.Staged == material.Staged),
            "each alias is journaled with its material's file, relative path and staged copy");
        check(!AnimationCommitService.SharesTarget(all), "aliases sharing their material's file are no two edits of one file");
        check(AnimationCommitService.SharesTarget([.. staged, material with { GamePath = Body + "material/v0001/mt_other.mtrl" }]),
            "two added files at one target still are");
        foreach (var file in all)
            AnimationCommitService.WriteAdded(file, all, inPlace: false);
        var written = Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories).ToArray();
        check(written.Length == 3 && written.Count(f => f.EndsWith(".mtrl", StringComparison.Ordinal)) == 1,
            $"a new mod gets one material file (wrote {written.Length} files)");
        var meta = AnimationCommitService.CreateNewModMetadata("New Mod", all, "[]");
        var defaults = meta["DefaultData"]!["Files"]!;
        var keys = MaterialKeys(defaults);
        check(keys.Length == 256 && keys.All(key => defaults[key]!.GetValue<string>() == "files/" + Material) &&
              defaults[Model]!.GetValue<string>() == "files/" + Model,
            $"the new mod maps all 256 material versions to that one file ({keys.Length} mapped)");
    }

    /// <summary>In place: mapped where the animation is, saved again without a second copy, then undone.</summary>
    private static void InPlace(string parent, Action<bool, string> check, Action<Action, string> reject)
    {
        var mod = Path.Combine(parent, "Mod");
        Directory.CreateDirectory(Path.Combine(mod, "files"));
        File.WriteAllBytes(Path.Combine(mod, "files", "laugh.pap"), [9]);
        var meta = ModMetadata.CreateV4ModMetadata("Mod", "Author", "Description", "1.0", new JsonObject
        {
            ["Files"] = new JsonObject { [Pap] = "files/laugh.pap" },
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        });
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Style",
            ["Options"] = new JsonArray(
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Loud", ["Files"] = new JsonObject { [Pap] = "files/loud.pap" } },
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Other", ["Files"] = new JsonObject { [OtherPap] = "files/clap.pap" } }),
        });
        var metaPath = Path.Combine(mod, "meta.json");
        File.WriteAllText(metaPath, meta.ToJsonString());
        var before = Unstamped(metaPath);

        var store = new AnimationJournalStore(Path.Combine(parent, "Config"));
        var commits = new AnimationCommitService(null!, null!, null!, store);
        var clip = new AnimationClip(Pap, "laugh", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("a", 7, 1, Guid.NewGuid(), "Test", "Laugh", clip, null, [Pap],
            [new AnimationResource(Pap, Path.Combine(mod, "files", "laugh.pap"), "hash", "Mod", mod, "files/laugh.pap")], DateTime.UtcNow, true);
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false,
            AnimationOperation.EditTimeline,
            Timeline: new AnimationTimelineEdit(false, 0, null));
        var manifest = new AnimationDependencyManifest(capture.Sources, ImmutableDictionary<string, byte[]>.Empty);
        var journal = commits.PrepareAsync(request, manifest, ImmutableDictionary<string, byte[]>.Empty, CancellationToken.None,
            (Pap, Files(), null, null, Aliases())).GetAwaiter().GetResult();
        var target = journal.Files.Single(f => f.GamePath == Material);
        var aliases = journal.Files.Where(f => f.AliasOf != null).ToArray();
        check(journal.Files.Count == 258 && aliases.Length == 255 && aliases.All(f => f.AliasOf == Material && f.Target == target.Target &&
                  f.RelativePath == "instant_edit/" + Material && f.Staged == target.Staged && f.AddedFor == Pap) &&
              Directory.EnumerateFiles(store.DirectoryFor(journal.Id), "*.staged").Count() == 3,
            $"in place, the 255 aliases are journaled with the material's file and only 3 files are staged ({journal.Files.Count} entries)");
        var reloaded = store.Load().Single(j => j.Id == journal.Id);
        check(reloaded.Files.Count(f => f.AliasOf == Material) == 255, "the aliases survive the journal being read back after a restart");
        var valid = true;
        foreach (var file in reloaded.Files)
            try { commits.ValidateTarget(reloaded, file); }
            catch (InvalidDataException) { valid = false; }
        check(valid, "recovery accepts every entry of the read-back journal, aliases included");
        reject(() => commits.ValidateTarget(reloaded, aliases[0] with { RelativePath = "instant_edit/" + Texture, Target = Path.Combine(mod, "instant_edit", Texture) }),
            "recovery refuses an alias that doesn't share its material's file");
        reject(() => AnimationCommitService.Aliased(journal.Files.Where(f => f.AliasOf == null).ToList(),
                ImmutableDictionary<string, string>.Empty.Add(Body + "material/v0000/mt_missing.mtrl", Body + "material/v0001/mt_missing.mtrl")),
            "an alias of a file the edit doesn't add is refused");

        foreach (var file in journal.Files)
            AnimationCommitService.WriteAdded(file, journal.Files, inPlace: true);
        var saved = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var defaults = saved["DefaultData"]!["Files"]!;
        var loud = saved["Groups"]![0]!["Options"]![0]!["Files"]!;
        check(MaterialKeys(defaults).Length == 256 && MaterialKeys(loud).Length == 256 &&
              MaterialKeys(defaults).All(key => defaults[key]!.GetValue<string>() == "instant_edit/" + Material) &&
              MaterialKeys(saved["Groups"]![0]!["Options"]![1]!["Files"]!).Length == 0,
            "in place, every option that maps the animation maps all 256 material versions to the one file, and no other option does");
        check(Directory.EnumerateFiles(Path.Combine(mod, "instant_edit"), "*.mtrl", SearchOption.AllDirectories).Count() == 1,
            "in place, one material file is written");

        // Activation resolves each written file and one alias of each, not all 255.
        var resolved = new List<string>();
        var validated = new List<string>();
        AnimationCommitService.VerifyActivationAsync(journal.Files, path =>
        {
            resolved.Add(path);
            var file = ModMetadata.AnimationMappedFiles(mod, path).FirstOrDefault();
            return Task.FromResult(file == null ? path : Path.GetFullPath(Path.Combine(mod, file)));
        }, file => validated.Add(file.GamePath), () => Task.CompletedTask).GetAwaiter().GetResult();
        check(resolved.Count == 4 && validated.Count == 3, $"activation resolves 4 paths and checks 3 files ({resolved.Count}, {validated.Count})");

        // A second save of the same prop finds the collection already giving its files.
        var edited = capture.Sources[0];
        var reused = new List<string>();
        var packaged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in Files())
        {
            var file = ModMetadata.AnimationMappedFiles(mod, path).Single();
            var full = Path.Combine(mod, file);
            var current = new AnimationResource(path, full, AnimationPap.Hash(File.ReadAllBytes(full)), "Mod", mod, file);
            if (!AnimationEditService.Given(path, bytes, current, edited, reused))
                packaged[path] = bytes;
        }
        check(packaged.Count == 0 && reused.Order(StringComparer.Ordinal).SequenceEqual(Files().Keys.Order(StringComparer.Ordinal)) &&
              AnimationEditService.KeptAliases(Aliases(), Files(), packaged).IsEmpty,
            "saving in place again reuses the private files, lists them as reused and adds no alias");
        reject(() => AnimationEditService.Given(Material, [1], new AnimationResource(Material, "x", "other", "Mod", mod, "x"), edited, []),
            "a different file already at a private path is still refused");
        var mappings = new[] { (Material, "instant_edit/" + Material) }.Concat(Aliases().Keys.Select(a => (a, "instant_edit/" + Material))).ToArray();
        var mapped = File.ReadAllText(metaPath);
        ModMetadata.AddAnimationResourceMappings(mod, Pap, mappings);
        check(MaterialKeys(JsonNode.Parse(File.ReadAllText(metaPath))!["DefaultData"]!["Files"]!).Length == 256,
            "mapping the same file at the same paths again is accepted and changes no mapping");
        reject(() => ModMetadata.AddAnimationResourceMapping(mod, Pap, Material, "instant_edit/other.mtrl"),
            "a private path the mod maps to another file is still refused");
        File.WriteAllText(metaPath, mapped);

        // Undo: no hash check on aliases, each file deleted once, after its aliases' mappings are gone.
        var changedAlias = aliases[0] with { AfterHash = "changed" };
        AnimationCommitService.CheckAddedUndo(changedAlias);
        reject(() => AnimationCommitService.CheckAddedUndo(target with { AfterHash = "changed" }), "undo still checks the material's own file");
        foreach (var file in journal.Files)
            AnimationCommitService.CheckAddedUndo(file);
        var deleted = journal.Files.Where(file => AnimationCommitService.UndoAdded(file, journal.Files)).Select(file => file.GamePath).ToArray();
        var undone = JsonNode.Parse(File.ReadAllText(metaPath))!;
        check(deleted.Order(StringComparer.Ordinal).SequenceEqual(Files().Keys.Order(StringComparer.Ordinal)) &&
              MaterialKeys(undone["DefaultData"]!["Files"]!).Length == 0 && MaterialKeys(undone["Groups"]![0]!["Options"]![0]!["Files"]!).Length == 0 &&
              !Directory.EnumerateFiles(mod, "*.mtrl", SearchOption.AllDirectories).Any(),
            $"undo removes all 256 mappings and deletes each of the 3 files once ({deleted.Length} deleted)");
        check(Unstamped(metaPath) == before, "undo leaves meta.json as it was before the save");
        var twice = journal.Files.Count(file => AnimationCommitService.UndoAdded(file, journal.Files));
        check(twice == 0 && Unstamped(metaPath) == before, "undoing again after a restart changes nothing");
    }

    /// <summary>meta.json without the time it was last written.</summary>
    private static string Unstamped(string metaPath)
    {
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        meta.Remove("LastWrite");
        return meta.ToJsonString();
    }

    /// <summary>A snapshot file deleted before Save: the save fails with the copy's message and never starts.</summary>
    private static void SourceGone(string parent, Action<bool, string> check)
    {
        var model = Path.Combine(parent, "phone.mdl");
        File.WriteAllBytes(model, [1, 2, 3]);
        var source = new TimelinePropSource("phones", "Phones", "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", 9001, 9000,
            ImmutableDictionary<string, string>.Empty.Add(Model, "file:" + model), ImmutableDictionary<string, string>.Empty);
        var doc = new TimelineDocument(30, 30, null, [new TimelineEvent("n1", "C198", TimelineLane.Prop, 0, 0, false, "", TimelineAccess.Editable,
            null, false, ImmutableDictionary<string, double>.Empty, Source: source)], 2);
        var present = TimelineWindowViews.PropAdded(doc, _ => null);
        check(present.Error == null && present.Files.ContainsKey(Model) && present.Aliases.IsEmpty, "a prop whose files are there is added");
        File.Delete(model);
        var gone = TimelineWindowViews.PropAdded(doc, _ => null);
        check(gone.Error == "The prop's model from Phones is gone. Pick it again." && gone.Files.IsEmpty && gone.Aliases.IsEmpty,
            $"a snapshot file deleted before Save fails the save with the copy's message and adds nothing (got {gone.Error})");
        check(TimelineWindowViews.PropAdded(new TimelineDocument(30, 30, null, [], 1), _ => null) is { Error: null, Files.IsEmpty: true },
            "a timeline without a prop from a mod adds nothing");
    }
}
