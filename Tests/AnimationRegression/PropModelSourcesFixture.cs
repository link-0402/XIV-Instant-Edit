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
        Part(check, "texture rules", () => TextureRules(check));
        Part(check, "private copy", () => PrivateCopy(check));
        Lumina.GameData? data = null;
        try
        {
            if (Directory.Exists(GameFolder))
                data = new Lumina.GameData(GameFolder, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
            Part(check, "telephone resolve", () => Telephone(check, data));
            Part(check, "vibrator resolve", () => Vibrator(check, data));
            Part(check, "telephone copy", () => TelephoneCopy(check, data));
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
        check(source.Textures.Count == textures.Length &&
              source.Textures.Values.Order(StringComparer.Ordinal).SequenceEqual(
                  textures.Select(name => root + "texture/" + name).Order(StringComparer.Ordinal)) &&
              source.Textures.TryGetValue("chara/monster/m8044/obj/body/b0001/texture/v01_m8044b0001_d.tex", out var named) &&
              named == root + "texture/v01_m8044b0001_d.tex" &&
              (data is null || source.Textures.Values.All(source.Files.ContainsKey)),
            $"the Telephone names a private path for each texture its material lists: {string.Join(", ", source.Textures)}");
    }

    /// <summary>
    /// An MTRL listing <paramref name="textures"/> (path and DX11 flag) behind the shader name, with an empty
    /// shader section: enough for the texture table to read.
    /// </summary>
    private static byte[] Material(params (string Path, bool Dx11)[] textures)
    {
        var strings = new MemoryStream();
        strings.Write("character.shpk\0"u8);
        var offsets = new List<ushort>();
        foreach (var (path, _) in textures)
        {
            offsets.Add((ushort)strings.Position);
            strings.Write(System.Text.Encoding.UTF8.GetBytes(path + "\0"));
        }
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);
        var table = strings.ToArray();
        var bytes = new byte[16 + 4 * textures.Length + table.Length + 12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x0103u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), (ushort)table.Length);
        bytes[12] = (byte)textures.Length;
        for (var i = 0; i < textures.Length; ++i)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(16 + 4 * i, 2), offsets[i]);
            BitConverter.TryWriteBytes(bytes.AsSpan(18 + 4 * i, 2), (ushort)(textures[i].Dx11 ? 0x8000 : 0));
        }
        table.CopyTo(bytes, 16 + 4 * textures.Length);
        return bytes;
    }

    /// <summary>
    /// The texture rules on the Telephone's real model with a made-up material and mod files: a texture the mod ships
    /// only as its DX11 <c>--</c> file, two textures of one file name from different folders, and one found nowhere.
    /// </summary>
    private static void TextureRules(Action<bool, string> check)
    {
        if (!File.Exists(TelephoneModel))
        {
            Console.WriteLine($"[SKIP] props from mods: the Telephone model isn't at {TelephoneModel}, so the texture rules aren't run");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ie-prop-sources-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string Put(string name, byte[] bytes)
            {
                var file = Path.Combine(dir, name);
                File.WriteAllBytes(file, bytes);
                return file;
            }
            var material = Put("mt.mtrl", Material(
                ("chara/common/texture/dx11_only.tex", true),
                ("chara/a/texture/same.tex", false),
                ("chara/b/texture/same.tex", false),
                ("chara/c/texture/nowhere.tex", false),
                ("chara/a/texture/same.tex", false)));
            const string model = "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";
            var dx11 = Put("dx11.tex", [1]);
            var sameA = Put("same_a.tex", [2]);
            var sameB = Put("same_b.tex", [3]);
            var effective = new Dictionary<string, string>
            {
                [model] = TelephoneModel,
                ["chara/monster/m8044/obj/body/b0001/material/v0001/mt_m8044b0001_a.mtrl"] = material,
                ["chara/common/texture/--dx11_only.tex"] = dx11,
                ["chara/a/texture/same.tex"] = sameA,
                ["chara/b/texture/same.tex"] = sameB,
            };
            var source = PropModelSources.Resolve(dir, "Synthetic", model, 9001, 9000, effective, _ => null, _ => false);
            const string root = "chara/weapon/w9001/obj/body/b9000/texture/";
            var listing = $"{string.Join(", ", source.Textures)} | {Listing(source)}";
            void Say(bool ok, string message) => check(ok, ok ? message : message + ": " + listing);
            Say(source.Textures.TryGetValue("chara/common/texture/dx11_only.tex", out var dxPath) && dxPath == root + "dx11_only.tex" &&
                  source.Files.TryGetValue(root + "dx11_only.tex", out var plain) && plain == "file:" + dx11 &&
                  source.Files.TryGetValue(root + "--dx11_only.tex", out var twin) && twin == "file:" + dx11,
                $"a texture the mod ships only as its DX11 file resolves, under both private names");
            Say(source.Textures.TryGetValue("chara/a/texture/same.tex", out var first) && first == root + "same.tex" &&
                  source.Textures.TryGetValue("chara/b/texture/same.tex", out var second) && second == root + "same_2.tex" &&
                  source.Files.GetValueOrDefault(root + "same.tex") == "file:" + sameA &&
                  source.Files.GetValueOrDefault(root + "same_2.tex") == "file:" + sameB,
                $"two textures of one file name from different folders get their own private names, the later one _2");
            Say(source.Textures.TryGetValue("chara/c/texture/nowhere.tex", out var missing) && missing == root + "nowhere.tex" &&
                  !source.Files.ContainsKey(root + "nowhere.tex"),
                $"a texture found nowhere has a private name but no file, so the copy can report it");
            Say(source.Textures.Count == 4 && source.Files.Count == 2 + 4,
                $"a texture listed twice is named once");
            var owners = new Dictionary<string, string>
            {
                [root + "x.tex"] = "chara/a/x.tex",
                [root + "x_2.tex"] = "chara/b/x.tex",
            };
            check(PropModelSources.PrivateTexturePath("chara/weapon/w9001/obj/body/b9000/", "chara/c/x.tex", owners) == root + "x_3.tex" &&
                  PropModelSources.PrivateTexturePath("chara/weapon/w9001/obj/body/b9000/", "chara/b/x.tex", owners) == root + "x_2.tex",
                "a third texture of one name gets _3, and a texture keeps the name it was given");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
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

    private const string CopyRoot = "chara/weapon/w9001/obj/body/b9000/";

    private static string[] TexturesOf(byte[] material)
        => MaterialPreviewBundleBuilder.ReadMaterialTextures(material).Select(texture => texture.StoredPath).ToArray();

    /// <summary>
    /// Building the private copy from made-up files: two materials sharing a texture, a DX11 texture, a model and
    /// a texture from the game, one texture found nowhere.
    /// </summary>
    private static void PrivateCopy(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-prop-copies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string Put(string name, byte[] bytes)
            {
                var file = Path.Combine(dir, name);
                File.WriteAllBytes(file, bytes);
                return file;
            }
            var materialA = Material(("chara/a/texture/same.tex", false), ("chara/a/texture/d.tex", true));
            var materialB = Material(("chara/a/texture/same.tex", false), ("chara/b/texture/n.tex", false));
            var model = "game:chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";
            var same = Put("same.tex", [2]);
            var files = ImmutableDictionary<string, string>.Empty
                .Add(CopyRoot + "model/w9001b9000.mdl", model)
                .Add(CopyRoot + "material/v0001/mt_a.mtrl", "file:" + Put("a.mtrl", materialA))
                .Add(CopyRoot + "material/v0001/mt_b.mtrl", "file:" + Put("b.mtrl", materialB))
                .Add(CopyRoot + "texture/same.tex", "file:" + same)
                .Add(CopyRoot + "texture/d.tex", "game:chara/a/texture/--d.tex")
                .Add(CopyRoot + "texture/--d.tex", "game:chara/a/texture/--d.tex");
            var textures = ImmutableDictionary<string, string>.Empty
                .Add("chara/a/texture/same.tex", CopyRoot + "texture/same.tex")
                .Add("chara/a/texture/d.tex", CopyRoot + "texture/d.tex")
                .Add("chara/b/texture/n.tex", CopyRoot + "texture/n.tex");
            var source = new TimelinePropSource(dir, "Synthetic", "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", 9001, 9000, files, textures);
            var calls = new List<string>();
            Func<string, byte[]?> game = path =>
            {
                calls.Add(path);
                return path.EndsWith(".mdl", StringComparison.Ordinal) ? [9] : path.EndsWith("--d.tex", StringComparison.Ordinal) ? [4] : null;
            };
            var copy = PropCopies.Build(source, game);
            var listing = string.Join(", ", copy.Files.Keys.Order(StringComparer.Ordinal));
            check(copy.Files.Count == 6 && copy.Files.ContainsKey(CopyRoot + "model/w9001b9000.mdl") &&
                  copy.Files.ContainsKey(CopyRoot + "material/v0001/mt_a.mtrl") && copy.Files.ContainsKey(CopyRoot + "material/v0001/mt_b.mtrl") &&
                  copy.Files[CopyRoot + "texture/same.tex"].SequenceEqual(new byte[] { 2 }) &&
                  copy.Files[CopyRoot + "texture/d.tex"].SequenceEqual(new byte[] { 4 }) &&
                  copy.Files[CopyRoot + "texture/--d.tex"].SequenceEqual(new byte[] { 4 }),
                $"the copy holds the model, both materials and each found texture once: {listing}");
            check(copy.Missing.SequenceEqual([CopyRoot + "texture/n.tex"]),
                $"the copy reports the texture found nowhere: {string.Join(", ", copy.Missing)}");

            var versions = Enumerable.Range(0, 256).Where(n => n != 1).Select(n => $"v{n:D4}").ToArray();
            foreach (var name in new[] { "mt_a.mtrl", "mt_b.mtrl" })
            {
                var target = $"{CopyRoot}material/v0001/{name}";
                var aliases = copy.Aliases.Where(pair => pair.Key.EndsWith("/" + name, StringComparison.Ordinal)).ToArray();
                check(aliases.Length == 255 && aliases.All(pair => pair.Value == target) &&
                      aliases.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(
                          versions.Select(version => $"{CopyRoot}material/{version}/{name}").Order(StringComparer.Ordinal)),
                    $"{name} has an alias at v0000 and v0002..v0255, each pointing at its v0001 file");
            }
            check(copy.Aliases.Count == 510, $"two materials make 510 aliases (got {copy.Aliases.Count})");

            var inA = TexturesOf(copy.Files[CopyRoot + "material/v0001/mt_a.mtrl"]);
            var inB = TexturesOf(copy.Files[CopyRoot + "material/v0001/mt_b.mtrl"]);
            check(inA.SequenceEqual([CopyRoot + "texture/same.tex", CopyRoot + "texture/d.tex"]) &&
                  inB.SequenceEqual([CopyRoot + "texture/same.tex", CopyRoot + "texture/n.tex"]),
                $"each material names its textures under the private texture folder: {string.Join(", ", inA)} | {string.Join(", ", inB)}");
            check(copy.Files.Keys.Count(key => key.EndsWith("/same.tex", StringComparison.Ordinal)) == 1,
                "a texture two materials share is in the copy once");

            var flat = PropCopies.Flatten(copy);
            check(flat.Count == copy.Files.Count + copy.Aliases.Count &&
                  copy.Aliases.All(pair => flat[pair.Key].SequenceEqual(copy.Files[pair.Value])) &&
                  copy.Files.All(pair => flat[pair.Key].SequenceEqual(pair.Value)),
                "Flatten has the files and each alias with its target's bytes");

            var gone = source with { Files = files.SetItem(CopyRoot + "texture/same.tex", "file:" + Path.Combine(dir, "deleted.tex")) };
            try
            {
                PropCopies.Build(gone, game);
                check(false, "a file: source that doesn't exist throws");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message == "The prop's model from Synthetic is gone. Pick it again.",
                    $"a file: source that doesn't exist throws the spec text: {ex.Message}");
            }
            try
            {
                PropCopies.Build(source, path => path.EndsWith(".mdl", StringComparison.Ordinal) ? null : game(path));
                check(false, "a game model the game lacks throws");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message == "The prop's model from Synthetic is gone. Pick it again.", "a game model the game lacks throws the same text");
            }
            var noGameTexture = PropCopies.Build(source, path => path.EndsWith("--d.tex", StringComparison.Ordinal) ? null : game(path));
            check(noGameTexture.Missing.SequenceEqual([CopyRoot + "texture/d.tex", CopyRoot + "texture/n.tex"]) &&
                  !noGameTexture.Files.ContainsKey(CopyRoot + "texture/d.tex"),
                $"a game texture the game lacks is missing, not an error: {string.Join(", ", noGameTexture.Missing)}");

            var other = source with
            {
                Body = 9001,
                Files = files.ToImmutableDictionary(pair => pair.Key.Replace("b9000", "b9001"), pair => pair.Value),
                Textures = textures.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Replace("b9000", "b9001")),
            };
            calls.Clear();
            var doc = new TimelineDocument(30, 30, null, [Prop("n1", source), Prop("n2", source with { }), Prop("n3", other), Prop("n4", null)], 5);
            var merged = PropCopies.Of(doc, game);
            check(calls.Count(path => path.EndsWith("m8044b0001.mdl", StringComparison.Ordinal)) == 2 && merged.Files.Count == 12 &&
                  merged.Aliases.Count == 1020 && merged.Missing.Length == 2,
                $"two events with one Source build it once and a second body adds its own files (model reads {calls.Count(path => path.EndsWith(".mdl", StringComparison.Ordinal))}, files {merged.Files.Count}, aliases {merged.Aliases.Count}, missing {merged.Missing.Length})");
            check(PropCopies.Of(new TimelineDocument(30, 30, null, [Prop("n4", null)], 2), game) is { Files.Count: 0, Aliases.Count: 0, Missing.Length: 0 },
                "a document without a prop from a mod has an empty copy");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private static TimelineEvent Prop(string key, TimelinePropSource? source)
        => new(key, "C198", TimelineLane.Prop, 0, 0, false, "", TimelineAccess.Editable, null, false,
            ImmutableDictionary<string, double>.Empty, Source: source);

    /// <summary>The Telephone's real model, material and textures, built into a private copy.</summary>
    private static void TelephoneCopy(Action<bool, string> check, Lumina.GameData? data)
    {
        if (!Directory.Exists(TelephoneMod))
        {
            Console.WriteLine($"[SKIP] props from mods: the Telephone mod isn't at {TelephoneMod}, so its copy isn't built");
            return;
        }
        var effective = ModOptionGroups.EffectiveFiles(TelephoneMod, new Dictionary<string, IReadOnlyList<string>>
        {
            ["(You're Not Gonna Reach My) Telephone"] = ["Main Install"],
            ["Color Options"] = ["Moogle Pink"],
        });
        const string model = "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";
        var source = PropModelSources.Resolve(TelephoneMod, "(You're Not Gonna Reach My) Telephone", model, 9001, 9000,
            effective, Game(data), Exists(data));
        var copy = PropCopies.Build(source, Game(data));
        var material = copy.Files[CopyRoot + "material/v0001/mt_m8044b0001_a.mtrl"];
        var named = TexturesOf(material);
        check(named.Length > 0 && named.All(path => path.StartsWith(CopyRoot + "texture/", StringComparison.Ordinal)),
            $"the Telephone's built material names only private textures: {string.Join(", ", named)}");
        check(copy.Files[CopyRoot + "model/w9001b9000.mdl"].SequenceEqual(File.ReadAllBytes(effective[model])),
            "the Telephone's built model is the mod's model");
        check(source.Textures.Values.Where(path => !source.Files.ContainsKey(path)).Order(StringComparer.Ordinal).SequenceEqual(copy.Missing),
            $"the Telephone's missing textures are the private paths with no file: {string.Join(", ", copy.Missing)}");
        check(named.All(path => copy.Files.ContainsKey(path) || copy.Missing.Contains(path)),
            "every texture the Telephone's built material names is in the copy or reported missing");
        check(PropCopies.Flatten(copy).Count == copy.Files.Count + 255, "the Telephone's flattened copy has its files and 255 aliases");
    }
}
