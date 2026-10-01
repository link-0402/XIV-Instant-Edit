using System.Buffers;
using System.Buffers.Binary;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// Textures that hold one color everywhere, such as a fully white mask, a flat normal map or an
/// index map that picks one colorset row. Wherever the game samples such a texture it reads that
/// color, whatever the texture's size, so it shrinks to <see cref="Size"/> × <see cref="Size"/>
/// pixels before it is compressed. A survey of a 4,200-mod library on 2026-10-01 found 2,561 of
/// them larger than that, 16 GiB of files: 930 uncompressed ones (886 exactly one color, the rest
/// within 1 to 4 levels, like flat normal maps with a level of noise) and 1,631 block-compressed
/// ones that repeat one block, mostly index maps, masks and normal maps. Dalamud-free.
/// </summary>
internal static class SingleColor
{
    public const int Size = 32;

    /// <summary> How many levels a channel may vary across the texture for it to still count as one color. </summary>
    public const int Tolerance = 4;

    /// <summary>
    /// The check's limits for a shrunk texture: each pixel may move by the tolerance and the encoder's
    /// rounding, no more, and normals barely turn. Cut-outs, opacity and dye rows keep the usual limits.
    /// </summary>
    public static CompressionTolerances CheckTolerances { get; } = CompressionTolerances.Default with { LargeError = 8, MaxNormalAngleP99 = 5 };

    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Textures shrunk when they hold one color: ordinary 2D textures larger than the shrunk size, in
    /// an uncompressed color format or in BC1, BC3, BC5 or BC7, which Penumbra decodes to the colors
    /// the game samples. Single-channel and HDR formats stay as they are.
    /// </summary>
    public static bool Shrinkable(TexInfo info)
        => info.Is2D && (TextureCost.IsCompressibleColour(info.Format) || info.Format is TextureCost.Bc1 or TextureCost.Bc3 or TextureCost.Bc5 or TextureCost.Bc7) &&
           (long)info.Width * info.Height > Size * Size && Math.Max(info.Width, info.Height) <= TextureFiles.MaxDimension;

