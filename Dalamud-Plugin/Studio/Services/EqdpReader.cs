using System;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Reads one race's Equipment Deformer Parameter table: two bits per equipment slot and set id
/// saying whether that race has a material, and a model, of its own for the item. A race whose
/// model bit is unset wears another race's model; a race whose material bit is unset looks the
/// material up under the race it falls back to, which is why a port that writes a race's own files
/// has to set the matching bits (see <see cref="ModEqdpOverride"/>).
///
/// Layout as in Penumbra's ExpandedEqdpFile: identifier, block size and block count, then one
/// 16-bit header per block holding that block's offset in entries (0xFFFF = the block is not in the
/// file and reads as zero), then the blocks themselves, 16 bits per set id.
/// </summary>
internal sealed class EqdpReader
{
    private const int IdentifierSize = 2;
    private const int PreambleSize = 4;
    private const ushort CollapsedBlock = ushort.MaxValue;
    private const int EntrySize = 2;

    private readonly byte[] _data;
    private readonly int    _blockSize;
    private readonly int    _blockCount;

    private EqdpReader(byte[] data)
    {
        _data       = data;
        _blockSize  = BitConverter.ToUInt16(data, IdentifierSize);
        _blockCount = BitConverter.ToUInt16(data, IdentifierSize + 2);
    }

    /// <summary>Where a race's table lives; equipment and accessories have one file each.</summary>
    public static string PathFor(RaceGender race, bool accessory)
        => $"chara/xls/charadb/{(accessory ? "accessorydeformerparameter" : "equipmentdeformerparameter")}/c{race.RaceCode}.eqdp";

    public static EqdpReader? Load(GameDataService gameData, RaceGender race, bool accessory)
        => gameData.GetVanillaFileBytes(PathFor(race, accessory)) is { } bytes ? Parse(bytes) : null;

    /// <summary>A table from its bytes (the game's file, or one a mod ships), or null when it is not one.</summary>
    public static EqdpReader? Parse(byte[] bytes)
    {
        if (bytes is not { Length: > IdentifierSize + PreambleSize })
            return null;

        var reader = new EqdpReader(bytes);
        return reader._blockSize > 0 && reader._blockCount > 0 ? reader : null;
    }

    /// <summary>The race's entry for a set id: the raw bits for every slot, or 0 when the game has none.</summary>
    public ushort Entry(ushort setId)
    {
        int block = setId / _blockSize;
        if (block >= _blockCount)
            return 0;

        int headerAt = IdentifierSize + PreambleSize + block * 2;
        if (headerAt + 2 > _data.Length)
            return 0;

        ushort start = BitConverter.ToUInt16(_data, headerAt);
        if (start == CollapsedBlock)
            return 0;

        // Block headers count entries from the end of all headers, not bytes from the file start.
        int at = IdentifierSize + PreambleSize + _blockCount * 2 + (start + setId % _blockSize) * EntrySize;
        return at + EntrySize <= _data.Length ? BitConverter.ToUInt16(_data, at) : (ushort)0;
    }
}
