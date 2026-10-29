using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Where a prop summon (C198, or C043) is held, changed in the editor: deltas against each race's vanilla attach
/// entry (see <see cref="AtchTable"/>) at <paramref name="State"/>.
/// </summary>
/// <param name="State">The attach state the place starts from: its vanilla entry is the base the deltas move.</param>
/// <param name="OwnState">"Only this animation": the place is written to attach state 2, which no other animation shows a prop on.</param>
/// <param name="Bone">The bone it attaches to; null keeps the base's.</param>
/// <param name="Move">Added to the base's offset, in metres.</param>
/// <param name="Turn">Added to the base's rotation, in radians.</param>
/// <param name="Size">The base's scale times this; 1 keeps it.</param>
internal sealed record TimelinePropPlace(int State, bool OwnState, string? Bone, Vector3 Move, Vector3 Turn, float Size)
{
    /// <summary>The attach state a save writes the place to and the TMB picks: 2 for only this animation, else <see cref="State"/>.</summary>
    public int Target => OwnState ? 2 : State;
}

/// <summary>
/// A Penumbra <c>Atch</c> manipulation: the entry one race shows prop attach point <paramref name="Code"/> with in
/// <paramref name="State"/>. With <paramref name="Remove"/> the mod's own one there goes instead (the entry is the game's).
/// </summary>
internal sealed record AtchManipulation(ushort GenderRace, string Code, int State, AtchEntry Entry, bool Remove = false);

/// <summary>
/// Turns prop places (<see cref="TimelineEvent.Place"/>) into what a save writes: an attach override per race the
/// file plays on, and the attach state the TMB picks for the prop.
/// </summary>
internal static class TimelinePropPlaces
{
    /// <summary>
    /// A C014/C174 ObjectPosition as an attach state, and an attach state as one: the game numbers Stowed (state 1)
    /// 0 and Drawn (state 0) 1; 2 to 5 are the same.
    /// </summary>
    public static int SwapStowedDrawn(int value) => value switch { 0 => 1, 1 => 0, _ => value };

    /// <summary>
    /// The attach state <paramref name="summon"/> shows now: its C198 AtchState less one when not Default; else the
    /// ObjectPosition of the first C014 or C174 of its group acting on its slot; else 1, Stowed (the game shows Toast's
    /// own emote, a C043 and a Default C198 with no control, in state 1).
    /// </summary>
    public static int CurrentState(TimelineDocument doc, TimelineEvent summon)
    {
        if (summon.Magic == "C198" && (int)summon.Values.GetValueOrDefault("AtchState") is > 0 and var atch)
            return atch - 1;
        if (summon.Group != null && Control(doc, summon) is { } control)
            return SwapStowedDrawn((int)control.Values.GetValueOrDefault("ObjectPosition"));
        return 1;
    }