    /// <summary>
    /// Whether a texture's stored top level can hold one color, read from a TEX file only as far as
    /// needed. BGRA32 pixels may vary by <see cref="Tolerance"/> levels per channel; other formats must
    /// repeat one pixel or 4 × 4 block exactly, which encoders do for one color. Most textures are
    /// ruled out within their first row, so this is cheap enough to run on every texture a character
    /// wears. A top level the file can't hold doesn't count.
    /// </summary>
    public static bool MayHoldOneColor(Stream tex, TexInfo info)
    {
        if (!Shrinkable(info))
            return false;
        Span<byte> head = stackalloc byte[TextureCost.HeaderSize];
        tex.Position = 0;
        if (tex.ReadAtLeast(head, head.Length, false) < head.Length)
            return false;
        var size = TextureCost.Vram(info.Format, info.Width, info.Height, 1);
        // The top level where the header puts it, or right after the header as Penumbra reads it.
        long start = BinaryPrimitives.ReadUInt32LittleEndian(head[28..]);
        if (start < TextureCost.HeaderSize || start + size > tex.Length)
            start = TextureCost.HeaderSize;
        if (start + size > tex.Length)
            return false;
        tex.Position = start;

        var unit = info.Format switch
        {
            TextureCost.Bgra8 or TextureCost.Bgrx8 => 4,
            TextureCost.Bgra4 or TextureCost.Bgr5A1 => 2,
            TextureCost.Bc1 => 8,
            _ => 16,
        };
        var tolerant = info.Format == TextureCost.Bgra8;
        Span<byte> first = stackalloc byte[unit];
        Span<int> low = stackalloc int[4];
        Span<int> high = stackalloc int[4];
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var started = false;
            for (var left = size; left > 0;)
            {
                var count = (int)Math.Min(ChunkBytes, left);
                tex.ReadExactly(buffer, 0, count);
                left -= count;
                var chunk = buffer.AsSpan(0, count);
                if (!started)
                {
                    chunk[..unit].CopyTo(first);
                    for (var c = 0; c < 4 && tolerant; c++)
                        low[c] = high[c] = first[c];
                    started = true;
                }
                for (var i = 0; i < count; i += unit)
                {
                    var texel = chunk.Slice(i, unit);
                    if (!tolerant)
                    {
                        if (!texel.SequenceEqual(first))
                            return false;
                        continue;
                    }
                    for (var c = 0; c < 4; c++)
                    {
                        low[c] = Math.Min(low[c], texel[c]);
                        high[c] = Math.Max(high[c], texel[c]);
                        if (high[c] - low[c] > Tolerance)
                            return false;
                    }
                }
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The color a decoded top level (BGRA32) holds, each channel the rounded average, when every
    /// pixel is within <see cref="Tolerance"/> levels of every other in the channels that are kept;
    /// null when it holds more than one color.
    /// </summary>
    /// <param name="twoChannel">The texture becomes BC5, which keeps only red and green, so blue and alpha may vary.</param>
    public static (byte B, byte G, byte R, byte A)? ColorOf(ReadOnlySpan<byte> bgra, int width, int height, bool twoChannel)
    {
        var pixels = (long)width * height;
        if (width <= 0 || height <= 0 || bgra.Length < pixels * 4)
            throw new ArgumentException("The image doesn't match the texture's size.");
        Span<int> low = [255, 255, 255, 255];
        Span<int> high = [0, 0, 0, 0];
        Span<long> sums = [0, 0, 0, 0];
        // BGRA order: red and green are bytes 2 and 1.
        var judged = twoChannel ? 1 : 0;
        var last = twoChannel ? 2 : 3;
        for (long p = 0; p < pixels; p++)
        {
            var pixel = bgra.Slice((int)(p * 4), 4);
            for (var c = 0; c < 4; c++)
            {
                sums[c] += pixel[c];
                if (c < judged || c > last)
                    continue;
                low[c] = Math.Min(low[c], pixel[c]);
                high[c] = Math.Max(high[c], pixel[c]);
                if (high[c] - low[c] > Tolerance)
                    return null;
            }
        }
        Span<byte> average = stackalloc byte[4];
        for (var c = 0; c < 4; c++)
            average[c] = (byte)((sums[c] + pixels / 2) / pixels);
        return (average[0], average[1], average[2], average[3]);
    }

    /// <summary> A <see cref="Size"/> × <see cref="Size"/> BGRA32 TEX of one color, without mipmaps, for Penumbra to encode. </summary>
    public static byte[] Tex((byte B, byte G, byte R, byte A) color)
    {
        var bytes = new byte[TextureCost.HeaderSize + Size * Size * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), TextureCost.Bgra8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), Size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), Size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), TextureCost.HeaderSize);
        for (var i = TextureCost.HeaderSize; i < bytes.Length; i += 4)
        {
            bytes[i] = color.B;
            bytes[i + 1] = color.G;
            bytes[i + 2] = color.R;
            bytes[i + 3] = color.A;
        }
        return bytes;
    }

    /// <summary>
    /// A small decoded image (BGRA32) stretched to the original's size, each pixel taking the nearest
    /// one, so the check compares what the game samples at every pixel of the original.
    /// </summary>
    public static byte[] Stretch(ReadOnlySpan<byte> small, int smallWidth, int smallHeight, int width, int height)
    {
        if (smallWidth <= 0 || smallHeight <= 0 || small.Length < (long)smallWidth * smallHeight * 4)
            throw new ArgumentException("The image doesn't match its size.");
        var stretched = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = (int)((long)y * smallHeight / height) * smallWidth;
            for (var x = 0; x < width; x++)
                small.Slice((row + (int)((long)x * smallWidth / width)) * 4, 4).CopyTo(stretched.AsSpan((y * width + x) * 4, 4));
        }
        return stretched;
    }
}
