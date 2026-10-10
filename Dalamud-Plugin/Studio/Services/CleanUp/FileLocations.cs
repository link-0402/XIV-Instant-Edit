using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.CleanUp;

/// <summary>One redirection's file, moved to where Penumbra itself would keep it.</summary>
internal sealed record LocationMove(OptionRef Option, string GamePath, string FromRel, string ToRel);

/// <summary>The moves, and the files no option uses once they are done.</summary>
internal sealed record LocationPlan(IReadOnlyList<LocationMove> Moves, IReadOnlyList<string> Obsolete);

/// <summary>
/// Puts a mod's files where Penumbra keeps them: the default files at their game path, an option's
/// under "group/option/game path", with folder names made safe and unique. Files are copied, the
/// mappings changed, and the old files deleted once Penumbra has the new mappings; files that stay
/// locked are deleted later. Images, this plugin's files and anything under instant_edit stay where
/// they are. Mod Optimizer's normalization, rebuilt here. Dalamud-free.
/// </summary>
internal static class FileLocations
{
    public static LocationPlan Plan(PenumbraModMeta meta, string folder, IReadOnlySet<string> ledgerRelKeys)
    {
        var model = CheckModel.Build(meta, folder);
        var groupNames = new Dictionary<int, string>();
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionNames = new Dictionary<OptionRef, string>();
        var usedOptions = new Dictionary<int, HashSet<string>>();
        foreach (var group in meta.Groups)
            groupNames[group.Index] = Unique(PathNormalizer.SafeSegment(group.Name.Replace('/', '_').Replace('\\', '_')), usedGroups);

        var moves = new List<LocationMove>();
        foreach (var mapping in model.Mappings.Where(m => !m.Shadowed))
        {
            if (Protected(mapping.RelPath, ledgerRelKeys))
                continue;
            var gamePath = mapping.Key;
            string target;
            if (mapping.Container.IsDefault)
            {
                target = gamePath;
            }
            else
            {
                var group = groupNames.GetValueOrDefault(mapping.Container.GroupIndex, "Group");
                if (!optionNames.TryGetValue(mapping.Container.Option, out var option))
                {
                    if (!usedOptions.TryGetValue(mapping.Container.GroupIndex, out var used))
                        usedOptions[mapping.Container.GroupIndex] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var name = meta.OptionName(mapping.Container.Option);
                    option = optionNames[mapping.Container.Option] = Unique(PathNormalizer.SafeSegment(name.Replace('/', '_').Replace('\\', '_')), used);
                }
                target = $"{group}/{option}/{gamePath}";
            }
            if (!PathNormalizer.SameRel(mapping.RelPath, target))
                moves.Add(new LocationMove(mapping.Container.Option, gamePath, mapping.RelPath, target));
        }

        // What no option points at once the moves are done, in the mod's folders.
        var referenced = new HashSet<string>(model.Mappings.Select(m => m.RelPath), StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
            referenced.Add(move.ToRel);
        // Files every mapping of which moves away; these go wherever they are, at the mod's top level too.
        var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in moves.Select(m => m.FromRel).Distinct(StringComparer.OrdinalIgnoreCase))
            if (model.Mappings.All(m => !PathNormalizer.SameRel(m.RelPath, source) || moves.Any(mv => mv.Option == m.Container.Option && mv.GamePath == m.Key)))
            {
                referenced.Remove(source);
                released.Add(source.Replace('\\', '/'));
            }
        referenced.UnionWith(model.SkippedMappings.Select(s => s.RelPath));
        referenced.UnionWith(model.AuxiliaryPaths);

        var obsolete = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(folder, file).Replace('\\', '/');
            // Other files at the top level are the mod's own (readme, meta.json); only the subfolders are swept.
            if (!relPath.Contains('/') && !released.Contains(relPath) || referenced.Contains(relPath) || Protected(relPath, ledgerRelKeys))
                continue;
            var extension = Path.GetExtension(relPath);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            obsolete.Add(relPath);
        }
        return new LocationPlan(moves, obsolete);
    }

    /// <summary>Files moving would break: this plugin's own, and anything under instant_edit.</summary>
    private static bool Protected(string relPath, IReadOnlySet<string> ledgerRelKeys)
    {
        var key = PathNormalizer.RelKey(relPath);
        return ledgerRelKeys.Contains(key)
            || key.StartsWith("instant_edit/", StringComparison.Ordinal)
            || key.EndsWith(".instant-edit-tmp", StringComparison.Ordinal)
            || key.EndsWith(PenumbraModMeta.BackupSuffix, StringComparison.Ordinal);
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); ++i)
            candidate = $"{name}_{i}";
        return candidate;
    }

    /// <param name="onOwnerThread">Runs an action on the thread that owns <paramref name="project"/>; inline when null.</param>
    public static async Task<CleanUpResult> ApplyAsync(ModProject project, LocationPlan plan, ModBackupStore backups, string cacheRoot, IModReloader penumbra,
        StudioSettings settings, CancellationToken token, Func<Action, Task>? onOwnerThread = null)
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
        if (plan.Moves.Count == 0 && plan.Obsolete.Count == 0)
            return new CleanUpResult(0, 0, Array.Empty<string>(), 0, 0);

        var metaPath = Path.Combine(folder, PenumbraModMeta.FileName);
        var created = new List<string>();
        using var operation = backups.Begin(folder, ModBackupStore.IdOf(folder), $"Moved {plan.Moves.Count} files", cacheRoot);
        MetaFiles original;
        try
        {
            original = MetaFiles.Take(folder);
            operation.KeepMeta();
            foreach (var relPath in plan.Obsolete)
                operation.Keep(relPath);

            // Copy every file to its new place first; a target holding something else stops it all.
            foreach (var move in plan.Moves)
            {
                token.ThrowIfCancellationRequested();
                var source = ModPaths.Resolve(folder, move.FromRel);
                var target = ModPaths.Resolve(folder, move.ToRel);
                if (File.Exists(target))
                {
                    if (!SameBytes(source, target))
                        throw new IOException($"{move.ToRel} already holds a different file.");
                    continue;
                }
                operation.Keep(move.ToRel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".instant-edit-tmp";
                try
                {
                    File.Copy(source, temp, overwrite: true);
                    File.Move(temp, target);
                }
                catch
                {
                    DeleteCreated(new[] { temp });   // no half-written file is left in the mod
                    throw;
                }
                created.Add(target);
            }

            await OnOwner(() => project.EditStructure(meta =>
            {
                foreach (var move in plan.Moves)
                {
                    if (!meta.TryGetFile(move.Option, move.GamePath, out var current) || !PathNormalizer.SameRel(current, move.FromRel))
                        throw new InvalidOperationException($"{move.GamePath} changed since the files were listed. Look again.");
                    var key = meta.FilesOf(move.Option).Select(f => f.GamePath).First(k => PathNormalizer.SameGamePath(k, move.GamePath));
                    meta.SetFile(move.Option, key, current.Contains('\\') ? move.ToRel.Replace('/', '\\') : move.ToRel);
                }
            })).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            DeleteCreated(created);
            return new CleanUpResult(0, 0, new[] { $"Nothing was moved: {ex.Message}" }, 0, 0);
        }

        // meta.json is written: from here on it goes to the end, and what changed is in the backups whatever happens.
        var keep = true;
        try
        {
            if (plan.Moves.Count > 0 && (!penumbra.Reload(name) || !penumbra.IsPresent(name)))
            {
                original.PutBack(folder);
                await OnOwner(() => project.RefreshFromDisk(force: true)).ConfigureAwait(false);
                DeleteCreated(created);
                keep = false;   // the mod is as it was
                penumbra.Reload(name);
                return new CleanUpResult(0, 0, new[] { "Penumbra didn't take the moved files, so everything was put back." }, 0, 0);
            }

            // The list was made earlier: a file an option uses again since (after a restore, say) stays.
            var used = PendingCleanup.ReferencedBy(folder);
            int pending = 0;
            long freed = 0;
            foreach (var relPath in plan.Obsolete)
            {
                if (used is null || used.Contains(relPath))
                {
                    operation.Forget(relPath);
                    continue;
                }
                var path = ModPaths.Resolve(folder, relPath);
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (await FileRetry.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false))
                {
                    freed += size;
                    continue;
                }
                PendingCleanup.Add(settings, folder, relPath, "An old file was locked after its mapping moved.");
                operation.Pending(relPath);
                pending++;
            }
            FileRetry.DeleteEmptyDirectories(folder);
            return new CleanUpResult(plan.Moves.Count, 0, Array.Empty<string>(), pending, freed);
        }
        finally
        {
            if (keep)
                operation.Commit();
        }
    }

    private static void DeleteCreated(IEnumerable<string> created)
    {
        foreach (var path in created)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
