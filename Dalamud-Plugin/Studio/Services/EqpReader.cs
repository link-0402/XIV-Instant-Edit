using System;
using System.Numerics;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Reads the game's Equipment Parameter table: one 64-bit entry per equipment set id, saying what
/// a piece hides or shows on the rest of the character. Layout as in Penumbra's
/// ExpandedEqpGmpBase: a 64-bit control block says which of the 64 blocks of 160 entries are
/// present, and absent blocks read as zero. The control block occupies entry 0, so ids 0 and 1
/// both read entry 1.
/// </summary>
internal sealed class EqpReader
{
    public const string GamePath = "chara/xls/equipmentparameter/equipmentparameter.eqp";

    private const int BlockSize = 160;
    private const int NumBlocks = 64;
    private const int EntrySize = 8;

    private readonly byte[] _data;
    private readonly ulong  _control;

    public EqpReader(byte[] data)
    {
        _data    = data;
        _control = BitConverter.ToUInt64(data, 0);
    }

    /// <summary>Reads and parses the table, or returns null when the file is missing or too short.</summary>
    public static EqpReader? Load(GameDataService gameData)
    {
        var bytes = gameData.GetVanillaFileBytes(GamePath);
        return bytes is { Length: >= EntrySize } ? new EqpReader(bytes) : null;
    }

    /// <summary>The vanilla entry for a set id, or 0 when its block is not in the file.</summary>
    public ulong Entry(ushort setId)
    {
        int id = setId <= 1 ? 1 : setId;
        if (id >= BlockSize * NumBlocks)
            return 0;

        int block = id / BlockSize;
        if ((_control & (1ul << block)) == 0)
            return 0;

        // Only blocks whose bit is set are stored, in order.
        int blocksBefore = BitOperations.PopCount(_control & ((1ul << block) - 1));
        int offset = (blocksBefore * BlockSize + id % BlockSize) * EntrySize;
        return offset + EntrySize <= _data.Length ? BitConverter.ToUInt64(_data, offset) : 0;
    }
}
