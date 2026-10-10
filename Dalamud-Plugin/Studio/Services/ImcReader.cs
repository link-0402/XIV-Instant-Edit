using System;
using System.Buffers.Binary;
using System.Numerics;

namespace InstantEdit.Studio.Services;

/// <summary>One IMC entry, split into the fields Penumbra's IMC manipulations use.</summary>
internal readonly record struct ImcEntry(byte MaterialId, byte DecalId, ushort AttributeMask, byte SoundId, byte VfxId, byte MaterialAnimationId);

/// <summary>An IMC file: a default row (variant 0), then one row per variant, each with one entry per part the file has.</summary>
internal sealed class ImcData
{
    private readonly byte[] _data;

    internal ImcData(byte[] data, int variantCount, ushort partMask)
    {
        _data        = data;
        VariantCount = variantCount;
        PartMask     = partMask;
        PartCount    = BitOperations.PopCount(partMask);
    }

    /// <summary>How many variant rows follow the default row.</summary>
    public int VariantCount { get; }
    public ushort PartMask { get; }
    public int PartCount { get; }

    /// <summary>A part's entry for a variant (0 = the default row), or null when the file has none.</summary>
    public ImcEntry? Entry(int part, int variant)
    {
        if (part < 0 || part >= PartCount || variant < 0 || variant > VariantCount)
            return null;
        var offset = 4 + (variant * PartCount + part) * 6;
        if (offset + 6 > _data.Length)
            return null;
        var packed = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 2));
        return new ImcEntry(_data[offset], _data[offset + 1], (ushort)(packed & 0x3FF), (byte)(packed >> 10), _data[offset + 4], _data[offset + 5]);
    }
}

/// <summary>Reads an item's IMC file from its bytes (the game's or a mod's own).</summary>
internal static class ImcReader
{
    public static ImcData? Parse(byte[] data)
    {
        if (data.Length < 4)
            return null;
        var imc = new ImcData(data, BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)));
        return imc.PartCount == 0 ? null : imc;
    }
}
