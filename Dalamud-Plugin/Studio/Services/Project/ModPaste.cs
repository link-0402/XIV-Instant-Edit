using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Project;

/// <summary>What a copy holds: files of one option, an option (or the default files) with everything it carries, or a group.</summary>
internal enum ModClipKind { Files, Option, Group }

/// <summary>
/// What was copied in a mod window, to be pasted into a mod: another one, or another place in the
/// same one. Its meta.json part is taken when it is copied; its files are read from the source
/// mod's folder when it is pasted, as a copy in Explorer is.
/// </summary>
internal sealed class ModClip
{
    public required ModClipKind Kind { get; init; }
    public required string SourceName { get; init; }
    public required string SourceFolder { get; init; }

    /// <summary>The file's, option's or group's name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Files and Option: an object holding the option's Files, FileSwaps and Manipulations (and the
    /// option's own fields); Group: the group as meta.json has it.
    /// </summary>
    public required JObject Content { get; init; }

    /// <summary>The option it was copied from (Files and Option).</summary>
    public OptionRef? From { get; init; }

    /// <summary>The source mod's models, to tell which item its files belong to.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>How many file entries it has, in all its options.</summary>
    public int FileCount => ModPaste.Carriers(this).Sum(c => c["Files"] is JObject files ? files.Count : 0);
}

/// <summary>What a paste makes: a new group, a new option of a group, or more files in an option.</summary>
internal enum PasteKind { NewGroup, NewOption, IntoOption }

internal readonly record struct PasteTarget(PasteKind Kind, string GroupId, OptionRef Option)
{
    public static PasteTarget NewGroup => new(PasteKind.NewGroup, string.Empty, default);
    public static PasteTarget NewOptionIn(string groupId) => new(PasteKind.NewOption, groupId, default);
    public static PasteTarget Into(OptionRef option) => new(PasteKind.IntoOption, option.Group, option);
}

/// <summary>A file a paste copies into the mod folder; a material's texture paths move with a retarget.</summary>
internal sealed record PasteFile(string Source, string RelPath, bool Material);

/// <summary>What a group or option made by a paste is now called in meta.json.</summary>
internal sealed record PasteResult(string? GroupId, OptionRef? Option, string Name);

/// <summary>A paste worked out against the mod it goes into: where each file goes, and what it changes.</summary>
internal sealed class PastePlan
{
    public required ModClip Clip { get; init; }
    public required PasteTarget Target { get; init; }

    /// <summary>The new group's or option's name.</summary>
    public required string Name { get; init; }

    public ModRetarget? Retarget { get; init; }

    /// <summary>The copy's meta.json part as it goes into the mod: game paths moved, each file pointed at its new place.</summary>
    public required JObject Content { get; init; }

    /// <summary>The files to copy, each once however many entries use it.</summary>
    public List<PasteFile> Files { get; } = new();

    /// <summary>File entries pasted.</summary>
    public int Entries { get; set; }

    /// <summary>Entries at a game path the mod already has or uses.</summary>
    public int Used { get; set; }

    /// <summary>Game paths whose file the source mod doesn't have; left out.</summary>
    public List<string> Missing { get; } = new();

    /// <summary>Game paths the option pasted into has already; they are pointed at the copies.</summary>
    public List<string> Replaced { get; } = new();

    /// <summary>Game paths where a file of the user's own is set up in the option pasted into: the paste can't go there.</summary>
    public List<string> Blocked { get; } = new();

    /// <summary>Game paths a retarget moved, from and to.</summary>
    public List<(string From, string To)> Moved { get; } = new();

    /// <summary>Files of the retargeted item that keep their game paths (models, effects).</summary>
    public int Kept { get; set; }

    /// <summary>Whether it carries file swaps or manipulations (copied as they are).</summary>
    public bool HasExtras { get; set; }
}

/// <summary>
/// Copying files, options and groups out of one mod and pasting them into another. A paste writes
/// new files only (never over one), where the mod window puts that option's files, and points
/// meta.json at them in one write; the source mod is only read. Groups and options get new ids, so
/// a group pasted back into its own mod is a group of its own.
/// </summary>
internal static class ModPaste
{
    private const int MaxNames = 100;

