using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using InstantEdit.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;

namespace InstantEdit.Studio;

/// <summary>
/// Instant Edit's hand-offs for the files of a mod open in a mod window: a model to Blender, a
/// texture to the image editor, an animation to Blender. The main window carries them out and
/// reports what happened in its status feed.
/// </summary>
internal interface IModHandoff
{
    /// <summary>Why the row's model can't go to Blender, or null when it can.</summary>
    string? ModelBlock(ResourceView row);

    void EditModel(ResourceView row, ActorView mod);

    /// <summary>Why the row's texture can't be edited, or null when it can.</summary>
    string? TextureBlock(ResourceView row);

    void EditTexture(ResourceView row, ActorView mod);

    /// <summary>Why the row's material can't open in the Material window, or null when it can.</summary>
    string? MaterialBlock(ResourceView row);

    /// <summary>Opens the row's material; <paramref name="copies"/> lets the Material window save a copy into an option of the mod.</summary>
    void EditMaterial(ResourceView row, ActorView mod, IModFileCopies copies);

    /// <summary>Why the row's animation can't go to Blender, or null when it can.</summary>
    string? AnimationBlock(ResourceView row);

    /// <summary>Opens the send dialog, drawn by the window <paramref name="owner"/> (see <see cref="DrawDialogs"/>).</summary>
    void SendAnimation(ResourceView row, string owner);

    /// <summary>Draws the dialogs the hand-offs of window <paramref name="owner"/> opened.</summary>
    void DrawDialogs(string owner);

    /// <summary>The latest message of the model, texture and animation hand-offs, or null.</summary>
    (DateTimeOffset Time, string Text, FeedbackSeverity Severity)? LatestMessage { get; }
}

/// <summary>
/// The mod window a material was opened from, for the Material window's "Save as copy into an
/// option": the places in the mod a copy can go, and the mod window's own write that puts it there
/// as new files of the mod. Render thread only.
/// </summary>
internal interface IModFileCopies
{
    string ModName { get; }

    /// <summary>Why no copy can be written now (the mod window was closed, the mod is being updated), or null.</summary>
    string? Block { get; }

    /// <summary>
    /// The default files and every option, for a copy of files at <paramref name="gamePaths"/> (the
    /// first is the copied file, which is at <paramref name="sourceRelPath"/> in the mod).
    /// </summary>
    IReadOnlyList<ModHandoffOption> Options(IReadOnlyList<string> gamePaths, string sourceRelPath);

    /// <summary>
    /// Writes <paramref name="files"/> into the mod as new files and points the option
    /// <paramref name="optionKey"/> at them. The files it used before stay in the mod.
    /// </summary>
    ModHandoffCopy SaveCopy(string optionKey, IReadOnlyList<(string GamePath, byte[] Bytes)> files);
}

/// <summary>
/// A place in a mod a copy can go: its key (<see cref="OptionRef.Serialize"/>) and name, whether it
/// has a file at the copy's game path already and whether that is the copied file itself, and why it
/// can't take the copy (null when it can).
/// </summary>
internal sealed record ModHandoffOption(string Key, string Label, bool HasFile, bool UsesSource, string? Block);

/// <summary>A file a copy wrote: its game path, its path in the mod as meta.json names it, and the file on disk.</summary>
internal sealed record ModHandoffCopiedFile(string GamePath, string RelPath, string DiskPath);

/// <summary>What a copy into an option wrote, in the order of its files, or why nothing was written.</summary>
internal sealed record ModHandoffCopy(string? Error, string OptionLabel, IReadOnlyList<ModHandoffCopiedFile> Files)
{
    public static ModHandoffCopy Failed(string error) => new(error, string.Empty, []);
}

/// <summary>The mod a file belongs to, as the hand-offs name it.</summary>
internal sealed record ModHandoffMod(string Name, string FolderName, string FolderPath, Guid? StableId);

/// <summary>
/// A file of an open mod: its game path, the file on disk and its path in the mod folder, and the
/// option carrying it. <paramref name="GroupId"/> is empty for the default files.
/// <paramref name="OptionId"/> is an option's id, or "c#N" for container N of a combining group,
/// whose options are <paramref name="GroupOptionIds"/> in order.
/// </summary>
internal sealed record ModHandoffFile(
    string GamePath,
    string DiskPath,
    string RelPath,
    string GroupId,
    string OptionId,
    IReadOnlyList<string> GroupOptionIds,
    string OptionLabel);

/// <summary>
/// Turns the files of a mod window into the rows Instant Edit's hand-offs take. Each row names the
/// one option it came from, so a model sent to Blender is exported back into that very option even
/// when several options carry the same file.
/// </summary>
internal static class ModHandoffBridge
{
    /// <summary>The membership of the default files, as <c>ModMetadata</c> reads meta.json.</summary>
    public const string DefaultMembership = "default";

