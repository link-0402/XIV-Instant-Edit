using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Models;

namespace InstantEdit.Services.Animations;

/// <summary>An expression library Instant Edit made: its mod folder name, its folder on disk and its marker.</summary>
internal sealed record InstantEditLibrary(string ModDirectory, string ModRoot, FaceLibraryMarker Marker);

/// <summary>Where copied faces go: one of the Instant Edit libraries, or a new one named <paramref name="NewName"/>.</summary>
internal sealed record FaceCopyTarget(InstantEditLibrary? Library, string? NewName);

/// <summary>
/// A face, blink or lip shape to copy: the PAP it plays from, its motion name there, and its name in the library. With
/// <paramref name="Built"/> it is a face session's edit, taken from that build instead of the file.
/// </summary>
internal sealed record FaceCopySource(string GamePath, string Motion, string Name, FaceCopyBuilt? Built = null);

/// <summary>A face's file with a face session's edit baked in, and the file as the collection gave it.</summary>
internal sealed record FaceCopyBuilt(byte[] Pap, AnimationResource Resource);

/// <summary>
/// Copying <paramref name="Faces"/> into <paramref name="Target"/>'s file for <paramref name="Race"/>'s face folder
/// <paramref name="Face"/>, as <paramref name="Capture"/>'s collection gives them. <paramref name="Replace"/> lets a copy
/// replace a face of the same name the library has for another motion. An edited face, or one under a new name, can't
/// take a name of <paramref name="GameNames"/>, the faces the game has for the folder.
/// </summary>
internal sealed record FaceCopyRequest(AnimationCapture Capture, string Race, string Face, FaceCopyTarget Target,
    ImmutableArray<FaceCopySource> Faces, bool Replace, AnimationOperation Operation = AnimationOperation.CopyFaces,
    IReadOnlySet<string>? GameNames = null);

/// <summary>
/// A face session saved into a library: <paramref name="Source"/>'s face with <paramref name="Document"/>'s edits, or unchanged
/// when it has none, under <paramref name="Name"/> (cfxf_ and its stem). <paramref name="GameNames"/> and
/// <paramref name="Replace"/> as in <see cref="FaceCopyRequest"/>.
/// </summary>
internal sealed record FaceLibrarySave(AnimationTimelineSource Source, TimelineDocument Document, FaceCopyTarget Target, string Name,
    IReadOnlySet<string> GameNames, bool Replace);

/// <summary>
/// What a copy did, by face name in the library. <paramref name="JournalId"/> is the save's, null when nothing was written:
/// every face was there already, or <paramref name="Asks"/> lists the names that need a yes to replace.
/// </summary>
internal sealed record FaceCopyOutcome(Guid? JournalId, string LibraryName, ImmutableArray<string> Added, ImmutableArray<string> Already,
    ImmutableArray<string> Replaced, ImmutableArray<string> Asks);

/// <summary>Instant Edit's own expression libraries, and copying faces into them unchanged.</summary>
internal sealed partial class AnimationEditService
{
    // The face folders a game pack may sit in, for every race: f0001-f0010, f0101-f0110 and f0201-f0210.
    private static readonly string[] GameFaceFolders =
        [.. Enumerable.Range(1, 10).Concat(Enumerable.Range(101, 10)).Concat(Enumerable.Range(201, 10)).Select(n => $"f{n:D4}")];

