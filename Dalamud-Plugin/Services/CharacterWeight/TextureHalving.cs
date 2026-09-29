using System.Buffers.Binary;

namespace InstantEdit.Services.CharacterWeight;

/// <summary>
/// Halving textures for the size cap: each output pixel averages a 2 × 2 block of the input, channel
/// by channel, as a box filter and mip generation do. Dalamud-free.
/// </summary>
internal static class TextureHalving
{
    /// <summary> Halves a four-byte-per-pixel image. The width and height must be even. </summary>
    public static byte[] Halve(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException($"A {width} × {height} image can't be halved evenly.");
        int halfWidth = width / 2, halfHeight = height / 2, stride = width * 4;
        var result = new byte[checked(halfWidth * halfHeight * 4)];
        var output = 0;
        for (var y = 0; y < halfHeight; y++)
        {
            var top = 2 * y * stride;
            var bottom = top + stride;
            for (var x = 0; x < halfWidth; x++)
            {
                int left = top + x * 8, below = bottom + x * 8;
                for (var channel = 0; channel < 4; channel++)
                    result[output++] = (byte)((pixels[left + channel] + pixels[left + 4 + channel] +
                                               pixels[below + channel] + pixels[below + 4 + channel] + 2) >> 2);
            }
        }
        return result;
    }

    /// <summary> The top level of an uncompressed BGRA32 TEX, which is what Penumbra decodes textures to. </summary>
    public static (byte[] Pixels, int Width, int Height) ReadBgra(byte[] tex)
    {
        var header = TextureFiles.ReadTex(tex);
        if (header.Format != TextureCost.Bgra8)
            throw new InvalidDataException("Penumbra did not return BGRA32 pixels.");
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28));
        return (tex.AsSpan(offset, checked(header.Width * header.Height * 4)).ToArray(), header.Width, header.Height);
    }

    /// <summary> An uncompressed 32-bit TGA of BGRA pixels with a top-left origin, for Penumbra to convert. </summary>
    public static byte[] Tga(byte[] bgra, int width, int height)
    {
        if (bgra.Length != checked(width * height * 4))
            throw new ArgumentException("The pixel buffer does not match the image size.");
        var bytes = new byte[checked(18 + bgra.Length)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)height);
        bytes[16] = 32;
        bytes[17] = 0x28;
        bgra.CopyTo(bytes, 18);
        return bytes;
    }
}
