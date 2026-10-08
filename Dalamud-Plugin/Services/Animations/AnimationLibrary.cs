using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations;

/// <summary>The lists the Timeline window's loader shows.</summary>
internal enum AnimationLibraryGroup : byte { Emotes, Idles, GroundSits, ChairSits, Lying, Movement, Expressions, Other }

/// <summary>
/// One animation the loader can play on the GPose actor: an ActionTimeline row with a name people
/// know it by. <paramref name="Face"/> plays it on the face, as an expression.
/// </summary>
/// <param name="Detail">What it is in the game: the emote's command, or the timeline's key.</param>
internal sealed record AnimationLibraryItem(AnimationLibraryGroup Group, ushort Timeline, string Name, string Detail, string Key,
    bool Face = false);

/// <summary>
/// Everything the loader offers, from the Emote and ActionTimeline sheets: emotes with their sitting
/// and upper-body versions, the standing idles and the ground, chair and bed poses numbered as
/// /changepose cycles them, every movement track, the expressions, and every other timeline by its
/// key. The poses come from the hidden emotes /changepose plays, so a new pose appears by itself.
/// </summary>
internal static class AnimationLibrary
{
    /// <summary>An ActionTimeline row: its key and how it loads.</summary>
    internal sealed record TimelineRow(ushort Id, string Key, byte LoadType);

    /// <summary>
    /// An Emote row: its name, command, category name and its seven timelines (0 for none): the
    /// emote, its start, and its versions on the ground, in a chair, for the upper body and lying down.
    /// </summary>
    internal sealed record EmoteRow(uint Id, string Name, string Command, string Category, ImmutableArray<ushort> Timelines);

    // The base pose each family cycles from, and the numbered poses /changepose adds to it.
    private static readonly (AnimationLibraryGroup Group, string Name, string Poses, string Base, Regex Pose)[] Families =
    [
        (AnimationLibraryGroup.Idles, "Idle", "Idle pose", "normal/idle", new Regex(@"^emote/pose(\d+)_loop$", RegexOptions.CultureInvariant)),
        (AnimationLibraryGroup.GroundSits, "Ground sit", "Ground sit pose", "emote/jmn", new Regex(@"^emote/j_pose(\d+)_loop$", RegexOptions.CultureInvariant)),
        (AnimationLibraryGroup.ChairSits, "Chair sit", "Chair sit pose", "emote/sit", new Regex(@"^emote/s_pose(\d+)_loop$", RegexOptions.CultureInvariant)),
        (AnimationLibraryGroup.Lying, "Lying in bed", "Lying pose", "emote/bed_liedown_loop", new Regex(@"^emote/l_pose(\d+)_loop$", RegexOptions.CultureInvariant)),
    ];

    // An emote's timelines after its own: its start, then its versions in these places.
    private static readonly string?[] Versions = [null, "start", "on the ground", "in a chair", "upper body", "lying down", null];

    public static string GroupName(AnimationLibraryGroup group) => group switch
    {
        AnimationLibraryGroup.Emotes => "Emotes",
        AnimationLibraryGroup.Idles => "Idles",
        AnimationLibraryGroup.GroundSits => "Ground sits",
        AnimationLibraryGroup.ChairSits => "Chair sits",
        AnimationLibraryGroup.Lying => "Lying",
        AnimationLibraryGroup.Movement => "Movement",
        AnimationLibraryGroup.Expressions => "Expressions",
        _ => "Other",
    };

    /// <summary>Reads the sheets. Slow enough to run off the framework thread.</summary>
    public static ImmutableArray<AnimationLibraryItem> Read(IDataManager data)
    {
        var timelines = data.GetExcelSheet<ActionTimeline>()
            .Where(row => row.RowId is > 0 and <= ushort.MaxValue)
            .Select(row => new TimelineRow((ushort)row.RowId, row.Key.ExtractText(), row.LoadType))
            .ToArray();
        var emotes = data.GetExcelSheet<Emote>().Select(row => new EmoteRow(row.RowId, row.Name.ExtractText(),
            row.TextCommand.IsValid ? row.TextCommand.Value.Command.ExtractText() : string.Empty,
            row.EmoteCategory.IsValid ? row.EmoteCategory.Value.Name.ExtractText() : string.Empty,
            [.. row.ActionTimeline.Select(t => t.RowId is > 0 and <= ushort.MaxValue ? (ushort)t.RowId : (ushort)0)])).ToArray();
        return Build(timelines, emotes);
    }

