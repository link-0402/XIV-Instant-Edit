using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Studio.Services;

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
        Places(check);
    }

    private static TimelineEvent Event(string key, string magic, int start, int duration, string? group, params (string Name, double Value)[] values) =>
        new(key, magic, TimelineLane.Prop, start, duration, duration > 0, "", TimelineAccess.Movable, null, false,
            values.ToImmutableDictionary(v => v.Name, v => v.Value), group);

    private static TimelineDocument Placed(TimelineDocument doc, string key, TimelinePropPlace place) =>
        doc with { Events = [.. doc.Events.Select(e => e.Key == key ? e with { Place = place } : e)] };

    private static bool Throws(Action action, string text)
    {
        try { action(); }
        catch (InvalidDataException e) { return e.Message.Contains(text, StringComparison.Ordinal); }
        return false;
    }

    /// <summary>
    /// A prop's place: deltas against each race's own vanilla entry, written as attach overrides; the state the
    /// TMB picks; and the override as Penumbra's meta.json carries it.
    /// </summary>
    private static void Places(Action<bool, string> check)
    {
        // Race 101 holds the mug and the teacup at scale 1; race 801 (a Miqo'te) holds the mug at 0.85 but in use
        // (state 2) at 1, and the teacup on the back (j_sebo_a) but on the hand in use.
        var t101 = AtchTable.Parse(Build(["tsl", "tcs"], (code, state) => (code == "tcs" && state == 0 ? "j_sebo_a" : "n_buki_r", 1f, Vector3.Zero, Vector3.Zero)));
        var t801 = AtchTable.Parse(Build(["tsl", "tcs"], (code, state) =>
            (code == "tcs" && state != 2 ? "j_sebo_a" : "n_buki_r", state == 2 ? 1f : .85f, Vector3.Zero, Vector3.Zero)));
        AtchTable Tables(ushort race) => race == 801 ? t801 : t101;
        var types = new PropAttachTypes(null);
        types.Learn(1962, "tsl");
        types.Learn(1990, "tcs");
        ushort[] both = [101, 801];

        // Toast: the mug is a C043 to frame 60, then a C198 to 185, one prop.
        var toast = new TimelineDocument(185, 185, null,
        [
            Event("t3", "C043", 0, 60, "p3", ("Model", 1962), ("Body", 1), ("Variant", 1)),
            Event("t4", "C198", 60, 125, "p3", ("Model", 1962), ("Body", 1), ("Variant", 1), ("SummonId", 0), ("AtchState", 0)),
        ], 1);
        var up = new Vector3(0, .05f, 0);
        var moved = Placed(toast, "t4", new TimelinePropPlace(0, false, null, up, Vector3.Zero, 1));
        var shared = TimelinePropPlaces.Manipulations(moved, both, Tables, types);
        check(shared.Length == 2 && shared.All(m => m is { Code: "tsl", State: 0, Entry: { Bone: "n_buki_r", Offset: var o, Rotation: var r } } && o == up && r == Vector3.Zero) &&
              shared[0] is { GenderRace: 101, Entry.Scale: 1f } && shared[1] is { GenderRace: 801, Entry.Scale: .85f },
            "a moved prop writes one override per race, each moved from that race's own entry");
        check(TimelinePropPlaces.Manipulations(Placed(toast, "t4", new TimelinePropPlace(0, false, null, Vector3.Zero, Vector3.Zero, 1)), both, Tables, types).IsEmpty,
            "a place that changes nothing writes no override");
        check(Throws(() => TimelinePropPlaces.Manipulations(moved, both, Tables, new PropAttachTypes(null)), "Preview once to place 1962"),
            "a prop whose attach point was never seen asks for a preview");
        check(TimelinePropPlaces.Manipulations(moved, [801], Tables, types) is [{ GenderRace: 801, Entry.Scale: .85f }],
            "a clip rebuilt for another race moves that race's own entry");

        // The state the TMB picks now: the C198's own, else a C014 or C174 of the group on its slot, else 0.
        TimelineEvent Mug(int atch) => Event("t4", "C198", 60, 125, "p3", ("Model", 1962), ("SummonId", 0), ("AtchState", atch));
        TimelineDocument Doc(params TimelineEvent[] events) => new(185, 185, null, [.. events], 1);
        check(TimelinePropPlaces.CurrentState(Doc(Mug(4)), Mug(4)) == 3, "a C198 attach state names the state, one up");
        var switched = Doc(Mug(0), Event("t5", "C014", 61, 0, "p3", ("Enabled", 1), ("ObjectPosition", 3), ("ObjectControl", 2)));
        check(TimelinePropPlaces.CurrentState(switched, Mug(0)) == 3, "at Default a C014 of the group on the prop's slot names the state");
        var stowed = Doc(Mug(0), Event("t5", "C174", 61, 1, "p3", ("ObjectPosition", 0), ("ObjectControl", 2)));
        check(TimelinePropPlaces.CurrentState(stowed, Mug(0)) == 1, "a C014 or C174 position 0 is Stowed, attach state 1");
        check(TimelinePropPlaces.CurrentState(Doc(Mug(0)), Mug(0)) == 0, "at Default with nothing else the prop is in state 0");

        // Only this animation: state 2 becomes a moved copy of the current state's place.
        var own = Placed(toast, "t4", new TimelinePropPlace(0, true, null, up, Vector3.Zero, 1));
        check(TimelinePropPlaces.Manipulations(own, both, Tables, types) is [{ GenderRace: 101, State: 2 }, { GenderRace: 801, State: 2, Entry.Scale: .85f }],
            "only this animation writes attach state 2");
        check(TimelinePropPlaces.Manipulations(Placed(toast, "t4", new TimelinePropPlace(0, true, null, Vector3.Zero, Vector3.Zero, 1)), both, Tables, types)
                is [{ GenderRace: 801, State: 2, Entry: { Scale: .85f, Bone: "n_buki_r" } }],
            "unmoved, only a race whose state 2 differs from today's place needs an override");
        var cup = Doc(Event("t4", "C198", 60, 125, "p3", ("Model", 1990), ("SummonId", 0), ("AtchState", 0)));
        check(TimelinePropPlaces.Manipulations(Placed(cup, "t4", new TimelinePropPlace(0, true, null, up, Vector3.Zero, 1.2f)), [801], Tables, types)
                is [{ State: 2, Entry: { Bone: "j_sebo_a", Scale: var size } }] && size == .85f * 1.2f,
            "state 2 takes the current place's bone and scale, never its own");
        var ownState = TimelinePropPlaces.ApplyState(own, own.Events[1]);
        check(ownState.Events.Single(e => e.Key == "t4").Values["AtchState"] == 3, "only this animation sets the C198 to attach state 2");
        var sharedState = TimelinePropPlaces.ApplyState(moved, moved.Events[1]);
        check(sharedState.Events.Single(e => e.Key == "t4").Values["AtchState"] == 1, "a shared place writes the state it edits, Default becoming state 0");

        // A C043 summon has no attach state: a C014 at spawn + 1 picks it, replacing the group's one on its slot.
        var weapon = new TimelineDocument(100, 100, null,
        [
            Event("t0", "C043", 10, 60, "p0", ("Model", 1962), ("Body", 1), ("Variant", 1)),
            Event("t1", "C014", 11, 0, "p0", ("Enabled", 1), ("ObjectPosition", 3), ("ObjectControl", 2)),
            Event("t2", "C014", 12, 0, null, ("Enabled", 1), ("ObjectPosition", 4), ("ObjectControl", 0)),
            Event("t6", "C014", 60, 0, "p0", ("Enabled", 1), ("ObjectPosition", 0), ("ObjectControl", 2)),
        ], 5);
        var inUse = TimelinePropPlaces.ApplyState(Placed(weapon, "t0", new TimelinePropPlace(2, false, null, up, Vector3.Zero, 1)), weapon.Events[0]);
        var c014 = inUse.Events.Where(e => e.Magic == "C014" && e.Values["ObjectControl"] == 2 && e.Start < 60).ToArray();
        check(c014 is [{ Key: "n5", Group: "p0", Start: 11 } added] && added.Values["Enabled"] == 1 && added.Values["ObjectPosition"] == 2 &&
              inUse.NextKey == 6 && inUse.Events.Any(e => e.Key == "t2"),
            "a C043 prop gets a C014 at spawn + 1 on its slot, in place of the group's own");
        var lateOnly = weapon with { Events = [.. weapon.Events.Where(e => e.Key != "t1")] };
        check(inUse.Events.Single(e => e.Key == "t6") == weapon.Events[3] &&
              TimelinePropPlaces.ApplyState(Placed(lateOnly, "t0", new TimelinePropPlace(2, false, null, up, Vector3.Zero, 1)), weapon.Events[0])
                  .Events.Count(e => e.Key == "t6" || e.Magic == "C014" && e.Start == 11) == 2,
            "a later C014 that stows the prop is kept as it was");
        var drawn = TimelinePropPlaces.ApplyState(Placed(weapon, "t0", new TimelinePropPlace(0, false, null, up, Vector3.Zero, 1)), weapon.Events[0]);
        check(drawn.Events.Single(e => e.Magic == "C014" && e.Values["ObjectControl"] == 2 && e.Start == 11).Values["ObjectPosition"] == 1,
            "attach state 0 is written as position 1, Drawn");
        var twice = new TimelineDocument(100, 100, null,
        [
            Event("t0", "C043", 0, 30, "p0", ("Model", 1962)) with { Place = new TimelinePropPlace(2, false, null, up, Vector3.Zero, 1) },
            Event("t7", "C043", 30, 30, "p0", ("Model", 1962)) with { Place = new TimelinePropPlace(3, false, null, up, Vector3.Zero, 1) },
        ], 1);
        check(TimelinePropPlaces.ApplyStates(twice).Events.Where(e => e.Magic == "C014").Select(e => (e.Start, (int)e.Values["ObjectPosition"]))
                .OrderBy(e => e.Start).SequenceEqual([(1, 2), (31, 3)]),
            "two placed summons of one prop each get their own C014 at their own spawn");
        check(TimelinePropPlaces.ApplyState(weapon, weapon.Events[0]) == weapon, "a prop without a place keeps its state");

        check(AnimationEditService.PropRaces("chara/human/c0101/animation/a0001/bt_common/emote/toast.pap", null, "c0801").SequenceEqual(new ushort[] { 101, 801 }) &&
              AnimationEditService.PropRaces("chara/human/c0101/animation/a0001/bt_common/emote/toast.pap", null, null).SequenceEqual(new ushort[] { 101 }),
            "a prop is placed for the file's race, and for the race a rebuild makes it for");

        Meta(check);
    }

    /// <summary>The override as Penumbra's meta.json carries it.</summary>
    private static void Meta(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-atch-meta-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var defaults = new ModCarrier();
            defaults.Atch.Add(new AtchManipulation(801, "tsl", 2, new AtchEntry("n_buki_r", .85f, new Vector3(0, .05f, 0), Vector3.Zero)));
            ModMetaWriter.Write(temp, "Atch", defaults, Array.Empty<ModGroupOut>());
            const string expected = "{\"Type\":\"Atch\",\"Manipulation\":{\"Entry\":{\"Bone\":\"n_buki_r\",\"Scale\":0.85,\"OffsetX\":0,\"OffsetY\":0.05,\"OffsetZ\":0," +
                                    "\"RotationX\":0,\"RotationY\":0,\"RotationZ\":0},\"Gender\":\"Female\",\"Race\":\"Miqote\",\"Type\":\"tsl\",\"Index\":2}}";
            var written = JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "meta.json")))!["DefaultData"]!["Manipulations"]!.AsArray();
            check(written.Count == 1 && written[0]!.ToJsonString() == expected && ModMetaWriter.AtchJson(defaults.Atch[0]) == expected,
                "an attach override is written as Penumbra's Atch manipulation");
        }
        finally { Directory.Delete(temp, true); }
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
    private static byte[] Build() => Build(["tsl", "gdr"], (code, state) =>
    {
        var bone = code == "gdr" ? (state == 4 ? "n_throw" : "n_buki_r") : (state == 5 ? "n_buki_l" : "n_buki_r");
        var (scale, offset, rotation) = code == "tsl" && state == 1 ? (1.5f, new Vector3(.1f, .2f, .3f), new Vector3(-.4f, -1.571f, .6f)) : (1f, Vector3.Zero, Vector3.Zero);
        return (bone, scale, offset, rotation);
    });

    /// <summary>A table of <paramref name="codes"/> and six states, each row from <paramref name="row"/>, bone names in a pool after the entries.</summary>
    private static byte[] Build(string[] codes, Func<string, int, (string Bone, float Scale, Vector3 Offset, Vector3 Rotation)> row)
    {
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
                    var (bone, scale, offset, rotation) = row(code, state);
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
