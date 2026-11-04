using System.Text;
using System.Text.Json;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>The marker an Instant Edit expression library keeps in its mod root: its name and the pack it plays as.</summary>
internal sealed record FaceLibraryMarker(int Version, string Name, string Pack);

/// <summary>
/// The rules for the expression libraries Instant Edit makes: the marker that tells them apart from other mods,
/// the pack a library name plays as, and the names of the faces saved into one.
/// </summary>
internal static class FaceLibraryMod
{
    public const string MarkerFile = "instant-edit-expressions.json";
    public const int Version = 1;
    private const long MaxMarkerBytes = 64 * 1024;
    // A PAP entry's name field is 32 bytes with its terminator.
    private const int MaxMotionBytes = 31;
    private const string FacePrefix = "cfxf_";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Reads the marker in <paramref name="modRoot"/>; false when there is none or it isn't a valid one.</summary>
    public static bool TryRead(string modRoot, out FaceLibraryMarker marker)
    {
        marker = null!;
        try
        {
            var path = Path.Combine(modRoot, MarkerFile);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxMarkerBytes) return false;
            if (JsonSerializer.Deserialize<FaceLibraryMarker>(File.ReadAllText(path), Json) is not { Version: Version } read ||
                string.IsNullOrWhiteSpace(read.Name) || read.Pack is null || Clean(read.Pack) != read.Pack || !TmbFile.IsSafePackName(read.Pack))
                return false;
            marker = read;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Serialize(FaceLibraryMarker marker) => JsonSerializer.Serialize(marker, Json);

    /// <summary>The pack a library named <paramref name="libraryName"/> plays as, or null when the name has nothing usable.</summary>
    public static string? PackName(string libraryName) => Clean(libraryName) is { Length: > 0 } pack && TmbFile.IsSafePackName(pack) ? pack : null;

    /// <summary>
    /// Why a new library can't use <paramref name="pack"/>, or null: the game has it in some race's face folder
    /// (<paramref name="gamePackAnywhere"/>) or an installed library uses it.
    /// </summary>
    public static string? PackProblem(string pack, Func<string, bool> gamePackAnywhere, IEnumerable<string> installedPacks)
    {
        if (PackName(pack) != pack) return "Use letters or numbers in the name.";
        if (gamePackAnywhere(pack)) return "The game already uses that name.";
        if (installedPacks.Contains(pack, StringComparer.OrdinalIgnoreCase)) return "An installed expression set already uses that name.";
        return null;
    }

    /// <summary>The face motion for <paramref name="stem"/> (cfxf_ and the stem cleaned like a pack), or null when too long or empty.</summary>
    public static string? FaceName(string stem) =>
        Clean(stem) is { Length: > 0 } clean && FacePrefix.Length + clean.Length <= MaxMotionBytes ? FacePrefix + clean : null;

    /// <summary>
    /// Why a face can't be saved as <paramref name="motion"/>, or null. The game having that face for the folder
    /// (<paramref name="gameHasForFolder"/>) refuses it, except for faces copied in from other sets.
    /// </summary>
    public static string? FaceNameProblem(string motion, Func<string, bool> gameHasForFolder, bool copy)
    {
        if (copy) return null;
        if (!motion.StartsWith(FacePrefix, StringComparison.Ordinal) || FaceName(motion[FacePrefix.Length..]) != motion) return "Use letters or numbers in the name.";
        if (gameHasForFolder(motion)) return "The game already has a face with that name.";
        return null;
    }

    // Lowercase a-z, 0-9 and _; every other run of characters becomes one _, and none are left at the ends.
    private static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') result.Append(c);
            else if (result.Length > 0 && result[^1] != '_') result.Append('_');
        }
        return result.ToString().TrimEnd('_');
    }
}
