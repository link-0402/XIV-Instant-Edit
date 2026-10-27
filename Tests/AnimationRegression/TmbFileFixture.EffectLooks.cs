using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

internal static partial class TmbFileFixture
{
    private const string GlowPath = "vfx/mod/eff/glow.avfx";

    /// <summary>
    /// The Timeline's effect editor: an effect's whole-effect values and colours as read, a look written
    /// in place (only the numbers it changes, each part's colours once), and saving looks as copies of
    /// their effects, mapped beside them, in place of the paths the timeline names.
    /// </summary>
    private static void EffectLooks(Action<bool, string> check, Action<Action, string> reject)
    {
        var effect = LookEffect();
        var look = TimelineEffectLooks.Read(effect);
        var expected = new (Vector3 Rgb, int Uses)[]
        {
            (new(1, .5f, 0), 2), (Vector3.Zero, 2), (new(.2f, .4f, .6f), 1), (new(0, 0, 1), 1), (new(1, 0, 0), 1), (Vector3.One, 1), (new(1, .8f, .6f), 1),
        };
        check(look.Tint == new Vector3(1, 1, 1.5f) && look.Size == Vector3.One && look.Offset == Vector3.Zero && look.Turn == Vector3.Zero &&
              look.Colors.Length == expected.Length &&
              look.Colors.Zip(expected).All(c => c.First.Rgb == TimelineEffectLooks.Swatch(c.Second.Rgb) && c.First.Uses == c.Second.Uses) &&
              look is { Glow: true, Opacity: true, LightRange: true, Bound: false, BinderPositions.IsEmpty: true },
            "an effect's look reads its root tint, size, offset and turn, and its colours most used first: no white emitter colour, " +
            "simple colours only when used, light colours but none of a blur; it is not on a binder");
        Binders(check);
        Moving(check);
        var bare = Chunk("AVFX", Chunk("Ver", Ints(1)));
        check(TimelineEffectLooks.Read(bare) is { Tint: null, Size: null, Colors.IsEmpty: true, Glow: false, Opacity: false, LightRange: false } &&
              TimelineEffectLooks.Apply(bare, new TimelineEffectEdit { Tint = Vector3.One, Hue = 90, Glow = 2 }).SequenceEqual(bare),
            "an effect without the fields has nothing to show and nothing to change");
        reject(() => TimelineEffectLooks.Read([1, 2, 3, 4, 5, 6, 7, 8]), "a file that is not an effect is refused");

        check(TimelineEffectLooks.Apply(effect, new TimelineEffectEdit()).SequenceEqual(effect) && new TimelineEffectEdit().IsEmpty,
            "an empty look changes no byte");
        var sized = TimelineEffectLooks.Apply(effect, new TimelineEffectEdit { Size = new Vector3(.5f) });
        var sizeFields = new[] { "RvSx", "RvSy", "RvSz" }.SelectMany(name => Found(effect, name)).ToList();
        check(sized.Length == effect.Length && Differing(effect, sized).All(i => sizeFields.Any(f => i >= f.At && i < f.At + 4)) &&
              TimelineEffectLooks.Read(sized).Size == new Vector3(.5f),
            "a new size changes only the root size fields");

        var edit = new TimelineEffectEdit
        {
            Tint = new Vector3(2, 1, 1), Size = new Vector3(.5f), Move = new Vector3(0, 1, 0), Turn = new Vector3(0, MathF.PI / 2, 0), Hue = 180,
            Colors = [(TimelineEffectLooks.Swatch(new Vector3(0, 0, 1)), new Vector3(0, 1, 0))], Glow = 2, Opacity = .5f, LightRange = .5f,
        };
        var edited = TimelineEffectLooks.Apply(effect, edit);
        var after = TimelineEffectLooks.Read(edited);
        check(edited.Length == effect.Length && after.Tint == new Vector3(2, 1, 1) && after.Size == new Vector3(.5f) &&
              after.Offset == new Vector3(0, 1, 0) && after.Turn is { } turn && Vector3.Distance(turn, new Vector3(0, MathF.PI / 2, 0)) < 1e-6f &&
              Found(edited, "RvRy") is [(var turnY, 4)] && Math.Abs(BitConverter.ToSingle(edited, turnY) - MathF.PI / 2) < 1e-6f,
            "a look writes the whole effect's tint, and scales, moves and turns it from its own values, the turn in radians");
        check(TimelineEffectLooks.Read(TimelineEffectLooks.Apply(effect, new TimelineEffectEdit { Turn = new Vector3(10, -10, 1) })).Turn ==
              new Vector3(2 * MathF.PI, -2 * MathF.PI, 1) && TimelineEffectEdit.MaxTurn == 2 * MathF.PI,
            "a turn is held to one full turn either way, in radians");
        check(Near(Colours(edited, "Ptcl", "Col", "RGB"), [new(0, .5f, 1), new(0, 1, 0)]) &&
              Near(Colours(edited, "Ptcl", "Data", "ColB", "RGB"), [new(0, .5f, 1)]) &&
              Near(Colours(edited, "Emit", "Col", "RGB"), [Vector3.One, new(.6f, .4f, .2f)]) &&
              Near(Colours(edited, "Efct", "Data", "Col", "RGB"), [new(.6f, .8f, 1), new(.3f, .1f, .1f)]) &&
              Found(edited, "Ptcl", "Smpl", "Cols") is [var (cols, _), var (unused, _)] &&
              edited.AsSpan(cols, 8).SequenceEqual(new byte[] { 0, 255, 255, 200, 255, 255, 255, 255 }) &&
              edited.AsSpan(unused, 16).SequenceEqual(effect.AsSpan(unused, 16)),
            "the hue turns every colour once, a swapped colour takes its new one; white emitters, alpha bytes, " +
            "unused simple colours and blurs stay");
        check(Near(Values(edited, "Ptcl", "Col", "A"), [.4f]) && Near(Values(edited, "Ptcl", "Data", "ColB", "A"), [.25f]) &&
              Near(Values(edited, "Emit", "Col", "A"), [.5f]) &&
              Near(Values(edited, "Ptcl", "Col", "Bri"), [4]) && Near(Values(edited, "Emit", "Col", "Bri"), [3]) &&
              Near(Values(edited, "Efct", "Data", "Col", "Bri"), [3]) &&
              Near(Values(edited, "Efct", "Data", "DstS"), [3, 9]),
            "glow and opacity scale each particle's main curve once (not its emitter's or other colours'), glow the light too, " +
            "light range only a point light's");
        check(Values(edited, "Ptcl", "Col", "A") is [var alpha] && Keys(edited, "Ptcl", "Col", "A").Single() is var key &&
              BitConverter.ToSingle(edited, key + 4) == 1 && BitConverter.ToSingle(edited, key + 8) == 1 && alpha > 0,
            "a single-value key changes only its value, the third float");

        var hdr = new Vector3(2, 1, 0);
        check(TimelineEffectLooks.Shift(new Vector3(.5f), 90, 1) == new Vector3(.5f) &&
              Near([TimelineEffectLooks.Shift(hdr, 0, 0)], [new Vector3(2)]) &&
              Near([TimelineEffectLooks.Shift(new Vector3(1, 0, 0), 120, 1)], [new Vector3(0, 1, 0)]) &&
              Near([TimelineEffectLooks.Shift(new Vector3(.9f, .3f, .1f), 360, 1)], [new Vector3(.9f, .3f, .1f)]) &&
              Near([TimelineEffectLooks.Shift(hdr, -60, 1)], [new Vector3(2, 0, 1)]),
            "a hue shift keeps a colour's brightness, glows above 1 too; greys have no hue to turn");
        var swap = new TimelineEffectEdit().WithSwap(Vector3.One, Vector3.UnitX);
        check(swap.Equals(new TimelineEffectEdit { Colors = [(Vector3.One, Vector3.UnitX)] }) && !swap.IsEmpty &&
              swap.WithSwap(Vector3.One, null).IsEmpty && swap.WithSwap(Vector3.One, Vector3.UnitY).Colors.Single().To == Vector3.UnitY &&
              swap.Recolor(new Vector3(1.0001f, 1, 1)) == Vector3.UnitX,
            "a swap matches its swatch, replaces an earlier one and goes away again");
        check(TimelineEffectLooks.Apply(effect, new TimelineEffectEdit { Tint = new Vector3(float.NaN, 99, -1), Glow = float.PositiveInfinity })
                  is var unsafeEdit && TimelineEffectLooks.Read(unsafeEdit).Tint == new Vector3(0, TimelineEffectEdit.MaxTint, 0) &&
              Near(Values(unsafeEdit, "Ptcl", "Col", "Bri"), [2]),
            "values out of range are clamped and invalid ones ignored");
        static byte[] Key(params float[] values) => Chunk("Keys", [.. BitConverter.GetBytes((short)0), .. BitConverter.GetBytes((short)1), .. Floats(values)]);
        var extreme = Chunk("AVFX", Chunk("Ptcl", Chunk("Col", [.. Chunk("RGB", Key(3e38f, 0, -3e38f)), .. Chunk("A", Key(1, 1, 3e38f))])));
        check(TimelineEffectLooks.Apply(extreme, new TimelineEffectEdit { Saturation = 0, Opacity = 2 }).SequenceEqual(extreme),
            "a colour or value that a change would push past a float's range stays as it is");

        Saving(check, reject, effect, edit);
    }

