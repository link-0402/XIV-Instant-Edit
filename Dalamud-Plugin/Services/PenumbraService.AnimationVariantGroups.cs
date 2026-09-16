using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    internal const string AnimationVariantGroupDescriptionPrefix = "Managed by XIV Instant Edit animation variants: ";

    /// <summary>
    /// Fold a set of optioned animation outputs into one single-select Penumbra group.
    /// Model variant export already builds exactly this shape one file at a time, so
    /// this accumulates through <see cref="BuildVariantGroup"/> rather than adding a
    /// second group writer that could drift from it.
    /// </summary>
    internal static JsonObject BuildAnimationVariantGroup(string groupName, IReadOnlyList<AnimationFileChange> optioned)
    {
        if (!IsSafeVariantGroupName(groupName))
            throw new InvalidDataException($"'{groupName}' is not a valid Penumbra group name.");
        if (optioned.Count == 0) throw new InvalidDataException("An animation variant group needs at least one option.");
        JsonObject? group = null;
        // First-seen order keeps the options in the order the user mapped them.
        foreach (var change in optioned)
        {
            if (change.Option.Length == 0) throw new InvalidDataException("An animation variant option has no name.");
            if (!IsSafeVariantName(change.Option))
                throw new InvalidDataException($"'{change.Option}' is not a valid Penumbra option name.");
            // The model helpers of the same name are .mdl-only; animation paths use
            // the dependency graph's rules, as ValidateTarget already does.
            if (!AnimationDependencies.SafeGamePath(change.GamePath))
                throw new InvalidDataException($"'{change.GamePath}' is not a valid game path.");
            var relative = change.RelativePath.Replace('\\', '/');
            if (!AnimationDependencies.SafeGamePath(relative))
                throw new InvalidDataException($"'{relative}' is not a valid path inside the mod.");
            group = BuildVariantGroup(group, AnimationVariantGroupDescriptionPrefix + groupName,
                change.GamePath, relative, change.Option, groupName, 0);
        }
        // BuildVariantGroup points DefaultSettings at the option it just wrote, which
        // is right for a single export and wrong here: a generated mod must start on
        // the leading "None" entry so it changes nothing until the user picks.
        group!["DefaultSettings"] = 0;
        group["Description"] = AnimationVariantGroupDescriptionPrefix + groupName;
        return group;
    }
}
