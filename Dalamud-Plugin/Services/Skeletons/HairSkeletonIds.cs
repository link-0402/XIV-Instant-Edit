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

    /// <summary>The hair skeleton IDs the mods in a Penumbra mod folder use; see <see cref="Users"/>.</summary>
    public static IReadOnlySet<int> Library(string modRoot) => Users(modRoot, null).Keys.ToHashSet();

    /// <summary>
    /// The hair skeleton IDs the mods in a Penumbra mod folder use, each with the names of the mod folders
    /// using it: the IDs in their <c>skeleton/hair/hNNNN/</c> file keys and the entries of their hair EST
    /// manipulations, read from each mod's <c>meta.json</c>, <c>default_mod.json</c> and <c>group_*.json</c>.
    /// A file is parsed only when its text names <c>skeleton/hair/h</c> or <c>"Est"</c> (any case).
    /// The folder named <paramref name="exceptFolder"/> (any case) is left out. Files that can't be read,
    /// parsed or walked (duplicate property names among them) are skipped. Throws
    /// <see cref="InvalidDataException"/> when the mod folder is missing or can't be listed.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Users(string modRoot, string? exceptFolder)
    {
        const string unreadable = "The Penumbra mod folder can't be read.";
        if (!Directory.Exists(modRoot))
            throw new InvalidDataException(unreadable);
        string[] mods;
        try { mods = Directory.EnumerateDirectories(modRoot).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidDataException(unreadable, e); }
        var users = new Dictionary<int, List<string>>();
        foreach (var mod in mods)
        {
            var name = Path.GetFileName(mod);
            if (string.Equals(name, exceptFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            var ids = new HashSet<int>();
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
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                // Most of a library names neither a hair skeleton key nor an Est entry, so it is never parsed.
                if (!text.Contains("skeleton/hair/h", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("\"Est\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                // A JsonObject with a duplicate property name throws ArgumentException when first read.
                var found = new HashSet<int>();
                try { Collect(JsonNode.Parse(text), found); }
                catch (Exception e) when (e is JsonException or ArgumentException or FormatException or OverflowException) { continue; }
                ids.UnionWith(found);
            }
            foreach (var id in ids)
            {
                if (!users.TryGetValue(id, out var folders))
                    users[id] = folders = [];
                folders.Add(name);
            }
        }
        return users.ToDictionary(pair => pair.Key, IReadOnlyList<string> (pair) => pair.Value);
    }

    /// <summary>
    /// The hair skeleton IDs the library's other mods use: <see cref="Users"/> without the mod's directory
    /// name, nor its folder's name in case the two differ, so the mod never sees its own IDs as taken.
    /// </summary>
    public static IReadOnlySet<int> TakenByOthers(string modRoot, string modFolder, string modDirectory)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(modFolder));
        return Users(modRoot, modDirectory)
            .Where(pair => pair.Value.Any(name => !string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key)
            .ToHashSet();
    }

    /// <summary>The hair skeleton IDs one mod file's JSON names, by the same rules as <see cref="Users"/>.</summary>
    public static IReadOnlySet<int> InModFile(JsonNode? modFile)
    {
        var ids = new HashSet<int>();
        Collect(modFile, ids);
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
