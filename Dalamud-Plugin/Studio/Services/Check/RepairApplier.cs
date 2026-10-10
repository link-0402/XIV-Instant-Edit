using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Check;

internal sealed record ApplyResult(bool Ok, string Message, int Applied, int Deleted, int Pending);

/// <summary>
/// Applies a check's ticked repairs: meta.json edits in one go, through <see cref="ModProject.EditStructure"/>,
/// with meta.json and every file to be deleted backed up first. Penumbra reads the mod again; if it
/// drops the mod, meta.json is put back. Files are deleted only once Penumbra has the new mappings.
/// Dalamud-free; Penumbra comes in through <see cref="IModReloader"/>.
/// </summary>
internal static class RepairApplier
{
    public const string BusyReason = "Wait a moment, until every change is written into the mod.";
    public const string ChangedReason = "The mod changed since it was checked. Check it again.";

    /// <summary>
    /// Why the repairs can't be applied now, or null when they can. Drawn every frame, so it doesn't
    /// read meta.json: <paramref name="stale"/> is the session's watch (<see cref="CheckSession.IsStale"/>),
    /// and <see cref="ApplyAsync"/> compares the fingerprint itself.
    /// </summary>
    public static string? WhyNot(ModProject? project, ModCheckReport report, bool syncBusy, bool penumbraAvailable, bool stale)
    {
        if (project is null) return "The mod is closed.";
        if (syncBusy) return BusyReason;
        if (!penumbraAvailable) return "Penumbra is not available.";
        if (stale || Moved(project, report) || report.Fingerprint is null) return ChangedReason;
        return null;
    }

