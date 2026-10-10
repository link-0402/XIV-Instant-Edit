using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.CleanUp;

/// <summary>Files clean-up or repairs left behind because something held them, deleted once they are free.</summary>
internal static class PendingCleanup
{
    // The list is replaced, never changed in place, under this lock.
    private static readonly object Lock = new();

    /// <summary>Notes a file to delete later (once per mod and path), and saves the settings.</summary>
    public static void Add(StudioSettings settings, string modFolder, string relPath, string reason)
    {
        var normalized = relPath.Replace('\\', '/');
        bool listed;
        lock (Lock)
            listed = settings.PendingCleanup.Any(p => string.Equals(p.ModFolder, modFolder, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(p.RelPath, normalized, StringComparison.OrdinalIgnoreCase));
        if (listed)
            return;
        long? size = null;
        DateTime? lastWrite = null;
        try
        {
            var info = new FileInfo(ModPaths.Resolve(modFolder, normalized));
            if (info.Exists)
            {
                size = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        lock (Lock)
            settings.PendingCleanup = new List<PendingFile>(settings.PendingCleanup)
            {
                new PendingFile { ModFolder = modFolder, RelPath = normalized, Reason = reason, Since = DateTime.UtcNow, Size = size, LastWriteUtc = lastWrite },
            };
        settings.SaveSoon();
    }

    /// <summary>
    /// Tries the waiting files again (only one mod's, when <paramref name="onlyModFolder"/> is given);
    /// files of a mod that is being written right now wait. A file the mod's meta.json uses again (after
    /// a restore, say) or that changed since it was left (a reinstall) is kept and taken off the list.
    /// Returns how many were deleted.
    /// </summary>
    public static async Task<int> RetryAsync(StudioSettings settings, Func<string, bool> modBusy, CancellationToken token, string? onlyModFolder = null)
    {
        var cleaned = 0;
        var dropped = false;
        var references = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        var entries = settings.PendingCleanup.ToArray();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (onlyModFolder is not null && !string.Equals(entry.ModFolder, onlyModFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (modBusy(entry.ModFolder))
                continue;
            try
            {
                var path = ModPaths.Resolve(entry.ModFolder, entry.RelPath);
                if (Directory.Exists(entry.ModFolder) && File.Exists(path) && !StillUnused(entry, path, references))
                {
                    Remove(settings, entry);
                    dropped = true;
                    continue;
                }
                if (!Directory.Exists(entry.ModFolder) || await FileRetry.DeleteAsync(path, token, attempts: 2).ConfigureAwait(false))
                {
                    Remove(settings, entry);
                    cleaned++;
                }
            }
            catch (ArgumentException)
            {
                Remove(settings, entry);   // a path that leads out of the mod is never deleted
                dropped = true;
            }
        }
        if (cleaned > 0 || dropped)
            settings.SaveSoon();
        return cleaned;
    }

    private static void Remove(StudioSettings settings, PendingFile entry)
    {
        lock (Lock)
            settings.PendingCleanup = settings.PendingCleanup.Where(p => !ReferenceEquals(p, entry)).ToList();
    }

    /// <summary>Whether a waiting file is still the one that was left and still unused by the mod.</summary>
    private static bool StillUnused(PendingFile entry, string path, Dictionary<string, HashSet<string>?> references)
    {
        var info = new FileInfo(path);
        if (entry.Size is { } size && info.Length != size || entry.LastWriteUtc is { } written && info.LastWriteTimeUtc != written)
            return false;
        if (!references.TryGetValue(entry.ModFolder, out var used))
            references[entry.ModFolder] = used = ReferencedBy(entry.ModFolder);
        // A meta.json that can't be read says nothing is safe to delete.
        return used is not null && !used.Contains(entry.RelPath.Replace('\\', '/'));
    }

    /// <summary>Every file the mod's meta.json points at, or null when it can't be read.</summary>
    internal static HashSet<string>? ReferencedBy(string modFolder)
    {
        try
        {
            var model = CheckModel.Build(PenumbraModMeta.Load(modFolder), modFolder);
            var referenced = new HashSet<string>(model.Mappings.Select(m => m.RelPath.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            referenced.UnionWith(model.SkippedMappings.Select(s => s.RelPath.Replace('\\', '/')));
            referenced.UnionWith(model.AuxiliaryPaths.Select(p => p.Replace('\\', '/')));
            return referenced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }
}
