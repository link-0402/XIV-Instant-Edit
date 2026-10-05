using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The timeline editor's catalog (what can be placed) and file imports: expressions from the
/// face's resident set and libraries, sounds, effects and prop recipes harvested from vanilla emote
/// timelines, the source mod's mappings, and packaging of sounds and effects picked on disk.
/// </summary>
internal static class TimelineCatalogFixture
{
    private sealed record Entry(string Magic, int Size, Action<byte[], int>? Fill = null, int Field = 0, string? Text = null);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Labels(check, reject);
        TimelineReading(check);
        var temp = Path.Combine(Path.GetTempPath(), "ie-timeline-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Catalog(check, temp);
            Imports(check, reject, temp);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Labels(Action<bool, string> check, Action<Action, string> reject)
    {
        check(TimelineCatalogBuilder.Readable("cfxf_battle_camera1") == "Battle Camera 1" &&
              TimelineCatalogBuilder.Readable("cfxb_blink1") == "Blink 1" &&
              TimelineCatalogBuilder.Readable("cfxl_lip_nor2") == "Lip Nor 2" &&
              TimelineCatalogBuilder.Readable("cfxf_angry") == "Angry",
            "motion names read as words without their kind prefix, numbers split off");
        var root = Path.Combine(Path.GetTempPath(), "Some Mod");
        check(new TimelineCatalogKey(801, "F0002", root) == new TimelineCatalogKey(801, "f0002", root.ToUpperInvariant() + Path.DirectorySeparatorChar) &&
              new TimelineCatalogKey(801, "f0002", root).GetHashCode() == new TimelineCatalogKey(801, "f0002", root.ToUpperInvariant()).GetHashCode() &&
              new TimelineCatalogKey(801, "f0002") != new TimelineCatalogKey(801, "f0002", root) &&
              new TimelineCatalogKey(801, "f0002") != new TimelineCatalogKey(101, "f0002"),
            "one catalog per race, face folder and mod folder, however the folder is spelled");
        check(TimelineCatalogKey.FromFaceSkeleton("chara/human/c0101/skeleton/face/f0002/skl_c0101f0002.sklb") is { Race: 101, Face: "f0002", ModRoot: null } &&
              TimelineCatalogKey.FromFaceSkeleton("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb") == null &&
              TimelineCatalogKey.FromFaceSkeleton("chara/human/c0101/skeleton/face/x/skl.sklb") == null,
            "the catalog key comes from the player's face skeleton path");
        reject(() => _ = new TimelineCatalogKey(801, "face2"), "a face folder must be f and four digits");
        reject(() => _ = new TimelineCatalogKey(0, "f0002"), "a race code must be set");
    }

    private static void TimelineReading(Action<bool, string> check)
    {
        var tmb = Tmb(new Entry("TMDH", 16), new Entry("TMPP", 12, Field: 8, Text: "emot/laugh"),
            new Entry("C063", 32, Field: 20, Text: "sound/test/a.scd"), new Entry("C010", 40, Field: 32, Text: "cfxf_laugh"));
        var entries = AnimationDependencies.TimelineEntries("chara/action/test.tmb", tmb);
        check(entries.Select(e => e.Magic).SequenceEqual(["TMDH", "TMPP", "C063", "C010"]) &&
              AnimationDependencies.EntryString(tmb, entries[2], 20) == "sound/test/a.scd" &&
              AnimationDependencies.EntryString(tmb, entries[1], 8) == "emot/laugh" &&
              AnimationDependencies.EntryString(tmb, entries[0], 12) == null,
            "timeline entries are listed in order and their strings read through entry-relative pointers");
        var references = AnimationDependencies.Read("chara/action/test.tmb", tmb);
        check(references.Problems.IsEmpty && references.References.Contains(new AnimationReference("emot/laugh", "facepack")) &&
              references.References.Contains(new AnimationReference("cfxf_laugh", "animation")),
            "the TMPP face library a timeline loads is reported as a face pack");
        var broken = tmb.ToArray();
        Int(broken, 12 + 16 + 8, 0x7FFF0000);
        var brokenReferences = AnimationDependencies.Read("chara/action/test.tmb", broken);
        check(!brokenReferences.Problems.IsEmpty && brokenReferences.References.Contains(new AnimationReference("cfxf_laugh", "animation")),
            "a broken face library name is a problem, and the timeline's motions are still read");
        var pap = Pap(("a", 7, tmb), ("b", 7, Tmb(new Entry("C063", 32, Field: 20, Text: "sound/test/b.scd"))));
        var papEntries = AnimationDependencies.TimelineEntries("chara/test.pap", pap);
        check(papEntries.Count(e => e.Timeline == 0) == 4 && papEntries.Single(e => e.Timeline == 1).Magic == "C063" &&
              AnimationDependencies.EntryString(pap, papEntries.Single(e => e.Timeline == 1), 20) == "sound/test/b.scd",
            "every embedded PAP timeline is walked and its entries say which timeline holds them");

        // The game stores three-letter AVFX names as their reversed letters then a NUL ("xeT\0").
        var vfx = Avfx(["vfx/test/a.atex", "", "vfx/test/a.atex", "vfx/test/b.atex"], ["sound/test/c.scd"]);
        var paths = AnimationDependencies.ReadVfxPaths(vfx);
        check(paths.Problems.IsEmpty && paths.Textures.SequenceEqual(["vfx/test/a.atex", "vfx/test/b.atex"]) &&
              paths.Sounds.SequenceEqual(["sound/test/c.scd"]) &&
              AnimationDependencies.Read("vfx/test.avfx", vfx).References.Any(r => r.Path == "vfx/test/b.atex"),
            "AVFX textures in the game's own chunk layout are found, with emitter sounds kept apart");
    }

    private static void Catalog(Action<bool, string> check, string temp)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var empty = Tmb();
        files["chara/human/c0801/animation/f0002/resident/face.pap"] = Pap(("cfxf_angry", 17, empty), ("cfxb_blink1", 18, empty),
            ("cfxf_salute", 17, empty), ("cfxf_bow", 17, empty), ("cfxl_lip_nor1", 19, empty));
        files["chara/action/facial/pose/smile.tmb"] = Tmb(new Entry("TMDH", 16), new Entry("TMPP", 12, Field: 8, Text: "smile"),
            new Entry("C010", 40, Field: 32, Text: "cfxf_smile"));
        files["chara/action/facial/pose/bow.tmb"] = Tmb(new Entry("TMDH", 16), new Entry("C010", 40, Field: 32, Text: "cfxf_bow"));
        files["chara/action/facial/pose/missing.tmb"] = Tmb(new Entry("TMPP", 12, Field: 8, Text: "missing"), new Entry("C010", 40, Field: 32, Text: "cfxf_missing"));
        files["chara/human/c0801/animation/f0002/nonresident/smile.pap"] = Pap(("cfxf_smile", 17, empty));
        files["chara/human/c0801/animation/f0002/nonresident/emot/laugh.pap"] = Pap(("cfxf_laugh", 17, empty), ("cfxf_smile", 17, empty),
            ("cfxf_angry", 17, empty), ("cfxl_lip_laugh", 19, empty));
        files["chara/human/c0801/animation/a0001/bt_common/emote/laugh.pap"] = Pap(("cbem_laugh", 7, Tmb(
            new Entry("TMPP", 12, Field: 8, Text: "emot/laugh"), new Entry("C063", 32, Field: 20, Text: "sound/test/se_laugh.scd"),
            new Entry("C012", 72, Field: 20, Text: "vfx/test/laugh.avfx"))));
        files["chara/action/emote/laugh.tmb"] = Tmb(new Entry("C063", 32, Field: 20, Text: "sound/test/se_laugh.scd"),
            new Entry("C063", 32, Field: 20, Text: "sound/test/se_other.scd"));
        files["vfx/test/laugh.avfx"] = Avfx(["vfx/test/t.atex"], ["sound/test/se_vfx_laugh.scd", "sound/test/se_absent.scd"]);
        files["sound/test/se_vfx_laugh.scd"] = "SEDBSSCF"u8.ToArray();
        // Female Miqo'te has no Toast of her own here, so she plays her skeleton parent's (c0201).
        files["chara/human/c0201/animation/a0001/bt_common/emote_sp/sp19.pap"] = Pap(("cbem_sp19", 7, Tmb(
            new Entry("C031", 24, (b, at) => { Short(b, at + 20, 388); Short(b, at + 22, 2); }),
            new Entry("C043", 32, (b, at) => { Short(b, at + 24, 1962); Short(b, at + 26, 1); Int(b, at + 28, 1); }),
            new Entry("C198", 40, (b, at) => { Short(b, at + 32, 1962); Short(b, at + 34, 1); Int(b, at + 36, 1); }),
            new Entry("C031", 24, (b, at) => { Short(b, at + 20, 1); Short(b, at + 22, 0); }))));
        files["chara/human/c0801/animation/a0001/bt_common/emote_sp/sp75.pap"] = Pap(("cbem_sp75", 7, Tmb(
            new Entry("C031", 24, (b, at) => { Short(b, at + 20, 402); Short(b, at + 22, 2); }),
            new Entry("C198", 40, (b, at) => { b[at + 28] = 1; Short(b, at + 32, 1970); Short(b, at + 34, 1); Int(b, at + 36, 1); }),
            new Entry("C198", 40, (b, at) => { Short(b, at + 32, 1969); Short(b, at + 34, 1); Int(b, at + 36, 1); }))));
        files["chara/human/c0801/animation/a0001/bt_common/emote_sp/sp23.pap"] = Pap(("cbem_sp23", 7, Tmb(
            new Entry("C198", 40, (b, at) => { Short(b, at + 32, 1961); Short(b, at + 34, 1); Int(b, at + 36, 1); }))));
        files["chara/action/emote/battle02.tmb"] = Tmb(new Entry("C031", 24, (b, at) => { Short(b, at + 20, 190); Short(b, at + 22, 0); }));
        files["chara/action/emote/broken.tmb"] = [1, 2, 3];
        files["vfx/common/eff/dk01.avfx"] = Avfx([], []);
        files[TimelineCatalogBuilder.WeaponModelPath(new(201, 1, 1, null))] = [];
        files[TimelineCatalogBuilder.WeaponModelPath(new(1962, 1, 1, null))] = [];

        var sheets = new TimelineCatalogSheets(
            [new("Smile", [new("facial/pose/smile", 0)]), new("Shut Eyes", [new("facial/pose/bow", 0)]),
             new("Laugh", [new("emote/laugh", 2)]), new("Toast", [new("emote_sp/sp19", 2)]), new("Navigate", [new("emote_sp/sp75", 2)]),
             new("Read", [new("emote_sp/sp23", 2)]), new("Battle Stance", [new("emote/battle02", 1)]), new("Broken", [new("emote/broken", 1)])],
            ["facial/pose/smile", "facial/pose/bow", "facial/pose/missing", "facial/pose/../x"],
            ["dk01", "gone", "bad path"],
            [new("Bronze Sword", 201, 1, 1), new("Bronze Sword Replica", 201, 1, 1), new("Missing Model", 999, 1, 1), new("Toast Mug", 1962, 1, 1)],
            [new(402, "weapon/emote_sp/sp75"), new(388, "weapon/emote_sp/sp19_c0101"), new(5, "")]);

        var mod = Path.Combine(temp, "Mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject
            {
                ["sound/mod/a.scd"] = "a.scd", ["vfx/mod/b.avfx"] = "vfx\\b.avfx", ["chara/x.mdl"] = "x.mdl",
                ["sound/test/SE_LAUGH.scd"] = "laugh.scd", ["sound/bad path.scd"] = "x.scd", ["sound/mod/out.scd"] = "..\\outside.scd",
            } },
            ["Groups"] = new JsonArray(new JsonObject
            {
                ["Options"] = new JsonArray(new JsonObject { ["Files"] = new JsonObject { ["sound/mod/opt.scd"] = "opt.scd" } }),
                ["Containers"] = new JsonArray(new JsonObject { ["Files"] = new JsonObject { ["vfx/mod/c.avfx"] = "c.avfx" } }),
            }),
        }.ToJsonString());
        File.WriteAllText(Path.Combine(mod, "default_mod.json"), """{ "Files": { "sound/mod/old.scd": "old.scd" } }""");
        File.WriteAllText(Path.Combine(mod, "group_001_pick.json"), """{ "Options": [ { "Files": { "sound/mod/group.scd": "sub\\g.scd" } } ] }""");
        File.WriteAllText(Path.Combine(mod, "group_002_broken.json"), "{");

        var builder = new TimelineCatalogBuilder(files.ContainsKey, files.GetValueOrDefault);
        var data = builder.Build(sheets, new TimelineCatalogKey(801, "f0002", mod));
        TimelineCatalogItem? Expression(string motion, string? pack) => data.Expressions.SingleOrDefault(e => e.Path == motion && e.Pack == pack);
        check(Expression("cfxf_angry", null) is { Label: "Angry", Origin: TimelineCatalogOrigin.Resident } &&
              Expression("cfxf_bow", null) is { Label: "Shut Eyes" } && Expression("cfxb_blink1", null) is { Label: "Blink 1" } &&
              Expression("cfxf_salute", null) != null && data.LipShapes.Any(l => l.Path == "cfxl_lip_nor1" && l.Pack == null),
            "resident faces and blinks are expressions without a library, labelled by their expression emote when one exists");
        check(Expression("cfxf_smile", "smile") is { Label: "Smile", Origin: TimelineCatalogOrigin.Expression } &&
              Expression("cfxf_laugh", "emot/laugh") is { Origin: TimelineCatalogOrigin.Emote, Detail: "Laugh" } &&
              Expression("cfxf_smile", "emot/laugh") is { Label: "Smile" } &&
              !data.Expressions.Any(e => e.Path == "cfxf_angry" && e.Pack != null) &&
              !data.Expressions.Any(e => e.Path == "cfxf_missing") &&
              data.LipShapes.Any(l => l.Path == "cfxl_lip_laugh" && l.Pack == "emot/laugh"),
            "library expressions name their TMPP library, only libraries the face has are offered, and resident motions are not repeated");
        check(Expression("cfxf_laugh", "emot/laugh")!.Request(TimelineAddKind.Expression, 12, 30) ==
              new TimelineAddRequest(TimelineAddKind.Expression, 12, "cfxf_laugh", 30, "emot/laugh"),
            "a catalog item becomes the add request that places it");

        var sound = data.Sounds.Where(s => s.Path.Equals("sound/test/se_laugh.scd", StringComparison.OrdinalIgnoreCase)).ToArray();
        check(data.Sounds[0].Origin == TimelineCatalogOrigin.Mod && sound is [{ Origin: TimelineCatalogOrigin.Mod, Path: "sound/test/SE_LAUGH.scd" }] &&
              data.Sounds.Any(s => s is { Path: "sound/test/se_other.scd", Label: "Laugh: se_other", Origin: TimelineCatalogOrigin.Emote }) &&
              data.Sounds.Any(s => s is { Path: "sound/test/se_vfx_laugh.scd", Detail: "Laugh" }) &&
              !data.Sounds.Any(s => s.Path == "sound/test/se_absent.scd"),
            "sounds come from the mod first, then emote timelines and their effects, each path once");
        check(data.Effects.Any(e => e is { Path: "vfx/test/laugh.avfx", Origin: TimelineCatalogOrigin.Emote }) &&
              data.Effects.Any(e => e is { Path: "vfx/common/eff/dk01.avfx", Label: "dk01", Origin: TimelineCatalogOrigin.Common }) &&
              !data.Effects.Any(e => e.Path.Contains("gone") || e.Path.Contains(' ')) &&
              data.Effects.Count(e => e.Origin == TimelineCatalogOrigin.Mod) == 2,
            "effects come from the mod, emote timelines and the common effect list when the game has them");
        var recipes = data.Props.Where(p => p.Origin == TimelineCatalogOrigin.Emote).Select(p => (p.Label, p.Prop)).ToArray();
        check(recipes.SequenceEqual([("Toast", new TimelinePropModel(1962, 1, 1, 388)), ("Navigate", new TimelinePropModel(1970, 1, 1, null)),
                  ("Navigate", new TimelinePropModel(1969, 1, 1, 402)), ("Read", new TimelinePropModel(1961, 1, 1, null))]) &&
              data.Props.Single(p => p.Prop!.Model == 1969).Detail.Contains("sp75"),
            "prop recipes pair each spawned model with the prop animation of its first summon, from the race or its skeleton parent");
        check(data.Props.Where(p => p.Origin == TimelineCatalogOrigin.Item).Select(p => (p.Label, p.Detail, p.Prop)).SequenceEqual(
                  [("Bronze Sword", "Also Bronze Sword Replica", new TimelinePropModel(201, 1, 1, null))]),
            "items list each existing weapon model once, after the recipes and without models a recipe offers");
        check(data.WeaponTimelines.SequenceEqual([new TimelineWeaponTimeline(388, "weapon/emote_sp/sp19_c0101"), new TimelineWeaponTimeline(402, "weapon/emote_sp/sp75")]),
            "weapon timelines with a key are offered in row order");
        check(data.ModFiles.Count == 7 && data.ModFiles["SOUND/MOD/A.SCD"] == Path.Combine(mod, "a.scd") &&
              data.ModFiles["sound/mod/group.scd"] == Path.Combine(mod, "sub", "g.scd") &&
              data.ModFiles.ContainsKey("sound/mod/opt.scd") && data.ModFiles.ContainsKey("vfx/mod/c.avfx") && data.ModFiles.ContainsKey("sound/mod/old.scd") &&
              !data.ModFiles.ContainsKey("chara/x.mdl") && !data.ModFiles.ContainsKey("sound/mod/out.scd"),
            "the mod's sound and effect mappings are read from every option, container and older group file, never outside its folder");
        check(data.Notes.Contains("Some of the mod's settings could not be read.") && data.Notes.Contains("1 game file could not be read."),
            "unreadable mod settings and game files are noted, not fatal");

        var faceless = builder.Build(sheets, new TimelineCatalogKey(801, "f0003"));
        check(faceless.Expressions.IsEmpty && faceless.LipShapes.IsEmpty && faceless.Notes.Contains("No face animations for f0003, so no expressions.") &&
              faceless.ModFiles.Count == 0 && faceless.Sounds.All(s => s.Origin == TimelineCatalogOrigin.Emote),
            "a face folder without animations offers no expressions and says so");

        // With the game's load table, its pick wins over the skeleton chart: c0801 toasts with c0101's copy.
        files[PapLoadTable.GamePath] = PapLoadTableFixture.Table(["bt_common"], [(0, "emote_sp/sp19")],
            [new(801, 1, 0, [0])], [new(101, 1, 0), new(801, 1, 0), new(801, 1, 0), new(801, 1, 0)]);
        files["chara/human/c0101/animation/a0001/bt_common/emote_sp/sp19.pap"] = Pap(("cbem_sp19", 7, Tmb(
            new Entry("C031", 24, (b, at) => { Short(b, at + 20, 388); Short(b, at + 22, 2); }),
            new Entry("C043", 32, (b, at) => { Short(b, at + 24, 1963); Short(b, at + 26, 1); Int(b, at + 28, 1); }))));
        var tabled = builder.Build(sheets, new TimelineCatalogKey(801, "f0002"));
        check(tabled.Props.Any(p => p is { Label: "Toast", Origin: TimelineCatalogOrigin.Emote } && p.Prop == new TimelinePropModel(1963, 1, 1, 388)) &&
              !tabled.Props.Any(p => p.Origin == TimelineCatalogOrigin.Emote && p.Prop?.Model == 1962) &&
              tabled.Props.Any(p => p is { Label: "Navigate", Origin: TimelineCatalogOrigin.Emote }),
            "emote recipes come from the copy the game's load table picks, and emotes it doesn't list from the race's own");
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            builder.Build(sheets, new TimelineCatalogKey(801, "f0002"), cancelled.Token);
            check(false, "a cancelled build stops");
        }
        catch (OperationCanceledException) { check(true, "a cancelled build stops"); }
    }

    private static void Imports(Action<bool, string> check, Action<Action, string> reject, string temp)
    {
        check(TimelineImports.Slug("My Cool Sound!.wav") == "my_cool_sound" && TimelineImports.Slug("__x__.ogg") == "x" &&
              TimelineImports.Slug("ÄÖ.scd") == "file" && TimelineImports.Slug(new string('a', 60) + ".scd") == new string('a', 40) &&
              TimelineImports.Slug("a b - c.scd") == "a_b_c",
            "import slugs keep lowercase letters, digits and single underscores, at most 40");
        var scd = "SEDBSSCF"u8.ToArray().Concat(new byte[24]).ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(scd))[..8];
        check(TimelineImports.Hash8(scd) == hash && TimelineImports.SoundPath("Bell Ring.scd", scd) == $"sound/instant_edit/bell_ring_{hash}.scd" &&
              TimelineImports.EffectPath("Glow.avfx", scd) == $"vfx/instant_edit/glow_{hash}.avfx" &&
              AnimationDependencies.SafeGamePath(TimelineImports.SoundPath("ÄÖ !.scd", scd)),
            "imported files get new game paths from their name and content hash");

        var scdFile = Path.Combine(temp, "Bell Ring.scd");
        File.WriteAllBytes(scdFile, scd);
        var bell = TimelineImports.Sound(scdFile, 0.5f, null, bytes => bytes.Length == scd.Length ? 2.5f : null);
        check(bell.GamePath == $"sound/instant_edit/bell_ring_{hash}.scd" && bell.Files.Count == 1 && bell.Files[bell.GamePath].SequenceEqual(scd) &&
              bell.Seconds == 2.5f && TimelineImports.Sound(scdFile, 1, null).Seconds == 0,
            "an SCD is packaged as it is, with its length when a reader is given");
        var badScd = Path.Combine(temp, "bad.scd");
        File.WriteAllBytes(badScd, [1, 2, 3]);
        reject(() => TimelineImports.Sound(badScd, 1, null), "a file without the SCD header is refused");
        var wav = Path.Combine(temp, "Voice Line.wav");
        File.WriteAllBytes(wav, [0x52, 0x49, 0x46, 0x46]);
        reject(() => TimelineImports.Sound(wav, 1, null), "wav needs the audio converter");
        (string Path, float Volume)? converted = null;
        var encoded = "SEDBSSCF"u8.ToArray().Concat(new byte[] { 9, 9 }).ToArray();
        var voice = TimelineImports.Sound(wav, 0.8f, (path, volume) => { converted = (path, volume); return (encoded, 1.25f); });
        check(converted == (wav, 0.8f) && voice.GamePath == $"sound/instant_edit/voice_line_{TimelineImports.Hash8(encoded)}.scd" &&
              voice.Files[voice.GamePath].SequenceEqual(encoded) && voice.Seconds == 1.25f,
            "wav and ogg go through the converter at the chosen volume, named after the encoded sound");
        reject(() => TimelineImports.Sound(wav, 1, (_, _) => ([1, 2], 1)), "a converter result that is not an SCD is refused");
        reject(() => TimelineImports.Sound(Path.Combine(temp, "x.mp3"), 1, null), "other sound formats are refused");

        // An effect picked from a mod's eff folder: one texture at its game path above, one with a
        // same-named file in a folder below (a decoy elsewhere loses), a sound beside it and one the game has.
        var effects = Path.Combine(temp, "Effect");
        var eff = Path.Combine(effects, "vfx", "mine", "eff");
        Directory.CreateDirectory(Path.Combine(eff, "Textures"));
        Directory.CreateDirectory(Path.Combine(effects, "vfx", "mine", "texture"));
        Directory.CreateDirectory(Path.Combine(eff, "other", "deep"));
        var avfx = Avfx(["vfx/mine/texture/a.atex", "vfx/common/texture/known.atex", "vfx/test/tex/b.atex"], ["sound/mine/c.scd"]);
        var effectFile = Path.Combine(eff, "Glow Burst.avfx");
        File.WriteAllBytes(effectFile, avfx);
        File.WriteAllBytes(Path.Combine(effects, "vfx", "mine", "texture", "a.atex"), [1]);
        File.WriteAllBytes(Path.Combine(eff, "a.atex"), [9]);
        File.WriteAllBytes(Path.Combine(eff, "Textures", "B.ATEX"), [2]);
        File.WriteAllBytes(Path.Combine(eff, "other", "deep", "b.atex"), [8]);
        Directory.CreateDirectory(Path.Combine(eff, "tex"));
        File.WriteAllBytes(Path.Combine(eff, "tex", "b.atex"), [3]);
        File.WriteAllBytes(Path.Combine(eff, "c.scd"), scd);
        var effect = TimelineImports.Effect(effectFile, path => path == "vfx/common/texture/known.atex");
        check(effect.GamePath == $"vfx/instant_edit/glow_burst_{TimelineImports.Hash8(avfx)}.avfx" && effect.Seconds == 0 &&
              effect.Files.Count == 4 && effect.Files[effect.GamePath].SequenceEqual(avfx) &&
              effect.Files["vfx/mine/texture/a.atex"].SequenceEqual(new byte[] { 1 }) &&
              effect.Files["vfx/test/tex/b.atex"].SequenceEqual(new byte[] { 3 }) &&
              effect.Files["sound/mine/c.scd"].SequenceEqual(scd) && !effect.Files.ContainsKey("vfx/common/texture/known.atex"),
            "an effect packages the files it names that the game lacks, found at their game path or by name below it");
        File.Delete(Path.Combine(eff, "c.scd"));
        File.Delete(Path.Combine(effects, "vfx", "mine", "texture", "a.atex"));
        File.Delete(Path.Combine(eff, "a.atex"));
        string? message = null;
        try { TimelineImports.Effect(effectFile, _ => false); }
        catch (InvalidDataException e) { message = e.Message; }
        check(message == "Missing next to the effect: a.atex, known.atex, c.scd.",
            "an effect missing files is refused, naming them");
        var many = Path.Combine(eff, "many.avfx");
        File.WriteAllBytes(many, Avfx(["x/1.atex", "x/2.atex", "x/3.atex", "x/4.atex", "x/5.atex"], []));
        message = null;
        try { TimelineImports.Effect(many, _ => false); }
        catch (InvalidDataException e) { message = e.Message; }
        check(message == "Missing next to the effect: 1.atex, 2.atex, 3.atex and 2 more.", "a refusal names at most three missing files");
        var spaced = Path.Combine(eff, "spaced.avfx");
        File.WriteAllBytes(spaced, Avfx(["vfx/te st/x.atex"], []));
        reject(() => TimelineImports.Effect(spaced, _ => true), "an effect naming a path the game cannot load is refused");
        var garbage = Path.Combine(eff, "garbage.avfx");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        reject(() => TimelineImports.Effect(garbage, _ => true), "an unreadable effect is refused");
        reject(() => TimelineImports.Effect(scdFile, _ => true), "only .avfx files are effects");
    }

    private static void Int(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Short(byte[] bytes, int at, short value) => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at), value);

    /// <summary>A TMLB in the game's layout: items, then the strings they point at.</summary>
    private static byte[] Tmb(params Entry[] entries)
    {
        var items = new byte[12 + entries.Sum(e => e.Size)];
        "TMLB"u8.CopyTo(items); Int(items, 8, entries.Length);
        var strings = new List<byte>(); var pointers = new List<(int Field, int Text)>();
        var cursor = 12;
        foreach (var entry in entries)
        {
            Encoding.ASCII.GetBytes(entry.Magic).CopyTo(items, cursor); Int(items, cursor + 4, entry.Size);
            entry.Fill?.Invoke(items, cursor);
            if (entry.Text != null)
            {
                // Pointers count from the entry's start + 8.
                pointers.Add((cursor + entry.Field, items.Length + strings.Count - (cursor + 8)));
                strings.AddRange(Encoding.UTF8.GetBytes(entry.Text)); strings.Add(0);
            }
            cursor += entry.Size;
        }
        foreach (var (field, text) in pointers) Int(items, field, text);
        var bytes = items.Concat(strings).ToArray();
        Int(bytes, 4, bytes.Length);
        return bytes;
    }

    /// <summary>A PAP holding one clip per entry, each with its own timeline, aligned as the game aligns them.</summary>
    private static byte[] Pap(params (string Name, short Type, byte[] Timeline)[] clips)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)clips.Length);
        writer.Write((short)0x0801); writer.Write((byte)0); writer.Write((byte)0);
        var offsets = stream.Position;
        writer.Write(0); writer.Write(0); writer.Write(0);
        var info = (int)stream.Position;
        for (var i = 0; i < clips.Length; i++)
        {
            var name = Encoding.UTF8.GetBytes(clips[i].Name);
            writer.Write(name); writer.Write(new byte[32 - name.Length]);
            writer.Write(clips[i].Type); writer.Write((short)i); writer.Write(clips[i].Type >= 17 ? 1 : 0);
        }
        var havok = (int)stream.Position;
        writer.Write(new byte[8]);
        var timeline = (int)stream.Position;
        for (var i = 0; i < clips.Length; i++)
        {
            writer.Write(clips[i].Timeline);
            if (i + 1 < clips.Length) writer.Write(new byte[(timeline - (int)stream.Position) & 3]);
        }
        stream.Position = offsets;
        writer.Write(info); writer.Write(havok); writer.Write(timeline);
        return stream.ToArray();
    }

    /// <summary>An AVFX with texture chunks and one emitter naming the sounds, names stored as the game stores them.</summary>
    private static byte[] Avfx(string[] textures, string[] sounds)
    {
        static byte[] Chunk(string name, byte[] payload)
        {
            var result = new byte[8 + payload.Length + ((-payload.Length) & 3)];
            Encoding.ASCII.GetBytes(new string(name.Reverse().ToArray())).CopyTo(result, 0);
            Int(result, 4, payload.Length); payload.CopyTo(result, 8);
            return result;
        }
        static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + "\0");
        var body = new List<byte>();
        body.AddRange(Chunk("Ver", [1, 0, 0, 0]));
        foreach (var texture in textures) body.AddRange(Chunk("Tex", Text(texture)));
        if (sounds.Length > 0) body.AddRange(Chunk("Emit", sounds.SelectMany(s => Chunk("SdNm", Text(s))).ToArray()));
        return Chunk("AVFX", [.. body]);
    }
}
