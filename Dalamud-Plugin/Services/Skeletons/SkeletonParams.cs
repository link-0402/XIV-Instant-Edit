using System.Buffers.Binary;
using System.Text;

namespace InstantEdit.Services.Skeletons;

/// <summary> A look-at parameter: where its 56 bytes start in the file, and its gain (at +48). </summary>
internal sealed record LookAtParam(int Offset, float Gain)
{
    public int GainOffset => Offset + 48;
}

/// <summary> A bone a look-at group turns: where its 66 bytes start, its parameter, and its bone and parent names (32 bytes each, at +2 and +34). </summary>
internal sealed record LookAtElement(int Offset, byte ParamIndex, string Bone, string Parent)
{
    public int BoneOffset => Offset + 2;
    public int ParentOffset => Offset + 34;
}

internal sealed record LookAtGroup(string Id, IReadOnlyList<LookAtElement> Elements);

internal sealed record LookAtLayout(IReadOnlyList<LookAtParam> Params, IReadOnlyList<LookAtGroup> Groups);

/// <summary>
/// Reads the look-at section of a skeleton parameters file (<c>.skp</c>, magic <c>plks</c>): the bones the game turns
/// toward what a character looks at, in groups, each bone pointing at a parameter with a gain. Offsets, not copies,
/// so an edit can patch the bytes in place.
/// </summary>
internal static class SkeletonParams
{
    public const int NameBytes = 32;
    private const uint LookAtFlag = 0x2;
    private const int ParamBytes = 56, ElementBytes = 2 + 2 * NameBytes, GroupIdBytes = NameBytes;

    public static LookAtLayout ReadLookAt(ReadOnlySpan<byte> file)
    {
        // "sklp" stored reversed, as the game stores its four-character codes.
        if (file.Length < 0x20 || !file[..4].SequenceEqual("plks"u8))
            throw new InvalidDataException("This isn't a skeleton parameters file.");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(file[8..]) & LookAtFlag) == 0)
            throw new InvalidDataException("This skeleton has no head tracking.");
        var at = BinaryPrimitives.ReadInt32LittleEndian(file[0x10..]);
        Need(file, at, 2);
        int paramCount = file[at], groupCount = file[at + 1];
        at += 2;
        Need(file, at, groupCount);
        var counts = file.Slice(at, groupCount).ToArray();
        at += groupCount;

        var parameters = new List<LookAtParam>(paramCount);
        for (var i = 0; i < paramCount; i++, at += ParamBytes)
        {
            Need(file, at, ParamBytes);
            if (file[at + 52] != i)
                throw Damaged();
            parameters.Add(new LookAtParam(at, BinaryPrimitives.ReadSingleLittleEndian(file[(at + 48)..])));
        }

        var groups = new List<LookAtGroup>(groupCount);
        for (var g = 0; g < groupCount; g++)
        {
            Need(file, at, GroupIdBytes + 1);
            var id = Name(file.Slice(at, GroupIdBytes));
            if (file[at + GroupIdBytes] != counts[g])
                throw Damaged();
            at += GroupIdBytes + 1;
            var elements = new List<LookAtElement>(counts[g]);
            for (var e = 0; e < counts[g]; e++, at += ElementBytes)
            {
                Need(file, at, ElementBytes);
                var param = file[at + 1];
                if (param >= paramCount)
                    throw Damaged();
                elements.Add(new LookAtElement(at, param, Name(file.Slice(at + 2, NameBytes)), Name(file.Slice(at + 34, NameBytes))));
            }
            groups.Add(new LookAtGroup(id, elements));
        }
        return new LookAtLayout(parameters, groups);
    }

    /// <summary> The text before the first NUL: the game leaves junk after it in some names. </summary>
    private static string Name(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    private static void Need(ReadOnlySpan<byte> file, int at, int length)
    {
        if (at < 0 || length < 0 || at > file.Length - length)
            throw Damaged();
    }

    private static InvalidDataException Damaged() => new("The skeleton parameters file is damaged.");
}