    /// <summary>
    /// Effects on a binder: bound when a timeline or one of its items names a binder (each Item holds
    /// the items before it too, so the last is read), with the positions of the binders' property
    /// blocks as their Pos curves give them.
    /// </summary>
    private static void Binders(Action<bool, string> check)
    {
        static byte[] Leaf(string name, int value) => BinderEffects.Leaf(name, value);
        static byte[] Entry(int binder) => BinderEffects.Entry(binder);
        static byte[] Timeline(int binder, params byte[][] items) => BinderEffects.Timeline(binder, items);
        static byte[] Axis(string name, params float[] values) => BinderEffects.Axis(name, values);
        static byte[] Pos(float[] x, float[] y, float[] z) => BinderEffects.Pos(x, y, z);
        static byte[] Effect(params byte[][] parts) => BinderEffects.Effect(parts);

        var bound = Effect(Timeline(-1, Entry(0)), Chunk("Bind", [.. Leaf("BnVr", 1), .. Chunk("PrpS", Pos([1], [2], [3]))]));
        check(TimelineEffectLooks.Read(bound) is { Bound: true, BinderPositions: [{ Binder: 0, Block: "PrpS", Constant: { X: 1, Y: 2, Z: 3 }, Keys: 1 }] },
            "an effect whose timeline item uses a binder is bound, and reads the binder's position from its one key");

        var several = Effect(Timeline(2), Chunk("Bind", Leaf("BnVr", 1)),
            Chunk("Bind", [.. Chunk("Prp1", Pos([1, 5], [2], [])), .. Chunk("Prp2", Leaf("BnNo", 0)),
                .. Chunk("PrpG", Chunk("Pos", [.. Leaf("ACT", 0), .. Axis("Y", 5), .. Axis("Z", 6)]))]));
        check(TimelineEffectLooks.Read(several) is
              {
                  Bound: true,
                  BinderPositions: [{ Binder: 1, Block: "Prp1", Constant: null, Keys: 2 }, { Binder: 1, Block: "PrpG", Constant: { X: 0, Y: 5, Z: 6 }, Keys: 1 }],
              },
            "a timeline without items that names a binder is bound; binders without property blocks, and blocks without a position, " +
            "list none; a moving position has no constant; an axis the file leaves out is 0");

        var unbound = Effect(Timeline(-1, Entry(-1), [.. Entry(-1), .. Entry(-1)]), Timeline(-1),
            Chunk("Bind", Chunk("PrpS", Pos([], [], []))));
        check(TimelineEffectLooks.Read(unbound) is { Bound: false, BinderPositions: [{ Binder: 0, Block: "PrpS", Constant: null, Keys: 0 }] },
            "an effect whose timelines and items name no binder is not bound, though its binders are still read; a position without keys has no constant");
        var lastItem = Effect(Timeline(-1, Entry(-1), [.. Entry(-1), .. Entry(0)]));
        check(TimelineEffectLooks.Read(lastItem).Bound,
            "the last Item of a timeline is the one read: an item added there that names a binder makes the effect bound");
    }

