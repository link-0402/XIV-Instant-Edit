using System.Collections.Immutable;
using InstantEdit.Services;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Services;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Props from other mods: which models of a mod can be props, whether one rides a single bone, the private weapon
/// body it gets and the files its copy is built from. Real mods, checked from a local file, are only read, never written.
/// </summary>
internal static partial class PropModelSourcesFixture
{
    private const string GameFolder = @"E:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";

    /// <summary> The checks against real mods in your library, in an untracked PropModelSourcesFixture.Local.cs; skipped without it. </summary>
    static partial void RealMods(Action<bool, string> check);

    /// <summary> A real monster model (a prop that rides n_hara) from that file; null without it. </summary>
    static partial void RealMonster(ref string? model);

    public static void Run(Action<bool, string> check)
    {
        Part(check, "target set", () => TargetSets(check));
        Part(check, "body allocation", () => Bodies(check));
        Part(check, "body search", () => BodySearch(check));
        Part(check, "in-place mod bodies", () => InPlaceBodies(check));
        Part(check, "savable names", () => SavableNames(check));
        Part(check, "candidates", () => CandidateModels(check));
        Part(check, "qualification", () => Qualification(check));
        Part(check, "texture rules", () => TextureRules(check));
        Part(check, "private copy", () => PrivateCopy(check));
        Part(check, "timeline source", () => TimelineSource(check));
        Part(check, "body choice", () => BodyChoice(check));
        Part(check, "mod listing", () => ModListing(check));
        RealMods(check);
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
        // In game 2026-10-29 a summon on w9001 never showed (a phone case mod, private body or its own shipped b0200);
        // the same copy on w9901, the set of the game's still props, did.
        check(PropModelSources.TargetSet("chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl") == 9901,
            "a monster prop goes to weapon set 9901");
        check(PropModelSources.TargetSet("chara/demihuman/d1002/obj/equipment/e0001/model/d1002e0001_top.mdl") == 9901,
            "a demihuman prop goes to weapon set 9901");
        check(PropModelSources.TargetSet("chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl") == 9901 &&
              PropModelSources.TargetSet(@"CHARA\WEAPON\W9001\obj\body\b0200\model\w9001b0200.mdl") == 9901,
            "a w9001 weapon prop, whose summons the game never shows, goes to weapon set 9901");
        check(PropModelSources.TargetSet("chara/weapon/w9901/obj/body/b0026/model/w9901b0026.mdl") == 9901 &&
              PropModelSources.TargetSet("chara/weapon/w1962/obj/body/b0001/model/w1962b0001.mdl") == 1962,
            "other weapon props keep their set");
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

        var none = new HashSet<(ushort, ushort)>();
        check(PropModelSources.AllocateBody(9001, 9100, _ => false, none, body => body == 9100) == 9101,
            "a body busy outside the timeline is skipped");
        check(PropModelSources.AllocateBody(9001, 9998, p => p.EndsWith("b9999.imc"), new HashSet<(ushort, ushort)> { (9001, 9998) }, _ => false) == 9000,
            "the search walks upward from its start and wraps from 9999 to 9000");
        check(PropModelSources.AllocateBody(9001, 9500, _ => false, none, body => body != 9499) == 9499,
            "the search reaches the body just below its start last");
        check(PropModelSources.AllocateBody(9001, 9500, _ => false, none, _ => true) is null,
            "no private body when every one is busy outside the timeline");
    }

