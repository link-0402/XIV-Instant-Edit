using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>One texture slot in an <see cref="MtrlInfo"/>: a resolved game path plus its raw flags bits.</summary>
public readonly struct MtrlTextureOffset
{
    public readonly string Path;
    public readonly ushort Flags;
    public MtrlTextureOffset(string path, ushort flags) { Path = path; Flags = flags; }

    /// <summary>The game requests DX11 textures with a "--" prefix on the file name.</summary>
    public bool Dx11 => (Flags & 0x8000) != 0;
}

/// <summary>One UV/color set entry: a resolved name plus its raw index/unknown bytes.</summary>
public readonly struct MtrlNamedSet
{
    public readonly string Name;
    public readonly byte Index;
    public readonly byte Unknown1;
    public MtrlNamedSet(string name, byte index, byte unknown1) { Name = name; Index = index; Unknown1 = unknown1; }
}

/// <summary>A shader key: which variant of a shader feature the material selects (e.g. Texture Mode = Compatibility).</summary>
public sealed class MtrlShaderKey
{
    public uint Id;
    public uint Value;
    public MtrlShaderKey Clone() => new() { Id = Id, Value = Value };
}

/// <summary>A shader constant: its id, and where its floats sit in the shader value block.</summary>
public sealed class MtrlConstant
{
    public uint Id;
    public ushort Offset;
    public ushort Size;
    public MtrlConstant Clone() => new() { Id = Id, Offset = Offset, Size = Size };
}

/// <summary>A texture sampler: which shader input (by id) reads which texture, with what sampling settings.</summary>
public sealed class MtrlSampler
{
    public uint Id;
    public uint Settings;
    public byte TextureIndex;
    public byte[] Padding = new byte[3];
    public MtrlSampler Clone() => new() { Id = Id, Settings = Settings, TextureIndex = TextureIndex, Padding = Padding.ToArray() };

    /// <summary>Samplers without a texture point at index 255.</summary>
    public bool HasTexture => TextureIndex != byte.MaxValue;
}

/// <summary>
/// Plain, source-agnostic .mtrl contents. The header, texture/UV/colour-set tables and strings
/// are always read. The rest — additional data, the colour/dye table, and the shader section
/// (keys, constants, samplers, constant values) — is parsed into the lists below when its sizes
/// add up (<see cref="ShaderDataParsed"/>); <see cref="Tail"/> always holds it raw as well, so a
/// file that does not parse cleanly can still be passed through untouched.
/// </summary>
public sealed class MtrlInfo
{
    public uint Version;
    public ushort FileSize;
    public ushort DataSetSize;
    public byte TextureCount;
    public byte UvSetCount;
    public byte ColorSetCount;
    public byte AdditionalDataSize;
    public MtrlTextureOffset[] TextureOffsets = Array.Empty<MtrlTextureOffset>();
    public MtrlNamedSet[] UvColorSets = Array.Empty<MtrlNamedSet>();
    public MtrlNamedSet[] ColorSets = Array.Empty<MtrlNamedSet>();
    public string ShaderPackageName = string.Empty;

    /// <summary>Everything from just after the strings block to EOF, as read.</summary>
    public byte[] Tail = Array.Empty<byte>();

    // ── Parsed tail (valid when ShaderDataParsed) ────────────────────────────

    public bool ShaderDataParsed;
    public byte[] AdditionalData = Array.Empty<byte>();

    /// <summary>The colour table and dye table, kept as raw bytes.</summary>
    public byte[] DataSet = Array.Empty<byte>();

    public ushort MaterialFlags1;
    public ushort MaterialFlags2;
    public List<MtrlShaderKey> ShaderKeys = new();
    public List<MtrlConstant>  Constants  = new();
    public List<MtrlSampler>   Samplers   = new();

    /// <summary>The shader constant value block that <see cref="Constants"/> point into, raw.</summary>
    public byte[] ShaderValues = Array.Empty<byte>();

    /// <summary>
    /// An Endwalker colour table (16 rows of 32 bytes, with or without its 32-byte dye table) on a
    /// character.shpk material; Dawntrail doubled both, and the game no longer reads the old one.
    /// </summary>
    public bool LegacyColorTable => ShaderPackageName.Equals("character.shpk", StringComparison.OrdinalIgnoreCase) && DataSetSize is 512 or 544;

    /// <summary>A deep copy, so a shared template can be edited per material without touching the original.</summary>
    public MtrlInfo Clone() => new()
    {
        Version            = Version,
        FileSize           = FileSize,
        DataSetSize        = DataSetSize,
        TextureCount       = TextureCount,
        UvSetCount         = UvSetCount,
        ColorSetCount      = ColorSetCount,
        AdditionalDataSize = AdditionalDataSize,
        TextureOffsets     = TextureOffsets.ToArray(),
        UvColorSets        = UvColorSets.ToArray(),
        ColorSets          = ColorSets.ToArray(),
        ShaderPackageName  = ShaderPackageName,
        Tail               = Tail.ToArray(),
        ShaderDataParsed   = ShaderDataParsed,
        AdditionalData     = AdditionalData.ToArray(),
        DataSet            = DataSet.ToArray(),
        MaterialFlags1     = MaterialFlags1,
        MaterialFlags2     = MaterialFlags2,
        ShaderKeys         = ShaderKeys.Select(k => k.Clone()).ToList(),
        Constants          = Constants.Select(c => c.Clone()).ToList(),
        Samplers           = Samplers.Select(s => s.Clone()).ToList(),
        ShaderValues       = ShaderValues.ToArray(),
    };
}

