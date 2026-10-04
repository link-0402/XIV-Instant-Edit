using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The timeline editor's TMB core: structural parse and canonical rewrite, the entry schema, the
/// document round trip with its automatic follow-ups, entry templates and the PAP footer rewrite.
/// Expected timelines are assembled here in VFXEditor's layout, never with TmbFile's own writer.
/// </summary>
internal static class TmbFileFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Schema(check);
        var original = Sample();
        Structure(check, reject, original);
        Document(check, reject, original);
        Length(check, original);
        Templates(check, reject, original);
        Refusals(reject, original);
        Pap(check, reject, original);
        ReviewFixes(check, original);
    }

    /// <summary>
    /// Fixes from the 2026-10-03 review: a copy keeps every byte of the entry it copies, going back
    /// to the loaded length puts what only scaling moves back, only motions that lasted the loaded
    /// length follow it, and an expression played by a C009 keeps the length the user gave it.
    /// </summary>
    private static void ReviewFixes(Action<bool, string> check, byte[] original)
    {
        var doc = TimelineConvert.Read(TmbFile.Parse(original));
        var copied = TimelineEdits.Duplicate(doc, ["t12"], 3, out var made);
        var effects = TmbFile.Parse(TimelineConvert.Apply(original, copied)).Entries.Where(x => x.Magic == "C012").ToList();
        check(made.Length == 1 && copied.Events[^1].CloneOf == "t12" && effects.Count == 2 && effects[1].Time == effects[0].Time + 3 &&
              new[] { 12, 16, 24, 28, 64, 68 }.All(at => effects[0].Int32(at) == effects[1].Int32(at)) &&
              new[] { 32, 40, 48, 56 }.All(at => effects[0].Vector(at).SequenceEqual(effects[1].Vector(at))) &&
              effects[0].Text == effects[1].Text,
            "a copy of a loaded effect keeps every byte of it, only moved");

        var forever = new Tmb().Head(30, null, 2).Actor(2, 3).Track(3, 4).Effect(4).I32(12, 9999999).Build();
        var foreverDoc = TimelineConvert.Read(TmbFile.Parse(forever));
        var foreverCopy = TimelineEdits.Duplicate(foreverDoc, [foreverDoc.Events[0].Key], 2, out _);
        check(TmbFile.Parse(TimelineConvert.Apply(forever, foreverCopy)).Entries.Count(x => x.Magic == "C012" && x.Int32(12) == 9999999) == 2,
            "a copy of an effect that lasts until the end saves");

        var away = TimelineEdits.SetLength(doc, 60, true, doc);
        var back = TimelineEdits.SetLength(away, 100, true, doc);
        check(away.Events.Single(x => x.Key == "t13").Start == 18 &&
              back.Events.Single(x => x.Key == "t13") is { Start: 30, Duration: 8 } && TimelineConvert.Apply(original, back).Length > 0,
            "scaling the length away and back puts a gameplay entry back where it was, and saves");

        ImmutableDictionary<string, double> none = ImmutableDictionary<string, double>.Empty;
        var body = new TimelineEvent("t1", "C009", TimelineLane.Motion, 0, 100, false, "cbem_a", TimelineAccess.Locked, null, false, none);
        var half = body with { Key = "t2", Magic = "C010", Duration = 50, Path = "cbem_b" };
        var motions = new TimelineDocument(100, 100, null, [body, half], 1);
        var steps = TimelineEdits.SetLength(TimelineEdits.SetLength(motions, 50, false, motions), 120, false, motions);
        check(steps.Events[0].Duration == 120 && steps.Events[1].Duration == 50,
            "only motions that lasted the loaded length follow later length changes");

        var expression = new Tmb().Head(40, null, 2).Actor(2, 3).Track(3, 4).Item("C009", 0x18, 4).I32(12, 40).Text(20, "cfxf_x").Build();
        var expressionDoc = TimelineConvert.Read(TmbFile.Parse(expression));
        var shorter = expressionDoc with { Events = [expressionDoc.Events[0] with { Duration = 20 }] };
        check(TmbFile.Parse(TimelineConvert.Apply(expression, shorter)).Entries.Single().Int32(12) == 20,
            "an expression played by a C009 keeps the length the user gave it");
    }

    private static readonly byte[] Lua = Ints(8, 1, 0x10, 2, 0);
    private static readonly byte[] SummonFilter = [1, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Length 100 with a face pack; one actor, six tracks: the body motion (C009), a sound (C063) on a
    /// track with a Lua condition, a face (C010, no time control, end = duration, an unknown flag bit
    /// set), an effect (C012) with vectors, a gameplay push (C117) driving a curve (TMFC), lip sync
    /// (C010 with time control). Ids 2 to 15, curve last and on no track, as vanilla has them.
    /// </summary>
    private static byte[] Sample(string? pack = "emot/laugh") => new Tmb().Head(100, pack, 2).Actor(2, 3, 4, 5, 6, 7, 8)
        .Track(3, 9).Track(4, 10).Extra(20, Lua).Track(5, 11).Track(6, 12).Track(7, 13).Track(8, 14)
        .Motion(9, 100).Sound(10, 10).Face(11, 5, 40, 0x08000000, 40, "cfxf_laugh").Effect(12).Push(13, 15)
        .Face(14, 50, 20, 1, 1, "cfxl_lip_nor1").Curve(15).Build();

    private static void Schema(Action<bool, string> check)
    {
        using var stream = typeof(TmbFile).Assembly.GetManifestResourceStream("InstantEdit.Services.Animations.TmbLayouts.json")!;
        var layouts = JsonDocument.Parse(stream).RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("Size").GetInt32());
        check(layouts.Count == 65 && TmbEntrySchema.Kinds.Count == layouts.Count &&
              layouts.All(l => TmbEntrySchema.Find(l.Key)?.Size == l.Value),
            "the entry schema covers every C-entry kind of TmbLayouts.json with the same sizes");
        check(TmbEntrySchema.Classify("C010", "cfxl_lip_nor1") is (TimelineLane.LipSync, TimelineAccess.Editable, null) &&
              TmbEntrySchema.Classify("C009", "cfxf_smile") is (TimelineLane.Expression, TimelineAccess.Editable, null) &&
              TmbEntrySchema.Classify("C010", "cfxb_blink").Lane == TimelineLane.Expression &&
              TmbEntrySchema.Classify("C010", "cbem_sp50") is (TimelineLane.Motion, TimelineAccess.Locked, TmbEntrySchema.MotionReason) &&
              TmbEntrySchema.Classify("C063", "") is (TimelineLane.Sound, TimelineAccess.Editable, null) &&
              TmbEntrySchema.Classify("C173", "").Lane == TimelineLane.Effect &&
              TmbEntrySchema.Classify("C198", "") is (TimelineLane.Prop, TimelineAccess.Editable, null) &&
              TmbEntrySchema.Classify("C043", "") is (TimelineLane.Prop, TimelineAccess.Movable, not null) &&
              TmbEntrySchema.Classify("C013", "") is (TimelineLane.Other, TimelineAccess.Locked, TmbEntrySchema.GameplayReason) &&
              TmbEntrySchema.Classify("C042", "") is (TimelineLane.Other, TimelineAccess.Movable, not null),
            "entries get their lane and access from their kind, and C009/C010 from the motion they play");
        var sound = TmbEntrySchema.Get("C063");
        check(!sound.HasDuration && TmbEntrySchema.Get("C012").HasDuration && TmbEntrySchema.Get("C094").HasDuration &&
              sound.StringOffset == 20 && TmbEntrySchema.Get("C010").StringOffset == 32 && TmbEntrySchema.Get("C042").StringOffset == -1 &&
              sound.Fields.Single(f => f.Name == "PositionFlags") is { Kind: TimelineFieldKind.Flags } flags &&
              flags.Options.Select(o => o.Value).SequenceEqual([1.0, 2, 4]) &&
              TmbEntrySchema.Get("C012").Fields.Single(f => f.Name == "Visibility").Options.Length == 4 &&
              TmbEntrySchema.Get("C042").Fields.IsEmpty,
            "only understood fields are exposed, and a sound's +12 is its loop, not a length");
    }

    private static void Structure(Action<bool, string> check, Action<Action, string> reject, byte[] original)
    {
        var file = TmbFile.Parse(original);
        check(file.Length == 100 && file.FacePack == "emot/laugh" && !file.Edited &&
              file.Actors.Single() is { Id: 2 } actor && actor.Tracks.SequenceEqual(new short[] { 3, 4, 5, 6, 7, 8 }) &&
              file.Tracks.Select(t => t.Conditional).SequenceEqual([false, true, false, false, false, false]) &&
              file.TrackOf(10)?.Id == 4 && file.TrackOf(15) == null,
            "a timeline reads its length, face pack, actor and tracks with their Lua condition");
        var sound = file.Find(10)!;
        var face = file.Find(11)!;
        var effect = file.Find(12)!;
        check(file.Entries.Select(e => e.Magic).SequenceEqual(["C009", "C063", "C010", "C012", "C117", "C010", "TMFC"]) &&
              sound.Time == 10 && sound.Text == "sound/test/se.scd" && sound.Int32(12) == 1 && sound.Byte(28) == 1 &&
              face.Single(28) == 40 && face.Text == "cfxf_laugh" && face.Int32(36) == 380 &&
              effect.SByte(24) == 1 && effect.Int16(26) == -1 && effect.Vector(48).SequenceEqual([0f, 0.5f, 0f]) &&
              effect.Vector(56).Length == 4 && file.Find(13)!.Int32(TmbFile.CurveIdOffset) == 15 &&
              file.CurveTimes(15).SequenceEqual([0f, 8f]),
            "timeline entries expose their time, string, typed fields, vectors and curve rows");
        check(file.ToArray().AsSpan().SequenceEqual(original) && file.ToCanonicalArray().AsSpan().SequenceEqual(original),
            "an unedited timeline writes its own bytes, and the canonical writer reproduces a canonical timeline");
        byte[] embedded = [1, 2, 3, .. original, 9];
        check(TmbFile.Parse(embedded, 3).ToArray().AsSpan().SequenceEqual(original), "a timeline is read at an offset inside a larger file");

        file.SetTime(10, 10);
        file.SetInt32(10, 24, 0);
        file.SetString(10, "sound/test/se.scd");
        file.SetVector(12, 48, [0f, 0.5f, 0f]);
        file.ScaleCurve(15, 1);
        file.FacePack = "emot/laugh";
        check(!file.Edited, "setting fields to the values they hold is not an edit");
        reject(() => file.SetInt32(10, 20, 0), "an entry's string pointer cannot be set as a plain field");
        reject(() => file.SetInt32(13, 20, 3), "a C117's raw curve id cannot be set as a plain field");
        reject(() => file.SetInt16(10, 8, 3), "an entry's id cannot be set as a plain field");
        reject(() => file.SetString(10, "sound/bad\npath.scd"), "a path with control characters is refused");
        reject(() => file.FacePack = "../laugh", "an unsafe face pack name is refused");
        reject(() => file.ScaleCurve(13, 2), "only a TMFC holds curve rows");

        // A new entry gets a new track right after the first actor's last track, and its entry goes after
        // the entries of the tracks before it; everything renumbers from 2.
        var two = TmbFile.Parse(new Tmb().Head(30, null, 2, 3).Actor(2, 4).Actor(3, 5).Track(4, 6).Track(5, 7)
            .Sound(6, 0).Sound(7, 5).Build());
        var added = two.AddEntry("C063", new byte[0x20]);
        two.SetTime(added, 9);
        two.SetInt32(added, 12, 1);
        two.SetByte(added, 28, 1);
        two.SetString(added, "sound/test/se.scd");
        var expected = new Tmb().Head(30, null, 2, 3).Actor(2, 4, 5).Actor(3, 6).Track(4, 7).Track(5, 8).Track(6, 9)
            .Sound(7, 0).Sound(8, 9).Sound(9, 5).Build();
        check(two.Edited && two.ToArray(out var ids).AsSpan().SequenceEqual(expected) && ids[added] == 8 && ids[7] == 9,
            "an added entry gets its own track after the first actor's tracks and is renumbered in place");
        reject(() => two.AddEntry("C063", new byte[0x1C]), "an added entry of the wrong size is refused");
        reject(() => two.AddEntry("C999", new byte[0x20]), "an added entry of an unknown kind is refused");
    }

    private static void Document(Action<bool, string> check, Action<Action, string> reject, byte[] original)
    {
        var doc = TimelineConvert.Read(TmbFile.Parse(original));
        var e = doc.Events.ToDictionary(x => x.Key);
        check(doc is { Length: 100, SourceLength: 100, FacePack: "emot/laugh", NextKey: 1 } &&
              doc.Events.Select(x => x.Key).SequenceEqual(["t9", "t10", "t11", "t12", "t13", "t14"]),
            "a document has one event per C-entry keyed by its id, without the curve");
        check(e["t9"] is { Lane: TimelineLane.Motion, Access: TimelineAccess.Locked, Duration: 100, Resizable: false, Path: "cbem_test" } &&
              e["t10"] is { Lane: TimelineLane.Sound, Access: TimelineAccess.Editable, Conditional: true, Duration: 0, Resizable: false } &&
              e["t10"].Values["Loop"] == 1 && e["t10"].Values["PositionFlags"] == 1 &&
              e["t11"] is { Lane: TimelineLane.Expression, Access: TimelineAccess.Editable, Resizable: true, Duration: 40, Start: 5 } &&
              e["t11"].Values["TimeControl"] == 0 &&
              e["t12"] is { Lane: TimelineLane.Effect, Duration: 30, Path: "vfx/test/eff/test.avfx" } &&
              e["t12"].Values["Position.Y"] == 0.5 && e["t12"].Values["Bind2.Origin"] == 2 && e["t12"].Values["Visibility"] == 3 &&
              e["t13"] is { Lane: TimelineLane.Other, Access: TimelineAccess.Locked, LockReason: TmbEntrySchema.GameplayReason } &&
              e["t14"] is { Lane: TimelineLane.LipSync, Conditional: false } && e["t14"].Values["TimeControl"] == 1,
            "events carry lane, access, length, path, condition and typed values");
        check(ReferenceEquals(TimelineConvert.Apply(original, doc), original), "an unchanged document gives back the original bytes");

        TimelineDocument With(string key, Func<TimelineEvent, TimelineEvent> change) =>
            doc with { Events = [.. doc.Events.Select(x => x.Key == key ? change(x) : x)] };
        var moved = TimelineConvert.Apply(original, With("t10", x => x with { Start = 12 }));
        var patched = original.ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(patched.AsSpan(At(original, "C063") + 10), 12);
        check(moved.AsSpan().SequenceEqual(patched), "moving an event changes only its time");

        var changed = doc with
        {
            Events = [.. doc.Events.Select(x => x.Key switch
            {
                "t10" => x with { Values = x.Values.SetItem("SoundIndex", 3).SetItem("PositionFlags", 5).SetItem("BindId", 71) },
                "t11" => x with { Path = "cfxf_smile", Values = x.Values.SetItem("TimeControl", 1) },
                "t12" => x with { Path = "vfx/test/eff/other.avfx", Duration = 45, Values = x.Values.SetItem("Scale.X", 2).SetItem("Visibility", 1).SetItem("Bind1.Type", 1) },
                _ => x,
            })],
        };
        var output = TmbFile.Parse(TimelineConvert.Apply(original, changed));
        var reread = TimelineConvert.Read(output).Events.ToDictionary(x => x.Key);
        check(reread["t10"].Values["SoundIndex"] == 3 && reread["t10"].Values["PositionFlags"] == 5 && reread["t10"].Values["BindId"] == 71 &&
              reread["t11"] is { Path: "cfxf_smile" } && output.Find(11)!.Int32(20) == 0x08000001 && output.Find(11)!.Single(28) == 40 &&
              output.Find(11)!.Int32(36) == 380 && reread["t12"] is { Path: "vfx/test/eff/other.avfx", Duration: 45 } &&
              output.Find(12)!.Vector(32).SequenceEqual([2f, 1f, 1f]) && output.Find(12)!.Int32(64) == 1 && output.Find(12)!.SByte(25) == 1 &&
              output.Find(12)!.Int32(68) == 5 && !output.Entries.Any(x => x.Text == "cfxf_laugh"),
            "edited paths and values are written, other bits and unknown fields are kept, unused strings dropped");

        // Removing the effect drops its track; the sound's track stays for its condition. Ids close up
        // and the push follows its curve to its new id.
        var removed = TimelineConvert.Apply(original, doc with { Events = doc.Events.RemoveAll(x => x.Key is "t10" or "t12") });
        var expected = new Tmb().Head(100, "emot/laugh", 2).Actor(2, 3, 4, 5, 6, 7)
            .Track(3, 8).Track(4).Extra(20, Lua).Track(5, 9).Track(6, 10).Track(7, 11)
            .Motion(8, 100).Face(9, 5, 40, 0x08000000, 40, "cfxf_laugh").Push(10, 12).Face(11, 50, 20, 1, 1, "cfxl_lip_nor1").Curve(12).Build();
        check(removed.AsSpan().SequenceEqual(expected), "deleting events renumbers ids canonically and remaps the curve id");

        var sound = TimelineTemplates.Create(new(TimelineAddKind.Sound, 40, "sound/test/se.scd")).Single() with { Key = "n1" };
        var added = TimelineConvert.Apply(original, doc with { Events = doc.Events.Add(sound), NextKey = 2 });
        expected = new Tmb().Head(100, "emot/laugh", 2).Actor(2, 3, 4, 5, 6, 7, 8, 9)
            .Track(3, 10).Track(4, 11).Extra(20, Lua).Track(5, 12).Track(6, 13).Track(7, 14).Track(8, 15).Track(9, 16)
            .Motion(10, 100).Sound(11, 10).Face(12, 5, 40, 0x08000000, 40, "cfxf_laugh").Effect(13).Push(14, 17)
            .Face(15, 50, 20, 1, 1, "cfxl_lip_nor1").Sound(16, 40).Curve(17).Build();
        check(added.AsSpan().SequenceEqual(expected), "an added sound gets a track and entry where VFXEditor puts them, before the curve, which is remapped");

        var bare = Sample(null);
        var bareDoc = TimelineConvert.Read(TmbFile.Parse(bare));
        check(TimelineConvert.Apply(original, doc with { FacePack = null }).AsSpan().SequenceEqual(bare) &&
              TimelineConvert.Apply(bare, bareDoc with { FacePack = "emot/laugh" }).AsSpan().SequenceEqual(original),
            "clearing the face pack removes the TMPP and setting one inserts it right after the header");

        reject(() => TimelineConvert.Apply(original, doc with { Events = doc.Events.RemoveAll(x => x.Key == "t13") }), "a gameplay entry cannot be deleted");
        reject(() => TimelineConvert.Apply(original, With("t9", x => x with { Path = "cbem_other" })), "the clip's own motion cannot be renamed");
        reject(() => TimelineConvert.Apply(original, With("t11", x => x with { Path = "cbem_other" })), "a face cannot become a body motion");
        reject(() => TimelineConvert.Apply(original, With("t12", x => x with { Values = x.Values.SetItem("Visibility", 7) })), "a choice outside its options is refused");
        reject(() => TimelineConvert.Apply(original, With("t10", x => x with { Values = x.Values.Remove("BindId") })), "an event missing a field is refused");
        reject(() => TimelineConvert.Apply(original, With("t10", x => x with { Duration = 5 })), "a kind without a length cannot be given one");
        reject(() => TimelineConvert.Apply(original, With("t10", x => x with { Start = -1 })), "a negative start is refused");
        reject(() => TimelineConvert.Apply(original, With("t10", x => x with { Key = "t99" })), "an event the timeline does not have is refused");
        reject(() => TimelineConvert.Apply(original, doc with { Events = doc.Events.Add(doc.Events[1]) }), "two events with one key are refused");
        reject(() => TimelineConvert.Apply(original, doc with { Events = doc.Events.Add(e["t13"] with { Key = "n1" }) }), "a gameplay entry cannot be added");
        // Only length scaling moves a gameplay entry: back at the loaded length it is where it was loaded.
        check(TimelineConvert.Apply(original, With("t13", x => x with { Start = 31 })) == original,
            "a gameplay entry stays put while the length stays");
        reject(() => TimelineConvert.Apply(original, doc with { Length = 1 }), "a length below the minimum is refused");
        reject(() => TimelineConvert.Apply(original, doc with { SourceLength = 90 }), "a document of another timeline is refused");
        reject(() => TimelineConvert.Apply(original, doc with { FacePack = "a b" }), "an unsafe face pack is refused");

        // A Movable entry (animation blending) moves and goes away, and only takes a new length from a scaled timeline.
        var blend = new Tmb().Head(30, null, 2).Actor(2, 3).Track(3, 4).Item("C088", 0x14, 4, 5).I32(12, 17).Build();
        var blendDoc = TimelineConvert.Read(TmbFile.Parse(blend));
        var blending = blendDoc.Events.Single();
        check(blending is { Access: TimelineAccess.Movable, Resizable: false, Duration: 17 } &&
              TmbFile.Parse(TimelineConvert.Apply(blend, blendDoc with { Events = [blending with { Start = 7 }] })).Find(4)!.Time == 7 &&
              TmbFile.Parse(TimelineConvert.Apply(blend, blendDoc with { Length = 45, Events = [blending with { Start = 8, Duration = 26 }] }))
                  .Find(4) is { Time: 8 } scaledBlend && scaledBlend.Int32(12) == 26 &&
              TmbFile.Parse(TimelineConvert.Apply(blend, blendDoc with { Events = [] })).Entries.IsEmpty,
            "a movable entry moves, is deleted, and scales with the timeline's length");
        check(TimelineConvert.Apply(blend, blendDoc with { Events = [blending with { Duration = 20 }] }) == blend,
            "a movable entry keeps its length while the timeline's length stays");

        // A float a file left as NaN reads back as NaN and is not an edit.
        var nan = Patch(original, b => BitConverter.GetBytes(float.NaN).CopyTo(b, b.AsSpan().IndexOf(BitConverter.GetBytes(0.5f))));
        var nanDoc = TimelineConvert.Read(TmbFile.Parse(nan));
        var nanSound = nanDoc.Events.Single(x => x.Key == "t10");
        check(double.IsNaN(nanDoc.Events.Single(x => x.Key == "t12").Values["Position.Y"]) && ReferenceEquals(TimelineConvert.Apply(nan, nanDoc), nan) &&
              TmbFile.Parse(TimelineConvert.Apply(nan, nanDoc with { Events = nanDoc.Events.Replace(nanSound, nanSound with { Start = 11 }) }))
                  .Find(12)!.Vector(48) is [0f, var y, 0f] && float.IsNaN(y),
            "an unchanged NaN field is kept and does not block other edits");
    }

    private static void Length(Action<bool, string> check, byte[] original)
    {
        // The editor scaled starts and lengths by 1.5; the clip's motion, the face's end frame and the
        // push's curve follow on their own, while the lip sync with time control keeps its 0 to 1 range.
        var doc = TimelineConvert.Read(TmbFile.Parse(original));
        var scaled = doc with
        {
            Length = 150,
            Events = [.. doc.Events.Select(x => x.Key switch
            {
                "t11" => x with { Start = 8, Duration = 60 },
                "t13" => x with { Start = 45, Duration = 12 },
                "t14" => x with { Start = 75, Duration = 30 },
                _ => x,
            })],
        };
        var output = TmbFile.Parse(TimelineConvert.Apply(original, scaled));
        var reread = TimelineConvert.Read(output);
        check(output.Length == 150 && output.Find(9)!.Int32(12) == 150 && reread.Events[0].Duration == 150 &&
              output.Find(11)!.Single(28) == 60 && output.Find(11)!.Time == 8 && output.Find(14)!.Single(28) == 1 &&
              output.Find(13) is { Time: 45 } push && push.Int32(12) == 12 && push.Int32(TmbFile.CurveIdOffset) == 15 &&
              output.CurveTimes(15).SequenceEqual([0f, 12f]),
            "a length change carries the body motion, the face end frame and the curve rows along");
        var shorter = TmbFile.Parse(TimelineConvert.Apply(original, doc with { Length = 80 }));
        check(shorter.Length == 80 && shorter.Find(9)!.Int32(12) == 80 && shorter.Find(11)!.Single(28) == 40 &&
              shorter.CurveTimes(15).SequenceEqual([0f, 8f]),
            "a length change without scaling moves nothing but the motion's length");
    }

    private static void Templates(Action<bool, string> check, Action<Action, string> reject, byte[] original)
    {
        var sound = TimelineTemplates.Create(new(TimelineAddKind.Sound, 4, "sound/test/se.scd")).Single();
        var effect = TimelineTemplates.Create(new(TimelineAddKind.Effect, 4, "vfx/test/eff/test.avfx")).Single();
        var face = TimelineTemplates.Create(new(TimelineAddKind.Expression, 12, "cfxf_smile", 0, "smile")).Single();
        var lip = TimelineTemplates.Create(new(TimelineAddKind.LipSync, 20, "cfxl_lip_nor1")).Single();
        check(sound is { Key: "", Magic: "C063", Lane: TimelineLane.Sound, Duration: 0, Group: null, Access: TimelineAccess.Editable } &&
              sound.Values["Loop"] == 1 && sound.Values["PositionFlags"] == 1 &&
              effect is { Magic: "C012", Duration: 30, Resizable: true } && effect.Values["Visibility"] == 3 &&
              effect.Values["Bind1.Origin"] == 1 && effect.Values["Bind2.Origin"] == 2 && effect.Values["Scale.Z"] == 1 && effect.Values["Color.A"] == 1 &&
              face is { Magic: "C010", Lane: TimelineLane.Expression, Duration: 30 } && face.Values["TimeControl"] == 1 &&
              lip is { Magic: "C010", Lane: TimelineLane.LipSync, Duration: 40 } && lip.Values["TimeControl"] == 0,
            "templates make sounds, effects, faces and lip sync with vanilla defaults");

        var prop = TimelineTemplates.Create(new(TimelineAddKind.Prop, 10, "", 60, Prop: new TimelinePropModel(1926, 1, 1, 274)));
        var still = TimelineTemplates.Create(new(TimelineAddKind.Prop, 10, "", 0, Prop: new TimelinePropModel(1926, 1, 1, null)));
        check(prop.Select(x => x.Magic).SequenceEqual(["C173", "C198", "C031", "C094", "C094"]) &&
              prop.Select(x => x.Group).Distinct().Single() is { } group && still.Select(x => x.Group).Distinct().Single() != group &&
              prop[2].Start == 15 && prop[1].Duration == 60 && still.Select(x => x.Magic).SequenceEqual(["C173", "C198"]),
            "a prop is the sync effect and model, plus an animation behind a hide and fade-in, sharing one group");

        var bare = Sample(null);
        var doc = TimelineConvert.Read(TmbFile.Parse(bare));
        IEnumerable<TimelineEvent> keyed = [.. prop, face, lip];
        var output = TmbFile.Parse(TimelineConvert.Apply(bare, doc with
        {
            FacePack = "smile", Events = doc.Events.AddRange(keyed.Select((x, i) => x with { Key = "n" + (i + 1) })), NextKey = 8,
        }));
        var added = output.Entries.Skip(6).SkipLast(1).ToList();
        check(output.FacePack == "smile" && output.Entries[^1].Magic == "TMFC" && output.Tracks.Length == 13 &&
              output.Entries.Single(x => x.Magic == "C117").Int32(TmbFile.CurveIdOffset) == output.Entries[^1].Id &&
              added.Select(x => x.Magic).SequenceEqual(["C173", "C198", "C031", "C094", "C094", "C010", "C010"]) &&
              added[0] is { Text: TimelineTemplates.SyncEffect, Time: 10 } && added[0].Int32(12) == 1 && added[0].Int16(26) == -1 &&
              added[0].Int32(28) == 1 && added[0].Int32(32) == 1 &&
              added[1].Int16(32) == 1926 && added[1].Int16(34) == 1 && added[1].Int32(36) == 1 && added[1].Int32(12) == 60 &&
              added[2] is { Time: 15 } && added[2].UInt16(20) == 274 && added[2].Int16(22) == 2 &&
              added[3].Block(28)!.SequenceEqual(SummonFilter) && added[3].Int32(12) == 5 && added[3].Single(24) == 0 &&
              added[4] is { Time: 15 } && added[4].Int32(12) == 7 && added[4].Single(24) == 1 &&
              added[5] is { Text: "cfxf_smile", Time: 12 } && added[5].Int32(20) == 1 && added[5].Single(28) == 1 && added[5].Int32(12) == 30 &&
              added[6] is { Text: "cfxl_lip_nor1" } && added[6].Int32(20) == 0 && added[6].Single(28) == 40,
            "added props, faces and lip sync are written with their template bytes and the new face pack");
        var lanes = TimelineConvert.Read(output).Events.Skip(6).Select(x => x.Lane);
        check(lanes.SequenceEqual([TimelineLane.Effect, TimelineLane.Prop, TimelineLane.Prop, TimelineLane.Prop, TimelineLane.Prop,
                  TimelineLane.Expression, TimelineLane.LipSync]),
            "added entries read back on their lanes");

        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Sound, 0, "vfx/test/eff/test.avfx")), "a sound needs an .scd path");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Effect, 0, "/vfx/x.avfx")), "an effect needs a game path");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Expression, 0, "cfxl_lip_nor1")), "lip sync is not an expression");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.LipSync, 0, "cfxf_smile")), "an expression is not lip sync");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Prop, 0, "")), "a prop needs a model");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Prop, 0, "", 0, Prop: new TimelinePropModel(0, 1, 1, null))), "model 0 is not a prop");
        reject(() => TimelineTemplates.Create(new(TimelineAddKind.Sound, -1, "sound/test/se.scd")), "a negative start is refused");
    }

    private static void Refusals(Action<Action, string> reject, byte[] original)
    {
        var sound = At(original, "C063");
        reject(() => TmbFile.Parse(Patch(original, b => "C999"u8.CopyTo(b.AsSpan(sound)))), "an unknown entry kind is refused");
        reject(() => TmbFile.Parse(Patch(original, b => Int(b, sound + 4, 0x24))), "a known kind of the wrong size is refused");
        reject(() => TmbFile.Parse(Patch(original, b => Int(b, sound + 20, 100000))), "a pointer past the timeline is refused");
        reject(() => TmbFile.Parse(Patch(original, b => Int(b, sound + 20, -8))), "a pointer back into the entries is refused");
        reject(() => TmbFile.Parse(Patch(original, b => b[^1] = (byte)'x')), "an unterminated string is refused");
        reject(() => TmbFile.Parse(original[..^4]), "a timeline shorter than its size is refused");
        reject(() => TmbFile.Parse(Patch(original, b => Int(b, 8, 99))), "an entry count past the timeline is refused");
        reject(() => TmbFile.Parse(Patch(original, b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(sound + 8), 9))), "two entries with one id are refused");
        reject(() => TmbFile.Parse(Patch(original, b => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(At(original, "C009") + 8), 99))),
            "a track naming a missing entry is refused");
        reject(() => TmbFile.Parse(Patch(original, b => Int(b, At(original, "C012") + 36, 0))), "a vector without values is refused");
        reject(() => TmbFile.Parse(Patch(original, b => "TMAL"u8.CopyTo(b.AsSpan(12)))), "a timeline must start with its header item");
        reject(() => TmbFile.Parse(Patch(original, b => "TMLX"u8.CopyTo(b))), "a timeline must start with TMLB");
    }

    private static void Pap(Action<bool, string> check, Action<Action, string> reject, byte[] sample)
    {
        var small = new Tmb().Head(10, "ab").Build();
        var other = new Tmb().Head(10, "abcd").Build();
        byte[] tail = [0xAA, 0xBB, 0xCC];
        (string, short)[] names = [("cbem_a", 0), ("cbem_b", 1), ("cbem_b", 1)];
        var bytes = BuildPap(names, [sample, small, other], false, tail);
        var pap = new AnimationPap(bytes);
        var footer = pap.TimelineOffset;
        var second = footer + sample.Length + (-sample.Length & 3);
        var third = second + small.Length + (-(second - footer + small.Length) & 3);
        check(footer % 4 == 3 && small.Length % 4 == 3 && pap.TimelineBytes(0).SequenceEqual(sample) &&
              pap.TimelineBytes(1).SequenceEqual(small) && pap.TimelineBytes(2).SequenceEqual(other) &&
              bytes.AsSpan(third, other.Length).SequenceEqual(other),
            "PAP timelines are found back to back, padded to 4 from an odd footer offset");
        check(pap.EntryIndex("cbem_a", 0) == 0, "a PAP entry is found by name and binding");
        reject(() => pap.EntryIndex("cbem_a", 1), "a PAP entry with another binding is not found");
        reject(() => pap.EntryIndex("cbem_b", 1), "an ambiguous PAP entry is refused");
        check(pap.WithTimeline(0, sample).AsSpan().SequenceEqual(bytes) && pap.WithTimeline(1, small).AsSpan().SequenceEqual(bytes),
            "writing a timeline back unchanged gives the same PAP");

        var longer = new Tmb().Head(10, "abcdefg").Build();
        var rebuilt = pap.WithTimeline(1, longer);
        var moved = second + longer.Length + (-(second - footer + longer.Length) & 3);
        var again = new AnimationPap(rebuilt);
        check(rebuilt.AsSpan(0, second).SequenceEqual(bytes.AsSpan(0, second)) && again.TimelineBytes(1).SequenceEqual(longer) &&
              again.TimelineBytes(2).SequenceEqual(other) && rebuilt.AsSpan(moved, other.Length).SequenceEqual(other) &&
              rebuilt.AsSpan(moved + other.Length).SequenceEqual(tail) && rebuilt.Length == moved + other.Length + tail.Length &&
              again.Havok.SequenceEqual(pap.Havok) && again.Entries.SequenceEqual(pap.Entries),
            "a replaced timeline moves the ones after it to their new padding and keeps the bytes after the last");
        var doc = TimelineConvert.Read(TmbFile.Parse(sample));
        var edited = TimelineConvert.Apply(sample, doc with { FacePack = null });
        var swapped = new AnimationPap(pap.WithTimeline(0, edited));
        check(swapped.TimelineBytes(0).SequenceEqual(edited) && swapped.TimelineBytes(1).SequenceEqual(small) &&
              swapped.TimelineBytes(2).SequenceEqual(other) && swapped.Havok.SequenceEqual(pap.Havok),
            "an edited first timeline swaps in and the others walk back");
        reject(() => pap.WithTimeline(3, small), "a PAP entry past the last is refused");
        reject(() => pap.WithTimeline(0, [1, 2, 3]), "a replacement that is not a timeline is refused");
        reject(() => pap.WithTimeline(0, Patch(small, b => Int(b, 4, small.Length + 4))), "a replacement whose size is wrong is refused");
        var absolute = new AnimationPap(BuildPap(names, [sample, small, other], true, tail));
        reject(() => absolute.TimelineBytes(1), "timelines padded from the file start instead of the footer are refused");
    }

    /// <summary>A PAP whose footer starts at an odd offset: 3 entries after the 26-byte header, 9 Havok bytes.</summary>
    private static byte[] BuildPap((string Name, short Binding)[] entries, byte[][] timelines, bool absolute, byte[] tail)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8);
        writer.Write(0x20001);
        writer.Write((short)entries.Length);
        writer.Write((short)0x0101);
        writer.Write((byte)0);
        writer.Write((byte)0);
        var offsets = stream.Position;
        writer.Write(0); writer.Write(0); writer.Write(0);
        var info = (int)stream.Position;
        foreach (var (name, binding) in entries)
        {
            var text = Encoding.ASCII.GetBytes(name);
            writer.Write(text); writer.Write(new byte[32 - text.Length]);
            writer.Write((short)7); writer.Write(binding); writer.Write(0);
        }
        var havok = (int)stream.Position;
        writer.Write(new byte[] { 0x57, 0xE0, 0xE0, 0x57, 1, 2, 3, 4, 5 });
        var footer = (int)stream.Position;
        for (var i = 0; i < timelines.Length; i++)
        {
            if (i > 0) writer.Write(new byte[((absolute ? 0 : footer) - (int)stream.Position) & 3]);
            writer.Write(timelines[i]);
        }
        writer.Write(tail);
        stream.Position = offsets;
        writer.Write(info); writer.Write(havok); writer.Write(footer);
        return stream.ToArray();
    }

    private static int At(byte[] bytes, string magic) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(magic));

    private static byte[] Patch(byte[] bytes, Action<byte[]> change)
    {
        var copy = bytes.ToArray();
        change(copy);
        return copy;
    }

    private static void Int(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);

    private static byte[] Ints(params int[] values) => [.. values.SelectMany(v => BitConverter.GetBytes(v))];

    private static byte[] Floats(params float[] values) => [.. values.SelectMany(v => BitConverter.GetBytes(v))];

    /// <summary>
    /// Assembles a timeline the way VFXEditor writes one: items, then extra data, id lists and strings
    /// stored once, every pointer from its item + 8 (a TMFC's from + 12).
    /// </summary>
    private sealed class Tmb
    {
        private readonly record struct Pointer(int Offset, int Region, byte[] Data, int Count, bool Curve);
        private readonly List<(byte[] Bytes, List<Pointer> Pointers)> items = [];
        private byte[] Last => items[^1].Bytes;

        public Tmb Head(int length, string? pack, params short[] actors)
        {
            Item("TMDH", 0x10, 1).I16(12, length).I16(14, 3);
            if (pack != null) Item("TMPP", 0x0C).Text(8, pack);
            return Item("TMAL", 0x10).Ids(8, actors);
        }

        public Tmb Actor(short id, params short[] tracks) => Item("TMAC", 0x1C, id).Ids(20, tracks);
        public Tmb Track(short id, params short[] entries) => Item("TMTR", 0x18, id).Ids(12, entries);
        public Tmb Motion(short id, int length) => Item("C009", 0x18, id).I32(12, length).Text(20, "cbem_test");
        /// <summary>A sound as the template writes one: loop 1, index 0, 3D range, bind 0.</summary>
        public Tmb Sound(short id, int time) => Item("C063", 0x20, id, time).I32(12, 1).Text(20, "sound/test/se.scd").U8(28, 1);
        public Tmb Face(short id, int time, int duration, int flags, float end, string motion) =>
            Item("C010", 0x28, id, time).I32(12, duration).I32(20, flags).F32(28, end).Text(32, motion).I32(36, 380);
        public Tmb Effect(short id) => Item("C012", 0x48, id, 20).I32(12, 30).Text(20, "vfx/test/eff/test.avfx")
            .Bind(24, 1, 0, -1).Bind(28, 2, 0, -1).Vector(32, 1, 1, 1).Vector(40, 0, 0, 0).Vector(48, 0, 0.5f, 0)
            .Vector(56, 1, 1, 1, 1).I32(64, 3).I32(68, 5);
        public Tmb Push(short id, int curve) => Item("C117", 0x18, id, 30).I32(12, 8).I32(20, curve);
        /// <summary>One curve group of two rows at frames 0 and 8 (rows: u32, time, velocity, distance, two more floats).</summary>
        public Tmb Curve(short id) => Item("TMFC", 0x20, id).I32(16, 1)
            .Point(new(12, 0, [.. Ints(1, 0, 0, 2), .. Ints(1), .. Floats(0, 0.125f, 0, 0, 0), .. Ints(1), .. Floats(8, 0, 1, 0, 0)], -1, true));

        public Tmb Item(string magic, int size, int id = -1, int time = 0)
        {
            var bytes = new byte[size];
            Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size);
            if (id >= 0)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), (short)id);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(10), (short)time);
            }
            items.Add((bytes, []));
            return this;
        }

        public Tmb I32(int at, int value) { BinaryPrimitives.WriteInt32LittleEndian(Last.AsSpan(at), value); return this; }
        public Tmb I16(int at, int value) { BinaryPrimitives.WriteInt16LittleEndian(Last.AsSpan(at), (short)value); return this; }
        public Tmb F32(int at, float value) { BinaryPrimitives.WriteSingleLittleEndian(Last.AsSpan(at), value); return this; }
        public Tmb U8(int at, int value) { Last[at] = (byte)value; return this; }
        /// <summary>A bind point: origin and type bytes, then the int16 point id.</summary>
        public Tmb Bind(int at, int origin, int type, int point) => U8(at, origin).U8(at + 1, type).I16(at + 2, point);
        public Tmb Text(int at, string text) => Point(new(at, 2, Encoding.ASCII.GetBytes(text), -1, false));
        public Tmb Ids(int at, params short[] ids) => Point(new(at, 1, [.. ids.SelectMany(i => BitConverter.GetBytes(i))], ids.Length, false));
        public Tmb Extra(int at, byte[] data) => Point(new(at, 0, data, -1, false));
        public Tmb Vector(int at, params float[] values) => Point(new(at, 0, Floats(values), values.Length, false));

        private Tmb Point(Pointer pointer)
        {
            items[^1].Pointers.Add(pointer);
            return this;
        }

        public byte[] Build()
        {
            var all = items.SelectMany(i => i.Pointers).ToList();
            var extraAt = 12 + items.Sum(i => i.Bytes.Length);
            var idsAt = extraAt + all.Where(p => p.Region == 0).Sum(p => p.Data.Length);
            var textAt = idsAt + all.Where(p => p.Region == 1).Sum(p => p.Data.Length);
            MemoryStream[] regions = [new(), new(), new()];
            var texts = new Dictionary<string, int>();
            var output = new MemoryStream();
            output.Write("TMLB"u8);
            output.Write(new byte[8]);
            foreach (var (bytes, pointers) in items)
            {
                var position = (int)output.Length;
                foreach (var p in pointers)
                {
                    int target;
                    if (p.Region < 2)
                    {
                        target = (p.Region == 0 ? extraAt : idsAt) + (int)regions[p.Region].Length;
                        regions[p.Region].Write(p.Data);
                    }
                    else
                    {
                        var text = Encoding.ASCII.GetString(p.Data);
                        if (!texts.TryGetValue(text, out var at))
                        {
                            texts[text] = at = (int)regions[2].Length;
                            regions[2].Write(p.Data);
                            regions[2].WriteByte(0);
                        }
                        target = textAt + at;
                    }
                    var value = target - position - (p.Curve ? 12 : 8);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(p.Offset), value);
                    if (p.Count >= 0) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(p.Offset + 4), p.Count);
                    if (p.Curve) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), value + p.Data.Length);
                }
                output.Write(bytes);
            }
            foreach (var region in regions) region.WriteTo(output);
            var result = output.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), items.Count);
            return result;
        }
    }
}
