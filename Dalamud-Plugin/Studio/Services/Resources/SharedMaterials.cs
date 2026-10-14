using System;
using System.Text.RegularExpressions;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>
/// Materials a mod names without carrying them, because the player's body mod provides them: skin,
/// and what sits on it (nails, pubes, piercings). Neither the mod nor the game has them, yet they are
/// not missing, so the Resources tree lists them as shared instead of flagging them.
/// </summary>
internal static class SharedMaterials
{
    private readonly record struct Exclusion(Regex Pattern, string Note);

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// A human body's materials ("/mt_c0201b0001_bibo.mtrl"). The game loads them from the wearer's own body
    /// folder, whatever race the name says, so whichever body mod is on provides them. Only these are shared:
    /// a gear or accessory material the mod lacks is missing, whatever its name.
    /// </summary>
    private static readonly Regex BodyFolder = new(@"^chara/human/c\d{4}/obj/body/b\d{4}/material/", Options);

    /// <summary>Common body mod materials in that folder, for the note; anything else there is skin.</summary>
    private static readonly Exclusion[] Notes =
    {
        new(new Regex(@"/[^/]*pube[^/]*\.mtrl$", Options),  "pubes material: the player's body mod provides it"),
        new(new Regex(@"/[^/]*pierc[^/]*\.mtrl$", Options), "piercings material: the player's body mod provides it"),
        new(new Regex(@"/[^/]*_ya(?:finger|toe)[^/]*\.mtrl$", Options), "nails material: the player's body mod provides it"),
    };

    private const string SkinNote = "skin material: the player's body mod provides it";

    /// <summary>Why a material the mod and the game both lack is still not missing, or null when it is.</summary>
    public static string? NoteFor(string gamePath)
    {
        if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) || !BodyFolder.IsMatch(gamePath))
            return null;
        foreach (var note in Notes)
            if (note.Pattern.IsMatch(gamePath))
                return note.Note;
        return SkinNote;
    }
}
