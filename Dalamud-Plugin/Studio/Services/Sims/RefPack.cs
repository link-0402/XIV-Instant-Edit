using System;
using System.IO;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>
/// RefPack (also known as QFS) decompressor — the LZ77 variant Maxis has used for
/// DBPF entries since the original SimCity. Sims 4 packages normally use plain ZLIB
/// (compression type 0x5A42) instead, but 0xFFFF entries still turn up in older
/// content and in packages produced by third-party tools, so both paths are needed.
///
/// The stream is a sequence of commands, each identified by its first byte:
///   0x00-0x7F  two bytes  — copy 0-3 literals, then back-reference up to 1 KiB
///   0x80-0xBF  three bytes — copy 0-3 literals, then back-reference up to 16 KiB
///   0xC0-0xDF  four bytes  — copy 0-3 literals, then back-reference up to 128 KiB
///   0xE0-0xFB  one byte   — copy 4-112 literals, no back-reference
///   0xFC-0xFF  one byte   — copy 0-3 literals, then stop
/// Back-references may overlap the bytes they are writing, so the copy has to run
/// one byte at a time rather than as a block move.
/// </summary>
public static class RefPack
{
    /// <summary>
    /// Decompresses a RefPack stream. <paramref name="expectedSize"/> is the entry's
    /// declared uncompressed size, used to size the output buffer; the header's own
    /// size field is used when it is absent (zero). Either size is checked against
    /// <see cref="DbpfPackage.MaxResourceSize"/> and the packed length before anything is
    /// allocated, and a stream that stops short of that size is an error.
    /// </summary>
    public static byte[] Decompress(byte[] source, int expectedSize)
    {
        if (source.Length < 2)
            throw new InvalidDataException("RefPack stream is too short to contain a header.");

        int pos = 0;

        // Header: two flag bits, then the 0xFB signature byte. Flag 0x80 makes the size
        // fields 4 bytes instead of 3, and flag 0x01 means a compressed-size field comes
        // first (skipped here); the big-endian uncompressed size follows.
        int flags = source[pos++];
        int magic = source[pos++];
        if (magic != 0xFB)
            throw new InvalidDataException($"Not a RefPack stream (signature 0x{flags:X2}{magic:X2}).");

        bool large = (flags & 0x80) != 0;
        int sizeBytes = large ? 4 : 3;
        if ((flags & 0x01) != 0)
            pos += sizeBytes;                     // skip the compressed-size field

        if (pos + sizeBytes > source.Length)
            throw new InvalidDataException("RefPack stream is too short to contain a header.");

        long headerSize = 0;
        for (int i = 0; i < sizeBytes; i++)
            headerSize = (headerSize << 8) | source[pos++];

        long outSize = expectedSize > 0 ? expectedSize : headerSize;
        if (outSize == 0)
            throw new InvalidDataException("RefPack stream declares no output.");
        DbpfPackage.CheckDeclaredSize(outSize, source.Length, "RefPack stream");

        var dest = new byte[outSize];
        int destPos = 0;

        try
        {
            Unpack(source, pos, dest, ref destPos);
        }
        catch (IndexOutOfRangeException)
        {
            // A command whose operand bytes are missing.
            throw new InvalidDataException("RefPack stream is truncated.");
        }

        if (destPos != dest.Length)
            throw new InvalidDataException($"RefPack stream ends after {destPos:N0} of {dest.Length:N0} bytes.");

        return dest;
    }

    private static void Unpack(byte[] source, int pos, byte[] dest, ref int destPos)
    {
        while (pos < source.Length)
        {
            int b0 = source[pos++];
            int literals, copyCount, offset;

            if (b0 < 0x80)
            {
                int b1 = source[pos++];
                literals  = b0 & 0x03;
                copyCount = ((b0 & 0x1C) >> 2) + 3;
                offset    = ((b0 & 0x60) << 3) + b1 + 1;
            }
            else if (b0 < 0xC0)
            {
                int b1 = source[pos++];
                int b2 = source[pos++];
                literals  = (b1 & 0xC0) >> 6;
                copyCount = (b0 & 0x3F) + 4;
                offset    = ((b1 & 0x3F) << 8) + b2 + 1;
            }
            else if (b0 < 0xE0)
            {
                int b1 = source[pos++];
                int b2 = source[pos++];
                int b3 = source[pos++];
                literals  = b0 & 0x03;
                copyCount = ((b0 & 0x0C) << 6) + b3 + 5;
                offset    = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
            }
            else if (b0 < 0xFC)
            {
                literals  = ((b0 & 0x1F) << 2) + 4;
                copyCount = 0;
                offset    = 0;
            }
            else
            {
                literals  = b0 & 0x03;
                copyCount = 0;
                offset    = 0;
            }

            CopyLiterals(source, ref pos, dest, ref destPos, literals);

            if (copyCount > 0)
            {
                int from = destPos - offset;
                if (from < 0)
                    throw new InvalidDataException("RefPack back-reference points before the start of the output.");
                // Deliberately byte-by-byte: runs are encoded as self-overlapping copies.
                for (int i = 0; i < copyCount && destPos < dest.Length; i++)
                    dest[destPos++] = dest[from++];
            }

            if (b0 >= 0xFC)
                break;
        }
    }

    private static void CopyLiterals(byte[] source, ref int pos, byte[] dest, ref int destPos, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (pos >= source.Length || destPos >= dest.Length)
                return;
            dest[destPos++] = source[pos++];
        }
    }
}
