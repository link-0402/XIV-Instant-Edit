using System.Collections.Immutable;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Props recognised on load: a summon and its sync effect, animation, fades and controls share one group, as the
/// six vanilla emote shapes lay them out (research notes §7). The group resizes as one, and Add writes vanilla's fade-out.
/// </summary>
internal static partial class TmbFileFixture
{
    private const string GameFolder = @"E:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";

    private static void Props(Action<bool, string> check, Action<Action, string> reject)
    {
        var shapes = PropShapes();
        foreach (var (name, bytes, expected) in shapes)
            check(GroupsMatch(TimelineConvert.Read(TmbFile.Parse(bytes)), expected, out var got), $"{name}'s prop parts share one group ({got})");

        var navigate = Navigate(true);
        check(GroupsMatch(TimelineConvert.Read(TmbFile.Parse(navigate)), NavigateGroups(true), out var navigated),
            $"a fade inside both props' spans goes to the first, and a control joins the prop whose slot it targets ({navigated})");
        var unrelated = Sp50(extras: true);
        check(GroupsMatch(TimelineConvert.Read(TmbFile.Parse(unrelated)), Sp50Groups(), out var loose),
            $"a fade for everyone, and a prop fade outside the prop's span, belong to no prop ({loose})");

        var fades = TmbFile.Parse(unrelated);
        check(fades.Find(13)!.FilterFlags() == (1, 8) && fades.Find(21)!.FilterFlags() == (1, 15) && fades.Find(23)!.FilterFlags() == null &&
              fades.Find(19)!.FilterFlags() == null,
            "a fade's filter reads as its on flag and the objects it applies to; none without a filter block");

        Vanilla(check, shapes);
        GroupResize(check);
        PropTemplates(check);
        OlderFields(check, reject);
    }

    /// <summary>
    /// Edits kept by an older build (an autosave) lack the fields this build reads (C198 slot and attach state):
    /// they come from the loaded file's entry, or 0 for an added event, so saving never refuses them.
    /// </summary>
    private static void OlderFields(Action<bool, string> check, Action<Action, string> reject)
    {
        var bytes = new Tmb().Head(94, null, 2).Actor(2, 3).Track(3, 19, 20).Summon(19, 0, 81, 0, 1922).U8(29, 3).Motion(20, 94).Build();
        var loaded = TimelineConvert.Read(TmbFile.Parse(bytes));
        var summon = loaded.Events[0];
        var oldValues = summon.Values.Remove("SummonId").Remove("AtchState");
        var kept = loaded with
        {
            Events = [summon with { Values = oldValues, Duration = 70 }, loaded.Events[1], summon with { Key = "n1", Values = oldValues, Group = null, Start = 10 }],
            NextKey = 2,
        };
        reject(() => TimelineConvert.Apply(bytes, kept), "an event without this build's fields is refused as it is");
        var filled = TimelineEdits.FillFields(kept, loaded);
        check(filled.Events[0].Values["AtchState"] == 3 && filled.Events[0].Values["SummonId"] == 0 && filled.Events[2].Values["AtchState"] == 0 &&
              filled.Events[0].Values["Model"] == 1922 && ReferenceEquals(TimelineEdits.FillFields(loaded, loaded), loaded),
            "missing fields come from the loaded entry, or 0 for an added one; a document with all of them stays as it is");
        var saved = TmbFile.Parse(TimelineConvert.Apply(bytes, filled));
        var summons = saved.Entries.Where(e => e.Magic == "C198").ToList();
        check(summons.Count == 2 && summons.Single(e => e.Time == 0) is { } first && first.Int32(12) == 70 && first.Byte(29) == 3 &&
              summons.Single(e => e.Time == 10).Byte(29) == 0 && summons.All(e => e.Byte(28) == 0),
            "an older autosave's prop saves, keeping the file's attach state");
    }

