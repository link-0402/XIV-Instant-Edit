using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.PenumbraMods;

/// <summary>
/// An All races save's files: staged, journaled, written and undone on a temporary mod. Runs the commit service's staging and
/// its rename and added-file steps; the Penumbra side (resolving, reloading) is checked in game.
/// </summary>
internal static class RaceCopyCommitFixture
{
    private const string Edited = "chara/human/c0101/animation/a0001/bt_common/emote/wave.pap";
    private const string Shared = "chara/human/c1101/animation/a0001/bt_common/emote/wave.pap";
    private const string Added = "chara/human/c0801/animation/a0001/bt_common/emote/wave.pap";
    private const string Earlier = "chara/human/c0901/animation/a0001/bt_common/emote/wave.pap";
    private static readonly byte[] SharedBytes = [5, 5, 5];

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var parent = Path.Combine(Path.GetTempPath(), "InstantEditRaceCopyCommitRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            Paths(check);
            OldJournals(check);
            RoundTrip(parent, reversed: false, check, reject);
            RoundTrip(parent, reversed: true, check, reject);
            NewMod(check);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static AnimationBakeRequest Request(AnimationDestination destination, params string[] copies)
    {
        var clip = new AnimationClip(Edited, "wave", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("a", 7, 1, Guid.NewGuid(), "Test", "Wave", clip, null, [Edited], [], DateTime.UtcNow, true);
        return new(Guid.NewGuid(), capture, destination, destination == AnimationDestination.NewMod ? "New" : "", false, AnimationOperation.EditTimeline,
            Timeline: new AnimationTimelineEdit(false, 0, null) { RaceCopies = [.. copies] });
    }

    private static void Paths(Action<bool, string> check)
    {
        var request = Request(AnimationDestination.InPlace, Shared, Added);
        check(AnimationCommitService.AddedGamePath(Added, request) && AnimationCommitService.AddedGamePath(Added.ToUpperInvariant(), request),
            "a listed copy can be added, however its path is cased");
        check(!AnimationCommitService.AddedGamePath(Earlier, request) && !AnimationCommitService.AddedGamePath(Added), "a PAP the save doesn't list can't");
        check(!AnimationCommitService.AddedGamePath("chara/human/c0801/animation/a0001/bt_common/emote/wave.tex",
                Request(AnimationDestination.InPlace, "chara/human/c0801/animation/a0001/bt_common/emote/wave.tex")),
            "only PAPs can be listed");
        var one = AnimationCommitService.RaceCopyRelative(Added);
        var two = AnimationCommitService.RaceCopyRelative(Added);
        check(RaceCopies.IsCopy(one) && one != two && one.EndsWith(".pap", StringComparison.Ordinal) && one.Contains("/c0801/", StringComparison.Ordinal),
            "a copy lands in the copies' folder, under a fresh name each save");
        check(AnimationResources.SafeModRelativePath(one, Added), "a copy's file passes the mod-file rules");
        check(RaceCopies.IsCopy(AnimationCommitService.AddedRelative(request, Added)) &&
              AnimationCommitService.AddedRelative(Request(AnimationDestination.NewMod, Added), Added).StartsWith(RaceCopies.Folder, StringComparison.Ordinal),
            "a copy goes in the copies' folder in place and in a new mod");
        check(AnimationCommitService.AddedRelative(request, "chara/sound.scd") == "instant_edit/chara/sound.scd" &&
              AnimationCommitService.AddedRelative(Request(AnimationDestination.NewMod), "chara/sound.scd") == "files/chara/sound.scd",
            "any other added file stays where it went");
    }

    private static void OldJournals(Action<bool, string> check)
    {
        var old = JsonSerializer.Deserialize<AnimationTimelineEdit>("""{"Startup":false,"EntryIndex":0,"DurationSeconds":null}""")!;
        check(old.RaceCopies.IsEmpty, "a journal from before All races lists no copies");
        var change = JsonSerializer.Deserialize<AnimationFileChange>(
            """{"GamePath":"a","Target":"b","ModDirectory":"c","ModRoot":"d","RelativePath":"e","BeforeHash":"","AfterHash":"f","Backup":"","Staged":"g"}""")!;
        check(!change.RaceCopy, "a journal entry from before All races is no race copy");
    }

    /// <summary>
    /// A mod mapping one file at the edited path and at a second race's, an option holding another file for the edited path, a race the mod
    /// doesn't cover, and an earlier copy. Saved and undone with the file steps in the journal's order, or reversed.
    /// </summary>
    private static void RoundTrip(string parent, bool reversed, Action<bool, string> check, Action<Action, string> reject)
    {
        var note = reversed ? " (reversed)" : "";
        var home = Path.Combine(parent, reversed ? "Reversed" : "Forward");
        var mod = Path.Combine(home, "Mod");
        const string earlierFile = RaceCopies.Folder + "chara/human/c0901/animation/a0001/bt_common/emote/wave.ie00112233.pap";
        Directory.CreateDirectory(Path.Combine(mod, "files"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(mod, earlierFile))!);
        File.WriteAllBytes(Path.Combine(mod, "files", "shared.pap"), SharedBytes);
        File.WriteAllBytes(Path.Combine(mod, "files", "other.pap"), [8]);
        File.WriteAllBytes(Path.Combine(mod, earlierFile), [7, 7]);
        var meta = ModMetadata.CreateV4ModMetadata("Mod", "Author", "Description", "1.0", new JsonObject
        {
            ["Files"] = new JsonObject { [Edited] = "files/shared.pap", [Shared] = "files/shared.pap", [Earlier] = earlierFile },
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        });
        meta["Groups"] = new JsonArray(new JsonObject
        {
            ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Style",
            ["Options"] = new JsonArray(
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Same", ["Files"] = new JsonObject { [Edited] = "files/shared.pap" } },
                new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Other", ["Files"] = new JsonObject { [Edited] = "files/other.pap" } }),
        });
        var metaPath = Path.Combine(mod, "meta.json");
        File.WriteAllText(metaPath, meta.ToJsonString());
        var before = Unstamped(metaPath);

        var config = Path.Combine(home, "Config");
        var store = new AnimationJournalStore(config);
        var backups = new ModelBackupStore(config);
        var commits = new AnimationCommitService(null!, null!, backups, store);
        var hash = AnimationPap.Hash(SharedBytes);
        var sharedFile = Path.Combine(mod, "files", "shared.pap");
        var request = Request(AnimationDestination.InPlace, Shared, Added, Earlier);
        ImmutableArray<AnimationResource> resources =
        [
            new(Edited, sharedFile, hash, "Mod", mod, "files/shared.pap"),
            new(Shared, sharedFile, hash, "Mod", mod, "files/shared.pap"),
            new(Earlier, Path.Combine(mod, earlierFile), AnimationPap.Hash([7, 7]), "Mod", mod, earlierFile),
        ];
        var manifest = new AnimationDependencyManifest(resources, ImmutableDictionary<string, byte[]>.Empty);
        var outputs = ImmutableDictionary<string, byte[]>.Empty.Add(Edited, [1, 1]).Add(Shared, [2, 2]).Add(Earlier, [4, 4]);
        IReadOnlyDictionary<string, byte[]> added = new Dictionary<string, byte[]> { [Added] = [3, 3] };
        var journal = commits.PrepareAsync(request, manifest, outputs, CancellationToken.None, (Edited, added, null, null, null)).GetAwaiter().GetResult();

        var edited = journal.Files.Single(f => f.GamePath == Edited);
        var shared = journal.Files.Single(f => f.GamePath == Shared);
        var earlier = journal.Files.Single(f => f.GamePath == Earlier);
        var copy = journal.Files.Single(f => f.GamePath == Added);
        check(edited.RelativePath.StartsWith("files/shared.ie", StringComparison.Ordinal) && !edited.RaceCopy, "the edited file renames where it is" + note);
        check(RaceCopies.IsCopy(shared.RelativePath) && shared.RenamedFromRelativePath == "files/shared.pap" && shared.AddedFor == null,
            "a shared file's other path moves to a copy of its own, through the rename route" + note);
        check(RaceCopies.IsCopy(earlier.RelativePath) && earlier.RelativePath != earlierFile && earlier.RenamedFromRelativePath == earlierFile,
            "an earlier copy gets a fresh name in the copies' folder" + note);
        check(RaceCopies.IsCopy(copy.RelativePath) && copy.RaceCopy && copy.AddedFor == Edited && copy.AddedForFile == edited.RelativePath,
            "a new copy is added beside the edited file as it will be after its rename" + note);
        check(!AnimationCommitService.SharesTarget(journal.Files), "no two entries write one file" + note);

        var reloaded = store.Load().Single(j => j.Id == journal.Id);
        check(reloaded.Request.Timeline!.RaceCopies.SequenceEqual([Shared, Added, Earlier]) && reloaded.Files.Single(f => f.GamePath == Added).RaceCopy,
            "the copies survive the journal being read back" + note);
        var valid = true;
        foreach (var file in reloaded.Files)
            try { commits.ValidateTarget(reloaded, file); }
            catch (InvalidDataException) { valid = false; }
        check(valid, "recovery accepts every entry of the read-back journal" + note);

        // The commit's file steps: outputs before added files, as the journal holds them; backups taken as the commit takes them.
        var renames = journal.Files.Where(f => f.AddedFor == null).ToArray();
        if (reversed) Array.Reverse(renames);
        var written = new List<AnimationFileChange>();
        foreach (var file in renames)
        {
            var old = Path.GetFullPath(Path.Combine(file.ModRoot, file.RenamedFromRelativePath!));
            var withBackup = file with { Backup = backups.Create(old, file.ModDirectory, file.RenamedFromRelativePath!) };
            Directory.CreateDirectory(Path.GetDirectoryName(withBackup.Target)!);
            AnimationJournalStore.WriteAtomic(withBackup.Target, File.ReadAllBytes(withBackup.Staged));
            AnimationCommitService.FinishRename(withBackup);
            written.Add(withBackup);
        }
        AnimationCommitService.WriteAdded(copy, journal.Files, inPlace: true);

        string? Mapped(JsonNode? container, string path) => container?["Files"]?[path]?.GetValue<string>();
        var saved = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var defaults = saved["DefaultData"];
        var same = saved["Groups"]![0]!["Options"]![0];
        var other = saved["Groups"]![0]!["Options"]![1];
        check(Mapped(defaults, Edited) == edited.RelativePath && Mapped(defaults, Shared) == shared.RelativePath && Mapped(defaults, Earlier) == earlier.RelativePath,
            "each path maps its own new file" + note);
        check(Mapped(defaults, Added) == copy.RelativePath && Mapped(same, Added) == copy.RelativePath, "a new copy maps wherever the edited file does" + note);
        check(Mapped(other, Edited) == "files/other.pap" && Mapped(other, Added) == null && Mapped(other, Shared) == null,
            "an option holding another file for the edited path gets nothing" + note);
        check(!File.Exists(sharedFile), "the shared file goes once nothing maps it" + note);
        check(!File.Exists(Path.Combine(mod, earlierFile)), "the earlier copy goes" + note);
        check(File.ReadAllBytes(shared.Target).SequenceEqual<byte>([2, 2]) && File.ReadAllBytes(copy.Target).SequenceEqual<byte>([3, 3]), "each copy holds its own bytes" + note);

        // Undo, as the service runs it: in the journal's order, or reversed.
        AnimationCommitService.CheckAddedUndo(copy);
        var undoing = written.ToArray();
        if (reversed) Array.Reverse(undoing);
        AnimationCommitService.UndoAdded(copy, journal.Files);
        foreach (var file in undoing)
            AnimationCommitService.UndoRename(file, () => backups.Resolve(backups.Describe(file.ModDirectory, file.RenamedFromRelativePath!).Id, Path.GetFileName(file.Backup)));
        check(Unstamped(metaPath) == before, "undo leaves meta.json as it was before the save" + note);
        check(File.ReadAllBytes(sharedFile).SequenceEqual(SharedBytes), "undo brings the shared file back byte for byte" + note);
        check(File.ReadAllBytes(Path.Combine(mod, earlierFile)).SequenceEqual<byte>([7, 7]), "undo brings the earlier copy back byte for byte" + note);
        check(File.ReadAllBytes(Path.Combine(mod, "files", "other.pap")).SequenceEqual<byte>([8]), "the option's other file was never touched" + note);
        check(journal.Files.All(f => f.RenamedFromRelativePath is not null && f.GamePath != Added || !File.Exists(f.Target)) && !File.Exists(copy.Target) &&
              !File.Exists(edited.Target) && !File.Exists(shared.Target) && !File.Exists(earlier.Target), "undo takes every new file away" + note);
        foreach (var file in undoing)
            AnimationCommitService.UndoRename(file, () => throw new InvalidOperationException("Nothing to restore."));
        check(Unstamped(metaPath) == before && File.ReadAllBytes(sharedFile).SequenceEqual(SharedBytes), "undoing again after a restart changes nothing" + note);

        // A copy can't be added beside an edited file the save doesn't write.
        reject(() => commits.PrepareAsync(request with { Id = Guid.NewGuid() }, manifest, ImmutableDictionary<string, byte[]>.Empty, CancellationToken.None,
            (Edited, added, null, null, null)).GetAwaiter().GetResult(), "a copy beside an edited file the save doesn't write is refused" + note);
        var unlisted = Request(AnimationDestination.InPlace);
        reject(() => commits.PrepareAsync(unlisted, manifest, ImmutableDictionary<string, byte[]>.Empty.Add(Edited, [1, 1]), CancellationToken.None,
            (Edited, added, null, null, null)).GetAwaiter().GetResult(), "a PAP the save doesn't list can't be added" + note);
    }

    private static void NewMod(Action<bool, string> check)
    {
        const string root = @"D:\Mods\New";
        var copy = AnimationCommitService.RaceCopyRelative(Added);
        var metadata = AnimationCommitService.CreateNewModMetadata("New", [
            new AnimationFileChange(Edited, root + @"\files\" + Edited, "New", root, "files/" + Edited, "", "a", "", "s1"),
            new AnimationFileChange(Added, root + "\\" + copy, "New", root, copy, "", "b", "", "s2", AddedFor: Edited, RaceCopy: true),
        ], "[]", "Test");
        var files = metadata["DefaultData"]!["Files"]!;
        check(files[Edited]!.GetValue<string>() == "files/" + Edited && files[Added]!.GetValue<string>() == copy, "a new mod's metadata maps the edited file and every copy");
    }

    private static string Unstamped(string metaPath)
    {
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        meta.Remove("LastWrite");
        return meta.ToJsonString();
    }
}
