using System;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>A decoded texture: straight RGBA8 pixels plus its dimensions.</summary>
public sealed class DecodedTexture
{
    public int    Width;
    public int    Height;
    public byte[] Rgba = Array.Empty<byte>();

    /// <summary>What the source resource was, for the report and the UI.</summary>
    public string SourceFormat = string.Empty;

    public Image<Rgba32> ToImage() => Image.LoadPixelData<Rgba32>(Rgba, Width, Height);

    public byte[] ToPng()
    {
        using var image = ToImage();
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>PNG bytes of a copy scaled to fit inside a square of <paramref name="maxSize"/> pixels.</summary>
    public byte[] ToThumbnailPng(int maxSize)
    {
        using var image = ToImage();
        if (image.Width > maxSize || image.Height > maxSize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(maxSize, maxSize),
                Mode = ResizeMode.Max,
            }));
        }
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Decodes the texture shapes a Sims 4 package stores.
///
/// _IMG resources hold an ordinary DDS. RLE2 and RLES instead open with the block
/// format as a FourCC ("DXT5") followed by their own tag, and hold that format's block
/// data run-length encoded: rather than storing every block, the resource stores a
/// command stream where each command says "the next N blocks are fully transparent",
/// "the next N blocks are opaque, here is their colour data" or "the next N blocks are
/// complete, here is all of it" — and the block bytes themselves live in separate
/// parallel streams that each command advances independently. Unpacking that gives
/// back a plain block payload, which then goes down the same path as a DDS.
/// </summary>
public static class SimsTextureDecoder
{
    private const uint DdsMagic = 0x20534444;   // "DDS "
    private const uint TagRle2  = 0x32454C52;   // "RLE2"
    private const uint TagRles  = 0x53454C52;   // "RLES"

    private const uint FourCcDxt1 = 0x31545844;   // "DXT1"
    private const uint FourCcDxt3 = 0x33545844;   // "DXT3"
    private const uint FourCcDxt5 = 0x35545844;   // "DXT5"
    private const uint FourCcAti2 = 0x32495441;   // "ATI2"
    private const uint FourCcDx10 = 0x30315844;   // "DX10"

    // Maxis writes DXT blocks under its own DST tags. The block layout is identical —
    // only the tag differs — so they decode through the same path.
    private const uint FourCcDst1 = 0x31545344;   // "DST1"
    private const uint FourCcDst3 = 0x33545344;   // "DST3"
    private const uint FourCcDst5 = 0x35545344;   // "DST5"

    /// <summary>
    /// Largest side accepted. The Sims 4 itself stops at 4096; 8192 leaves room for
    /// oversized custom content while keeping one decode (blocks, pixels, RGBA and the
    /// thumbnail image) to about a gigabyte at worst, since a header is just two numbers
    /// and a downloaded file can claim anything.
    /// </summary>
    internal const int MaxTextureSide = 8192;

    /// <summary>Most blocks one RLE command can cover: its count is the top 14 bits.</summary>
    private const int MaxBlocksPerRleCommand = 0xFFFF >> 2;

    /// <summary>Decodes a texture resource of any of the supported types.</summary>
    public static DecodedTexture Decode(uint resourceType, byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("Texture resource is too small to hold a header.");

        // An RLE resource leads with its block format as a FourCC and its tag second,
        // where a plain DDS leads with the DDS magic.
        uint tag = BitConverter.ToUInt32(data, 4);
        if (tag == TagRle2 || tag == TagRles)
            return DecodeDds(UnpackRle(data, tag), tag == TagRle2 ? "RLE2" : "RLES");

        if (BitConverter.ToUInt32(data, 0) == DdsMagic)
            return DecodeDds(data, "DDS");

        throw new InvalidDataException(
            $"Resource {SimsResourceType.Name(resourceType)} is not a DDS, RLE2 or RLES texture.");

    }

    // ─────────────────────────────────────────────────────────────────────────
    // RLE unpacking
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class MipOffsets
    {
        public int Command;
        public int Offset0;
        public int Offset1;
        public int Offset2;
        public int Offset3;
    }

    private static byte[] UnpackRle(byte[] data, uint tag)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        uint fourCc = br.ReadUInt32();        // block format, e.g. "DXT5"
        br.ReadUInt32();                      // RLE2 / RLES tag
        int width    = br.ReadUInt16();
        int height   = br.ReadUInt16();
        int mipCount = br.ReadUInt16();
        br.ReadUInt16();                      // unknown / always zero

