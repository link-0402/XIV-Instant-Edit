using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Reads an Extra Skeleton Table: which skeleton a race uses for a given hair (or face) id, which
/// is what makes hair physics work. Layout: a uint count, then that many (setId, raceId) pairs,
/// then that many skeleton ids, in the same order. The race id is the race code as a number,
/// so "0101" reads as 101.
/// </summary>
internal sealed class EstReader
{
    public const string HairPath = "chara/xls/charadb/hairskeletontemplate.est";
    public const string FacePath = "chara/xls/charadb/faceskeletontemplate.est";

    private readonly Dictionary<(ushort Race, ushort Set), ushort> _entries = new();

    public EstReader(byte[] data)
    {
        int count = BitConverter.ToInt32(data, 0);
        int descriptors = 4;
        int skeletons   = descriptors + count * 4;
        if (count < 0 || skeletons + count * 2 > data.Length)
            return;

        for (int i = 0; i < count; i++)
        {
            ushort set  = BitConverter.ToUInt16(data, descriptors + i * 4);
            ushort race = BitConverter.ToUInt16(data, descriptors + i * 4 + 2);
            _entries[(race, set)] = BitConverter.ToUInt16(data, skeletons + i * 2);
        }
    }

    public static EstReader? Load(GameDataService gameData, string gamePath)
    {
        var bytes = gameData.GetVanillaFileBytes(gamePath);
        return bytes is { Length: >= 4 } ? new EstReader(bytes) : null;
    }

    /// <summary>The skeleton id the game ships for this race and id, or null when it has no entry.</summary>
    public ushort? Entry(string raceCode, ushort setId)
        => ushort.TryParse(raceCode, out var race) && _entries.TryGetValue((race, setId), out var skeleton)
            ? skeleton
            : null;

    /// <summary>
    /// Every id on a race that has an entry, with the skeleton it uses, by id. The skeleton number
    /// rarely matches the id itself, so this is how a port finds the skeleton of another hairstyle.
    /// </summary>
    public IReadOnlyList<(ushort SetId, ushort SkeletonId)> EntriesFor(string raceCode)
    {
        if (!ushort.TryParse(raceCode, out var race))
            return Array.Empty<(ushort, ushort)>();

        var list = new List<(ushort SetId, ushort SkeletonId)>();
        foreach (var ((r, set), skeleton) in _entries)
            if (r == race)
                list.Add((set, skeleton));
        list.Sort((a, b) => a.SetId.CompareTo(b.SetId));
        return list;
    }

    /// <summary>The first id on a race that uses this skeleton, for finding the model it belongs to.</summary>
    public ushort? SetUsing(string raceCode, ushort skeletonId)
    {
        foreach (var (set, skeleton) in EntriesFor(raceCode))
            if (skeleton == skeletonId)
                return set;
        return null;
    }

    public int Count => _entries.Count;
}
