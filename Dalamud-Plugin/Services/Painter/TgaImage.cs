using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InstantEdit.Services.Painter;

/// <summary> An 8-bit RGBA image, rows top-down. </summary>
internal sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        if (width is < 1 or > TextureFiles.MaxDimension || height is < 1 or > TextureFiles.MaxDimension)
            throw new InvalidDataException($"Unsupported image size {width} x {height}.");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[checked(width * height * 4)];
        if (Pixels.Length != width * height * 4)
            throw new InvalidDataException("The pixel buffer does not match the image size.");
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary> The top-left <paramref name="width"/> × <paramref name="height"/> corner. </summary>
    public RgbaImage Crop(int width, int height)
    {
        if (width < 1 || height < 1 || width > Width || height > Height)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width} x {height} doesn't fit in {Width} x {Height}.");
        var result = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
            Array.Copy(Pixels, y * Width * 4, result.Pixels, y * width * 4, width * 4);
        return result;
    }

    /// <summary> SHA-256 over the size and the RGBA pixels; equal images hash equally whatever their file layout. </summary>
    public string PixelHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, Width);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], Height);
        hash.AppendData(size);
        hash.AppendData(Pixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>
/// Minimal TGA reading and writing for the Painter round trip. Painter writes 24-bit files when a
/// map's alpha is constant white, and bottom-left origins; everything is normalized to 32-bit
/// top-down RGBA, the only layout texture sessions accept.
/// </summary>
internal static class TgaImage
{
    public static RgbaImage Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 18)
            throw new InvalidDataException("The TGA file is too short.");
        int idLength = bytes[0];
        if (bytes[1] != 0)
            throw new InvalidDataException("Color-mapped TGA files are not supported.");
        var type = bytes[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int bits = bytes[16];
        var descriptor = bytes[17];
        var gray = type is 3 or 11;
        var rle = type is 10 or 11;
        if (type is not (2 or 3 or 10 or 11))
            throw new InvalidDataException($"Unsupported TGA image type {type}.");
        if (gray ? bits != 8 : bits is not (24 or 32))
            throw new InvalidDataException($"Unsupported TGA bit depth {bits}.");
        if (width == 0 || height == 0 || width > TextureFiles.MaxDimension || height > TextureFiles.MaxDimension)
            throw new InvalidDataException($"Unsupported TGA size {width} x {height}.");
        var bytesPerPixel = bits / 8;
        var count = width * height;
        var raw = new byte[checked(count * bytesPerPixel)];
        var at = 18 + idLength;
        if (!rle)
        {
            if (at + raw.Length > bytes.Length)
                throw new InvalidDataException("The TGA pixel data is truncated.");
            bytes.Slice(at, raw.Length).CopyTo(raw);
        }
        else
        {
            var written = 0;
            while (written < raw.Length)
            {
                if (at >= bytes.Length)
                    throw new InvalidDataException("The TGA run-length data is truncated.");
                var packet = bytes[at++];
                var run = (packet & 0x7F) + 1;
                if (written + run * bytesPerPixel > raw.Length)
                    throw new InvalidDataException("A TGA run-length packet overruns the image.");
                if ((packet & 0x80) != 0)
                {
                    if (at + bytesPerPixel > bytes.Length)
                        throw new InvalidDataException("The TGA run-length data is truncated.");
                    for (var i = 0; i < run; i++)
                        bytes.Slice(at, bytesPerPixel).CopyTo(raw.AsSpan(written + i * bytesPerPixel));
                    at += bytesPerPixel;
                }
                else
                {
                    var length = run * bytesPerPixel;
                    if (at + length > bytes.Length)
                        throw new InvalidDataException("The TGA run-length data is truncated.");
                    bytes.Slice(at, length).CopyTo(raw.AsSpan(written));
                    at += length;
                }
                written += run * bytesPerPixel;
            }
        }

        var image = new RgbaImage(width, height);
        var topDown = (descriptor & 0x20) != 0;
        var rightToLeft = (descriptor & 0x10) != 0;
        var alphaBits = descriptor & 0x0F;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = topDown ? y : height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var sourceColumn = rightToLeft ? width - 1 - x : x;
                var source = (sourceRow * width + sourceColumn) * bytesPerPixel;
                var target = (y * width + x) * 4;
                if (gray)
                {
                    image.Pixels[target] = image.Pixels[target + 1] = image.Pixels[target + 2] = raw[source];
                    image.Pixels[target + 3] = 255;
                }
                else
                {
                    image.Pixels[target] = raw[source + 2];
                    image.Pixels[target + 1] = raw[source + 1];
                    image.Pixels[target + 2] = raw[source];
                    image.Pixels[target + 3] = bytesPerPixel == 4 && alphaBits > 0 ? raw[source + 3] : (byte)255;
                }
            }
        }
        return image;
    }

    /// <summary> Uncompressed 32-bit BGRA with an 8-bit alpha and a top-left origin. </summary>
    public static byte[] WriteBgra32(RgbaImage image)
    {
        var bytes = new byte[checked(18 + image.Pixels.Length)];
        WriteHeader(bytes, 2, image.Width, image.Height, 32, 0x28);
        var pixels = image.Pixels;
        for (int i = 0, o = 18; i < pixels.Length; i += 4, o += 4)
        {
            bytes[o] = pixels[i + 2];
            bytes[o + 1] = pixels[i + 1];
            bytes[o + 2] = pixels[i];
            bytes[o + 3] = pixels[i + 3];
        }
        return bytes;
    }

    /// <summary> Uncompressed 24-bit color from RGBA pixels, ignoring alpha. </summary>
    public static byte[] WriteRgb24(RgbaImage image)
    {
        var bytes = new byte[checked(18 + image.Width * image.Height * 3)];
        WriteHeader(bytes, 2, image.Width, image.Height, 24, 0x20);
        var pixels = image.Pixels;
        for (int i = 0, o = 18; i < pixels.Length; i += 4, o += 3)
        {
            bytes[o] = pixels[i + 2];
            bytes[o + 1] = pixels[i + 1];
            bytes[o + 2] = pixels[i];
        }
        return bytes;
    }

    /// <summary> Uncompressed 8-bit grayscale holding one RGBA component (0 = R ... 3 = A). </summary>
    public static byte[] WriteChannel(RgbaImage image, int component)
    {
        if (component is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(component));
        var bytes = new byte[checked(18 + image.Width * image.Height)];
        WriteHeader(bytes, 3, image.Width, image.Height, 8, 0x20);
        var pixels = image.Pixels;
        for (int i = component, o = 18; i < pixels.Length; i += 4, o++)
            bytes[o] = pixels[i];
        return bytes;
    }

    private static void WriteHeader(byte[] bytes, byte type, int width, int height, byte bits, byte descriptor)
    {
        bytes[2] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)height);
        bytes[16] = bits;
        bytes[17] = descriptor;
    }
}