    // ─────────────────────────────────────────────────────────────────────────
    // Copying
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Files of one option (game path and path in the mod), as a copy.</summary>
    public static ModClip CopyFiles(PenumbraModMeta meta, string folder, string modName, OptionRef from, IReadOnlyList<(string GamePath, string RelPath)> files)
    {
        var list = new JObject();
        foreach (var (gamePath, rel) in files)
            if (list.Property(gamePath, StringComparison.OrdinalIgnoreCase) == null)
                list.Add(gamePath, rel);
        return new ModClip
        {
            Kind         = ModClipKind.Files,
            SourceName   = modName,
            SourceFolder = folder,
            Name         = files.Count > 0 ? Path.GetFileName(files[0].GamePath) : "files",
            Content      = new JObject { ["Files"] = list },
            From         = from,
            Models       = Models(meta),
        };
    }

    /// <summary>An option (a Combining group's container, or the default files) with everything it carries; null when it is gone.</summary>
    public static ModClip? CopyOption(PenumbraModMeta meta, string folder, string modName, OptionRef option)
    {
        if (meta.CopyCarrier(option) is not { } content)
            return null;
        return new ModClip
        {
            Kind         = ModClipKind.Option,
            SourceName   = modName,
            SourceFolder = folder,
            Name         = meta.OptionName(option),
            Content      = content,
            From         = option,
            Models       = Models(meta),
        };
    }

    /// <summary>A group with its options and everything they carry; null when it is gone.</summary>
    public static ModClip? CopyGroup(PenumbraModMeta meta, string folder, string modName, string groupId)
    {
        if (meta.FindGroupView(groupId) is not { } view || meta.CopyGroup(groupId) is not { } content)
            return null;
        return new ModClip
        {
            Kind         = ModClipKind.Group,
            SourceName   = modName,
            SourceFolder = folder,
            Name         = view.Name,
            Content      = content,
            Models       = Models(meta),
        };
    }

    private static List<string> Models(PenumbraModMeta meta)
        => meta.AllFiles().Select(f => PathNormalizer.GamePath(f.GamePath)).Where(p => p.EndsWith(".mdl", StringComparison.Ordinal))
            .Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Why a group (or an option of it) can't be copied, or null when it can.</summary>
    public static string? WhyNoCopy(PenumbraModMeta meta, string groupId)
    {
        if (meta.FindGroupView(groupId) is not { } view || meta.CopyGroup(groupId) is not { } group)
            return "That group is no longer in the mod.";
        if (view.Type == ModGroupType.Unknown)
            return "This kind of group is not known here.";
        return IsAttributeGroup(group) ? "Attribute groups aren't copied." : null;
    }

