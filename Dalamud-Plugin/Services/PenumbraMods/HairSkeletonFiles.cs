using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Skeletons;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Where a hair skeleton's inputs come from: game files, the skeleton writer, the <c>j_kao</c> of a
/// template, and the hair skeleton IDs the mod library uses.
/// </summary>
internal sealed record HairSkeletonSources(
    Func<string, byte[]?> GameFile,
    Func<string, bool> GameFileExists,
    Func<byte[], ImmutableArray<SkeletonBone>, byte[]> Write,
    Func<byte[], SkeletonBone> TemplateKao,
    Func<IReadOnlySet<int>> TakenIds,
    Random Random);

/// <summary>One race's skeleton file; its game path is also its mod-relative path.</summary>
internal sealed record HairSkeletonFile(int GenderRace, string GamePath, byte[] Bytes);

/// <summary>The skeleton files of a hair model (mod-relative, <c>/</c> separators) for every race it is used for.</summary>
internal sealed record HairSkeletonPlan(string Model, int SkeletonId, IReadOnlyList<HairSkeletonFile> Files, IReadOnlyList<string> Notes);

/// <summary>
/// A mod's custom hair skeleton for one model file: one <c>.sklb</c> per race the model is used as
/// hair for, mapped in the options that use it for that race, with the hair EST entries naming it.
/// Every skeleton ID belongs to one (mod, model), so its keys and files are this plugin's to change;
/// keys of other skeleton IDs are never touched.
/// </summary>
internal static partial class HairSkeletonFiles
{
    // Player gender-race codes: 101 (c0101) to 1801 (c1801).
    private static readonly int[] PlayerRaces = Enumerable.Range(1, 18).Select(code => code * 100 + 1).ToArray();

