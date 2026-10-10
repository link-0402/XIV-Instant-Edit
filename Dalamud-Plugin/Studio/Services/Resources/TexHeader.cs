using System;
using System.Buffers.Binary;
using System.IO;
using Lumina.Data.Files;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>What a .tex (or .atex) file is, read from its 80-byte header alone.</summary>
internal readonly record struct TexHeaderInfo(uint Format, int Width, int Height, int Depth, int MipCount, uint Attributes)
{
    public string FormatName => Enum.IsDefined((TexFile.TextureFormat)(int)Format)
        ? ((TexFile.TextureFormat)(int)Format).ToString()
        : $"0x{Format:X4}";

    public string Size => Depth > 1 ? $"{Width}×{Height}×{Depth}" : $"{Width}×{Height}";

    public bool IsBlockCompressed => (Format & 0xF000) is 0x3000 or 0x6000;
}

/// <summary>
/// Reads the header of the game's texture format: attributes, format, size and mip count, laid out
/// as Lumina's <c>TexFile.TexHeader</c> (the layout <see cref="TextureConverter"/> writes). Enough to
/// tell a texture's size and format, and to check that a .tex dropped in is one, without decoding it.
/// </summary>
internal static class TexHeader
{
    public const int Size = 80;

    public static bool TryRead(ReadOnlySpan<byte> data, out TexHeaderInfo info)
    {
        info = default;
        if (data.Length < Size)
            return false;

        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint format     = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int width       = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        int height      = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        int depth       = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int mips        = data[14] & 0x7F;   // newer files keep a flag in the top bit and the array size in the next byte

        if (width == 0 || height == 0 || width > 16384 || height > 16384 || format == 0)
            return false;

        info = new TexHeaderInfo(format, width, height, Math.Max(depth, 1), Math.Max(mips, 1), attributes);
        return true;
    }

    /// <summary>Reads just the header of a file on disk.</summary>
    public static bool TryRead(string path, out TexHeaderInfo info)
    {
        info = default;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[Size];
            int read = stream.ReadAtLeast(header, Size, throwOnEndOfStream: false);
            return read == Size && TryRead(header, out info);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