/// <summary>
/// Reads raw .mtrl bytes into an <see cref="MtrlInfo"/> — from the live game (via
/// <see cref="GameDataService.GetVanillaFileBytes"/>) or from a bundled preset file on disk.
/// Layout after the strings block follows xivModdingFramework's Mtrl reader: additional data,
/// the data set (colour + dye tables), then a 12-byte shader header (value block size, key /
/// constant / sampler counts, two flag words), the keys (id, value), constants (id, offset,
/// size), samplers (id, settings, texture index, 3 padding bytes) and the value block.
/// </summary>
public static class MtrlReader
{
    private const int HeaderSize = 16;

    public const uint Signature = 0x01030000;

    public static bool LooksLikeMtrl(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data) == Signature;

    public static MtrlInfo Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        var info = new MtrlInfo();
        info.Version = br.ReadUInt32();

        uint packedFileSize = br.ReadUInt32();
        info.FileSize = (ushort)packedFileSize;
        info.DataSetSize = (ushort)(packedFileSize >> 16);

        ushort stringTableSize = br.ReadUInt16();
        ushort shaderPackageNameOffset = br.ReadUInt16();
        info.TextureCount = br.ReadByte();
        info.UvSetCount = br.ReadByte();
        info.ColorSetCount = br.ReadByte();
        info.AdditionalDataSize = br.ReadByte();

        var rawTextureOffsets = new uint[info.TextureCount];
        for (int i = 0; i < rawTextureOffsets.Length; i++)
            rawTextureOffsets[i] = br.ReadUInt32();

        var rawUvSets = new (ushort NameOffset, byte Index, byte Unknown1)[info.UvSetCount];
        for (int i = 0; i < rawUvSets.Length; i++)
            rawUvSets[i] = (br.ReadUInt16(), br.ReadByte(), br.ReadByte());

        var rawColorSets = new (ushort NameOffset, byte Index, byte Unknown1)[info.ColorSetCount];
        for (int i = 0; i < rawColorSets.Length; i++)
            rawColorSets[i] = (br.ReadUInt16(), br.ReadByte(), br.ReadByte());

        var strings = br.ReadBytes(stringTableSize);

        info.TextureOffsets = new MtrlTextureOffset[rawTextureOffsets.Length];
        for (int i = 0; i < rawTextureOffsets.Length; i++)
        {
            ushort offset = (ushort)rawTextureOffsets[i];
            ushort flags = (ushort)(rawTextureOffsets[i] >> 16);
            info.TextureOffsets[i] = new MtrlTextureOffset(ReadCString(strings, offset), flags);
        }

        info.UvColorSets = new MtrlNamedSet[rawUvSets.Length];
        for (int i = 0; i < rawUvSets.Length; i++)
            info.UvColorSets[i] = new MtrlNamedSet(ReadCString(strings, rawUvSets[i].NameOffset), rawUvSets[i].Index, rawUvSets[i].Unknown1);

        info.ColorSets = new MtrlNamedSet[rawColorSets.Length];
        for (int i = 0; i < rawColorSets.Length; i++)
            info.ColorSets[i] = new MtrlNamedSet(ReadCString(strings, rawColorSets[i].NameOffset), rawColorSets[i].Index, rawColorSets[i].Unknown1);

        info.ShaderPackageName = ReadCString(strings, shaderPackageNameOffset);

        int tailStart = HeaderSize + info.TextureCount * 4 + info.UvSetCount * 4 + info.ColorSetCount * 4 + stringTableSize;
        info.Tail = tailStart < data.Length ? data[tailStart..] : Array.Empty<byte>();

        TryParseTail(info, data, tailStart);
        return info;
    }

    /// <summary>Parses the tail into its parts; leaves <see cref="MtrlInfo.ShaderDataParsed"/> false if the sizes do not add up.</summary>
    private static void TryParseTail(MtrlInfo info, byte[] data, int tailStart)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false) { Position = tailStart };
            using var br = new BinaryReader(ms);

            info.AdditionalData = br.ReadBytes(info.AdditionalDataSize);
            info.DataSet        = br.ReadBytes(info.DataSetSize);

            ushort valuesSize   = br.ReadUInt16();
            ushort keyCount     = br.ReadUInt16();
            ushort constCount   = br.ReadUInt16();
            ushort samplerCount = br.ReadUInt16();
            info.MaterialFlags1 = br.ReadUInt16();
            info.MaterialFlags2 = br.ReadUInt16();

            info.ShaderKeys = new List<MtrlShaderKey>(keyCount);
            for (int i = 0; i < keyCount; i++)
                info.ShaderKeys.Add(new MtrlShaderKey { Id = br.ReadUInt32(), Value = br.ReadUInt32() });

            info.Constants = new List<MtrlConstant>(constCount);
            for (int i = 0; i < constCount; i++)
                info.Constants.Add(new MtrlConstant { Id = br.ReadUInt32(), Offset = br.ReadUInt16(), Size = br.ReadUInt16() });

            info.Samplers = new List<MtrlSampler>(samplerCount);
            for (int i = 0; i < samplerCount; i++)
                info.Samplers.Add(new MtrlSampler { Id = br.ReadUInt32(), Settings = br.ReadUInt32(), TextureIndex = br.ReadByte(), Padding = br.ReadBytes(3) });

            info.ShaderValues = br.ReadBytes(valuesSize);

            // Everything must be accounted for — anything left over means the layout was not what we expect.
            info.ShaderDataParsed = info.ShaderValues.Length == valuesSize && ms.Position == data.Length
                                    && info.Constants.All(c => c.Offset + c.Size <= valuesSize);
        }
        catch (EndOfStreamException)
        {
            info.ShaderDataParsed = false;
        }
    }

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
}