    private static bool Moved(ModProject project, ModCheckReport report)
        => !string.Equals(Path.GetFullPath(project.FolderPath).TrimEnd('\\', '/'), Path.GetFullPath(report.Folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool Changed(ModProject project, ModCheckReport report)
        => Moved(project, report) || report.Fingerprint is null || MetaFingerprint.Of(project.FolderPath) != report.Fingerprint;

    /// <param name="onOwnerThread">Runs an action on the thread that owns <paramref name="project"/> (the framework thread in game); inline when null.</param>
    public static async Task<ApplyResult> ApplyAsync(ModProject project, ModCheckReport report, RepairSelection selection, ModBackupStore backups,
        string cacheRoot, IModReloader penumbra, StudioSettings settings, CancellationToken token, Func<Action, Task>? onOwnerThread = null)
    {
        Task OnOwner(Action action)
        {
            if (onOwnerThread is not null)
                return onOwnerThread(action);
            action();
            return Task.CompletedTask;
        }

        if (token.IsCancellationRequested)
            return new ApplyResult(false, "Cancelled. Nothing was changed.", 0, 0, 0);
        if (Changed(project, report))
            return new ApplyResult(false, ChangedReason, 0, 0, 0);
        var repairs = selection.Effective();
        if (repairs.Count == 0)
            return new ApplyResult(true, "No repairs were ticked.", 0, 0, 0);

        var edits = repairs.SelectMany(repair => repair.EditsFor(selection.Choice(repair.Id))).Distinct().ToArray();
        var deletions = repairs.SelectMany(repair => repair.Deletions).ToArray();
        var released = selection.DeleteReleasedFiles
            ? edits.Select(edit => edit switch { RemoveFileEdit r => r.ExpectedRelPath, RetargetFileEdit r => r.OldRelPath, _ => null })
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var metaPath = Path.Combine(project.FolderPath, PenumbraModMeta.FileName);
        var folder = project.FolderPath;
        var name = project.FolderName;

        using var operation = backups.Begin(folder, project.Meta.Identifier ?? Guid.Empty,
            $"Applied {repairs.Count} fix{(repairs.Count == 1 ? string.Empty : "es")}", cacheRoot);
        byte[] original;
        try
        {
            original = File.ReadAllBytes(metaPath);
            operation.Keep(PenumbraModMeta.FileName);
            foreach (var relPath in deletions.Select(d => d.RelPath).Concat(released).Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(ModPaths.Resolve(project.FolderPath, relPath)))
                    operation.Keep(relPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ApplyResult(false, $"Nothing was changed: the backup couldn't be made ({ex.Message}).", 0, 0, 0);
        }

        var counts = new EditCounts();
        try
        {
            await OnOwner(() => project.EditStructure(meta =>
            {
                // Every edit is tried on a copy first, so a stale one stops the whole apply before anything changes.
                ApplyEdits(meta.Clone(), edits, new EditCounts());
                ApplyEdits(meta, edits, counts);
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ApplyResult(false, $"{ex.Message} Nothing was changed.", 0, 0, 0);
        }

        // meta.json is written: from here on the operation goes to the end (a cancel would leave it half done),
        // and whatever happens, what changed is in the backups.
        var keep = true;
        try
        {
            return await Finish().ConfigureAwait(false);
        }
        finally
        {
            if (keep)
                operation.Commit();
        }

        async Task<ApplyResult> Finish()
        {
            if (counts.Any)
            {
                if (!penumbra.Reload(name) || !penumbra.IsPresent(name))
                {
                    var temp = metaPath + ".instant-edit-tmp";
                    File.WriteAllBytes(temp, original);
                    File.Move(temp, metaPath, overwrite: true);
                    keep = false;   // the mod is as it was
                    await OnOwner(() => project.RefreshFromDisk(force: true)).ConfigureAwait(false);
                    penumbra.Reload(name);
                    return new ApplyResult(false, "Penumbra didn't take the repaired mod, so its meta.json was put back.", 0, 0, 0);
                }
            }

            // Only now that Penumbra has the new mappings, and only files nothing points at any more.
            var model = CheckModel.Build(PenumbraModMeta.Load(project.FolderPath), project.FolderPath);
            var referenced = new HashSet<string>(model.Mappings.Select(m => m.RelPath), StringComparer.OrdinalIgnoreCase);
            referenced.UnionWith(model.SkippedMappings.Select(s => s.RelPath));
            referenced.UnionWith(model.AuxiliaryPaths);
            var delete = new List<string>();
            foreach (var deletion in deletions)
            {
                var info = new FileInfo(ModPaths.Resolve(project.FolderPath, deletion.RelPath));
                if (referenced.Contains(deletion.RelPath) || !info.Exists)
                    continue;
                if (info.Length != deletion.Size || info.LastWriteTimeUtc != deletion.LastWriteUtc)
                    continue;   // changed since the check
                delete.Add(deletion.RelPath);
            }
            foreach (var relPath in released)
                if (!referenced.Contains(relPath) && File.Exists(ModPaths.Resolve(project.FolderPath, relPath)))
                    delete.Add(relPath);

            int deleted = 0, pending = 0;
            foreach (var relPath in delete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await FileRetry.DeleteAsync(ModPaths.Resolve(folder, relPath), CancellationToken.None).ConfigureAwait(false))
                {
                    deleted++;
                    continue;
                }
                CleanUp.PendingCleanup.Add(settings, folder, relPath, "A file a repair left unused was locked.");
                operation.Pending(relPath);
                pending++;
            }
            if (deleted > 0)
                FileRetry.DeleteEmptyDirectories(folder);

            var parts = new List<string>();
            if (counts.Added > 0) parts.Add($"{counts.Added} redirection{(counts.Added == 1 ? string.Empty : "s")} added");
            if (counts.Removed > 0) parts.Add($"{counts.Removed} removed");
            if (counts.Changed > 0) parts.Add($"{counts.Changed} changed");
            if (counts.AddedMeta > 0) parts.Add($"{counts.AddedMeta} metadata added");
            if (counts.RemovedMeta > 0) parts.Add($"{counts.RemovedMeta} metadata removed");
            var message = $"Applied {repairs.Count} fix{(repairs.Count == 1 ? string.Empty : "es")}";
            if (parts.Count > 0) message += $" ({string.Join(", ", parts)})";
            if (deleted > 0) message += $"; deleted {deleted} unused file{(deleted == 1 ? string.Empty : "s")}";
            if (pending > 0) message += $"; {pending} locked file{(pending == 1 ? " is" : "s are")} removed later";
            return new ApplyResult(true, message + ".", repairs.Count, deleted, pending);
        }
    }

    private sealed class EditCounts
    {
        public int Added, Removed, Changed, AddedMeta, RemovedMeta;
        public bool Any => Added + Removed + Changed + AddedMeta + RemovedMeta > 0;
    }

    private static void ApplyEdits(PenumbraModMeta meta, IReadOnlyList<RepairEdit> edits, EditCounts counts)
    {
        foreach (var edit in edits.OfType<RetargetFileEdit>())
        {
            var current = Expect(meta, edit.Option, edit.GamePath, edit.OldRelPath);
            meta.SetFile(edit.Option, KeyOf(meta, edit.Option, edit.GamePath), Styled(meta, edit.Option, edit.NewRelPath, current));
            counts.Changed++;
        }
        foreach (var edit in edits.OfType<RekeyFileEdit>())
        {
            var current = Expect(meta, edit.Option, edit.OldGamePath, null);
            if (meta.TryGetFile(edit.Option, edit.NewGamePath, out _))
                throw new InvalidOperationException($"{Label(meta, edit.Option)} already redirects {edit.NewGamePath}. Check the mod again.");
            meta.RemoveFile(edit.Option, edit.OldGamePath);
            meta.SetFile(edit.Option, PathNormalizer.GamePath(edit.NewGamePath), current);
            counts.Changed++;
        }
        foreach (var edit in edits.OfType<RemoveFileEdit>())
        {
            Expect(meta, edit.Option, edit.GamePath, edit.ExpectedRelPath);
            meta.RemoveFile(edit.Option, edit.GamePath);
            counts.Removed++;
        }
        foreach (var edit in edits.OfType<AddFileEdit>())
        {
            RequireOption(meta, edit.Option);
            if (meta.TryGetFile(edit.Option, edit.GamePath, out var existing))
            {
                if (Same(existing, edit.RelPath))
                    continue;
                throw new InvalidOperationException($"{Label(meta, edit.Option)} already redirects {edit.GamePath} to {existing}. Check the mod again.");
            }
            meta.SetFile(edit.Option, PathNormalizer.GamePath(edit.GamePath), Styled(meta, edit.Option, edit.RelPath, null));
            counts.Added++;
        }
        foreach (var edit in edits.OfType<RemoveManipulationEdit>())
        {
            RequireOption(meta, edit.Option);
            if (!meta.RemoveManipulation(edit.Option, edit.Identity))
                throw new InvalidOperationException($"{edit.Description} is no longer in {Label(meta, edit.Option)}. Check the mod again.");
            counts.RemovedMeta++;
        }
        foreach (var edit in edits.OfType<AddManipulationEdit>())
        {
            RequireOption(meta, edit.Option);
            var identity = PenumbraModMeta.IdentityOf(edit.Manipulation);
            if (meta.FindManipulation(edit.Option, identity) is { } existing && JToken.DeepEquals(existing, edit.Manipulation))
                continue;
            meta.UpsertManipulation(edit.Option, (JObject)edit.Manipulation.DeepClone());
            counts.AddedMeta++;
        }
    }

    private static void RequireOption(PenumbraModMeta meta, OptionRef option)
    {
        if (!meta.OptionExists(option))
            throw new InvalidOperationException($"The option {option} no longer exists. Check the mod again.");
    }

    /// <summary>The file a redirection points at, which must still be what the check saw.</summary>
    private static string Expect(PenumbraModMeta meta, OptionRef option, string gamePath, string? expectedRelPath)
    {
        RequireOption(meta, option);
        if (!meta.TryGetFile(option, gamePath, out var relPath))
            throw new InvalidOperationException($"{Label(meta, option)} no longer redirects {gamePath}. Check the mod again.");
        if (expectedRelPath is not null && !Same(relPath, expectedRelPath))
            throw new InvalidOperationException($"{gamePath} in {Label(meta, option)} changed since the check. Check the mod again.");
        return relPath;
    }

    /// <summary>The key a redirection is stored under, as it is spelled in meta.json.</summary>
    private static string KeyOf(PenumbraModMeta meta, OptionRef option, string gamePath)
        => meta.FilesOf(option).Select(f => f.GamePath).FirstOrDefault(key => PathNormalizer.SameGamePath(key, gamePath)) ?? gamePath;

    /// <summary>A path written the way the option already writes its paths: backslashes, as Penumbra does, when it uses them.</summary>
    private static string Styled(PenumbraModMeta meta, OptionRef option, string relPath, string? replacing)
    {
        var backslashes = replacing?.Contains('\\') ?? meta.FilesOf(option).Any(f => f.RelPath.Contains('\\'));
        return backslashes ? relPath.Replace('/', '\\') : relPath.Replace('\\', '/');
    }

    private static string Label(PenumbraModMeta meta, OptionRef option) => meta.OptionLabel(option);

    private static bool Same(string a, string b) => PathNormalizer.SameRel(a, b);
}
