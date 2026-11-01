namespace InstantEdit.Services.Actors;

/// <summary>A Penumbra collection, Glamourer design or Customize+ profile a preset uses: its id and the name it had when picked.</summary>
internal sealed record PartnerLink(Guid Id, string Name);

/// <summary>A partner to spawn: its name and the collection, design and profile put on it, each optional.</summary>
internal sealed record PartnerPreset(Guid Id, string Name, PartnerLink? Collection, PartnerLink? Design, PartnerLink? Profile);

/// <summary>The rule a partner's name follows and the messages for a name that breaks it.</summary>
internal static class PartnerNames
{
    public const string Taken = "Name is taken";
    public const string Yours = "Use a name other than your character's";
    public const string FirstLast = "Use a first and last name";

    /// <summary>
    /// Whether <paramref name="name"/> is shaped like a player's name, which Penumbra needs to tell actors apart: two
    /// parts split by one space, each 2-15 characters, A-Z then a-z, ' or -, without --, -' or '-, 21 characters at most.
    /// </summary>
    public static bool IsPlayerName(string name)
    {
        if (name.Length > 21)
            return false;
        var parts = name.Split(' ');
        return parts.Length == 2 && parts.All(IsPart);
    }

    private static bool IsPart(string part)
    {
        if (part.Length is < 2 or > 15 || part[0] is < 'A' or > 'Z')
            return false;
        for (var i = 1; i < part.Length; i++)
        {
            var c = part[i];
            if (c is not (>= 'a' and <= 'z' or '\'' or '-'))
                return false;
            if ((part[i - 1], c) is ('-', '-') or ('-', '\'') or ('\'', '-'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Why <paramref name="name"/>, trimmed, can't name the preset <paramref name="self"/>, or null: your character's name
    /// or another preset's, case ignored, then not a player name.
    /// </summary>
    public static string? Problem(string name, IEnumerable<PartnerPreset> presets, Guid self, string? playerName)
    {
        var trimmed = name.Trim();
        if (playerName != null && string.Equals(trimmed, playerName.Trim(), StringComparison.OrdinalIgnoreCase))
            return Yours;
        if (presets.Any(preset => preset.Id != self && string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return Taken;
        return IsPlayerName(trimmed) ? null : FirstLast;
    }
}
