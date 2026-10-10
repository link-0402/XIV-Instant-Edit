using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>Gear names by model set, for titling a mod's model entries.</summary>
internal interface IItemNames
{
    /// <summary>The gear that uses a model set, with the slot each piece goes in. Safe to call from a worker.</summary>
    IReadOnlyList<(string Name, EquipSlot Slot)> ForSet(bool accessory, ushort setId);
}

/// <summary>
/// How game paths group and what to call the groups: which model set a file belongs to, where the
/// game looks up a material a model names, and a readable title for each entry of the contents pane.
/// </summary>
internal static class ResourceNaming
{
    private static readonly Regex SetFolder = new(
        @"^(chara/(?:equipment/e\d{4}|accessory/a\d{4}|weapon/w\d{4}/obj/body/b\d{4}|monster/m\d{4}/obj/body/b\d{4}|demihuman/d\d{4}/obj/equipment/e\d{4}|human/c\d{4}/obj/(?:hair|face|tail|zear|body)/[hftzb]\d{4}))/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MaterialName = new(
        @"^mt_([cwmd])(\d{4})([a-z])(\d{4})(?:_|\.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HumanRace = new(@"c(\d{4})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> RaceNames =
        RaceInfo.AllRaces.ToDictionary(r => r.RaceCode, r => r.DisplayName, StringComparer.Ordinal);

    /// <summary>
    /// The model set a game path belongs to — "chara/equipment/e6114", "chara/weapon/w0201/obj/body/b0001"
    /// — with a character feature's race written as c****, so every race's copy of one hairstyle lands
    /// in the same set. Null for anything outside a model set.
    /// </summary>
    public static string? SetKey(string gamePath)
    {
        var match = SetFolder.Match(gamePath);
        if (!match.Success)
            return null;
        var folder = match.Groups[1].Value;
        return folder.StartsWith("chara/human/c", StringComparison.Ordinal)
            ? "chara/human/c****" + folder["chara/human/c0000".Length..]
            : folder;
    }

    /// <summary>A path with every race code written as c****, so the same file for several races groups as one.</summary>
    public static string RaceAgnostic(string gamePath)
        => gamePath.StartsWith("chara/human/c", StringComparison.Ordinal)
            ? HumanRace.Replace(gamePath, "c****")
            : gamePath;

    /// <summary>The race codes a set of paths covers, as "Midlander (Female)" etc.</summary>
    public static List<string> Races(IEnumerable<string> gamePaths)
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in gamePaths)
        {
            var file = Path.GetFileName(path);
            var inName = HumanRace.Match(file);
            if (inName.Success && RaceNames.ContainsKey(inName.Groups[1].Value))
                codes.Add(inName.Groups[1].Value);
            else if (path.StartsWith("chara/human/c", StringComparison.Ordinal) && path.Length > 17 && RaceNames.ContainsKey(path[13..17]))
                codes.Add(path[13..17]);
        }
        return codes.Select(c => RaceNames[c]).ToList();
    }

    /// <summary>
    /// The folder the game looks a material up in when a model names it relatively
    /// ("/mt_c0201e6114_sho_a.mtrl"): the name itself says which model set it belongs to. Gear always
    /// resolves through variant v0001 here; a mod may also carry other variants, which are matched by name.
    /// </summary>
    public static string? MaterialFolder(string materialFileName)
    {
        var match = MaterialName.Match(materialFileName.TrimStart('/'));
        if (!match.Success)
            return null;

        var (prefix, a, kind, b) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
        return (prefix, kind) switch
        {
            ("c", "e") => $"chara/equipment/e{b}/material/v0001",
            ("c", "a") => $"chara/accessory/a{b}/material/v0001",
            ("c", "h") => $"chara/human/c{a}/obj/hair/h{b}/material/v0001",
            ("c", "f") => $"chara/human/c{a}/obj/face/f{b}/material/v0001",
            ("c", "t") => $"chara/human/c{a}/obj/tail/t{b}/material/v0001",
            ("c", "z") => $"chara/human/c{a}/obj/zear/z{b}/material/v0001",
            ("c", "b") => $"chara/human/c{a}/obj/body/b{b}/material/v0001",
            ("w", "b") => $"chara/weapon/w{a}/obj/body/b{b}/material/v0001",
            ("m", "b") => $"chara/monster/m{a}/obj/body/b{b}/material/v0001",
            ("d", "e") => $"chara/demihuman/d{a}/obj/equipment/e{b}/material/v0001",
            _          => null,
        };
    }

    /// <summary>Where a model's own materials live when the name does not say: its folder, with model swapped for material/v0001.</summary>
    public static string FallbackMaterialFolder(string modelPath)
    {
        var folder = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
        return folder.EndsWith("/model", StringComparison.Ordinal)
            ? folder[..^"/model".Length] + "/material/v0001"
            : folder;
    }

    /// <summary>The variant-free part of a material folder ("…/material/v0001" → "…/material/"), for matching any variant.</summary>
    public static string MaterialFolderStem(string folder)
    {
        int at = folder.LastIndexOf("/material", StringComparison.Ordinal);
        return at >= 0 ? folder[..(at + "/material".Length)] + "/" : folder + "/";
    }

    /// <summary>
    /// Where a texture of a material made here goes: the model set's texture folder (or the
    /// material's own folder outside a set), named after the material and the slot's postfix —
    /// "chara/equipment/e6114/texture/mt_c0201e6114_sho_a_normal.tex".
    /// </summary>
    public static string AuthoredTexturePath(string materialGamePath, string postfix)
    {
        var path = materialGamePath.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(path);
        int at = path.IndexOf("/material/", StringComparison.Ordinal);
        var folder = at > 0 ? path[..at] + "/texture" : (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty);
        var file = string.IsNullOrWhiteSpace(postfix) ? name : $"{name}_{postfix.Trim()}";
        var safe = new string(file.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return folder.Length > 0 ? $"{folder}/{safe}.tex" : $"{safe}.tex";
    }

    /// <summary>The title and subtitle of a model set's entry.</summary>
    public static (string Title, string Subtitle) SetTitle(string setKey, IEnumerable<string> gamePaths, IItemNames? names)
    {
        var parts = setKey.Split('/');
        var races = Races(gamePaths);
        string raceText = races.Count switch
        {
            0 => string.Empty,
            1 => races[0],
            _ => $"{races.Count} races",
        };

        if (parts.Length >= 3 && parts[1] is "equipment" or "accessory" && TryId(parts[2], out var setId))
        {
            bool accessory = parts[1] == "accessory";
            var slots = gamePaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .Select(n => n.Length > 4 && n[^4] == '_' ? n[^3..] : string.Empty)
                .Where(k => SlotInfo.ReverseMap.ContainsKey(k))
                .Select(k => SlotInfo.ReverseMap[k])
                .ToHashSet();

            var items = names?.ForSet(accessory, setId) ?? Array.Empty<(string, EquipSlot)>();
            var matching = items.Where(i => slots.Count == 0 || slots.Contains(i.Slot)).Select(i => i.Name).Distinct().ToList();
            var slotText = slots.Count == 1 ? SlotInfo.DisplayLabelMap[slots.First()] : slots.Count > 1 ? $"{slots.Count} slots" : string.Empty;
            var title = matching.Count switch
            {
                0 => $"{(accessory ? "Accessory" : "Equipment")} {parts[2]}",
                1 => matching[0],
                _ => $"{matching[0]} +{matching.Count - 1}",
            };
            return (title, Join(parts[2], slotText, raceText));
        }

        if (parts.Length >= 6 && parts[1] == "human")
        {
            var kind = parts[4] switch
            {
                "hair" => "Hair",
                "face" => "Face",
                "tail" => "Tail",
                "zear" => "Ears",
                "body" => "Body",
                _      => parts[4],
            };
            var id = TryId(parts[5], out var featureId) ? featureId.ToString(CultureInfo.InvariantCulture) : parts[5];
            return ($"{kind} {id}", Join(parts[5], raceText));
        }

        if (parts.Length >= 6 && parts[1] is "weapon" or "monster" or "demihuman")
        {
            var label = parts[1] switch
            {
                "weapon"  => "Weapon",
                "monster" => "Monster",
                _         => "Demihuman",
            };
            return ($"{label} {parts[2]} · {parts[5]}", setKey);
        }

        return (setKey, string.Empty);
    }

    private static bool TryId(string part, out ushort id)
        => ushort.TryParse(part.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
}
