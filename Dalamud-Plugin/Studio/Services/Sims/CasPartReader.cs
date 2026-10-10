using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>A resource key from a CASP key table.</summary>
public readonly struct SimsResourceKey : IEquatable<SimsResourceKey>
{
    public readonly uint  Type;
    public readonly uint  Group;
    public readonly ulong Instance;

    public SimsResourceKey(uint type, uint group, ulong instance)
    {
        Type     = type;
        Group    = group;
        Instance = instance;
    }

    public bool IsEmpty => Type == 0 && Group == 0 && Instance == 0;

    public bool Equals(SimsResourceKey other)
        => Type == other.Type && Group == other.Group && Instance == other.Instance;

    public override bool Equals(object? obj) => obj is SimsResourceKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Type, Group, Instance);
    public override string ToString() => $"{Type:X8}:{Group:X8}:{Instance:X16}";
}

/// <summary>What a CAS part uses one of its textures for.</summary>
public enum SimsTextureRole
{
    Unknown,
    Diffuse,
    Normal,
    Specular,
    Shadow,
    Emission,
    RegionMap,
}

/// <summary>One detail level of a CAS part, and the meshes that make it up.</summary>
public sealed class SimsCasLod
{
    /// <summary>0 is the highest detail; the game uses up to four levels.</summary>
    public int Level;

    /// <summary>GEOM keys for this level. Empty when the creator supplied no mesh for it.</summary>
    public List<SimsResourceKey> Meshes = new();
}

/// <summary>One CAS part: its name and the resources it references, by role.</summary>
public sealed class SimsCasPart
{
    public string Name = string.Empty;
    public uint   Version;

    public SimsResourceKey Diffuse;
    public SimsResourceKey Normal;
    public SimsResourceKey Specular;
    public SimsResourceKey Shadow;
    public SimsResourceKey RegionMap;

    /// <summary>GEOM keys this part uses, across every LOD.</summary>
    public List<SimsResourceKey> Meshes = new();

    /// <summary>
    /// The part's detail levels, when the LOD list could be located and validated.
    /// Empty otherwise, in which case the caller has no authoritative level to go on.
    /// </summary>
    public List<SimsCasLod> Lods = new();

    /// <summary>Every key in the trailing table, useful for diagnostics.</summary>
    public List<SimsResourceKey> KeyTable = new();

    public IEnumerable<(SimsResourceKey Key, SimsTextureRole Role)> TextureKeys()
    {
        if (!Diffuse.IsEmpty)   yield return (Diffuse,   SimsTextureRole.Diffuse);
        if (!Normal.IsEmpty)    yield return (Normal,    SimsTextureRole.Normal);
        if (!Specular.IsEmpty)  yield return (Specular,  SimsTextureRole.Specular);
        if (!Shadow.IsEmpty)    yield return (Shadow,    SimsTextureRole.Shadow);
        if (!RegionMap.IsEmpty) yield return (RegionMap, SimsTextureRole.RegionMap);
    }
}

/// <summary>
/// Reader for the CAS Part resource (type 0x034AEECB) — the only place in a package
/// that names a part and ties its textures to it.
///
/// CASP is by far the least stable format in a package: the field list has grown
/// across a great many game versions, and the block holding the texture indices sits
/// in the middle of it, behind a run of version-gated fields. Walking to it means
/// getting every one of those right, and getting any of them wrong yields a plausible
/// but wrong byte index rather than an error.
///
/// So this reader does not walk it. It reads only the three things whose position is
/// fixed in every version — the header, the name that follows it, and the key table
/// the header points at — and works out the roles from the key *types* instead, which
/// are unambiguous: the game stores a diffuse as RLE2, a specular as RLES, a normal as
/// a plain DDS and a region map under its own type. That gives the same answer as the
/// index block for every package tested, and keeps working when the middle of the
/// format shifts again.
///
/// The one thing types cannot separate is diffuse from shadow, since both are RLE2.
/// The field order puts diffuse first, so the first is taken as the diffuse — and the
/// role stays editable in the UI regardless.
///
/// The LOD list is the exception: nothing outside it says which mesh is which detail
/// level, and neither group IDs nor vertex counts are a usable substitute (a creator
/// may copy LOD 0 into every level, giving several meshes identical counts, and the
/// group IDs do not run in level order). It is found by search rather than by walking:
/// every offset ahead of the key table is tried as the start of a LOD list, and one is
/// accepted only if the whole run parses, every mesh key indexes a GEOM-typed entry,
/// and the levels ascend from zero. A misaligned guess essentially never satisfies all
/// of that at once — across 916 CAS parts in twenty packages this found exactly one
/// match every time, never zero and never two. If that ever stops holding, the match
/// is rejected rather than guessed at and <see cref="SimsCasPart.Lods"/> stays empty.
/// </summary>
public static class CasPartReader
{
    /// <summary>Version, key-table offset and preset count — the fields ahead of the name.</summary>
    private const int HeaderSize = 12;

    private const int MaxLods    = 8;
    private const int MaxLevel   = 7;
    private const int MaxAssets  = 16;
    private const int MaxLodKeys = 32;
    private const int AssetSize  = 12;

