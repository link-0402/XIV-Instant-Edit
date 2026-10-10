using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Check;

internal enum ContainerKind
{
    Default,
    SingleOption,
    MultiOption,
    CombiningContainer,
}

/// <summary>Something Penumbra applies as a unit: the default files, one option, or one container of a combining group.</summary>
internal sealed class CheckContainer
{
    public required OptionRef Option { get; init; }
    public required ContainerKind Kind { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The object holding its Files, FileSwaps and Manipulations; null when the default files have none.</summary>
    public JObject? Root { get; init; }

    public int GroupIndex { get; init; } = -1;
    public int OptionIndex { get; init; } = -1;

    /// <summary>How many options (single-choice) or containers (combining) its group has.</summary>
    public int SiblingCount { get; init; }

    public int GroupPriority { get; init; }
    public int OptionPriority { get; init; }
    public bool HasCondition { get; init; }

    public bool IsDefault => Kind == ContainerKind.Default;

    /// <summary>Exactly one option of a single-choice group, and one container of a combining group, is on at a time.</summary>
    public bool IsExclusiveWith(CheckContainer other)
        => GroupIndex >= 0 && GroupIndex == other.GroupIndex && OptionIndex != other.OptionIndex
        && Kind is ContainerKind.SingleOption or ContainerKind.CombiningContainer;

    public override string ToString() => DisplayName;
}

internal enum MetaIssueKind
{
    NonStringMapping,
    EscapingPath,
    InvalidGamePath,
    BadGroup,
    MalformedManipulation,
}

/// <summary>Something in meta.json Penumbra skips. <see cref="Location"/> is the JSON path of the entry.</summary>
internal sealed record MetaIssue(MetaIssueKind Kind, string Location, string Message, string? GamePath = null);

/// <summary>A redirection Penumbra skips (its game path is not one); its file still counts as used.</summary>
internal sealed record SkippedMapping(CheckContainer Container, string Key, string RelPath);

/// <summary>What the check makes of a redirection nothing loads.</summary>
internal enum MappingClass
{
    /// <summary>Requested by one of this mod's model or material chains.</summary>
    Used,
    VanillaReplacement,

    /// <summary>Outside what the check can judge (animations, effects, skin, shared roots). Never offered for removal.</summary>
    NotAnalyzed,
    JunkKey,

    /// <summary>Belongs to an item whose models this mod ships, but none of them asks for it.</summary>
    DeadOwn,

    /// <summary>Never asked for, and belongs to an item this mod does not otherwise change.</summary>
    Foreign,
}

/// <summary>One redirection of one container. <see cref="Key"/> is its game path as Penumbra compares it; <see cref="RelPath"/> uses forward slashes.</summary>
internal sealed class CheckMapping
{
    public required CheckContainer Container { get; init; }
    public required string Key { get; init; }
    public required string RelPath { get; init; }
    public MappingClass Class { get; set; } = MappingClass.NotAnalyzed;

    /// <summary>An earlier redirection of the same path in the same container wins; Penumbra ignores this one.</summary>
    public bool Shadowed { get; set; }

    public Resources.GameRoot Root => Resources.ResourceNaming.RootOf(Key);
}

internal sealed record ImcGroupOption(string Name, ushort AttributeMask);

/// <summary>A Penumbra IMC group: one IMC entry whose attribute mask the chosen options toggle.</summary>
internal sealed record ImcGroupInfo(string Name, ImcMeta Identifier, bool AllVariants, bool OnlyAttributes, bool CanBeDisabled,
    IReadOnlyList<ImcGroupOption> Options, long DefaultSettings, int Priority, bool HasCondition)
{
    public bool DisabledByDefault => CanBeDisabled && (DefaultSettings & (1L << Options.Count)) != 0;

    /// <summary>The attribute mask with the default settings, as Penumbra works it out: the entry's mask XOR every option on by default.</summary>
    public ushort DefaultMask()
    {
        var mask = Identifier.Entry.AttributeMask;
        for (var i = 0; i < Options.Count; ++i)
            if ((DefaultSettings & (1L << i)) != 0)
                mask ^= Options[i].AttributeMask;
        return (ushort)(mask & 0x3FF);
    }
}

internal sealed record ManipulationRef(CheckContainer Container, JObject Json, ParsedManipulation Parsed);

/// <summary>
/// A mod's options as the check reads them: every container Penumbra applies files from, every
/// redirection in them, the IMC groups and manipulations, and what Penumbra would skip.
/// </summary>
internal sealed class CheckModel
{
    public IReadOnlyList<CheckContainer> Containers { get; }
    public CheckContainer Default { get; }
    public IReadOnlyList<CheckMapping> Mappings { get; }
    public IReadOnlyList<SkippedMapping> SkippedMappings { get; }
    public IReadOnlyList<MetaIssue> Issues { get; }
    public IReadOnlyList<ImcGroupInfo> ImcGroups { get; }
    public IReadOnlyList<ManipulationRef> Manipulations { get; }

