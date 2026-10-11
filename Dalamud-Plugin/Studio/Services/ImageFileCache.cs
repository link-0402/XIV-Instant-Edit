using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;

namespace InstantEdit.Studio.Services;

/// <summary>What a source image is, without decoding it: its size, and what that costs.</summary>
internal readonly record struct ImageFacts(int Width, int Height, string? Error)
{
    public bool Ok => Error == null;

    public long Pixels => (long)Width * Height;

    /// <summary>Bytes the texture takes uncompressed (four per pixel), as the game would carry it.</summary>
    public long UncompressedBytes => Pixels * 4;

    public string Size => $"{Width}×{Height}";

    public static string Mib(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MiB";
}

/// <summary>
/// Reads image headers for the editor and the validator, which ask on every frame they draw. Only
/// the header is read, never the pixels, and the answer is kept against the file's timestamp — so a
/// texture re-exported while the window is open is picked up without re-reading anything large.
/// </summary>
internal sealed class ImageFileCache
{
    private readonly Dictionary<string, (DateTime Stamp, long Length, ImageFacts Facts)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public ImageFacts Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ImageFacts(0, 0, "no file set");

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return new ImageFacts(0, 0, "file not found");
        }
        catch (Exception ex)
        {
            return new ImageFacts(0, 0, ex.Message);
        }

        if (_cache.TryGetValue(path, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Facts;

        ImageFacts facts;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".tex" or ".atex")
            {
                // The game's own format: its header says what ImageSharp cannot.
                facts = Resources.TexHeader.TryRead(path, out var header)
                    ? new ImageFacts(header.Width, header.Height, null)
                    : new ImageFacts(0, 0, "not a readable .tex file");
            }
            else if (ext == ".dds")
            {
                // ImageSharp has no DDS decoder; the header holds the size.
                facts = ReadDds(path);
            }
            else
            {
                var image = Image.Identify(StudioImages.Decoder, path);   // png, jpeg, tga, bmp, gif, webp headers
                facts = new ImageFacts(image.Width, image.Height, null);
            }
        }
        catch (Exception ex)
        {
            facts = new ImageFacts(0, 0, ex.Message);
        }

        _cache[path] = (info.LastWriteTimeUtc, info.Length, facts);
        return facts;
    }

    private const uint DdsMagic = 0x20534444;   // "DDS "

    /// <summary>A DDS file's size from its header: the magic, then height at byte 12 and width at byte 16.</summary>
    internal static ImageFacts ReadDds(string path)
    {
        Span<byte> head = stackalloc byte[20];
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return new ImageFacts(0, 0, "not a readable .dds file");
        }

        uint height = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != DdsMagic || width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            return new ImageFacts(0, 0, "not a readable .dds file");
        return new ImageFacts((int)width, (int)height, null);
    }
}
