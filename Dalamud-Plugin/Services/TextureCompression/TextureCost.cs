using System.Buffers.Binary;

namespace InstantEdit.Services.TextureCompression;

/// <summary> What a TEX header says about a texture: format, size, mipmaps and layout. </summary>
internal readonly record struct TexInfo(uint Attributes, uint Format, int Width, int Height, int Depth, int Mips, int ArraySize)
{
    private const uint TypeMask = 0x13C00000;
    private const uint Type2D = 0x00800000;
    private const uint TypeCube = 0x02000000;
    private const uint Type3D = 0x01000000;

    /// <summary> An ordinary 2D texture: no array, cube or volume. Only these are changed. </summary>
    public bool Is2D => (Attributes & TypeMask) == Type2D && Depth <= 1 && ArraySize <= 1;

    public bool IsBlockCompressed => TextureCost.IsBlockCompressed(Format);

    /// <summary> Bytes the texture takes in video memory with its whole mip chain, or null for an unknown format. </summary>
    public long? Vram
    {
        get
        {
            if (TextureCost.BitsPerPixel(Format) is null)
                return null;
            var layers = Math.Max(1, ArraySize) * ((Attributes & TypeCube) != 0 ? 6 : 1);
            if ((Attributes & Type3D) == 0)
                return TextureCost.Vram(Format, Width, Height, Mips) * layers;
            long total = 0;
            for (var mip = 0; mip < Mips; mip++)
                total += TextureCost.Vram(Format, Math.Max(1, Width >> mip), Math.Max(1, Height >> mip), 1) * Math.Max(1, Depth >> mip);
            return total;
        }
    }
}

/// <summary>
/// Texture memory from TEX headers. FFXIV's format codes carry their size: bits 4 to 7 are log2 of
/// the bits per pixel, and families 3 and 6 are block compressed in 4 × 4 tiles. A texture's video
/// memory is its mip chain, which is what the file stores after its 80-byte header. Dalamud-free.
/// </summary>
internal static class TextureCost
{
    public const int HeaderSize = 80;
    public const uint Bc1 = 0x3420, Bc2 = 0x3430, Bc3 = 0x3431, Bc4 = 0x6120, Bc5 = 0x6230, Bc6H = 0x6330, Bc7 = 0x6432;
    public const uint Bgra8 = 0x1450, Bgrx8 = 0x1451, Bgra4 = 0x1440, Bgr5A1 = 0x1441;

    /// <summary> The header of a TEX file, or null when <paramref name="head"/> is too short or its size is invalid. </summary>
    public static TexInfo? Read(ReadOnlySpan<byte> head)
    {
        if (head.Length < HeaderSize)
            return null;
        var info = new TexInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(head),
            BinaryPrimitives.ReadUInt32LittleEndian(head[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(head[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(head[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(head[12..]),
            head[14] & 0x7F,
            head[15]);
        return info.Width > 0 && info.Height > 0 && info.Mips is > 0 and <= 13 ? info : null;
    }

    /// <summary> Bits per pixel of a known format, or null. </summary>
    public static int? BitsPerPixel(uint format)
    {
        var family = (format >> 12) & 0xF;
        var bits = 1 << (int)((format >> 4) & 0xF);
        return family is >= 1 and <= 6 && bits is >= 4 and <= 128 ? bits : null;
    }

    public static bool IsBlockCompressed(uint format) => ((format >> 12) & 0xF) is 3 or 6;

    /// <summary> Bytes of a 2D texture's first <paramref name="mips"/> levels. </summary>
    public static long Vram(uint format, int width, int height, int mips)
    {
        var bits = BitsPerPixel(format) ?? throw new NotSupportedException($"Unknown texture format 0x{format:X4}.");
        var block = IsBlockCompressed(format);
        long total = 0;
        for (var mip = 0; mip < mips; mip++)
        {
            long w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
            total += block ? (w + 3) / 4 * ((h + 3) / 4) * bits * 2 : w * h * bits / 8;
        }
        return total;
    }

    /// <summary> Colour formats that block compression makes smaller while keeping all four channels. </summary>
    public static bool IsCompressibleColour(uint format) => format is Bgra8 or Bgrx8 or Bgra4 or Bgr5A1;

    /// <summary> The top level of an uncompressed BGRA32 TEX, which is what Penumbra decodes textures to, without copying it. </summary>
    public static (ArraySegment<byte> Pixels, int Width, int Height) TopLevelBgra(byte[] tex)
    {
        var header = TextureFiles.ReadTex(tex);
        if (header.Format != Bgra8)
            throw new InvalidDataException("Penumbra did not return BGRA32 pixels.");
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28));
        return (new ArraySegment<byte>(tex, offset, checked(header.Width * header.Height * 4)), header.Width, header.Height);
    }

    public static string FormatName(uint format) => format switch
    {
        0x1130 => "L8",
        0x1131 => "A8",
        Bgra4 => "B4G4R4A4",
        Bgr5A1 => "B5G5R5A1",
        Bgra8 => "BGRA32",
        Bgrx8 => "BGRX32",
        0x2150 => "R32F",
        0x2250 => "R16G16F",
        0x2260 => "R32G32F",
        0x2460 => "R16G16B16A16F",
        0x2470 => "R32G32B32A32F",
        Bc1 => "BC1",
        Bc2 => "BC2",
        Bc3 => "BC3",
        Bc4 => "BC4",
        Bc5 => "BC5",
        Bc6H => "BC6H",
        Bc7 => "BC7",
        _ => $"0x{format:X4}",
    };
}
