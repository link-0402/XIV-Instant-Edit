using System;
using System.IO;
using InstantEdit.Studio.Models;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Converts image files (png/jpeg/dds) into FFXIV .tex texture files.
/// The .tex header follows Lumina's <c>TexFile.TexHeader</c> (the same layout
/// Penumbra's TexFileParser and TexTools' Tex.cs use).
/// Uncompressed textures use the game's B8G8R8A8 (RGBA 32-bit) format;
/// BC7 compression runs fully in-process via the managed BCnEncoder library.
/// </summary>
public static class TextureConverter
{
    private const int TexHeaderSize = 80;
    private const int MaxMipCount   = 13;

    /// <summary>The widest or tallest texture the game can make (and that fits the .tex header).</summary>
    public const int MaxSide = 16384;

    // TexFile.Attribute (bit flags; the texture-type dimension lives at bit 22-24)
    private const uint TextureType2D = 0x00800000;

    // TexFile.TextureFormat
    private const uint FormatB8G8R8A8 = 0x1450;
    private const uint FormatBC1      = 0x3420;
    private const uint FormatBC2      = 0x3430;
    private const uint FormatBC3      = 0x3431;
    private const uint FormatBC4      = 0x6120;
    private const uint FormatBC5      = 0x6230;
    private const uint FormatBC6H     = 0x6330;
    private const uint FormatBC7      = 0x6432;

    private const uint DdsMagic = 0x20534444; // "DDS "

    /// <summary>
    /// Converts the image at <paramref name="sourcePath"/> into a complete .tex
    /// file (header + mip data). When <paramref name="compressBc7"/> is set, the
    /// texture is compressed to BC7 in-process. Returns false with an error
    /// message on failure.
    /// </summary>
    public static bool Convert(string sourcePath, bool compressBc7, out byte[] texData, out string error)
        => Convert(sourcePath, compressBc7 ? TextureCompression.Bc7 : TextureCompression.None, out texData, out error);

