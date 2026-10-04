using System.Buffers.Binary;
using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Walks an SCD track: a list of commands, each a 16-bit type and a payload whose size the type
/// fixes or the payload states. Sizes follow VFXEditor's ScdTrackItem data classes; a 2026-10-02
/// survey walked every track of 8,592 vanilla SCDs with them. Dalamud-free.
/// </summary>
internal static class ScdTrack
{
    public const int End = 0x00;
    public const int Volume = 0x01;
    public const int Interval = 0x03;
    public const int KeyOn = 0x07;
    public const int KeyOff = 0x0C;
    public const int LoopStart = 0x0D;
    public const int LoopEnd = 0x0E;
    public const int EndForLoop = 0x10;
    public const int Jump = 0x25;
    public const int MidiEnd = 0x35;
    private const int MaxCommands = 4096;

    /// <summary>One command: where its type field sits and how many payload bytes follow it.</summary>
    public readonly record struct Command(int Type, int Offset, int Length)
    {
        public int Data => Offset + 2;
        public int Next => Data + Length;
    }

    public static ImmutableArray<Command> Read(ReadOnlySpan<byte> bytes, int offset)
    {
        var commands = ImmutableArray.CreateBuilder<Command>();
        var at = offset;
        while (true)
        {
            if (commands.Count >= MaxCommands)
                throw new InvalidDataException("An SCD track never ends.");
            var type = U16(bytes, at);
            var data = at + 2;
            var length = type switch
            {
                End or 0x07 or 0x0C or 0x0E or 0x10 or 0x24 or 0x26 or 0x28 or 0x29 or 0x2A or 0x2D or 0x2E or 0x31 or 0x34 or 0x35 or 0x36 => 0,
                0x2F => 2,
                0x03 or 0x05 or 0x11 or 0x13 or 0x15 or 0x17 or 0x1D or 0x20 or 0x30 or 0x3C or 0x3E => 4,
                0x01 or 0x02 or 0x06 or 0x08 or 0x09 or 0x0A or 0x0D or 0x12 or 0x14 or 0x16 or 0x18 or 0x19 or 0x1A or 0x1B or 0x1C
                    or 0x1E or 0x1F or 0x21 or 0x22 or 0x23 or 0x25 or 0x27 or 0x32 or 0x33 => 8,
                0x04 or 0x37 or 0x38 => 12,
                0x2B or 0x3B => 16,
                // External audio: bank and index, then that many random choices when the index is negative.
                0x0F or 0x2C => 4 + Math.Max(0, -(short)U16(bytes, data + 2)) * 2,
                // Analysis flags: a count of 16-bit values.
                0x39 => 2 + (short)U16(bytes, data) * 2,
                // Config: type and count, then one value for float intervals or count values beyond that.
                0x3A => 4 + (U16(bytes, data) == 1 ? 2 : U16(bytes, data) > 1 ? U16(bytes, data + 2) * 2 : 0),
                0x3D => ZeroOne(bytes, data),
                0x3F => ChannelZeroOne(bytes, data),
                // Unknown 64: version, count, a 16-bit field, then 12-byte items.
                0x40 => 4 + Byte(bytes, data + 1) * 12,
                _ => throw new InvalidDataException($"An SCD track uses unknown command 0x{type:X2}."),
            };
            if (length < 0 || data + length > bytes.Length)
                throw new InvalidDataException("An SCD track runs past the end of the file.");
            commands.Add(new Command(type, at, length));
            if (type is End or EndForLoop or MidiEnd)
                return commands.ToImmutable();
            at = data + length;
        }
    }

    /// <summary>Volume 0-1 points: version, reserve, header size, count, then 4-byte points.</summary>
    private static int ZeroOne(ReadOnlySpan<byte> bytes, int at) => 6 + (short)U16(bytes, at + 4) * 4;

    private static int ChannelZeroOne(ReadOnlySpan<byte> bytes, int at)
    {
        var count = (short)U16(bytes, at + 4);
        var length = 6;
        for (var i = 0; i < count; i++)
        {
            var channel = ZeroOne(bytes, at + length);
            if (channel < 6)
                return -1;
            length += channel;
        }
        return length;
    }

    private static int U16(ReadOnlySpan<byte> bytes, int at)
        => at >= 0 && at + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]) : throw new InvalidDataException("An SCD track runs past the end of the file.");

    private static int Byte(ReadOnlySpan<byte> bytes, int at)
        => at >= 0 && at < bytes.Length ? bytes[at] : throw new InvalidDataException("An SCD track runs past the end of the file.");
}
