using System.Text.Json.Nodes;
using InstantEdit.Services.PenumbraMods;

/// <summary>
/// Which file a Penumbra mod serves for each game path under the user's option settings: the default files,
/// then the picked options of each group, a higher-priority group over a lower one. Written on a temporary mod
/// folder in the plugin's meta.json layout, plus a check against a real mod when the folder is there.
/// </summary>
internal static class ModEffectiveFilesFixture
{
    private const string RealMod = @"G:\Penumbra\(You're Not Gonna Reach My) Telephone";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None =
        new Dictionary<string, IReadOnlyList<string>>();

    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ie-effective-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Layers(check, dir);
            Priority(check, dir);
            Multi(check, dir);
            Combining(check, dir);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        Real(check);
    }

    private static Dictionary<string, IReadOnlyList<string>> Picks(params (string Group, string[] Options)[] picks)
        => picks.ToDictionary(pick => pick.Group, pick => (IReadOnlyList<string>)pick.Options, StringComparer.Ordinal);

    private static JsonObject Files(params (string GamePath, string Relative)[] files)
    {
        var result = new JsonObject();
        foreach (var (gamePath, relative) in files)
            result[gamePath] = relative;
        return result;
    }

    private static JsonObject Option(string name, params (string GamePath, string Relative)[] files)
        => new() { ["Name"] = name, ["Files"] = Files(files) };

    private static JsonObject Group(string name, string type, int priority, params JsonObject[] options)
        => new() { ["Name"] = name, ["Type"] = type, ["Priority"] = priority, ["Options"] = new JsonArray(options) };

    /// <summary>A mod folder with a meta.json holding these files and groups, and the named files written on disk.</summary>
    private static string Mod(string root, string name, JsonObject defaultFiles, IEnumerable<JsonObject> groups, params string[] onDisk)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        foreach (var relative in onDisk)
        {
            var file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, relative);
        }
        var meta = new JsonObject
        {
            ["FileVersion"] = 4,
            ["Name"] = name,
            ["DefaultData"] = new JsonObject { ["Files"] = defaultFiles },
            ["Groups"] = new JsonArray(groups.Select(group => (JsonNode?)group).ToArray()),
        };
        File.WriteAllText(Path.Combine(folder, "meta.json"), meta.ToJsonString());
        return folder;
    }

    private static string? At(IReadOnlyDictionary<string, string> files, string gamePath, string folder)
        => files.TryGetValue(gamePath, out var path) && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            ? path[folder.Length..].TrimStart('\\', '/').Replace('\\', '/')
            : null;

    private static void Layers(Action<bool, string> check, string dir)
    {
        var folder = Mod(dir, "layers",
            Files(("chara/x/model.mdl", "def/model.mdl"), ("chara/x/tex_d.tex", "def/tex_d.tex"),
                ("Chara\\X\\Mixed.TEX", "def\\mixed.tex"), ("chara/x/gone.tex", "def/gone.tex"),
                ("chara/x/escape.tex", "../outside.tex")),
            [Group("Color", "Single", 1,
                Option("Red", ("chara/x/tex_d.tex", "color/red/tex_d.tex"), ("chara/x/gone.tex", "color/red/gone.tex")),
                Option("Blue", ("chara/x/tex_d.tex", "color/blue/tex_d.tex")))],
            "def/model.mdl", "def/tex_d.tex", "def/mixed.tex", "color/red/tex_d.tex", "color/blue/tex_d.tex");
        File.WriteAllText(Path.Combine(dir, "outside.tex"), "outside");

        var none = ModOptionGroups.EffectiveFiles(folder, None);
        check(none.Count == 3 && At(none, "chara/x/model.mdl", folder) == "def/model.mdl" &&
              At(none, "chara/x/tex_d.tex", folder) == "def/tex_d.tex",
            "effective files with no options are the default mapping, with absolute paths under the mod");
        check(At(none, "chara/x/mixed.tex", folder) == "def/mixed.tex",
            "a game path is lower case with forward slashes however the mod wrote it");
        check(!none.ContainsKey("chara/x/gone.tex") && !none.ContainsKey("chara/x/escape.tex"),
            "a mapping to a missing file, or to a file outside the mod, is skipped");

        var blue = ModOptionGroups.EffectiveFiles(folder, Picks(("Color", ["Blue"])));
        check(At(blue, "chara/x/tex_d.tex", folder) == "color/blue/tex_d.tex" &&
              At(blue, "chara/x/model.mdl", folder) == "def/model.mdl",
            "a Single group's picked option overrides the default mapping of the same game path");
        var red = ModOptionGroups.EffectiveFiles(folder, Picks(("color", ["red"])));
        check(At(red, "chara/x/tex_d.tex", folder) == "color/red/tex_d.tex" && !red.ContainsKey("chara/x/gone.tex"),
            "group and option names match without regard to case, and a picked option's missing file is skipped");
        var unknown = ModOptionGroups.EffectiveFiles(folder, Picks(("Color", ["Green"]), ("Elsewhere", ["Red"])));
        check(unknown.Count == none.Count && At(unknown, "chara/x/tex_d.tex", folder) == "def/tex_d.tex",
            "an unknown group or option changes nothing");
    }

    private static void Priority(Action<bool, string> check, string dir)
    {
        static JsonObject[] Pair(bool highFirst)
        {
            var low = Group("Low", "Single", 1, Option("Pick", ("chara/x/shared.tex", "low/shared.tex")));
            var high = Group("High", "Single", 3, Option("Pick", ("chara/x/shared.tex", "high/shared.tex")));
            return highFirst ? [high, low] : [low, high];
        }
        var picks = Picks(("Low", ["Pick"]), ("High", ["Pick"]));
        foreach (var highFirst in new[] { false, true })
        {
            var folder = Mod(dir, "priority-" + highFirst, Files(), Pair(highFirst), "low/shared.tex", "high/shared.tex");
            var files = ModOptionGroups.EffectiveFiles(folder, picks);
            check(At(files, "chara/x/shared.tex", folder) == "high/shared.tex",
                $"of two groups mapping one path, the higher priority wins ({(highFirst ? "listed first" : "listed second")})");
        }

        var tie = Mod(dir, "tie", Files(), [
            Group("First", "Single", 2, Option("Pick", ("chara/x/shared.tex", "first/shared.tex"))),
            Group("Second", "Single", 2, Option("Pick", ("chara/x/shared.tex", "second/shared.tex")))],
            "first/shared.tex", "second/shared.tex");
        check(At(ModOptionGroups.EffectiveFiles(tie, Picks(("First", ["Pick"]), ("Second", ["Pick"]))),
                  "chara/x/shared.tex", tie) == "first/shared.tex",
            "of two groups of equal priority, the one listed first wins");

        var fallback = Mod(dir, "fallback", Files(("chara/x/shared.tex", "def/shared.tex")), [
            Group("High", "Single", 3, Option("Pick", ("chara/x/shared.tex", "high/missing.tex"))),
            Group("Low", "Single", 1, Option("Pick", ("chara/x/shared.tex", "low/shared.tex")))],
            "def/shared.tex", "low/shared.tex");
        check(At(ModOptionGroups.EffectiveFiles(fallback, Picks(("High", ["Pick"]), ("Low", ["Pick"]))),
                  "chara/x/shared.tex", fallback) == "low/shared.tex",
            "a higher-priority mapping whose file is missing leaves the lower one in place");
    }

    private static void Multi(Action<bool, string> check, string dir)
    {
        var folder = Mod(dir, "multi", Files(("chara/x/extra.tex", "def/extra.tex")), [
            Group("Extras", "Multi", 0,
                Option("One", ("chara/x/extra.tex", "one/extra.tex"), ("chara/x/one.tex", "one/one.tex")),
                Option("Two", ("chara/x/extra.tex", "two/extra.tex")),
                Option("Three", ("chara/x/extra.tex", "three/extra.tex"), ("chara/x/three.tex", "three/three.tex")))],
            "def/extra.tex", "one/extra.tex", "one/one.tex", "two/extra.tex", "three/extra.tex", "three/three.tex");

        var files = ModOptionGroups.EffectiveFiles(folder, Picks(("Extras", ["One", "Two"])));
        check(At(files, "chara/x/extra.tex", folder) == "two/extra.tex" && At(files, "chara/x/one.tex", folder) == "one/one.tex",
            "of two picked options in a Multi group, the later one wins and the earlier one's other files stay");
        check(!files.ContainsKey("chara/x/three.tex"), "a Multi option that isn't picked contributes no paths");
        var reversed = ModOptionGroups.EffectiveFiles(folder, Picks(("Extras", ["Two", "One"])));
        check(At(reversed, "chara/x/extra.tex", folder) == "two/extra.tex",
            "the options of a Multi group count in the group's order, not the order they were picked in");
        var empty = ModOptionGroups.EffectiveFiles(folder, Picks(("Extras", [])));
        check(At(empty, "chara/x/extra.tex", folder) == "def/extra.tex" && empty.Count == 1,
            "a Multi group with nothing picked leaves the default mapping");
    }

    private static void Combining(Action<bool, string> check, string dir)
    {
        var combining = new JsonObject
        {
            ["Name"] = "Parts", ["Type"] = "Combining", ["Priority"] = 0,
            ["Options"] = new JsonArray(new JsonObject { ["Name"] = "A" }, new JsonObject { ["Name"] = "B" }),
            ["Containers"] = new JsonArray(
                new JsonObject { ["Files"] = Files() },
                new JsonObject { ["Files"] = Files(("chara/x/part.tex", "a/part.tex")) },
                new JsonObject { ["Files"] = Files(("chara/x/part.tex", "b/part.tex")) },
                new JsonObject { ["Files"] = Files(("chara/x/part.tex", "ab/part.tex")) }),
        };
        var folder = Mod(dir, "combining", Files(), [combining], "a/part.tex", "b/part.tex", "ab/part.tex");
        check(At(ModOptionGroups.EffectiveFiles(folder, Picks(("Parts", ["A", "B"]))), "chara/x/part.tex", folder) == "ab/part.tex" &&
              At(ModOptionGroups.EffectiveFiles(folder, Picks(("Parts", ["B"]))), "chara/x/part.tex", folder) == "b/part.tex" &&
              At(ModOptionGroups.EffectiveFiles(folder, Picks(("Parts", ["A"]))), "chara/x/part.tex", folder) == "a/part.tex" &&
              ModOptionGroups.EffectiveFiles(folder, Picks(("Parts", []))).Count == 0,
            "a Combining group serves the container its picked options select");
    }

    private static void Real(Action<bool, string> check)
    {
        if (!Directory.Exists(RealMod))
        {
            Console.WriteLine("[SKIP] mod effective files: the telephone mod isn't where the checks look, so a real mod isn't read");
            return;
        }
        var files = ModOptionGroups.EffectiveFiles(RealMod, Picks(
            ("(You're Not Gonna Reach My) Telephone", ["Main Install"]), ("Color Options", ["Moogle Pink"])));
        var diffuse = files.GetValueOrDefault("chara/monster/m8044/obj/body/b0001/texture/v01_m8044b0001_d.tex");
        var model = files.GetValueOrDefault("chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl");
        check(diffuse is not null && diffuse.Contains("Moogle Pink", StringComparison.OrdinalIgnoreCase) && File.Exists(diffuse),
            "the telephone's diffuse texture resolves into the Moogle Pink option");
        check(model is not null && model.Contains("Main Install", StringComparison.OrdinalIgnoreCase) && File.Exists(model),
            "the telephone's model resolves into Main Install");
    }
}