    /// <summary>
    /// A group that switches attributes rather than carrying files: an IMC group, one Instant Edit
    /// keeps for a model's parts, or one whose options only hold Atr manipulations.
    /// </summary>
    internal static bool IsAttributeGroup(JObject group)
    {
        if (string.Equals((string?)group["Type"], "Imc", StringComparison.OrdinalIgnoreCase))
            return true;
        if (((string?)group["Description"] ?? string.Empty).StartsWith(
                global::InstantEdit.Services.PenumbraMods.AttributeGroupService.AttributeGroupDescriptionPrefix, StringComparison.Ordinal))
            return true;
        var carriers = GroupCarriers(group).ToList();
        if (carriers.Any(c => c["Files"] is JObject { Count: > 0 } || c["FileSwaps"] is JObject { Count: > 0 }))
            return false;
        var manipulations = carriers.SelectMany(c => c["Manipulations"] as JArray ?? []).OfType<JObject>().ToList();
        return manipulations.Count > 0 && manipulations.All(m => string.Equals((string?)m["Type"], "Atr", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every object of a copy that carries files: the copy itself, or each option and container of a group.</summary>
    internal static IEnumerable<JObject> Carriers(ModClip clip)
        => clip.Kind == ModClipKind.Group ? GroupCarriers(clip.Content) : [clip.Content];

    private static IEnumerable<JObject> GroupCarriers(JObject group)
        => (group["Options"] as JArray ?? []).Concat(group["Containers"] as JArray ?? []).OfType<JObject>();

    /// <summary>Every game path a copy has a file at.</summary>
    internal static IEnumerable<string> GamePaths(ModClip clip)
        => Carriers(clip).SelectMany(c => c["Files"] is JObject files ? files.Properties().Select(p => p.Name) : []);

    // ─────────────────────────────────────────────────────────────────────────
    // Where a copy can go
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Why a copy can't be pasted there, or null when it can.</summary>
    public static string? WhyNot(ModClip clip, PasteTarget target, PenumbraModMeta meta, string modFolder)
    {
        switch (clip.Kind, target.Kind)
        {
            case (ModClipKind.Group, PasteKind.NewGroup):
                return null;
            case (ModClipKind.Group, _):
                return "Paste a group onto the mod's tile.";
            case (ModClipKind.Files, not PasteKind.IntoOption):
                return "Paste files into an option.";
            case (ModClipKind.Option, PasteKind.NewGroup):
                return "Paste an option into a group or an option.";
        }

        if (target.Kind == PasteKind.NewOption)
            return meta.FindGroupView(target.GroupId) is { } group ? PenumbraModMeta.WhyNoNewOption(group) : "That group is no longer in the mod.";

        if (!meta.OptionExists(target.Option))
            return "That option is no longer in the mod.";
        if (clip.From == target.Option && SameFolder(clip.SourceFolder, modFolder))
            return "It is here already.";
        return null;
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A name not in <paramref name="taken"/>: the name, else "name (copy)", "name (copy 2)" and so on.</summary>
    public static string UniqueName(IEnumerable<string> taken, string name)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        name = name.Trim().Length > 0 ? name.Trim() : "Pasted";
        if (!used.Contains(name))
            return name;
        var candidate = $"{name} (copy)";
        for (int i = 2; used.Contains(candidate); i++)
            candidate = $"{name} (copy {i})";
        return candidate;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pasting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out a paste into the mod at <paramref name="modFolder"/>: each entry's game path (moved by
    /// <paramref name="retarget"/> when given), and where each file goes, once however many entries
    /// use it — where the mod window puts that option's files, else beside it as name_copy. Never a
    /// path that is taken: <paramref name="inUse"/> (see ModProject.RelKeysInUse), one meta.json points
    /// at, or a file there already. <paramref name="usedHere"/> says whether the mod has or uses a game
    /// path; <paramref name="replaced"/> whether a file of the user's own is set up there.
    /// </summary>
    public static PastePlan Plan(ModClip clip, PasteTarget target, string name, ModRetarget? retarget, PenumbraModMeta meta, string modFolder,
        IReadOnlySet<string> inUse, Func<string, bool> usedHere, Func<OptionRef, string, bool> replaced)
    {
        if (WhyNot(clip, target, meta, modFolder) is { } why)
            throw new InvalidOperationException(why);
        name = name.Trim();

        var content = (JObject)clip.Content.DeepClone();
        var plan = new PastePlan { Clip = clip, Target = target, Name = name, Retarget = retarget, Content = content };
        var referenced = meta.ReferencedRelKeys();
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string rel)
        {
            var key = PathNormalizer.RelKey(rel);
            return planned.Contains(key) || inUse.Contains(key) || referenced.Contains(key) || File.Exists(PathNormalizer.ToDiskPath(modFolder, rel));
        }

        foreach (var (carrier, natural) in PlacedCarriers(content, target, name, meta))
        {
            if (carrier["FileSwaps"] is JObject { Count: > 0 } || carrier["Manipulations"] is JArray { Count: > 0 })
                plan.HasExtras = true;
            if (carrier["Files"] is not JObject files)
                continue;

            var placed = new JObject();
            foreach (var entry in files.Properties())
            {
                if (entry.Value.Type != JTokenType.String)
                    continue;
                var gamePath = entry.Name;
                if (SourceFile(clip.SourceFolder, (string)entry.Value!) is not { } source)
                {
                    plan.Missing.Add(gamePath);
                    continue;
                }

                var to = gamePath;
                if (retarget != null && retarget.Covers(gamePath))
                {
                    if (retarget.Moves(gamePath))
                    {
                        to = retarget.Path(gamePath);
                        plan.Moved.Add((gamePath, to));
                    }
                    else
                    {
                        plan.Kept++;
                    }
                }
                if (!PathNormalizer.IsSafeRel(PathNormalizer.GamePath(to)) || !PathNormalizer.HasStorableSegments(PathNormalizer.GamePath(to)))
                    throw new InvalidOperationException($"{to} is not a valid game path.");
                if (placed.Property(to, StringComparison.OrdinalIgnoreCase) != null)
                    throw new InvalidOperationException($"Two of its files would go to {to}.");

                if (!bySource.TryGetValue(source, out var rel))
                {
                    rel = Free(natural(to), Taken);
                    planned.Add(PathNormalizer.RelKey(rel));
                    bySource[source] = rel;
                    plan.Files.Add(new PasteFile(source, rel, retarget != null && to.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)));
                }
                placed.Add(to, rel);
                plan.Entries++;
                if (usedHere(to))
                    plan.Used++;
                if (target.Kind == PasteKind.IntoOption)
                {
                    if (meta.TryGetFile(target.Option, to, out _))
                        plan.Replaced.Add(to);
                    if (replaced(target.Option, to))
                        plan.Blocked.Add(to);
                }
            }
            files.Replace(placed);
        }
        return plan;
    }

    /// <summary>
    /// Each object of the content that carries files, with where a file of it goes in the mod folder
    /// by game path. A group gets new ids first (the group and each option) and the paste's name.
    /// </summary>
    private static IEnumerable<(JObject Carrier, Func<string, string> Natural)> PlacedCarriers(JObject content, PasteTarget target, string name, PenumbraModMeta meta)
    {
        static string InFolder(string folder, string gamePath) => $"{folder}\\{PathNormalizer.GamePath(gamePath).Replace('/', '\\')}";

        switch (target.Kind)
        {
            case PasteKind.NewGroup:
            {
                content["Name"] = name;
                var id = Guid.NewGuid().ToString();
                SetId(content, id);
                foreach (var option in (content["Options"] as JArray ?? []).OfType<JObject>())
                    SetId(option, Guid.NewGuid().ToString());

                // The options' names (a container's is the options it combines), as the mod window will show them.
                var scratch = PenumbraModMeta.Parse("{}");
                scratch.UpsertGroup((JObject)content.DeepClone());
                var view = scratch.FindGroupView(id)!;
                var objects = (view.Type == ModGroupType.Combining ? content["Containers"] : content["Options"]) as JArray ?? [];
                var group = PathNormalizer.SafeSegment(name);
                foreach (var set in view.FileSets)
                {
                    if (set.Index < objects.Count && objects[set.Index] is JObject carrier)
                    {
                        var folder = $"{group}\\{PathNormalizer.SafeSegment(set.Name)}";
                        yield return (carrier, path => InFolder(folder, path));
                    }
                }
                break;
            }
            case PasteKind.NewOption:
            {
                content.Remove("Id");
                var folder = $"{PathNormalizer.SafeSegment(meta.FindGroupView(target.GroupId)?.Name ?? "group")}\\{PathNormalizer.SafeSegment(name)}";
                yield return (content, path => InFolder(folder, path));
                break;
            }
            default:
                yield return (content, path => ModWriter.ReplacementRel(meta, path, target.Option));
                break;
        }
    }

    private static void SetId(JObject obj, string id)
    {
        if (obj.Property("Id", StringComparison.Ordinal) is { } existing)
            existing.Value = id;
        else if (obj.Property("Type", StringComparison.Ordinal) is { } type)
            type.AddAfterSelf(new JProperty("Id", id));
        else
            obj.AddFirst(new JProperty("Id", id));
    }

    /// <summary>The file a meta.json value of the source mod points at, when it is there; null when not (or outside the mod).</summary>
    private static string? SourceFile(string folder, string rel)
    {
        if (!PathNormalizer.IsAbsolute(rel) && !PathNormalizer.IsSafeRel(rel))
            return null;
        try
        {
            var full = Path.GetFullPath(PathNormalizer.ToDiskPath(folder, rel));
            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Free(string natural, Func<string, bool> taken)
    {
        for (int n = 0; n < MaxNames; n++)
        {
            var candidate = n == 0 ? natural : ModHandoffCopies.Beside(natural, n == 1 ? "_copy" : $"_copy{n}");
            if (!taken(candidate))
                return candidate;
        }
        throw new IOException($"{Path.GetFileName(natural)} has no free file name left in the mod.");
    }

    /// <summary>
    /// Copies a paste's files into the mod folder (off the render thread): each to a temporary file
    /// first, then moved into place, never over a file. When one fails, those written are deleted
    /// again and the error is thrown. Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> Write(string modFolder, PastePlan plan)
    {
        var written = new List<string>();
        try
        {
            foreach (var file in plan.Files)
            {
                var full = PathNormalizer.ToDiskPath(modFolder, file.RelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var temp = full + PenumbraModMeta.TempSuffix;
                try
                {
                    if (file.Material && plan.Retarget is { } retarget)
                        File.WriteAllBytes(temp, retarget.Material(File.ReadAllBytes(file.Source), Path.GetFileName(file.Source)));
                    else
                        File.Copy(file.Source, temp, overwrite: true);
                    File.Move(temp, full, overwrite: false);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
                written.Add(full);
            }
            return written;
        }
        catch
        {
            Delete(written);
            throw;
        }
    }

    /// <summary>
    /// Puts a paste into <paramref name="meta"/>, its files written by now: a new group at the end, a
    /// new option at the end of its group, or the entries, swaps and manipulations into the option.
    /// A name taken meanwhile gets "(copy)". Throws when the place it was meant for is gone or full.
    /// </summary>
    public static PasteResult Apply(PenumbraModMeta meta, PastePlan plan)
    {
        var content = (JObject)plan.Content.DeepClone();
        switch (plan.Target.Kind)
        {
            case PasteKind.NewGroup:
            {
                var name = UniqueName(meta.Groups.Select(g => g.Name), plan.Name);
                content["Name"] = name;
                var id = (string?)content["Id"] ?? throw new InvalidOperationException("The group has no id.");
                meta.UpsertGroup(content);
                return new PasteResult(id, null, name);
            }
            case PasteKind.NewOption:
            {
                var group = meta.FindGroupView(plan.Target.GroupId) ?? throw new InvalidOperationException("That group is no longer in the mod.");
                if (PenumbraModMeta.WhyNoNewOption(group) is { } why)
                    throw new InvalidOperationException(why);
                var name = UniqueName(group.Options.Select(o => o.Name), plan.Name);
                if (content.Property("Name", StringComparison.Ordinal) is { } existing)
                    existing.Value = name;
                else
                    content.AddFirst(new JProperty("Name", name));
                var made = meta.AddOptionObject(group.Id, content) ?? throw new InvalidOperationException($"{group.Name} has no room for another option.");
                return new PasteResult(group.Id, made, name);
            }
            default:
            {
                var option = plan.Target.Option;
                if (!meta.OptionExists(option))
                    throw new InvalidOperationException("That option is no longer in the mod.");
                foreach (var entry in (content["Files"] as JObject)?.Properties() ?? [])
                    meta.SetFile(option, entry.Name, (string)entry.Value!);
                foreach (var swap in (content["FileSwaps"] as JObject)?.Properties() ?? [])
                    if (swap.Value.Type == JTokenType.String)
                        meta.SetSwap(option, swap.Name, (string)swap.Value!);
                foreach (var manipulation in (content["Manipulations"] as JArray ?? []).OfType<JObject>())
                    meta.UpsertManipulation(option, manipulation);
                return new PasteResult(option.IsDefault ? null : option.Group, option, meta.OptionName(option));
            }
        }
    }

    /// <summary>Deletes a paste's files again, when meta.json could not take it.</summary>
    public static void Delete(IEnumerable<string> files)
    {
        foreach (var file in files)
            TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the mod folder, unused by anything.
        }
    }
}

/// <summary>
/// Moves a copy's files from one item to another, the way a mashup does: a texture or material in
/// the item's folder ("chara/accessory/a0053") goes into the other item's folder
/// ("chara/equipment/e0279"), with the item's ids in its name swapped too (c0201a0053 becomes
/// c0201e0279). Only ids the source item has are swapped, so a texture of another race keeps its
/// race. A moved material's texture paths move with it. Models and other files keep their paths.
/// </summary>
internal sealed partial class ModRetarget
{
    private static readonly string[] Markers = ["/model/", "/material/", "/texture/", "/vfx/", "/animation/"];

    private readonly Dictionary<string, string> _ids;

    private ModRetarget(string from, string to, Dictionary<string, string> ids)
    {
        From = from;
        To   = to;
        _ids = ids;
    }

    /// <summary>The item folder files move out of.</summary>
    public string From { get; }

    /// <summary>The item folder they move into.</summary>
    public string To { get; }

    [GeneratedRegex(@"(?<![a-z])[a-z]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Id();

    /// <summary>The folder of the item a game path belongs to ("chara/accessory/a0053"), or null.</summary>
    public static string? ItemFolder(string gamePath)
    {
        var path = PathNormalizer.GamePath(gamePath);
        if (!path.StartsWith("chara/", StringComparison.Ordinal))
            return null;
        int at = -1;
        foreach (var marker in Markers)
        {
            int i = path.IndexOf(marker, StringComparison.Ordinal);
            if (i > 0 && (at < 0 || i < at))
                at = i;
        }
        if (at < 0)
            return null;
        var segment = path[(path.LastIndexOf('/', at - 1) + 1)..at];
        return Id().IsMatch(segment) ? path[..at] : null;
    }

    /// <summary>The ids in a path (a0053, c0201 and the like), each once, in the order they come.</summary>
    public static List<string> Ids(string path)
        => Id().Matches(PathNormalizer.GamePath(path)).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The item most of a copy's files belong to, and a model of the source mod in it: one the copy
    /// has, else any (null when the mod has none there). Null when none of its files is an item's.
    /// </summary>
    public static (string Folder, string? Model)? SourceOf(ModClip clip)
    {
        var paths = ModPaste.GamePaths(clip).ToList();
        var folder = paths.Select(ItemFolder).OfType<string>().GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (folder == null)
            return null;
        var models = clip.Models.Where(m => ItemFolder(m) == folder).ToList();
        var model = models.FirstOrDefault(m => paths.Any(p => PathNormalizer.SameGamePath(p, m))) ?? models.FirstOrDefault();
        return (folder, model);
    }

    /// <summary>
    /// Moving files of the item at <paramref name="fromFolder"/> (its model <paramref name="fromModel"/>,
    /// when known) to the item of <paramref name="toModel"/>. Ids of the same kind pair up (c0201 with
    /// the target's race); the item's own id pairs with the target's (a0053 with e0279). Null when
    /// that model is in no item's folder.
    /// </summary>
    public static ModRetarget? Between(string fromFolder, string? fromModel, string toModel)
    {
        if (ItemFolder(toModel) is not { } to)
            return null;
        var from = PathNormalizer.GamePath(fromFolder);
        var source = Ids(fromModel != null && ItemFolder(fromModel) == from ? fromModel : from);
        var target = Ids(toModel);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in source)
        {
            if (target.FirstOrDefault(t => t[0] == id[0] && !used.Contains(t)) is { } same)
            {
                ids[id] = same;
                used.Add(same);
            }
        }
        // A race only ever pairs with a race.
        var restFrom = source.Where(id => id[0] != 'c' && !ids.ContainsKey(id)).ToList();
        var restTo = target.Where(id => id[0] != 'c' && !used.Contains(id)).ToList();
        for (int i = 0; i < Math.Min(restFrom.Count, restTo.Count); i++)
            ids[restFrom[i]] = restTo[i];
        foreach (var same in ids.Where(p => p.Key == p.Value).Select(p => p.Key).ToList())
            ids.Remove(same);
        return new ModRetarget(from, to, ids);
    }

    /// <summary>Whether a game path is in the item files move out of.</summary>
    public bool Covers(string gamePath) => PathNormalizer.GamePath(gamePath).StartsWith(From + "/", StringComparison.Ordinal);

    /// <summary>Whether a file moves: a texture or material of the item.</summary>
    public bool Moves(string gamePath)
        => Covers(gamePath) && System.IO.Path.GetExtension(gamePath).ToLowerInvariant() is ".tex" or ".mtrl";

    /// <summary>Where a file goes: in the other item for one that moves, else where it was.</summary>
    public string Path(string gamePath)
    {
        var path = PathNormalizer.GamePath(gamePath);
        if (!Moves(path))
            return path;
        var rest = Id().Replace(path[From.Length..], m => _ids.TryGetValue(m.Value, out var to) ? to : m.Value);
        return To + rest;
    }

    /// <summary>A material with its texture paths in the item moved too; one that can't be read is copied as it is.</summary>
    public byte[] Material(byte[] bytes, string name)
    {
        MtrlInfo info;
        try
        {
            info = MtrlReader.Read(bytes);
        }
        catch (Exception)
        {
            return bytes;
        }

        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in info.TextureOffsets)
        {
            var key = global::InstantEdit.Services.PathRules.NormalizeGamePath(texture.Path);
            if (Moves(key) && !rewrites.ContainsKey(key))
                rewrites[key] = Path(key);
        }
        if (rewrites.Count == 0)
            return bytes;
        try
        {
            return global::InstantEdit.Services.PenumbraMods.MashupPlanning.RewriteMaterialTexturePaths(bytes, rewrites);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{name}: its texture paths could not be moved ({ex.Message}).", ex);
        }
    }
}