    /// <summary>The start of the search for a free body, and the search itself over a fake Penumbra.</summary>
    private static void BodySearch(Action<bool, string> check)
    {
        var phone = SourceAt("phones", MonsterModel, 9001, 9000);
        var start = PropModelSources.StartBody(phone);
        check(start is >= 9000 and <= 9999 && PropModelSources.StartBody(phone with { }) == start &&
              PropModelSources.StartBody(PropModelSources.Rebodied(phone, 9123)) == start,
            $"the search starts at the same body for the same pick, whichever body it was resolved under (start {start})");
        var otherMod = PropModelSources.StartBody(SourceAt("other phones", MonsterModel, 9001, 9000));
        var otherFiles = PropModelSources.StartBody(phone with { Files = phone.Files.SetItem(PropModelSources.ModelPath(9001, 9000), @"file:C:\m\red.mdl") });
        check(otherMod != start && otherFiles != start,
            $"another mod or other source files start elsewhere ({start}, {otherMod}, {otherFiles})");

        // The walk from the start: b0 is served by a mod, b1 is in the timeline, the game ships b2, the animation's mod maps b3.
        var walk = Enumerable.Range(0, 6).Select(i => (ushort)(9000 + (start - 9000 + i) % 1000)).ToArray();
        var asked = new List<string>();
        Task<string> Penumbra(string path)
        {
            asked.Add(path);
            return Task.FromResult(path == PropModelSources.ModelPath(9001, walk[0]) ? @"D:\Mods\Phone Mod\phone.mdl" : path.ToUpperInvariant().Replace('/', '\\'));
        }
        var found = PropModelSources.FindBody(phone, new HashSet<(ushort, ushort)> { (9001, walk[1]) }, new HashSet<(ushort, ushort)> { (9001, walk[3]) },
            path => path == $"chara/weapon/w9001/obj/body/b{walk[2]:D4}/b{walk[2]:D4}.imc", Penumbra).GetAwaiter().GetResult();
        check(found.Start == start && found.Free == walk[4] && found.Busy.SetEquals([walk[0], walk[3]]),
            $"the search skips a body a mod serves, one the timeline has, one the game ships and one the animation's mod maps (free {found.Free}, busy {string.Join(", ", found.Busy)})");
        check(asked.SequenceEqual([PropModelSources.ModelPath(9001, walk[0]), PropModelSources.ModelPath(9001, walk[4])]),
            $"only bodies past the timeline, game and mod checks are resolved: {string.Join(", ", asked)}");
        check(!found.Outside(walk[4]) && found.Outside(walk[0]) && found.Outside(walk[5]),
            "outside the timeline only the free body counts as free, so a body never asked about isn't given out");

        var empty = new TimelineDocument(60, 60, null, [], 1);
        check(PropModelSources.BodyFor(empty, phone, _ => false, found.Start, found.Outside) == walk[4],
            "the placed prop gets the body the search found");
        var later = new TimelineDocument(60, 60, null, [Prop("t1", SourceAt("x", MonsterModel.Replace("b0001", "b0009"), 9001, walk[4]))], 2);
        check(PropModelSources.BodyFor(later, phone, _ => false, found.Start, found.Outside) is null,
            "a body the timeline took after the search isn't searched past");
        var same = new TimelineDocument(60, 60, null, [Prop("t1", PropModelSources.Rebodied(phone, 9777))], 2);
        check(PropModelSources.BodyFor(same, phone, _ => false, found.Start, found.Outside) == 9777,
            "a prop of the same mod, model and files in the timeline still shares its body");

        var full = PropModelSources.FindBody(phone, new HashSet<(ushort, ushort)>(), new HashSet<(ushort, ushort)>(), _ => false,
            path => Task.FromResult("game:" + path)).GetAwaiter().GetResult();
        check(full.Free is null && full.Busy.Count == 1000 && PropModelSources.BodyFor(empty, phone, _ => false, full.Start, full.Outside) is null,
            "no body when Penumbra serves every one");
    }

