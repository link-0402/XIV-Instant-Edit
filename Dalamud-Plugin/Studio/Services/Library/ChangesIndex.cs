using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Library;

/// <summary>What kind of thing a mod changes.</summary>
internal enum ChangeKind { Item, Emote, Face, Hair, StandingIdle, GroundSit, ChairSit, Other }

/// <summary>One thing a mod changes: an item, a face, an idle pose. <see cref="Search"/> is the text a search is matched against.</summary>
internal sealed record ChangeTag(ChangeKind Kind, string Label, string Search);

/// <summary>What the library's search box searches: a field of the listing (up to Folder), or what mods change.</summary>
internal enum SearchIn { Name, Author, Tag, Folder, Anything, Item, Emote, Face, Hair, StandingIdle, GroundSit, ChairSit }

/// <summary>
/// What a mod changes, for the library's "Search in" filter. Faces, hair, idles and sit poses are
/// recognised from the game paths the mod redirects; items, emotes, faces and hair also come from
/// Penumbra's changed items. Mod Optimizer's replacement index, rebuilt here. Dalamud-free.
/// </summary>
internal static partial class ChangesIndex
{
    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/obj/face/f(?<face>\d{4})/", RegexOptions.IgnoreCase)]
    private static partial Regex FaceRegex();

    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/obj/hair/h(?<hair>\d{4})/", RegexOptions.IgnoreCase)]
    private static partial Regex HairRegex();

    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/animation/a0001/bt_common/(?:resident/(?<base>idle)\.pap|emote/(?<prefix>s_|j_|l_)?pose(?<pose>\d{2})_(?:loop|start)\.pap|emote/(?<emote>[a-z0-9_]+)\.pap)$", RegexOptions.IgnoreCase)]
    private static partial Regex AnimationRegex();

    [GeneratedRegex(@"^chara/(?<type>equipment|accessory|weapon)/(?<id>[eaw]\d{4})/", RegexOptions.IgnoreCase)]
    private static partial Regex GearRegex();