    /// <summary>
    /// Converts the image at <paramref name="sourcePath"/> into a complete .tex file (header + mip
    /// data), in the format <paramref name="compression"/> asks for. <paramref name="progress"/> is
    /// reported through while a block format is compressed, which is the slow part by far.
    /// Returns false with an error message on failure.
    /// </summary>
    public static bool Convert(string sourcePath, TextureCompression compression, out byte[] texData, out string error,
        IProgress<float>? progress = null)
    {
        texData = Array.Empty<byte>();
        error   = string.Empty;

        byte[] source;
        try
        {
            source = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex)
        {
            error = $"Could not read source file: {ex.Message}";
            return false;
        }

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                    return compression == TextureCompression.None
                        ? ConvertImageToBgra(source, out texData, out error)
                        : ConvertImageToBlocks(source, compression, out texData, out error, progress);

                case ".dds":
                    if (!ReadDds(source, out var info, out error))
                        return false;
                    if (compression == TextureCompression.None)
                        return WriteTex(info, out texData);
                    if (info.FormatCode == TexFormatOf(compression))
                        return WriteTex(info, out texData);      // already in that format — passthrough
                    return ConvertDdsToBlocks(info, compression, out texData, out error, progress);

                default:
                    error = $"Unsupported file type '{ext}' (use png, jpeg or dds).";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Conversion failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Generates a placeholder .tex: a <see cref="DummyTextureLibrary.Size"/> square of the
    /// option's one colour — no source file needed.
    /// </summary>
    public static bool CreateDummy(DummyTextureLibrary.DummyOption option, TextureCompression compression, out byte[] texData, out string error)
    {
        texData = Array.Empty<byte>();
        error   = string.Empty;

        const int size = DummyTextureLibrary.Size;
        var bgra = new byte[size * size * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i]     = option.B;
            bgra[i + 1] = option.G;
            bgra[i + 2] = option.R;
            bgra[i + 3] = option.A;
        }

        if (compression == TextureCompression.None)
        {
            var header = BuildHeader(size, size, FormatB8G8R8A8, mipCount: 1, out _);
            var data = new byte[TexHeaderSize + bgra.Length];
            header.CopyTo(data, 0);
            bgra.CopyTo(data, TexHeaderSize);
            texData = data;
            return true;
        }

        var dds = EncodeToDds(bgra, size, size, compression, out error);
        if (dds == null) return false;
        return ReadDds(dds, out var info, out error) && WriteTex(info, out texData);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PNG / JPEG
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Uncompressed path: RGBA pixels packed as the game's B8G8R8A8 format, single mip.</summary>
    private static bool ConvertImageToBgra(byte[] source, out byte[] texData, out string error)
    {
        using var image = LoadImage(source, out error);
        if (image == null)
        {
            texData = Array.Empty<byte>();
            return false;
        }

        var bgra = ImageToBgra(image);
        var header = BuildHeader(image.Width, image.Height, FormatB8G8R8A8, mipCount: 1, out _);
        var data = new byte[TexHeaderSize + bgra.Length];
        header.CopyTo(data, 0);
        bgra.CopyTo(data, TexHeaderSize);
        texData = data;
        error   = string.Empty;
        return true;
    }

    /// <summary>Block-compressed path: encode the image (with a generated mip chain) via BCnEncoder.</summary>
    private static bool ConvertImageToBlocks(byte[] source, TextureCompression compression, out byte[] texData,
        out string error, IProgress<float>? progress = null)
    {
        using var image = LoadImage(source, out error);
        if (image == null)
        {
            texData = Array.Empty<byte>();
            return false;
        }

        var bgra = ImageToBgra(image);
        var dds = EncodeToDds(bgra, image.Width, image.Height, compression, out error, progress);
        if (dds == null)
        {
            texData = Array.Empty<byte>();
            return false;
        }

        if (!ReadDds(dds, out var info, out error))
        {
            texData = Array.Empty<byte>();
            return false;
        }

        return WriteTex(info, out texData);
    }

    private static Image<Rgba32>? LoadImage(byte[] source, out string error)
    {
        try
        {
            // The size first, from the header: an image too big for the game isn't decoded at all.
            using (var header = new MemoryStream(source, writable: false))
            {
                var info = Image.Identify(StudioImages.Decoder, header);
                if (SizeProblem(info.Width, info.Height) is { } problem)
                {
                    error = problem;
                    return null;
                }
            }
            error = string.Empty;
            return Image.Load<Rgba32>(StudioImages.Decoder, source);
        }
        catch (Exception ex)
        {
            error = $"Could not load image: {ex.Message}";
            return null;
        }
    }

    /// <summary>Why the game can't have a texture of this size, or null when it can.</summary>
    internal static string? SizeProblem(long width, long height)
        => width < 1 || height < 1 ? "The image has no pixels."
         : width > MaxSide || height > MaxSide ? $"The image is {width}x{height}; the game takes textures up to {MaxSide}x{MaxSide}. Make it smaller first."
         : null;

    private static byte[] ImageToBgra(Image<Rgba32> image)
    {
        var bgra = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bgra);
        for (int i = 0; i + 4 <= bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BC7 compression via BCnEncoder (in-process, no external tools)
    // ─────────────────────────────────────────────────────────────────────────

    private static byte[]? EncodeToDds(byte[] bgraPixels, int width, int height, TextureCompression compression,
        out string error, IProgress<float>? progress = null)
    {
        try
        {
            // The encoder already spreads blocks over every core; what makes BC7 slow is the search
            // it runs per block, which is also what makes it hold detail better than BC3.
            var encoder = new BcEncoder();
            encoder.OutputOptions.Format          = BcFormatOf(compression);
            encoder.OutputOptions.GenerateMipMaps = true;
            encoder.OutputOptions.Quality         = CompressionQuality.Balanced;
            encoder.OutputOptions.FileFormat      = OutputFileFormat.Dds;
            if (progress != null)
                encoder.Options.Progress = new BlockProgress(progress);

            using var ms = new MemoryStream();
            encoder.EncodeToStream(bgraPixels, width, height, PixelFormat.Bgra32, ms);
            error = string.Empty;
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            error = $"BC7 compression failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>Re-compresses a DDS into another block format: decode mip 0 to pixels, then encode.</summary>
    private static bool ConvertDdsToBlocks(DdsInfo info, TextureCompression compression, out byte[] texData,
        out string error, IProgress<float>? progress = null)
    {
        texData = Array.Empty<byte>();
        error   = string.Empty;

        byte[] pixels;
        if (info.Compressed)
        {
            if (info.FormatCode == FormatBC6H)
            {
                error = "A BC6H (HDR) .dds can't be changed to another format here. Save it as a PNG first.";
                return false;
            }
            var format = ToBcEncoderFormat(info.FormatCode);
            if (format == CompressionFormat.Unknown)
            {
                error = $"Cannot recompress DDS format 0x{info.FormatCode:X4}.";
                return false;
            }

            try
            {
                var decoder   = new BcDecoder();
                var decoded   = decoder.DecodeRaw(info.MipData[0], info.Width, info.Height, format);
                pixels = new byte[decoded.Length * 4];
                for (int i = 0; i < decoded.Length; i++)
                {
                    pixels[i * 4]     = decoded[i].b;
                    pixels[i * 4 + 1] = decoded[i].g;
                    pixels[i * 4 + 2] = decoded[i].r;
                    pixels[i * 4 + 3] = decoded[i].a;
                }
            }
            catch (Exception ex)
            {
                error = $"Could not decode DDS for recompression: {ex.Message}";
                return false;
            }
        }
        else
        {
            pixels = info.MipData[0]; // uncompressed DDS already gives BGRA bytes
        }

        var dds = EncodeToDds(pixels, info.Width, info.Height, compression, out error, progress);
        if (dds == null) return false;
        return ReadDds(dds, out var encoded, out error) && WriteTex(encoded, out texData);
    }

    private static CompressionFormat BcFormatOf(TextureCompression compression)
        => compression switch
        {
            TextureCompression.Bc3 => CompressionFormat.Bc3,
            TextureCompression.Bc5 => CompressionFormat.Bc5,
            _                      => CompressionFormat.Bc7,
        };

    /// <summary>The game format code a compression choice produces, for the DDS passthrough check.</summary>
    private static uint TexFormatOf(TextureCompression compression)
        => compression switch
        {
            TextureCompression.Bc3 => FormatBC3,
            TextureCompression.Bc5 => FormatBC5,
            _                      => FormatBC7,
        };

    /// <summary>Turns the encoder per-block reports into a 0-1 fraction, without flooding the caller.</summary>
    private sealed class BlockProgress : IProgress<ProgressElement>
    {
        private readonly IProgress<float> _target;
        private int _lastPercent = -1;

        public BlockProgress(IProgress<float> target) => _target = target;

        public void Report(ProgressElement value)
        {
            if (value.TotalBlocks <= 0)
                return;

            int percent = (int)(100.0 * value.CurrentBlock / value.TotalBlocks);
            if (percent == _lastPercent)
                return;

            _lastPercent = percent;
            _target.Report(percent / 100f);
        }
    }

    private static CompressionFormat ToBcEncoderFormat(uint format)
        => format switch
        {
            FormatBC1 => CompressionFormat.Bc1WithAlpha,
            FormatBC2 => CompressionFormat.Bc2,
            FormatBC3 => CompressionFormat.Bc3,
            FormatBC4 => CompressionFormat.Bc4,
            FormatBC5 => CompressionFormat.Bc5,
            FormatBC7 => CompressionFormat.Bc7,
            _         => CompressionFormat.Unknown,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // DDS parsing
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class DdsInfo
    {
        public uint      FormatCode;
        public int       Width;
        public int       Height;
        public bool      Compressed;
        public byte[][]  MipData = Array.Empty<byte[]>();   // pixel/block data per mip
        public int       DataSize;                          // total mip data size
    }

    private static bool ReadDds(byte[] source, out DdsInfo info, out string error)
    {
        info  = new DdsInfo();
        error = string.Empty;

        using var ms = new MemoryStream(source, writable: false);
        using var br = new BinaryReader(ms);

        if (br.ReadUInt32() != DdsMagic)
        {
            error = "Not a valid DDS file.";
            return false;
        }

        br.ReadUInt32();              // dwSize
        br.ReadUInt32();              // dwFlags
        uint height = br.ReadUInt32();
        uint width  = br.ReadUInt32();
        br.ReadUInt32();              // dwPitchOrLinearSize
        br.ReadUInt32();              // dwDepth
        uint ddsMipCount = br.ReadUInt32();
        if (SizeProblem(width, height) is { } sizeProblem)
        {
            error = sizeProblem;
            return false;
        }
        br.ReadBytes(44);             // dwReserved1[11]

        br.ReadUInt32();              // pf.dwSize (always 32)
        uint pfFlags  = br.ReadUInt32();
        uint fourCC   = br.ReadUInt32();
        uint rgbBits  = br.ReadUInt32();
        uint rMask     = br.ReadUInt32();
        uint gMask     = br.ReadUInt32();
        uint bMask     = br.ReadUInt32();
        uint aMask     = br.ReadUInt32();
        br.ReadBytes(16);             // dwCaps..dwCaps4
        br.ReadUInt32();              // trailing padding — the DDS header is 124 bytes after the magic

        bool hasDx10    = fourCC == 0x30315844; // "DX10"
        uint dxgiFormat = 0;
        if (hasDx10)
        {
            dxgiFormat = br.ReadUInt32();   // DX10 extension begins at offset 128
            br.ReadBytes(16);               // resource dimension, misc flags, array size, misc flags 2
        }

        uint format;
        int  bytesPerBlock;
        bool swapRgba = false;

        if (hasDx10)
        {
            switch (dxgiFormat)
            {
                case 71: case 72: format = FormatBC1; bytesPerBlock = 8;  break;
                case 74: case 75: format = FormatBC2; bytesPerBlock = 16; break;
                case 77: case 78: format = FormatBC3; bytesPerBlock = 16; break;
                case 80: case 81: format = FormatBC4; bytesPerBlock = 8;  break;
                case 83: case 84: format = FormatBC5; bytesPerBlock = 16; break;
                case 95: case 96: format = FormatBC6H; bytesPerBlock = 16; break;
                case 98: case 99: format = FormatBC7; bytesPerBlock = 16; break;
                case 28: case 29: format = FormatB8G8R8A8; bytesPerBlock = 4; swapRgba = true; break;
                case 87: case 88: format = FormatB8G8R8A8; bytesPerBlock = 4; break;
                default:
                    error = $"Unsupported DDS format (DXGI 0x{dxgiFormat:X}).";
                    return false;
            }
        }
        else if (fourCC == 0x31545844) { format = FormatBC1; bytesPerBlock = 8;  }  // DXT1
        else if (fourCC == 0x32545844 || fourCC == 0x33545844) { format = FormatBC2; bytesPerBlock = 16; }  // DXT2 / DXT3
        // DXT4 and DXT5 are the same blocks; DXT4 only says the colour is premultiplied by alpha.
        else if (fourCC == 0x34545844 || fourCC == 0x35545844) { format = FormatBC3; bytesPerBlock = 16; }  // DXT4 / DXT5
        else if (fourCC == 0x31495441 || fourCC == 0x55344342) { format = FormatBC4; bytesPerBlock = 8;  }  // ATI1 / BC4U
        else if (fourCC == 0x32495441 || fourCC == 0x55354342) { format = FormatBC5; bytesPerBlock = 16; }  // ATI2 / BC5U
        else if (fourCC == 0)
        {
            // Uncompressed DDS — only 32-bit pixels are supported.
            if (rgbBits != 32)
            {
                error = $"Unsupported uncompressed DDS ({rgbBits} bpp; only 32 bpp is supported).";
                return false;
            }
            format = FormatB8G8R8A8;
            bytesPerBlock = 4;
            // DDPF_RGB with red in the low byte means the file is RGBA, not BGRA.
            swapRgba = rMask == 0x000000FF;
        }
        else
        {
            error = $"Unsupported DDS compression (FourCC 0x{fourCC:X8}).";
            return false;
        }

        int mipCount = (int)Math.Min(Math.Max(ddsMipCount, 1), MaxMipCount);
        bool compressed = bytesPerBlock >= 8;

        var dataOffset = (int)ms.Position;
        var mipData = new byte[mipCount][];
        int cursor = dataOffset;

        for (int i = 0; i < mipCount; i++)
        {
            int mipW = Math.Max((int)width >> i, 1);
            int mipH = Math.Max((int)height >> i, 1);
            int size = compressed
                ? ((mipW + 3) / 4) * ((mipH + 3) / 4) * bytesPerBlock
                : mipW * mipH * bytesPerBlock;

            if (cursor + size > source.Length)
            {
                error = "DDS file is truncated (mip chain does not fit).";
                return false;
            }

            var block = new byte[size];
            Array.Copy(source, cursor, block, 0, size);

            if (swapRgba)
            {
                for (int p = 0; p + 4 <= block.Length; p += 4)
                    (block[p], block[p + 2]) = (block[p + 2], block[p]);
            }

            mipData[i] = block;
            cursor += size;
        }

        info.FormatCode = format;
        info.Width      = (int)width;
        info.Height     = (int)height;
        info.Compressed = compressed;
        info.MipData    = mipData;
        info.DataSize   = cursor - dataOffset;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // .tex header (80 bytes, Lumina TexFile.TexHeader layout)
    // ─────────────────────────────────────────────────────────────────────────

    private static bool WriteTex(DdsInfo info, out byte[] texData)
    {
        var header = BuildHeader(info.Width, info.Height, info.FormatCode, info.MipData.Length, out var mipOffsets);
        var result = new byte[TexHeaderSize + info.DataSize];
        header.CopyTo(result, 0);
        for (int i = 0; i < info.MipData.Length; i++)
            info.MipData[i].CopyTo(result, (int)mipOffsets[i]);
        texData = result;
        return true;
    }

    private static byte[] BuildHeader(int width, int height, uint format, int mipCount, out uint[] mipOffsets)
    {
        var w = new BinaryWriter(new MemoryStream(TexHeaderSize));
        w.Write(TextureType2D);                 // uint   Attributes / Type
        w.Write(format);                        // uint   TextureFormat
        w.Write((ushort)width);                 // ushort Width
        w.Write((ushort)height);                // ushort Height
        w.Write((ushort)1);                     // ushort Depth
        w.Write((ushort)mipCount);              // ushort MipLevels

        // LoD mip indices — clamped so they never point past the last mip.
        for (int i = 0; i < 3; i++)
            w.Write((uint)Math.Min(i, mipCount - 1));

        // 13 mip surface offsets, recomputed from the header start.
        int mipCountActual = Math.Min(mipCount, MaxMipCount);
        mipOffsets = new uint[mipCountActual];
        long offset = TexHeaderSize;
        int wCur = Math.Max(width, 1), hCur = Math.Max(height, 1);
        for (int i = 0; i < MaxMipCount; i++)
        {
            if (i < mipCountActual)
            {
                mipOffsets[i] = (uint)offset;
                w.Write((uint)offset);
                offset += MipDataSize(format, wCur, hCur);
                wCur = Math.Max(wCur / 2, 1);
                hCur = Math.Max(hCur / 2, 1);
            }
            else
            {
                w.Write(0u);
            }
        }

        var ms = (MemoryStream)w.BaseStream;
        return ms.ToArray();
    }

    private static int MipDataSize(uint format, int width, int height)
        => format == FormatB8G8R8A8
            ? width * height * 4
            : format == FormatBC1 || format == FormatBC4
                ? ((width + 3) / 4) * ((height + 3) / 4) * 8
                : ((width + 3) / 4) * ((height + 3) / 4) * 16;
}
