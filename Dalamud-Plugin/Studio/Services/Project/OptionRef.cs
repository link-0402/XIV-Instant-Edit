using System;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// Which part of a mod a file belongs to: the mod's default files, or one option of one of its
/// groups. Groups and options are named by the ids Penumbra keeps for them (GUIDs since meta.json
/// version 4); content without ids is addressed by position instead (<c>#3</c>), and a Combining
/// group's file sets by their container index (<c>c#5</c>).
///
/// The default value is the default files, so a freshly made reference never points into a group by
/// accident.
/// </summary>
internal readonly struct OptionRef : IEquatable<OptionRef>
{
    private readonly string? _group;
    private readonly string? _option;

    public OptionRef(string group, string option)
    {
        _group  = group;
        _option = option;
    }

    /// <summary>The mod's default files (<c>DefaultData</c> in meta.json).</summary>
    public static OptionRef Default => default;

    public string Group  => _group  ?? string.Empty;
    public string Option => _option ?? string.Empty;

    public bool IsDefault => Group.Length == 0;

    /// <summary>The persisted form: empty for the default files, else "group/option".</summary>
    public string Serialize() => IsDefault ? string.Empty : $"{Group}/{Option}";

    public static OptionRef Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Default;
        int slash = text.IndexOf('/');
        return slash < 0 ? new OptionRef(text, string.Empty) : new OptionRef(text[..slash], text[(slash + 1)..]);
    }

    public bool Equals(OptionRef other)
        => string.Equals(Group, other.Group, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Option, other.Option, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is OptionRef other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Group.ToLowerInvariant(), Option.ToLowerInvariant());

    public static bool operator ==(OptionRef a, OptionRef b) => a.Equals(b);
    public static bool operator !=(OptionRef a, OptionRef b) => !a.Equals(b);

    public override string ToString() => IsDefault ? "(default)" : Serialize();
}
