using System.Collections.Immutable;
using InstantEdit.Services;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Services;

/// <summary>
/// Props from other mods: which models of a mod can be props, whether one rides a single bone, the private weapon
/// body it gets and the files its copy is built from. Real mods under G:\Penumbra are only read, never written.
/// </summary>
internal static class PropModelSourcesFixture
{
    private const string GameFolder = @"E:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
    private const string HanzoModel = @"G:\Penumbra\Hanzo Dojo - I Want You Phonecase\w9001b0200_v0\chara\weapon\w9001\obj\body\b0200\model\w9001b0200.mdl";
    private const string TelephoneMod = @"G:\Penumbra\(You're Not Gonna Reach My) Telephone";
    private const string TelephoneModel = TelephoneMod + @"\(You're Not Gonna Reach My) Telephone\Main Install\chara\monster\m8044\obj\body\b0001\model\m8044b0001.mdl";
    private const string VibratorMod = @"G:\Penumbra\BoneFactor Vibrator";
    private const string VibratorModel = VibratorMod + @"\chara\weapon\w0528\obj\body\b0016\model\w0528b0016.mdl";
    private const string BenchRiderModel = @"G:\Penumbra\[NFLB] Bench Rider [F Talls][Sit2]\new group\F Miqo\chara\accessory\a0131\model\c0101a0131_ril.mdl";