    /// <summary>The installed game's c0101 emote files group as the synthetic shapes do.</summary>
    private static void Vanilla(Action<bool, string> check, (string Name, byte[] Bytes, Dictionary<short, string> Groups)[] shapes)
    {
        if (!Directory.Exists(GameFolder))
        {
            Console.WriteLine("[SKIP] prop groups: the game isn't installed where the checks look, so the vanilla emotes aren't read");
            return;
        }
        using var game = new Lumina.GameData(GameFolder, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        foreach (var (name, _, expected) in shapes)
        {
            var path = $"chara/human/c0101/animation/a0001/bt_common/emote_sp/{name}.pap";
            if (game.GetFile(path)?.Data is not { } data)
            {
                check(false, $"the game has {path}");
                continue;
            }
            var doc = TimelineConvert.Read(TmbFile.Parse(new AnimationPap(data).TimelineBytes(0)));
            check(GroupsMatch(doc, expected, out var got), $"vanilla {name}'s prop parts group as the research notes list them ({got})");
        }
    }

    private static void GroupResize(Action<bool, string> check)
    {
        var sp50 = Sp50();
        var doc = TimelineConvert.Read(TmbFile.Parse(sp50));
        TimelineEvent At(TimelineDocument d, short id) => TimelineEdits.Find(d, "t" + id)!;

        var longer = TimelineEdits.ResizeGroup(doc, "t19", false, 30);
        check(At(longer, 19).Duration == 111 && At(longer, 15) is { Start: 102, Duration: 9 } && At(longer, 16) is { Start: 111, Duration: 5 } &&
              At(longer, 17).Start == 5 && At(longer, 12).Start == 0 && At(longer, 13) is { Start: 0, Duration: 5 } && At(longer, 14) is { Start: 5, Duration: 14 },
            "a longer prop keeps its fade-out at its end and the hold after it; the start parts stay");
        var shortest = TimelineEdits.ResizeGroup(doc, "t19", false, -80);
        check(At(shortest, 19).Duration == 1 && At(shortest, 15) is { Start: 0, Duration: 1 } && At(shortest, 16) is { Start: 1, Duration: 5 } &&
              At(shortest, 17).Start == 5 && At(shortest, 14) is { Start: 5, Duration: 14 },
            "a prop shortened past its fade-out lasts a frame, its fade-out squeezed into it");
        var squeezedSaved = TimelineConvert.Read(TmbFile.Parse(TimelineConvert.Apply(sp50, shortest)));
        check(squeezedSaved.Events.Single(e => e.Magic == "C198") is { Duration: 1, Group: { } squeezedGroup } &&
              squeezedSaved.Events.Count(e => e.Group == squeezedGroup) == 7,
            "a prop squeezed to a frame saves and reads back with every part, its fade-in after its end included");
        var regrown = TimelineEdits.ResizeGroup(shortest, "t19", false, 80);
        check(At(regrown, 19).Duration == 81 && At(regrown, 15) is { Start: 80, Duration: 1 } && At(regrown, 16) is { Start: 81, Duration: 5 } &&
              At(regrown, 13) is { Start: 0, Duration: 5 },
            "a squeezed fade-out still follows the prop's end when it grows again, and its hold after it");
        var shortProp = TimelineEdits.Add(TimelineConvert.Read(TmbFile.Parse(Sp50())) with { Events = [] },
            TimelineTemplates.Create(new(TimelineAddKind.Prop, 0, "", 12, Prop: new TimelinePropModel(1922, 1, 1, 235), ClipLength: 94)), null, out _);
        var shortSummon = shortProp.Events.Single(e => e.Magic == "C198").Key;
        var shortLonger = TimelineEdits.ResizeGroup(shortProp, shortSummon, false, 20);
        check(shortLonger.Events.Any(e => e.Magic == "C094" && e.Start == 27 && e.Duration == 5 && e.Values["StartVisibility"] == 1) &&
              shortLonger.Events.Any(e => e.Magic == "C094" && e.Start == 32 && e.Duration == 82 && e.Values["StartVisibility"] == 0 && e.Values["EndVisibility"] == 0) &&
              shortLonger.Events.Single(e => e.Magic == "C031").Start == 5,
            "a short added prop's fade-out follows its end though it starts in the prop's first frames");
        var later = TimelineEdits.ResizeGroup(doc, "t19", true, 10);
        check(At(later, 19) is { Start: 10, Duration: 81 } && new short[] { 12, 13, 14, 15, 16, 17 }.All(id => At(later, id).Start == At(doc, id).Start + 10) &&
              At(later, 20).Start == 0 && At(later, 15).Duration == 9,
            "dragging a prop's left edge moves all of its parts");
        check(ReferenceEquals(TimelineEdits.ResizeGroup(doc, "t19", false, 0), doc) &&
              TimelineEdits.ResizeGroup(doc, "t15", false, 3) is var single && At(single, 15).Duration == 12 && At(single, 19).Duration == 81,
            "a part of a prop resizes on its own");

        var saved = TimelineConvert.Apply(sp50, longer);
        var reread = TimelineConvert.Read(TmbFile.Parse(saved));
        // A rewrite numbers entries again, so the group is named after the summon's new id.
        check(reread.Events.Single(e => e.Magic == "C198") is { Duration: 111, Group: { } regrouped } && reread.Events.Count(e => e.Group == regrouped) == 7 &&
              reread.Events.Single(e => e.Magic == "C094" && e.Start == 102).Duration == 9,
            "a resized prop saves, and reads back as one group");

        // Review Focus 3: an animation a frame after the spawn still belongs, and keeps its frame.
        var early = TimelineConvert.Read(TmbFile.Parse(Sp50(animation: 1)));
        check(At(early, 17).Group == "p19" && At(TimelineEdits.ResizeGroup(early, "t19", false, 30), 17).Start == 1 &&
              At(TimelineEdits.ResizeGroup(early, "t19", false, -80), 17).Start == 1,
            "an animation 1 frame after the spawn is part of the prop and keeps that frame through a resize");

        var toast = TimelineConvert.Read(TmbFile.Parse(Toast()));
        var shorter = TimelineEdits.ResizeGroup(toast, "t20", false, -100);
        check(At(shorter, 20) is { Start: 60, Duration: 25 } && At(shorter, 17) is { Start: 78, Duration: 5 } && At(shorter, 18) is { Start: 83, Duration: 17 } &&
              At(shorter, 14).Start == 3 && At(shorter, 19) is { Start: 0, Duration: 60 } && At(shorter, 16).Start == 5,
            "Toast shortened keeps its animation at frame 3, the hand-off summon as it was and the fade-out at the new end");

        var sp54 = Sp54();
        var chicken = TimelineConvert.Read(TmbFile.Parse(sp54));
        var cut = TimelineEdits.ResizeGroup(chicken, "t21", false, -100);
        check(At(cut, 21).Duration == 160 && At(cut, 16) is { Start: 71, Duration: 203 } && At(cut, 19).Start == 145 && At(cut, 20).Start == 152,
            "a part that can only move keeps its length when the prop shortens");
        var cutSaved = TmbFile.Parse(TimelineConvert.Apply(sp54, cut));
        check(cutSaved.Entries.Single(e => e.Magic == "C198").Int32(12) == 160 && cutSaved.Entries.Single(e => e.Magic == "C187").Int32(12) == 203,
            "a shortened prop with a removed part saves");
        var tooShort = TimelineEdits.ResizeGroup(chicken, "t21", false, -250);
        check(At(tooShort, 21).Duration == 10 && At(tooShort, 16) is { Start: 9, Duration: 203 },
            "a part that starts past the prop's new end moves inside it");
    }

    private static void PropTemplates(Action<bool, string> check)
    {
        var prop = TimelineTemplates.Create(new(TimelineAddKind.Prop, 0, "", 60, Prop: new TimelinePropModel(1922, 1, 1, 235), ClipLength: 200));
        bool Fade(TimelineEvent e, int start, int duration, double from, double to) =>
            e is { Magic: "C094" } && e.Start == start && e.Duration == duration && e.Values["StartVisibility"] == from && e.Values["EndVisibility"] == to;
        check(prop.Any(e => Fade(e, 55, 5, 1, 0)) && prop.Any(e => Fade(e, 60, 140, 0, 0)) && prop.Select(e => e.Group).Distinct().Count() == 1,
            "an added prop fades out over its last 5 frames and stays hidden to the clip's end");
        var still = TimelineTemplates.Create(new(TimelineAddKind.Prop, 150, "", 60, Prop: new TimelinePropModel(1961, 1, 1, null), ClipLength: 200));
        check(still.Any(e => Fade(e, 205, 5, 1, 0)) && !still.Any(e => e.Magic == "C094" && e.Start >= 210),
            "a still prop fades out too, and gets no hold when it lasts past the clip");
        var toTheEnd = TimelineTemplates.Create(new(TimelineAddKind.Prop, 140, "", 60, Prop: new TimelinePropModel(1961, 1, 1, null), ClipLength: 200));
        check(toTheEnd.Any(e => Fade(e, 195, 5, 1, 0)) && toTheEnd.Count(e => e.Magic == "C094") == 1,
            "a prop that ends with the clip gets no hold after it");

        // A prop shorter than its opening keeps its hide and fade-in, which start after its end.
        var bare = new Tmb().Head(94, null, 2).Actor(2, 3).Track(3, 20).Motion(20, 94).Build();
        var blink = TimelineTemplates.Create(new(TimelineAddKind.Prop, 0, "", 1, Prop: new TimelinePropModel(1922, 1, 1, 235), ClipLength: 94));
        var blinkSaved = TimelineConvert.Read(TmbFile.Parse(TimelineConvert.Apply(bare,
            TimelineEdits.Add(TimelineConvert.Read(TmbFile.Parse(bare)), blink, null, out _))));
        check(blinkSaved.Events.Single(e => e.Magic == "C198") is { Duration: 1, Group: { } blinkGroup } &&
              blinkSaved.Events.Count(e => e.Group == blinkGroup) == blink.Length && blink.Length == 7,
            "a prop added with a length of 1 reads back with all of its parts");
        var summon = prop.Single(e => e.Magic == "C198");
        check(summon.Values["SummonId"] == 0 && summon.Values["AtchState"] == 0 && prop.Single(e => e.Magic == "C031").Values["TargetType"] == 2,
            "an added prop is summon 0 at its default attach state, animated on that summon");

        var bytes = Sp50();
        var doc = TimelineConvert.Read(TmbFile.Parse(bytes));
        var added = TimelineEdits.Add(doc, prop, null, out var error);
        var written = TmbFile.Parse(TimelineConvert.Apply(bytes, added));
        var addedFades = written.Entries.Where(e => e.Magic == "C094").Skip(4).ToList();
        check(error == null && addedFades.Count == 4 && addedFades.All(e => e.FilterFlags() == (1, 8)) &&
              addedFades.Any(e => e.Time == 55 && e.Int32(12) == 5 && e.Single(20) == 1 && e.Single(24) == 0) &&
              addedFades.Any(e => e.Time == 60 && e.Int32(12) == 140 && e.Single(20) == 0 && e.Single(24) == 0),
            "the added fade-out and hold are written for the summon only");

        var control = new TimelineEvent("n1", "C014", TimelineLane.Prop, 1, 0, false, "", TimelineAccess.Movable, TmbEntrySchema.MovableReason, false,
            ImmutableDictionary<string, double>.Empty.Add("Enabled", 1).Add("ObjectPosition", 2).Add("ObjectControl", 2), "p19");
        var positioned = TmbFile.Parse(TimelineConvert.Apply(bytes, doc with { Events = doc.Events.Add(control), NextKey = 2 }));
        check(TimelineTemplates.CanCreate("C014") && positioned.Entries.Single(e => e.Magic == "C014") is { Time: 1 } c014 &&
              c014.Int32(12) == 1 && c014.Int32(20) == 2 && c014.Int32(24) == 2,
            "an added weapon position writes whether it is on, the attach state and the object it moves");
    }

    private static bool GroupsMatch(TimelineDocument doc, Dictionary<short, string> expected, out string got)
    {
        got = string.Join(" ", doc.Events.Where(e => e.Group != null).Select(e => $"{e.Key}:{e.Group}"));
        return doc.Events.All(e => e.Group == expected.GetValueOrDefault(short.Parse(e.Key.AsSpan(1))));
    }

    private static Dictionary<short, string> Group(string id, params short[] members) => members.ToDictionary(m => m, _ => id);

    private static (string Name, byte[] Bytes, Dictionary<short, string> Groups)[] PropShapes() =>
    [
        ("sp19", Toast(), Group("p19", 13, 14, 15, 16, 17, 18, 19, 20)),
        ("sp23", new Tmb().Head(460, null, 2).Actor(2, 3).Track(3, 8, 9, 10, 11, 12)
            .Sync(8).Fade(9, 0, 9, 0, 0).Fade(10, 9, 5, 0, 1).Summon(11, 0, 460, 0, 1961).Motion(12, 460).Build(), Group("p11", 8, 9, 10, 11)),
        ("sp50", Sp50(), Sp50Groups()),
        ("sp54", Sp54(), Group("p21", 14, 15, 16, 17, 18, 19, 20, 21)),
        ("sp62_loop", new Tmb().Head(86, null, 2).Actor(2, 3).Track(3, 11, 12, 13, 15)
            .Sync(11).Summon(12, 43, 43, 0, 1948).Weapon(13, 0, 43, 1948).Motion(15, 86).Build(), Group("p13", 11, 12, 13)),
        ("sp75", Navigate(false), NavigateGroups(false)),
    ];

    /// <summary>Toast (c0101 sp19): a summoned weapon hands the mug to a summon at frame 60; the animation 3 frames in.</summary>
    private static byte[] Toast() => new Tmb().Head(200, null, 2).Actor(2, 3).Track(3, 12, 13, 14, 15, 16, 17, 18, 19, 20, 28)
        .Effect(12).Sync(13).Animation(14, 3, 388, 2).Fade(15, 0, 5, 0, 0).Fade(16, 5, 5, 0, 1).Fade(17, 178, 5, 1, 0).Fade(18, 183, 17, 0, 0)
        .Weapon(19, 0, 60, 1962).Summon(20, 60, 125, 0, 1962).Motion(28, 200).Build();

    /// <summary>
    /// sp50: the prop ends where its fade-out ends (72 + 9 = 81). With <paramref name="extras"/>, a fade for everyone at 30,
    /// a prop fade past the span at 90 and one with no filter block at 40.
    /// </summary>
    private static byte[] Sp50(int animation = 5, bool extras = false)
    {
        var tmb = new Tmb().Head(94, null, 2).Actor(2, 3).Track(3, [12, 13, 14, 15, 16, 17, 19, 20, .. extras ? new short[] { 21, 22, 23 } : []])
            .Sync(12).Fade(13, 0, 5, 0, 0).Fade(14, 5, 14, 0, 1).Fade(15, 72, 9, 1, 0).Fade(16, 81, 5, 0, 0).Animation(17, animation, 235, 2)
            .Summon(19, 0, 81, 0, 1922).Motion(20, 94);
        if (extras)
            tmb.Fade(21, 30, 5, 1, 0, 15).Fade(22, 90, 4, 0, 0).Fade(23, 40, 5, 1, 0, -1);
        return tmb.Build();
    }

    private static Dictionary<short, string> Sp50Groups() => Group("p19", 12, 13, 14, 15, 16, 17, 19);

    /// <summary>sp54: a removed part (C187) from frame 71 to the clip's end, past the prop's own end at 260.</summary>
    private static byte[] Sp54() => new Tmb().Head(274, null, 2).Actor(2, 3).Track(3, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22)
        .Effect(13).Animation(14, 5, 274, 2).Sync(15).Part(16, 71, 203, 1).Fade(17, 0, 5, 0, 0).Fade(18, 5, 7, 0, 1).Fade(19, 245, 7, 1, 0)
        .Fade(20, 252, 22, 0, 0).Summon(21, 0, 260, 0, 1926).Motion(22, 274).Build();

    /// <summary>
    /// Navigate (sp75): two props, summon 0 from 0 to 443 and summon 1 from 173 for 220. With <paramref name="extras"/>, a prop fade
    /// at 200 (inside both spans) and two weapon positions, one per summon slot.
    /// </summary>
    private static byte[] Navigate(bool extras)
    {
        var tmb = new Tmb().Head(463, null, 2).Actor(2, 3).Track(3, [16, 17, 18, 19, 20, 21, 22, 23, 24, .. extras ? new short[] { 25, 26, 27 } : []])
            .Motion(16, 463).Fade(17, 0, 35, 0, 0).Fade(18, 35, 10, 0, 1).Fade(19, 417, 5, 1, 0).Fade(20, 422, 41, 0, 0).Sync(21)
            .Animation(22, 5, 402, 2).Summon(23, 173, 220, 1, 1970).Summon(24, 0, 443, 0, 1969);
        if (extras)
            tmb.Fade(25, 200, 5, 1, 0).Control("C014", 26, 174, 1, 3).Control("C014", 27, 1, 1, 2);
        return tmb.Build();
    }

    private static Dictionary<short, string> NavigateGroups(bool extras)
    {
        var groups = Group("p24", [17, 18, 19, 20, 21, 22, 24, .. extras ? new short[] { 25, 27 } : []]);
        groups[23] = "p23";
        if (extras) groups[26] = "p23";
        return groups;
    }
}