    /// <summary>The private bodies the animation's own mod maps in any option, from its meta.json.</summary>
    private static void InPlaceBodies(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-prop-inplace-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            check(PropModelSources.ModBodies(dir).Count == 0, "a mod without a readable meta.json maps no private body");
            var phone = SourceAt("phones", MonsterModel, 9001, 9000);
            var start = PropModelSources.StartBody(phone);
            static System.Text.Json.Nodes.JsonObject Files(params string[] gamePaths)
            {
                var result = new System.Text.Json.Nodes.JsonObject();
                foreach (var gamePath in gamePaths)
                    result[gamePath] = "x.mdl";
                return result;
            }
            var meta = new System.Text.Json.Nodes.JsonObject
            {
                ["FileVersion"] = 4,
                ["Name"] = "Phone Mod",
                ["DefaultData"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Files"] = Files("chara/weapon/w9001/obj/body/b9000/model/w9001b9000.mdl", "chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl"),
                },
                ["Groups"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Name"] = "Emote", ["Type"] = "Single", ["Priority"] = 0,
                        ["Options"] = new System.Text.Json.Nodes.JsonArray(
                            new System.Text.Json.Nodes.JsonObject { ["Name"] = "Off" },
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["Name"] = "On",
                                ["Files"] = Files($"Chara/Weapon/W9001/obj/body/b{start:D4}/material/v0001/mt_phone_a.mtrl",
                                    "chara/weapon/w0528/obj/body/b9001/texture/x.tex"),
                            }),
                    }),
            };
            File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString());
            var bodies = PropModelSources.ModBodies(dir);
            check(bodies.SetEquals([(9001, 9000), (9001, start), (528, 9001)]),
                $"the mod's private bodies come from every option, in any case, and only b9000..b9999: {string.Join(", ", bodies)}");
            check(PropModelSources.AllocateBody(9001, 9000, _ => false, new HashSet<(ushort, ushort)>(), body => bodies.Contains((9001, body))) == 9001,
                "a b9000 the animation's mod maps is skipped, b9001 given instead");
            var found = PropModelSources.FindBody(phone, new HashSet<(ushort, ushort)>(), bodies, _ => false, Task.FromResult).GetAwaiter().GetResult();
            check(found.Free != start && found.Free != 9000 && found.Busy.Contains(start),
                $"a body the animation's mod maps is busy even with its option off (free {found.Free})");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>Material and texture names a save takes: an absolute material name, unsafe texture names, a material name refused.</summary>
    private static void SavableNames(Action<bool, string> check)
    {
        check(PropModelSources.MaterialFileName("/chara/weapon/w9001/obj/body/b0200/material/v0001/MT_W9001B0200_A.mtrl") == "mt_w9001b0200_a.mtrl" &&
              PropModelSources.MaterialFileName(@"\chara\x\mt_b.mtrl") == "mt_b.mtrl" &&
              PropModelSources.MaterialFileName("/mt_c.mtrl") == "mt_c.mtrl" && PropModelSources.MaterialFileName("mt_d.mtrl") == "mt_d.mtrl" &&
              PropModelSources.MaterialFileName("material/") == "",
            "a material name with folders stands for its file name");
        const string root = "chara/weapon/w9001/obj/body/b9000/";
        var odd = PropModelSources.PrivateTexturePath(root, "chara/a/texture/my tex (1).tex", ImmutableDictionary<string, string>.Empty);
        check(odd == root + "texture/my_tex__1_.tex" && InstantEdit.Services.Animations.AnimationDependencies.SafeGamePath(odd),
            $"a texture name with a space or brackets gets _ for each (got {odd})");
        var owners = new Dictionary<string, string> { [odd] = "chara/a/texture/my tex (1).tex" };
        check(PropModelSources.PrivateTexturePath(root, "chara/b/texture/my+tex_(1).tex", owners) == root + "texture/my_tex__1__2.tex" &&
              PropModelSources.PrivateTexturePath(root, "chara/a/texture/my tex (1).tex", owners) == odd,
            "two names that become the same get _2, and a texture keeps the name it was given");
        check(PropModelSources.PrivateTexturePath(root, "chara/a/texture/v01_x-y.z_d.tex", ImmutableDictionary<string, string>.Empty) == root + "texture/v01_x-y.z_d.tex",
            "a texture name the game takes stays as it is");

        var source = SourceAt("phones", MonsterModel, 9001, 9000);
        var fine = source with { Files = source.Files.Add(root + "material/v0001/mt_phone_a.mtrl", "file:C:/m/a.mtrl") };
        var spaced = source with { Files = source.Files.Add(root + "material/v0001/mt phone (a).mtrl", "file:C:/m/a.mtrl") };
        check(!PropModelSources.MaterialUnsavable(fine) && PropModelSources.MaterialUnsavable(spaced) &&
              PropModelSources.UnsavableMaterial == "This model's material name can't be saved.",
            "a material name a save would refuse is refused at the pick");
        var gameMaterial = source with { Files = source.Files.Add(root + "material/v0001/mt_phone_b.mtrl", "game:chara/monster/m8044/obj/body/b0001/material/v0001/mt_phone_b.mtrl") };
        check(PropModelSources.PickProblem(fine, _ => false) == null && PropModelSources.PickProblem(spaced, _ => true) == PropModelSources.UnsavableMaterial &&
              PropModelSources.PickProblem(gameMaterial, _ => true) == null &&
              PropModelSources.PickProblem(gameMaterial, _ => false) == "This model's material mt_phone_b.mtrl is in neither the mod nor the game.",
            "a material the game is to give but doesn't have is refused at the pick, as every save would fail");
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
        check(!PropModelSources.Qualifies("chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl", [1, 2, 3]),
            "a model that can't be read doesn't qualify");
    }

    private static string Listing(TimelinePropSource source)
        => string.Join("; ", source.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} = {pair.Value}"));

    /// <summary>
    /// An MTRL listing <paramref name="textures"/> (path and DX11 flag) behind the shader name, with an empty
    /// shader section: enough for the texture table to read.
    /// </summary>
    internal static byte[] Material(params (string Path, bool Dx11)[] textures)
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
    /// The texture rules on a real monster model with a made-up material and mod files: a texture the mod ships
    /// only as its DX11 <c>--</c> file, two textures of one file name from different folders, and one found nowhere.
    /// </summary>
    private static void TextureRules(Action<bool, string> check)
    {
        string? realModel = null;
        RealMonster(ref realModel);
        if (realModel is null || !File.Exists(realModel))
        {
            Console.WriteLine("[SKIP] props from mods: no real monster model, so the texture rules aren't run");
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
                [model] = realModel,
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
            var dashed = new Dictionary<string, string> { [root + "--y.tex"] = "chara/a/--y.tex" };
            check(PropModelSources.PrivateTexturePath("chara/weapon/w9001/obj/body/b9000/", "chara/b/y.tex", dashed, dx11Copy: true) == root + "y_2.tex" &&
                  PropModelSources.PrivateTexturePath("chara/weapon/w9001/obj/body/b9000/", "chara/b/y.tex", dashed) == root + "y.tex",
                "a texture with a DX11 copy takes a name whose -- name is free too");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
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

    private const string MonsterModel = "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";

    private static TimelinePropSource SourceAt(string mod, string model, ushort set, ushort body)
        => new(mod, mod, model, set, body,
            ImmutableDictionary<string, string>.Empty.Add($"chara/weapon/w{set:D4}/obj/body/b{body:D4}/model/w{set:D4}b{body:D4}.mdl", "game:" + model),
            ImmutableDictionary<string, string>.Empty);

    /// <summary>The events one Add of <paramref name="request"/> makes, keyed n1, n2, ... as the editor would.</summary>
    private static TimelineDocument Added(TimelineDocument doc, TimelineAddRequest request)
    {
        var events = TimelineTemplates.Create(request).Select((ev, i) => ev with { Key = "n" + (doc.NextKey + i) }).ToArray();
        return doc with { Events = doc.Events.AddRange(events), NextKey = doc.NextKey + events.Length };
    }

    private static TimelineEvent Summon(TimelineDocument doc) => doc.Events.Last(ev => ev.Magic == "C198");

    /// <summary>
    /// A prop from a mod on the timeline: its summon slot, the snapshot on its C198 and what an edit does to it, and
    /// the private bodies a document already uses.
    /// </summary>
    private static void TimelineSource(Action<bool, string> check)
    {
        var empty = new TimelineDocument(100, 100, null, [], 1);
        var source = SourceAt("Monster Prop", MonsterModel, 9001, 9000);
        var request = new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(9001, 9000, 1, null, source), ClipLength: 100,
            Summon: 1);
        var sourced = Added(empty, request);
        var c198 = Summon(sourced);
        check(c198.Values["SummonId"] == 1, "a prop added on Summon 1 writes SummonId 1");
        check(Summon(Added(empty, request with { Summon = 0 })).Values["SummonId"] == 0 &&
              Summon(Added(empty, new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(1962, 1, 1, null)))).Values["SummonId"] == 0,
            "a prop added without a slot writes SummonId 0");
        check(ReferenceEquals(c198.Source, source) && sourced.Events.Count(ev => ev.Source != null) == 1,
            "the C198 of a prop from a mod carries its source, and only the C198");
        check(Summon(Added(empty, new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(1962, 1, 1, null)))).Source == null,
            "a game prop's C198 has no source");

        var other = TimelineEdits.Replace(sourced, c198 with { Values = c198.Values.SetItem("Model", 1962).SetItem("Body", 1) });
        check(TimelineEdits.Find(other, c198.Key) is { Source: null } && Summon(other).Values["Model"] == 1962,
            "giving the prop another model drops its source");
        var body = TimelineEdits.Replace(sourced, c198 with { Values = c198.Values.SetItem("Body", 9001) });
        check(TimelineEdits.Find(body, c198.Key) is { Source: null }, "giving the prop another body drops its source");
        var moved = TimelineEdits.Replace(sourced, c198 with { Start = 10, Values = c198.Values.SetItem("Variant", 2) });
        check(TimelineEdits.Find(moved, c198.Key) is { Start: 10, Source: { } kept } && ReferenceEquals(kept, source),
            "moving the prop or changing its variant keeps its source");

        check(!TimelineEdits.Same(c198, c198 with { Source = null }) && !TimelineEdits.Same(c198, c198 with { Source = source with { Body = 9001 } }) &&
              !TimelineEdits.Same(c198, c198 with { Source = source with { SourceModel = "chara/monster/m8044/obj/body/b0002/model/m8044b0002.mdl" } }),
            "events with another source, or none, differ");
        check(TimelineEdits.Same(c198, c198 with { Source = source with { Files = source.Files.ToImmutableDictionary() } }),
            "events whose sources have the same set, body and model are the same");
        var place = new TimelinePropPlace(0, false, null, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.Zero, 1);
        check(!ReferenceEquals(TimelineEdits.Replace(sourced, c198 with { Place = place }), sourced),
            "a changed place is an edit");

        var two = Added(sourced, request with { Prop = new TimelinePropModel(528, 9000, 1, null, SourceAt("Weapon Prop", "chara/weapon/w0528/obj/body/b0016/model/w0528b0016.mdl", 528, 9000)) });
        var saved = two with { Events = two.Events.Add(Prop("t9", null) with { Values = ImmutableDictionary<string, double>.Empty.Add("Model", 9001).Add("Body", 9003) }) };
        var stale = saved with { Events = saved.Events.Add(Prop("t8", SourceAt("Old", MonsterModel, 9001, 9007)) with
        {
            Values = ImmutableDictionary<string, double>.Empty.Add("Model", 9001).Add("Body", 9008),
        }) };
        var taken = PropModelSources.Taken(stale);
        check(taken.SetEquals([(9001, 9000), (528, 9000), (9001, 9003), (9001, 9007), (9001, 9008)]),
            $"the bodies a document uses: each source's, each sourced summon's model and body, and a private body loaded without one: {string.Join(", ", taken)}");
        check(PropModelSources.Taken(Added(empty, new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(1962, 1, 1, null)))).Count == 0,
            "a game prop takes no private body");
    }

    /// <summary>
    /// The picker's list of a mod's models, end to end over a made-up mod folder: Penumbra's settings (or none), every
    /// other group's default, the effective files, the candidates and their qualification.
    /// </summary>
    private static void ModListing(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-prop-listing-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "w.mdl", "plain.mdl", "fancy.mdl", "spare.mdl", "gear.mdl" })
                File.WriteAllBytes(Path.Combine(dir, name), [1, 2, 3]);
            // A real monster model, when there is one, qualifies as a monster on n_hara; made-up bytes don't.
            string? realModel = null;
            RealMonster(ref realModel);
            var realMonster = realModel is not null && File.Exists(realModel);
            if (realMonster)
                File.Copy(realModel!, Path.Combine(dir, "m.mdl"));
            else
                File.WriteAllBytes(Path.Combine(dir, "m.mdl"), [1, 2, 3]);
            static System.Text.Json.Nodes.JsonObject Files(params (string GamePath, string File)[] files)
            {
                var result = new System.Text.Json.Nodes.JsonObject();
                foreach (var (gamePath, file) in files)
                    result[gamePath] = file;
                return result;
            }
            var meta = new System.Text.Json.Nodes.JsonObject
            {
                ["FileVersion"] = 4,
                ["Name"] = "listing",
                ["DefaultData"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Files"] = Files(("chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl", "w.mdl"), (MonsterModel, "m.mdl"),
                        ("chara/equipment/e0001/model/c0101e0001_top.mdl", "gear.mdl")),
                },
                ["Groups"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Name"] = "Phone", ["Type"] = "Single", ["Priority"] = 0, ["DefaultSettings"] = 1,
                        ["Options"] = new System.Text.Json.Nodes.JsonArray(
                            new System.Text.Json.Nodes.JsonObject { ["Name"] = "Plain", ["Files"] = Files(("chara/demihuman/d1002/obj/equipment/e0001/model/d1002e0001_top.mdl", "plain.mdl")) },
                            new System.Text.Json.Nodes.JsonObject { ["Name"] = "Fancy", ["Files"] = Files(("chara/demihuman/d1003/obj/equipment/e0001/model/d1003e0001_top.mdl", "fancy.mdl")) }),
                    },
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["Name"] = "Extra", ["Type"] = "Multi", ["Priority"] = 0,
                        ["Options"] = new System.Text.Json.Nodes.JsonArray(
                            new System.Text.Json.Nodes.JsonObject { ["Name"] = "Spare", ["Files"] = Files(("chara/weapon/w0528/obj/body/b0016/model/w0528b0016.mdl", "spare.mdl")) }),
                    }),
            };
            File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString());
            static string Show(IEnumerable<PropModelSources.PropCandidate> rows) => string.Join(", ", rows.Select(row => $"{row.Label} {(row.Qualifies ? "yes" : "no")}"));
            var monster = realMonster ? "yes" : "no";

            var picked = PropModelSources.ModModels(dir, new Dictionary<string, IReadOnlyList<string>> { ["Extra"] = ["Spare"] });
            check(Show(picked) == $"d1003e0001_top.mdl no, m8044b0001.mdl {monster}, w0528b0016.mdl yes, w9001b0200.mdl yes" &&
                  picked[0].GamePath == "chara/demihuman/d1003/obj/equipment/e0001/model/d1003e0001_top.mdl",
                $"a mod's models list under Penumbra's options, a group they leave out taking its default: {Show(picked)}");
            var defaults = PropModelSources.ModModels(dir, null);
            check(Show(defaults) == $"d1003e0001_top.mdl no, m8044b0001.mdl {monster}, w9001b0200.mdl yes",
                $"without Penumbra's settings every group takes its default: {Show(defaults)}");
            check(PropModelSources.ModFiles(dir, null).ContainsKey("chara/equipment/e0001/model/c0101e0001_top.mdl") &&
                  !defaults.Any(row => row.GamePath.StartsWith("chara/equipment/", StringComparison.Ordinal)),
                "gear the mod serves isn't listed");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The body a prop picked from a mod gets: the same mod's same model shares one, another model gets its own even
    /// when its material has the same name, and the merged copy keeps both materials apart.
    /// </summary>
    private static void BodyChoice(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-prop-bodies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var red = Path.Combine(dir, "red.mtrl");
            var blue = Path.Combine(dir, "blue.mtrl");
            File.WriteAllBytes(red, Material(("chara/a/texture/red.tex", false)));
            File.WriteAllBytes(blue, Material(("chara/b/texture/blue.tex", false)));
            const string first = "chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl";
            const string second = "chara/monster/m8044/obj/body/b0002/model/m8044b0002.mdl";
            TimelinePropSource Picked(string model, ushort body, string material)
            {
                var root = $"chara/weapon/w9001/obj/body/b{body:D4}/";
                var textures = ImmutableDictionary<string, string>.Empty;
                foreach (var texture in TexturesOf(File.ReadAllBytes(material)))
                    textures = textures.Add(texture, root + "texture/" + Path.GetFileName(texture));
                return new TimelinePropSource("phones", "Phones", model, 9001, body, ImmutableDictionary<string, string>.Empty
                    .Add($"{root}model/w9001b{body:D4}.mdl", "game:" + model)
                    .Add($"{root}material/v0001/mt_phone_a.mtrl", "file:" + material), textures);
            }
            TimelineDocument With(TimelineDocument doc, TimelinePropSource source)
                => Added(doc, new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(source.Set, source.Body, 1, null, source), ClipLength: 60));
            Func<string, bool> shipped = path => path.EndsWith("b9000.imc", StringComparison.Ordinal);

            var doc = new TimelineDocument(60, 60, null, [], 1);
            var body1 = PropModelSources.BodyFor(doc, "phones", first, 9001, shipped);
            check(body1 == 9001, $"the first prop gets the lowest body the game doesn't ship (got {body1})");
            doc = With(doc, Picked(first, body1!.Value, red));
            check(PropModelSources.BodyFor(doc, "phones", first, 9001, shipped) == body1 &&
                  PropModelSources.BodyFor(doc, "PHONES", first.ToUpperInvariant(), 9001, shipped) == body1,
                "a second prop from the same mod and model reuses its body");
            var body2 = PropModelSources.BodyFor(doc, "phones", second, 9001, shipped);
            check(body2 == 9002, $"a prop from another model gets its own body (got {body2})");
            check(PropModelSources.BodyFor(doc, "other phones", first, 9001, shipped) == 9002,
                "the same model from another mod gets its own body");
            doc = With(doc, Picked(second, body2!.Value, blue));
            var copy = PropCopies.Of(doc, path => path.EndsWith(".mdl", StringComparison.Ordinal) ? [9] : null);
            var redCopy = copy.Files.GetValueOrDefault("chara/weapon/w9001/obj/body/b9001/material/v0001/mt_phone_a.mtrl");
            var blueCopy = copy.Files.GetValueOrDefault("chara/weapon/w9001/obj/body/b9002/material/v0001/mt_phone_a.mtrl");
            check(redCopy != null && blueCopy != null &&
                  TexturesOf(redCopy).SequenceEqual(["chara/weapon/w9001/obj/body/b9001/texture/red.tex"]) &&
                  TexturesOf(blueCopy).SequenceEqual(["chara/weapon/w9001/obj/body/b9002/texture/blue.tex"]),
                $"two materials of one name stay apart, each under its own body: {string.Join(", ", copy.Files.Keys.Order(StringComparer.Ordinal))}");
            // A pick is resolved under some body first; the body it ends on comes from its snapshot.
            check(PropModelSources.BodyFor(doc, Picked(first, 9001, red), shipped, 9000, _ => false) == 9001 &&
                  PropModelSources.BodyFor(doc, Picked(first, 9006, red), shipped, 9000, _ => false) == 9001,
                "the same model with the same options reuses its body, whichever body it was resolved under");
            var green = Path.Combine(dir, "green.mtrl");
            File.WriteAllBytes(green, Material(("chara/a/texture/red.tex", false)));
            var recoloured = Picked(first, 9001, green);
            var body3 = PropModelSources.BodyFor(doc, recoloured, shipped, 9000, _ => false);
            check(body3 == 9003, $"the same model after its options changed (another material file) gets a new body (got {body3})");
            check(PropModelSources.BodyFor(doc, Picked(first, 9001, red) with { ModDirectory = "other phones" }, shipped, 9000, _ => false) == 9003,
                "the same files from another mod get their own body");
            var withThird = With(doc, PropModelSources.Rebodied(recoloured, body3!.Value));
            var both = PropCopies.Of(withThird, path => path.EndsWith(".mdl", StringComparison.Ordinal) ? [9] : null);
            check(both.Files.ContainsKey("chara/weapon/w9001/obj/body/b9001/material/v0001/mt_phone_a.mtrl") &&
                  both.Files.ContainsKey("chara/weapon/w9001/obj/body/b9003/material/v0001/mt_phone_a.mtrl") &&
                  both.Files.ContainsKey("chara/weapon/w9001/obj/body/b9003/model/w9001b9003.mdl"),
                $"the earlier pick and the re-pick each have their own copy: {string.Join(", ", both.Files.Keys.Order(StringComparer.Ordinal))}");
            var moved = PropModelSources.Rebodied(Picked(first, 9001, red), 9004);
            var direct = Picked(first, 9004, red);
            check(moved.Body == 9004 && moved.Files.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(direct.Files.OrderBy(p => p.Key, StringComparer.Ordinal)) &&
                  moved.Textures.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(direct.Textures.OrderBy(p => p.Key, StringComparer.Ordinal)) &&
                  PropModelSources.SameFiles(direct, Picked(first, 9001, red)) && !PropModelSources.SameFiles(direct, Picked(first, 9001, green)),
                "a snapshot moved to another body is the one made there, and two snapshots compare whichever body they are under");

            var full = new TimelineDocument(60, 60, null, [.. Enumerable.Range(9000, 1000).Select(n =>
                Prop("t" + n, SourceAt("x" + n, MonsterModel.Replace("b0001", $"b{n - 8000:D4}"), 9001, (ushort)n)))], 1);
            check(PropModelSources.BodyFor(full, "phones", first, 9001, _ => false) is null &&
                  PropModelSources.BodyFor(full, Picked(first, 9001, red), _ => false, 9000, _ => false) is null, "no body when every private one is used");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
