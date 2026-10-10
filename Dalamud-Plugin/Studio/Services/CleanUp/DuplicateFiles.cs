using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.CleanUp;

/// <summary>Files with the same content, one of which is kept; <see cref="Others"/> are mapped to it and deleted.</summary>
internal sealed record DuplicateSet(string Keeper, IReadOnlyList<string> Others, long BytesFreed);

/// <summary>
/// Files a mod carries more than once: the same size, the same SHA-256, the same bytes. Every
/// mapping of a copy is pointed at the one kept (the shortest path, or a file Instant Edit wrote),
/// and the copies are deleted. Files a ui/ game path uses are never touched. Mod Optimizer's
/// duplicate tools, rebuilt here. Dalamud-free.
/// </summary>
internal static class DuplicateFiles
{
    public static IReadOnlyList<DuplicateSet> Scan(PenumbraModMeta meta, string folder, IReadOnlySet<string> ledgerRelKeys, CancellationToken token)
    {
        var model = CheckModel.Build(meta, folder);
        var usable = model.Mappings.GroupBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(m => !m.Key.StartsWith("ui/", StringComparison.Ordinal)))
            .Select(group => group.First().RelPath)
            .ToList();

        var files = new List<(string RelPath, string Path, long Size)>();
        foreach (var relPath in usable)
        {
            try
            {
                var path = ModPaths.Resolve(folder, relPath);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                    files.Add((relPath, path, info.Length));
            }
            catch (ArgumentException) { }
        }