    [GeneratedRegex(@"^chara/human/c([0-9]{4})/skeleton/hair/h([0-9]{4})/skl_c\1h\2\.sklb$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkeletonKey();

    private static string RaceLabel(int genderRace) => $"c{genderRace:D4}";

    /// <summary>
    /// Build the skeleton files for every race the model is used as hair for, under the ID the store
    /// holds for the model or a new free one. Throws <see cref="InvalidDataException"/> when the model
    /// is used as no hair, or not for the rig's race.
    /// </summary>
    public static HairSkeletonPlan Prepare(
        HairSkeletonRequest rig,
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonSources sources,
        HairSkeletonStore store)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var modelName = Path.GetFileName(model);
        var meta = LoadV4ModMetadata(modFolder);
        var races = EstEntryService.HairUses(meta, model).Select(use => use.GenderRace).Distinct().Order().ToList();
        if (races.Count == 0)
            throw new InvalidDataException($"No Penumbra option uses {modelName} as a hair model, so it can't get a hair skeleton.");
        if (EstEntryService.EstRaceCode(rig.Race) is not { } rigRace || !races.Contains(rigRace))
            throw new InvalidDataException(
                $"The hair rig is made on {rig.Race}, but {modelName} is used for {string.Join(", ", races.Select(RaceLabel))}.");

        var table = EstTable.Parse(sources.GameFile(EstTable.GamePath(EstSlot.Hair))
            ?? throw new InvalidDataException("The game's hair EST table could not be read."));
        // The library scan sees this mod's own skeleton too, so a recorded ID wins over the taken ones.
        var id = Record(store.Load(), EstEntryService.EstModKey(modFolder, modDirectory), model)?.SkeletonId
                 ?? HairSkeletonIds.Pick(HairSkeletonIds.Vanilla(table).Union(sources.TakenIds()).ToHashSet(), sources.Random);

        var files = new List<HairSkeletonFile>();
        var notes = new List<string>();
        foreach (var race in races)
        {
            var label = RaceLabel(race);
            if (HairSkeletonIds.TemplateId(table, (ushort)race, sources.GameFileExists) is not { } templateId)
            {
                notes.Add($"No vanilla hair skeleton for {label}, so it keeps its own.");
                continue;
            }
            var templatePath = ModelSkeletonPaths.PartialPath(label, EstSlot.Hair, templateId);
            var template = sources.GameFile(templatePath)
                ?? throw new InvalidDataException($"The game's hair skeleton {templatePath} could not be read.");
            files.Add(new HairSkeletonFile(race, ModelSkeletonPaths.PartialPath(label, EstSlot.Hair, id),
                sources.Write(template, HairSkeletonLayout.Bones(rig, sources.TemplateKao(template)))));
        }
        // An empty plan would only take back the model's EST entries.
        if (files.Count == 0)
            throw new InvalidDataException("No race the model is used for has a vanilla hair skeleton.");
        return new HairSkeletonPlan(model, id, files, notes);
    }

    /// <summary>
    /// Record the ID, write the plan's files into the mod, map each one in the options that use the
    /// model for its race (and nowhere else), delete the ID's files no option maps any more, and set
    /// the hair EST entries. Returns the plan's notes, then the EST warnings.
    /// </summary>
    public static List<string> Apply(
        HairSkeletonPlan plan,
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonStore store,
        EstEntryStore estStore)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        if (!string.Equals(plan.Model, model, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The hair skeleton plan was made for another model.", nameof(plan));
        // Check every destination before the first write.
        var targets = plan.Files.Select(file => (file.Bytes, Path: Destination(modFolder, file.GamePath))).ToList();
        // Record the ID before the first write, so a step that fails later never orphans its keys and files.
        var modKey = EstEntryService.EstModKey(modFolder, modDirectory);
        var records = store.Load().Where(record => !SameRecord(record, modKey, model)).ToList();
        records.Add(new ManagedHairSkeleton(modKey, model, plan.SkeletonId));
        store.Save(records);
        foreach (var (bytes, path) in targets)
            WriteAtomic(path, bytes);

        var meta = LoadV4ModMetadata(modFolder);
        var built = plan.Files.ToDictionary(file => file.GenderRace, file => file.GamePath);
        var metaChanged = Remap(meta, model, plan.SkeletonId, built);
        // Races and options that dropped the model keep no EST entry naming the skeleton.
        var estRecords = estStore.Load().ToList();
        var estCount = estRecords.Count;
        metaChanged |= EstEntryService.ReleaseEstEntries(meta, modKey, model, plan.SkeletonId, built.Keys, estRecords);
        // meta.json goes first, so a failed write never leaves an option mapping a deleted file.
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (estRecords.Count != estCount)
            estStore.Save(estRecords);
        DeleteUnmapped(meta, modFolder, plan.SkeletonId);

        var requests = plan.Files
            .Select(file => new EstEntryRequest { Slot = "Hair", Entry = plan.SkeletonId, Race = RaceLabel(file.GenderRace) })
            .ToList();
        var warnings = EstEntryService.UpdateEstEntries(modFolder, modDirectory, model, requests, estStore);
        return [.. plan.Notes, .. warnings];
    }

    /// <summary>
    /// Take back the model's hair skeleton: unmap and delete its files, take back its EST entries, and
    /// drop its record. Without a record nothing changes. Returns the EST warnings.
    /// </summary>
    public static List<string> Remove(
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonStore store,
        EstEntryStore estStore)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var modKey = EstEntryService.EstModKey(modFolder, modDirectory);
        var records = store.Load().ToList();
        if (Record(records, modKey, model) is not { } record)
            return [];

        var meta = LoadV4ModMetadata(modFolder);
        var metaChanged = Remap(meta, model, record.SkeletonId, new Dictionary<int, string>());
        var estRecords = estStore.Load().ToList();
        var estCount = estRecords.Count;
        metaChanged |= EstEntryService.ReleaseEstEntries(meta, modKey, model, record.SkeletonId, [], estRecords);
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (estRecords.Count != estCount)
            estStore.Save(estRecords);
        DeleteUnmapped(meta, modFolder, record.SkeletonId);

        var warnings = EstEntryService.UpdateEstEntries(modFolder, modDirectory, model, [], estStore);
        records.Remove(record);
        store.Save(records);
        return warnings;
    }

    private static bool SameRecord(ManagedHairSkeleton record, string modKey, string model)
        => string.Equals(record.Mod, modKey, StringComparison.Ordinal) &&
           string.Equals(record.Model, model, StringComparison.OrdinalIgnoreCase);

    private static ManagedHairSkeleton? Record(IEnumerable<ManagedHairSkeleton> records, string modKey, string model)
        => records.LastOrDefault(record => SameRecord(record, modKey, model));

    /// <summary>
    /// Give every container exactly the keys of skeleton <paramref name="id"/> for the races it uses the
    /// model for that <paramref name="built"/> has a file for (gender-race to game path). True when a key changed.
    /// </summary>
    private static bool Remap(JsonObject meta, string model, int id, IReadOnlyDictionary<int, string> built)
    {
        var uses = EstEntryService.HairUses(meta, model);
        var changed = false;
        foreach (var container in EstEntryService.EstContainers(meta))
        {
            if (container.Data["Files"] is not JsonObject files)
                continue;
            var races = uses.Where(use => use.Container == container.Key && built.ContainsKey(use.GenderRace))
                .Select(use => use.GenderRace).ToHashSet();
            foreach (var (key, value) in files.ToList())
            {
                if (SkeletonKey().Match(PathRules.NormalizeGamePath(key)) is not { Success: true } match ||
                    int.Parse(match.Groups[2].Value) != id)
                    continue;
                var race = int.Parse(match.Groups[1].Value);
                // Keep a key only in the exact form Apply writes; anything else is rewritten below.
                if (races.Contains(race) && string.Equals(key, built[race], StringComparison.Ordinal) &&
                    string.Equals(JsonString(value), ModRelative(built[race]), StringComparison.Ordinal))
                    continue;
                files.Remove(key);
                changed = true;
            }
            foreach (var race in races)
            {
                if (files.ContainsKey(built[race]))
                    continue;
                files[built[race]] = ModRelative(built[race]);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Penumbra writes mod-relative paths with backslashes.</summary>
    private static string ModRelative(string gamePath) => gamePath.Replace('/', '\\');

    /// <summary>Delete the skeleton's files, at any player race's path, that no container maps.</summary>
    private static void DeleteUnmapped(JsonObject meta, string modFolder, int id)
    {
        var mapped = EstEntryService.EstContainers(meta)
            .SelectMany(container => container.Data["Files"] as JsonObject ?? new JsonObject())
            .Select(pair => PathRules.NormalizeGamePath(JsonString(pair.Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(modFolder);
        foreach (var race in PlayerRaces)
        {
            var relative = ModelSkeletonPaths.PartialPath(RaceLabel(race), EstSlot.Hair, id);
            if (mapped.Contains(relative))
                continue;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var folder = Path.GetDirectoryName(path)!;
            if (!File.Exists(path) || !IsPathWithin(path, root) || HasReparsePointInPath(root, folder) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            File.Delete(path);
            RemoveEmptyFolders(root, folder);
        }
    }

    /// <summary>Remove <paramref name="folder"/> and its parents while they are empty, up to the mod folder.</summary>
    private static void RemoveEmptyFolders(string root, string folder)
    {
        var current = folder;
        while (IsPathWithin(current, root) && !string.Equals(Path.TrimEndingDirectorySeparator(current),
                   Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                    return;
                Directory.Delete(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>The full path of a mod-relative file; refuses one outside the mod folder or behind a reparse point.</summary>
    private static string Destination(string modFolder, string relativePath)
    {
        var root = Path.GetFullPath(modFolder);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, root) || HasReparsePointInPath(root, Path.GetDirectoryName(path)!) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("The hair skeleton's destination in the mod is unsafe.");
        return path;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
