using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Writes meta manipulations in the form Penumbra's temporary mod IPC reads (version 1, <c>META0001</c>): the version
/// byte and prefix, then a little-endian int32 count and the records of each kind in order (IMC, EQP, EQDP, EST, RSP,
/// GMP, global EQP, Atch, Shp, Atr), gzipped and base64 encoded. Only attach overrides are written; the other counts are 0.
/// </summary>
internal static class PenumbraMetaEncoder
{
    /// <summary>The bone name field of an attach entry: 35 bytes, NUL padded, then one byte with its length.</summary>
    private const int BoneBytes = 35;
    private const int KindsBeforeAtch = 7;

    /// <exception cref="InvalidDataException">A code isn't 3 ASCII characters, or a bone name doesn't fit.</exception>
    public static string Encode(IReadOnlyList<AtchManipulation> atch)
    {
        using var raw = new MemoryStream();
        raw.WriteByte(1);
        raw.Write("META0001"u8);
        for (var i = 0; i < KindsBeforeAtch; i++)
            Int32(raw, 0);
        Int32(raw, atch.Count);
        Span<byte> record = stackalloc byte[8 + BoneBytes + 1 + 7 * sizeof(float)];
        foreach (var m in atch)
        {
            if (m.Code is not { Length: 3 } code || code.Any(c => c is < '!' or > '~'))
                throw new InvalidDataException($"The attach point {m.Code} is not 3 characters.");
            var bone = Encoding.ASCII.GetBytes(m.Entry.Bone);
            if (bone.Length is 0 or > BoneBytes - 1)
                throw new InvalidDataException($"The bone name {m.Entry.Bone} is empty or too long for an attach override.");
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, ((uint)code[0] << 16) | ((uint)code[1] << 8) | code[2]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], m.GenderRace);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], checked((ushort)m.State));
            var entry = record[8..];
            bone.CopyTo(entry);
            entry[BoneBytes] = (byte)bone.Length;
            float[] values = [m.Entry.Scale, m.Entry.Offset.X, m.Entry.Offset.Y, m.Entry.Offset.Z, m.Entry.Rotation.X, m.Entry.Rotation.Y, m.Entry.Rotation.Z];
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(entry[(BoneBytes + 1 + 4 * i)..], values[i]);
            raw.Write(record);
        }
        Int32(raw, 0); // Shp
        Int32(raw, 0); // Atr

        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionMode.Compress, true))
            gzip.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return Convert.ToBase64String(zipped.GetBuffer(), 0, (int)zipped.Length);
    }

    private static void Int32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
