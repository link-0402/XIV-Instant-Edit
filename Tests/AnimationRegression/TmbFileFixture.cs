using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using InstantEdit.Services.Audio;
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
        HeaderLength(check, reject);
        Templates(check, reject, original);
        Refusals(reject, original);
        Pap(check, reject, original);
        ReviewFixes(check, original);
        EffectLoops(check);
        EffectCopies(check, reject);
    }

    /// <summary>
    /// Async VFX (C173) loop/wait: the editor reads and writes it, and the check finds looping
    /// effects that stop after one loop (1 at frame 0, or a wait) and keeps them looping, as the
    /// Red Carpet dance's 909 needed. Effects that don't loop, aren't read, or already loop pass.
    /// </summary>
    private static void EffectLoops(Action<bool, string> check)
    {
        static Tmb Async(Tmb tmb, short id, int time, int loop, string path) =>
            tmb.Item("C173", 0x44, id, time).I32(12, loop).Text(20, path).Bind(24, 1, 0, -1).I32(28, 3).I32(32, 2);
        var timeline = new Tmb().Head(455, null, 2).Actor(2, 3).Track(3, 4, 5, 6, 7, 8);
        timeline = Async(timeline, 4, 0, 909, "vfx/mod/sparkle.avfx");
        timeline = Async(timeline, 5, 0, 909, "vfx/mod/music.avfx");
        timeline = Async(timeline, 6, 0, 1, "vfx/mod/cloud.avfx");
        timeline = Async(timeline, 7, 20, 1, "vfx/mod/cloud.avfx");
        timeline = Async(timeline, 8, 0, -1, "vfx/mod/props.avfx");
        var bytes = timeline.Build();
        var doc = TimelineConvert.Read(TmbFile.Parse(bytes));
        check(TmbEntrySchema.Get("C173").Fields.Single() is { Name: TimelineEffectCheck.LoopField, Min: -1 } &&
              doc.Events.Select(e => e.Values[TimelineEffectCheck.LoopField]).SequenceEqual([909.0, 909, 1, 1, -1]),
            "an Async VFX entry's loop/wait is an editable field");

        var loops = new Dictionary<string, TimelineEffectInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["vfx/mod/Sparkle.avfx"] = new([(0, 455)]), ["vfx/mod/music.avfx"] = new([]), ["vfx/mod/cloud.avfx"] = new([(0, 130), (10, 40)]),
            ["vfx/mod/props.avfx"] = new([(0, 455)]),
        };
        var problems = TimelineEffectCheck.Find(doc, loops);
        check(problems.Select(p => (p.Key, p.Issue, p.Value, p.Frames)).SequenceEqual(
                  [("t4", TimelineEffectIssue.Pauses, 909, 455), ("t6", TimelineEffectIssue.LoopsOnce, 1, 130)]) &&
              problems[0].Text == "sparkle plays its loop once, then pauses for 30.3 s." && problems[1].Text == "cloud plays its loop once, then stops.",
            "looping effects with a wait, or with 1 at frame 0, stop after their first loop; others pass");
        check(TimelineEffectCheck.Find(doc, new Dictionary<string, TimelineEffectInfo>()).IsEmpty,
            "an effect that couldn't be read is never judged");
        check(InstantEdit.Ui.TimelineWindowViews.EffectLoopWarning(problems) == "2 effects stop after their first loop: sparkle, cloud." &&
              InstantEdit.Ui.TimelineWindowViews.EffectLoopWarning([problems[1]]) == problems[1].Text &&
              InstantEdit.Ui.TimelineWindowViews.EffectLoopWarning([]) == null,
            "the Timeline window names the effects that stop");

        var looping = TimelineEffectCheck.Fix(doc, problems);
        var saved = TimelineConvert.Apply(bytes, looping);
        var changed = Enumerable.Range(0, bytes.Length).Count(i => saved.Length != bytes.Length || saved[i] != bytes[i]);
        check(TmbFile.Parse(saved).Entries.Select(e => e.Int32(12)).SequenceEqual([-1, 909, -1, 1, -1]) && changed == 8 &&
              TimelineEffectCheck.Find(TimelineConvert.Read(TmbFile.Parse(saved)), loops).IsEmpty &&
              ReferenceEquals(TimelineEffectCheck.Fix(looping, []), looping),
            "keeping them looping writes -1 into those entries' loop/wait and changes no other byte");

        var prop = TimelineTemplates.Create(new TimelineAddRequest(TimelineAddKind.Prop, 0, "", Prop: new TimelinePropModel(1962, 1, 1, null)));
        var withProp = TimelineEdits.Add(doc, prop, null, out var error);
        check(error == null && prop[0] is { Magic: "C173" } sync && sync.Values[TimelineEffectCheck.LoopField] == 1 &&
              TmbFile.Parse(TimelineConvert.Apply(bytes, withProp)).Entries.Count(e => e.Magic == "C173" && e.Int32(12) == 1) == 3,
            "a new prop's sync effect still plays once, as vanilla's do");

        static byte[] TimelineChunk(int start, int end) => Chunk("TmLn", [.. Chunk("LpSt", Ints(start)), .. Chunk("LpEd", Ints(end)), .. Chunk("BnNo", Ints(-1))]);
        var avfx = Chunk("AVFX", [.. Chunk("Ver", Ints(1)), .. TimelineChunk(0, 0), .. TimelineChunk(0, 455), .. TimelineChunk(-1, 20),
            .. Chunk("Emit", Chunk("LpSt", Ints(0)))]);
        check(AnimationDependencies.ReadVfxLoops(avfx).SequenceEqual([(0, 455)]) &&
              AnimationDependencies.ReadVfxLoops(Chunk("AVFX", Chunk("Ver", Ints(1)))).IsEmpty,
            "an effect loops when one of its timelines has a loop range");
    }

    /// <summary>
    /// Effects that stop even at loop/wait -1, as the Red Carpet dance's did: its effects schedule
    /// their own KILL at frame 908 and its music is marked to loop but has no loop points. The check
    /// finds both, and keeping them going swaps in a copy with the kill turned off and the sound
    /// looping, mapped beside the originals. Effects that clean up after themselves are left alone.
    /// </summary>
    private static void EffectCopies(Action<bool, string> check, Action<Action, string> reject)
    {
        const string music = "sound/mod/music.scd";
        static byte[] Field(string name, int value) => Chunk(name, Ints(value));
        static byte[] Entry(byte enabled, int start, int timeline) => [.. Chunk("bEna", [enabled]), .. Field("StTm", start), .. Field("TlNo", timeline)];
        static byte[] Item(int start, int end, int emitter, int clip) => [.. Chunk("bEna", [1]), .. Field("StTm", start), .. Field("EdTm", end),
            .. Field("BdNo", -1), .. Field("EfNo", -1), .. Field("EmNo", emitter), .. Field("Plfm", 0), .. Field("ClNo", clip)];
        // Like VFXEditor writes them: every Item chunk repeats the items before it, every Trgr chunk all items and the triggers so far.
        static byte[] Timeline(int loopEnd, bool kill, params byte[][] items) =>
        [
            .. Field("LpSt", 0), .. Field("LpEd", loopEnd), .. Field("BnNo", -1), .. Field("TICn", items.Length), .. Field("CpCn", kill ? 1 : 0),
            .. items.Select((_, i) => Chunk("Item", [.. items.Take(i + 1).SelectMany(x => x)])).SelectMany(x => x),
            .. (kill ? Chunk("Clip", [.. Ints(0x4B494C4C), .. new byte[160]]) : []),
        ];
        static byte[] Effect(byte killEnabled, int loopEnd, string? sound = null)
        {
            byte[][] items = [Entry(killEnabled, 908, 1), Entry(1, 0, 2)];
            byte[][] triggers = [Entry(1, 0, 1), .. Enumerable.Repeat(Entry(1, 0, -1), 11)];
            var scheduler = Chunk("Schd", [
                .. Field("ItCn", items.Length), .. Field("TrCn", triggers.Length),
                .. items.Select((_, i) => Chunk("Item", [.. items.Take(i + 1).SelectMany(x => x)])).SelectMany(x => x),
                .. triggers.Select((_, i) => Chunk("Trgr", [.. items.SelectMany(x => x), .. triggers.Take(i + 1).SelectMany(x => x)])).SelectMany(x => x)]);
            return Chunk("AVFX", [
                .. Chunk("Ver", Ints(1)), .. scheduler,
                .. Chunk("TmLn", Timeline(0, false, Item(0, -1, 0, -1))),
                .. Chunk("TmLn", Timeline(0, true, Item(0, 1, -1, 0))),
                .. Chunk("TmLn", Timeline(loopEnd, false, Item(213, 347, 1, -1), Item(357, 455, 2, -1))),
                .. Chunk("Emit", [.. Chunk("SdNm", [.. Encoding.ASCII.GetBytes(sound ?? ""), 0]), .. Field("SdNo", 1)])]);
        }

        var carpet = Effect(0xFF, 455, music);
        var killed = TimelineEffectFiles.WithoutScheduledKills(carpet);
        var flipped = Enumerable.Range(0, carpet.Length).Where(i => carpet[i] != killed[i]).ToList();
        check(TimelineEffectFiles.EndsAt(carpet) == 908 && TimelineEffectFiles.EndsAt(killed) == null &&
              flipped.Count == 14 && flipped.All(i => carpet[i] == 0xFF && killed[i] == 0) &&
              AnimationDependencies.ReadVfxLoops(killed).SequenceEqual([(0, 455)]),
            "an effect that keeps going but schedules its own kill ends there; turning off every copy of that item keeps it going");
        check(TimelineEffectFiles.EndsAt(Effect(0, 455)) == null && TimelineEffectFiles.EndsAt(Effect(1, 0)) == null,
            "a kill that is off, or one that cleans up after an effect that ends anyway, doesn't count");

        const string renamedSound = "sound/instant_edit/music_loop_0a1b2c3d.scd";
        var renamed = TimelineEffectFiles.WithSound(carpet, "Sound/Mod/Music.scd", renamedSound);
        static int Padded(string text) => (text.Length + 1 + 3) & ~3;
        check(AnimationDependencies.ReadVfxPaths(renamed).Sounds.SequenceEqual([renamedSound]) &&
              renamed.Length == carpet.Length + Padded(renamedSound) - Padded(music) &&
              TimelineEffectFiles.EndsAt(renamed) == 908 && AnimationDependencies.ReadVfxLoops(renamed).SequenceEqual([(0, 455)]),
            "renaming an emitter's sound rewrites only that name and the sizes around it");
        reject(() => TimelineEffectFiles.WithSound(carpet, "sound/other.scd", renamedSound), "renaming a sound the effect doesn't play is refused");

        var scd = AudioFixture.Template();
        var length = ScdFile.Read(scd).Audio[0].Seconds;
        var looped = TimelineEffectFiles.WithLoopPoints(scd, 0);
        var audio = ScdFile.Read(looped).Audio[0];
        var unmarked = (byte[])scd.Clone();
        unmarked[ScdFile.Read(scd).Sounds[0].Offset + 4] &= 0xFE;
        check(TimelineEffectFiles.UnloopedSeconds(scd, 0) == length && TimelineEffectFiles.UnloopedSeconds(scd, length + 1) == null &&
              TimelineEffectFiles.UnloopedSeconds(unmarked, 0) == null && TimelineEffectFiles.UnloopedSeconds(looped, 0) == null &&
              audio is { LoopStart: 0 } && audio.LoopEnd == (int)Math.Round(length * audio.SampleRate) &&
              Enumerable.Range(0, scd.Length).Count(i => scd[i] != looped[i]) <= 4,
            "a sound marked to loop without loop points plays once; loop points over all of it (MS-ADPCM in samples) make it loop");

        var copy = TimelineEffectFiles.LoopingCopy("vfx/mod/carpet.avfx", carpet, true, [(music, scd)], 0);
        var soundCopy = copy.Files.Keys.Single(k => k.EndsWith(".scd"));
        check(copy.GamePath.StartsWith("vfx/instant_edit/carpet_loop_") && soundCopy.StartsWith("sound/instant_edit/music_loop_") && copy.Files.Count == 2 &&
              copy.Beside is { } beside && beside[copy.GamePath] == "vfx/mod/carpet.avfx" && beside[soundCopy] == music &&
              TimelineEffectFiles.EndsAt(copy.Files[copy.GamePath]) == null &&
              AnimationDependencies.ReadVfxPaths(copy.Files[copy.GamePath]).Sounds.SequenceEqual([soundCopy]) &&
              TimelineEffectFiles.UnloopedSeconds(copy.Files[soundCopy], 0) == null,
            "the looping copy turns off the kill, plays a looping copy of the sound, and names what each copy replaces");

        static Tmb Async(Tmb tmb, short id, int time, int loop, string path) =>
            tmb.Item("C173", 0x44, id, time).I32(12, loop).Text(20, path).Bind(24, 1, 0, -1).I32(28, 3).I32(32, 2);
        var timeline = new Tmb().Head(455, null, 2).Actor(2, 3).Track(3, 4, 5, 6, 7);
        timeline = Async(timeline, 4, 0, 909, "vfx/mod/carpet.avfx");
        timeline = Async(timeline, 5, 0, -1, "vfx/mod/nocopy.avfx");
        timeline = Async(timeline, 6, 20, 1, "vfx/mod/carpet.avfx");
        timeline = Async(timeline, 7, 0, 1, "vfx/mod/cloud.avfx");
        var bytes = timeline.Build();
        var doc = TimelineConvert.Read(TmbFile.Parse(bytes));
        var effects = new Dictionary<string, TimelineEffectInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["vfx/mod/carpet.avfx"] = new([(0, 455)], 908, 30.334, copy),
            ["vfx/mod/nocopy.avfx"] = new([(0, 455)], 908),
            ["vfx/mod/cloud.avfx"] = new([(0, 130)]),
        };
        var problems = TimelineEffectCheck.Find(doc, effects);
        check(problems.Select(p => (p.Key, p.Issue, p.Frames)).SequenceEqual([("t4", TimelineEffectIssue.Pauses, 455),
                  ("t4", TimelineEffectIssue.EndsItself, 908), ("t4", TimelineEffectIssue.SoundStops, 910), ("t7", TimelineEffectIssue.LoopsOnce, 130)]) &&
              problems[1].Text == "carpet ends itself after 30.3 s." && problems[2].Text == "carpet's sound stops after 30.3 s.",
            "an effect that ends itself or whose sound stops is found when a copy that keeps it going exists");
        check(InstantEdit.Ui.TimelineWindowViews.EffectLoopWarning(problems) == "2 effects stop: carpet, cloud." &&
              InstantEdit.Ui.TimelineWindowViews.EffectLoopWarning([.. problems.Take(2)]) ==
                  "carpet plays its loop once, then pauses for 30.3 s. carpet ends itself after 30.3 s.",
            "the warning names each effect once and says how one effect stops");

        var fixedDoc = TimelineEffectCheck.Fix(doc, problems);
        var saved = TmbFile.Parse(TimelineConvert.Apply(bytes, fixedDoc)).Entries;
        check(saved.Select(e => (e.Text, e.Int32(12))).SequenceEqual([(copy.GamePath, -1), ("vfx/mod/nocopy.avfx", -1), ("vfx/mod/carpet.avfx", 1),
                  ("vfx/mod/cloud.avfx", -1)]) &&
              TimelineEffectCheck.Copies(problems).Single() == copy && TimelineEffectCheck.Find(fixedDoc, effects).IsEmpty,
            "keeping them going plays the copy at -1 instead; entries that restart themselves keep the original");
        var added = InstantEdit.Ui.TimelineWindowViews.AddedFiles(fixedDoc, [copy]);
        var addedBeside = InstantEdit.Ui.TimelineWindowViews.AddedBeside(fixedDoc, [copy]);
        check(added.Count == 2 && addedBeside.Count == 2 && addedBeside[soundCopy] == music &&
              InstantEdit.Ui.TimelineWindowViews.AddedBeside(doc, [copy]).IsEmpty,
            "a save packages the copies and maps each beside its original, only while the timeline uses them");
    }

    /// <summary>An AVFX chunk: the name stored reversed, the payload's size, the payload, padding to 4.</summary>
    private static byte[] Chunk(string name, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + ((-payload.Length) & 3)];
        Encoding.ASCII.GetBytes(new string(name.Reverse().ToArray())).CopyTo(chunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
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
        reject(() => TimelineConvert.Apply(original, doc with { HeaderLength = 90 }), "a document of another timeline is refused");
        reject(() => TimelineConvert.Apply(original, doc with { SourceLength = 99 }),
            "a document loaded with a motion length its timeline would not take is refused");
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

    /// <summary>
    /// A mod's header length (TMDH) that isn't its motion's, like Red Carpet's Popoto Step dance: a
    /// 455-frame motion under a header copied from a 30-frame walk, laid out for two loops. The
    /// document takes the motion's length, saving keeps the header unless the length changes (then
    /// it scales with the motion), and a length change retimes the real motion.
    /// </summary>
    private static void HeaderLength(Action<bool, string> check, Action<Action, string> reject)
    {
        check(TimelineDocument.MotionFrames(455 / 30f) == 455 && TimelineDocument.MotionFrames(1 / 60f) == 1 &&
              TimelineDocument.MotionFrames(0) == null && TimelineDocument.MotionFrames(.01f) == null &&
              TimelineDocument.MotionFrames(-1) == null && TimelineDocument.MotionFrames(float.NaN) == null &&
              TimelineDocument.MotionFrames(1200) == null,
            "a motion's length in seconds becomes whole frames, or nothing when unknown or out of range");

        var dance = new Tmb().Head(30, null, 2).Actor(2, 3, 4, 5, 6).Track(3, 7).Track(4, 8, 9).Track(5, 10, 11).Track(6, 12)
            .Motion(7, 455).Sound(8, 0).Sound(9, 200).Face(10, 0, 55, 0, 55, "cfxf_bossy").Face(11, 455, 55, 0, 55, "cfxf_bossy")
            .Sound(12, 907).Build();
        var header = TimelineConvert.Read(TmbFile.Parse(dance));
        var doc = TimelineConvert.Read(TmbFile.Parse(dance), TimelineDocument.MotionFrames(455 / 30f));
        check(header is { Length: 30, SourceLength: 30, HeaderLength: 30 } &&
              doc is { Length: 455, SourceLength: 455, HeaderLength: 30, LengthChanged: false } &&
              doc.Events.Select(e => (e.Key, e.Start, e.Duration)).SequenceEqual(header.Events.Select(e => (e.Key, e.Start, e.Duration))),
            "a timeline takes its motion's length over a header that says otherwise, and the header's without one");
        check(TimelineConvert.Read(TmbFile.Parse(dance), 31) is { Length: 30, HeaderLength: 30 } &&
              TimelineConvert.Read(TmbFile.Parse(dance), 29).Length == 30 && TimelineConvert.Read(TmbFile.Parse(dance), 0).Length == 30,
            "a motion one frame off the header is the header's length counted differently, as in a few vanilla timelines");
        check(TimelineEdits.PastEnd(doc).Select(e => e.Key).SequenceEqual(["t11", "t12"]) && TimelineEdits.PastEnd(header).Length == 3,
            "only events past the motion's end count as past the end");

        check(ReferenceEquals(TimelineConvert.Apply(dance, doc), dance), "an unchanged document gives back the original bytes");
        var moved = TmbFile.Parse(TimelineConvert.Apply(dance, doc with
        {
            Events = [.. doc.Events.Select(e => e.Key == "t9" ? e with { Start = 210 } : e)],
        }));
        check(moved.Length == 30 && moved.Find(7)!.Int32(12) == 455 && moved.Find(9)!.Time == 210,
            "an edit that keeps the length keeps the mod's header length");

        var doubled = TimelineEdits.SetLength(doc, 910, false, doc);
        var saved = TmbFile.Parse(TimelineConvert.Apply(dance, doubled));
        check(doubled.Events[0].Duration == 910 && saved.Find(7)!.Int32(12) == 910 && saved.Find(12)!.Time == 907 &&
              AnimationEditService.RetimeSeconds(doubled) is { } seconds && Math.Abs(seconds - 910 / 30f) < 1e-4 &&
              AnimationEditService.RetimeSeconds(doc) == null,
            "doubling the length makes the 15 s motion 30 s, not 2 s from the header's 1 s");
        check(saved.Length == 60 && doubled.SavedHeaderLength == 60,
            "a mod's own header length scales with the motion");
        check(TmbFile.Parse(TimelineConvert.Apply(dance, doc with { Length = 600 })) is { Length: 40 } longer && longer.Find(7)!.Int32(12) == 600,
            "the clip's motion entry follows a length change on its own");
        // A 2 s pose loop whose header and events run past the motion, as some mods lay them out.
        var pose = new Tmb().Head(369, null, 2).Actor(2, 3, 4).Track(3, 5).Track(4, 6).Motion(5, 60).Sound(6, 351).Build();
        var poseDoc = TimelineConvert.Read(TmbFile.Parse(pose), 60);
        check(poseDoc is { Length: 60, HeaderLength: 369 } &&
              TmbFile.Parse(TimelineConvert.Apply(pose, TimelineEdits.SetLength(poseDoc, 120, true, poseDoc))) is { Length: 738 } posed &&
              posed.Find(6)!.Time == 702 && posed.Find(5)!.Int32(12) == 120,
            "a header longer than its motion stays as much longer, so the events laid out up to it still fit");
        var empty = new Tmb().Head(0, null, 2).Actor(2, 3).Track(3, 4).Motion(4, 60).Build();
        check(TimelineEdits.SetLength(TimelineConvert.Read(TmbFile.Parse(dance), 31), 50, false).SavedHeaderLength == 50 &&
              TimelineEdits.SetLength(TimelineConvert.Read(TmbFile.Parse(pose)), 400, false).SavedHeaderLength == 400 &&
              TimelineConvert.Read(TmbFile.Parse(empty), 60) is { Length: 60, HeaderLength: 0 } emptyDoc &&
              TmbFile.Parse(TimelineConvert.Apply(empty, emptyDoc with { Length = 90 })).Length == 90,
            "a header that matched the motion, said 0 or is the only length known becomes the new length");
        var back = TimelineEdits.SetLength(doubled, 455, false, doc);
        check(!back.LengthChanged && ReferenceEquals(TimelineConvert.Apply(dance, back), dance),
            "going back to the motion's length leaves the file as it was, header included");

        var counted = new Tmb().Head(30, null, 2).Actor(2, 3).Track(3, 4).Motion(4, 456).Build();
        var countedDoc = TimelineConvert.Read(TmbFile.Parse(counted), 455);
        check(countedDoc.Length == 455 && TimelineEdits.SetLength(countedDoc, 910, false, countedDoc).Events[0].Duration == 910 &&
              TmbFile.Parse(TimelineConvert.Apply(counted, countedDoc with { Length = 910 })) is { Length: 60 } stretched &&
              stretched.Find(4)!.Int32(12) == 910,
            "a motion entry one frame longer than its motion still lasts the whole clip");
        reject(() => TimelineConvert.Apply(dance, doc with { HeaderLength = 455 }), "a document whose header length isn't the file's is refused");
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
