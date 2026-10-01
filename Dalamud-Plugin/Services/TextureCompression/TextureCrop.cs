using System.Buffers.Binary;
using System.Numerics;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// A power-of-two part of UV space aligned to its own size: 1 / 2^<see cref="ShiftU"/> of the width
/// starting at tile <see cref="TileU"/>, and likewise down. Moving UVs into it subtracts a multiple
/// of its size and doubles <see cref="ShiftU"/> times, which floats and halves both do exactly.
/// </summary>
internal readonly record struct UvWindow(int ShiftU, int ShiftV, int TileU, int TileV)
{
    /// <summary> The share of the texture the window keeps. </summary>
    public double Fraction => 1.0 / (1L << (ShiftU + ShiftV));

    public float MoveU(float u) => Move(u, ShiftU, TileU);
    public float MoveV(float v) => Move(v, ShiftV, TileV);

    private static float Move(float value, int shift, int tile)
    {
        var scale = (float)(1 << shift);
        var origin = tile / scale;
        var moved = (value - origin) * scale;
        if (moved / scale + origin != value)
            throw new InvalidDataException("A UV can't be moved exactly.");
        return moved;
    }

    public override string ToString()
        => $"{1 << ShiftU} × {1 << ShiftV} grid, column {TileU + 1}, row {TileV + 1}";
}

/// <summary>
/// Cuts a texture down to a <see cref="UvWindow"/>: each mip level keeps exactly the pixels, or 4 × 4
/// blocks of a compressed format, it held inside the window, copied without decoding, so nothing is
/// re-encoded. Levels too small to cut along block edges are left off the end of the chain, where
/// the texture is a few pixels on screen. Dalamud-free.
/// </summary>
internal static class TextureCrop
{
    /// <summary> No side of a cut texture gets smaller than this. </summary>
    public const int MinSize = 16;

    /// <summary> How many times a texture side can be halved for a window: it must divide evenly, stay at least <see cref="MinSize"/> and, compressed, whole blocks. </summary>
    public static int MaxShift(int size, bool blocks)
    {
        var shift = 0;
        while (shift < 12 && size % (1 << (shift + 1)) == 0 && size >> (shift + 1) >= MinSize && (!blocks || (size >> (shift + 1)) % 4 == 0))
            shift++;
        return shift;
    }

    /// <summary>
    /// The smallest window holding every UV between <paramref name="min"/> and <paramref name="max"/>,
    /// halving each side at most so often; null when a UV lies outside 0..1, where it wraps around the texture.
    /// </summary>
    public static UvWindow? Fit(Vector2 min, Vector2 max, int maxShiftU, int maxShiftV)
    {
        if (!(min.X >= 0 && min.Y >= 0 && max.X <= 1 && max.Y <= 1 && min.X <= max.X && min.Y <= max.Y))
            return null;
        var (shiftU, tileU) = Axis(min.X, max.X, maxShiftU);
        var (shiftV, tileV) = Axis(min.Y, max.Y, maxShiftV);
        return new UvWindow(shiftU, shiftV, tileU, tileV);
    }

    private static (int Shift, int Tile) Axis(float low, float high, int maxShift)
    {
        for (var shift = maxShift; shift > 0; shift--)
        {
            var scale = 1 << shift;
            var tile = Math.Min(scale - 1, (int)Math.Floor(low * (double)scale));
            if (high <= (tile + 1) / (double)scale)
                return (shift, tile);
        }
        return (0, 0);
    }

    /// <summary> The cut texture. <paramref name="tex"/> is a 2D TEX whose mip offsets describe its data (see <see cref="TextureFiles.NormalizeMipOffsets"/>). </summary>
    public static byte[] Crop(byte[] tex, UvWindow window)
    {
        var info = TextureCost.Read(tex) ?? throw new InvalidDataException("The texture's header can't be read.");
        if (!info.Is2D)
            throw new NotSupportedException("Only ordinary 2D textures can be cut.");
        var bits = TextureCost.BitsPerPixel(info.Format) ?? throw new NotSupportedException($"Unknown texture format 0x{info.Format:X4}.");
        var blocks = TextureCost.IsBlockCompressed(info.Format);
        if (window.ShiftU > MaxShift(info.Width, blocks) || window.ShiftV > MaxShift(info.Height, blocks))
            throw new InvalidDataException("The window doesn't divide the texture evenly.");
        int width = info.Width >> window.ShiftU, height = info.Height >> window.ShiftV;
        int left = window.TileU * width, top = window.TileV * height;

        var levels = new List<byte[]>();
        for (var mip = 0; mip < info.Mips; mip++)
        {
            // The window must land on whole pixels of this level, and on block edges when compressed.
            if (window.ShiftU > 0 && width % (1 << mip) != 0 || window.ShiftV > 0 && height % (1 << mip) != 0)
                break;
            int x = left >> mip, y = top >> mip;
            if (blocks && (x % 4 != 0 || y % 4 != 0))
                break;
            int sourceWidth = Math.Max(1, info.Width >> mip), sourceHeight = Math.Max(1, info.Height >> mip);
            int levelWidth = Math.Max(1, width >> mip), levelHeight = Math.Max(1, height >> mip);
            var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28 + mip * 4));
            if (offset < TextureCost.HeaderSize || offset + TextureCost.Vram(info.Format, sourceWidth, sourceHeight, 1) > tex.Length)
                throw new InvalidDataException("The texture's mip data is incomplete.");
            // Rows of pixels, or of 4 × 4 blocks, and the bytes of one pixel or block.
            int unitBytes = blocks ? bits * 2 : bits / 8, unit = blocks ? 4 : 1;
            int sourceUnits = (sourceWidth + unit - 1) / unit, units = (levelWidth + unit - 1) / unit, rows = (levelHeight + unit - 1) / unit;
            int firstUnit = x / unit, firstRow = y / unit;
            var level = new byte[units * rows * unitBytes];
            for (var row = 0; row < rows; row++)
                tex.AsSpan((int)(offset + ((long)(firstRow + row) * sourceUnits + firstUnit) * unitBytes), units * unitBytes)
                    .CopyTo(level.AsSpan(row * units * unitBytes));
            levels.Add(level);
        }
        if (levels.Count == 0)
            throw new InvalidDataException("The window doesn't land on the texture's blocks.");

        var result = new byte[TextureCost.HeaderSize + levels.Sum(level => level.Length)];
        tex.AsSpan(0, TextureCost.HeaderSize).CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), (ushort)height);
        result[14] = (byte)((tex[14] & 0x80) | levels.Count);
        // LOD mip indices stay within the levels kept, as Penumbra's writer keeps them.
        for (var lod = 0; lod < 3; lod++)
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(16 + lod * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16 + lod * 4), Math.Min(index, (uint)levels.Count - 1));
        }
        result.AsSpan(28, 13 * 4).Clear();
        var at = TextureCost.HeaderSize;
        for (var mip = 0; mip < levels.Count; mip++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28 + mip * 4), (uint)at);
            levels[mip].CopyTo(result.AsSpan(at));
            at += levels[mip].Length;
        }
        return result;
    }
}