        if (mipCount <= 0)
            throw new InvalidDataException("RLE texture declares no mip levels.");
        CheckDimensions(width, height);

        bool isRles = tag == TagRles;
        if (16 + (long)mipCount * (isRles ? 24 : 20) > data.Length)
            throw new InvalidDataException("RLE texture is too small for its mip table.");

        var mips = new MipOffsets[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            var m = new MipOffsets { Command = br.ReadInt32() };
            m.Offset0 = br.ReadInt32();
            m.Offset1 = br.ReadInt32();
            m.Offset2 = br.ReadInt32();
            m.Offset3 = br.ReadInt32();
            if (isRles)
                br.ReadInt32();               // fifth stream — alpha overlay, unused here
            mips[i] = m;
        }

        // Only mip 0 is needed — it is the full-resolution image — and its command
        // stream runs until the next mip's begins.
        int commandEnd = mipCount > 1 ? mips[1].Command : data.Length;

        int mipWidth  = width;
        int mipHeight = height;
        int blocksX = Math.Max((mipWidth  + 3) / 4, 1);
        int blocksY = Math.Max((mipHeight + 3) / 4, 1);
        int blockLimit = blocksX * blocksY;

        var cursor = mips[0];
        int command = cursor.Command;
        if (command < 0 || command > data.Length || commandEnd < command || commandEnd > data.Length)
            throw new InvalidDataException("RLE command stream lies outside the resource.");

        // Every block has to come from a command, and one command covers at most
        // MaxBlocksPerRleCommand of them, so a stream too short to reach the last block is
        // refused before the block buffer is allocated rather than left zero-filled.
        if ((long)(commandEnd - command) / 2 * MaxBlocksPerRleCommand < blockLimit)
            throw new InvalidDataException("RLE command stream is too short for the image size.");

        var blocks = new byte[blockLimit * 16];

        // The four streams line up with the four fields of a DXT5 block, not with the
        // block as a whole: two carry the colour halves for every block that has colour,
        // and two carry the alpha halves for the blocks that also have alpha.
        int colorLow = cursor.Offset0, colorHigh = cursor.Offset1;
        int alphaLow = cursor.Offset2, alphaHigh = cursor.Offset3;

        int blockIndex = 0;

        while (command + 2 <= commandEnd && blockIndex < blockLimit)
        {
            ushort op = BitConverter.ToUInt16(data, command);
            command += 2;

            int kind  = op & 0x3;
            int count = op >> 2;

            for (int i = 0; i < count && blockIndex < blockLimit; i++, blockIndex++)
            {
                int at = blockIndex * 16;

                switch (kind)
                {
                    case 0:
                        // Fully transparent: alpha 0 everywhere, colour irrelevant.
                        Array.Clear(blocks, at, 16);
                        break;

                    case 1:
                        // A complete block — alpha and colour both come off the streams.
                        Require(data, alphaLow, 2);
                        Require(data, alphaHigh, 6);
                        Require(data, colorLow, 4);
                        Require(data, colorHigh, 4);
                        Buffer.BlockCopy(data, alphaLow,  blocks, at,      2);
                        Buffer.BlockCopy(data, alphaHigh, blocks, at + 2,  6);
                        Buffer.BlockCopy(data, colorLow,  blocks, at + 8,  4);
                        Buffer.BlockCopy(data, colorHigh, blocks, at + 12, 4);
                        alphaLow += 2; alphaHigh += 6; colorLow += 4; colorHigh += 4;
                        break;

                    case 2:
                        // Fully opaque: only the colour is stored, the alpha is implied.
                        Require(data, colorLow, 4);
                        Require(data, colorHigh, 4);
                        blocks[at]     = 0xFF;
                        blocks[at + 1] = 0xFF;
                        Array.Clear(blocks, at + 2, 6);
                        Buffer.BlockCopy(data, colorLow,  blocks, at + 8,  4);
                        Buffer.BlockCopy(data, colorHigh, blocks, at + 12, 4);
                        colorLow += 4; colorHigh += 4;
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported RLE command type {kind}.");
                }
            }
        }

        if (blockIndex != blockLimit)
            throw new InvalidDataException(
                $"RLE command stream covers {blockIndex:N0} of {blockLimit:N0} blocks.");