    /// <summary>File swaps per container: game path to game path, both as comparison keys.</summary>
    public IReadOnlyDictionary<CheckContainer, IReadOnlyDictionary<string, string>> Swaps { get; }

    /// <summary>Images the mod, its groups and options show; not redirections, but files to keep.</summary>
    public IReadOnlySet<string> AuxiliaryPaths { get; }

    private CheckModel(List<CheckContainer> containers, List<CheckMapping> mappings, List<SkippedMapping> skipped, List<MetaIssue> issues,
        List<ImcGroupInfo> imcGroups, List<ManipulationRef> manipulations, Dictionary<CheckContainer, IReadOnlyDictionary<string, string>> swaps,
        HashSet<string> auxiliary)
    {
        Containers      = containers;
        Default         = containers[0];
        Mappings        = mappings;
        SkippedMappings = skipped;
        Issues          = issues;
        ImcGroups       = imcGroups;
        Manipulations   = manipulations;
        Swaps           = swaps;
        AuxiliaryPaths  = auxiliary;
    }

    public CheckContainer? Find(OptionRef option) => Containers.FirstOrDefault(container => container.Option == option);

    public static CheckModel Build(PenumbraModMeta meta, string folder)
    {
        var containers = new List<CheckContainer>();
        var issues = new List<MetaIssue>();
        var imcGroups = new List<ImcGroupInfo>();
        var auxiliary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = meta.Root;
        AddImage(root, auxiliary);

        containers.Add(new CheckContainer
        {
            Option      = OptionRef.Default,
            Kind        = ContainerKind.Default,
            DisplayName = meta.OptionLabel(OptionRef.Default),
            Root        = root["DefaultData"] as JObject,
        });

        if (root["Groups"] is JArray groups)
        {
            for (var gi = 0; gi < groups.Count; ++gi)
            {
                if (groups[gi] is not JObject group)
                {
                    issues.Add(new MetaIssue(MetaIssueKind.BadGroup, groups[gi].Path, $"Option group {gi + 1} is not an object."));
                    continue;
                }
                AddImage(group, auxiliary);
                var groupId = (string?)group["Id"] is { Length: > 0 } id ? id : $"#{gi}";
                var groupName = (string?)group["Name"] is { Length: > 0 } name ? name : $"Group {gi + 1}";
                var type = ((string?)group["Type"] ?? "Single").ToLowerInvariant();
                var priority = JsonNumbers.GetInt(group["Priority"], 0);
                var groupCondition = HasCondition(group);
                var options = group["Options"] as JArray;
                switch (type)
                {
                    case "single":
                    case "multi":
                        if (options is null) break;
                        for (var oi = 0; oi < options.Count; ++oi)
                        {
                            if (options[oi] is not JObject option) continue;
                            AddImage(option, auxiliary);
                            var optionRef = new OptionRef(groupId, (string?)option["Id"] is { Length: > 0 } optionId ? optionId : $"#{oi}");
                            containers.Add(new CheckContainer
                            {
                                Option         = optionRef,
                                Kind           = type == "single" ? ContainerKind.SingleOption : ContainerKind.MultiOption,
                                DisplayName    = meta.OptionLabel(optionRef),
                                Root           = option,
                                GroupIndex     = gi,
                                OptionIndex    = oi,
                                SiblingCount   = options.Count,
                                GroupPriority  = priority,
                                OptionPriority = JsonNumbers.GetInt(option["Priority"], 0),
                                HasCondition   = groupCondition || HasCondition(option),
                            });
                        }
                        break;
                    case "combining":
                        if (options is not null)
                            foreach (var option in options.OfType<JObject>())
                                AddImage(option, auxiliary);
                        if (group["Containers"] is not JArray combined) break;
                        for (var ci = 0; ci < combined.Count; ++ci)
                        {
                            if (combined[ci] is not JObject container) continue;
                            var optionRef = new OptionRef(groupId, $"c#{ci}");
                            containers.Add(new CheckContainer
                            {
                                Option        = optionRef,
                                Kind          = ContainerKind.CombiningContainer,
                                DisplayName   = meta.OptionLabel(optionRef),
                                Root          = container,
                                GroupIndex    = gi,
                                OptionIndex   = ci,
                                SiblingCount  = combined.Count,
                                GroupPriority = priority,
                                HasCondition  = groupCondition,
                            });
                        }
                        break;
                    case "imc":
                        if (ParseImcGroup(group, groupName, priority, groupCondition) is { } imc)
                            imcGroups.Add(imc);
                        else
                            issues.Add(new MetaIssue(MetaIssueKind.BadGroup, group.Path, $"IMC group {groupName} has no valid identifier or default entry."));
                        break;
                }
            }
        }

        var mappings = new List<CheckMapping>();
        var skipped = new List<SkippedMapping>();
        var manipulations = new List<ManipulationRef>();
        var swaps = new Dictionary<CheckContainer, IReadOnlyDictionary<string, string>>();
        foreach (var container in containers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (container.Root?["Files"] is JObject files)
            {
                foreach (var property in files.Properties())
                {
                    if (property.Value.Type != JTokenType.String)
                    {
                        issues.Add(new MetaIssue(MetaIssueKind.NonStringMapping, property.Path,
                            $"The redirection of {property.Name} in {container.DisplayName} does not point at a file.", property.Name));
                        continue;
                    }
                    var stored = (string)property.Value!;
                    if (!PathNormalizer.IsSafeRel(stored))
                    {
                        issues.Add(new MetaIssue(MetaIssueKind.EscapingPath, property.Path,
                            $"{property.Name} in {container.DisplayName} points at {stored}, outside the mod folder.", property.Name));
                        continue;
                    }
                    var relPath = stored.Replace('\\', '/');
                    var trimmed = property.Name.Replace('\\', '/').TrimStart('/');
                    if (trimmed.Length == 0 || trimmed.Split('/').Any(segment => segment is "" or "." or ".."))
                    {
                        issues.Add(new MetaIssue(MetaIssueKind.InvalidGamePath, property.Path,
                            $"'{property.Name}' in {container.DisplayName} is not a valid game path.", property.Name));
                        skipped.Add(new SkippedMapping(container, property.Name, relPath));
                        continue;
                    }
                    var key = PathNormalizer.GamePath(property.Name);
                    mappings.Add(new CheckMapping { Container = container, Key = key, RelPath = relPath, Shadowed = !seen.Add(key) });
                }
            }

            var containerSwaps = new Dictionary<string, string>(StringComparer.Ordinal);
            if (container.Root?["FileSwaps"] is JObject fileSwaps)
                foreach (var swap in fileSwaps.Properties())
                    if (swap.Value.Type == JTokenType.String)
                        containerSwaps.TryAdd(PathNormalizer.GamePath(swap.Name), PathNormalizer.GamePath((string)swap.Value!));
            swaps[container] = containerSwaps;

            if (container.Root?["Manipulations"] is JArray array)
            {
                foreach (var token in array)
                {
                    if (token is JObject json)
                        manipulations.Add(new ManipulationRef(container, json, MetaManipulations.Parse(json)));
                    else
                        issues.Add(new MetaIssue(MetaIssueKind.MalformedManipulation, token.Path, $"A manipulation in {container.DisplayName} is not an object."));
                }
            }
        }

        return new CheckModel(containers, mappings, skipped, issues, imcGroups, manipulations, swaps, auxiliary);
    }

