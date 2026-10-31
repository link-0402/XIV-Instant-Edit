using InstantEdit.Services.Actors;

/// <summary>Partners: the player-name rule, a preset's name problems and the preset store.</summary>
internal static class PartnerFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Names(check);
        Problems(check);
        Store(check);
    }

    private static void Names(Action<bool, string> check)
    {
        var good = new[] { "Aria Rose", "Xb Yq", "Aria Rose-Lee", "O'neil Rose", "Abcdefghij Klmnopqrst" };
        var bad = new[]
        {
            "Aria", "aria Rose", "XIVBridgeTest Partner", "A Rose", "Abcdefghijklmnop Rose", "Abcdefghijk Klmnopqrst",
            "Aria Ro--se", "Aria Ro-'se", "Aria  Rose", "Aria Rose Lee",
        };
        var wrong = good.Where(name => !PartnerNames.IsPlayerName(name)).Concat(bad.Where(PartnerNames.IsPlayerName)).ToArray();
        check(wrong.Length == 0, "partner names: player names are two capitalised parts of 2-15 letters, 21 characters at most" + Failed(string.Join('|', wrong)));
    }

    private static void Problems(Action<bool, string> check)
    {
        var id = Guid.NewGuid();
        var other = new PartnerPreset(Guid.NewGuid(), "Aria Rose", null, null, null);
        check(PartnerNames.Problem("  Aria Rose ", [], id, "Lucina Zero") == null, "partner problem: a trimmed free player name has no problem");
        check(PartnerNames.Problem("lucina zero", [], id, "Lucina Zero") == PartnerNames.Yours, "partner problem: your own name, case ignored, is refused");
        check(PartnerNames.Problem("aria rose", [other], id, null) == PartnerNames.Taken, "partner problem: another preset's name, case ignored, is taken");
        check(PartnerNames.Problem("Aria Rose", [other], other.Id, null) == null, "partner problem: a preset keeps its own name");
        check(PartnerNames.Problem("Aria", [], id, null) == PartnerNames.FirstLast, "partner problem: one name asks for a first and last name");
        check(PartnerNames.Taken == "Name is taken" && PartnerNames.Yours == "Use a name other than your character's" &&
              PartnerNames.FirstLast == "Use a first and last name, like Aria Rose",
            "partner problem: the messages read as the spec says");
    }

    private static void Store(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-partner-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var warnings = new List<string>();
            var file = Path.Combine(temp, PartnerPresetStore.FileName);
            var store = new PartnerPresetStore(temp, warnings.Add);
            check(store.Presets.IsEmpty && warnings.Count == 0, "partner store: a new store is empty");

            var preset = new PartnerPreset(Guid.NewGuid(), " Aria Rose ", new PartnerLink(Guid.NewGuid(), "Partner mods"),
                new PartnerLink(Guid.NewGuid(), "Summer look"), new PartnerLink(Guid.NewGuid(), "Tall"));
            var saved = store.Save(preset, "Lucina Zero", out var error);
            var kept = preset with { Name = "Aria Rose" };
            check(saved && error == null && store.Presets.SequenceEqual([kept]) && new PartnerPresetStore(temp, warnings.Add).Presets.SequenceEqual([kept]) && warnings.Count == 0,
                "partner store: a saved preset with all three links is trimmed and read back equal by a new store");

            var renamed = kept with { Name = "Mira Vale" };
            check(store.Save(renamed, null, out _) && store.Presets.SequenceEqual([renamed]) && new PartnerPresetStore(temp, warnings.Add).Presets.SequenceEqual([renamed]),
                "partner store: saving the same Id with a new name replaces it");
            check(!store.Save(renamed with { Name = "Aria" }, null, out var refused) && refused == PartnerNames.FirstLast && store.Presets.SequenceEqual([renamed]),
                "partner store: a name that isn't a player name is refused and nothing changes");
            check(!store.Save(new PartnerPreset(Guid.NewGuid(), "mira vale", null, null, null), null, out var taken) && taken == PartnerNames.Taken,
                "partner store: another preset's name is refused");

            check(store.Delete(renamed.Id) && !store.Delete(renamed.Id) && store.Presets.IsEmpty && new PartnerPresetStore(temp, warnings.Add).Presets.IsEmpty,
                "partner store: delete removes the preset from memory and the file, and again does nothing");

            var goodId = Guid.NewGuid();
            File.WriteAllText(file, $$"""{ "Version": 1, "Presets": [ { "Id": "{{goodId}}", "Name": "Aria Rose", "Collection": null, "Design": null, "Profile": null }, { "Name": "Mira Vale" } ], "OwnedAssignments": [] }""");
            warnings.Clear();
            var partial = new PartnerPresetStore(temp, warnings.Add);
            check(partial.Presets.Length == 1 && partial.Presets[0].Id == goodId && warnings.Count == 1 && File.Exists(file + ".bak"),
                "partner store load: an entry without an Id is skipped with one warning and the file is kept as .bak");

            File.Delete(file + ".bak");
            File.WriteAllText(file, "not json");
            warnings.Clear();
            var broken = new PartnerPresetStore(temp, warnings.Add);
            check(broken.Presets.IsEmpty && warnings.Count == 1 && File.Exists(file + ".bak") && File.ReadAllText(file + ".bak") == "not json",
                "partner store load: a file that isn't JSON loads empty, warns and is kept as .bak");

            broken.SetOwnsAssignment("Aria Rose", true);
            check(broken.OwnsAssignment("ARIA ROSE") && new PartnerPresetStore(temp, warnings.Add).OwnsAssignment("aria rose"),
                "partner store: an owned assignment survives a reload, case ignored");
            broken.SetOwnsAssignment("aria rose", false);
            check(!broken.OwnsAssignment("Aria Rose") && !new PartnerPresetStore(temp, warnings.Add).OwnsAssignment("Aria Rose"),
                "partner store: an assignment no longer owned is gone after a reload");

            var memory = new PartnerPresetStore(null);
            check(memory.Save(new PartnerPreset(Guid.NewGuid(), "Aria Rose", null, null, null), null, out _) && memory.Presets.Length == 1,
                "partner store memory: without a folder presets are kept in memory");
            check(warnings.Count == 1, "partner store: nothing past the bad files warned" + Failed(warnings.Count == 1 ? null : string.Join('|', warnings)));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static string Failed(string? detail) => string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
}
