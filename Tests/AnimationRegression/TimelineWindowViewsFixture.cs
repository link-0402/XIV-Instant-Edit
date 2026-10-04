using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;

/// <summary>
/// The Timeline window's pure texts and choices: lane names and colours, event labels, why a save
/// is blocked, the catalog key, the files a save packages, typed game paths and expression packs.
/// </summary>
internal static class TimelineWindowViewsFixture
{
    private static readonly ImmutableDictionary<string, double> NoValues = ImmutableDictionary<string, double>.Empty;

    private static TimelineEvent Ev(string key, string magic, TimelineLane lane, int start, string path = "", int duration = 0,
        ImmutableDictionary<string, double>? values = null, TimelineAccess access = TimelineAccess.Editable) =>
        new(key, magic, lane, start, duration, duration > 0, path, access, null, false, values ?? NoValues);

    private static ImmutableDictionary<string, double> Values(params (string Name, double Value)[] values) =>
        values.ToImmutableDictionary(v => v.Name, v => v.Value);

    private static TimelineDocument Doc(int length, int sourceLength, params TimelineEvent[] events) => new(length, sourceLength, null, [.. events], 1);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var texts = new List<string>();
        Lanes(check, texts);
        Labels(check, texts);
        SaveProblems(check, texts);
        CatalogKeys(check);
        AddedFiles(check);
        GamePaths(check, texts);
        Packs(check, texts);
        Texts(check, texts);
        check(texts.All(text => !text.Contains("...", StringComparison.Ordinal) && !text.Contains('\u2026')),
            "Timeline window texts never use an ellipsis");
    }

    private static void Lanes(Action<bool, string> check, List<string> texts)
    {
        var lanes = Enum.GetValues<TimelineLane>();
        var names = lanes.Select(TimelineWindowViews.LaneName).ToArray();
        texts.AddRange(names);
        check(names.Length == 7 && names.All(name => name.Length > 0) && names.Distinct().Count() == 7,
            "Every lane has its own name");
        check(names.SequenceEqual(["Motion", "Expressions", "Lip sync", "Sounds", "Effects", "Props", "Other"]),
            "Lane names read Motion, Expressions, Lip sync, Sounds, Effects, Props, Other");
        check(lanes.Select(TimelineWindowViews.LaneColour).Distinct().Count() == 7, "Every lane has its own colour");
        check(TimelineWindowViews.LaneColour((TimelineLane)42) == TimelineWindowViews.LaneColour(TimelineLane.Other),
            "An unknown lane is drawn like Other");
        check(TimelineWindowViews.AddKind(TimelineLane.Motion) == null && TimelineWindowViews.AddKind(TimelineLane.Other) == null,
            "Nothing is added to the Motion and Other lanes");
        check(TimelineWindowViews.AddKind(TimelineLane.Expression) == TimelineAddKind.Expression &&
              TimelineWindowViews.AddKind(TimelineLane.LipSync) == TimelineAddKind.LipSync &&
              TimelineWindowViews.AddKind(TimelineLane.Sound) == TimelineAddKind.Sound &&
              TimelineWindowViews.AddKind(TimelineLane.Effect) == TimelineAddKind.Effect &&
              TimelineWindowViews.AddKind(TimelineLane.Prop) == TimelineAddKind.Prop,
            "A double click on a lane adds that lane's kind");
        var kinds = TimelineWindowViews.AddKinds.Select(TimelineWindowViews.AddKindName).ToArray();
        texts.AddRange(kinds);
        check(kinds.SequenceEqual(["Expression", "Lip sync", "Sound", "Effect", "Prop"]), "The Add menu lists the five kinds in order");

        check(TimelineWindowViews.PickerFor(Ev("a", "C063", TimelineLane.Sound, 0, "sound/a.scd")) == TimelineAddKind.Sound &&
              TimelineWindowViews.PickerFor(Ev("b", "C012", TimelineLane.Effect, 0, "vfx/a.avfx", 30)) == TimelineAddKind.Effect &&
              TimelineWindowViews.PickerFor(Ev("c", "C173", TimelineLane.Effect, 0, "vfx/b.avfx")) == TimelineAddKind.Effect &&
              TimelineWindowViews.PickerFor(Ev("d", "C198", TimelineLane.Prop, 0, "", 60)) == TimelineAddKind.Prop &&
              TimelineWindowViews.PickerFor(Ev("e", "C010", TimelineLane.Expression, 0, "cfxf_smile", 30)) == TimelineAddKind.Expression &&
              TimelineWindowViews.PickerFor(Ev("f", "C010", TimelineLane.LipSync, 0, "cfxl_lip_nor1", 40)) == TimelineAddKind.LipSync,
            "Choose opens the picker matching the event");
        check(TimelineWindowViews.PickerFor(Ev("g", "C009", TimelineLane.Motion, 0, "cbem_sp01", 90, access: TimelineAccess.Locked)) == null &&
              TimelineWindowViews.PickerFor(Ev("h", "C042", TimelineLane.Other, 0)) == null,
            "The body motion and other kinds have no picker");
    }

    private static void Labels(Action<bool, string> check, List<string> texts)
    {
        string Label(TimelineEvent ev)
        {
            var label = TimelineWindowViews.EventLabel(ev);
            texts.Add(label);
            return label;
        }
        check(Label(Ev("a", "C063", TimelineLane.Sound, 0, "sound/vfx/se_vfx_etc_emot_herolandding.scd")) == "se_vfx_etc_emot_herolandding",
            "A sound is labelled with its file name");
        check(Label(Ev("b", "C012", TimelineLane.Effect, 0, "vfx/common/eff/cmc_bubble01t.avfx", 30)) == "cmc_bubble01t",
            "An effect is labelled with its file name");
        check(Label(Ev("c", "C010", TimelineLane.Expression, 0, "cfxf_battle_camera1", 30)) == "Battle Camera 1",
            "An expression is labelled with its readable motion name");
        check(Label(Ev("d", "C010", TimelineLane.LipSync, 0, "cfxl_lip_nor1", 40)) == "Lip Nor 1", "Lip sync reads its motion name");
        check(Label(Ev("e", "C009", TimelineLane.Motion, 0, "cbem_sp01", 90, access: TimelineAccess.Locked)) == "cbem_sp01",
            "The body motion keeps its motion name");
        check(Label(Ev("f", "C198", TimelineLane.Prop, 0, "", 60, Values(("Model", 1926), ("Body", 1), ("Variant", 1)))) == "w1926 b0001 v1",
            "A prop model reads like the catalog's");
        check(Label(Ev("g", "C031", TimelineLane.Prop, 5, "", 1, Values(("WeaponTimeline", 274)))) == "Prop animation 274",
            "A prop animation names its weapon timeline");
        check(Label(Ev("h", "C094", TimelineLane.Prop, 0, "", 7, Values(("StartVisibility", 0), ("EndVisibility", 1)))) == "Fade in" &&
              Label(Ev("i", "C094", TimelineLane.Prop, 0, "", 7, Values(("StartVisibility", 1), ("EndVisibility", 0)))) == "Fade out" &&
              Label(Ev("j", "C094", TimelineLane.Prop, 0, "", 5, Values(("StartVisibility", 0), ("EndVisibility", 0)))) == "Hide",
            "Visibility changes read Fade in, Fade out and Hide");
        check(Label(Ev("k", "C042", TimelineLane.Other, 0)) == "Footstep", "An event without a path takes its kind's name");
        check(Label(Ev("l", "C999", TimelineLane.Other, 0)) == "C999", "An unknown kind shows its magic");
        check(TimelineWindowViews.KindName("C063") == "Sound" && TimelineWindowViews.KindName("C999") == "C999", "Kind names come from the schema");
    }

    private static void SaveProblems(Action<bool, string> check, List<string> texts)
    {
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("a", "b", "c")),
            new SkeletonDescription("skeleton", "print", [], [], [], []), 0, "fits");
        var matched = new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate);
        var ambiguous = new SkeletonResolution(SkeletonResolutionState.Ambiguous, [candidate, candidate], null, "Several skeletons fit.");
        string? Problem(bool editable, string? reason, bool inPlace, bool writable, bool lengthChanged, SkeletonResolution? resolution)
        {
            var text = TimelineWindowViews.SaveProblem(editable, reason, inPlace, writable, lengthChanged, resolution);
            if (text != null)
                texts.Add(text);
            return text;
        }
        check(Problem(false, "Unterminated animation timeline string.", false, true, false, matched) == "Unterminated animation timeline string.",
            "A read-only timeline shows why it can't be edited");
        check(Problem(false, null, false, true, false, matched) is { Length: > 0 }, "A read-only timeline without a reason still says so");
        check(Problem(true, null, true, false, false, matched) == TimelineWindowViews.NoWritableSource,
            "In place without a writable source asks for a new mod");
        check(Problem(true, null, true, true, false, null) == null && Problem(true, null, false, false, false, null) == null,
            "Without a length change the skeleton does not matter");
        check(Problem(true, null, false, false, true, matched) == null, "A length change with a matched skeleton saves");
        check(Problem(true, null, false, false, true, ambiguous) == "Several skeletons fit.",
            "A length change waits for the skeleton choice and gives its reason");
        check(Problem(true, null, false, false, true, null) is { Length: > 0 }, "A length change without a skeleton match is blocked");

        var past = Doc(10, 10, Ev("a", "C063", TimelineLane.Sound, 12, "sound/a.scd"), Ev("b", "C063", TimelineLane.Sound, 15, "sound/b.scd"),
            Ev("c", "C063", TimelineLane.Sound, 9, "sound/c.scd"));
        var warning = TimelineWindowViews.PastEndWarning(past);
        texts.Add(warning ?? "");
        check(warning == "2 events start after the end and won't play.", "Events past the end are counted in the warning");
        check(TimelineWindowViews.PastEndWarning(Doc(10, 10, Ev("a", "C063", TimelineLane.Sound, 10, "sound/a.scd"))) ==
              "1 event starts after the end and won't play.", "One event past the end reads in the singular");
        check(TimelineWindowViews.PastEndWarning(Doc(10, 10, Ev("a", "C063", TimelineLane.Sound, 9, "sound/a.scd"))) == null,
            "No warning while every event plays");
    }

    private static void CatalogKeys(Action<bool, string> check)
    {
        const string clip = "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap";
        var root = Path.Combine(Path.GetTempPath(), "Mods", "Smile Pack");
        var key = TimelineWindowViews.CatalogKey(
            ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb", "chara/human/c0101/skeleton/face/f0003/skl_c0101f0003.sklb"], clip, root);
        check(key is { Race: 101, Face: "f0003" } && key.ModRoot == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            "The catalog key comes from the player's face skeleton and the clip's mod");
        var fallback = TimelineWindowViews.CatalogKey(["chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb"],
            "chara/human/c0801/animation/a0001/bt_common/emote/joy.pap", null);
        check(fallback is { Race: 801, Face: TimelineWindowViews.DefaultFace, ModRoot: null },
            "Without a face skeleton the clip's race and face f0001 are used");
        check(TimelineWindowViews.CatalogKey([], "chara/monster/m0001/animation/a0001/bt_common/resident/monster.pap", null) == null,
            "A clip for no player race has no catalog");
        check(TimelineWindowViews.CatalogKey([], clip, "relative/mod") is { ModRoot: null }, "A relative mod folder is ignored");
        check(TimelineWindowViews.RaceOf("chara/human/c1801/animation/a0001/bt_common/emote/joy.pap") == 1801 &&
              TimelineWindowViews.RaceOf("chara/human/cxyz1/animation/joy.pap") == null &&
              TimelineWindowViews.RaceOf("chara/action/emote/joy.tmb") == null,
            "The race comes only from chara/human/cNNNN paths");
    }

    private static void AddedFiles(Action<bool, string> check)
    {
        byte[] sound = [1], effect = [2], texture = [3], unused = [4];
        var imports = new[]
        {
            new TimelineImport("sound/instant_edit/hello_0a1b2c3d.scd",
                ImmutableDictionary<string, byte[]>.Empty.Add("sound/instant_edit/hello_0a1b2c3d.scd", sound), 1.5f),
            new TimelineImport("vfx/instant_edit/spark_11223344.avfx",
                ImmutableDictionary<string, byte[]>.Empty.Add("vfx/instant_edit/spark_11223344.avfx", effect).Add("vfx/spark/texture/spark.atex", texture), 0),
            new TimelineImport("sound/instant_edit/unused_99999999.scd",
                ImmutableDictionary<string, byte[]>.Empty.Add("sound/instant_edit/unused_99999999.scd", unused), 2),
        };
        var doc = Doc(90, 90,
            Ev("a", "C063", TimelineLane.Sound, 0, "Sound/Instant_Edit/Hello_0A1B2C3D.scd"),
            Ev("b", "C012", TimelineLane.Effect, 10, "vfx/instant_edit/spark_11223344.avfx", 30),
            Ev("c", "C063", TimelineLane.Sound, 20, "sound/vfx/other.scd"));
        var added = TimelineWindowViews.AddedFiles(doc, imports);
        check(added.Count == 3 && ReferenceEquals(added["sound/instant_edit/hello_0a1b2c3d.scd"], sound) &&
              ReferenceEquals(added["vfx/instant_edit/spark_11223344.avfx"], effect) && ReferenceEquals(added["vfx/spark/texture/spark.atex"], texture),
            "A save packages every file of the imports the timeline uses, an effect's textures included");
        check(!added.ContainsKey("sound/instant_edit/unused_99999999.scd"), "Imports no event uses any more are left out");
        check(TimelineWindowViews.AddedFiles(Doc(90, 90), imports).IsEmpty, "An empty timeline packages nothing");
        var other = new TimelineImport("vfx/instant_edit/glow_55667788.avfx",
            ImmutableDictionary<string, byte[]>.Empty.Add("vfx/instant_edit/glow_55667788.avfx", [5]).Add("vfx/spark/texture/spark.atex", [6]), 0);
        var both = Doc(90, 90, Ev("b", "C012", TimelineLane.Effect, 10, "vfx/instant_edit/spark_11223344.avfx", 30),
            Ev("d", "C012", TimelineLane.Effect, 20, "vfx/instant_edit/glow_55667788.avfx", 30));
        var refused = false;
        try { TimelineWindowViews.AddedFiles(both, [.. imports, other]); }
        catch (InvalidDataException) { refused = true; }
        check(refused, "Two imports needing different files at one path are refused, not overwritten");
    }

    private static void GamePaths(Action<bool, string> check, List<string> texts)
    {
        string? Path(string text, TimelineAddKind kind, out string? error)
        {
            var path = TimelineWindowViews.GamePath(text, kind, out error);
            if (error != null)
                texts.Add(error);
            return path;
        }
        check(Path("  Sound\\Foo\\Bar.SCD ", TimelineAddKind.Sound, out var error) == "sound/foo/bar.scd" && error == null,
            "A typed sound path is trimmed, lowercased and uses forward slashes");
        check(Path("/vfx/common/eff/a.avfx", TimelineAddKind.Effect, out error) == "vfx/common/eff/a.avfx" && error == null,
            "A typed effect path loses its leading slash");
        check(Path("sound/foo/bar.avfx", TimelineAddKind.Sound, out error) == null && error!.Contains(".scd", StringComparison.Ordinal),
            "A sound path must end in .scd");
        check(Path("vfx/foo/bar.scd", TimelineAddKind.Effect, out error) == null && error!.Contains(".avfx", StringComparison.Ordinal),
            "An effect path must end in .avfx");
        check(Path("sound/foo bar/x.scd", TimelineAddKind.Sound, out error) == null && error != null &&
              Path("sound/../x.scd", TimelineAddKind.Sound, out error) == null && error != null &&
              Path("C:/sound/x.scd", TimelineAddKind.Sound, out error) == null && error != null,
            "Paths the game can't load are refused");
        check(Path("   ", TimelineAddKind.Sound, out error) == null && error != null, "An empty path asks for one");
        check(Path("cfxf_smile", TimelineAddKind.Expression, out error) == null && error != null, "Only sounds and effects take a game path");
    }

    private static void Packs(Action<bool, string> check, List<string> texts)
    {
        check(TimelineWindowViews.PackAfter(null, "smile", out var error) == "smile" && error == null,
            "The first library expression brings its library");
        check(TimelineWindowViews.PackAfter("smile", "SMILE", out error) == "smile" && error == null, "The same library is kept");
        check(TimelineWindowViews.PackAfter("smile", null, out error) == "smile" && error == null, "A resident expression keeps the library");
        check(TimelineWindowViews.PackAfter(null, null, out error) == null && error == null, "No library stays none");
        var kept = TimelineWindowViews.PackAfter("smile", "emot/laugh", out error);
        texts.Add(error ?? "");
        check(kept == "smile" && error != null && error.Contains("smile", StringComparison.Ordinal),
            "A second library is refused with a short reason");
    }

    private static void Texts(Action<bool, string> check, List<string> texts)
    {
        check(TimelineWindowViews.Frames(1.5f) == 45 && TimelineWindowViews.Frames(1 / 60f) == 1 && TimelineWindowViews.Frames(float.NaN) == 0,
            "Seconds become whole frames, rounded half away from zero");
        check(TimelineWindowViews.SoundFrames(1.01f) == 31 && TimelineWindowViews.SoundFrames(null) == 0 &&
              TimelineWindowViews.SoundFrames(-1) == 0 && TimelineWindowViews.SoundFrames(float.PositiveInfinity) == 0 &&
              TimelineWindowViews.SoundFrames(10000) == TimelineDocument.MaxFrames,
            "A sound takes its length in frames rounded up, nothing when unknown, at most the TMB range");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var frame = TimelineWindowViews.FrameText(36);
            var length = TimelineWindowViews.LengthText(Doc(75, 60));
            var duration = TimelineWindowViews.DurationText(30);
            texts.AddRange([frame, length, duration, TimelineWindowViews.DurationText(1), TimelineWindowViews.DurationText(9999999)]);
            check(frame == "1.2s · frame 36", "The playhead reads seconds and the frame, whatever the culture");
            check(length == "2.5s · 75 frames · was 2s", "A changed length shows the old one");
            check(TimelineWindowViews.LengthText(Doc(60, 60)) == "2s · 60 frames", "An unchanged length shows no old one");
            check(duration == "1s · 30 frames" && TimelineWindowViews.DurationText(1) == "0.03s · 1 frame", "Durations read seconds and frames");
            check(TimelineWindowViews.DurationText(9999999) == "until the end", "Mod durations past the TMB range read until the end");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        var visibility = TmbEntrySchema.Get("C012").Fields.Single(field => field.Name == "Visibility");
        var position = TmbEntrySchema.Get("C063").Fields.Single(field => field.Name == "PositionFlags");
        var time = TmbEntrySchema.Get("C010").Fields.Single(field => field.Name == "TimeControl");
        var scale = TmbEntrySchema.Get("C012").Fields.Single(field => field.Name == "Scale.X");
        var index = TmbEntrySchema.Get("C063").Fields.Single(field => field.Name == "SoundIndex");
        var values = new[]
        {
            TimelineWindowViews.FieldText(visibility, 3), TimelineWindowViews.FieldText(visibility, 9),
            TimelineWindowViews.FieldText(position, 5), TimelineWindowViews.FieldText(position, 0),
            TimelineWindowViews.FieldText(time, 1), TimelineWindowViews.FieldText(time, 0),
            TimelineWindowViews.FieldText(scale, .5), TimelineWindowViews.FieldText(index, 2),
        };
        texts.AddRange(values);
        check(values.SequenceEqual(["Always with triggers", "9", "3D range, Use bind point", "None", "Yes", "No", "0.5", "2"]),
            "Read-only field values use their option names");

        check(TimelineWindowViews.SoundFilter.Contains(".wav", StringComparison.Ordinal) &&
              TimelineWindowViews.SoundFilter.Contains(".ogg", StringComparison.Ordinal) &&
              TimelineWindowViews.SoundFilter.Contains(".scd", StringComparison.Ordinal) &&
              TimelineWindowViews.EffectFilter.Contains(".avfx", StringComparison.Ordinal),
            "The import dialogs offer .wav, .ogg, .scd and .avfx");
        var name = TimelineWindowViews.DefaultModName("Joy - Instant Edit");
        texts.Add(name);
        check(name == "Joy - Instant Edit Timeline", "A timeline save suggests the clip's mod name with Timeline");
        texts.Add(TimelineWindowViews.NoWritableSource);
    }
}