    /// <summary>
    /// The option memberships of a file: "default", or "option:{group}:{option}" for each option
    /// carrying it (every selected option of a combining group's container). Empty when the mod's
    /// groups or options have no ids (meta.json before version 4).
    /// </summary>
    public static IReadOnlyList<string> Memberships(string groupId, string optionId, IReadOnlyList<string> groupOptionIds)
    {
        if (groupId.Length == 0)
            return [DefaultMembership];
        if (!Guid.TryParse(groupId, out var group))
            return [];

        if (optionId.StartsWith("c#", StringComparison.Ordinal))
        {
            if (!int.TryParse(optionId.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var container) || container <= 0)
                return [];
            var memberships = new List<string>();
            for (var bit = 0; bit < groupOptionIds.Count && bit < 31; bit++)
                if ((container & (1 << bit)) != 0 && Guid.TryParse(groupOptionIds[bit], out var selected))
                    memberships.Add(Membership(group, selected));
            return memberships;
        }

        return Guid.TryParse(optionId, out var option) ? [Membership(group, option)] : [];
    }

    private static string Membership(Guid group, Guid option)
        => $"option:{group.ToString("D", CultureInfo.InvariantCulture)}:{option.ToString("D", CultureInfo.InvariantCulture)}";

    /// <summary>A file of the mod carried by <paramref name="option"/>, named for the hand-offs from the mod's meta.json.</summary>
    public static ModHandoffFile File(PenumbraModMeta meta, string gamePath, string diskPath, string relPath, OptionRef option)
    {
        if (option.IsDefault)
            return new ModHandoffFile(gamePath, diskPath, relPath, string.Empty, string.Empty, [], "Default");

        var group = meta.FindGroupView(option.Group);
        var optionIds = group?.Options.Select(o => o.Ref.Option).ToList() ?? [];
        var label = group == null ? meta.OptionName(option) : $"{group.Name}: {meta.OptionName(option)}";
        return new ModHandoffFile(gamePath, diskPath, relPath, option.Group, option.Option, optionIds, label);
    }

    /// <summary>The hand-off row of one file of the mod.</summary>
    public static ResourceView Row(ModHandoffMod mod, ModHandoffFile file)
    {
        var type = ResourceViews.ResourceType(file.GamePath);
        return new ResourceView(
            type,
            string.Empty,
            Path.GetFileName(file.GamePath),
            file.GamePath,
            file.DiskPath,
            $"Loaded from: {mod.Name}",
            mod.Name,
            mod.FolderName,
            mod.FolderPath,
            file.RelPath.Replace('\\', '/'),
            mod.StableId,
            ResourceSourceState.LoadedMod,
            ResourceSection.Other.ToString(),
            type,
            int.MaxValue,
            file.OptionLabel,
            Memberships(file.GroupId, file.OptionId, file.GroupOptionIds),
            []);
    }

    /// <summary>
    /// The mod as the hand-offs see it: every file of it as a row, so a model's material preview
    /// finds the mod's own materials and textures. Imports use the character at
    /// <paramref name="importObjectIndex"/> for skeletons and racial scaling.
    /// </summary>
    public static ActorView Mod(ModHandoffMod mod, IEnumerable<ModHandoffFile> files, int importObjectIndex)
        => new(null, "Mod", mod.Name, files.Select(file => Row(mod, file)).ToList(), importObjectIndex);
}

/// <summary>
/// A copy of files saved into one option of a mod: written into the mod folder as new files, where
/// the mod window puts that option's files, and the option's game paths pointed at them. No file of
/// the mod is written over, and every other entry of meta.json stays as it is.
/// </summary>
internal static class ModHandoffCopies
{
    private const int MaxNames = 100;

    /// <summary>"Default files", or "Group: Option".</summary>
    public static string Label(PenumbraModMeta meta, OptionRef option)
        => option.IsDefault ? "Default files" : $"{meta.FindGroupView(option.Group)?.Name ?? "(missing group)"}: {meta.OptionName(option)}";

