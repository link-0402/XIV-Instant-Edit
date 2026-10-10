using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// A group of options in the mod Build mode makes, laid out the way Penumbra shows it: the player
/// picks one of its options (or, for a multi-select group, any number). Its id and its options' ids
/// stay the same from build to build, so Penumbra keeps what the player picked.
/// </summary>
[Serializable]
public class PackGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Group";
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether any number of its options can be picked at once (Penumbra's Multi); otherwise exactly one is (Single).</summary>
    public bool Multi { get; set; }

    public List<PackOption> Options { get; set; } = new();

    /// <summary>Names of variant files whose option was taken out: they stay out of the mod rather than getting a new one.</summary>
    public List<string> LeftOut { get; set; } = new();

    public PackOption? Find(Guid option) => Options.FirstOrDefault(o => o.Id == option);

    public bool IsLeftOut(string key) => LeftOut.Contains(key, StringComparer.OrdinalIgnoreCase);

    public PackGroup Clone() => new()
    {
        Id          = Id,
        Name        = Name,
        Description = Description,
        Multi       = Multi,
        Options     = Options.Select(o => o.Clone()).ToList(),
        LeftOut     = LeftOut.ToList(),
    };
}

/// <summary>One option of a <see cref="PackGroup"/>: what goes into it is set on the items and files themselves.</summary>
[Serializable]
public class PackOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Option";
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// For an option made from variants: the file name (without extension) it was made for. A variant
    /// file of that name goes into it, from every variant slot of the group, however the option is
    /// renamed. Empty for an option made by hand.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public PackOption Clone() => new() { Id = Id, Name = Name, Description = Description, Key = Key };
}

/// <summary>
/// Where something of the modpack goes in the built mod, as it is saved: null to go wherever what it
/// belongs to goes (a texture with its material, a material or a model with its item), "" for the
/// mod's default files, else "group id/option id".
/// </summary>
public static class PackPlace
{
    public const string Default = "";

    public static string Of(Guid group, Guid option) => $"{group:D}/{option:D}";

    public static bool TryParse(string? place, out Guid group, out Guid option)
    {
        group = option = Guid.Empty;
        if (string.IsNullOrEmpty(place))
            return false;
        int slash = place.IndexOf('/');
        return slash > 0 && Guid.TryParse(place.AsSpan(0, slash), out group) && Guid.TryParse(place.AsSpan(slash + 1), out option);
    }
}
