using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Services.Materials;

/// <summary>
/// Save as variant: the edited material goes into a mod as a new file, in an option of a Single
/// group made for that material. The group's first option, "Original", carries no file, so the mod's
/// own material shows with it; each variant is one more option pointing the material's game path at
/// its file. A later variant of the same material joins the same group, found by the marker in its
/// description, so renaming it in Penumbra keeps it in use.
/// </summary>
internal static class MaterialVariants
{
    public const string OriginalOption = "Original";
    private const int MaxNameLength = 120;
    private const string Arrow = " -> ";

    /// <summary>What a variant save wrote: the group and option it made (with their names now, for selecting it in Penumbra) and the files, in order.</summary>
    internal sealed record Written(string GroupId, string GroupName, OptionRef Option, string OptionName, bool NewGroup,
        IReadOnlyList<ModHandoffCopiedFile> Files);

    /// <summary>The group this mod keeps the variants of <paramref name="gamePath"/> in, or null when it has none yet.</summary>
    public static ModGroupView? FindGroup(PenumbraModMeta meta, string gamePath)
    {
        if (meta.Root["Groups"] is not JArray groups)
            return null;
        for (var index = 0; index < groups.Count; index++)
        {
            if (groups[index] is not JObject group || !string.Equals((string?)group["Type"], "Single", StringComparison.OrdinalIgnoreCase)
                || (string?)group["Description"] is not { } description
                || !description.StartsWith(ModOptionGroups.VariantGroupDescriptionPrefix, StringComparison.Ordinal))
                continue;
            var arrow = description.LastIndexOf(Arrow, StringComparison.Ordinal);
            if (arrow >= 0 && PathNormalizer.SameGamePath(description[(arrow + Arrow.Length)..], gamePath))
                return meta.Groups.FirstOrDefault(view => view.Index == index);
        }
        return null;
    }

    /// <summary>The name a new group gets: "&lt;material&gt; variants", numbered when the mod has a group of that name.</summary>
    public static string NewGroupName(PenumbraModMeta meta, string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(PathNormalizer.GamePath(gamePath)) + " variants";
        var used = meta.Groups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var number = 2; used.Contains(candidate); number++)
            candidate = $"{name} {number}";
        return candidate;
    }

    /// <summary>The name the dialog suggests: "Variant", then "Variant 2" and so on past the group's options.</summary>
    public static string SuggestName(PenumbraModMeta meta, string gamePath)
    {
        var used = FindGroup(meta, gamePath)?.Options.Select(option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var candidate = "Variant";
        for (var number = 2; used.Contains(candidate); number++)
            candidate = $"Variant {number}";
        return candidate;
    }

    /// <summary>Why <paramref name="name"/> can't name a new variant of <paramref name="gamePath"/>, or null when it can.</summary>
    public static string? NameProblem(PenumbraModMeta meta, string gamePath, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Give the variant a name.";
        if (name.Length > MaxNameLength)
            return "That name is too long.";
        if (name.Any(char.IsControl))
            return "That name has characters Penumbra can't show.";
        if (string.Equals(name, OriginalOption, StringComparison.OrdinalIgnoreCase))
            return $"\"{OriginalOption}\" is the mod's own material. Pick another name.";
        if (FindGroup(meta, gamePath)?.Options.Any(option => string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
            return "The group has a variant with this name already.";
        return null;
    }

    /// <summary>
    /// Adds the variant to <paramref name="meta"/> (making the group first when there is none) and
    /// writes <paramref name="files"/> (the material first, then the textures it reads that are new)
    /// as new files of that option, where the mod window puts an option's files. Saving meta.json is
    /// the caller's. A file is never written over; when anything fails, the files written are deleted
    /// again and the error is thrown. <paramref name="inUse"/>: see <see cref="ModHandoffCopies.Write"/>.
    /// </summary>
    public static Written Write(string modFolder, PenumbraModMeta meta, string name,
        IReadOnlyList<(string GamePath, byte[] Bytes)> files, IReadOnlySet<string> inUse)
    {
        if (files.Count == 0)
            throw new ArgumentException("There is nothing to save.", nameof(files));
        var gamePath = files[0].GamePath;
        name = name.Trim();
        if (NameProblem(meta, gamePath, name) is { } problem)
            throw new InvalidOperationException(problem);

        var group = FindGroup(meta, gamePath);
        var created = group == null;
        if (group == null)
        {
            var id = Guid.NewGuid().ToString();
            meta.UpsertGroup(NewGroup(meta, id, NewGroupName(meta, gamePath), gamePath));
            group = meta.FindGroupView(id) ?? throw new InvalidOperationException("The variant group could not be made.");
        }
        var option = meta.AddOption(group.Id, name) ?? throw new InvalidOperationException($"{group.Name} can't take another option.");
        var written = ModHandoffCopies.Write(modFolder, meta, option, files, inUse);
        return new Written(group.Id, group.Name, option, name, created, written);
    }

    /// <summary>
    /// <see cref="Write"/> into a mod folder no mod window is writing: meta.json read from disk and
    /// written back (the old one kept beside it once, as the mod window does).
    /// </summary>
    public static Written WriteInto(string modFolder, string name, IReadOnlyList<(string GamePath, byte[] Bytes)> files)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = PenumbraModMeta.Load(modFolder);
            var written = Write(modFolder, meta, name, files, new HashSet<string>());
            try
            {
                meta.Save(modFolder, backupFirst: true);
            }
            catch
            {
                ModHandoffCopies.Delete(written.Files);
                throw;
            }
            return written;
        }
    }

    /// <summary>
    /// A Single group laid out the way Penumbra writes one, above every other group of the mod, with
    /// "Original" first and picked by default, so collections that never chose keep the mod's own material.
    /// </summary>
    private static JObject NewGroup(PenumbraModMeta meta, string id, string name, string gamePath)
    {
        var highest = meta.Root["Groups"] is JArray groups
            ? groups.OfType<JObject>().Select(group => group["Priority"]?.Type == JTokenType.Integer ? (int)group["Priority"]! : 0).DefaultIfEmpty(0).Max()
            : 0;
        if (highest == int.MaxValue)
            throw new InvalidOperationException("The mod's groups use every priority Penumbra has. Lower one in Penumbra first.");
        return new JObject
        {
            ["Type"]            = "Single",
            ["Id"]              = id,
            ["Name"]            = name,
            ["Description"]     = ModOptionGroups.VariantGroupDescriptionPrefix + name + Arrow + PathNormalizer.GamePath(gamePath),
            ["Priority"]        = highest + 1,
            ["DefaultSettings"] = 0,
            ["Options"]         = new JArray(new JObject { ["Id"] = Guid.NewGuid().ToString(), ["Name"] = OriginalOption }),
        };
    }
}