    /// <summary>
    /// The places a copy of files at <paramref name="gamePaths"/> can go: the default files, then each
    /// option (each container of a combining group) as meta.json lists them. One where a file of the
    /// user's own is set up for one of the game paths (<paramref name="replaced"/>) can't take it: the
    /// mod window's next update would point it back at that file.
    /// </summary>
    public static IReadOnlyList<ModHandoffOption> Options(PenumbraModMeta meta, IReadOnlyList<string> gamePaths, string sourceRelPath,
        Func<OptionRef, string, bool> replaced)
    {
        var options = new List<ModHandoffOption>();
        foreach (var option in meta.FileCarriers())
        {
            var rel = string.Empty;
            var has = gamePaths.Count > 0 && meta.TryGetFile(option, gamePaths[0], out rel);
            var source = has && sourceRelPath.Length > 0 && PathNormalizer.SameRel(rel, sourceRelPath);
            var block = gamePaths.Any(path => replaced(option, path)) ? "A file of your own replaces it here. Undo that in the mod window first." : null;
            options.Add(new ModHandoffOption(option.Serialize(), Label(meta, option), has, source, block));
        }
        return options;
    }

    /// <summary>
    /// Where each file of a copy goes in the mod folder: where the mod window puts a file of that option
    /// (see <see cref="ModWriter.ReplacementRel"/>), else beside it as name_copy, name_copy2 and so on.
    /// Never a path that is <paramref name="taken"/>: a file there already, or one the mod uses.
    /// </summary>
    public static IReadOnlyList<(string GamePath, string RelPath)> Plan(PenumbraModMeta meta, OptionRef option, IReadOnlyList<string> gamePaths,
        Func<string, bool> taken)
    {
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<(string GamePath, string RelPath)>();
        foreach (var gamePath in gamePaths)
        {
            if (!PathNormalizer.IsSafeRel(PathNormalizer.GamePath(gamePath)))
                throw new InvalidOperationException($"{gamePath} is not a valid game path.");
            var natural = ModWriter.ReplacementRel(meta, gamePath, option);
            string? rel = null;
            for (var n = 0; n < MaxNames && rel == null; n++)
            {
                var candidate = n == 0 ? natural : Beside(natural, n == 1 ? "_copy" : $"_copy{n}");
                var key = PathNormalizer.RelKey(candidate);
                if (planned.Contains(key) || taken(candidate))
                    continue;
                planned.Add(key);
                rel = candidate;
            }
            plan.Add((gamePath, rel ?? throw new IOException($"{Path.GetFileName(gamePath)} has no free file name left in the mod.")));
        }
        return plan;
    }

    private static string Beside(string rel, string suffix)
    {
        var folder = Path.GetDirectoryName(rel) ?? string.Empty;
        var name = $"{Path.GetFileNameWithoutExtension(rel)}{suffix}{Path.GetExtension(rel)}";
        return folder.Length > 0 ? $"{folder}\\{name}" : name;
    }

    /// <summary>
    /// Writes a copy's files where <see cref="Plan"/> puts them and points the option's game paths at
    /// them in <paramref name="meta"/>; saving meta.json is the caller's. <paramref name="inUse"/> are
    /// the rel keys anything else of the mod still needs (see ModProject.RelKeysInUse). A file is never
    /// written over: one that appears meanwhile fails the copy. When anything fails, the files written
    /// are deleted again and the error is thrown.
    /// </summary>
    public static IReadOnlyList<ModHandoffCopiedFile> Write(string modFolder, PenumbraModMeta meta, OptionRef option,
        IReadOnlyList<(string GamePath, byte[] Bytes)> files, IReadOnlySet<string> inUse)
    {
        if (files.Count == 0)
            throw new ArgumentException("There is nothing to copy.", nameof(files));
        if (!meta.OptionExists(option))
            throw new InvalidOperationException("That option is no longer in the mod.");

        var referenced = meta.ReferencedRelKeys();
        var plan = Plan(meta, option, files.Select(file => file.GamePath).ToList(), rel =>
        {
            var key = PathNormalizer.RelKey(rel);
            return inUse.Contains(key) || referenced.Contains(key) || File.Exists(PathNormalizer.ToDiskPath(modFolder, rel));
        });
        var written = new List<ModHandoffCopiedFile>();
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var full = PathNormalizer.ToDiskPath(modFolder, plan[i].RelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var temp = full + PenumbraModMeta.TempSuffix;
                File.WriteAllBytes(temp, files[i].Bytes);
                try
                {
                    File.Move(temp, full, overwrite: false);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
                written.Add(new ModHandoffCopiedFile(plan[i].GamePath, plan[i].RelPath, full));
            }
            foreach (var file in written)
                meta.SetFile(option, file.GamePath, file.RelPath);
            return written;
        }
        catch
        {
            Delete(written);
            throw;
        }
    }

    /// <summary>Deletes the files of a copy again, when meta.json could not take it.</summary>
    public static void Delete(IEnumerable<ModHandoffCopiedFile> files)
    {
        foreach (var file in files)
            TryDelete(file.DiskPath);
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
