using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// Skeleton IDs for custom hair skeletons: a free ID that no vanilla hair skeleton of any race and no
/// mod in the Penumbra library uses, and the vanilla hair skeleton a new one starts from.
/// </summary>
internal static partial class HairSkeletonIds
{
    public const int Minimum = 1000, Maximum = 9999;

    // EstType is a MetaIndex: 73 is the hair table.
    private const int HairSlotNumber = 73;

    // [0-9], not \d: \d matches any Unicode digit, which int.Parse refuses.
    [GeneratedRegex("skeleton/hair/h([0-9]{4})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HairSkeletonFile();

    /// <summary>Every hair skeleton ID the game's hair EST table names, for any race.</summary>
    public static IReadOnlySet<int> Vanilla(EstTable hairTable)
        => hairTable.Rows.Where(row => row.Entry > 0).Select(row => (int)row.Entry).ToHashSet();

    /// <summary>
    /// The lowest vanilla hair skeleton ID of a gender-race (801 for <c>c0801</c>) whose skeleton file
    /// <paramref name="exists"/>, or null when the race has none.
    /// </summary>
    public static ushort? TemplateId(EstTable hairTable, ushort genderRace, Func<string, bool> exists)
    {
        var race = $"c{genderRace:D4}";
        foreach (var id in hairTable.Rows.Where(row => row.GenderRace == genderRace && row.Entry > 0)
                     .Select(row => row.Entry).Distinct().Order())
            if (exists(ModelSkeletonPaths.PartialPath(race, EstSlot.Hair, id)))
                return id;
        return null;
    }

    /// <summary>
    /// The hair skeleton IDs the mods in a Penumbra mod folder use: the IDs in their
    /// <c>skeleton/hair/hNNNN/</c> file keys and the entries of their hair EST manipulations, read
    /// from each mod's <c>meta.json</c>, <c>default_mod.json</c> and <c>group_*.json</c>. Files that
    /// can't be read, parsed or walked (duplicate property names among them) are skipped.
    /// </summary>
    public static IReadOnlySet<int> Library(string modRoot)
    {
        var ids = new HashSet<int>();
        if (!Directory.Exists(modRoot)) return ids;
        string[] mods;
        try { mods = Directory.EnumerateDirectories(modRoot).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ids; }
        foreach (var mod in mods)
        {
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(mod, "group_*.json")
                    .Prepend(Path.Combine(mod, "default_mod.json"))
                    .Prepend(Path.Combine(mod, "meta.json"))
                    .Where(File.Exists)
                    .ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                // A JsonObject with a duplicate property name throws ArgumentException when first read.
                var found = new HashSet<int>();
                try { Collect(JsonNode.Parse(File.ReadAllText(file)), found); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
                                              ArgumentException or FormatException or OverflowException) { continue; }
                ids.UnionWith(found);
            }
        }
        return ids;
    }

    /// <summary>A uniformly random ID in [<see cref="Minimum"/>, <see cref="Maximum"/>] that is not taken.</summary>
    public static int Pick(IReadOnlySet<int> taken, Random random)
    {
        var free = Enumerable.Range(Minimum, Maximum - Minimum + 1).Where(id => !taken.Contains(id)).ToArray();
        if (free.Length == 0)
            throw new InvalidOperationException($"Every hair skeleton ID from {Minimum} to {Maximum} is taken.");
        return free[random.Next(free.Length)];
    }

    // Walks a mod file's JSON for Files objects and Est manipulations, wherever the format puts them.
    private static void Collect(JsonNode? node, HashSet<int> ids)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array) Collect(item, ids);
                break;
            case JsonObject item:
                if (item["Files"] is JsonObject files)
                    foreach (var (key, _) in files)
                        if (HairSkeletonFile().Match(key) is { Success: true } match)
                            ids.Add(int.Parse(match.Groups[1].Value));
                if (HairEstEntry(item) is int entry)
                    ids.Add(entry);
                foreach (var (_, child) in item)
                    if (child is JsonObject or JsonArray)
                        Collect(child, ids);
                break;
        }
    }

    private static int? HairEstEntry(JsonObject item)
    {
        if (item["Type"] is not JsonValue type || !type.TryGetValue<string>(out var typeName) ||
            !string.Equals(typeName, "Est", StringComparison.OrdinalIgnoreCase) ||
            item["Manipulation"] is not JsonObject manipulation || manipulation["Slot"] is not JsonValue slot)
            return null;
        var hair = slot.TryGetValue<int>(out var slotNumber)
            ? slotNumber == HairSlotNumber
            : slot.TryGetValue<string>(out var slotName) && string.Equals(slotName, "Hair", StringComparison.OrdinalIgnoreCase);
        return hair && manipulation["Entry"] is JsonValue value && value.TryGetValue<int>(out var entry) && entry > 0
            ? entry
            : null;
    }
}