    public static bool TryParse(byte[] data, out SimsCasPart part, out string error)
    {
        part  = new SimsCasPart();
        error = string.Empty;

        try
        {
            Parse(data, part);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void Parse(byte[] data, SimsCasPart part)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        part.Version = br.ReadUInt32();
        if (part.Version < 0x10)
            throw new InvalidDataException($"CASP version 0x{part.Version:X2} is older than this reader supports.");

        // Offset of the key table, measured from the end of this field.
        uint tableOffset = br.ReadUInt32();
        long tablePosition = ms.Position + tableOffset;

        br.ReadUInt32();                        // preset count — always 0 in shipped content
        part.Name = ReadName(br);

        ReadKeyTable(br, ms, tablePosition, part);
        AssignRoles(part);
        LocateLods(data, (int)tablePosition, part);
    }

    /// <summary>
    /// Finds the LOD list by trying every offset in front of the key table and keeping
    /// the one that validates. Ambiguity is treated as failure: two candidates mean the
    /// checks are not discriminating enough on this resource, and a wrong level is worse
    /// than none.
    /// </summary>
    private static void LocateLods(byte[] data, int tableStart, SimsCasPart part)
    {
        List<SimsCasLod>? found = null;

        for (int at = HeaderSize; at < tableStart; at++)
        {
            if (!TryReadLods(data, at, tableStart, part.KeyTable, out var candidate))
                continue;

            if (found != null)
                return;                 // ambiguous — leave Lods empty
            found = candidate;
        }

        if (found != null)
            part.Lods = found;
    }

    /// <summary>
    /// Attempts to read a LOD list at <paramref name="at"/>. Returns false as soon as
    /// anything fails to line up, which is what makes the search safe to run over every
    /// offset.
    /// </summary>
    private static bool TryReadLods(byte[] data, int at, int tableStart,
        List<SimsResourceKey> keyTable, out List<SimsCasLod> lods)
    {
        lods = new List<SimsCasLod>();

        int p = at;
        int lodCount = data[p++];
        if (lodCount is < 1 or > MaxLods)
            return false;

        int meshKeys = 0;
        int previousLevel = -1;

        for (int i = 0; i < lodCount; i++)
        {
            if (p + 6 > tableStart)
                return false;

            int level = data[p++];
            p += 4;                                     // unused
            if (level > MaxLevel || level <= previousLevel)
                return false;
            previousLevel = level;

            int assetCount = data[p++];
            if (assetCount > MaxAssets)
                return false;
            p += assetCount * AssetSize;

            if (p + 1 > tableStart)
                return false;
            int keyCount = data[p++];
            // A level with no mesh is normal: creators often ship LOD 0 only.
            if (keyCount > MaxLodKeys || p + keyCount > tableStart)
                return false;

            var lod = new SimsCasLod { Level = level };
            for (int k = 0; k < keyCount; k++)
            {
                byte index = data[p++];
                if (index >= keyTable.Count || keyTable[index].Type != SimsResourceType.Geom)
                    return false;
                lod.Meshes.Add(keyTable[index]);
                meshKeys++;
            }
            lods.Add(lod);
        }

        // Levels must start at the highest detail, and the list has to reference at
        // least one mesh — otherwise a run of zero bytes would qualify.
        return lods[0].Level == 0 && meshKeys > 0;
    }

    private static void ReadKeyTable(BinaryReader br, MemoryStream ms, long position, SimsCasPart part)
    {
        if (position < 0 || position >= ms.Length)
            throw new InvalidDataException("CASP key table lies outside the resource.");

        ms.Position = position;
        int count = br.ReadByte();
        if (position + 1 + (long)count * 16 > ms.Length)
            throw new InvalidDataException("CASP key table runs past the end of the resource.");

        for (int i = 0; i < count; i++)
        {
            ulong instance = br.ReadUInt64();
            uint  group    = br.ReadUInt32();
            uint  type     = br.ReadUInt32();
            part.KeyTable.Add(new SimsResourceKey(type, group, instance));
        }
    }

    /// <summary>
    /// Sorts the key table by resource type. Empty slots are skipped: an unused
    /// texture reference is written as an all-zero key rather than omitted.
    /// </summary>
    private static void AssignRoles(SimsCasPart part)
    {
        bool haveDiffuse = false;

        foreach (var key in part.KeyTable)
        {
            if (key.IsEmpty)
                continue;

            switch (key.Type)
            {
                case SimsResourceType.Rle2:
                    // Diffuse comes before shadow in the part's own field order.
                    if (!haveDiffuse)
                    {
                        part.Diffuse = key;
                        haveDiffuse  = true;
                    }
                    else if (part.Shadow.IsEmpty)
                    {
                        part.Shadow = key;
                    }
                    break;

                case SimsResourceType.Rles:
                    if (part.Specular.IsEmpty) part.Specular = key;
                    break;

                case SimsResourceType.ImgDds:
                case SimsResourceType.ImgOverlay:
                    if (part.Normal.IsEmpty) part.Normal = key;
                    break;

                case SimsResourceType.RegionMap:
                    if (part.RegionMap.IsEmpty) part.RegionMap = key;
                    break;

                case SimsResourceType.Geom:
                    part.Meshes.Add(key);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the part name: a length prefix in 7-bit chunks giving the size in
    /// <em>bytes</em>, followed by big-endian UTF-16 — not the little-endian encoding
    /// the rest of the format uses.
    /// </summary>
    private static string ReadName(BinaryReader br)
    {
        int byteLength = Read7BitLength(br);
        if (byteLength <= 0)
            return string.Empty;
        if (byteLength > 4096)
            throw new InvalidDataException("CASP name length is implausible.");

        var bytes = br.ReadBytes(byteLength);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    private static int Read7BitLength(BinaryReader br)
    {
        int value = 0, shift = 0;
        while (shift < 28)
        {
            byte b = br.ReadByte();
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
        throw new InvalidDataException("CASP string length prefix is malformed.");
    }
}
