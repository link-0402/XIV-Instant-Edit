using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InstantEdit.Studio.Models;

/// <summary>
/// Game paths of character features (hair, faces, tails, ears), which live under the
/// race they belong to rather than in a shared equipment folder:
/// <c>chara/human/c{race}/obj/{folder}/{prefix}{id:D4}/…</c>.
///
/// Ids are discovered by probing <see cref="ModelGamePath"/> against the game's files
/// (see <c>GameDataService.GetFeatureIds</c>), so these patterns are checked against the
/// real data every time the browser lists a kind.
/// </summary>
public static class FeatureNaming
{
    private sealed record KindInfo(string Folder, char Prefix, string ModelSuffix, string? MaterialPart, string Label);

    private static readonly IReadOnlyDictionary<SubjectKind, KindInfo> Kinds = new Dictionary<SubjectKind, KindInfo>
    {
        [SubjectKind.Hair] = new("hair", 'h', "hir", "hir", "Hair"),
        [SubjectKind.Face] = new("face", 'f', "fac", "fac", "Face"),
        [SubjectKind.Tail] = new("tail", 't', "til", null,  "Tail"),
        [SubjectKind.Ear]  = new("zear", 'z', "zer", null,  "Ears"),
    };

    /// <summary>Matches a human feature material name and captures its race code, kind prefix and id.</summary>
    private static readonly Regex MaterialNamePattern = new(@"^mt_c(\d{4})([hftz])(\d{4})", RegexOptions.Compiled);

    public static string KindLabel(SubjectKind kind) => Kinds[kind].Label;

    public static char Prefix(SubjectKind kind) => Kinds[kind].Prefix;

    /// <summary>e.g. "chara/human/c0101/obj/hair/h0005".</summary>
    public static string Folder(SubjectKind kind, string raceCode, ushort id)
        => $"chara/human/c{raceCode}/obj/{Kinds[kind].Folder}/{Kinds[kind].Prefix}{id:D4}";

    /// <summary>e.g. "chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl".</summary>
    public static string ModelGamePath(SubjectKind kind, string raceCode, ushort id)
        => $"{Folder(kind, raceCode, id)}/model/c{raceCode}{Kinds[kind].Prefix}{id:D4}_{Kinds[kind].ModelSuffix}.mdl";

    /// <summary>
    /// Material folder. Character features only ever use the v0001 material set, so unlike
    /// gear there is no variant to redirect.
    /// </summary>
    public static string MaterialFolder(SubjectKind kind, string raceCode, ushort id)
        => $"{Folder(kind, raceCode, id)}/material/v0001";

    public static string TextureFolder(SubjectKind kind, string raceCode, ushort id)
        => $"{Folder(kind, raceCode, id)}/texture";

    /// <summary>
    /// Fallback name for a new material, e.g. "mt_c0101h0005_hir_a". The vanilla model's own
    /// material names are preferred whenever it exists (see "Match vanilla materials").
    /// </summary>
    public static string DefaultMaterialName(SubjectKind kind, string raceCode, ushort id, int letter)
    {
        var info = Kinds[kind];
        var suffix = (char)('a' + Math.Max(letter, 1) - 1);
        var part = info.MaterialPart != null ? $"_{info.MaterialPart}" : string.Empty;
        return $"mt_c{raceCode}{info.Prefix}{id:D4}{part}_{suffix}";
    }

    /// <summary>
    /// Where a feature material named <paramref name="materialName"/> actually lives. Feature
    /// materials carry their race and id in their name (a model may point at another race's
    /// material), so those win over the model's own race and id when present.
    /// </summary>
    public static string ResolveMaterialPath(SubjectKind kind, string materialName, string fallbackRaceCode, ushort fallbackId)
    {
        var name = MaterialNaming.SanitizeFileName(materialName.TrimStart('/'));
        var match = MaterialNamePattern.Match(name);
        if (match.Success && match.Groups[2].Value[0] == Kinds[kind].Prefix
            && ushort.TryParse(match.Groups[3].Value, out var id))
            return $"{MaterialFolder(kind, match.Groups[1].Value, id)}/{name}.mtrl";

        return $"{MaterialFolder(kind, fallbackRaceCode, fallbackId)}/{name}.mtrl";
    }

    /// <summary>
    /// The race code a feature material name carries, e.g. "0201" in "/mt_c0201h0127_hir_a.mtrl".
    /// A vanilla model's names say which race the game shares that feature's materials under.
    /// </summary>
    public static bool TryReadRaceCode(string materialName, out string raceCode)
    {
        var match = MaterialNamePattern.Match(MaterialNaming.SanitizeFileName(materialName.TrimStart('/')));
        raceCode = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// Races that can have this kind at all. The browser narrows this further to races that
    /// actually ship at least one id.
    /// </summary>
    public static IEnumerable<RaceGender> CandidateRaces(SubjectKind kind)
    {
        foreach (var rg in RaceInfo.AllRaces)
        {
            bool possible = kind switch
            {
                SubjectKind.Tail => rg.Race is PlayerRace.Miqote or PlayerRace.AuRa or PlayerRace.Hrothgar,
                SubjectKind.Ear  => rg.Race is PlayerRace.Viera,
                _                => true,
            };
            if (possible) yield return rg;
        }
    }
}
