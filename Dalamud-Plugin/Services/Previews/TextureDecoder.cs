using System.Buffers.Binary;
using Lumina.Data.Files;

namespace InstantEdit.Services.Previews;

/// <summary> A decoded, preview-sized texture: BGRA pixels plus the source's header facts. </summary>
public sealed record DecodedTexture(byte[] Bgra, int Width, int Height, int SourceWidth, int SourceHeight, uint Format, int Mips)
{
    public bool Downscaled => Width != SourceWidth || Height != SourceHeight;
}

/// <summary> CPU decode of .tex files into preview-sized BGRA, reusing the material preview's loaders. </summary>
public static class TextureDecoder
{
    public const int MaxPreviewEdge = 512;
    private const int HeaderSize = 80;
    private const uint TextureTypeMask = 0x13C00000;
    private const uint TextureType2D = 0x00800000;

    /// <summary> Decodes mip 0 and box-filters it down until its longest edge fits <paramref name="maxEdge"/>. </summary>
    public static DecodedTexture Decode(byte[] bytes, int maxEdge = MaxPreviewEdge)
    {
        if (bytes.Length < HeaderSize || bytes.Length > TextureFiles.MaxBytes)
            throw new InvalidDataException("The texture file is empty, truncated or too large.");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes) & TextureTypeMask) != TextureType2D)
            throw new NotSupportedException("Only ordinary 2D textures can be previewed.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)) > 1 || bytes[15] > 1)
            throw new NotSupportedException("Texture arrays, cubes and volumes cannot be previewed.");
        RequireFirstMip(bytes);

        TexFile tex;
        byte[] bgra;
        try
        {
            tex = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<TexFile>(TextureFiles.NormalizeMipOffsets(bytes));
            bgra = tex.ImageData;
        }
        catch (Exception e) when (e is not InvalidDataException and not NotSupportedException)
        {
            throw new InvalidDataException($"The texture could not be decoded: {e.Message}", e);
        }

        int width = tex.Header.Width, height = tex.Header.Height;
        if (width is < 1 or > TextureFiles.MaxDimension || height is < 1 or > TextureFiles.MaxDimension)
            throw new InvalidDataException("The texture dimensions are outside the supported range.");
        if (bgra.Length != checked(width * height * 4))
            throw new InvalidDataException("The decoded texture has an unexpected size.");

        var (pixels, scaledWidth, scaledHeight) = Downscale(bgra, width, height, maxEdge);
        return new DecodedTexture(pixels, scaledWidth, scaledHeight, width, height, (uint)tex.Header.Format, Math.Max(1, tex.Header.MipCount));
    }

    /// <summary>
    /// Lumina pads a short mip with zeros instead of failing, so check here that the first mip
    /// level (the one previewed) is entirely present. Mirrors the size rules of
    /// <see cref="TextureFiles.NormalizeMipOffsets"/>.
    /// </summary>
    private static void RequireFirstMip(byte[] bytes)
    {
        var format = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        long width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        long height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        var bitsPerPixel = 1L << (int)((format >> 4) & 0xF);
        var blockCompressed = ((format >> 12) & 0xF) is 3 or 6;
        var size = blockCompressed
            ? Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * bitsPerPixel * 2
            : width * height * bitsPerPixel / 8;
        if (width < 1 || height < 1 || offset < HeaderSize || offset + size > bytes.Length)
            throw new InvalidDataException("The texture's first mip level is incomplete.");
    }

    /// <summary>
    /// Halves the image repeatedly (averaging each block, alpha included) until the longest edge is
    /// at most <paramref name="maxEdge"/>. Returns the input untouched when it already fits.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Downscale(byte[] bgra, int width, int height, int maxEdge)
    {
        if (width < 1 || height < 1 || bgra.Length < width * height * 4)
            throw new ArgumentException("The pixel buffer does not match the dimensions.", nameof(bgra));
        var factor = 1;
        while (Math.Max(width, height) / factor > Math.Max(1, maxEdge))
            factor <<= 1;
        if (factor == 1)
            return (bgra, width, height);

        var outWidth = Math.Max(1, width / factor);
        var outHeight = Math.Max(1, height / factor);
        var result = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var y0 = y * factor;
            var y1 = Math.Min(height, y0 + factor);
            for (var x = 0; x < outWidth; x++)
            {
                var x0 = x * factor;
                var x1 = Math.Min(width, x0 + factor);
                long b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * width * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = row + sx * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        a += bgra[i + 3];
                        count++;
                    }
                }

                var o = (y * outWidth + x) * 4;
                result[o] = (byte)(b / count);
                result[o + 1] = (byte)(g / count);
                result[o + 2] = (byte)(r / count);
                result[o + 3] = (byte)(a / count);
            }
        }

        return (result, outWidth, outHeight);
    }

    /// <summary> A copy with the alpha channel ignored, so the colour shows everywhere. </summary>
    public static byte[] Opaque(byte[] bgra)
    {
        var result = (byte[])bgra.Clone();
        for (var i = 3; i < result.Length; i += 4)
            result[i] = 255;
        return result;
    }

    /// <summary> The alpha channel as an opaque greyscale image. </summary>
    public static byte[] AlphaOnly(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            result[i] = alpha;
            result[i + 1] = alpha;
            result[i + 2] = alpha;
            result[i + 3] = 255;
        }
        return result;
    }

    /// <summary> "BC7", "BGRA32", or the raw code for formats the editor does not handle. </summary>
    public static string FormatLabel(uint format)
    {
        try
        {
            return TextureFiles.FormatName(format);
        }
        catch (NotSupportedException)
        {
            return $"0x{format:X4}";
        }
    }
}
