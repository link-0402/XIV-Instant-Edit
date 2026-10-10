using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using InstantEdit.Models;
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

    /// <summary>Why the row's animation can't go to Blender, or null when it can.</summary>
    string? AnimationBlock(ResourceView row);

    /// <summary>Opens the send dialog, drawn by the window <paramref name="owner"/> (see <see cref="DrawDialogs"/>).</summary>
    void SendAnimation(ResourceView row, string owner);

    /// <summary>Draws the dialogs the hand-offs of window <paramref name="owner"/> opened.</summary>
    void DrawDialogs(string owner);

    /// <summary>The latest message of the model, texture and animation hand-offs, or null.</summary>
    (DateTimeOffset Time, string Text, FeedbackSeverity Severity)? LatestMessage { get; }
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
    /// <summary>The membership of the default files, as <c>PenumbraService</c> reads meta.json.</summary>
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
