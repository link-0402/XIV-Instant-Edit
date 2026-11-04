using System.Collections.Immutable;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;

/// <summary>
/// A save that writes a face library file into an expression library mod other than its own: staged and journaled
/// beside the save's files, written at a fresh path the library maps, and put back on undo, or a library the save made
/// disabled. Runs the commit service's staging and its library steps on temporary mod folders; the Penumbra side
/// (resolving, adding, enabling, reloading) is checked in game.
/// </summary>
internal static class FaceLibraryCommitFixture
{
    private const string Pap = "chara/human/c0101/animation/a0001/bt_common/emote/laugh.pap";
    private const string LibraryPath = "chara/human/c0101/animation/f0001/nonresident/test_faces.pap";
    private static readonly FaceLibraryMarker Marker = new(FaceLibraryMod.Version, "Test Faces", "test_faces");
    private static readonly byte[] OldBytes = [5, 5, 5];
    private static readonly byte[] NewBytes = [6, 6, 6, 6];

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var parent = Path.Combine(Path.GetTempPath(), "InstantEditFaceLibraryCommitRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var config = Path.Combine(parent, "Config");
            var store = new AnimationJournalStore(config);
            var commits = new AnimationCommitService(null!, null!, new ModelBackupStore(config), store);
            var mod = Path.Combine(parent, "Mod");
            Directory.CreateDirectory(Path.Combine(mod, "files"));
            File.WriteAllBytes(Path.Combine(mod, "files", "laugh.pap"), [9]);

            OldJournal(commits, store, mod, parent, check);
            Existing(commits, store, mod, parent, check);
            NoFileYet(commits, mod, parent, check);
            Created(commits, mod, parent, check);
            LaterChange(commits, mod, parent, check, reject);
            Refused(commits, mod, parent, check, reject);
            LibraryOnly(commits, store, mod, parent, check, reject);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>An in-place timeline save of the laugh in <paramref name="mod"/>.</summary>
    private static (AnimationBakeRequest Request, AnimationDependencyManifest Manifest) Save(string mod)
    {
        var clip = new AnimationClip(Pap, "laugh", 0, 0, 42, "chara/test.sklb");
        var path = Path.Combine(mod, "files", "laugh.pap");
        var capture = new AnimationCapture("a", 7, 1, Guid.NewGuid(), "Test", "Laugh", clip, null, [Pap],
            [new AnimationResource(Pap, path, AnimationPap.Hash(File.ReadAllBytes(path)), "Mod", mod, "files/laugh.pap")], DateTime.UtcNow, true);
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false,
            AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(false, 0, null));
        return (request, new AnimationDependencyManifest(capture.Sources, ImmutableDictionary<string, byte[]>.Empty));
    }

    private static AnimationEditJournal Prepare(AnimationCommitService commits, string mod, AnimationCommitService.LibraryFile library)
    {
        var (request, manifest) = Save(mod);
        return commits.PrepareAsync(request, manifest, ImmutableDictionary<string, byte[]>.Empty.Add(Pap, [1, 2, 3]), CancellationToken.None,
            library: library).GetAwaiter().GetResult();
    }