        var result = new List<DuplicateSet>();
        foreach (var sizeGroup in files.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var hashed = new List<(string Hash, string Path, string RelPath)>();
            foreach (var file in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hashed.Add((Convert.ToHexString(SHA256.HashData(stream)), file.Path, file.RelPath));
            }
            foreach (var hashGroup in hashed.GroupBy(h => h.Hash).Where(g => g.Count() > 1))
            {
                var pending = hashGroup.ToList();
                while (pending.Count > 1)
                {
                    var first = pending[0];
                    var equal = pending.Where(item => SameBytes(first.Path, item.Path)).ToList();
                    pending.RemoveAll(equal.Contains);
                    if (equal.Count < 2)
                        continue;
                    var ordered = equal.Select(item => item.RelPath)
                        .OrderBy(rel => ledgerRelKeys.Contains(PathNormalizer.RelKey(rel)) ? 0 : 1)
                        .ThenBy(rel => rel.Length).ThenBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    // Files this plugin wrote are never deleted; when several are equal, each keeps its own.
                    var others = ordered.Skip(1).Where(rel => !ledgerRelKeys.Contains(PathNormalizer.RelKey(rel))).ToArray();
                    if (others.Length > 0)
                        result.Add(new DuplicateSet(ordered[0], others, sizeGroup.Key * others.Length));
                }
            }
        }
        return result.OrderByDescending(set => set.BytesFreed).ToArray();
    }

    /// <param name="onOwnerThread">Runs an action on the thread that owns <paramref name="project"/>; inline when null.</param>
    public static async Task<CleanUpResult> ApplyAsync(ModProject project, IReadOnlyList<DuplicateSet> sets, ModBackupStore backups, string cacheRoot,
        IModReloader penumbra, StudioSettings settings, CancellationToken token, Func<Action, Task>? onOwnerThread = null)
    {
        Task OnOwner(Action action)
        {
            if (onOwnerThread is not null)
                return onOwnerThread(action);
            action();
            return Task.CompletedTask;
        }

        var folder = project.FolderPath;
        var name = project.FolderName;
        await PendingCleanup.RetryAsync(settings, _ => false, token, folder).ConfigureAwait(false);
        if (sets.Count == 0)
            return new CleanUpResult(0, 0, Array.Empty<string>(), 0, 0);

        // The sets must still be what the scan found: every copy used, none by a ui/ path, the same bytes.
        var model = CheckModel.Build(PenumbraModMeta.Load(folder), folder);
        var usages = model.Mappings.GroupBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var replacement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var set in sets)
            {
                var keeper = ModPaths.Resolve(folder, set.Keeper);
                if (!usages.TryGetValue(set.Keeper, out var keeperUses) || keeperUses.Any(m => m.Key.StartsWith("ui/", StringComparison.Ordinal)))
                    throw new IOException($"{set.Keeper} changed since the duplicates were found.");
                foreach (var other in set.Others)
                {
                    var path = ModPaths.Resolve(folder, other);
                    if (!usages.TryGetValue(other, out var uses) || uses.Any(m => m.Key.StartsWith("ui/", StringComparison.Ordinal)) || !File.Exists(path) || !SameBytes(keeper, path))
                        throw new IOException($"{other} changed since the duplicates were found.");
                    replacement[other] = set.Keeper;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return new CleanUpResult(0, 0, new[] { $"Nothing was changed: {ex.Message} Look again." }, 0, 0);
        }

        var metaPath = Path.Combine(folder, PenumbraModMeta.FileName);
        using var operation = backups.Begin(folder, project.Meta.Identifier ?? Guid.Empty, $"Removed {replacement.Count} duplicate files", cacheRoot);
        byte[] original;
        try
        {
            original = File.ReadAllBytes(metaPath);
            operation.Keep(PenumbraModMeta.FileName);
            foreach (var other in replacement.Keys)
                operation.Keep(other);
            await OnOwner(() => project.EditStructure(meta =>
            {
                foreach (var file in meta.AllFiles().ToArray())
                    if (replacement.TryGetValue(file.RelPath.Replace('\\', '/'), out var keeper))
                        meta.SetFile(file.Option, file.GamePath, file.RelPath.Contains('\\') ? keeper.Replace('/', '\\') : keeper);
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new CleanUpResult(0, 0, new[] { $"Nothing was changed: {ex.Message}" }, 0, 0);
        }

        // meta.json is written: from here on it goes to the end, and what changed is in the backups whatever happens.
        var keep = true;
        try
        {
            if (!penumbra.Reload(name) || !penumbra.IsPresent(name))
            {
                var temp = metaPath + ".instant-edit-tmp";
                File.WriteAllBytes(temp, original);
                File.Move(temp, metaPath, overwrite: true);
                keep = false;   // the mod is as it was
                await OnOwner(() => project.RefreshFromDisk(force: true)).ConfigureAwait(false);
                penumbra.Reload(name);
                return new CleanUpResult(0, 0, new[] { "Penumbra didn't take the new mappings, so meta.json was put back." }, 0, 0);
            }

            int pending = 0;
            long freed = 0;
            foreach (var other in replacement.Keys)
            {
                var path = ModPaths.Resolve(folder, other);
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (await FileRetry.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false))
                {
                    freed += size;
                    continue;
                }
                PendingCleanup.Add(settings, folder, other, "A duplicate file was locked after its mappings moved.");
                operation.Pending(other);
                pending++;
            }
            FileRetry.DeleteEmptyDirectories(folder);
            return new CleanUpResult(replacement.Count, 0, Array.Empty<string>(), pending, freed);
        }
        finally
        {
            if (keep)
                operation.Commit();
        }
    }

    private static bool SameBytes(string first, string second)
    {
        var a = new FileInfo(first);
        var b = new FileInfo(second);
        if (a.Length != b.Length)
            return false;
        using var left = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var right = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> lb = stackalloc byte[4096];
        Span<byte> rb = stackalloc byte[4096];
        while (true)
        {
            var lc = left.ReadAtLeast(lb, lb.Length, throwOnEndOfStream: false);
            var rc = right.ReadAtLeast(rb, rb.Length, throwOnEndOfStream: false);
            if (lc != rc || !lb[..lc].SequenceEqual(rb[..rc]))
                return false;
            if (lc < lb.Length)
                return true;
        }
    }
}
