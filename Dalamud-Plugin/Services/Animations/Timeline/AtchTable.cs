using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>Where one attach point holds a prop in one state: the bone, and the scale, offset and rotation (radians) from it.</summary>
internal sealed record AtchEntry(string Bone, float Scale, Vector3 Offset, Vector3 Rotation);

/// <summary>
/// A character's attach-offset table (<c>chara/xls/attachOffset/cXXXX.atch</c>), which says where each held prop
/// sits on the body. Layout follows Penumbra.GameData's AtchFile: a u16 count of attach points and a u16 count of
/// states, the points' 3-character codes (each stored reversed and NUL ended in 4 bytes), a 32-byte bitfield, then
/// per point its states' 32-byte rows (i32 absolute offset of the NUL ended bone name, f32 scale, f32 offset XYZ,
/// f32 rotation XYZ in radians) and the bone names after them.
/// </summary>
internal sealed class AtchTable
{
    private const int HeaderSize = 4, BitfieldSize = 32, RowSize = 32;

    private readonly AtchEntry[] entries;
    private readonly Dictionary<string, int> points;

    /// <summary>How many states each attach point has (6 in the game's tables).</summary>
    public int States { get; }

    /// <summary>The attach points' codes as the game names them, three characters.</summary>
    public ImmutableArray<string> Codes { get; }

    private AtchTable(int states, ImmutableArray<string> codes, AtchEntry[] entries)
    {
        States = states;
        Codes = codes;
        this.entries = entries;
        points = new Dictionary<string, int>(codes.Length, StringComparer.Ordinal);
        for (var i = 0; i < codes.Length; i++)
            points.TryAdd(codes[i], i);
    }

    /// <summary>The game path of a race's table, from its race code (101 for c0101).</summary>
    public static string GamePath(int raceCode) => $"chara/xls/attachOffset/c{raceCode:D4}.atch";

    /// <summary>Whether two entries place a prop the same: the same bone and exactly the same numbers.</summary>
    public static bool SameEntry(AtchEntry a, AtchEntry b)
        => a.Bone == b.Bone && a.Scale == b.Scale && a.Offset == b.Offset && a.Rotation == b.Rotation;

    /// <summary>The row of an attach point <paramref name="code"/> in <paramref name="state"/>; null when the table has no such point or state.</summary>
    public AtchEntry? Entry(string code, int state)
        => state >= 0 && state < States && points.TryGetValue(code, out var point) ? entries[point * States + state] : null;

    /// <exception cref="InvalidDataException">The file is short, or a bone name lies outside it.</exception>
    public static AtchTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            throw new InvalidDataException("The attach table is shorter than its header.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes), states = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var rows = HeaderSize + 4L * count + BitfieldSize;
        var names = rows + (long)RowSize * count * states;
        if (names > bytes.Length)
            throw new InvalidDataException($"The attach table is {bytes.Length} bytes, short of the {names} its {count} points of {states} states need.");

        var codes = new string[count];
        for (var i = 0; i < count; i++)
        {
            var code = bytes.Slice(HeaderSize + 4 * i, 4);
            var length = code.IndexOf((byte)0);
            var text = Encoding.ASCII.GetString(code[..(length < 0 ? 4 : length)]).ToCharArray();
            Array.Reverse(text);
            codes[i] = new string(text);
        }

        var entries = new AtchEntry[count * states];
        var bones = new Dictionary<int, string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var row = bytes[(int)(rows + (long)RowSize * i)..];
            var at = BinaryPrimitives.ReadInt32LittleEndian(row);
            if (!bones.TryGetValue(at, out var bone))
            {
                if (at < names || at >= bytes.Length)
                    throw new InvalidDataException($"An attach table bone name lies at {at}, outside the {names}..{bytes.Length} that holds them.");
                var end = bytes[at..].IndexOf((byte)0);
                if (end < 0)
                    throw new InvalidDataException($"The attach table bone name at {at} never ends.");
                bones[at] = bone = Encoding.ASCII.GetString(bytes.Slice(at, end));
            }
            entries[i] = new AtchEntry(bone, Float(row, 1), new Vector3(Float(row, 2), Float(row, 3), Float(row, 4)),
                new Vector3(Float(row, 5), Float(row, 6), Float(row, 7)));
        }
        return new AtchTable(states, [.. codes], entries);
    }

    private static float Float(ReadOnlySpan<byte> row, int field) => BinaryPrimitives.ReadSingleLittleEndian(row[(4 * field)..]);
}
