using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>Gear names by model set, for titling a mod's model entries.</summary>
internal interface IItemNames
{
    /// <summary>The gear that uses a model set, with the slot each piece goes in. Safe to call from a worker.</summary>
    IReadOnlyList<(string Name, EquipSlot Slot)> ForSet(bool accessory, ushort setId);
}

internal enum GameRootKind
{
    Equipment,
    Accessory,
    Weapon,
    Monster,
    Demihuman,
    Hair,
    Face,
    Tail,
    Ear,
    Body,
    Common,
    OtherChara,
    Other,
}

/// <summary>The item a game path belongs to: a gear set, a hairstyle, a weapon body. Character features are race-agnostic.</summary>
internal readonly record struct GameRoot(GameRootKind Kind, ushort Primary, ushort Secondary, string Name)
{
    /// <summary>Roots other mods commonly request files of, so no one mod can know every user.</summary>
    public bool IsShared => Kind is GameRootKind.Body or GameRootKind.Common or GameRootKind.OtherChara or GameRootKind.Other;

    public string Label => Kind switch
    {
        GameRootKind.Equipment => $"Equipment e{Primary:D4}",
        GameRootKind.Accessory => $"Accessory a{Primary:D4}",
        GameRootKind.Weapon    => $"Weapon w{Primary:D4} b{Secondary:D4}",
        GameRootKind.Monster   => $"Monster m{Primary:D4} b{Secondary:D4}",
        GameRootKind.Demihuman => $"Demihuman d{Primary:D4} e{Secondary:D4}",
        GameRootKind.Hair      => $"Hair {Primary}",
        GameRootKind.Face      => $"Face {Primary}",
        GameRootKind.Tail      => $"Tail {Primary}",
        GameRootKind.Ear       => $"Ears {Primary}",
        GameRootKind.Body      => $"Body b{Primary:D4}",
        _                      => Name,
    };

    public override string ToString() => Label;
}

