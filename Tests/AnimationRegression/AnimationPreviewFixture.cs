using InstantEdit.Services;
using InstantEdit.Services.Animations.Preview;

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
