using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationCommitService(PenumbraService penumbra, AnimationResources resources,
    ModelBackupStore backups, AnimationJournalStore store)
{
    public async Task<AnimationEditJournal> PrepareAsync(AnimationBakeRequest request, AnimationDependencyManifest manifest,
        ImmutableDictionary<string, byte[]> outputs, CancellationToken token)
    {
        var journal = new AnimationEditJournal { Id = request.Id, Request = RecoveryRequest(request),
            PoseBefore = request.Operation == AnimationOperation.BakeOffsets ? request.Capture.Pose : null };
        var dir = store.DirectoryFor(request.Id);
        Directory.CreateDirectory(dir);
        if (request.Destination == AnimationDestination.NewMod)
        {
            if (!PenumbraService.IsSafeNewModName(request.ModName)) throw new InvalidDataException("Choose a valid, unused mod name.");
            var root = await penumbra.AnimationModRootAsync();
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
        foreach (var (gamePath, bytes) in files)
        {
            token.ThrowIfCancellationRequested();
            string mod, root, target, relative, before;
            if (request.Destination == AnimationDestination.NewMod)
            {
                mod = journal.ModDirectory; root = journal.ModRoot; relative = "files/" + gamePath;
                target = Path.GetFullPath(Path.Combine(root, relative)); before = "";
            }
            else
            {
                var source = manifest.Resources.Single(r => r.GamePath == gamePath);
                if (!AnimationResources.CanReplace(source)) throw new IOException($"{gamePath} has no verified writable Penumbra destination. Choose Create new mod.");
                mod = source.ModDirectory!; root = source.ModRoot!; relative = source.RelativePath!;
                target = source.ResolvedPath; before = source.Hash;
                if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new IOException($"{target} is read-only.");
            }
            TextureFiles.EnsureLocalPath(target);
            if (!PathRules.IsPathWithin(target, root)) throw new IOException("The output escapes its mod directory.");
            var staged = Path.Combine(dir, journal.Files.Count + ".staged");
            await File.WriteAllBytesAsync(staged, bytes, token);
            var after = AnimationPap.Hash(bytes);
            if (AnimationPap.Hash(await File.ReadAllBytesAsync(staged, token)) != after) throw new IOException("Staged output validation failed.");
            journal.Files.Add(new AnimationFileChange(gamePath, target, mod, root, relative, before, after, "", staged));
        }
        if (journal.Files.Select(f => f.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw new IOException("Several edited game paths share one source file. Use Create new mod to separate them.");
        store.Save(journal);
        return journal;
    }

    internal static AnimationBakeRequest RecoveryRequest(AnimationBakeRequest request)
    {
        AnimationClip Compact(AnimationClip clip) => clip.Resolution is { } resolution ? clip with
        { Resolution = resolution with { Candidates = resolution.Selected is { } selected ? [selected] : [] } } : clip;
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
        await penumbra.AnimationExportAsync(async () =>
        {
            await checkActor();
            await resources.CheckAsync(journal.Request.Capture.CollectionId, manifest.Resources, token);
            if (journal.Request.Destination == AnimationDestination.NewMod)
            {
                var metadata = AnimationMetadata.Applicable(
                    AnimationMetadata.Decode(await penumbra.AnimationMetadataAsync(journal.Request.Capture.CollectionId)),
                    journal.Files.Select(file => file.GamePath)).ToJsonString();
                if (metadata != manifest.ManipulationsJson) throw new IOException("Applicable collection metadata changed during baking. Refresh and retry.");
            }
            foreach (var file in journal.Files) ValidateTarget(journal, file);
            if (journal.Request.Destination == AnimationDestination.InPlace)
                foreach (var group in journal.Files.GroupBy(f => (f.ModDirectory, f.ModRoot)))
                    await penumbra.CheckAnimationModRootAsync(group.Key.ModDirectory, group.Key.ModRoot);
            // Dependency and metadata reads can span several ticks; recheck the live destination last.
            await checkActor();
            token.ThrowIfCancellationRequested();
            status("Committing validated animation files…");
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
                    if (file.BeforeHash.Length > 0)
                    {
                        RequireHash(file.Target, file.BeforeHash);
                        var backup = backups.Create(file.Target, file.ModDirectory, file.RelativePath);
                        RequireHash(backup, file.BeforeHash);
                        journal.Files[i] = file = file with { Backup = backup }; store.Save(journal);
                        var mapping = await penumbra.ResolveAnimationPathAsync(journal.Request.Capture.CollectionId, file.GamePath);
                        if (!PathRules.SamePhysicalPath(mapping, file.Target)) throw new IOException($"The mapping for {file.GamePath} changed during commit.");
                        RequireHash(file.Target, file.BeforeHash);
                    }
                    else if (File.Exists(file.Target)) throw new IOException($"Another operation created {file.Target}.");
                    RequireHash(file.Staged, file.AfterHash);
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                    AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(file.Staged));
                }
                if (journal.Request.Destination == AnimationDestination.NewMod)
                {
                    var metadata = CreateNewModMetadata(
                        journal.ModDirectory, journal.Files, manifest.ManipulationsJson);
                    AnimationJournalStore.WriteAtomic(
                        Path.Combine(journal.ModRoot, "meta.json"),
                        JsonSerializer.SerializeToUtf8Bytes(metadata));
                    journal.ModFileHashes = ModHashes(journal.ModRoot);
                }
                journal.State = "Committed"; store.Save(journal);
                await checkActor();
                status("Activating and verifying animation resources…");
                await penumbra.ActivateAnimationAsync(journal);
                await VerifyActivationAsync(journal.Files,
                    path => penumbra.ResolveAnimationPathAsync(journal.Request.Capture.CollectionId, path),
                    file => RequireHash(file.Target, file.AfterHash),
                    penumbra.RedrawAnimationAsync);
                if (journal.Request.Destination == AnimationDestination.NewMod) journal.ModFileHashes = ModHashes(journal.ModRoot);
                journal.State = "Activated"; store.Save(journal);
            }
            catch (Exception error)
            {
                journal.State = "RecoveryRequired"; journal.Message = error.Message; store.Save(journal);
                try { await UndoFilesCoreAsync(journal); }
                catch (Exception rollback) { journal.Message += " Rollback needs attention: " + rollback.Message; store.Save(journal); }
                throw new IOException(journal.Message, error);
            }
        }, token);
    }

    internal static async Task VerifyActivationAsync(IEnumerable<AnimationFileChange> files,
        Func<string, Task<string>> resolve, Action<AnimationFileChange> validate, Func<Task> redraw)
    {
        foreach (var file in files)
        {
            var resolved = await resolve(file.GamePath);
            if (!PathRules.SamePhysicalPath(resolved, file.Target))
                throw new IOException($"Activation failed: {file.GamePath} resolves to {resolved}, expected {file.Target}.");
            validate(file);
        }
        // Do not enqueue a native character rebuild until every mapping and file passed.
        await redraw();
    }

    internal static JsonObject CreateNewModMetadata(
        string modName, IEnumerable<AnimationFileChange> changes, string manipulationsJson)
    {
        var files = new JsonObject();
        foreach (var change in changes)
            files[change.GamePath] = change.RelativePath.Replace('\\', '/');
        var manipulations = JsonNode.Parse(manipulationsJson) as JsonArray
            ?? throw new InvalidDataException("Animation manipulations are not a JSON array.");
        return PenumbraService.CreateV4ModMetadata(
            modName,
            "XIV Instant Edit",
            "Animation offsets baked by XIV Instant Edit.",
            "1.0",
            new JsonObject
            {
                ["Files"] = files,
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = manipulations,
            });
    }

    public Task UndoFilesAsync(AnimationEditJournal journal) => penumbra.AnimationExportAsync(() => UndoFilesCoreAsync(journal), CancellationToken.None);
    private async Task UndoFilesCoreAsync(AnimationEditJournal journal)
    {
        if (journal.State == "Prepared")
        { journal.State = "FilesUndone"; journal.Message = "The prepared job had not changed any files."; store.Save(journal); return; }
        foreach (var file in journal.Files) ValidateTarget(journal, file);
        if (journal.Request.Destination == AnimationDestination.NewMod)
        {
            var marker = Path.Combine(journal.ModRoot, ".instant-edit-animation.json");
            if (!File.Exists(marker)) throw new IOException("The created mod's ownership marker is missing.");
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("Id").GetGuid() != journal.Id) throw new IOException("The mod belongs to another animation job.");
            foreach (var file in journal.Files.Where(f => File.Exists(f.Target))) RequireHash(file.Target, file.AfterHash);
            if (journal.ModFileHashes.Count > 0)
            {
                var current = ModHashes(journal.ModRoot);
                if (current.Count != journal.ModFileHashes.Count || journal.ModFileHashes.Any(p => current.GetValueOrDefault(p.Key) != p.Value))
                    throw new IOException("The created mod was changed after activation. Undo will not disable newer work.");
            }
            journal.State = "Undoing"; store.Save(journal);
            await penumbra.DisableAnimationModAsync(journal);
        }
        else
        {
            foreach (var group in journal.Files.GroupBy(f => (f.ModDirectory, f.ModRoot)))
                await penumbra.CheckAnimationModRootAsync(group.Key.ModDirectory, group.Key.ModRoot);
            // Preflight every backup and target before restoring any file. Restart can resume a partly completed undo.
            foreach (var file in journal.Files)
            {
                var hash = HashFile(file.Target);
                if (hash == file.BeforeHash) continue;
                if (hash != file.AfterHash) throw new IOException($"{file.Target} was edited after this job. Undo will not overwrite it.");
                RequireHash(ResolveBackup(file), file.BeforeHash);
            }
            journal.State = "Undoing"; store.Save(journal);
            foreach (var file in journal.Files)
            {
                if (HashFile(file.Target) == file.BeforeHash) continue;
                RequireHash(file.Target, file.AfterHash);
                AnimationJournalStore.WriteAtomic(file.Target, File.ReadAllBytes(ResolveBackup(file)));
            }
            await penumbra.ReloadAnimationSourcesAsync(journal.Files.Select(f => f.ModDirectory));
        }
        journal.State = "FilesUndone"; store.Save(journal);
    }
    private string ResolveBackup(AnimationFileChange file)
    {
        var target = backups.Describe(file.ModDirectory, file.RelativePath);
        return backups.Resolve(target.Id, Path.GetFileName(file.Backup));
    }
    private void ValidateTarget(AnimationEditJournal journal, AnimationFileChange file)
    {
        if (!PenumbraService.IsSafeModName(file.ModDirectory) || !AnimationDependencies.SafeGamePath(file.GamePath) ||
            !AnimationDependencies.SafeGamePath(file.RelativePath.Replace('\\', '/')) ||
            !string.Equals(Path.GetFullPath(Path.Combine(file.ModRoot, file.RelativePath)), Path.GetFullPath(file.Target), StringComparison.OrdinalIgnoreCase) ||
            !PathRules.IsPathWithin(file.Target, file.ModRoot)) throw new InvalidDataException("Invalid animation recovery target.");
        TextureFiles.EnsureLocalPath(file.Target);
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
