using System.Text.Json.Nodes;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The mashup clean-up reads every file a mod maps to decide which are the same. A file many options
/// map has to be held once, not once per mapping: a modpack's shared texture used to cost its size times
/// the number of options in memory, inside the game.
/// </summary>
internal static class ModCleanupMemoryScenarios
{
    public static void Run(string root)
    {
        var mod = Path.Combine(root, "CleanupMemory");
        Directory.CreateDirectory(Path.Combine(mod, "legacy"));
        var shared = new byte[6 * 1024 * 1024];
        new Random(7).NextBytes(shared);
        File.WriteAllBytes(Path.Combine(mod, "legacy", "shared.tex"), shared);

        const string gamePath = "chara/common/texture/cleanup_memory.tex";
        const int optionCount = 12;
        var options = new JsonArray();
        for (var i = 0; i < optionCount; i++)
            options.Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = $"Option {i}",
                ["Files"] = new JsonObject { [gamePath] = "legacy/shared.tex" },
            });
        var meta = ModMetadata.CreateV4ModMetadata("Cleanup memory", "Author", "", "1.0", new JsonObject());
        meta["Groups"] = new JsonArray
        {
            new JsonObject { ["Id"] = Guid.NewGuid(), ["Type"] = "Single", ["Name"] = "Group", ["Options"] = options },
        };
        File.WriteAllText(Path.Combine(mod, "meta.json"), meta.ToJsonString());

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ModCleanup.NormalizeAndDeduplicateModForRegression(mod, "cleanup-memory");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Require(result.Warnings.Count == 0 && result.PathRemap is not null,
            $"the clean-up of a mod whose options share one file completes ({string.Join("; ", result.Warnings)})");
        Require(allocated < 3L * shared.Length,
            $"it holds the shared file once, not once per option ({allocated / 1024 / 1024} MB allocated for a {shared.Length / 1024 / 1024} MB file)");

        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        var targets = written["Groups"]![0]!["Options"]!.AsArray()
            .Select(option => option!["Files"]![gamePath]!.GetValue<string>()).Distinct().ToList();
        Require(targets.Count == 1 && File.ReadAllBytes(Path.Combine(mod, targets[0])).AsSpan().SequenceEqual(shared) &&
                Directory.GetFiles(mod, "*.tex", SearchOption.AllDirectories).Length == 1,
            "and every option points at the one kept copy, which has the file's bytes");
    }
}