    /// <summary>
    /// Move on an effect attached to a binder: added to the keys of its property blocks' Pos curves,
    /// with a curve (or a key) written in as VFXEditor writes a new one where an axis has none, so
    /// the file grows by just those. Turn and size can't change an attached effect; an unattached
    /// effect moves, turns and scales by its root fields.
    /// </summary>
    private static void Moving(Action<bool, string> check)
    {
        const string attached = "Turn and size can't change an attached effect";
        const string noPosition = "This effect can't be moved: its attach point has no position.";
        static byte[] Leaf(string name, int value) => BinderEffects.Leaf(name, value);
        static string? Thrown(Action action)
        {
            try { action(); return null; }
            catch (InvalidDataException e) { return e.Message; }
        }
        var up = new TimelineEffectEdit { Move = new Vector3(0, 1, 0) };
        var timeline = BinderEffects.Timeline(-1, BinderEffects.Entry(0));

        var bound = BinderEffects.Effect(timeline, Chunk("Bind", [.. Leaf("BnVr", 1), .. Chunk("PrpS", BinderEffects.Pos([1], [2], [3]))]));
        var lifted = TimelineEffectLooks.Apply(bound, up);
        var keyY = Keys(bound, "Bind", "PrpS", "Pos", "Y").Single();
        check(lifted.Length == bound.Length && Values(lifted, "Bind", "PrpS", "Pos", "Y") is [3] &&
              Values(lifted, "Bind", "PrpS", "Pos", "X") is [1] && Values(lifted, "Bind", "PrpS", "Pos", "Z") is [3] &&
              Differing(bound, lifted).All(i => i >= keyY + 12 && i < keyY + 16) &&
              Found(lifted, "RvPy") is [(var rvPy, 4)] && BitConverter.ToSingle(lifted, rvPy) == 0,
            "a move on an attached effect adds to its binder position's keys, not to its root offset");
        var moving = BinderEffects.Effect(timeline, Chunk("Bind", Chunk("PrpS", BinderEffects.Pos([1], [2, 4], [3]))));
        check(TimelineEffectLooks.Apply(moving, up) is var movedKeys && movedKeys.Length == moving.Length &&
              Values(movedKeys, "Bind", "PrpS", "Pos", "Y") is [3, 5],
            "a moving binder position moves at every key");

        // No X curve, a Y curve without keys, a Z curve with one key at 0.
        var sparse = BinderEffects.Effect(timeline,
            Chunk("Bind", [.. Leaf("BnVr", 1), .. Chunk("PrpS", [.. Leaf("BPID", 3),
                .. Chunk("Pos", [.. Leaf("ACT", 0), .. Leaf("ACTR", 0), .. BinderEffects.Axis("Y"), .. BinderEffects.Axis("Z", 0)])])]),
            BinderEffects.Timeline(-1));
        var placed = TimelineEffectLooks.Apply(sparse, new TimelineEffectEdit { Move = new Vector3(1, 2, 3) });
        var (oldPos, oldSize) = Found(sparse, "Bind", "PrpS", "Pos").Single();
        var (newPos, newSize) = Found(placed, "Bind", "PrpS", "Pos").Single();
        var grown = placed.Length - sparse.Length;
        int[] sizeFields = [4, Found(sparse, "Bind").Single().At - 4, Found(sparse, "Bind", "PrpS").Single().At - 4, oldPos - 4];
        check(TimelineEffectLooks.Read(placed) is { BinderPositions: [{ Block: "PrpS", Keys: 1, Constant: { X: 1, Y: 2, Z: 3 } }] } &&
              grown == 96 && BitConverter.ToInt32(placed, 4) == placed.Length - 8 && newPos == oldPos && newSize == oldSize + grown &&
              Differing(sparse.AsSpan(0, oldPos).ToArray(), placed.AsSpan(0, oldPos).ToArray()).All(i => sizeFields.Any(f => i >= f && i < f + 4)) &&
              sizeFields.All(f => BitConverter.ToInt32(placed, f) == BitConverter.ToInt32(sparse, f) + grown) &&
              sparse.AsSpan(oldPos + oldSize).SequenceEqual(placed.AsSpan(newPos + newSize)),
            "a binder position without an X curve or Y keys gets them, and only it and the sizes around it change");
        static byte[] NewKey(float value) => [.. BitConverter.GetBytes((short)0), .. BitConverter.GetBytes((short)1), .. Floats(1, 1, value)];
        var children = AnimationDependencies.Chunks(placed, newPos, newPos + newSize).ToList();
        check(children.Select(c => c.Name).SequenceEqual(["ACT", "ACTR", "X", "Y", "Z"]) &&
              placed.AsSpan(children[2].Start - 8, children[2].Size + 8).SequenceEqual(BinderEffects.NewCurve("X", 1)) &&
              Found(placed, "Bind", "PrpS", "Pos", "Y", "Keys") is [(var yKeys, 16)] && placed.AsSpan(yKeys, 16).SequenceEqual(NewKey(2)) &&
              Found(placed, "Bind", "PrpS", "Pos", "Y", "KeyC") is [(var yCount, 4)] && BitConverter.ToInt32(placed, yCount) == 1,
            "a new axis curve goes in its place among the others and is written as VFXEditor writes one: key count, behaviours, " +
            "random type, then one linear key at frame 0");

        var noBlocks = BinderEffects.Effect(timeline, Chunk("Bind", Leaf("BnVr", 1)));
        check(TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(noBlocks), up) == noPosition &&
              Thrown(() => TimelineEffectLooks.Apply(noBlocks, up)) == noPosition &&
              TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(bound), up) == null,
            "an attached effect whose binder has no position can't be moved");
        var turn = new TimelineEffectEdit { Turn = new Vector3(0, 1, 0) };
        var size = new TimelineEffectEdit { Size = new Vector3(2) };
        check(TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(bound), turn) == attached &&
              TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(bound), size with { Move = up.Move }) == attached &&
              Thrown(() => TimelineEffectLooks.Apply(bound, turn)) == attached && Thrown(() => TimelineEffectLooks.Apply(bound, size)) == attached &&
              TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(LookEffect()), turn) == null,
            "turn and size are refused on an attached effect");

        var effect = LookEffect();
        var rvPyAt = Found(effect, "RvPy").Single().At;
        var shifted = TimelineEffectLooks.Apply(effect, up);
        check(shifted.Length == effect.Length && BitConverter.ToSingle(shifted, rvPyAt) == 1 &&
              Differing(effect, shifted).All(i => i >= rvPyAt && i < rvPyAt + 4) &&
              TimelineEffectLooks.Read(TimelineEffectLooks.Apply(shifted, up)).Offset == new Vector3(0, 2, 0),
            "a move on an unattached effect adds to its root offset, so a moved copy moves on");
        var still = new TimelineEffectEdit { Move = Vector3.Zero, Turn = Vector3.Zero, Size = Vector3.One };
        check(TimelineEffectLooks.Apply(bound, new TimelineEffectEdit()).SequenceEqual(bound) && TimelineEffectLooks.Apply(bound, still).SequenceEqual(bound) &&
              TimelineEffectLooks.Apply(sparse, still).SequenceEqual(sparse) && TimelineEffectLooks.Apply(effect, still).SequenceEqual(effect) &&
              still.IsEmpty && TimelineEffectLooks.MoveRefusal(TimelineEffectLooks.Read(noBlocks), still) == null,
            "no move, no turn and a size of 1 change no byte, on an attached effect too");
        LibraryMoves(check);
    }

    /// <summary>
    /// Real effects from 25 sampled mod folders of the library at G:\Penumbra, when it is there: each
    /// reads, an empty look changes no byte, and a move either is refused for want of a position or
    /// changes only binder positions (or the root offset). The curves a move adds are laid out as the
    /// library's own binder position curves are.
    /// </summary>
    private static void LibraryMoves(Action<bool, string> check)
    {
        const string library = @"G:\Penumbra";
        if (!Directory.Exists(library))
        {
            Console.WriteLine("[SKIP] effect moves: the mod library isn't at G:\\Penumbra");
            return;
        }
        var random = new Random(20261027);
        var files = Directory.GetDirectories(library).Order(StringComparer.OrdinalIgnoreCase).OrderBy(_ => random.Next())
            .Select(folder => Directory.GetFiles(folder, "*.avfx", SearchOption.AllDirectories)).Where(f => f.Length > 0).Take(25)
            .SelectMany(f => f).ToList();
        var up = new TimelineEffectEdit { Move = new Vector3(0, 1, 0) };
        int read = 0, refused = 0, moved = 0;
        var failures = new List<string>();
        string[]? realCurve = null;
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            TimelineEffectLook before;
            try { before = TimelineEffectLooks.Read(bytes); read++; }
            catch (InvalidDataException e) { failures.Add($"{Path.GetFileName(file)} does not read: {e.Message}"); continue; }
            if (!TimelineEffectLooks.Apply(bytes, new TimelineEffectEdit()).SequenceEqual(bytes))
                failures.Add($"{Path.GetFileName(file)} changes with an empty look");
            byte[] after;
            try { after = TimelineEffectLooks.Apply(bytes, up); }
            catch (InvalidDataException e) when (e.Message == "This effect can't be moved: its attach point has no position.") { refused++; continue; }
            bool movedOnly;
            try
            {
                // Each position still where it was, one metre up: a still one's constant, a moving one's keys.
                movedOnly = TimelineEffectLooks.Read(after) is var look && OnlyMoved(bytes, 8, bytes.Length, after, 8, after.Length, "") &&
                            BitConverter.ToInt32(after, 4) == after.Length - 8 &&
                            (!look.Bound || look.BinderPositions.Length == before.BinderPositions.Length && look.BinderPositions.Zip(before.BinderPositions).All(p =>
                                p.First.Keys == Math.Max(1, p.Second.Keys) && (p.Second.Keys > 1
                                    ? p.First.Constant == null
                                    : p.First.Constant is { } now && Vector3.Distance(now, (p.Second.Constant ?? Vector3.Zero) + Vector3.UnitY) < 1e-5f)));
            }
            catch (InvalidDataException) { movedOnly = false; }
            if (movedOnly)
                moved++;
            else
                failures.Add($"{Path.GetFileName(file)} changes more than its position");
            // A real binder position curve with a key, X first, else Y or Z: its leaves, in order.
            foreach (var axis in new[] { "X", "Y", "Z" })
                if (realCurve == null || axis == "X" && realCurve[0] != "X")
                    foreach (var curve in Found(bytes, "Bind").SelectMany(b => AnimationDependencies.Chunks(bytes, b.At, b.At + b.Size))
                                 .Where(p => p.Name.StartsWith("Prp", StringComparison.Ordinal))
                                 .SelectMany(p => AnimationDependencies.Chunks(bytes, p.Start, p.Start + p.Size)).Where(c => c.Name == "Pos")
                                 .SelectMany(p => AnimationDependencies.Chunks(bytes, p.Start, p.Start + p.Size)).Where(c => c.Name == axis))
                    {
                        var leaves = AnimationDependencies.Chunks(bytes, curve.Start, curve.Start + curve.Size).ToList();
                        if (leaves.Any(l => l is { Name: "Keys", Size: >= 16 }))
                        {
                            realCurve = [axis, .. leaves.Select(l => $"{l.Name}:{(l.Name == "Keys" ? 16 : l.Size)}")];
                            break;
                        }
                    }
        }
        foreach (var failure in failures.Take(5))
            Console.WriteLine("  " + failure);
        var written = AnimationDependencies.Chunks(BinderEffects.NewCurve("X", 1), 8, 80).Select(l => $"{l.Name}:{l.Size}");
        check(files.Count > 0 && failures.Count == 0 && read == files.Count && moved + refused == files.Count,
            $"library effects read, keep every byte with an empty look, and move only their positions ({moved} moved, {refused} refused, {files.Count} in 25 mod folders)");
        check(realCurve != null && realCurve.Skip(1).SequenceEqual(written),
            $"a new binder curve has the leaves of the library's own ({realCurve?[0]} curve: {string.Join(", ", realCurve?.Skip(1) ?? [])})");
    }

    /// <summary>
    /// Whether <paramref name="b"/> is <paramref name="a"/> with a move written in: the same chunks,
    /// but for binder position curves and keys added, their key values and counts, and the root offset.
    /// </summary>
    private static bool OnlyMoved(byte[] a, int aStart, int aEnd, byte[] b, int bStart, int bEnd, string path)
    {
        var axis = System.Text.RegularExpressions.Regex.IsMatch(path, "^/Bind/Prp[S12G]/Pos/[XYZ]$");
        bool Added(string name) => path.EndsWith("/Pos", StringComparison.Ordinal) && name is "X" or "Y" or "Z" || axis && name == "Keys";
        var right = AnimationDependencies.Chunks(b, bStart, bEnd).ToList();
        var next = 0;
        foreach (var (name, at, size) in AnimationDependencies.Chunks(a, aStart, aEnd))
        {
            while (next < right.Count && right[next].Name != name && Added(right[next].Name))
                next++;
            if (next == right.Count || right[next].Name != name)
                return false;
            var (_, other, otherSize) = right[next++];
            var inner = path + "/" + name;
            if (System.Text.RegularExpressions.Regex.IsMatch(inner, "^/Bind(/Prp[S12G](/Pos(/[XYZ])?)?)?$"))
            {
                if (!OnlyMoved(a, at, at + size, b, other, other + otherSize, inner))
                    return false;
            }
            else if (axis && name == "Keys")
            {
                // Existing keys keep their frame, type and first two floats; a key may be added in front of none.
                if (otherSize != size && otherSize != size + 16)
                    return false;
                var shift = otherSize - size;
                for (var key = 0; key + 16 <= size; key += 16)
                    if (!a.AsSpan(at + key, 12).SequenceEqual(b.AsSpan(other + shift + key, 12)))
                        return false;
            }
            else if (!(axis && name == "KeyC" || path == "" && name == "RvPy") && !a.AsSpan(at, size).SequenceEqual(b.AsSpan(other, otherSize)))
                return false;
        }
        return right.Skip(next).All(c => Added(c.Name));
    }

    /// <summary> Effect files with timelines and binders, built as VFXEditor writes them. </summary>
    private static class BinderEffects
    {
        public static byte[] Leaf(string name, int value) => Chunk(name, Ints(value));

        public static byte[] Entry(int binder) =>
            [.. Leaf("bEna", 1), .. Leaf("StTm", 0), .. Leaf("EdTm", 30), .. Leaf("BdNo", binder), .. Leaf("EfNo", -1), .. Leaf("EmNo", 0), .. Leaf("ClNo", -1)];

        public static byte[] Timeline(int binder, params byte[][] items) => Chunk("TmLn", [.. Leaf("BnNo", binder), .. items.SelectMany(i => Chunk("Item", i))]);

        /// <summary> A curve whose keys hold junk (9, 9) in the first two floats, so reading the third is checked. </summary>
        public static byte[] Axis(string name, params float[] values) => Chunk(name, [
            .. Leaf("KeyC", values.Length),
            .. Chunk("Keys", [.. values.SelectMany((v, i) => (byte[])[.. BitConverter.GetBytes((short)(i * 10)), .. BitConverter.GetBytes((short)0), .. Floats(9, 9, v)])]),
        ]);

        public static byte[] Pos(float[] x, float[] y, float[] z) => Chunk("Pos", [.. Axis("X", x), .. Axis("Y", y), .. Axis("Z", z)]);

        public static byte[] Effect(params byte[][] parts) => Chunk("AVFX", [
            .. Chunk("Ver", Ints(0x20110913)), .. Chunk("RvPx", Floats(0)), .. Chunk("RvPy", Floats(0)), .. Chunk("RvPz", Floats(0)),
            .. Chunk("RvRy", Floats(0)), .. parts.SelectMany(p => p)]);

        /// <summary> A curve VFXEditor writes when one is added and given a key: count, behaviours, random type, one linear key (1, 1, value). </summary>
        public static byte[] NewCurve(string name, float value) => Chunk(name, [
            .. Leaf("KeyC", 1), .. Leaf("BvPr", 0), .. Leaf("BvPo", 0), .. Leaf("RanT", 0),
            .. Chunk("Keys", [.. BitConverter.GetBytes((short)0), .. BitConverter.GetBytes((short)1), .. Floats(1, 1, value)]),
        ]);
    }

    /// <summary> Looks on timeline events: kept by edits, refused unwritten, and saved as copies. </summary>
    private static void Saving(Action<bool, string> check, Action<Action, string> reject, byte[] effect, TimelineEffectEdit edit)
    {
        const string importPath = "vfx/instant_edit/other_loop_0a1b2c3d.avfx";
        static Tmb Async(Tmb tmb, short id, string path) => tmb.Item("C173", 0x44, id, 0).I32(12, -1).Text(20, path).Bind(24, 1, 0, -1).I32(28, 3).I32(32, 2);
        var timeline = new Tmb().Head(60, null, 2).Actor(2, 3).Track(3, 4, 5, 6, 7, 8);
        timeline = Async(timeline, 4, GlowPath);
        timeline = Async(timeline, 5, GlowPath);
        timeline = Async(timeline, 6, importPath);
        timeline = Async(timeline, 7, GlowPath);
        var bytes = timeline.Sound(8, 10).Build();
        var doc = TimelineConvert.Read(TmbFile.Parse(bytes));
        TimelineEvent Ev(TimelineDocument d, string key) => d.Events.Single(e => e.Key == key);
        var tinted = new TimelineEffectEdit { Tint = new Vector3(1, 1, 1.5f) };
        var withLooks = TimelineEdits.Replace(doc, Ev(doc, "t4") with { Look = edit });
        withLooks = TimelineEdits.Replace(withLooks, Ev(withLooks, "t5") with { Look = edit with { Colors = [.. edit.Colors] } });
        withLooks = TimelineEdits.Replace(withLooks, Ev(withLooks, "t6") with { Look = new TimelineEffectEdit { Hue = 30 } });
        withLooks = TimelineEdits.Replace(withLooks, Ev(withLooks, "t7") with { Look = tinted });
        var sound = Ev(withLooks, "t8");
        check(Ev(withLooks, "t4").Look == edit && Ev(withLooks, "t5").Look!.Equals(edit) && Ev(withLooks, "t7").Look == tinted &&
              ReferenceEquals(TimelineEdits.Replace(withLooks, Ev(withLooks, "t5") with { Look = edit with { } }), withLooks) &&
              ReferenceEquals(TimelineEdits.Replace(withLooks, sound with { Look = edit }), withLooks) &&
              Ev(TimelineEdits.Replace(withLooks, Ev(withLooks, "t4") with { Path = "vfx/mod/eff/other.avfx" }), "t4").Look == null &&
              Ev(TimelineEdits.Replace(withLooks, Ev(withLooks, "t4") with { Look = new TimelineEffectEdit() }), "t4").Look == null &&
              !TimelineEdits.Same(Ev(withLooks, "t4"), Ev(doc, "t4")),
            "an effect keeps its look through edits, an equal look is no change, and a new file, an empty look or a sound drop it");
        reject(() => TimelineConvert.Apply(bytes, withLooks), "a timeline whose looks weren't written into copies is refused");

        var import = new TimelineImport(importPath,
            ImmutableDictionary<string, byte[]>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add(importPath, effect).Add("vfx/instant_edit/tex/spark.atex", [1, 2, 3]),
            0, ImmutableDictionary<string, string>.Empty.Add(importPath, "vfx/mod/eff/other.avfx"));
        var imports = new Dictionary<string, TimelineImport>(StringComparer.OrdinalIgnoreCase) { [importPath] = import };
        byte[]? Read(string path) => path == GlowPath ? effect : path == importPath ? effect : null;
        var (baked, copies) = TimelineEffectLooks.Bake(withLooks, Read, imports);
        var glow = copies.Single(c => c.GamePath.StartsWith("vfx/instant_edit/glow_", StringComparison.Ordinal));
        var other = copies.Single(c => c.GamePath.StartsWith("vfx/instant_edit/other_loop_", StringComparison.Ordinal));
        check(copies.Length == 2 && Ev(baked, "t4").Path == glow.GamePath && Ev(baked, "t5").Path == glow.GamePath &&
              Ev(baked, "t6").Path == other.GamePath && Ev(baked, "t7").Path == GlowPath && baked.Events.All(e => e.Look == null) &&
              glow.Files.Single().Value.SequenceEqual(TimelineEffectLooks.Apply(effect, edit)) && glow.Beside![glow.GamePath] == GlowPath &&
              glow.GamePath == TimelineImports.EffectPath("glow.avfx", glow.Files[glow.GamePath]),
            "saving writes each look into one copy per effect and look, named by its content; a look that changes nothing keeps the effect");
        check(other.Files.Count == 2 && other.Files["vfx/instant_edit/tex/spark.atex"].SequenceEqual(new byte[] { 1, 2, 3 }) &&
              other.Beside![other.GamePath] == "vfx/mod/eff/other.avfx" && !other.GamePath.Contains("0a1b2c3d", StringComparison.Ordinal),
            "a copy of an import takes its other files along and maps beside what the import copies, without the old hash in its name");
        check(glow.Rebuild![glow.GamePath](effect).SequenceEqual(glow.Files[glow.GamePath]) &&
              other.Rebuild is { } otherRebuild && !otherRebuild.ContainsKey(other.GamePath),
            "a look's copy is made the same way from another file of its effect; a copy of a copy that can't be made again has none");
        var saved = TmbFile.Parse(TimelineConvert.Apply(bytes, baked)).Entries.Where(e => e.Magic == "C173").Select(e => e.Text).ToList();
        var used = imports.Values.Concat(copies).ToList();
        check(saved.SequenceEqual([glow.GamePath, glow.GamePath, other.GamePath, GlowPath]) &&
              InstantEdit.Ui.TimelineWindowViews.AddedFiles(baked, used).Keys.Order().SequenceEqual(
                  new[] { glow.GamePath, other.GamePath, "vfx/instant_edit/tex/spark.atex" }.Order()) &&
              InstantEdit.Ui.TimelineWindowViews.AddedBeside(baked, used)[glow.GamePath] == GlowPath,
            "the saved timeline plays the copies, and the save packages them (not the unused import) beside their effects");
        check(ReferenceEquals(TimelineEffectLooks.Bake(doc, Read, imports).Document, doc),
            "a timeline without looks is saved as it is");
        reject(() => TimelineEffectLooks.Bake(withLooks, _ => null, imports), "a look on an effect that wasn't read is refused, not dropped");
        Previewing(check, reject, withLooks, effect, edit);
        check(TimelineEffectLooks.BaseName("vfx/instant_edit/carpet_loop_0a1b2c3d.avfx") == "carpet_loop" &&
              TimelineEffectLooks.BaseName("vfx/mod/a_0a1b2c3d.avfx") == "a_0a1b2c3d",
            "a copy of a copy drops only an instant_edit copy's hash from its name");
    }

    /// <summary>
    /// Looks in a preview: the game may keep playing the clip's loaded file, so a look shows by pointing
    /// its effect's own path at an edited copy. That works only when every event of the effect has the
    /// same look; the rest are baked into copies as a save does. A change of looks alone leaves the
    /// rest of the previewed document the same, so the running preview only needs its files updated.
    /// </summary>
    private static void Previewing(Action<bool, string> check, Action<Action, string> reject, TimelineDocument withLooks, byte[] effect,
        TimelineEffectEdit edit)
    {
        const string plainPath = "vfx/mod/eff/plain.avfx";
        TimelineEvent Ev(TimelineDocument d, string key) => d.Events.Single(e => e.Key == key);
        // t4 and t5 share glow.avfx and its look; t6 is the import with its own look; t7 gets a file of its own whose look changes nothing.
        var doc = withLooks with { Events = [.. withLooks.Events.Select(e => e.Key == "t7" ? e with { Path = plainPath } : e)] };
        var (rest, looks) = TimelineEffectLooks.Redirectable(doc);
        check(looks.Count == 3 && looks[GlowPath.ToUpperInvariant()] == edit && looks.ContainsKey("vfx/instant_edit/other_loop_0a1b2c3d.avfx") &&
              rest.Events.All(e => e.Look == null) && rest.Events.Select(e => e.Path).SequenceEqual(doc.Events.Select(e => e.Path)),
            "a preview points each effect whose events share one look at its edited copy, and keeps the timeline's paths");
        var files = TimelineEffectLooks.Redirected(looks, _ => effect);
        check(files.Count == 2 && files[GlowPath].SequenceEqual(TimelineEffectLooks.Apply(effect, edit)) && !files.ContainsKey(plainPath),
            "the redirected files hold each effect with its look; a look that changes nothing adds no file");
        reject(() => TimelineEffectLooks.Redirected(looks, _ => null), "a look whose effect wasn't read is refused");

        var mixed = TimelineEdits.Replace(doc, Ev(doc, "t5") with { Look = new TimelineEffectEdit { Hue = 10 } });
        var (mixedRest, mixedLooks) = TimelineEffectLooks.Redirectable(mixed);
        check(!mixedLooks.ContainsKey(GlowPath) && Ev(mixedRest, "t4").Look == edit && Ev(mixedRest, "t5").Look!.Hue == 10 &&
              !TimelineEdits.Same(mixedRest, rest),
            "events of one effect with different looks keep them for copies, and the preview needs building again");

        var retinted = TimelineEdits.Replace(doc, Ev(doc, "t6") with { Look = new TimelineEffectEdit { Hue = 90 } });
        var (retintedRest, retintedLooks) = TimelineEffectLooks.Redirectable(retinted);
        check(TimelineEdits.Same(retintedRest, rest) && !ReferenceEquals(retintedRest, rest) &&
              !TimelineEffectLooks.SameLooks(retintedLooks, looks) &&
              TimelineEffectLooks.SameLooks(TimelineEffectLooks.Redirectable(doc).Looks, looks) &&
              !TimelineEdits.Same(TimelineEdits.Replace(retintedRest, Ev(retintedRest, "t4") with { Start = 9 }), rest) &&
              !TimelineEdits.Same(rest with { Length = rest.Length + 1 }, rest),
            "a changed look leaves the previewed document the same but its looks not; a move or a new length doesn't");
        var unlooked = TimelineEdits.Replace(doc, Ev(doc, "t4") with { Look = null });
        check(TimelineEffectLooks.Redirectable(rest).Looks.IsEmpty && !TimelineEffectLooks.Redirectable(unlooked).Looks.ContainsKey(GlowPath),
            "a document without looks has nothing to redirect, and an effect with an unchanged event isn't redirected");
    }

    /// <summary>
    /// An effect with every kind of look number: root fields, an emitter colour (white and coloured),
    /// a particle's colour with alpha and brightness, a model colour with its own alpha, simple
    /// animation colours in use, a point light and a radial blur with a colour of its own.
    /// </summary>
    private static byte[] LookEffect()
    {
        static byte[] F(string name, float value) => Chunk(name, Floats(value));
        static byte[] Keyed(params float[][] keys) =>
        [
            .. Chunk("KeyC", Ints(keys.Length)),
            .. Chunk("Keys", [.. keys.SelectMany((k, i) => (byte[])[.. BitConverter.GetBytes((short)(i * 10)), .. BitConverter.GetBytes((short)1), .. Floats(k)])]),
        ];
        static byte[] Rgb(params float[][] colours) => Chunk("RGB", Keyed(colours));
        static byte[] One(string name, params float[] values) => Chunk(name, Keyed([.. values.Select(v => new[] { 1f, 1f, v })]));
        return Chunk("AVFX", [
            .. Chunk("Ver", Ints(0x20110913)),
            .. F("RvPx", 0), .. F("RvPy", 0), .. F("RvPz", 0), .. F("RvRx", 0), .. F("RvRy", 0), .. F("RvRz", 0),
            .. F("RvSx", 1), .. F("RvSy", 1), .. F("RvSz", 1), .. F("RvR", 1), .. F("RvG", 1), .. F("RvB", 1.5f),
            .. Chunk("Emit", [.. Chunk("LpSt", Ints(0)), .. Chunk("Col", [.. Rgb([1, 1, 1], [.2f, .4f, .6f]), .. One("A", .5f), .. One("Bri", 3)])]),
            .. Chunk("Ptcl", [
                .. Chunk("bSCt", [1]),
                .. Chunk("Col", [.. Rgb([1, .5f, 0], [0, 0, 1]), .. One("A", .8f), .. One("Bri", 2)]),
                .. Chunk("Data", [.. Chunk("ColB", [.. Rgb([1, .5f, 0]), .. One("A", .25f)])]),
                .. Chunk("Smpl", Chunk("Cols", [255, 0, 0, 200, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0]))]),
            .. Chunk("Ptcl", [
                .. Chunk("bSCt", [0]),
                .. Chunk("Smpl", Chunk("Cols", [9, 9, 200, 255, 9, 9, 200, 255, 9, 9, 200, 255, 9, 9, 200, 255]))]),
            .. Chunk("Efct", [.. Chunk("EfVT", Ints(0)), .. Chunk("Data", [.. Chunk("Col", [.. Rgb([1, .8f, .6f]), .. One("Bri", 1.5f)]), .. One("DstS", 6)])]),
            .. Chunk("Efct", [.. Chunk("EfVT", Ints(7)), .. Chunk("Data", [.. Chunk("Col", Rgb([.3f, .1f, .1f])), .. One("DstS", 9)])]),
        ]);
    }

    /// <summary> The chunks at <paramref name="path"/> below the effect's root: payload start and size. </summary>
    private static List<(int At, int Size)> Found(byte[] avfx, params string[] path)
    {
        List<(int At, int Size)> found = [(8, avfx.Length - 8)];
        foreach (var name in path)
            found = [.. found.SelectMany(f => AnimationDependencies.Chunks(avfx, f.At, f.At + f.Size)).Where(c => c.Name == name).Select(c => (c.Start, c.Size))];
        return found;
    }

    private static IEnumerable<int> Keys(byte[] avfx, params string[] path) =>
        Found(avfx, [.. path, "Keys"]).SelectMany(k => Enumerable.Range(0, k.Size / 16).Select(i => k.At + 16 * i));

    private static Vector3[] Colours(byte[] avfx, params string[] path) =>
        [.. Keys(avfx, path).Select(k => new Vector3(BitConverter.ToSingle(avfx, k + 4), BitConverter.ToSingle(avfx, k + 8), BitConverter.ToSingle(avfx, k + 12)))];

    private static float[] Values(byte[] avfx, params string[] path) => [.. Keys(avfx, path).Select(k => BitConverter.ToSingle(avfx, k + 12))];

    private static bool Near(Vector3[] actual, Vector3[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(p => Vector3.Distance(p.First, p.Second) < 1e-4f);

    private static bool Near(float[] actual, float[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-4f);

    private static IEnumerable<int> Differing(byte[] a, byte[] b) => Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(i => a[i] != b[i]);
}