        return BuildDds(blocks, mipWidth, mipHeight, fourCc);
    }

    /// <summary>Refuses zero, negative and oversized dimensions before anything is sized from them.</summary>
    private static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Texture declares zero dimensions.");
        if (width > MaxTextureSide || height > MaxTextureSide)
            throw new InvalidDataException(
                $"Texture is {width}x{height}; the largest supported side is {MaxTextureSide}.");
    }

    /// <summary>Renders a FourCC back as its four characters, for error messages.</summary>
    private static string FourCcName(uint fourCc)
        => new(new[] { (char)(fourCc & 0xFF), (char)((fourCc >> 8) & 0xFF),
                       (char)((fourCc >> 16) & 0xFF), (char)((fourCc >> 24) & 0xFF) });

    /// <summary>Guards a stream read, so a truncated resource fails with a clear message.</summary>
    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
            throw new InvalidDataException("RLE stream runs past the end of the resource.");
    }

    /// <summary>Wraps raw block data in a minimal single-mip DDS header.</summary>
    private static byte[] BuildDds(byte[] blocks, int width, int height, uint fourCc)
    {
        var dds = new byte[128 + blocks.Length];
        using var ms = new MemoryStream(dds);
        using var bw = new BinaryWriter(ms);

        bw.Write(DdsMagic);
        bw.Write(124);                                   // dwSize
        bw.Write(0x00081007u);                           // caps | height | width | pixelformat | linearsize
        bw.Write(height);
        bw.Write(width);
        bw.Write(blocks.Length);                         // dwPitchOrLinearSize
        bw.Write(0);                                     // dwDepth
        bw.Write(1);                                     // dwMipMapCount
        bw.Write(new byte[44]);                          // dwReserved1[11]

        bw.Write(32);                                    // pf.dwSize
        bw.Write(0x4u);                                  // DDPF_FOURCC
        bw.Write(fourCc);
        bw.Write(new byte[20]);                          // bit counts and masks, unused for FourCC

        bw.Write(0x1000u);                               // DDSCAPS_TEXTURE
        bw.Write(new byte[16]);                          // dwCaps2..dwCaps4 + reserved

        Buffer.BlockCopy(blocks, 0, dds, 128, blocks.Length);
        return dds;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DDS decoding
    // ─────────────────────────────────────────────────────────────────────────

    private static DecodedTexture DecodeDds(byte[] dds, string sourceFormat)
    {
        if (dds.Length < 128)
            throw new InvalidDataException("DDS is too small to hold its header.");

        using var ms = new MemoryStream(dds, writable: false);
        using var br = new BinaryReader(ms);

        if (br.ReadUInt32() != DdsMagic)
            throw new InvalidDataException("Not a DDS file.");

        br.ReadUInt32();                        // dwSize
        br.ReadUInt32();                        // dwFlags
        int height = br.ReadInt32();
        int width  = br.ReadInt32();
        br.ReadUInt32();                        // dwPitchOrLinearSize
        br.ReadUInt32();                        // dwDepth
        br.ReadUInt32();                        // dwMipMapCount
        ms.Position += 44;                      // dwReserved1[11]

        br.ReadUInt32();                        // pf.dwSize
        uint pfFlags = br.ReadUInt32();
        uint fourCc  = br.ReadUInt32();
        int  rgbBits = br.ReadInt32();
        uint rMask   = br.ReadUInt32();
        uint gMask   = br.ReadUInt32();
        uint bMask   = br.ReadUInt32();
        uint aMask   = br.ReadUInt32();
        ms.Position += 20;                      // dwCaps..dwCaps4 + reserved2

        CheckDimensions(width, height);

        if (fourCc == FourCcDx10)
        {
            if (dds.Length < 148)
                throw new InvalidDataException("DDS is too small to hold its DX10 header.");
            uint dxgiFormat = br.ReadUInt32();
            ms.Position += 16;
            return DecodeBlocks(dds, (int)ms.Position, width, height, DxgiToFormat(dxgiFormat), sourceFormat);
        }

        bool isFourCc = (pfFlags & 0x4) != 0;
        if (isFourCc)
        {
            var format = fourCc switch
            {
                FourCcDxt1 or FourCcDst1 => CompressionFormat.Bc1WithAlpha,
                FourCcDxt3 or FourCcDst3 => CompressionFormat.Bc2,
                FourCcDxt5 or FourCcDst5 => CompressionFormat.Bc3,
                FourCcAti2               => CompressionFormat.Bc5,
                _ => throw new InvalidDataException(
                    $"Unsupported DDS block format \"{FourCcName(fourCc)}\"."),
            };
            return DecodeBlocks(dds, (int)ms.Position, width, height, format, sourceFormat);
        }

        return DecodeUncompressed(dds, (int)ms.Position, width, height, rgbBits, rMask, gMask, bMask, aMask, sourceFormat);
    }

    /// <summary>Bytes one mip level occupies: whole 4x4 blocks, at this format's block size.</summary>
    private static long BlockDataLength(int width, int height, CompressionFormat format)
    {
        long blocksX = Math.Max((width  + 3) / 4, 1);
        long blocksY = Math.Max((height + 3) / 4, 1);
        return blocksX * blocksY * BytesPerBlock(format);
    }

    private static int BytesPerBlock(CompressionFormat format) => format switch
    {
        CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 => 8,
        _ => 16,
    };

    private static CompressionFormat DxgiToFormat(uint dxgiFormat) => dxgiFormat switch
    {
        70 or 71 or 72 => CompressionFormat.Bc1WithAlpha,
        73 or 74 or 75 => CompressionFormat.Bc2,
        76 or 77 or 78 => CompressionFormat.Bc3,
        79 or 80 or 81 => CompressionFormat.Bc4,
        82 or 83 or 84 => CompressionFormat.Bc5,
        97 or 98 or 99 => CompressionFormat.Bc7,
        _ => throw new InvalidDataException($"Unsupported DXGI format {dxgiFormat}."),
    };

    private static DecodedTexture DecodeBlocks(byte[] dds, int dataOffset, int width, int height,
        CompressionFormat format, string sourceFormat)
    {
        // Only mip 0, sliced exactly. A DDS carries its whole mip chain after the header,
        // and handing all of it to the decoder makes it lay the smaller levels out as if
        // they were more rows of the full-size image — the picture then comes back as the
        // texture tiled at shrinking scales rather than as an error.
        long mip0Length = BlockDataLength(width, height, format);
        if (dataOffset + mip0Length > dds.Length)
            throw new InvalidDataException("DDS is truncated: it does not hold a complete first mip level.");

        var blockData = new byte[mip0Length];
        Buffer.BlockCopy(dds, dataOffset, blockData, 0, (int)mip0Length);

        var decoder = new BcDecoder();
        var pixels  = decoder.DecodeRaw(blockData, width, height, format);

        var rgba = new byte[(long)width * height * 4];
        for (int i = 0; i < pixels.Length && i * 4 + 3 < rgba.Length; i++)
        {
            rgba[i * 4]     = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }

        return new DecodedTexture
        {
            Width  = width,
            Height = height,
            Rgba   = rgba,
            SourceFormat = $"{sourceFormat} ({format})",
        };
    }

    private static DecodedTexture DecodeUncompressed(byte[] dds, int dataOffset, int width, int height,
        int rgbBits, uint rMask, uint gMask, uint bMask, uint aMask, string sourceFormat)
    {
        if (rgbBits != 32 && rgbBits != 24)
            throw new InvalidDataException($"Unsupported uncompressed DDS bit depth {rgbBits}.");

        int bytesPerPixel = rgbBits / 8;
        int pixelCount = width * height;            // at most MaxTextureSide squared, so no overflow
        if (dataOffset + (long)pixelCount * bytesPerPixel > dds.Length)
            throw new InvalidDataException("DDS is truncated: it does not hold every pixel of the first mip level.");

        var rgba = new byte[(long)pixelCount * 4];

        int rShift = MaskShift(rMask), gShift = MaskShift(gMask), bShift = MaskShift(bMask), aShift = MaskShift(aMask);

        for (int i = 0; i < pixelCount; i++)
        {
            int at = dataOffset + i * bytesPerPixel;

            uint value = bytesPerPixel == 4
                ? BitConverter.ToUInt32(dds, at)
                : (uint)(dds[at] | (dds[at + 1] << 8) | (dds[at + 2] << 16));

            rgba[i * 4]     = (byte)((value & rMask) >> rShift);
            rgba[i * 4 + 1] = (byte)((value & gMask) >> gShift);
            rgba[i * 4 + 2] = (byte)((value & bMask) >> bShift);
            rgba[i * 4 + 3] = aMask != 0 ? (byte)((value & aMask) >> aShift) : (byte)255;
        }

        return new DecodedTexture
        {
            Width  = width,
            Height = height,
            Rgba   = rgba,
            SourceFormat = $"{sourceFormat} (uncompressed {rgbBits}-bit)",
        };
    }

    private static int MaskShift(uint mask)
    {
        if (mask == 0) return 0;
        int shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }
        return shift;
    }
}
