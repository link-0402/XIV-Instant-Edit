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
              look is { Glow: true, Opacity: true, LightRange: true },
            "an effect's look reads its root tint, size, offset and turn, and its colours most used first: no white emitter colour, " +
            "simple colours only when used, light colours but none of a blur");
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
            Tint = new Vector3(2, 1, 1), Size = new Vector3(.5f), Offset = new Vector3(0, 1, 0), Turn = new Vector3(0, 90, 0), Hue = 180,
            Colors = [(TimelineEffectLooks.Swatch(new Vector3(0, 0, 1)), new Vector3(0, 1, 0))], Glow = 2, Opacity = .5f, LightRange = .5f,
        };
        var edited = TimelineEffectLooks.Apply(effect, edit);
        var after = TimelineEffectLooks.Read(edited);
        check(edited.Length == effect.Length && after.Tint == new Vector3(2, 1, 1) && after.Size == new Vector3(.5f) &&
              after.Offset == new Vector3(0, 1, 0) && after.Turn == new Vector3(0, 90, 0),
            "a look writes the whole effect's tint, size, offset and turn");
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

        Saving(check, reject, effect, edit);
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
