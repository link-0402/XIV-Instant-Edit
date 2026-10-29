using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;

/// <summary>
/// The in-game animation preview writes every revision to a new cache folder, because the game and
/// Penumbra never re-read a file path they already loaded, and keeps a record of its Penumbra
/// temporary mod so a crash can be cleaned up. These fixtures cover the pure parts: game path
/// safety, the revision layout, which leftovers may be deleted, the record round trip and the
/// cache folder staying recognizable after a temp cleaner removed the cache marker.
/// </summary>
internal static class AnimationPreviewFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        GamePaths(check, reject);
        Layout(check, reject);
        Leftovers(check);
        Times(check);
        Records(check);
        OwnedCacheEntry(check);
        MetaManipulations(check);
    }

    private static byte[] Gunzip(string encoded)
    {
        using var zipped = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new GZipStream(zipped, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }

    /// <summary>
    /// A placed prop's attach override rides in the preview's temporary mod as Penumbra's binary meta string: version 1,
    /// META0001, a count per kind (IMC, EQP, EQDP, EST, RSP, GMP, global EQP, Atch, Shp, Atr), Atch records of an 8-byte
    /// identifier and a 64-byte entry.
    /// </summary>
    private static void MetaManipulations(Action<bool, string> check)
    {
        var empty = Gunzip(PenumbraMetaEncoder.Encode([]));
        check(empty.Length == 9 + 10 * 4 && empty[0] == 1 && empty.AsSpan(1, 8).SequenceEqual("META0001"u8) && empty.AsSpan(9).IndexOfAnyExcept((byte)0) < 0,
            "no attach overrides encode as version 1, META0001 and ten zero counts");

        var moved = new AtchManipulation(801, "tsl", 2, new AtchEntry("n_buki_r", 1, new Vector3(0, .05f, 0), Vector3.Zero));
        var encoded = PenumbraMetaEncoder.Encode([moved]);
        var bytes = Gunzip(encoded);
        var atch = 9 + 7 * 4;
        var record = bytes.AsSpan(atch + 4);
        var entry = record[8..];
        check(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(atch)) == 1 && bytes.Length == atch + 4 + 72 + 8 &&
              BinaryPrimitives.ReadUInt32LittleEndian(record) == (('t' << 16) | ('s' << 8) | 'l') &&
              BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) == 801 && BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) == 2,
            "an attach override's identifier is the code as a number, the race and the state");
        check(entry[..35].SequenceEqual("n_buki_r"u8.ToArray().Concat(new byte[27]).ToArray()) && entry[35] == 8 &&
              BinaryPrimitives.ReadSingleLittleEndian(entry[36..]) == 1 && BinaryPrimitives.ReadSingleLittleEndian(entry[40..]) == 0 &&
              BinaryPrimitives.ReadSingleLittleEndian(entry[44..]) == .05f && entry[48..64].IndexOfAnyExcept((byte)0) < 0,
            "an attach override's entry is the NUL padded bone, its length, then scale, offset and rotation");
        check(AnimationMetadata.DecodeEqdp(encoded).Count == 0, "the plugin's own Penumbra meta reader takes the encoded string");
    }

    private static void GamePaths(Action<bool, string> check, Action<Action, string> reject)
    {
        check(AnimationPreviewRules.GamePath(@"\Chara\Human\C0101\Animation\A0001\BT_Common\Emote\Joy.PAP ") ==
              "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap",
            "preview game paths are normalized to Penumbra's form: forward slashes, no leading slash, lowercase");
        foreach (var path in new[]
                 {
                     "chara/action/emote/joy_st.tmb", "sound/voice/vo_emote/vo_emote_kn_c0101.scd",
                     "vfx/common/eff/cmc_emote_joy0f.avfx", "vfx/common/texture/joy_1.atex",
                     "chara/human/c1401/animation/a0001/bt_common/emote/pose 1 loop.pap",
                 })
            check(AnimationPreviewRules.IsSafeGamePath(path), $"a preview may replace {Path.GetExtension(path)} files ({path})");
        // 437: a prop from another mod brings its private body's model, materials and textures.
        foreach (var path in new[]
                 {
                     "chara/weapon/w9001/obj/body/b9119/model/w9001b9119.mdl",
                     "chara/weapon/w9001/obj/body/b9119/material/v0141/mt_m8044b0001_a.mtrl",
                     "chara/weapon/w9001/obj/body/b9119/texture/v01_m8044b0001_d.tex",
                 })
            check(AnimationPreviewRules.IsSafeGamePath(path), $"a preview may carry a prop's private {Path.GetExtension(path)} ({path})");
        check(!AnimationPreviewRules.IsSafeGamePath("chara/weapon/w9001/obj/body/b0200/model/w9001b0200.mdl") &&
              !AnimationPreviewRules.IsSafeGamePath("chara/equipment/e6001/material/v0001/mt_c0101e6001_top_a.mtrl"),
            "models, materials and textures elsewhere stay refused: a game body or gear isn't a prop's private copy");
        foreach (var path in new[]
                 {
                     "", "chara/../joy.pap", "chara/./joy.pap", "chara//joy.pap", "C:/joy.pap", "chara/joy.mdl",
                     "chara/joy.pap/", "chara/jöy.pap", "chara /joy.pap", "chara/ joy.pap", "chara/emote./joy.pap",
                     "chara/joy:1.pap", "chara/joy*.pap", new string('a', 600) + ".pap",
                 })
            reject(() => AnimationPreviewRules.GamePath(path), $"an unsafe preview game path is refused ({(path.Length > 40 ? path[..40] + " (long)" : path)})");
    }

    private static void Layout(Action<bool, string> check, Action<Action, string> reject)
    {
        var cache = Path.Combine(Path.GetTempPath(), "InstantEditAnimationPreviewRegression", "XIV Instant Edit");
        var previewRoot = AnimationPreviewRules.PreviewRoot(cache);
        check(previewRoot == Path.Combine(Path.GetFullPath(cache), "animation-preview"), "previews live in the cache's animation-preview folder");

        var session = AnimationPreviewRules.NewSessionName();
        check(AnimationPreviewRules.IsSessionName(session) && session != AnimationPreviewRules.NewSessionName(),
            "each preview session gets a new 16-digit hexadecimal folder name");
        check(AnimationPreviewRules.IsSessionName("0123456789abcdef") && !AnimationPreviewRules.IsSessionName("0123456789ABCDEF") &&
              !AnimationPreviewRules.IsSessionName("0123456789abcde") && !AnimationPreviewRules.IsSessionName("0123456789abcdeg"),
            "session names are 16 lowercase hexadecimal digits only");
        reject(() => AnimationPreviewRules.SessionFolder(previewRoot, "../outside"), "a session folder name cannot leave the preview folder");

        var sessionFolder = AnimationPreviewRules.SessionFolder(previewRoot, session);
        var revision1 = AnimationPreviewRules.RevisionFolder(sessionFolder, 1);
        var revision2 = AnimationPreviewRules.RevisionFolder(sessionFolder, 2);
        check(revision1 == Path.Combine(sessionFolder, "1") && revision2 == Path.Combine(sessionFolder, "2"),
            "revisions are numbered folders inside their session");

        const string pap = "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap";
        const string tmb = "chara/action/emote/joy_st.tmb";
        var first = AnimationPreviewRules.PlanFiles(revision1, [pap, "CHARA/ACTION/EMOTE/JOY_ST.TMB"]);
        var second = AnimationPreviewRules.PlanFiles(revision2, [pap, tmb]);
        check(first.Count == 2 && first[pap] == Path.Combine(revision1, "chara", "human", "c0101", "animation", "a0001", "bt_common", "emote", "joy.pap") &&
              first[tmb] == Path.Combine(revision1, "chara", "action", "emote", "joy_st.tmb"),
            "each game path is written at its own path inside the revision folder");
        check(first.Keys.All(key => !PathRules.SamePhysicalPath(first[key], second[key])),
            "a new revision maps every game path to a different file, so the game loads it again");
        check(first.Values.All(file => PathRules.IsPathWithin(file, revision1) && Path.IsPathFullyQualified(file)),
            "revision files are full paths inside the revision folder, as Penumbra file redirects need");
        reject(() => AnimationPreviewRules.PlanFiles(revision1, [pap, pap.ToUpperInvariant()]), "the same game path twice is refused");
        reject(() => AnimationPreviewRules.PlanFiles(revision1, []), "a preview without files is refused");
        reject(() => AnimationPreviewRules.PlanFiles(revision1,
                Enumerable.Range(0, AnimationPreviewRules.MaxFiles + 1).Select(i => $"chara/action/emote/clip{i}.tmb")),
            "a preview with too many files is refused");
        var propFiles = Enumerable.Range(1, 256).Select(v => $"chara/weapon/w9001/obj/body/b9119/material/v{v:D4}/mt_m8044b0001_a.mtrl")
            .Concat(Enumerable.Range(1, 256).Select(v => $"chara/weapon/w9001/obj/body/b9893/material/v{v:D4}/mt_w9001b0200_a.mtrl"))
            .Append("chara/weapon/w9001/obj/body/b9119/model/w9001b9119.mdl").ToArray();
        check(AnimationPreviewRules.PlanFiles(revision1, [pap, .. propFiles]).Count == propFiles.Length + 1,
            "two props' private copies (a material in every material folder each) fit beside the clip");
        reject(() => AnimationPreviewRules.PlanFiles(revision1, propFiles), "a preview of prop files alone, without the clip, is refused");
        reject(() => AnimationPreviewRules.PlanFiles(revision1, [pap, .. Enumerable.Range(0, AnimationPreviewRules.MaxPropFiles + 1)
                .Select(i => $"chara/weapon/w9001/obj/body/b9119/texture/t{i}.tex")]),
            "props bringing too many files are refused");
        var deep = Path.Combine(revision1, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 200), 10)));
        reject(() => AnimationPreviewRules.PlanFiles(deep, [pap]), "a file path Penumbra cannot redirect to (2048 characters or more) is refused");

        check(AnimationPreviewRules.TryParseRevision("1", out var one) && one == 1 &&
              AnimationPreviewRules.TryParseRevision("999999", out var last) && last == AnimationPreviewRules.MaxRevision,
            "revision folder names parse back to their numbers");
        check(new[] { "0", "01", "1000000", "-1", "1a", "", " 1" }.All(name => !AnimationPreviewRules.TryParseRevision(name, out _)),
            "only plain revision numbers count as revision folders");
        var refused = 0;
        foreach (var revision in new[] { 0, -1, AnimationPreviewRules.MaxRevision + 1 })
            try { AnimationPreviewRules.RevisionName(revision); }
            catch (InvalidOperationException) { refused++; }
        check(refused == 3, "revision numbers outside 1 to 999999 are refused");

        check(AnimationPreviewRules.IsSessionFolder(sessionFolder) && AnimationPreviewRules.IsSessionFolder(sessionFolder + Path.DirectorySeparatorChar),
            "a recorded session folder is recognized as one this feature made");
        check(!AnimationPreviewRules.IsSessionFolder(previewRoot) &&
              !AnimationPreviewRules.IsSessionFolder(Path.Combine(cache, "texture-edits", session)) &&
              !AnimationPreviewRules.IsSessionFolder(Path.Combine("animation-preview", session)) &&
              !AnimationPreviewRules.IsSessionFolder(Path.Combine(previewRoot, "notes")) &&
              !AnimationPreviewRules.IsSessionFolder(null),
            "a record cannot point the cleanup at the preview root, another cache folder, a relative path or a foreign folder");
        check(first.Values.All(AnimationPreviewRules.IsPreviewFile) && AnimationPreviewRules.IsPreviewFile(first[pap].Replace('\\', '/')),
            "a file a preview revision redirects a game path to is a preview file, whichever slashes Penumbra reports");
        check(!AnimationPreviewRules.IsPreviewFile(Path.Combine(cache, "texture-edits", session, "1", "joy.pap")) &&
              !AnimationPreviewRules.IsPreviewFile(Path.Combine(previewRoot, "notes", "joy.pap")) &&
              !AnimationPreviewRules.IsPreviewFile(Path.Combine("animation-preview", session, "1", "joy.pap")) &&
              !AnimationPreviewRules.IsPreviewFile(pap) && !AnimationPreviewRules.IsPreviewFile(null),
            "mod files, game paths, other cache folders and foreign folders are not preview files");

        var penumbra = Path.Combine(Path.GetTempPath(), "InstantEditAnimationPreviewRegression", "Penumbra");
        check(AnimationPreviewRules.Overlaps(Path.Combine(penumbra, "XIV Instant Edit", "animation-preview"), penumbra) &&
              AnimationPreviewRules.Overlaps(penumbra, Path.Combine(penumbra, "..")),
            "a preview folder inside Penumbra's mod folder (or around it) is detected");
        check(!AnimationPreviewRules.Overlaps(previewRoot, penumbra) && !AnimationPreviewRules.Overlaps(penumbra + "2", penumbra),
            "sibling folders, including ones sharing a name prefix, do not overlap");
    }

    private static void Leftovers(Action<bool, string> check)
    {
        check(AnimationPreviewRules.OlderRevisions(["1", "2", "3", "03", "notes", "4"], 3).SequenceEqual(["1", "2", "4"]),
            "after a revision is live, only the other revision folders of its session are deleted");
        check(AnimationPreviewRules.OlderRevisions(["3"], 3).IsEmpty, "the live revision is never deleted");
        const string keep = "0123456789abcdef";
        const string stale = "fedcba9876543210";
        const string recent = "00000000aaaaaaaa";
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = now - AnimationPreviewRules.StaleSessionAge;
        var old = now.AddDays(-2);
        check(AnimationPreviewRules.StaleSessions(
                  [(keep, old), (stale, old), ("FEDCBA9876543210", old), ("notes", old), ("0123456789abcde", old), ("1", old)], keep, cutoff)
              .SequenceEqual([stale]),
            "stale cleanup deletes only other session folders and leaves foreign names alone");
        check(AnimationPreviewRules.StaleSessions([(keep, old), (stale, old), (recent, now.AddHours(-1))], null, cutoff).SequenceEqual([keep, stale]),
            "at startup every day-old session folder is stale, while a recent one (another game client's live preview) stays");
    }

    private static void Times(Action<bool, string> check)
    {
        check(AnimationPreviewRules.ClampTime(1.5f, 2) == 1.5f && AnimationPreviewRules.ClampTime(5, 2) == 2 &&
              AnimationPreviewRules.ClampTime(-1, 2) == 0 && AnimationPreviewRules.ClampTime(float.NaN, 2) == 0,
            "a seek is clamped to the animation's duration");
        check(AnimationPreviewRules.ClampTime(1, 0) == 0 && AnimationPreviewRules.ClampTime(1, float.NaN) == 0 &&
              AnimationPreviewRules.ClampTime(float.PositiveInfinity, 2) == 2,
            "a seek on an animation without a usable duration goes to its start");
        check(AnimationPreviewRules.ClampSpeed(0.5f) == 0.5f && AnimationPreviewRules.ClampSpeed(-1) == 0 &&
              AnimationPreviewRules.ClampSpeed(9) == 4 && AnimationPreviewRules.ClampSpeed(float.NaN) == 1,
            "playback speed stays between paused and four times");
        check(new[] { "emote/jmn", "emote/j_pose01_loop", "emote/j_clap", "event_base/event_base_ground_start" }.All(key => AnimationPreviewRules.SeatedMode(key) == 1) &&
              new[] { "emote/sit", "emote/s_pose04_start", "emote/s_doze", "event_base/event_base_chair_start" }.All(key => AnimationPreviewRules.SeatedMode(key) == 2) &&
              new[] { "emote/l_pose02_loop", "emote/bed_liedown_loop" }.All(key => AnimationPreviewRules.SeatedMode(key) == 3) &&
              new[] { "normal/idle", "emote/salute", "emote/pose01_loop", "emote/u_amazed", "emote_sp/sp01", "", null }.All(key => AnimationPreviewRules.SeatedMode(key) == 0),
            "ground, chair and bed timelines are told from each other and from standing ones by key");
    }

    private static void Records(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEditAnimationPreviewRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = AnimationPreviewRules.SessionFolder(AnimationPreviewRules.PreviewRoot(Path.Combine(root, "XIV Instant Edit")),
                AnimationPreviewRules.NewSessionName());
            var record = new AnimationPreviewRecord
            {
                Tag = AnimationPreviewService.Tag,
                CollectionId = Guid.NewGuid(),
                Priority = AnimationPreviewService.Priority,
                SessionFolder = session,
                CreatedUtc = new DateTime(2026, 10, 2, 12, 30, 0, DateTimeKind.Utc),
            };
            var store = new AnimationPreviewStore(root);
            store.Load();
            check(store.Record is null && store.LoadError.Length == 0, "without a record file there is no leftover preview");
            store.Save(record);
            var reloaded = new AnimationPreviewStore(root);
            reloaded.Load();
            check(reloaded.Record == record && reloaded.Record!.CreatedUtc.Kind == DateTimeKind.Utc,
                "the preview record round-trips tag, collection, priority, session folder and creation time");
            check(!File.Exists(Path.Combine(root, AnimationPreviewStore.FileName + ".tmp")), "the record is saved through a temporary file");
            reloaded.Clear();
            var cleared = new AnimationPreviewStore(root);
            cleared.Load();
            check(cleared.Record is null && !File.Exists(Path.Combine(root, AnimationPreviewStore.FileName)),
                "clearing the record removes its file");

            var path = Path.Combine(root, AnimationPreviewStore.FileName);
            File.WriteAllText(path, "{ not json");
            var broken = new AnimationPreviewStore(root);
            broken.Load();
            check(broken.Record is null && broken.LoadError.Length > 0 && !File.Exists(path) &&
                  Directory.EnumerateFiles(root, AnimationPreviewStore.FileName + ".unreadable-*").Any(),
                "an unreadable record is set aside, not overwritten, and reported");

            store.Save(record);
            File.WriteAllText(path, File.ReadAllText(path).Replace(record.SessionFolder.Replace(@"\", @"\\"), @"C:\\Windows"));
            var foreign = new AnimationPreviewStore(root);
            foreign.Load();
            check(foreign.Record is null && foreign.LoadError.Length > 0,
                "a record whose session folder is not a preview session folder is refused, so cleanup never deletes elsewhere");

            var empty = record with { CollectionId = Guid.Empty };
            var saved = false;
            try { store.Save(empty); saved = true; }
            catch (InvalidDataException) { }
            check(!saved, "an incomplete record is never saved");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void OwnedCacheEntry(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEditAnimationPreviewRegression", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Combine(root, TextureFiles.CacheFolder);
            var session = AnimationPreviewRules.SessionFolder(AnimationPreviewRules.PreviewRoot(cache), AnimationPreviewRules.NewSessionName());
            var file = AnimationPreviewRules.PlanFiles(AnimationPreviewRules.RevisionFolder(session, 1), ["chara/action/emote/joy_st.tmb"]).Values.Single();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [1, 2, 3]);
            check(TextureFiles.EnsureCacheRoot(root) == cache && File.Exists(Path.Combine(cache, ".instant-edit-cache.json")),
                "a cache whose marker a temp cleaner removed is still recognized when it holds a leftover animation preview");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