    /// <summary>The loader's items in group order, from the sheets' rows.</summary>
    public static ImmutableArray<AnimationLibraryItem> Build(IEnumerable<TimelineRow> timelineRows, IEnumerable<EmoteRow> emoteRows)
    {
        var timelines = new Dictionary<ushort, TimelineRow>();
        foreach (var row in timelineRows)
            if (row.Key.Length > 0)
                timelines.TryAdd(row.Id, row);
        var emotes = emoteRows.ToArray();
        var items = new List<AnimationLibraryItem>();
        var listed = new HashSet<ushort>();

        void Add(AnimationLibraryGroup group, ushort id, string name, string detail, bool face = false)
        {
            if (!timelines.TryGetValue(id, out var row))
                return;
            items.Add(new AnimationLibraryItem(group, id, name, detail, row.Key, face));
            listed.Add(id);
        }

        // Emotes and expressions, by name.
        foreach (var emote in emotes.Where(e => e.Name.Length > 0 && e.Command.Length > 0 && e.Timelines.Length > 0)
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var main = emote.Timelines[0];
            if (!timelines.TryGetValue(main, out var mainRow))
                continue;
            if (AnimationCatalog.IsExpressionKey(mainRow.LoadType, mainRow.Key))
            {
                Add(AnimationLibraryGroup.Expressions, main, emote.Name, emote.Command, true);
                continue;
            }
            Add(AnimationLibraryGroup.Emotes, main, emote.Name, emote.Command);
            var seen = new HashSet<ushort> { main };
            for (var i = 1; i < Math.Min(emote.Timelines.Length, Versions.Length); i++)
                if (Versions[i] is { } version && emote.Timelines[i] is var id and > 0 && seen.Add(id))
                    Add(AnimationLibraryGroup.Emotes, id, $"{emote.Name} ({version})", emote.Command);
        }

        // The idles and the sitting and lying poses, as /changepose numbers them.
        foreach (var (group, name, poseName, baseKey, pose) in Families)
        {
            var first = group == AnimationLibraryGroup.Idles
                ? timelines.Values.Where(row => row.Key == baseKey).Select(row => row.Id).DefaultIfEmpty().Min()
                : emotes.Select(e => e.Timelines.FirstOrDefault()).FirstOrDefault(id => timelines.TryGetValue(id, out var row) && row.Key == baseKey);
            if (first > 0)
                Add(group, first, name, timelines[first].Key);
            var poses = new SortedDictionary<int, ushort>();
            foreach (var emote in emotes)
                if (emote.Timelines.FirstOrDefault() is var id and > 0 && timelines.TryGetValue(id, out var row) &&
                    pose.Match(row.Key) is { Success: true } match && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var number))
                    poses.TryAdd(number, id);
            foreach (var (number, id) in poses)
                Add(group, id, $"{poseName} {number}", timelines[id].Key);
        }

        // Walking, running, sprinting, jumping and turning, each its own track.
        foreach (var (id, name) in AnimationCatalog.Movement.OrderBy(pair => pair.Key))
            Add(AnimationLibraryGroup.Movement, id, name, timelines.TryGetValue(id, out var row) ? row.Key : string.Empty);

        // Every other timeline, by its key.
        foreach (var row in timelines.Values.OrderBy(row => row.Key, StringComparer.Ordinal).ThenBy(row => row.Id))
            if (!listed.Contains(row.Id))
                items.Add(new AnimationLibraryItem(AnimationLibraryGroup.Other, row.Id, KeyName(row.Key),
                    $"#{row.Id.ToString(CultureInfo.InvariantCulture)} · {row.Key}", row.Key,
                    AnimationCatalog.IsExpressionKey(row.LoadType, row.Key)));
        return [.. items.OrderBy(item => item.Group)];
    }

    /// <summary>A timeline key as a name: its last part with spaces, "event_base/event_base_chair_talk" as "Chair talk".</summary>
    internal static string KeyName(string key)
    {
        var slash = key.LastIndexOf('/');
        var name = slash >= 0 ? key[(slash + 1)..] : key;
        var folder = slash > 0 ? key[..slash] : string.Empty;
        var lastFolder = folder[(folder.LastIndexOf('/') + 1)..];
        if (lastFolder.Length > 0 && name.StartsWith(lastFolder + "_", StringComparison.Ordinal) && name.Length > lastFolder.Length + 1)
            name = name[(lastFolder.Length + 1)..];
        name = name.Replace('_', ' ').Trim();
        return name.Length == 0 ? key : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Whether <paramref name="item"/> matches every word of <paramref name="search"/> in its name or detail.</summary>
    public static bool Matches(AnimationLibraryItem item, string search)
    {
        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!item.Name.Contains(word, StringComparison.OrdinalIgnoreCase) && !item.Detail.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !item.Key.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
