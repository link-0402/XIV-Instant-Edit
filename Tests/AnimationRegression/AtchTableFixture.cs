using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The attach-offset table (.atch) the game reads a held prop's place from, and the map of which attach point each
/// prop model uses.
/// </summary>
internal static class AtchTableFixture
{
    private const string GameFolder = @"E:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Synthetic(check, reject);
        Vanilla(check);
        Types(check);
    }

    private static void Synthetic(Action<bool, string> check, Action<Action, string> reject)
    {
        var bytes = Build();
        var table = AtchTable.Parse(bytes);
        check(table.States == 6 && table.Codes.SequenceEqual(["tsl", "gdr"]), "a table reads its states and its attach point codes, un-reversed");
        check(table.Entry("gdr", 4)?.Bone == "n_throw" && table.Entry("gdr", 0)?.Bone == "n_buki_r" && table.Entry("tsl", 5)?.Bone == "n_buki_l",
            "an entry is the point's row at the state, its bone name read from the pool");
        var entry = table.Entry("tsl", 1);
        check(entry is { Scale: 1.5f } && entry.Offset == new Vector3(.1f, .2f, .3f) && entry.Rotation == new Vector3(-.4f, -1.571f, .6f),
            "an entry carries its scale, offset and rotation in radians");
        check(table.Entry("x", 0) == null && table.Entry("tsl", 6) == null && table.Entry("tsl", -1) == null && table.Entry("TSL", 0) == null,
            "a point or state the table lacks has no entry");
        check(AtchTable.SameEntry(table.Entry("tsl", 0)!, table.Entry("tsl", 2)!) && !AtchTable.SameEntry(table.Entry("tsl", 0)!, table.Entry("tsl", 1)!) &&
              !AtchTable.SameEntry(table.Entry("tsl", 0)!, table.Entry("gdr", 4)!),
            "two entries are the same when bone, scale, offset and rotation are equal");
        check(AtchTable.GamePath(101) == "chara/xls/attachOffset/c0101.atch" && AtchTable.GamePath(1801) == "chara/xls/attachOffset/c1801.atch",
            "a race code names the table's game path");

        reject(() => AtchTable.Parse(bytes.AsSpan(0, bytes.Length - 3)), "a table cut inside its last bone name is refused");
        reject(() => AtchTable.Parse(bytes.AsSpan(0, 40)), "a table cut inside its header is refused");
        reject(() => AtchTable.Parse([]), "an empty table is refused");
        var wild = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(wild.AsSpan(4 + 8 + 32), wild.Length + 100);
        reject(() => AtchTable.Parse(wild), "a bone name offset past the end is refused");
        var none = (byte[])bytes.Clone();
        none[^1] = (byte)'x';
        reject(() => AtchTable.Parse(none), "a bone name that never ends is refused");
    }

    /// <summary>A table of two points (tsl, gdr) and six states, bone names in a pool after the entries.</summary>
    private static byte[] Build()
    {
        string[] codes = ["tsl", "gdr"];
        var pool = new MemoryStream();
        var offsets = new Dictionary<string, int>();
        var start = 4 + 4 * codes.Length + 32 + 32 * codes.Length * 6;
        int Bone(string name)
        {
            if (!offsets.TryGetValue(name, out var at))
            {
                at = offsets[name] = start + (int)pool.Length;
                pool.Write(Encoding.ASCII.GetBytes(name + "\0"));
            }
            return at;
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write((ushort)codes.Length);
            writer.Write((ushort)6);
            foreach (var code in codes)
                writer.Write(Encoding.ASCII.GetBytes(new string(code.Reverse().ToArray()) + "\0"));
            writer.Write(new byte[32]);
            foreach (var code in codes)
                for (var state = 0; state < 6; state++)
                {
                    var bone = code == "gdr" ? (state == 4 ? "n_throw" : "n_buki_r") : (state == 5 ? "n_buki_l" : "n_buki_r");
                    var (scale, offset, rotation) = code == "tsl" && state == 1 ? (1.5f, new Vector3(.1f, .2f, .3f), new Vector3(-.4f, -1.571f, .6f)) : (1f, Vector3.Zero, Vector3.Zero);
                    writer.Write(Bone(bone));
                    writer.Write(scale);
                    writer.Write(offset.X); writer.Write(offset.Y); writer.Write(offset.Z);
                    writer.Write(rotation.X); writer.Write(rotation.Y); writer.Write(rotation.Z);
                }
            writer.Write(pool.ToArray());
        }
        return stream.ToArray();
    }

    /// <summary>The character c0101's table in the installed game, when there is one.</summary>
    private static void Vanilla(Action<bool, string> check)
    {
        if (!Directory.Exists(GameFolder))
        {
            Console.WriteLine("[SKIP] atch: the game isn't installed where the checks look, so the vanilla table isn't read");
            return;
        }
        using var game = new Lumina.GameData(GameFolder, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var file = game.GetFile(AtchTable.GamePath(101));
        if (file?.Data is not { } data)
        {
            check(false, "the game has c0101's attach table");
            return;
        }
        var table = AtchTable.Parse(data);
        check(table.Codes.Length == 143 && table.States == 6, "c0101's table has 143 attach points of 6 states");
        check(table.Entry("gdr", 0)?.Bone == "n_buki_r" && table.Entry("gdr", 4)?.Bone == "n_throw", "c0101's summon point holds the weapon bone and throws from n_throw");
        check(table.Entry("lpr", 0) is { } ear && MathF.Abs(ear.Rotation.Y + 1.571f) < .001f, "c0101's ear wiggle point turns a quarter circle about Y");
        check(AtchTable.SameEntry(table.Entry("tsl", 0)!, table.Entry("tsl", 2)!), "c0101's mug point is the same drawn and in use");
    }

    private static void Types(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-prop-attach-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var types = new PropAttachTypes(temp);
            check(types.CodeOf(1962) == null && types.All.Count == 0, "a prop never learned has no attach type");
            types.Learn(1962, "tsl");
            types.Learn(1963, "wdm");
            types.Learn(1962, "tsl");
            var again = new PropAttachTypes(temp);
            check(again.CodeOf(1962) == "tsl" && again.CodeOf(1963) == "wdm" && again.CodeOf(1) == null && again.All.Count == 2,
                "what a prop was learned to attach with is kept for the next start");
            check(File.ReadAllText(Path.Combine(temp, "prop-attach-types.json")).Contains("\"1962\"") , "the types are kept as model number to code");
            types.Learn(1962, "gdr");
            check(new PropAttachTypes(temp).CodeOf(1962) == "gdr", "learning a prop again replaces its type");
            types.Learn(0, "tsl");
            types.Learn(5, "toolong");
            types.Learn(6, "");
            check(new PropAttachTypes(temp).All.Count == 2, "a model 0 or a code that isn't 3 characters is not kept");

            File.WriteAllText(Path.Combine(temp, "prop-attach-types.json"), "not json");
            string? warned = null;
            check(new PropAttachTypes(temp, message => warned = message).All.Count == 0 && warned != null, "an unreadable file starts empty and says so");
        }
        finally { Directory.Delete(temp, true); }
    }
}
