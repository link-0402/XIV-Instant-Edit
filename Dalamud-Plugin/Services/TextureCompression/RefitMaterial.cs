using System.Buffers.Binary;
using InstantEdit.Services.Materials;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// A gear material's colour table kept in step with a UV refit. Gear shaders sample tile patterns at
/// the first UV set times each row's tile matrix (UU, UV, VU, VV), so once the UVs are scaled by 2^shift
/// into a window, the matrix's U column is divided by 2^ShiftU and its V column by 2^ShiftV. The
/// window's offset can't be carried by a 2 × 2 matrix: the pattern keeps its scale but may slide by
/// part of a tile. Only those halves change; the rest of the file stays byte for byte. Dalamud-free.
/// </summary>
internal static class RefitMaterial
{
    /// <summary> Why the material's tile matrices can't be scaled, in words for the card; null when they can. </summary>
    public static string? Problem(byte[] mtrl)
    {
        try
        {
            Table(mtrl);
            return null;
        }
        catch (InvalidDataException e)
        {
            return e.Message;
        }
    }

    /// <summary> A copy with every row's tile matrix scaled for <paramref name="window"/>; throws when a value can't be stored exactly. </summary>
    public static byte[] WithWindow(byte[] mtrl, UvWindow window)
    {
        var (at, rows, rowSize, matrix) = Table(mtrl);
        var result = (byte[])mtrl.Clone();
        for (var row = 0; row < rows; row++)
        {
            var span = result.AsSpan(at + row * rowSize, rowSize);
            // UU and VU multiply u, UV and VV multiply v (see TileTransform).
            Scale(span, matrix, window.ShiftU);
            Scale(span, matrix + 1, window.ShiftV);
            Scale(span, matrix + 2, window.ShiftU);
            Scale(span, matrix + 3, window.ShiftV);
        }
        return result;
    }

    private static void Scale(Span<byte> row, int half, int shift)
    {
        var value = HalfPacking.Get(row, half);
        var scale = (float)(1 << shift);
        var scaled = (Half)(value / scale);
        if ((float)scaled * scale != value)
            throw new InvalidDataException("A tile pattern's scale can't be stored exactly.");
        BinaryPrimitives.WriteInt16LittleEndian(row[(half * 2)..], BitConverter.HalfToInt16Bits(scaled));
    }

    /// <summary> Where the colour rows start, how many there are, their size and the tile matrix's first half. </summary>
    private static (int At, int Rows, int RowSize, int Matrix) Table(byte[] mtrl)
    {
        if (mtrl.Length < 16)
            throw new InvalidDataException("the material is truncated");
        int dataSetSize = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(6)), stringSize = BinaryPrimitives.ReadUInt16LittleEndian(mtrl.AsSpan(8));
        var additionalAt = 16 + 4 * (mtrl[12] + mtrl[13] + mtrl[14]) + stringSize;
        var at = additionalAt + mtrl[15];
        if (at + dataSetSize > mtrl.Length)
            throw new InvalidDataException("the material is truncated");
        var flags = mtrl[15] >= 4 ? new TableFlags(BinaryPrimitives.ReadUInt32LittleEndian(mtrl.AsSpan(additionalAt))) : default;
        return flags.Kind switch
        {
            ColorTableKind.Dawntrail when dataSetSize >= ColorTable.DtRowCount * DtColorRow.Size => (at, ColorTable.DtRowCount, DtColorRow.Size, 28),
            ColorTableKind.Legacy when dataSetSize >= ColorTable.LegacyRowCount * LegacyColorRow.Size => (at, ColorTable.LegacyRowCount, LegacyColorRow.Size, 12),
            ColorTableKind.None => throw new InvalidDataException("it has no colour table, so its tile pattern can't be kept in scale"),
            _ => throw new InvalidDataException("its colour table has a layout whose tile pattern can't be kept in scale"),
        };
    }
}