    private static ImcGroupInfo? ParseImcGroup(JObject group, string name, int priority, bool condition)
    {
        if (group["Identifier"] is not JObject identifier || MetaManipulations.ParseImc(identifier, group["DefaultEntry"]) is not { } imc)
            return null;
        var options = (group["Options"] as JArray)?.OfType<JObject>()
            .Where(option => option.Value<bool?>("IsDisableSubMod") != true)
            .Select(option => new ImcGroupOption(option.Value<string>("Name") ?? string.Empty, (ushort)(JsonNumbers.GetInt(option["AttributeMask"], 0) & 0x3FF)))
            .ToArray() ?? Array.Empty<ImcGroupOption>();
        long settings = JsonNumbers.TryGetInt(group["DefaultSettings"], out var value) ? value : 0;
        return new ImcGroupInfo(name, imc, group.Value<bool?>("AllVariants") ?? false, group.Value<bool?>("OnlyAttributes") ?? false,
            group.Value<bool?>("CanBeDisabled") ?? false, options, settings, priority, condition);
    }

    private static bool HasCondition(JObject obj)
        => obj["Condition"] is { } condition && condition.Type is not JTokenType.Null && (condition is not JContainer container || container.HasValues);

    private static void AddImage(JObject obj, HashSet<string> auxiliary)
    {
        var image = (obj["Image"] as JValue)?.Value as string;
        if (string.IsNullOrWhiteSpace(image) || image.Contains("://", StringComparison.Ordinal) || !PathNormalizer.IsSafeRel(image))
            return;
        auxiliary.Add(image.Replace('\\', '/'));
    }
}
