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

    /// <summary>
    /// The persisted form: empty for the default files, else "group/option". A '/' (or '%') in the
    /// group id is written as "%2F" ("%25"), so the first '/' always ends the group. Ids written
    /// before that (GUIDs and "#n") have neither, so they read back the same.
    /// </summary>
    public string Serialize() => IsDefault ? string.Empty : $"{EscapeGroup(Group)}/{Option}";

    public static OptionRef Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Default;
        int slash = text.IndexOf('/');
        return slash < 0 ? new OptionRef(UnescapeGroup(text), string.Empty) : new OptionRef(UnescapeGroup(text[..slash]), text[(slash + 1)..]);
    }

    private static string EscapeGroup(string group)
        => group.IndexOfAny(['/', '%']) < 0 ? group : group.Replace("%", "%25").Replace("/", "%2F");

    private static string UnescapeGroup(string group)
    {
        if (!group.Contains('%'))
            return group;
        var result = new System.Text.StringBuilder(group.Length);
        for (int i = 0; i < group.Length; i++)
        {
            if (group[i] == '%' && i + 2 < group.Length)
            {
                var code = group.AsSpan(i + 1, 2);
                if (code.Equals("2F", StringComparison.OrdinalIgnoreCase)) { result.Append('/'); i += 2; continue; }
                if (code.Equals("25", StringComparison.Ordinal))           { result.Append('%'); i += 2; continue; }
            }
            result.Append(group[i]);
        }
        return result.ToString();
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