    /// <summary>The installed mods that are Instant Edit expression libraries (a valid marker in their root), by name.</summary>
    public async Task<ImmutableArray<InstantEditLibrary>> InstantEditLibrariesAsync()
    {
        var mods = await animationMods.FaceLibraryRootsAsync().ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<InstantEditLibrary>();
        foreach (var (directory, _, root) in mods)
            if (directory.Length > 0 && root.Length > 0 && FaceLibraryMod.TryRead(root, out var marker))
                result.Add(new InstantEditLibrary(directory, root, marker));
        return [.. result.OrderBy(l => l.Marker.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Why a new library can't be named <paramref name="name"/>, or null: it must make a valid mod folder name and a pack
    /// no game face folder of any race and no installed expression set (<paramref name="libraries"/> included) uses.
    /// </summary>
    internal string? NewLibraryProblem(string name, IEnumerable<InstantEditLibrary> libraries)
    {
        if (!ModPaths.IsSafeNewModName(name)) return "Choose a valid library name.";
        if (FaceLibraryMod.PackName(name) is not { } pack) return "Use letters or numbers in the name.";
        return FaceLibraryMod.PackProblem(pack, GamePackAnywhere,
            FaceLibraries.Libraries.Select(l => l.Pack).Concat(libraries.Select(l => l.Marker.Pack)));
    }

    private bool GamePackAnywhere(string pack) => RaceInfo.PlayableCodes.Any(code =>
        GameFaceFolders.Any(face => resources.GameFileExists(FacePackPath($"c{code:D4}", face, pack))));

    /// <summary>
    /// Copies faces into an Instant Edit library unchanged, as a save of its own with its own undo (<see cref="Undo"/>).
    /// Fails at once while another edit runs.
    /// </summary>
    public Task<FaceCopyOutcome> CopyFacesAsync(FaceCopyRequest request) => LaunchLibraryJob(token => CopyFacesCoreAsync(request, token));

    /// <summary>
    /// Saves a face session's face into an Instant Edit library: an edited face is baked as a save in place bakes it and goes
    /// in under its new name; an unedited one is copied as it is. The library's file is the only one written, as a save of
    /// its own with its own undo. Fails at once while another edit runs.
    /// </summary>
    public Task<FaceCopyOutcome> SaveFaceToLibraryAsync(FaceLibrarySave save) => LaunchLibraryJob(token => SaveFaceToLibraryCoreAsync(save, token));

    private Task<FaceCopyOutcome> LaunchLibraryJob(Func<CancellationToken, Task<FaceCopyOutcome>> work)
    {
        var done = new TaskCompletionSource<FaceCopyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (launchLock)
        {
            if (Busy || disposed)
            {
                done.SetException(new InvalidOperationException("Another edit is running. Wait for it to end."));
                return done.Task;
            }
            LaunchLocked(async token =>
            {
                try { done.TrySetResult(await work(token)); }
                catch (OperationCanceledException) { done.TrySetCanceled(); throw; }
                catch (Exception e) { done.TrySetException(e); throw; }
            }, null, null);
        }
        return done.Task;
    }

    private async Task<FaceCopyOutcome> SaveFaceToLibraryCoreAsync(FaceLibrarySave save, CancellationToken token)
    {
        var source = save.Source;
        if (save.Document.Face == null || source.Document == null) throw new InvalidOperationException("Only a face can be saved into a library.");
        var face = source.MotionTarget;
        var gamePath = face.Resource.GamePath;
        var parts = gamePath.Replace('\\', '/').ToLowerInvariant().Split('/');
        if (parts.Length < 6 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation" || !parts[4].StartsWith('f'))
            throw new InvalidDataException("This face is not in a face folder.");
        var id = Guid.NewGuid();
        var edited = FaceChanged(save.Document);
        FaceCopySource copy;
        if (edited)
        {
            await CheckActorAsync(source.Capture, false);
            Status = "Reading the face";
            // The face must still be the file the session loaded, or the bake would drop newer work.
            var (resource, bytes) = await resources.ReadAsync(source.Capture.CollectionId, gamePath, token);
            if (resource.Hash != face.Resource.Hash) throw new IOException("The face's file changed while saving. Open the timeline again.");
            var request = TimelineRequest(source, save.Document, AnimationDestination.Library, "") with { Id = id };
            CheckBones(request, source);
            var built = await BuildFaceAsync(request, face, bytes, save.Document, Path.Combine(journals.DirectoryFor(id), "bake"), token);
            copy = new FaceCopySource(gamePath, face.Clip.Name, save.Name, new FaceCopyBuilt(built.Bytes, resource));
        }
        else
            copy = new FaceCopySource(gamePath, face.Clip.Name, save.Name);
        return await CopyFacesCoreAsync(new FaceCopyRequest(source.Capture, parts[2], parts[4], save.Target, [copy], save.Replace,
            edited ? AnimationOperation.EditTimeline : AnimationOperation.CopyFaces, save.GameNames), token, id);
    }

    private async Task<FaceCopyOutcome> CopyFacesCoreAsync(FaceCopyRequest request, CancellationToken token, Guid? journalId = null)
    {
        native.EnsureAvailable();
        var capture = request.Capture;
        if (request.Faces.IsDefaultOrEmpty) throw new InvalidDataException("Choose a face to copy.");
        if (!ushort.TryParse(request.Race.AsSpan(1), out var modelId) || request.Race.Length != 5 || request.Race[0] != 'c' ||
            request.Face.Length != 5 || request.Face[0] != 'f' || !ushort.TryParse(request.Face.AsSpan(1), out _))
            throw new InvalidDataException("The faces are copied for one race and face.");
        await CheckActorAsync(capture, false);
        Status = "Reading the faces";

        // The library, and its file for this face folder when it has one.
        string modDirectory, root;
        FaceLibraryMarker marker;
        var created = request.Target.Library == null;
        if (request.Target.Library is { } chosen)
        {
            if (!FaceLibraryMod.TryRead(chosen.ModRoot, out marker) || marker != chosen.Marker)
                throw new IOException("That mod is no longer the expression library it was.");
            (modDirectory, root) = (chosen.ModDirectory, chosen.ModRoot);
        }
        else
        {
            var name = (request.Target.NewName ?? "").Trim();
            if (NewLibraryProblem(name, await InstantEditLibrariesAsync()) is { } problem) throw new InvalidDataException(problem);
            modDirectory = name;
            root = Path.GetFullPath(Path.Combine(await animationMods.AnimationModRootAsync(), name));
            marker = new FaceLibraryMarker(FaceLibraryMod.Version, name, FaceLibraryMod.PackName(name)!);
        }
        var gamePath = FacePackPath(request.Race, request.Face, marker.Pack);
        AnimationResource? current = null;
        byte[]? currentBytes = null;
        if (!created)
        {
            var mapped = ModMetadata.AnimationMappedFiles(root, gamePath);
            if (mapped.Count > 1) throw new IOException($"{marker.Name} maps its faces for this face more than once.");
            if (mapped.Count == 1)
            {
                var notOn = $"{marker.Name} isn't on in this collection, or another mod plays its faces. Turn it on first.";
                try { (current, currentBytes) = await resources.ReadAsync(capture.CollectionId, gamePath, token); }
                catch (FileNotFoundException) { throw new IOException(notOn); }
                if (!string.Equals(current.ModDirectory, modDirectory, StringComparison.Ordinal) || !AnimationResources.CanReplace(current))
                    throw new IOException(notOn);
            }
        }

        // The faces, each read once per file as the collection gives it.
        var folder = $"chara/human/{request.Race}/animation/{request.Face}/";
        var reads = new Dictionary<string, (AnimationResource Resource, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var face in request.Faces)
        {
            var path = face.GamePath;
            token.ThrowIfCancellationRequested();
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) ||
                !AnimationDependencies.SafeGamePath(path))
                throw new InvalidDataException("Faces are copied only from this race's and face's own files.");
            // A new name, or an edit under one, can't hide a face the game has: the pickers would leave it out.
            if ((face.Built != null || face.Name != face.Motion) && request.GameNames?.Contains(face.Name) == true)
                throw new InvalidDataException("The game already has a face with that name.");
            if (face.Built == null && !reads.ContainsKey(path))
                reads[path] = await resources.ReadAsync(capture.CollectionId, path, token);
        }
        var resident = await ResidentMotionsAsync(capture.CollectionId, folder + "resident/face.pap", token);
        byte[] Bytes(FaceCopySource face) => face.Built?.Pap ?? reads[face.GamePath].Bytes;
        var files = request.Faces.Select(Bytes).Append(currentBytes).OfType<byte[]>().Distinct<byte[]>(ReferenceEqualityComparer.Instance).ToList();
        var prints = await framework.RunOnFrameworkThread(() =>
            files.ToDictionary(bytes => bytes, AnimationRuntime.InspectPap, (IEqualityComparer<byte[]>)ReferenceEqualityComparer.Instance));

        var copies = new List<FaceCopy>();
        foreach (var face in request.Faces)
        {
            var bytes = Bytes(face);
            var entries = new AnimationPap(bytes).Entries;
            var found = Enumerable.Range(0, entries.Length).Where(i => entries[i].Name == face.Motion && entries[i].Type is 17 or 18 or 19).ToArray();
            if (found.Length != 1) throw new InvalidDataException(found.Length == 0 ? $"{face.Motion} is not in that file." : $"That file holds {face.Motion} more than once.");
            var print = prints[bytes].GetValueOrDefault(entries[found[0]].Binding, "");
            if (print.Length == 0) throw new InvalidDataException($"{face.Motion} could not be read.");
            copies.Add(new FaceCopy(new FaceCopyEntry(face.Motion, found[0], print, bytes), face.Name, face.Built != null));
        }
        var held = new List<FaceCopyEntry>();
        if (currentBytes != null)
        {
            var entries = new AnimationPap(currentBytes).Entries;
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Type is 17 or 18 or 19 && entries[i].Name.Length > 0 && held.All(h => h.Name != entries[i].Name))
                    held.Add(new FaceCopyEntry(entries[i].Name, i,
                        prints[currentBytes].GetValueOrDefault(entries[i].Binding) is { Length: > 0 } print ? print : $"unread:{i}", currentBytes));
        }
        var plan = FaceLibraryCopy.Plan(held, copies, resident, request.Replace);
        if (plan.Build == null)
        {
            Status = plan.Asks.IsEmpty ? $"{marker.Name} has these faces already." : $"{marker.Name} has another face of that name.";
            return new FaceCopyOutcome(null, marker.Name, plan.Added, plan.Already, plan.Replaced, plan.Asks);
        }

        Status = "Building the library";
        var id = journalId ?? Guid.NewGuid();
        var mergeDirectory = Path.Combine(journals.DirectoryFor(id), "merge");
        Directory.CreateDirectory(mergeDirectory);
        var built = await FacePapBuilder.BuildAsync(framework, modelId, new AnimationPap(copies[0].Source.Pap).ModelType, plan.Build, mergeDirectory, token);
        if (!new AnimationPap(built).Entries.Select(e => e.Name).SequenceEqual(plan.Build.Select(b => b.Name)))
            throw new InvalidDataException("The library's faces did not come out as built.");

        var manifest = new AnimationDependencyManifest([.. reads.Values.Select(r => r.Resource),
                .. request.Faces.Select(f => f.Built?.Resource).OfType<AnimationResource>(), .. current is { } file ? [file] : Array.Empty<AnimationResource>()],
            ImmutableDictionary<string, byte[]>.Empty);
        // An edited face's save keeps no layers to open again: the library holds the face, not the session.
        var bake = new AnimationBakeRequest(id, capture, AnimationDestination.Library, marker.Name, false, request.Operation);
        var journal = await commits.PrepareAsync(bake, manifest, ImmutableDictionary<string, byte[]>.Empty, token,
            library: new AnimationCommitService.LibraryFile(gamePath, built, modDirectory, root, marker, created, current));
        recovery = recovery.Insert(0, journal);
        await commits.CommitAsync(journal, manifest, () => CheckActorAsync(capture, false), message =>
        {
            Status = message;
            if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
        }, token);
        await FaceLibraries.UpdateAsync(modDirectory, marker.Name, root, gamePath);
        journal.State = "Completed";
        journal.Message = request.Operation == AnimationOperation.EditTimeline ? $"Face saved into {marker.Name}." : $"Faces copied into {marker.Name}.";
        journals.Save(journal);
        Status = journal.Message;
        return new FaceCopyOutcome(id, marker.Name, plan.Added, plan.Already, plan.Replaced, []);
    }

    /// <summary>The motions a face always has loaded, from its resident face.pap as the collection gives it; none when it can't be read.</summary>
    private async Task<IReadOnlySet<string>> ResidentMotionsAsync(Guid collection, string gamePath, CancellationToken token)
    {
        try
        {
            var (_, bytes) = await resources.ReadAsync(collection, gamePath, token);
            return new AnimationPap(bytes).Entries.Select(e => e.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
