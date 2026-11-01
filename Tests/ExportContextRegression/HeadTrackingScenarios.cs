using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Head tracking: reading a body skeleton parameters file's look-at groups, and the edit that moves the back's turn
/// below the chest and scales each group's strength. With the game installed, every race's vanilla file is checked.
/// </summary>
internal static class HeadTrackingScenarios
{
    public static void Run(string testRoot)
    {
        CheckSynthetic();
        CheckRefusals();
        CheckVanillaFiles();
        CheckModFiles(testRoot);
        CheckRaceChanges();
        CheckOtherMods(testRoot);
    }

    private static void CheckRaceChanges()
    {
        Require(HeadTrackingService.NextRace(801, null) == (801, false),
            "head tracking: a character without a draw object (mid-redraw) keeps its race, so a preview's redraw doesn't end it");
        Require(HeadTrackingService.NextRace(801, 1801) == (1801, true) && HeadTrackingService.NextRace(null, 801) == (801, true),
            "head tracking: another race, or the first one seen, is a change");
        Require(HeadTrackingService.NextRace(801, 801) == (801, false), "head tracking: the same race is no change");
    }

    private static void CheckOtherMods(string testRoot)
    {
        var root = Path.Combine(testRoot, "HeadTrackingOthers");
        var gamePath = HeadTrackingEdit.GamePath(801);
        foreach (var (folder, path) in new[] { ("Ours", gamePath), ("Ours renamed", gamePath), ("Other skp", gamePath), ("Other race", HeadTrackingEdit.GamePath(1801)) })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            PhysicsModService.CreateModFiles(ModFileKind.SkeletonParams, Path.Combine(root, folder), folder, "test", path, [1]);
        }
        var ours = ModMetadata.ReadModStableIdentifier(Path.Combine(root, "Ours"))!.Value;
        // A copy of our mod under another folder name keeps its identifier, as Penumbra's rename does.
        File.Copy(Path.Combine(root, "Ours", "meta.json"), Path.Combine(root, "Ours renamed", "meta.json"), true);
        var found = HeadTrackingService.ModsMapping(root, ["Ours", "Ours renamed", "Other skp", "Other race", "Missing"], gamePath, ours);
        Require(found.SequenceEqual(["Other skp"]),
            $"head tracking: other mods mapping the race's file are found, never ours (even renamed) ({string.Join(", ", found)})");
    }

    private static void CheckModFiles(string testRoot)
    {
        var folder = Path.Combine(testRoot, "HeadTrackingMod");
        Directory.CreateDirectory(folder);
        var kind = ModFileKind.SkeletonParams;
        var c0801 = HeadTrackingEdit.GamePath(801);
        var c1801 = HeadTrackingEdit.GamePath(1801);
        JsonObject Files() => JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!["DefaultData"]!["Files"]!.AsObject();
        bool Exists(string relative) => File.Exists(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));

        var first = PhysicsModService.CreateModFiles(kind, folder, "Head tracking", "test", c0801, [1, 2, 3]);
        Require(first == "skeleton/skl_c0801b0001.1.skp" && (string?)Files()[c0801] == "skeleton\\skl_c0801b0001.1.skp" &&
                PhysicsModService.ModFile(kind, folder, c0801) is { } mapped && File.ReadAllBytes(mapped).SequenceEqual(new byte[] { 1, 2, 3 }),
            "head tracking mod: a new mod maps the race's skeleton parameters to its first file");

        var second = PhysicsModService.UpdateModFiles(kind, folder, c0801, [4]);
        Require(second == "skeleton/skl_c0801b0001.2.skp" && !Exists(first) && Exists(second),
            "head tracking mod: each save maps a new file name and deletes the old one");

        PhysicsModService.UpdateModFiles(kind, folder, c1801, [5]);
        var third = PhysicsModService.UpdateModFiles(kind, folder, c0801, [6]);
        Require(third.EndsWith(".3.skp", StringComparison.Ordinal) && Files().Count == 2 && Exists("skeleton/skl_c1801b0001.1.skp"),
            "head tracking mod: another race in the mod stays when this race saves again");

        Require(PhysicsModService.RemoveModFile(kind, folder, c0801) && Files().Count == 1 && Files()[c1801] is not null && !Exists(third),
            "head tracking mod: removing a race drops only its mapping and its file");
        Require(!PhysicsModService.RemoveModFile(kind, folder, c0801), "head tracking mod: removing a race that isn't mapped changes nothing");
    }

    private static void CheckSynthetic()
    {
        var file = Build();
        var layout = SkeletonParams.ReadLookAt(file);
        Require(layout.Params.Count == 4 && layout.Groups.Select(g => g.Id).SequenceEqual(["backbone", "base", "eyes"]) &&
                layout.Groups[0].Elements is [{ Bone: "j_sebo_c", Parent: "j_sebo_b", ParamIndex: 0 }] &&
                layout.Groups[1].Elements.Select(e => e.Bone).SequenceEqual(["j_kubi", "j_kao"]),
            "head tracking: the look-at groups, their bones and parameters are read");
        Require(Math.Abs(layout.Params[0].Gain - .309f) < 1e-6 && Math.Abs(layout.Params[1].Gain - .6f) < 1e-6,
            "head tracking: parameter gains are read");
        CheckEdits(file, "synthetic");
    }

    /// <summary> The checks every file must pass: game settings unchanged, Lower back, strengths. </summary>
    private static void CheckEdits(byte[] file, string name)
    {
        var vanilla = SkeletonParams.ReadLookAt(file);
        Require(HeadTrackingEdit.Apply(file, HeadTrackingSettings.Game).AsSpan().SequenceEqual(file),
            $"head tracking ({name}): the game's settings give the file back unchanged");

        var lower = HeadTrackingEdit.Apply(file, new HeadTrackingSettings(LowerBack: true));
        var element = SkeletonParams.ReadLookAt(lower).Groups.Single(g => g.Id == HeadTrackingEdit.Backbone).Elements.Single();
        var names = vanilla.Groups.Single(g => g.Id == HeadTrackingEdit.Backbone).Elements.Single().Offset + 2;
        var outside = Enumerable.Range(0, file.Length).Where(i => i < names || i >= names + 64).All(i => lower[i] == file[i]);
        Require(element is { Bone: "j_sebo_b", Parent: "j_sebo_a" } && outside && lower.Length == file.Length,
            $"head tracking ({name}): Lower back turns j_sebo_b under j_sebo_a and changes nothing else");

        var scaled = SkeletonParams.ReadLookAt(HeadTrackingEdit.Apply(file, new HeadTrackingSettings(Back: 0, NeckHead: .5f, Eyes: 0)));
        float Gain(LookAtLayout layout, string group, int element) =>
            layout.Params[layout.Groups.Single(g => g.Id == group).Elements[element].ParamIndex].Gain;
        Require(Gain(scaled, HeadTrackingEdit.Backbone, 0) == 0 && Gain(scaled, HeadTrackingEdit.Eyes, 0) == 0 &&
                Math.Abs(Gain(scaled, HeadTrackingEdit.Base, 0) - Gain(vanilla, HeadTrackingEdit.Base, 0) / 2) < 1e-6 &&
                Math.Abs(Gain(scaled, HeadTrackingEdit.Base, 1) - Gain(vanilla, HeadTrackingEdit.Base, 1) / 2) < 1e-6,
            $"head tracking ({name}): strengths scale each group's gains (0 = off, 50 % = half)");
    }

    private static void CheckRefusals()
    {
        void Refused(string what, byte[] file, HeadTrackingSettings settings) =>
            Require(Throws(() => HeadTrackingEdit.Apply(file, settings)), $"head tracking: {what} is refused");

        var lower = new HeadTrackingSettings(LowerBack: true);
        Refused("a truncated file", Build()[..0x30], lower);
        var magic = Build();
        magic[0] = (byte)'x';
        Refused("a file that isn't skeleton parameters", magic, lower);
        var flag = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(flag.AsSpan(8), 0);
        Refused("a file without look-at", flag, lower);
        Refused("a missing group", Build(backboneId: "spine"), new HeadTrackingSettings(Back: .5f));
        Refused("a back turn on another bone", Build(backboneBone: "j_sebo_x"), lower);
        Refused("a parameter two groups share", Build(kubiParam: 0), new HeadTrackingSettings(Back: .5f));
        Require(!Throws(() => HeadTrackingEdit.Apply(Build(backboneBone: "j_sebo_x"), new HeadTrackingSettings(Back: .5f))),
            "head tracking: strengths alone don't need the game's back bone");
    }

    private static void CheckVanillaFiles()
    {
        var sqpack = PhysicsScenarios.GameSqpack();
        if (sqpack is null)
        {
            Console.WriteLine("[SKIP] head tracking: the game isn't installed where XIVLauncher says, so vanilla files aren't checked");
            return;
        }
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var count = 0;
        foreach (var race in GameRaces.All)
        {
            if (game.GetFile(HeadTrackingEdit.GamePath(race.Code))?.Data is not { } bytes)
                continue;
            var layout = SkeletonParams.ReadLookAt(bytes);
            Require(layout.Groups.Single(g => g.Id == HeadTrackingEdit.Backbone).Elements is [{ Bone: "j_sebo_c", Parent: "j_sebo_b" }] &&
                    layout.Groups.Single(g => g.Id == HeadTrackingEdit.Base).Elements.Select(e => (e.Bone, e.Parent))
                        .SequenceEqual([("j_kubi", "j_sebo_c"), ("j_kao", "j_kubi")]) &&
                    layout.Groups.Single(g => g.Id == HeadTrackingEdit.Eyes).Elements.All(e => e.Parent == "j_kao"),
                $"head tracking: c{race.Code:D4}'s look-at groups are the ones the edit expects");
            CheckEdits(bytes, $"c{race.Code:D4}");
            count++;
        }
        Require(count >= 16, $"head tracking: all {count} of the game's body skeleton parameter files were checked");
        var c0801 = SkeletonParams.ReadLookAt(game.GetFile(HeadTrackingEdit.GamePath(801))!.Data);
        Require(Math.Abs(c0801.Params[0].Gain - .309f) < 1e-3 && Math.Abs(c0801.Params[1].Gain - .6f) < 1e-3 &&
                Math.Abs(c0801.Params[2].Gain - 1) < 1e-3 && Math.Abs(c0801.Params[3].Gain - 1) < 1e-3,
            "head tracking: c0801's gains are the game's (back 0.309, neck 0.6, head 1, eyes 1)");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    /// <summary> A file laid out like the game's c0801 one: four parameters, groups backbone, base and eyes. </summary>
    private static byte[] Build(string backboneId = "backbone", string backboneBone = "j_sebo_c", byte kubiParam = 1)
    {
        float[] gains = [.309f, .6f, 1, 1];
        (string Id, (byte Param, string Bone, string Parent)[] Elements)[] groups =
        [
            (backboneId, [(0, backboneBone, "j_sebo_b")]),
            ("base", [(kubiParam, "j_kubi", "j_sebo_c"), (2, "j_kao", "j_kubi")]),
            ("eyes", [(3, "j_f_eye_r", "j_kao"), (3, "j_f_eye_l", "j_kao")]),
        ];
        var bytes = new List<byte>();
        bytes.AddRange("plks0031"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(2u));
        bytes.AddRange(BitConverter.GetBytes(0x20));
        bytes.AddRange(BitConverter.GetBytes(0x20));
        bytes.AddRange(new byte[12]);
        bytes.Add((byte)gains.Length);
        bytes.Add((byte)groups.Length);
        bytes.AddRange(groups.Select(g => (byte)g.Elements.Length));
        for (var i = 0; i < gains.Length; i++)
        {
            bytes.AddRange(new byte[48]);
            bytes.AddRange(BitConverter.GetBytes(gains[i]));
            bytes.Add((byte)i);
            bytes.AddRange(new byte[3]);
        }
        static byte[] Name(string name)
        {
            var field = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(field, 0);
            return field;
        }
        foreach (var (id, elements) in groups)
        {
            bytes.AddRange(Name(id));
            bytes.Add((byte)elements.Length);
            foreach (var (param, bone, parent) in elements)
            {
                bytes.Add(0);
                bytes.Add(param);
                bytes.AddRange(Name(bone));
                bytes.AddRange(Name(parent));
            }
        }
        return [..bytes];
    }
}
