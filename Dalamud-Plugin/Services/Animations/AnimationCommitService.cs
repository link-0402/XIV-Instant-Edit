using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationCommitService(AnimationModService animationMods, AnimationResources resources,
    ModelBackupStore backups, AnimationJournalStore store, ModelBackupStore? legacyBackups = null, Func<string?>? author = null)
{
    /// <summary>
    /// Stages the edited files, and the files the edit adds (<paramref name="added"/>, such as
    /// imported sounds, keyed by game path) next to the edited animation they belong to. In
    /// place, an added file lands in that animation's mod and every option mapping its owner
    /// maps it too: the file <c>Owners</c> names for it (an edited copy's original), else the
    /// animation. A copy <c>Copies</c> lists is mapped only where its original is the file it was
    /// made from, and the copies made from the original's other files go beside it at the same game path.
    /// <paramref name="manipulations"/> are attach overrides the edit adds (a placed prop's), for the edited animation
    /// <c>For</c>: a new mod carries them, and in place every option mapping that animation gets them.
    /// <c>Aliases</c> are added game paths that share one of the added <c>Files</c> (alias to that file's game path, a
    /// prop material's other version folders): journaled with that file's target, never staged, mapped beside it.
    /// <paramref name="library"/> is a face library file the save writes into an expression library mod other than its own.
    /// </summary>
    public async Task<AnimationEditJournal> PrepareAsync(AnimationBakeRequest request, AnimationDependencyManifest manifest,
        ImmutableDictionary<string, byte[]> outputs, CancellationToken token,
        (string For, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyDictionary<string, string>? Owners,
            IReadOnlyDictionary<string, AddedCopies>? Copies, IReadOnlyDictionary<string, string>? Aliases)? added = null,
        (string For, IReadOnlyList<Timeline.AtchManipulation> Atch)? manipulations = null, LibraryFile? library = null)
    {
        if (request.Destination == AnimationDestination.Library && (library == null || !outputs.IsEmpty || added != null || manipulations != null))
            throw new InvalidDataException("A library save writes only the library's file.");
        var journal = new AnimationEditJournal { Id = request.Id, Request = RecoveryRequest(request) };
        var dir = store.DirectoryFor(request.Id);
        Directory.CreateDirectory(dir);
        if (request.Destination == AnimationDestination.NewMod)
        {
            if (!ModPaths.IsSafeNewModName(request.ModName)) throw new InvalidDataException("Choose a valid, unused mod name.");
            var root = await animationMods.AnimationModRootAsync();
            TextureFiles.EnsureLocalPath(root);
            journal.ModDirectory = request.ModName;
            journal.ModRoot = Path.GetFullPath(Path.Combine(root, request.ModName));
            if (!PathRules.IsPathWithin(journal.ModRoot, root) || Directory.Exists(journal.ModRoot))
                throw new IOException("That mod directory already exists. Choose a new mod name.");
        }
        // The dependency manifest is a read/validation graph used while baking.
        // It is not a list of files to copy into the result mod: that would pull
        // in every transitively resolved PAP, TMB, effect, skeleton, and other
        // resource, including files supplied by unrelated mods. A new animation
        // mod must contain only the clips that were actually baked. In-place
        // edits already have the same shape because `outputs` contains only the
        // selected clip and optional startup clip.
        var files = request.Destination == AnimationDestination.NewMod
            ? SelectNewModFiles(manifest, outputs)
            : outputs;
        // The game and Penumbra cannot refresh an animation resource from an
        // unchanged physical path once it has been cached, so a timeline edit,
        // skeleton repair or bone exclusion applied in place must land at a fresh
        // path each time instead of overwriting the original file.
        var renamesOutput = request.Destination == AnimationDestination.InPlace &&
            request.Operation is AnimationOperation.RepairSkeleton or AnimationOperation.ExcludeBones or AnimationOperation.EditTimeline;
        foreach (var (gamePath, bytes) in files)
        {
            token.ThrowIfCancellationRequested();
            string mod, root, target, relative, before; string? renamedFrom = null;
            if (request.Destination == AnimationDestination.NewMod)
            {
                mod = journal.ModDirectory; root = journal.ModRoot; relative = "files/" + gamePath;
                target = Path.GetFullPath(Path.Combine(root, relative)); before = "";
            }
            else
            {
                var source = manifest.Resources.Single(r => r.GamePath == gamePath);
                if (!AnimationResources.CanReplace(source)) throw new IOException($"{gamePath} has no verified writable Penumbra destination. Choose Create new mod.");
                mod = source.ModDirectory!; root = source.ModRoot!; before = source.Hash;
                if ((File.GetAttributes(source.ResolvedPath) & FileAttributes.ReadOnly) != 0) throw new IOException($"{source.ResolvedPath} is read-only.");
                if (renamesOutput)
                {
                    renamedFrom = source.RelativePath!;
                    // A copy of an All races save moves to the copies' folder, so a shared file's other path gets a file of its own.
                    relative = IsRaceCopy(request, gamePath) ? RaceCopyRelative(gamePath) : AnimationFileRename.NextRelativePath(renamedFrom);
                    target = Path.GetFullPath(Path.Combine(root, relative));
                    if (File.Exists(target)) throw new IOException($"{target} already exists.");
                }
                else
                {
                    relative = source.RelativePath!;
                    target = source.ResolvedPath;
                }
            }
            TextureFiles.EnsureLocalPath(target);
            if (!PathRules.IsPathWithin(target, root)) throw new IOException("The output escapes its mod directory.");
            var staged = Path.Combine(dir, journal.Files.Count + ".staged");
            await File.WriteAllBytesAsync(staged, bytes, token);
            var after = AnimationPap.Hash(bytes);
            if (AnimationPap.Hash(await File.ReadAllBytesAsync(staged, token)) != after) throw new IOException("Staged output validation failed.");
            journal.Files.Add(new AnimationFileChange(gamePath, target, mod, root, relative, before, after, "", staged, renamedFrom));
        }
        if (added is { } extra)
        {
            // An instant startup is added beside its loop, which itself stays as it is.
            if (!files.ContainsKey(extra.For) && !(request.Destination == AnimationDestination.InPlace && files.IsEmpty &&
                    manifest.Resources.Any(r => r.GamePath == extra.For)))
                throw new InvalidDataException("Added files must belong to an edited animation.");
            foreach (var (gamePath, bytes) in extra.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!AddedGamePath(gamePath, request) || files.ContainsKey(gamePath))
                    throw new InvalidDataException($"{gamePath} can't be added to the mod.");
                string mod, root;
                var relative = AddedRelative(request, gamePath);
                if (request.Destination == AnimationDestination.NewMod)
                {
                    mod = journal.ModDirectory; root = journal.ModRoot;
                }
                else
                {
                    var source = manifest.Resources.Single(r => r.GamePath == extra.For);
                    mod = source.ModDirectory!; root = source.ModRoot!;
                }
                var owner = extra.Owners?.GetValueOrDefault(gamePath) ?? extra.For;
                if (!AnimationDependencies.SafeGamePath(owner) || (files.ContainsKey(owner) && owner != extra.For) || extra.Files.ContainsKey(owner))
                    throw new InvalidDataException($"{gamePath} can't be mapped beside {owner}.");
                var copies = request.Destination == AnimationDestination.InPlace ? extra.Copies?.GetValueOrDefault(gamePath) : null;
                // A race copy plays where the edited file does now: that file's rename is committed first, and the mappings follow it.
                var raceCopy = IsRaceCopy(request, gamePath);
                var ownerFile = raceCopy ? journal.Files.FirstOrDefault(file => file.AddedFor == null && file.GamePath == owner)?.RelativePath
                    ?? throw new InvalidDataException($"{gamePath} can't be mapped beside {owner}.") : copies?.From;
                await StageAddedAsync(gamePath, bytes, mod, root, relative, owner, ownerFile, raceCopy);
                // The copies made from the original's other files share its game path, each in a file of its own.
                var number = 1;
                foreach (var (from, copy) in copies?.Others ?? ImmutableDictionary<string, byte[]>.Empty)
                    await StageAddedAsync(gamePath, copy, mod, root,
                        $"{Path.ChangeExtension(relative, null)}_{++number}{Path.GetExtension(relative)}", owner, from);
            }
            // The game paths that share an added file go after it, naming its target and staged copy.
            journal.Files.AddRange(Aliased(journal.Files, extra.Aliases, request));
        }
        if (SharesTarget(journal.Files))
            throw new IOException("Several edited game paths share one source file. Use Create new mod to separate them.");
        if (manipulations is { Atch.Count: > 0 } atch)
        {
            if (!files.ContainsKey(atch.For)) throw new InvalidDataException("Manipulations must belong to an edited animation.");
            // A new mod can't take the source mod's override away: it carries the game's entry over it instead.
            var removes = request.Destination == AnimationDestination.InPlace;
            var edit = new AnimationManipulationEdit
            {
                For = atch.For,
                Added = [.. atch.Atch.Where(m => !(removes && m.Remove)).Select(Studio.Services.ModMetaWriter.AtchJson)],
                Removed = [.. atch.Atch.Where(m => removes && m.Remove).Select(Studio.Services.ModMetaWriter.AtchJson)],
            };
            if (request.Destination == AnimationDestination.NewMod)
                (edit.ModDirectory, edit.ModRoot) = (journal.ModDirectory, journal.ModRoot);
            else
            {
                var source = manifest.Resources.Single(r => r.GamePath == atch.For);
                if (!AnimationResources.CanReplace(source)) throw new IOException($"{atch.For} has no verified writable Penumbra destination. Choose Create new mod.");
                (edit.ModDirectory, edit.ModRoot) = (source.ModDirectory!, source.ModRoot!);
            }
            journal.Manipulations = edit;
        }
        if (library != null) journal.Library = await StageLibraryAsync(journal, library, dir, token);
        store.Save(journal);
        return journal;

        // Stages a file the edit adds, mapped beside its owner: only where the owner is ownerFile, when there is one.
        async Task StageAddedAsync(string gamePath, byte[] bytes, string mod, string root, string relative, string owner, string? ownerFile, bool raceCopy = false)
        {
            var target = Path.GetFullPath(Path.Combine(root, relative));
            TextureFiles.EnsureLocalPath(target);
            if (!PathRules.IsPathWithin(target, root)) throw new IOException("The output escapes its mod directory.");
            if (File.Exists(target)) throw new IOException($"{target} already exists.");
            var staged = Path.Combine(dir, journal.Files.Count + ".staged");
            await File.WriteAllBytesAsync(staged, bytes, token);
            var after = AnimationPap.Hash(bytes);
            if (AnimationPap.Hash(await File.ReadAllBytesAsync(staged, token)) != after) throw new IOException("Staged output validation failed.");
            journal.Files.Add(new AnimationFileChange(gamePath, target, mod, root, relative, "", after, "", staged,
                AddedFor: owner, AddedForFile: ownerFile, RaceCopy: raceCopy));
        }
    }

    /// <summary>
    /// An added copy whose original the edited mod maps to several files (one per option): <paramref name="From"/>
    /// is the file it was made from, <paramref name="Others"/> the copies made the same way from the others, by file.
    /// </summary>
    internal sealed record AddedCopies(string From, IReadOnlyDictionary<string, byte[]> Others);

    /// <summary>Where an in-place edit puts the files it adds, inside the edited animation's mod.</summary>
    internal const string AddedFolder = "instant_edit/";

    /// <summary>Whether <paramref name="gamePath"/> is a copy <paramref name="request"/>'s All races save writes.</summary>
    internal static bool IsRaceCopy(AnimationBakeRequest? request, string gamePath) =>
        request is { Operation: AnimationOperation.EditTimeline, Timeline: { } edit } && edit.RaceCopies.Contains(gamePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>A race copy's file in its mod, under a fresh name each save: the game keeps a cached file by its physical path.</summary>
    internal static string RaceCopyRelative(string gamePath) => AnimationFileRename.NextRelativePath(RaceCopies.Folder + gamePath);

    /// <summary>Where a file <paramref name="request"/> adds goes in its mod.</summary>
    internal static string AddedRelative(AnimationBakeRequest request, string gamePath) =>
        IsRaceCopy(request, gamePath) ? RaceCopyRelative(gamePath)
        : request.Destination == AnimationDestination.NewMod ? "files/" + gamePath : AddedFolder + gamePath;

    /// <summary>
    /// The kinds of file an edit may add: sounds, effects and what effects use, a prop's private copy (the model,
    /// materials and textures of a weapon body in b9000..b9999, which the game doesn't ship), for a timeline edit of
    /// <paramref name="request"/>'s startup that startup's own animation (an instant startup beside its loop), and
    /// the copies its All races save lists.
    /// </summary>
    internal static bool AddedGamePath(string gamePath, AnimationBakeRequest? request = null) => AnimationDependencies.SafeGamePath(gamePath) &&
        (Path.GetExtension(gamePath).ToLowerInvariant() is ".scd" or ".avfx" or ".atex" or ".tmb" ||
         Path.GetExtension(gamePath).ToLowerInvariant() is ".mdl" or ".mtrl" or ".tex" && PrivatePropBody.IsMatch(gamePath) ||
         request is { Operation: AnimationOperation.EditTimeline, Timeline.Startup: true, Capture.Startup: { } startup } &&
         string.Equals(gamePath, startup.GamePath, StringComparison.OrdinalIgnoreCase) &&
         gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) ||
         IsRaceCopy(request, gamePath) && gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase));

    /// <summary>A weapon body in b9000..b9999, which the game doesn't ship: where a prop's private copy goes (the Preview's rule too).</summary>
    internal static readonly System.Text.RegularExpressions.Regex PrivatePropBody = new(@"^chara/weapon/w\d{4}/obj/body/b9\d{3}/",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The journal entries of <paramref name="aliases"/> (alias game path to the added game path it shares): each is its
    /// file's entry at the alias's game path, so it names the same target, relative path and staged copy. Refused unless
    /// the alias may be added, is no path the edit writes already, and shares exactly one added file of the same kind.
    /// </summary>
    internal static List<AnimationFileChange> Aliased(IReadOnlyList<AnimationFileChange> files, IReadOnlyDictionary<string, string>? aliases,
        AnimationBakeRequest? request = null)
    {
        var result = new List<AnimationFileChange>();
        foreach (var (alias, target) in (aliases ?? ImmutableDictionary<string, string>.Empty).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!AddedGamePath(alias, request) || files.Any(f => ModPaths.SameGamePath(f.GamePath, alias)) ||
                !string.Equals(Path.GetExtension(alias), Path.GetExtension(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{alias} can't be added to the mod.");
            var shared = files.Where(f => f.AddedFor != null && f.AliasOf == null && string.Equals(f.GamePath, target, StringComparison.Ordinal)).ToArray();
            if (shared.Length != 1) throw new InvalidDataException($"{alias} must share one file the edit adds.");
            result.Add(shared[0] with { GamePath = alias, AliasOf = target });
        }
        return result;
    }

    /// <summary>Whether two entries that write a file write the same one; aliases write none.</summary>
    internal static bool SharesTarget(IEnumerable<AnimationFileChange> files)
    {
        var written = files.Where(f => f.AliasOf == null).Select(f => f.Target).ToArray();
        return written.Distinct(StringComparer.OrdinalIgnoreCase).Count() != written.Length;
    }

    /// <summary>The game paths that map to <paramref name="file"/>'s target: its own, then its aliases'.</summary>
    private static List<(string GamePath, string RelativePath)> Mappings(AnimationFileChange file, IReadOnlyList<AnimationFileChange> files) =>
    [
        (file.GamePath, file.RelativePath),
        .. files.Where(f => f.AliasOf is { } of && string.Equals(of, file.GamePath, StringComparison.Ordinal) &&
                string.Equals(f.Target, file.Target, StringComparison.OrdinalIgnoreCase))
            .Select(f => (f.GamePath, f.RelativePath)),
    ];

    /// <summary>
    /// Commits a file the edit added: writes it from its staged copy and, in place, maps it and the aliases that
    /// share it wherever its owner is, in one write of meta.json. An alias is written and mapped with its file.
    /// </summary>
    internal static void WriteAdded(AnimationFileChange file, IReadOnlyList<AnimationFileChange> files, bool inPlace)
    {
        if (file.AddedFor is not { } owner || file.BeforeHash.Length > 0) throw new InvalidDataException("Invalid animation recovery target.");
        if (file.AliasOf != null) return;
        if (File.Exists(file.Target)) throw new IOException($"Another operation created {file.Target}.");
        RequireHash(file.Staged, file.AfterHash);
        Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
        AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(file.Staged));
        // An added file plays wherever its owner is: every option that maps the
        // animation, or the original of a copy, maps it too. A new mod's metadata maps it.
        if (inPlace)
            ModMetadata.AddAnimationResourceMappings(file.ModRoot, owner, Mappings(file, files), file.AddedForFile);
    }

    /// <summary>Before an in-place undo writes anything: an added file still there must be what this edit wrote. An alias has no file of its own.</summary>
    internal static void CheckAddedUndo(AnimationFileChange file)
    {
        // Gone already means undone; otherwise only remove what this edit wrote.
        if (file.AliasOf == null && File.Exists(file.Target)) RequireHash(file.Target, file.AfterHash);
    }

    /// <summary>
    /// Undoes an added file in place: the mappings of its aliases and its own go in one write of meta.json, then the file.
    /// An alias's mapping goes with its file's, and it deletes nothing. True when a file was deleted.
    /// </summary>
    internal static bool UndoAdded(AnimationFileChange file, IReadOnlyList<AnimationFileChange> files)
    {
        if (file.AliasOf != null) return false;
        ModMetadata.RemoveAnimationResourceMappings(file.ModRoot, Mappings(file, files));
        if (!File.Exists(file.Target)) return false;
        File.Delete(file.Target);
        return true;
    }

    internal static AnimationBakeRequest RecoveryRequest(AnimationBakeRequest request)
    {
        AnimationClip Compact(AnimationClip clip) => clip.Resolution is { } resolution ? clip with
        {
            Resolution = resolution with
            {
                Candidates = resolution.Selected is { } selected ? [selected] : [],
                // A rebuild for another race retargets onto that race's standard, never the clip's own.
                Standards = request.RepairModel != null ? [] : [.. resolution.Standards.Where(s => s.Standard == request.RepairTarget)],
                ActorStandards = request.RepairModel != null ? [.. resolution.ActorStandards.Where(s => s.Standard == request.RepairTarget)] : [],
            },
        } : clip;
        return request with { Capture = request.Capture with { Clip = Compact(request.Capture.Clip),
            Startup = request.Capture.Startup == null ? null : Compact(request.Capture.Startup) } };
    }

    internal static ImmutableDictionary<string, byte[]> SelectNewModFiles(
        AnimationDependencyManifest manifest, ImmutableDictionary<string, byte[]> outputs)
    {
        if (outputs.Keys.Any(path => !manifest.Files.ContainsKey(path)))
            throw new InvalidDataException("An edited animation is missing from the captured dependency manifest.");
        return outputs;
    }

    public async Task CommitAsync(AnimationEditJournal journal, AnimationDependencyManifest manifest,
        Func<Task> checkActor, Action<string> status, CancellationToken token)
    {
        try { await CommitCoreAsync(journal, manifest, checkActor, status, token); }
        // Refused or cancelled before any file was written: nothing was saved and nothing needs undoing.
        catch (Exception error) when (journal.State == "Prepared")
        {
            journal.State = "FilesUndone";
            journal.Message = error is OperationCanceledException ? "Cancelled before saving." : error.Message;
            store.Save(journal);
            throw;
        }
    }

    private async Task CommitCoreAsync(AnimationEditJournal journal, AnimationDependencyManifest manifest,
        Func<Task> checkActor, Action<string> status, CancellationToken token)
    {
        await animationMods.AnimationExportAsync(async () =>
        {
            await checkActor();
            await resources.CheckAsync(journal.Request.Capture.CollectionId, manifest.Resources, token);
            if (journal.Request.Destination == AnimationDestination.NewMod)
            {
                var metadata = AnimationMetadata.Applicable(
                    AnimationMetadata.Decode(await animationMods.AnimationMetadataAsync(journal.Request.Capture.CollectionId, journal.Request.Capture.ActorIndex)),
                    journal.Files.Where(file => file.AddedFor == null).Select(file => file.GamePath)).ToJsonString();
                if (metadata != manifest.ManipulationsJson) throw new IOException("Applicable collection metadata changed during baking. Refresh and retry.");
            }
            foreach (var file in journal.Files) ValidateTarget(journal, file);
            if (journal.Request.Destination == AnimationDestination.InPlace)
                foreach (var group in journal.Files.GroupBy(f => (f.ModDirectory, f.ModRoot)))
                    await animationMods.CheckAnimationModRootAsync(group.Key.ModDirectory, group.Key.ModRoot);
            if (journal.Library is { } library) await CheckLibraryAsync(journal, library);
            // Dependency and metadata reads can span several ticks; recheck the live destination last.
            await checkActor();
            token.ThrowIfCancellationRequested();
            status("Committing validated animation files");
            journal.State = "Committing"; store.Save(journal);
            // Cancellation ends here: complete or roll back this durable transaction.
            try
            {
                if (journal.Request.Destination == AnimationDestination.NewMod)
                {
                    if (Directory.Exists(journal.ModRoot)) throw new IOException("The new mod directory was created by another operation.");
                    Directory.CreateDirectory(journal.ModRoot);
                    AnimationJournalStore.WriteAtomic(Path.Combine(journal.ModRoot, ".instant-edit-animation.json"), JsonSerializer.SerializeToUtf8Bytes(new { Id = journal.Id }));
                }
                for (var i = 0; i < journal.Files.Count; i++)
                {
                    var file = journal.Files[i]; ValidateTarget(journal, file);
                    if (file.AddedFor != null)
                    {
                        WriteAdded(file, journal.Files, journal.Request.Destination == AnimationDestination.InPlace);
                        continue;
                    }
                    // A renamed output writes to a fresh path, so the file carrying the
                    // pre-edit bytes to verify and back up is the old path, not the target.
                    var renamed = file.RenamedFromRelativePath is { Length: > 0 };
                    var preEditTarget = renamed ? OldTargetPath(file) : file.Target;
                    if (file.BeforeHash.Length > 0)
                    {
                        RequireHash(preEditTarget, file.BeforeHash);
                        var backup = backups.Create(preEditTarget, file.ModDirectory, renamed ? file.RenamedFromRelativePath! : file.RelativePath);
                        RequireHash(backup, file.BeforeHash);
                        journal.Files[i] = file = file with { Backup = backup }; store.Save(journal);
                        var mapping = await animationMods.ResolveAnimationPathAsync(journal.Request.Capture.CollectionId, file.GamePath);
                        if (!PathRules.SamePhysicalPath(mapping, preEditTarget)) throw new IOException($"The mapping for {file.GamePath} changed during commit.");
                        RequireHash(preEditTarget, file.BeforeHash);
                        if (renamed && File.Exists(file.Target)) throw new IOException($"Another operation created {file.Target}.");
                    }
                    else if (File.Exists(file.Target)) throw new IOException($"Another operation created {file.Target}.");
                    RequireHash(file.Staged, file.AfterHash);
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                    AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(file.Staged));
                    if (renamed) FinishRename(file);
                }
                // Attach overrides play wherever the animation does; the journal learns what each option held first.
                if (journal.Manipulations is { } atch && atch.Added.Count + atch.Removed.Count > 0 &&
                    journal.Request.Destination == AnimationDestination.InPlace)
                    ModMetadata.AddAnimationManipulations(atch.ModRoot, atch.For, atch.Added, changes =>
                    {
                        atch.Changes = [.. changes];
                        store.Save(journal);
                    }, atch.Removed);
                if (journal.Request.Destination == AnimationDestination.NewMod)
                {
                    var metadata = CreateNewModMetadata(journal.ModDirectory, journal.Files, manifest.ManipulationsJson,
                        journal.Request.Operation == AnimationOperation.EditTimeline
                            ? "Animation timeline edited by XIV Instant Edit."
                            : "Animation offsets baked by XIV Instant Edit.", author?.Invoke(), journal.Manipulations?.Added);
                    AnimationJournalStore.WriteAtomic(
                        Path.Combine(journal.ModRoot, "meta.json"),
                        JsonSerializer.SerializeToUtf8Bytes(metadata));
                    journal.ModFileHashes = ModHashes(journal.ModRoot);
                }
                // The library goes after the save's own files, so a stop before it leaves nothing of it to undo.
                if (journal.Library != null) WriteLibrary(journal, author?.Invoke());
                journal.State = "Committed"; store.Save(journal);
                await checkActor();
                status("Activating and verifying animation resources");
                await animationMods.ActivateAnimationAsync(journal);
                await VerifyActivationAsync(journal.Library is { } written ? [.. journal.Files, written.File] : journal.Files,
                    path => animationMods.ResolveAnimationPathAsync(journal.Request.Capture.CollectionId, path),
                    file => RequireHash(file.Target, file.AfterHash),
                    () => animationMods.RedrawAnimationAsync(journal.Request.Capture.ActorIndex));
                if (journal.Request.Destination == AnimationDestination.NewMod) journal.ModFileHashes = ModHashes(journal.ModRoot);
                journal.State = "Activated"; store.Save(journal);
            }
            catch (Exception error)
            {
                journal.State = "RecoveryRequired"; journal.Message = error.Message;
                // The rollback is what puts back the files already changed, so it runs even when the record cannot be written
                // (the same full disk or locked config folder that failed the commit can fail this too).
                TrySave(journal);
                try { await UndoFilesCoreAsync(journal); }
                catch (Exception rollback) { journal.Message += " Rollback needs attention: " + rollback.Message; TrySave(journal); }
                throw new IOException(journal.Message, error);
            }
        }, token);
    }

    /// <summary> Saves the record on a failure path, where a second failure must not hide the first one. </summary>
    private void TrySave(AnimationEditJournal journal)
    {
        try { store.Save(journal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            journal.Message += $" (The recovery record could not be saved: {e.Message})";
        }
    }

    internal static async Task VerifyActivationAsync(IEnumerable<AnimationFileChange> files,
        Func<string, Task<string>> resolve, Action<AnimationFileChange> validate, Func<Task> redraw)
    {
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            // An alias shares a file checked by its own entry, and is mapped in the same write of meta.json as every other
            // alias of that file: one of them resolving shows the write landed, without a framework tick for each of 255.
            if (file.AliasOf is { } of)
            {
                if (checkedAliases.Add(of))
                {
                    var aliased = await resolve(file.GamePath);
                    if (!PathRules.SamePhysicalPath(aliased, file.Target))
                        throw new IOException($"Activation failed: {file.GamePath} resolves to {aliased}, expected {file.Target}.");
                }
                continue;
            }
            // Copies of one added file made from each of its original's files share its game path, which resolves
            // to the first: the one made from the file the collection resolves now. The others only need to be intact.
            // A race copy another mod outranks is Penumbra's decision, not a failed save: only its file is checked.
            if (!file.RaceCopy && (file.AddedForFile == null || checkedPaths.Add(file.GamePath)))
            {
                var resolved = await resolve(file.GamePath);
                if (!PathRules.SamePhysicalPath(resolved, file.Target))
                    throw new IOException($"Activation failed: {file.GamePath} resolves to {resolved}, expected {file.Target}.");
            }
            validate(file);
        }
        // Do not enqueue a native character rebuild until every mapping and file passed.
        await redraw();
    }

    internal static JsonObject CreateNewModMetadata(
        string modName, IEnumerable<AnimationFileChange> changes, string manipulationsJson,
        string description = "Animation offsets baked by XIV Instant Edit.", string? author = null, IEnumerable<string>? added = null)
    {
        var files = new JsonObject();
        foreach (var change in changes)
            files[change.GamePath] = change.RelativePath.Replace('\\', '/');
        var manipulations = JsonNode.Parse(manipulationsJson) as JsonArray
            ?? throw new InvalidDataException("Animation manipulations are not a JSON array.");
        // The edit's own (a placed prop's attach overrides) follow the collection's, replacing one for the same place.
        foreach (var manipulation in added ?? [])
        {
            var item = JsonNode.Parse(manipulation) as JsonObject ?? throw new InvalidDataException("A manipulation is not a JSON object.");
            if (ModMetadata.IndexOfManipulation(manipulations, item) is >= 0 and var at)
                manipulations.RemoveAt(at);
            manipulations.Add(item);
        }
        return ModMetadata.CreateV4ModMetadata(
            modName,
            ModMetadata.AuthorOrDefault(author),
            description,
            "1.0",
            new JsonObject
            {
                ["Files"] = files,
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = manipulations,
            });
    }

    /// <summary>
    /// After a renamed output is written: repoints the owning mod's own Files mapping at the new path so Penumbra resolves here
    /// from now on (ActivateAnimationAsync reloads the mod afterward to pick this up), and deletes the old file once no path maps it.
    /// </summary>
    internal static void FinishRename(AnimationFileChange file)
    {
        var stillReferenced = ModMetadata.RenameAnimationFileMapping(file.ModRoot, file.GamePath, file.RenamedFromRelativePath!, file.RelativePath);
        if (!stillReferenced)
            try { File.Delete(OldTargetPath(file)); } catch (IOException) { }
    }

    /// <summary>Undoes a renamed output: the old file back from <paramref name="backup"/> when it isn't there as it was, the mapping back, the new file gone.</summary>
    internal static void UndoRename(AnimationFileChange file, Func<string> backup)
    {
        if (!File.Exists(file.Target)) return;
        var oldTarget = OldTargetPath(file);
        if (!File.Exists(oldTarget) || HashFile(oldTarget) != file.BeforeHash)
            AnimationJournalStore.WriteAtomic(oldTarget, File.ReadAllBytes(backup()));
        try { ModMetadata.RenameAnimationFileMapping(file.ModRoot, file.GamePath, file.RelativePath, file.RenamedFromRelativePath!); }
        catch (InvalidDataException) { /* Already points back at the original path. */ }
        try { File.Delete(file.Target); } catch (IOException) { }
    }

    public Task UndoFilesAsync(AnimationEditJournal journal) => animationMods.AnimationExportAsync(() => UndoFilesCoreAsync(journal), CancellationToken.None);

    /// <summary>
    /// Whether nothing of <paramref name="journal"/>'s files is left to put back. FilesUndone: they were put back, or never
    /// written (a save refused, cancelled or rolled back), so an Undo of a "Not saved" row only finishes the record. A new
    /// mod whose commit stopped before its metadata, with no folder (<paramref name="modFolderExists"/>): it was never made.
    /// A finished new mod whose folder is gone was moved, and its Undo is refused (the ownership marker is missing).
    /// </summary>
    internal static bool NoFilesLeft(AnimationEditJournal journal, bool modFolderExists) =>
        journal.State == "FilesUndone" ||
        journal.Request.Destination == AnimationDestination.NewMod && !modFolderExists &&
        journal.State is "Committing" or "RecoveryRequired" && journal.ModFileHashes.Count == 0;

    private async Task UndoFilesCoreAsync(AnimationEditJournal journal)
    {
        if (journal.State == "Prepared")
        { journal.State = "FilesUndone"; journal.Message = "The prepared job had not changed any files."; store.Save(journal); return; }
        foreach (var file in journal.Files) ValidateTarget(journal, file);
        if (journal.Library is { } library) ValidateLibrary(journal, library);
        if (NoFilesLeft(journal, Directory.Exists(journal.ModRoot)))
        {
            journal.State = "FilesUndone"; store.Save(journal);
            return;
        }
        // The library goes back before the save's own files; a later change that refuses the undo refuses it here, before any write.
        var libraryUndo = journal.Library != null ? CheckLibraryUndo(journal) : LibraryUndo.Nothing;
        if (libraryUndo == LibraryUndo.Restore) await animationMods.CheckAnimationModRootAsync(journal.Library!.ModDirectory, journal.Library.ModRoot);
        Task UndoLibrary() => journal.Library == null ? Task.CompletedTask : UndoLibraryAsync(journal, libraryUndo,
            () => animationMods.DisableAnimationModAsync(journal.Library!.ModDirectory, journal.Library.ModRoot,
                journal.Request.Capture.CollectionId, journal.Request.Capture.ActorIndex),
            () => animationMods.ReloadAnimationSourcesAsync([journal.Library!.ModDirectory], journal.Request.Capture.ActorIndex));
        if (journal.Request.Destination == AnimationDestination.NewMod)
        {
            var marker = Path.Combine(journal.ModRoot, ".instant-edit-animation.json");
            if (!File.Exists(marker)) throw new IOException("The created mod's ownership marker is missing.");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("Id").GetGuid() != journal.Id) throw new IOException("The mod belongs to another animation job.");
            // An alias shares its file, which its file's own entry checks.
            foreach (var file in journal.Files.Where(f => f.AliasOf == null && File.Exists(f.Target))) RequireHash(file.Target, file.AfterHash);
            if (journal.ModFileHashes.Count > 0)
            {
                var current = ModHashes(journal.ModRoot);
                if (current.Count != journal.ModFileHashes.Count || journal.ModFileHashes.Any(p => current.GetValueOrDefault(p.Key) != p.Value))
                    throw new IOException("The created mod was changed after activation. Undo will not disable newer work.");
            }
            journal.State = "Undoing"; store.Save(journal);
            await UndoLibrary();
            await animationMods.DisableAnimationModAsync(journal, journal.Request.Capture.ActorIndex);
        }
        else
        {
            foreach (var group in journal.Files.GroupBy(f => (f.ModDirectory, f.ModRoot)))
                await animationMods.CheckAnimationModRootAsync(group.Key.ModDirectory, group.Key.ModRoot);
            // Preflight every backup and target before restoring any file. Restart can resume a partly completed undo.
            var atch = journal.Manipulations is { Changes.Count: > 0 } written ? written : null;
            if (atch != null)
            {
                if (!ModPaths.IsSafeModName(atch.ModDirectory)) throw new InvalidDataException("Invalid animation recovery target.");
                await animationMods.CheckAnimationModRootAsync(atch.ModDirectory, atch.ModRoot);
                ModMetadata.CheckAnimationManipulations(atch.ModRoot, atch.Changes);
            }
            foreach (var file in journal.Files)
            {
                if (file.AddedFor is not null)
                {
                    CheckAddedUndo(file);
                    continue;
                }
                if (file.RenamedFromRelativePath is { Length: > 0 })
                {
                    // The rename's own output file no longer existing is what "already
                    // undone" looks like here: the old path keeps its pre-edit bytes
                    // for as long as the rename is in effect, so it cannot tell the
                    // two states apart on its own.
                    if (!File.Exists(file.Target))
                    {
                        // While the edit is still in effect, its output can only be gone because
                        // something replaced it, such as a later in-place edit of the same clip.
                        if (journal.State is "Activated" or "ClearingOffsets" or "Completed")
                            throw new IOException($"{file.Target} no longer exists, most likely because a later edit replaced it. Undo that edit first.");
                        continue;
                    }
                    RequireHash(file.Target, file.AfterHash);
                    RequireHash(ResolveBackup(file), file.BeforeHash);
                    // Undo writes the backup to the original path; never over a file changed since.
                    var oldTarget = OldTargetPath(file);
                    if (File.Exists(oldTarget) && HashFile(oldTarget) != file.BeforeHash)
                        throw new IOException($"{oldTarget} was changed after this job. Undo will not overwrite it.");
                    continue;
                }
                var hash = HashFile(file.Target);
                if (hash == file.BeforeHash) continue;
                if (hash != file.AfterHash) throw new IOException($"{file.Target} was edited after this job. Undo will not overwrite it.");
                RequireHash(ResolveBackup(file), file.BeforeHash);
            }
            journal.State = "Undoing"; store.Save(journal);
            await UndoLibrary();
            if (atch != null) ModMetadata.RemoveAnimationManipulations(atch.ModRoot, atch.Changes);
            foreach (var file in journal.Files)
            {
                if (file.AddedFor is not null)
                {
                    UndoAdded(file, journal.Files);
                    continue;
                }
                if (file.RenamedFromRelativePath is { Length: > 0 })
                {
                    UndoRename(file, () => ResolveBackup(file));
                    continue;
                }
                if (HashFile(file.Target) == file.BeforeHash) continue;
                RequireHash(file.Target, file.AfterHash);
                AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(ResolveBackup(file)));
            }
            await animationMods.ReloadAnimationSourcesAsync(journal.Files.Select(f => f.ModDirectory), journal.Request.Capture.ActorIndex);
        }
        journal.State = "FilesUndone"; store.Save(journal);
    }
    /// <summary>
    /// A face library PAP a save writes into an expression library mod other than its own: its game path
    /// (chara/human/&lt;race&gt;/animation/&lt;face&gt;/nonresident/&lt;pack&gt;.pap), its bytes, the library's folder name, root and
    /// marker, whether the save makes the library, and for an existing one the file the collection resolves the game path to
    /// (null when it has none for that face folder yet).
    /// </summary>
    internal sealed record LibraryFile(string GamePath, byte[] Bytes, string ModDirectory, string ModRoot,
        Timeline.FaceLibraryMarker Marker, bool Created, AnimationResource? Current);

    /// <summary>What undoing a save's library file does, decided before anything is written.</summary>
    internal enum LibraryUndo { Nothing, Restore, Disable }

    /// <summary>Stages the library file in the job folder, at a fresh path in the library (the game keeps an unchanged path's file).</summary>
    private async Task<AnimationLibraryEdit> StageLibraryAsync(AnimationEditJournal journal, LibraryFile library, string dir, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var marker = library.Marker;
        if (!Timeline.FaceLibraryIndex.TryParse(library.GamePath, out _, out _, out var pack) || pack != marker.Pack ||
            marker.Version != Timeline.FaceLibraryMod.Version || string.IsNullOrWhiteSpace(marker.Name) ||
            Timeline.FaceLibraryMod.PackName(marker.Pack) != marker.Pack)
            throw new InvalidDataException($"{library.GamePath} isn't a file of that library.");
        if (!(library.Created ? ModPaths.IsSafeNewModName(library.ModDirectory) : ModPaths.IsSafeModName(library.ModDirectory)))
            throw new InvalidDataException("Invalid library name.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.ModRoot));
        TextureFiles.EnsureLocalPath(root);
        if (journal.Files.Select(f => (f.ModDirectory, f.ModRoot)).Append((journal.ModDirectory, journal.ModRoot)).Where(m => m.ModRoot.Length > 0)
                .Any(m => string.Equals(m.ModDirectory, library.ModDirectory, StringComparison.OrdinalIgnoreCase) ||
                          PathRules.IsPathWithin(root, m.ModRoot) || PathRules.IsPathWithin(m.ModRoot, root)))
            throw new InvalidDataException("The faces go into a library of their own, not the saved mod.");
        if (library.Created)
        {
            if (library.Current != null || !string.Equals(Path.GetFileName(root), library.ModDirectory, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid library name.");
            if (Directory.Exists(root)) throw new IOException("That library already exists. Choose a new name.");
        }
        else
        {
            if (!Timeline.FaceLibraryMod.TryRead(root, out var found) || found != marker)
                throw new IOException("That mod is no longer the expression library it was.");
            if (library.Current is { } current)
            {
                if (!AnimationResources.CanReplace(current) || !ModPaths.SameGamePath(current.GamePath, library.GamePath) ||
                    !string.Equals(current.ModDirectory, library.ModDirectory, StringComparison.Ordinal) || !PathRules.SamePhysicalPath(current.ModRoot, root))
                    throw new IOException($"{library.GamePath} has no writable file in the library.");
                if ((File.GetAttributes(current.ResolvedPath) & FileAttributes.ReadOnly) != 0) throw new IOException($"{current.ResolvedPath} is read-only.");
            }
        }
        var renamedFrom = library.Current?.RelativePath;
        var relative = AnimationFileRename.NextRelativePath(renamedFrom ?? "files/" + library.GamePath);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        TextureFiles.EnsureLocalPath(target);
        if (!PathRules.IsPathWithin(target, root)) throw new IOException("The output escapes its mod directory.");
        if (File.Exists(target)) throw new IOException($"{target} already exists.");
        var staged = Path.Combine(dir, "library.staged");
        await File.WriteAllBytesAsync(staged, library.Bytes, token);
        var after = AnimationPap.Hash(library.Bytes);
        if (AnimationPap.Hash(await File.ReadAllBytesAsync(staged, token)) != after) throw new IOException("Staged output validation failed.");
        var edit = new AnimationLibraryEdit
        {
            ModDirectory = library.ModDirectory, ModRoot = root, Created = library.Created, Name = marker.Name, Pack = marker.Pack,
            File = new AnimationFileChange(library.GamePath, target, library.ModDirectory, root, relative, library.Current?.Hash ?? "", after, "",
                staged, renamedFrom),
        };
        ValidateLibrary(journal, edit);
        return edit;
    }

    /// <summary>The library entry's rules on top of <see cref="ValidateTarget"/>: its file is the library's PAP for its pack, in its own mod.</summary>
    internal void ValidateLibrary(AnimationEditJournal journal, AnimationLibraryEdit library)
    {
        var file = library.File;
        if (file is null || file.AddedFor != null || file.AliasOf != null || file.AddedForFile != null ||
            !string.Equals(file.ModDirectory, library.ModDirectory, StringComparison.Ordinal) || !PathRules.SamePhysicalPath(file.ModRoot, library.ModRoot) ||
            !Timeline.FaceLibraryIndex.TryParse(file.GamePath, out _, out _, out var pack) || pack != library.Pack ||
            Timeline.FaceLibraryMod.PackName(library.Pack) != library.Pack || string.IsNullOrWhiteSpace(library.Name) ||
            (file.BeforeHash.Length > 0) != (file.RenamedFromRelativePath is { Length: > 0 }) ||
            library.Created && file.BeforeHash.Length > 0)
            throw new InvalidDataException("Invalid face library target.");
        ValidateTarget(journal, file);
    }

    /// <summary>The commit's checks of the library through Penumbra, before any file is written; then <see cref="CheckLibraryBefore"/>.</summary>
    private async Task CheckLibraryAsync(AnimationEditJournal journal, AnimationLibraryEdit library)
    {
        ValidateLibrary(journal, library);
        if (library.Created)
        {
            var root = await animationMods.AnimationModRootAsync();
            TextureFiles.EnsureLocalPath(root);
            if (!PathRules.SamePhysicalPath(library.ModRoot, Path.Combine(root, library.ModDirectory)))
                throw new IOException("A new library goes in Penumbra's mod folder.");
        }
        else
        {
            await animationMods.CheckAnimationModRootAsync(library.ModDirectory, library.ModRoot);
            if (library.File.RenamedFromRelativePath is { Length: > 0 })
            {
                var mapping = await animationMods.ResolveAnimationPathAsync(journal.Request.Capture.CollectionId, library.File.GamePath);
                if (!PathRules.SamePhysicalPath(mapping, OldTargetPath(library.File)))
                    throw new IOException($"The mapping for {library.File.GamePath} changed. Refresh and retry.");
            }
        }
        CheckLibraryBefore(library);
    }

    /// <summary>
    /// Before the library file is written: a new library's folder isn't there yet; an existing library is still the same
    /// one, and its file is still the one the save was built from, or it still maps no file at that game path.
    /// </summary>
    internal static void CheckLibraryBefore(AnimationLibraryEdit library)
    {
        var file = library.File;
        if (library.Created)
        {
            if (Directory.Exists(library.ModRoot)) throw new IOException("The new library's folder was made by another operation.");
            return;
        }
        if (!Timeline.FaceLibraryMod.TryRead(library.ModRoot, out var marker) || marker.Name != library.Name || marker.Pack != library.Pack)
            throw new IOException("That mod is no longer the expression library it was.");
        if (file.RenamedFromRelativePath is { Length: > 0 } old)
        {
            if (!ModMetadata.AnimationMappedFiles(library.ModRoot, file.GamePath).Any(mapped => SameModFile(mapped, old)))
                throw new IOException($"The library no longer maps {file.GamePath} to {old}. Refresh and retry.");
            RequireHash(OldTargetPath(file), file.BeforeHash);
        }
        else if (ModMetadata.ModMapsGamePath(library.ModRoot, file.GamePath))
            throw new IOException($"The library maps {file.GamePath} already. Refresh and retry.");
        if (File.Exists(file.Target)) throw new IOException($"Another operation created {file.Target}.");
    }

    /// <summary>
    /// Writes the library file, after the save's own files. An existing library's file is backed up, and the journal
    /// records the backup, before the new file lands at its fresh path and the library maps it there. A new library gets
    /// its folder, the job's ownership marker, the file, meta.json and last its library marker, so a half-made one is never listed.
    /// </summary>
    internal void WriteLibrary(AnimationEditJournal journal, string? author)
    {
        var library = journal.Library ?? throw new InvalidDataException("The save writes no library.");
        ValidateLibrary(journal, library);
        CheckLibraryBefore(library);
        var file = library.File;
        var renamed = file.RenamedFromRelativePath is { Length: > 0 };
        if (library.Created)
        {
            Directory.CreateDirectory(library.ModRoot);
            AnimationJournalStore.WriteAtomic(Path.Combine(library.ModRoot, ".instant-edit-animation.json"), JsonSerializer.SerializeToUtf8Bytes(new { Id = journal.Id }));
        }
        else if (renamed)
        {
            var backup = backups.Create(OldTargetPath(file), file.ModDirectory, file.RenamedFromRelativePath!);
            RequireHash(backup, file.BeforeHash);
            library.File = file = file with { Backup = backup }; store.Save(journal);
            RequireHash(OldTargetPath(file), file.BeforeHash);
        }
        RequireHash(file.Staged, file.AfterHash);
        Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
        AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(file.Staged));
        if (library.Created)
        {
            var meta = ModMetadata.CreateV4ModMetadata(library.Name, ModMetadata.AuthorOrDefault(author), "Expression library made by XIV Instant Edit.",
                "1.0", new JsonObject
                {
                    ["Files"] = new JsonObject { [file.GamePath] = file.RelativePath },
                    ["FileSwaps"] = new JsonObject(),
                    ["Manipulations"] = new JsonArray(),
                });
            AnimationJournalStore.WriteAtomic(Path.Combine(library.ModRoot, "meta.json"), JsonSerializer.SerializeToUtf8Bytes(meta));
            AnimationJournalStore.WriteAtomic(Path.Combine(library.ModRoot, Timeline.FaceLibraryMod.MarkerFile), System.Text.Encoding.UTF8.GetBytes(
                Timeline.FaceLibraryMod.Serialize(new Timeline.FaceLibraryMarker(Timeline.FaceLibraryMod.Version, library.Name, library.Pack))));
        }
        else if (renamed)
        {
            // The old file stays while another game path maps it; the backup brings it back on undo otherwise.
            if (!ModMetadata.RenameAnimationFileMapping(file.ModRoot, file.GamePath, file.RenamedFromRelativePath!, file.RelativePath))
                try { File.Delete(OldTargetPath(file)); } catch (IOException) { }
        }
        else ModMetadata.AddDefaultFileMapping(file.ModRoot, file.GamePath, file.RelativePath);
    }

    /// <summary>
    /// Before an undo writes anything: what putting the library file back takes. Still what the save wrote, or never
    /// written: restore the old file and mapping, or disable a library the save made. Changed by a later save since:
    /// the undo is refused, as that save built on this one's faces.
    /// </summary>
    internal LibraryUndo CheckLibraryUndo(AnimationEditJournal journal)
    {
        var library = journal.Library ?? throw new InvalidDataException("The save wrote no library.");
        ValidateLibrary(journal, library);
        var file = library.File;
        if (library.Created)
        {
            if (!Directory.Exists(library.ModRoot)) return LibraryUndo.Nothing;
            var owner = Path.Combine(library.ModRoot, ".instant-edit-animation.json");
            if (!File.Exists(owner))
            {
                // Stopped right after making the folder: nothing in it to put back, and the save's own files still go back.
                if (!Directory.EnumerateFileSystemEntries(library.ModRoot).Any()) return LibraryUndo.Nothing;
                throw new IOException("The created library's ownership marker is missing.");
            }
            using (var json = JsonDocument.Parse(File.ReadAllText(owner)))
                if (json.RootElement.GetProperty("Id").GetGuid() != journal.Id) throw new IOException("The library belongs to another animation job.");
            // Stopped before its meta.json: never listed by Penumbra, and disabling it changes nothing.
            if (!File.Exists(Path.Combine(library.ModRoot, "meta.json"))) return LibraryUndo.Disable;
        }
        else if (!Timeline.FaceLibraryMod.TryRead(library.ModRoot, out var marker) || marker.Name != library.Name || marker.Pack != library.Pack)
            return Changed();
        IReadOnlyList<string> mapped;
        try { mapped = ModMetadata.AnimationMappedFiles(library.ModRoot, file.GamePath); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return Changed(); }
        var renamedFrom = file.RenamedFromRelativePath is { Length: > 0 } from ? from : null;
        if (mapped.Any(m => SameModFile(m, file.RelativePath)))
        {
            // Mapped here but no longer the bytes this save wrote: something rewrote the file since.
            if (!File.Exists(file.Target) || HashFile(file.Target) != file.AfterHash) return Changed();
        }
        else if (renamedFrom != null ? !mapped.Any(m => SameModFile(m, renamedFrom)) : mapped.Count > 0)
            return Changed();
        else if (!File.Exists(file.Target))
            return library.Created ? LibraryUndo.Disable : LibraryUndo.Nothing;   // never written, or put back already
        else RequireHash(file.Target, file.AfterHash);   // written but not mapped yet
        if (library.Created) return LibraryUndo.Disable;
        if (renamedFrom != null)
        {
            // Undo writes the backup to the original path; never over a file changed since.
            var oldTarget = OldTargetPath(file);
            if (File.Exists(oldTarget) && HashFile(oldTarget) != file.BeforeHash)
                throw new IOException($"{oldTarget} was changed after this job. Undo will not overwrite it.");
            if (!File.Exists(oldTarget)) RequireHash(ResolveBackup(file), file.BeforeHash);
        }
        return LibraryUndo.Restore;

        static LibraryUndo Changed() => throw new IOException("A later save changed the library. Undo that save first.");
    }

    /// <summary>
    /// Puts the library file back as <see cref="CheckLibraryUndo"/> decided: the old file and mapping, then the new file
    /// goes; a library the save made is only disabled (<paramref name="disable"/>).
    /// Running it again after a stop changes nothing more.
    /// </summary>
    internal async Task UndoLibraryAsync(AnimationEditJournal journal, LibraryUndo step, Func<Task> disable, Func<Task> reload)
    {
        var library = journal.Library ?? throw new InvalidDataException("The save wrote no library.");
        var file = library.File;
        switch (step)
        {
            case LibraryUndo.Disable:
                await disable();
                break;
            case LibraryUndo.Restore:
                if (file.RenamedFromRelativePath is { Length: > 0 } old)
                {
                    var oldTarget = OldTargetPath(file);
                    if (!File.Exists(oldTarget) || HashFile(oldTarget) != file.BeforeHash)
                        AnimationJournalStore.WriteAtomic(oldTarget, File.ReadAllBytes(ResolveBackup(file)));
                    try { ModMetadata.RenameAnimationFileMapping(file.ModRoot, file.GamePath, file.RelativePath, old); }
                    catch (InvalidDataException) { /* Already points back at the original path. */ }
                }
                else ModMetadata.RemoveAnimationResourceMapping(file.ModRoot, file.GamePath, file.RelativePath);
                if (File.Exists(file.Target) && HashFile(file.Target) == file.AfterHash) File.Delete(file.Target);
                await reload();
                break;
        }
    }

    private static bool SameModFile(string left, string right) =>
        string.Equals(left.Replace('\\', '/').Trim().TrimStart('/'), right.Replace('\\', '/').Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private string ResolveBackup(AnimationFileChange file)
    {
        // A renamed file's backup was taken from - and is described by - the
        // original path, not the fresh one this edit wrote to.
        var relative = file.RenamedFromRelativePath is { Length: > 0 } renamedFrom ? renamedFrom : file.RelativePath;
        var target = backups.Describe(file.ModDirectory, relative);
        try { return backups.Resolve(target.Id, Path.GetFileName(file.Backup)); }
        catch (FileNotFoundException) when (legacyBackups is not null)
        {
            // Edits made before backups moved out of the cache keep theirs in the shared store.
            return legacyBackups.Resolve(target.Id, Path.GetFileName(file.Backup));
        }
    }
    /// <summary>The physical path of the original file a rename replaced, still inside its mod root.</summary>
    private static string OldTargetPath(AnimationFileChange file)
    {
        var old = file.RenamedFromRelativePath ?? throw new InvalidDataException("This file change was not renamed.");
        var path = Path.GetFullPath(Path.Combine(file.ModRoot, old));
        if (!PathRules.IsPathWithin(path, file.ModRoot)) throw new InvalidDataException("Invalid animation recovery target.");
        return path;
    }
    internal void ValidateTarget(AnimationEditJournal journal, AnimationFileChange file)
    {
        var safeRelative = file.AddedFor is null
            ? AnimationResources.SafeModRelativePath(file.RelativePath, file.GamePath) && file.AliasOf == null
            : AddedGamePath(file.GamePath, journal.Request) && AnimationDependencies.SafeGamePath(file.AddedFor) &&
              ModPaths.IsSafeGameResourcePath(file.RelativePath.Replace('\\', '/'), Path.GetExtension(file.GamePath));
        // An alias names the file of one added entry of this journal, in the same mod, for the same owner.
        if (file.AliasOf is { } of && journal.Files.Count(f => f.AliasOf == null && f.AddedFor != null &&
                string.Equals(f.GamePath, of, StringComparison.Ordinal) && string.Equals(f.Target, file.Target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.RelativePath, file.RelativePath, StringComparison.Ordinal) && f.ModDirectory == file.ModDirectory &&
                string.Equals(f.ModRoot, file.ModRoot, StringComparison.OrdinalIgnoreCase) && f.AddedFor == file.AddedFor &&
                f.AddedForFile == file.AddedForFile) != 1)
            throw new InvalidDataException("Invalid animation recovery target.");
        if (!ModPaths.IsSafeModName(file.ModDirectory) || !AnimationDependencies.SafeGamePath(file.GamePath) ||
            !safeRelative ||
            !string.Equals(Path.GetFullPath(Path.Combine(file.ModRoot, file.RelativePath)), Path.GetFullPath(file.Target), StringComparison.OrdinalIgnoreCase) ||
            !PathRules.IsPathWithin(file.Target, file.ModRoot)) throw new InvalidDataException("Invalid animation recovery target.");
        TextureFiles.EnsureLocalPath(file.Target);
        if (file.RenamedFromRelativePath is { Length: > 0 } renamedFrom)
        {
            if (!AnimationResources.SafeModRelativePath(renamedFrom, file.GamePath)) throw new InvalidDataException("Invalid animation recovery source.");
            TextureFiles.EnsureLocalPath(OldTargetPath(file));
        }
        if (!PathRules.IsPathWithin(file.Staged, store.DirectoryFor(journal.Id))) throw new InvalidDataException("Invalid animation staging path.");
        TextureFiles.EnsureLocalPath(file.Staged);
    }
    private static string HashFile(string path) { TextureFiles.EnsureLocalPath(path); return AnimationPap.Hash(File.ReadAllBytes(path)); }
    private static Dictionary<string, string> ModHashes(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            TextureFiles.EnsureLocalPath(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (result.Count >= 8192) throw new IOException("The created mod contains too many files to verify recovery.");
                result.Add(Path.GetRelativePath(root, file), HashFile(file));
            }
            foreach (var child in Directory.EnumerateDirectories(directory)) Visit(child);
        }
        Visit(root); return result;
    }
    internal static void RequireHash(string path, string hash)
    {
        if (HashFile(path) != hash) throw new IOException($"{path} changed. The operation was stopped to preserve newer work.");
    }
}