/// <summary>
/// How game paths group and what to call the groups: which model set a file belongs to, where the
/// game looks up a material a model names, and a readable title for each entry of the contents pane.
/// </summary>
internal static class ResourceNaming
{
    private static readonly HashSet<string> GameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "bgcommon", "bg", "cut", "chara", "shader", "ui", "sound", "vfx", "ui_script", "exd", "game_script", "music",
    };

    private static readonly Regex RootFolder = new(
        @"^chara/(?:(?<kind>equipment)/e(?<a>\d{4})|(?<kind>accessory)/a(?<a>\d{4})|(?<kind>weapon)/w(?<a>\d{4})/obj/body/b(?<b>\d{4})|(?<kind>monster)/m(?<a>\d{4})/obj/body/b(?<b>\d{4})|(?<kind>demihuman)/d(?<a>\d{4})/obj/equipment/e(?<b>\d{4})|human/c\d{4}/obj/(?<kind>hair|face|tail|zear|body)/[hftzb](?<a>\d{4}))(?:/|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a path starts with one of the game's top-level folders; anything else can never be requested.</summary>
    public static bool HasValidRoot(string path)
    {
        path = Project.PathNormalizer.GamePath(path);
        var slash = path.IndexOf('/');
        return slash > 0 && GameFolders.Contains(path[..slash]) && !path.Split('/').Any(segment => segment is "" or "." or "..");
    }

    /// <summary>
    /// Whether a redirection key is a game path with folders glued in front ("Group/Option/chara/..."),
    /// which the game never requests; <paramref name="realPath"/> is the game path inside it.
    /// </summary>
    public static bool IsMangledKey(string key, out string realPath)
    {
        realPath = string.Empty;
        if (HasValidRoot(key))
            return false;
        var segments = Project.PathNormalizer.GamePath(key).Split('/');
        for (var i = 1; i < segments.Length - 1; ++i)
        {
            if (!GameFolders.Contains(segments[i]))
                continue;
            var candidate = string.Join('/', segments[i..]);
            if (!HasValidRoot(candidate))
                continue;
            realPath = candidate;
            return true;
        }
        return false;
    }

    /// <summary>The item a game path belongs to.</summary>
    public static GameRoot RootOf(string gamePath)
    {
        var path = Project.PathNormalizer.GamePath(gamePath);
        var match = RootFolder.Match(path);
        if (match.Success)
        {
            var a = ushort.Parse(match.Groups["a"].Value);
            var b = match.Groups["b"].Success ? ushort.Parse(match.Groups["b"].Value) : (ushort)0;
            var kind = match.Groups["kind"].Value switch
            {
                "equipment" => GameRootKind.Equipment,
                "accessory" => GameRootKind.Accessory,
                "weapon"    => GameRootKind.Weapon,
                "monster"   => GameRootKind.Monster,
                "demihuman" => GameRootKind.Demihuman,
                "hair"      => GameRootKind.Hair,
                "face"      => GameRootKind.Face,
                "tail"      => GameRootKind.Tail,
                "zear"      => GameRootKind.Ear,
                _           => GameRootKind.Body,
            };
            return new GameRoot(kind, a, b, string.Empty);
        }
        if (path.StartsWith("chara/common/", StringComparison.Ordinal)) return new GameRoot(GameRootKind.Common, 0, 0, "Shared character files");
        if (path.StartsWith("chara/", StringComparison.Ordinal)) return new GameRoot(GameRootKind.OtherChara, 0, 0, "Other character files");
        var slash = path.IndexOf('/');
        return new GameRoot(GameRootKind.Other, 0, 0, slash > 0 ? path[..slash] : path);
    }

    /// <summary>A texture path's DX11 twin: the same file name with the -- prefix added, or taken off when it has one.</summary>
    public static string Dx11Twin(string texturePath)
    {
        var path = Project.PathNormalizer.GamePath(texturePath);
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        return name.StartsWith("--", StringComparison.Ordinal) ? folder + name[2..] : folder + "--" + name;
    }

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
    /// The game path the game requests for a material a model names relatively: where
    /// <see cref="MaterialFolder"/> says, except that a hair model naming its own race's material for
    /// a shared hairstyle is rebased onto the race the game shares it under ("/mt_c0801h0127_hir_a.mtrl"
    /// on c0801's h0127 loads c0201's), as <see cref="MaterialNaming.Resolve"/> does.
    /// </summary>
    public static string RelativeMaterialPath(string modelPath, string materialFileName)
    {
        var name = materialFileName.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        if (ModelNaming.Parse(modelPath) is { Kind: GameModelKind.Hair } hair
            && MaterialNaming.Resolve(hair, name, 1) is { Kind: MaterialRequestKind.Path, Path: { Length: > 0 } path })
            return path;
        return $"{MaterialFolder(name) ?? FallbackMaterialFolder(modelPath)}/{name}";
    }

    /// <summary>
    /// The folder the game looks a material up in when a model names it relatively
    /// ("/mt_c0201e6114_sho_a.mtrl"): the name itself says which model set it belongs to. Gear always
    /// resolves through variant v0001 here; a mod may also carry other variants, which are matched by name.
    /// Faces and Viera ears have no variant folder.
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
            ("c", "f") => $"chara/human/c{a}/obj/face/f{b}/material",
            ("c", "t") => $"chara/human/c{a}/obj/tail/t{b}/material/v0001",
            ("c", "z") => $"chara/human/c{a}/obj/zear/z{b}/material",
            ("c", "b") => $"chara/human/c{a}/obj/body/b{b}/material/v0001",
            ("w", "b") => $"chara/weapon/w{a}/obj/body/b{b}/material/v0001",
            ("m", "b") => $"chara/monster/m{a}/obj/body/b{b}/material/v0001",
            ("d", "e") => $"chara/demihuman/d{a}/obj/equipment/e{b}/material/v0001",
            _          => null,
        };
    }

    /// <summary>
    /// Where a model's own materials live when the name does not say: its folder, with model swapped
    /// for material/v0001 (just material/ for faces and Viera ears, which have no variants).
    /// </summary>
    public static string FallbackMaterialFolder(string modelPath)
    {
        var folder = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
        if (!folder.EndsWith("/model", StringComparison.Ordinal))
            return folder;
        var set = folder[..^"/model".Length];
        return NoVariantSet.IsMatch(set) ? set + "/material" : set + "/material/v0001";
    }

    private static readonly Regex NoVariantSet = new(@"/obj/(?:face/f|zear/z)\d{4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>Where a slot of a material made here writes its own texture: the path typed in for it, else <see cref="AuthoredTexturePath"/>.</summary>
    public static string SlotTexturePath(string materialGamePath, TextureSlot slot)
        => string.IsNullOrWhiteSpace(slot.GamePath)
            ? AuthoredTexturePath(materialGamePath, slot.Postfix)
            : PathNormalizer.GamePath(slot.GamePath);

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
