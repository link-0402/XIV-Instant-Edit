using System.Text.RegularExpressions;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Painter;

namespace InstantEdit.Services.Physics;

/// <summary> The game paths of a race's body skeleton and body physics, and of the body models a character draws. </summary>
internal static class PhysicsPaths
{
    // Smallclothes (e0000) and the Emperor's New gear (e0279), which body mods put the bare body on.
    private static readonly Regex BareBody = new(@"(^|/)chara/equipment/e(0000|0279)/model/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary> <c>chara/human/c0801/skeleton/base/b0001/phy_c0801b0001.phyb</c> for 801. </summary>
    public static string BodyPhysics(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/phy_c{race:D4}b0001.phyb";

    /// <summary> <c>chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb</c> for 801. </summary>
    public static string BodySkeleton(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";

    /// <summary>
    /// Names the game file Penumbra loads for <paramref name="gamePath"/>: "the game's own file", "the
    /// game's file for Female Au Ra" when a file swap points it at another race's (<paramref name="racePath"/>
    /// gives a race's path), or "another game file".
    /// </summary>
    public static string GameFileLabel(string gamePath, string? loaded, Func<ushort, string> racePath)
    {
        var path = PathRules.NormalizeGamePath(loaded);
        if (path.Length == 0 || string.Equals(path, PathRules.NormalizeGamePath(gamePath), StringComparison.OrdinalIgnoreCase))
            return "the game's own file";
        return GameRaces.All.FirstOrDefault(race => string.Equals(racePath(race.Code), path, StringComparison.OrdinalIgnoreCase)) is { } swapped
            ? $"the game's file for {swapped.Label}"
            : "another game file";
    }

    /// <summary> Whether a model's game path is bare body: smallclothes or the Emperor's New gear. </summary>
    public static bool IsBareBody(string gamePath) => BareBody.IsMatch(gamePath.Replace('\\', '/'));

    /// <summary> "body", "hands", "legs" or "feet" for a human's model slot 1 to 4. </summary>
    public static string SlotName(int slot) => slot switch
    {
        1 => "body",
        2 => "hands",
        3 => "legs",
        4 => "feet",
        _ => $"slot {slot}",
    };

    private static string SlotSuffix(int slot) => slot switch
    {
        1 => "_top.mdl",
        2 => "_glv.mdl",
        3 => "_dwn.mdl",
        4 => "_sho.mdl",
        _ => ".mdl",
    };

    /// <summary>
    /// The file behind a model a character draws in <paramref name="slot"/>, from Penumbra's resolved
    /// paths (file to the game paths it serves): the file that is <paramref name="drawnFile"/> (as
    /// <see cref="PainterVisibility.NormalizePath"/> writes it), with the slot's game path, whose race says
    /// what the model was made for. A game file Penumbra doesn't redirect is its own game path.
    /// </summary>
    public static (string Actual, string GamePath)? ResolveDrawn(IReadOnlyDictionary<string, HashSet<string>>? resolved, string drawnFile, int slot)
    {
        (string, string)? found = null;
        if (resolved is not null)
            foreach (var (actual, gamePaths) in resolved.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!actual.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || PainterVisibility.NormalizePath(actual) != drawnFile)
                    continue;
                var paths = gamePaths.Select(PathRules.NormalizeGamePath).Where(path => path.Length > 0).Order(StringComparer.Ordinal).ToList();
                if (paths.FirstOrDefault(path => path.EndsWith(SlotSuffix(slot), StringComparison.OrdinalIgnoreCase)) is { } slotPath)
                    return (actual, slotPath);
                found ??= (actual, paths.FirstOrDefault() ?? string.Empty);
            }
        if (found is null && !Path.IsPathRooted(drawnFile))
            return (drawnFile, PathRules.NormalizeGamePath(drawnFile));
        return found;
    }
}