    /// <summary>The tags of a mod's redirected game paths and Penumbra's changed items, without repeats, sorted by kind and label.</summary>
    public static IReadOnlyList<ChangeTag> Classify(IEnumerable<string> gamePaths, IEnumerable<(string Name, ChangeKind Kind)> changedItems)
    {
        var tags = new HashSet<ChangeTag>();
        foreach (var gamePath in gamePaths)
            if (FromPath(gamePath) is { } tag)
                tags.Add(tag);
        foreach (var (name, kind) in changedItems)
            if (!string.IsNullOrWhiteSpace(name))
                tags.Add(Tag(kind, name.Trim()));
        return tags.OrderBy(tag => tag.Kind).ThenBy(tag => tag.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Whether a search looks in what mods change (which needs a scan), not in the listing's fields.</summary>
    public static bool IsChangesSearch(SearchIn kind) => kind > SearchIn.Folder;

    /// <summary>
    /// Whether any tag of the kind searched holds every word of the query (an empty query: any tag of
    /// that kind). A listing search (<see cref="IsChangesSearch"/> false) matches nothing here.
    /// </summary>
    public static bool Matches(IReadOnlyList<ChangeTag> tags, SearchIn kind, string query)
    {
        if (!IsChangesSearch(kind))
            return false;
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tags.Any(tag => Covers(kind, tag.Kind) && terms.All(term => tag.Search.Contains(term, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether one field of a mod's listing holds the query: its name, author, one of its tags, or the
    /// folder Penumbra files it under. An empty query matches; a changes search matches nothing here.
    /// </summary>
    public static bool MatchesListing(InstalledMod mod, SearchIn kind, string query)
    {
        if (query.Length == 0)
            return !IsChangesSearch(kind);
        return kind switch
        {
            SearchIn.Name   => mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
            SearchIn.Author => mod.Author.Contains(query, StringComparison.OrdinalIgnoreCase),
            SearchIn.Tag    => mod.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)),
            SearchIn.Folder => mod.Folder.Contains(query, StringComparison.OrdinalIgnoreCase),
            _               => false,
        };
    }

    /// <summary>
    /// What one of Penumbra's changed items is, from the type of Penumbra's object for it. A
    /// customization is a (race, gender, customize index, value) tuple: only its index tells a face
    /// from a hairstyle, a tail, ears or face paint (which are none of the searched kinds).
    /// </summary>
    public static ChangeKind KindOfChangedItem(object? value) => value switch
    {
        null => ChangeKind.Other,
        System.Runtime.CompilerServices.ITuple { Length: 4 } customization
            when value.GetType().Name.StartsWith("ValueTuple", StringComparison.Ordinal) => customization[2]?.ToString() switch
            {
                "Face"      => ChangeKind.Face,
                "Hairstyle" => ChangeKind.Hair,
                _           => ChangeKind.Other,
            },
        _ => value.GetType().Name switch
        {
            "EquipItem" => ChangeKind.Item,
            "Emote"     => ChangeKind.Emote,
            _           => ChangeKind.Other,
        },
    };

    /// <summary>Every game path a mod's options redirect or swap, from meta.json.</summary>
    public static IEnumerable<string> GamePathsOf(PenumbraModMeta meta)
    {
        var paths = new List<string>();
        void Walk(JToken? token)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        if (property.Name is "Files" or "FileSwaps" && property.Value is JObject mapping)
                            paths.AddRange(mapping.Properties().Select(p => p.Name));
                        else
                            Walk(property.Value);
                    }
                    break;
                case JArray array:
                    foreach (var child in array)
                        Walk(child);
                    break;
            }
        }
        Walk(meta.Root["DefaultData"]);
        Walk(meta.Root["Groups"]);
        return paths;
    }

    private static bool Covers(SearchIn kind, ChangeKind tag) => kind switch
    {
        SearchIn.Anything     => true,
        SearchIn.Item         => tag == ChangeKind.Item,
        SearchIn.Emote        => tag == ChangeKind.Emote,
        SearchIn.Face         => tag == ChangeKind.Face,
        SearchIn.Hair         => tag == ChangeKind.Hair,
        SearchIn.StandingIdle => tag == ChangeKind.StandingIdle,
        SearchIn.GroundSit    => tag == ChangeKind.GroundSit,
        SearchIn.ChairSit     => tag == ChangeKind.ChairSit,
        _                     => false,
    };

    private static ChangeTag? FromPath(string gamePath)
    {
        gamePath = gamePath.Replace('\\', '/').TrimStart('/');
        var face = FaceRegex().Match(gamePath);
        if (face.Success)
        {
            var race = face.Groups["race"].Value;
            var id = face.Groups["face"].Value;
            return Tag(ChangeKind.Face, $"{RaceName(race)} face {int.Parse(id)} (f{id})", $"c{race}");
        }

        var hair = HairRegex().Match(gamePath);
        if (hair.Success)
        {
            var race = hair.Groups["race"].Value;
            var id = hair.Groups["hair"].Value;
            return Tag(ChangeKind.Hair, $"{RaceName(race)} hair {int.Parse(id)} (h{id})", $"c{race}");
        }

        var animation = AnimationRegex().Match(gamePath);
        if (animation.Success)
        {
            var code = animation.Groups["race"].Value;
            var race = RaceName(code);
            if (animation.Groups["base"].Success)
                return Tag(ChangeKind.StandingIdle, $"{race} base idle (idle.pap)", $"c{code}");
            if (animation.Groups["pose"].Success)
            {
                var (kind, name) = animation.Groups["prefix"].Value.ToLowerInvariant() switch
                {
                    "s_" => (ChangeKind.ChairSit, "chair sit"),
                    "j_" => (ChangeKind.GroundSit, "groundsit"),
                    "l_" => (ChangeKind.Emote, "doze"),
                    _    => (ChangeKind.StandingIdle, "standing idle"),
                };
                return Tag(kind, $"{race} {name} pose {animation.Groups["pose"].Value}", $"c{code}");
            }
            return Tag(ChangeKind.Emote, $"{race} {animation.Groups["emote"].Value.ToLowerInvariant()} animation", $"c{code}");
        }

        var gear = GearRegex().Match(gamePath);
        return gear.Success
            ? Tag(ChangeKind.Item, $"{gear.Groups["type"].Value.ToLowerInvariant()} model {gear.Groups["id"].Value.ToLowerInvariant()}")
            : null;
    }

    /// <summary>"Au Ra (Female)" for c1401, "c0104" for NPC and unknown codes.</summary>
    private static string RaceName(string code) => ushort.TryParse(code, out var race) ? RaceInfo.NameOf(race) : $"c{code}";

    private static ChangeTag Tag(ChangeKind kind, string label, string extra = "")
        => new(kind, label, Normalize(extra.Length > 0 ? $"{label} {extra}" : label));

    /// <summary>Lower case, apostrophes dropped (miqote finds Miqo'te), brackets and commas as spaces.</summary>
    private static string Normalize(string text)
    {
        var chars = text.ToLowerInvariant().Where(c => c is not ('\'' or '’')).Select(c => c is '(' or ')' or ',' or '/' ? ' ' : c).ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