    /// <summary>
    /// The attach overrides <paramref name="doc"/>'s prop places make, per race in <paramref name="genderRaces"/>:
    /// the race's vanilla entry at the place's <see cref="TimelinePropPlace.State"/>, moved by its deltas, at its
    /// <see cref="TimelinePropPlace.Target"/>. With <paramref name="loaded"/> only the places changed since it was loaded
    /// count, since a loaded place is the mod's override already (<see cref="FromOverrides"/>); one taken off counts as
    /// the game's entry at its target. One equal to the race's vanilla entry there removes the mod's override there
    /// (<paramref name="modOverrides"/>), else is left out.
    /// </summary>
    /// <param name="skipUnlearned">
    /// For the preview, which learns them: a prop whose attach point was never learned gets no override, rather than
    /// throwing; and one put back where the game has it writes the game's entry over the mod's rather than removing it.
    /// </param>
    /// <exception cref="InvalidDataException">A prop's attach point was never learned, or a race's table lacks it.</exception>
    public static ImmutableArray<AtchManipulation> Manipulations(TimelineDocument doc, IReadOnlyList<ushort> genderRaces,
        Func<ushort, AtchTable> tables, PropAttachTypes types, bool skipUnlearned = false, TimelineDocument? loaded = null,
        IReadOnlyCollection<AtchManipulation>? modOverrides = null)
    {
        var result = ImmutableArray.CreateBuilder<AtchManipulation>();
        foreach (var ev in doc.Events)
        {
            if (!IsSummon(ev))
                continue;
            var was = loaded?.Events.FirstOrDefault(e => e.Key == ev.Key)?.Place;
            if (loaded != null && Equals(ev.Place, was) || (ev.Place ?? was) is not { } place)
                continue;
            var model = (ushort)ev.Values.GetValueOrDefault("Model");
            if (types.CodeOf(model) is not { } code)
            {
                if (skipUnlearned)
                    continue;
                throw new InvalidDataException($"Preview once to place {model}");
            }
            foreach (var race in genderRaces)
            {
                var table = tables(race);
                var vanilla = table.Entry(code, place.Target);
                var from = ev.Place == null ? vanilla : table.Entry(code, place.State);
                if (from == null || vanilla == null)
                    throw new InvalidDataException($"c{race:D4} has no attach point {code} in state {(from == null ? place.State : place.Target)}.");
                var entry = ev.Place == null ? vanilla
                    : new AtchEntry(place.Bone ?? from.Bone, from.Scale * place.Size, from.Offset + place.Move, from.Rotation + place.Turn);
                // Back where the game has it: the mod's own override there goes, else there is nothing to write.
                var back = AtchTable.SameEntry(entry, vanilla);
                if (back && modOverrides?.Any(m => m.GenderRace == race && m.Code == code && m.State == place.Target) != true)
                    continue;
                var manipulation = new AtchManipulation(race, code, place.Target, entry, back && !skipUnlearned);
                // Two props of one kind share the race's attach point: they can't be placed apart.
                if (result.FirstOrDefault(m => m.GenderRace == race && m.Code == code && m.State == place.Target) is { } same)
                {
                    if (!AtchTable.SameEntry(same.Entry, entry))
                        throw new InvalidDataException("Two props of one kind need the same place.");
                    continue;
                }
                result.Add(manipulation);
            }
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// <paramref name="doc"/> with the attach state <paramref name="summon"/>'s place targets written into the TMB
    /// side, on it and the summons of its group without a place of their own: a C198 gets AtchState target + 1; a C043
    /// gets a C014 at its spawn + 1 on its slot (Enabled 1, ObjectPosition the target), in place of the group's C014 on
    /// that slot at its spawn or spawn + 1. A later C014 (a switch, a stow) is kept. The same document when the summon
    /// has no place.
    /// </summary>
    public static TimelineDocument ApplyState(TimelineDocument doc, TimelineEvent summon)
    {
        var current = doc.Events.FirstOrDefault(e => e.Key == summon.Key) ?? summon;
        if (current.Place is not { } place)
            return doc;
        var target = place.Target;
        var events = doc.Events.ToList();
        var next = Math.Max(0, doc.NextKey);
        var keys = doc.Events.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        // A summon of the group with a place of its own gets its own state from its own call.
        TimelineEvent[] summons = [.. (current.Group == null ? [current] : doc.Events.Where(e => e.Group == current.Group && (e.Key == current.Key || e.Place == null)))
            .Where(IsSummon)];
        foreach (var each in summons)
        {
            if (each.Magic == "C198")
            {
                var at = events.FindIndex(e => e.Key == each.Key);
                events[at] = events[at] with { Values = events[at].Values.SetItem("AtchState", target + 1) };
                continue;
            }
            const int slot = 2; // A C043 is always summon slot 0, which a C014 names 2.
            // Only the C014 that picks the state as the prop appears; one later switches it (or another summon's).
            if (each.Group != null && events.Where(e => e.Magic == "C014" && e.Group == each.Group && !added.Contains(e.Key) &&
                    e.Start >= each.Start && e.Start <= each.Start + 1 &&
                    (int)e.Values.GetValueOrDefault("ObjectControl") == slot).OrderBy(e => e.Start).LastOrDefault() is { } replaced)
                events.Remove(replaced);
            string key;
            do key = "n" + (next++).ToString(CultureInfo.InvariantCulture);
            while (!keys.Add(key));
            added.Add(key);
            events.Add(TimelineTemplates.Event("C014", Math.Min(each.Start + 1, TimelineDocument.MaxFrames), 0, "",
                new() { ["Enabled"] = 1, ["ObjectPosition"] = SwapStowedDrawn(target), ["ObjectControl"] = slot }, each.Group) with { Key = key });
        }
        return doc with { Events = [.. events], NextKey = next };
    }

    /// <summary>
    /// <paramref name="doc"/> with every prop place's attach state written into the TMB side (see <see cref="ApplyState"/>);
    /// with <paramref name="loaded"/> only the places changed since, as the file holds the others already.
    /// </summary>
    public static TimelineDocument ApplyStates(TimelineDocument doc, TimelineDocument? loaded = null)
    {
        foreach (var placed in doc.Events.Where(e => e.Place != null &&
                     (loaded == null || !Equals(e.Place, loaded.Events.FirstOrDefault(l => l.Key == e.Key)?.Place))).ToArray())
            doc = ApplyState(doc, placed);
        return doc;
    }

    /// <summary>
    /// <paramref name="doc"/>, as read from a mod's file, with the places the mod's attach overrides
    /// (<paramref name="overrides"/>) give its props, so the editor starts from where the mod has them. The first summon of
    /// each group's slot whose attach point is learned takes the first race in <paramref name="genderRaces"/> the mod
    /// overrides at the state the TMB shows: the bone, move, turn and size from the game's entry there to the mod's. State 2
    /// is "only this animation". A summon with a place, or the mod's entry equal to the game's, stays as it is.
    /// </summary>
    public static TimelineDocument FromOverrides(TimelineDocument doc, IReadOnlyList<ushort> genderRaces,
        IReadOnlyCollection<AtchManipulation> overrides, Func<ushort, AtchTable> tables, PropAttachTypes types)
    {
        var events = doc.Events;
        var heads = doc.Events.Where(e => e.Group != null && IsSummon(e)).GroupBy(e => (e.Group, Slot(e))).Select(g => g.MinBy(e => e.Start)!);
        foreach (var summon in heads)
        {
            if (summon.Place != null || types.CodeOf((ushort)summon.Values.GetValueOrDefault("Model")) is not { } code)
                continue;
            var state = CurrentState(doc, summon);
            foreach (var race in genderRaces)
            {
                if (overrides.FirstOrDefault(m => !m.Remove && m.GenderRace == race && m.Code == code && m.State == state) is not { } moved)
                    continue;
                if (tables(race).Entry(code, state) is { } vanilla && !AtchTable.SameEntry(moved.Entry, vanilla))
                {
                    var place = new TimelinePropPlace(state, state == 2, moved.Entry.Bone == vanilla.Bone ? null : moved.Entry.Bone,
                        moved.Entry.Offset - vanilla.Offset, moved.Entry.Rotation - vanilla.Rotation,
                        vanilla.Scale == 0 ? 1 : moved.Entry.Scale / vanilla.Scale);
                    events = events.Replace(summon, summon with { Place = place });
                }
                break;
            }
        }
        return events == doc.Events ? doc : doc with { Events = events };
    }

    /// <summary>Each prop model <paramref name="doc"/> summons (C198 or C043) with the frame it first appears.</summary>
    public static IReadOnlyDictionary<ushort, int> PropStarts(TimelineDocument doc)
    {
        var starts = new Dictionary<ushort, int>();
        foreach (var e in doc.Events.Where(IsSummon))
        {
            var model = (ushort)e.Values.GetValueOrDefault("Model");
            starts[model] = starts.TryGetValue(model, out var start) ? Math.Min(start, e.Start) : e.Start;
        }
        return starts;
    }

    private static bool IsSummon(TimelineEvent e) => e.Magic is "C198" or "C043" && e.Values.GetValueOrDefault("Model") > 0;

    /// <summary>A summon's slot: a C198's SummonId; a C043 is always slot 0.</summary>
    private static int Slot(TimelineEvent e) => e.Magic == "C198" ? (int)e.Values.GetValueOrDefault("SummonId") : 0;

    /// <summary>The first C014 or C174 of <paramref name="summon"/>'s group acting on its slot (2 for summon 0, 3 for summon 1).</summary>
    private static TimelineEvent? Control(TimelineDocument doc, TimelineEvent summon)
    {
        var slot = 2 + (summon.Magic == "C198" ? (int)summon.Values.GetValueOrDefault("SummonId") : 0);
        return doc.Events.Where(e => e.Magic is "C014" or "C174" && e.Group == summon.Group && (int)e.Values.GetValueOrDefault("ObjectControl") == slot)
            .OrderBy(e => e.Start).FirstOrDefault();
    }
}