    /// <summary>A library mod in <paramref name="parent"/>: its marker, and with <paramref name="withFile"/> its PAP for the face folder.</summary>
    private static string Library(string parent, string name, bool withFile)
    {
        var root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "files"));
        var files = new JsonObject();
        if (withFile)
        {
            File.WriteAllBytes(Path.Combine(root, "files", "faces.pap"), OldBytes);
            files[LibraryPath] = "files/faces.pap";
        }
        var meta = ModMetadata.CreateV4ModMetadata(name, "Author", "Description", "1.0", new JsonObject
        {
            ["Files"] = files, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
        });
        File.WriteAllText(Path.Combine(root, "meta.json"), meta.ToJsonString());
        File.WriteAllText(Path.Combine(root, FaceLibraryMod.MarkerFile), FaceLibraryMod.Serialize(Marker));
        return root;
    }

    private static AnimationCommitService.LibraryFile Existing(string root, string gamePath = LibraryPath)
    {
        var current = Path.Combine(root, "files", "faces.pap");
        return new(gamePath, NewBytes, Path.GetFileName(root), root, Marker, false,
            File.Exists(current) ? new AnimationResource(LibraryPath, current, AnimationPap.Hash(File.ReadAllBytes(current)), Path.GetFileName(root), root, "files/faces.pap") : null);
    }

    private static IReadOnlyList<string> Mapped(string root) => ModMetadata.AnimationMappedFiles(root, LibraryPath);

    private static (int Disabled, int Reloaded) Undo(AnimationCommitService commits, AnimationEditJournal journal, AnimationCommitService.LibraryUndo step)
    {
        var (disabled, reloaded) = (0, 0);
        commits.UndoLibraryAsync(journal, step, () => { disabled++; return Task.CompletedTask; }, () => { reloaded++; return Task.CompletedTask; })
            .GetAwaiter().GetResult();
        return (disabled, reloaded);
    }

    /// <summary>A journal from before libraries reads with none; one with a library reads it back.</summary>
    private static void OldJournal(AnimationCommitService commits, AnimationJournalStore store, string mod, string parent, Action<bool, string> check)
    {
        var root = Library(parent, "Old Journal Faces", withFile: true);
        var journal = Prepare(commits, mod, Existing(root));
        var path = Path.Combine(store.DirectoryFor(journal.Id), "journal.json");
        var read = store.Load().Single(j => j.Id == journal.Id);
        var valid = true;
        try { commits.ValidateLibrary(read, read.Library!); }
        catch (InvalidDataException) { valid = false; }
        check(read.Library is { Created: false, Pack: "test_faces", File.RenamedFromRelativePath: "files/faces.pap" } && valid,
            "a journal with a library file reads it back and recovery accepts it");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("Library");
        File.WriteAllText(path, json.ToJsonString());
        check(store.Load().Single(j => j.Id == journal.Id) is { Library: null, Files.Count: 1 }, "a journal written before libraries loads with none");
    }

    /// <summary>An existing library's file: backed up, written at a fresh path the library maps, then put back.</summary>
    private static void Existing(AnimationCommitService commits, AnimationJournalStore store, string mod, string parent, Action<bool, string> check)
    {
        var root = Library(parent, "Existing Faces", withFile: true);
        var mainMeta = File.Exists(Path.Combine(mod, "meta.json"));
        var journal = Prepare(commits, mod, Existing(root));
        var library = journal.Library!;
        check(journal.Files.Count == 1 && journal.Files.All(f => f.ModDirectory == "Mod") &&
              library.File.RelativePath.StartsWith("files/faces.ie", StringComparison.Ordinal) && library.File.RenamedFromRelativePath == "files/faces.pap" &&
              File.Exists(library.File.Staged) && !File.Exists(library.File.Target),
            $"the library file is staged at a fresh path beside the save's own files, not among them ({library.File.RelativePath})");

        commits.WriteLibrary(journal, null);
        library = journal.Library!;
        check(File.ReadAllBytes(library.File.Target).SequenceEqual(NewBytes) && Mapped(root).SequenceEqual([library.File.RelativePath]) &&
              !File.Exists(Path.Combine(root, "files", "faces.pap")) && library.File.Backup.Length > 0 &&
              store.Load().Single(j => j.Id == journal.Id).Library!.File.Backup == library.File.Backup,
            "commit writes the new file, maps the library's game path to it, and journals the old file's backup");
        check(File.Exists(Path.Combine(mod, "meta.json")) == mainMeta, "the save's own mod is not touched by the library step");

        journal.State = "Activated";
        var step = commits.CheckLibraryUndo(journal);
        var (disabled, reloaded) = Undo(commits, journal, step);
        check(step == AnimationCommitService.LibraryUndo.Restore && disabled == 0 && reloaded == 1 &&
              File.ReadAllBytes(Path.Combine(root, "files", "faces.pap")).SequenceEqual(OldBytes) && Mapped(root).SequenceEqual(["files/faces.pap"]) &&
              !File.Exists(library.File.Target),
            "undo puts the old file and mapping back and removes the new file");
        check(commits.CheckLibraryUndo(journal) == AnimationCommitService.LibraryUndo.Nothing, "undoing again after a restart finds nothing left");
    }

    /// <summary>An existing library with no file for this face folder yet: mapped in its default data, taken out on undo.</summary>
    private static void NoFileYet(AnimationCommitService commits, string mod, string parent, Action<bool, string> check)
    {
        var root = Library(parent, "Growing Faces", withFile: false);
        var journal = Prepare(commits, mod, Existing(root));
        commits.WriteLibrary(journal, null);
        var file = journal.Library!.File;
        check(file.RenamedFromRelativePath == null && file.BeforeHash == "" && Mapped(root).SequenceEqual([file.RelativePath]) && File.Exists(file.Target),
            "a library without a file for the face folder gets one mapped in its default data");
        var step = commits.CheckLibraryUndo(journal);
        Undo(commits, journal, step);
        check(step == AnimationCommitService.LibraryUndo.Restore && Mapped(root).Count == 0 && !File.Exists(file.Target),
            "undo takes that mapping and file out again");
    }

    /// <summary>A library the save makes: its folder, files and markers; undo disables it and deletes nothing.</summary>
    private static void Created(AnimationCommitService commits, string mod, string parent, Action<bool, string> check)
    {
        var root = Path.Combine(parent, "New Faces");
        var journal = Prepare(commits, mod, new(LibraryPath, NewBytes, "New Faces", root, Marker, true, null));
        check(!Directory.Exists(root) && journal.Library is { Created: true }, "prepare stages a new library without making its folder");
        commits.WriteLibrary(journal, "Tester");
        var file = journal.Library!.File;
        var meta = ModMetadata.LoadV4ModMetadata(root);
        var owner = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".instant-edit-animation.json")))!;
        check(FaceLibraryMod.TryRead(root, out var marker) && marker == Marker && Mapped(root).SequenceEqual([file.RelativePath]) &&
              File.ReadAllBytes(file.Target).SequenceEqual(NewBytes) && meta["Name"]!.GetValue<string>() == "Test Faces" &&
              meta["Author"]!.GetValue<string>() == "Tester" && owner["Id"]!.GetValue<Guid>() == journal.Id,
            "a new library gets its PAP, meta.json, library marker and the job's ownership marker");
        var step = commits.CheckLibraryUndo(journal);
        var (disabled, reloaded) = Undo(commits, journal, step);
        check(step == AnimationCommitService.LibraryUndo.Disable && disabled == 1 && reloaded == 0 && File.Exists(file.Target) &&
              Mapped(root).SequenceEqual([file.RelativePath]),
            "undo of a library the save made disables it and deletes nothing");
        var other = Prepare(commits, mod, new(LibraryPath, NewBytes, "Half Faces", Path.Combine(parent, "Half Faces"), Marker, true, null));
        Directory.CreateDirectory(other.Library!.ModRoot);
        File.WriteAllText(Path.Combine(other.Library.ModRoot, ".instant-edit-animation.json"), $"{{\"Id\":\"{other.Id}\"}}");
        check(commits.CheckLibraryUndo(other) == AnimationCommitService.LibraryUndo.Disable && !FaceLibraryMod.TryRead(other.Library.ModRoot, out _),
            "a library stopped before its meta.json is never listed, and its undo only disables");
    }

    /// <summary>A save whose only file is the library's (faces copied in): nothing of a mod of its own, and it reads back so.</summary>
    private static void LibraryOnly(AnimationCommitService commits, AnimationJournalStore store, string mod, string parent,
        Action<bool, string> check, Action<Action, string> reject)
    {
        var (save, manifest) = Save(mod);
        var request = save with { Id = Guid.NewGuid(), Destination = AnimationDestination.Library, Operation = AnimationOperation.CopyFaces, Timeline = null };
        AnimationEditJournal Prepare(AnimationBakeRequest bake, ImmutableDictionary<string, byte[]> outputs, AnimationCommitService.LibraryFile? library) =>
            commits.PrepareAsync(bake, manifest, outputs, CancellationToken.None, library: library).GetAwaiter().GetResult();

        var root = Library(parent, "Copied Faces", withFile: true);
        var journal = Prepare(request, ImmutableDictionary<string, byte[]>.Empty, Existing(root));
        check(journal.Files.Count == 0 && journal.ModDirectory == "" && journal.Library is { Created: false, File.RenamedFromRelativePath: "files/faces.pap" } &&
              !AnimationCommitService.NoFilesLeft(journal, false),
            "a library save journals the library's file alone");
        check(store.Load().Single(j => j.Id == journal.Id) is { Request: { Destination: AnimationDestination.Library, Operation: AnimationOperation.CopyFaces }, Files.Count: 0 },
            "a library save's journal reads back with its destination and operation");
        commits.WriteLibrary(journal, null);
        var step = commits.CheckLibraryUndo(journal);
        Undo(commits, journal, step);
        check(step == AnimationCommitService.LibraryUndo.Restore && File.ReadAllBytes(Path.Combine(root, "files", "faces.pap")).SequenceEqual(OldBytes) &&
              Mapped(root).SequenceEqual(["files/faces.pap"]),
            "a library save's undo puts the library's old file back");

        reject(() => Prepare(request with { Id = Guid.NewGuid() }, ImmutableDictionary<string, byte[]>.Empty, null),
            "a library save without a library file is refused");
        reject(() => Prepare(request with { Id = Guid.NewGuid() }, ImmutableDictionary<string, byte[]>.Empty.Add(Pap, [1]), Existing(Library(parent, "Other Faces", withFile: true))),
            "a library save that writes another file is refused");
    }

    /// <summary>A later save changed the library since: the undo is refused and the library stays as it is.</summary>
    private static void LaterChange(AnimationCommitService commits, string mod, string parent, Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Library(parent, "Saved Faces", withFile: true);
        var journal = Prepare(commits, mod, Existing(root));
        commits.WriteLibrary(journal, null);
        journal.State = "Activated";
        // As a later save does: its own fresh path, mapped in place of this one, and this one's file gone.
        var file = journal.Library!.File;
        var later = AnimationFileRename.NextRelativePath(file.RelativePath);
        File.WriteAllBytes(Path.Combine(root, later), [7]);
        ModMetadata.RenameAnimationFileMapping(root, LibraryPath, file.RelativePath, later);
        File.Delete(file.Target);
        reject(() => commits.CheckLibraryUndo(journal), "undo of a save into a library a later save changed is refused");
        check(Mapped(root).SequenceEqual([later]), "a refused library undo leaves the later save's file mapped");
    }

    private static void Refused(AnimationCommitService commits, string mod, string parent, Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Library(parent, "Checked Faces", withFile: true);
        reject(() => Prepare(commits, mod, Existing(root, gamePath: "chara/human/c0101/animation/f0001/nonresident/other_faces.pap")),
            "a library game path whose pack isn't the library's is refused at prepare");
        reject(() => Prepare(commits, mod, Existing(root, gamePath: "chara/human/c0101/animation/f0001/resident/face.pap")),
            "a game path that isn't a library file is refused at prepare");
        reject(() => Prepare(commits, mod, new(LibraryPath, NewBytes, "Mod", mod, Marker, false, null)),
            "the save's own mod can't take its library file");
        reject(() => Prepare(commits, mod, new(LibraryPath, NewBytes, "Checked Faces", root, Marker, true, null)),
            "a new library over an existing folder is refused");

        var journal = Prepare(commits, mod, Existing(root));
        var meta = File.ReadAllText(Path.Combine(root, "meta.json"));
        File.WriteAllBytes(Path.Combine(root, "files", "faces.pap"), [8, 8]);
        reject(() => commits.WriteLibrary(journal, null), "a library file changed since the save was built refuses the commit");
        check(!File.Exists(journal.Library!.File.Target) && File.ReadAllText(Path.Combine(root, "meta.json")) == meta &&
              journal.Library.File.Backup == "" && File.ReadAllBytes(Path.Combine(root, "files", "faces.pap")).SequenceEqual(new byte[] { 8, 8 }),
            "and writes nothing before refusing");
    }
}