    public static void Run(Action<bool, string> check)
    {
        Part(check, "target set", () => TargetSets(check));
        Part(check, "body allocation", () => Bodies(check));
        Part(check, "candidates", () => CandidateModels(check));
        Part(check, "qualification", () => Qualification(check));
        Lumina.GameData? data = null;
        try
        {
            if (Directory.Exists(GameFolder))
                data = new Lumina.GameData(GameFolder, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
            Part(check, "telephone resolve", () => Telephone(check, data));
            Part(check, "vibrator resolve", () => Vibrator(check, data));
        }
        finally
        {
            data?.Dispose();
        }
    }

    private static void Part(Action<bool, string> check, string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { check(false, $"props from mods, {name}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static Func<string, byte[]?> Game(Lumina.GameData? data)
        => path => data?.GetFile(path)?.Data;

    private static Func<string, bool> Exists(Lumina.GameData? data)
        => path =>
        {
            if (data is null)
                return false;
            try { return data.FileExists(path); }
            catch { return false; }
        };

    private static void TargetSets(Action<bool, string> check)
    {
        check(PropModelSources.TargetSet("chara/weapon/w0528/obj/body/b0016/model/w0528b0016.mdl") == 528,
            "a weapon prop keeps its own weapon set");
        check(PropModelSources.TargetSet("chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl") == 9001,
            "a monster prop goes to weapon set 9001");
        check(PropModelSources.TargetSet("chara/demihuman/d1002/obj/equipment/e0001/model/d1002e0001_top.mdl") == 9001,
            "a demihuman prop goes to weapon set 9001");
    }

    private static void Bodies(Action<bool, string> check)
    {
        var taken = new HashSet<(ushort, ushort)> { (9001, 9001) };
        check(PropModelSources.AllocateBody(9001, p => p.EndsWith("b9000.imc"), taken) == 9002,
            "a private body skips one the game ships and one already taken");
        check(PropModelSources.AllocateBody(9001, _ => true, new HashSet<(ushort, ushort)>()) is null,
            "no private body when the game ships every one");
        var seen = new List<string>();
        PropModelSources.AllocateBody(528, p => { seen.Add(p); return false; }, new HashSet<(ushort, ushort)> { (9001, 9000) });
        check(seen.FirstOrDefault() == "chara/weapon/w0528/obj/body/b9000/b9000.imc",
            "a body is free when the game has no imc for it under the prop's own set");
        check(PropModelSources.AllocateBody(528, _ => false, new HashSet<(ushort, ushort)> { (9001, 9000) }) == 9000,
            "a body taken in another set doesn't block the same body here");
        var all = new HashSet<(ushort, ushort)>();
        for (var body = 9000; body <= 9999; ++body)
            all.Add((9001, (ushort)body));
        check(PropModelSources.AllocateBody(9001, _ => false, all) is null, "no private body when every one is taken");
    }

    private static void CandidateModels(Action<bool, string> check)
    {
        var effective = new Dictionary<string, string>
        {
            ["chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl"] = @"C:\m\a.mdl",
            ["chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl"] = @"C:\m\b.mdl",
            ["chara/demihuman/d1002/obj/equipment/e0001/model/d1002e0001_top.mdl"] = @"C:\m\c.mdl",
            ["chara/accessory/a0131/model/c0101a0131_ril.mdl"] = @"C:\m\d.mdl",
            ["chara/equipment/e0001/model/c0101e0001_top.mdl"] = @"C:\m\e.mdl",
            ["chara/weapon/w9001/obj/body/b0200/material/v0001/mt_w9001b0200_a.mtrl"] = @"C:\m\f.mtrl",
            ["chara/monster/m8044/obj/body/b0001/texture/v01_m8044b0001_d.tex"] = @"C:\m\g.tex",
        };
        var candidates = PropModelSources.Candidates(effective);
        check(candidates.SequenceEqual([
                "chara/demihuman/d1002/obj/equipment/e0001/model/d1002e0001_top.mdl",
                "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl",
                "chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl",
            ]),
            $"only weapon, monster and demihuman models are prop candidates, sorted: {string.Join(", ", candidates)}");
    }

    private static string Bones(byte[] mdl)
    {
        var info = VanillaModelReader.Read(mdl);
        var used = info.BoneTables.SelectMany(table => table.BoneIndex)
            .Select(index => index < info.BoneNames.Length ? info.BoneNames[index] : $"#{index}").Distinct();
        return $"bones [{string.Join(", ", used)}], materials [{string.Join(", ", info.MaterialNames)}]";
    }

    private static void Qualification(Action<bool, string> check)
    {
        foreach (var (file, gamePath, qualifies, name) in new[]
                 {
                     (HanzoModel, "chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl", true, "Hanzo phone"),
                     (TelephoneModel, "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", true, "Telephone"),
                     (VibratorModel, "chara/weapon/w0528/obj/body/b0016/model/w0528b0016.mdl", true, "Vibrator"),
                     (BenchRiderModel, "chara/accessory/a0131/model/c0101a0131_ril.mdl", false, "Bench Rider ring"),
                 })
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"[SKIP] props from mods: the {name} model isn't at {file}");
                continue;
            }
            var mdl = File.ReadAllBytes(file);
            check(PropModelSources.Qualifies(gamePath, mdl) == qualifies,
                $"the {name} {(qualifies ? "qualifies" : "doesn't qualify")} as a prop: {Bones(mdl)}");
        }
        check(!PropModelSources.Qualifies("chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", [1, 2, 3]),
            "a model that can't be read doesn't qualify");
    }

    private static string Listing(TimelinePropSource source)
        => string.Join("; ", source.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} = {pair.Value}"));

    private static void Telephone(Action<bool, string> check, Lumina.GameData? data)
    {
        if (!Directory.Exists(TelephoneMod))
        {
            Console.WriteLine($"[SKIP] props from mods: the Telephone mod isn't at {TelephoneMod}");
            return;
        }
        var effective = ModOptionGroups.EffectiveFiles(TelephoneMod, new Dictionary<string, IReadOnlyList<string>>
        {
            ["(You're Not Gonna Reach My) Telephone"] = ["Main Install"],
            ["Color Options"] = ["Moogle Pink"],
        });
        const string model = "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";
        check(PropModelSources.Candidates(effective).SequenceEqual([model]), "the Telephone's one prop candidate is its monster model");
        var source = PropModelSources.Resolve(TelephoneMod, "(You're Not Gonna Reach My) Telephone", model, 9001, 9000,
            effective, Game(data), Exists(data));
        Console.WriteLine($"  {source.ModName} prop files: {Listing(source)}");
        const string root = "chara/weapon/w9001/obj/body/b9000/";
        check(source.ModDirectory == TelephoneMod && source.SourceModel == model && source.Set == 9001 && source.Body == 9000,
            "the Telephone source keeps its mod, model and private body");
        check(source.Files.TryGetValue(root + "model/w9001b9000.mdl", out var mdl) &&
              string.Equals(mdl, "file:" + effective[model], StringComparison.Ordinal),
            $"the Telephone's private model is the mod's model");
        check(source.Files.TryGetValue(root + "material/v0001/mt_m8044b0001_a.mtrl", out var mtrl) &&
              mtrl.StartsWith("file:", StringComparison.Ordinal) && mtrl.Contains("main install", StringComparison.OrdinalIgnoreCase),
            $"the Telephone's private material is the mod's material");
        var textures = MaterialPreviewBundleBuilder.ReadMaterialTextures(File.ReadAllBytes(mtrl![5..]))
            .Select(texture => Path.GetFileName(texture.StoredPath)).Distinct().ToArray();
        var privateTextures = source.Files.Keys.Where(key => key.StartsWith(root + "texture/", StringComparison.Ordinal)).ToArray();
        check(textures.Length > 0 && (data is null
                ? privateTextures.Length <= textures.Length
                : privateTextures.Order(StringComparer.Ordinal).SequenceEqual(
                    textures.Select(name => root + "texture/" + name).Order(StringComparer.Ordinal))),
            $"the Telephone has one private texture per texture its material names ({string.Join(", ", textures)})");
        check(source.Files.TryGetValue(root + "texture/v01_m8044b0001_d.tex", out var diffuse) &&
              diffuse.StartsWith("file:", StringComparison.Ordinal) && diffuse.Contains("moogle pink", StringComparison.OrdinalIgnoreCase),
            $"the Telephone's diffuse comes from the picked colour, Moogle Pink");
        check(source.Files.TryGetValue(root + "texture/v01_m8044b0001_n.tex", out var normal) &&
              normal.Contains("main install", StringComparison.OrdinalIgnoreCase),
            $"the Telephone's normal map comes from the main install");
        check(source.Files.Count == 2 + privateTextures.Length, $"the Telephone source holds only its model, material and textures");
    }

    private static void Vibrator(Action<bool, string> check, Lumina.GameData? data)
    {
        if (!Directory.Exists(VibratorMod))
        {
            Console.WriteLine($"[SKIP] props from mods: the Vibrator mod isn't at {VibratorMod}");
            return;
        }
        if (data is null)
        {
            Console.WriteLine("[SKIP] props from mods: the game isn't installed where the checks look, so the Vibrator's game material isn't read");
            return;
        }
        var effective = ModOptionGroups.EffectiveFiles(VibratorMod, new Dictionary<string, IReadOnlyList<string>>());
        const string model = "chara/weapon/w0528/obj/body/b0016/model/w0528b0016.mdl";
        var set = PropModelSources.TargetSet(model);
        var body = PropModelSources.AllocateBody(set, Exists(data), new HashSet<(ushort, ushort)>());
        check(set == 528 && body == 9000, $"the Vibrator gets w0528 b9000 (got w{set:D4} b{body:D4})");
        var source = PropModelSources.Resolve(VibratorMod, "BoneFactor Vibrator", model, 528, 9000, effective, Game(data), Exists(data));
        Console.WriteLine($"  {source.ModName} prop files: {Listing(source)}");
        const string root = "chara/weapon/w0528/obj/body/b9000/";
        check(source.Files.TryGetValue(root + "model/w0528b9000.mdl", out var mdl) && mdl == "file:" + effective[model],
            $"the Vibrator's private model is the mod's model");
        check(source.Files.TryGetValue(root + "material/v0001/mt_w0528b0016_a.mtrl", out var mtrl) &&
              mtrl == "game:chara/weapon/w0528/obj/body/b0016/material/v0001/mt_w0528b0016_a.mtrl",
            $"the Vibrator's material, which the mod doesn't ship, comes from the game");
        var textures = source.Files.Where(pair => pair.Key.StartsWith(root + "texture/", StringComparison.Ordinal)).ToArray();
        var modRoot = Path.GetFullPath(VibratorMod) + Path.DirectorySeparatorChar;
        check(textures.Length > 0 && textures.All(pair => pair.Value.StartsWith("file:" + modRoot, StringComparison.OrdinalIgnoreCase)),
            $"the Vibrator's textures are the mod's files");
    }
}
