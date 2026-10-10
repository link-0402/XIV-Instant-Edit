using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Services.Backups;

namespace InstantEdit.Studio.Services.CleanUp;

/// <summary>Files clean-up or repairs left behind because something held them, deleted once they are free.</summary>
internal static class PendingCleanup
{
    /// <summary>Notes a file to delete later (once per mod and path), and saves the settings.</summary>
    public static void Add(StudioSettings settings, string modFolder, string relPath, string reason)
    {
        var normalized = relPath.Replace('\\', '/');
        if (settings.PendingCleanup.Any(p => string.Equals(p.ModFolder, modFolder, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(p.RelPath, normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        settings.PendingCleanup.Add(new PendingFile { ModFolder = modFolder, RelPath = normalized, Reason = reason, Since = DateTime.UtcNow });
        settings.Save();
    }

    /// <summary>
    /// Tries the waiting files again (only one mod's, when <paramref name="onlyModFolder"/> is given);
    /// files of a mod that is being written right now wait. Returns how many were deleted.
    /// </summary>
    public static async Task<int> RetryAsync(StudioSettings settings, Func<string, bool> modBusy, CancellationToken token, string? onlyModFolder = null)
    {
        var cleaned = 0;
        foreach (var entry in settings.PendingCleanup.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (onlyModFolder is not null && !string.Equals(entry.ModFolder, onlyModFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (modBusy(entry.ModFolder))
                continue;
            try
            {
                if (!Directory.Exists(entry.ModFolder) || await FileRetry.DeleteAsync(ModPaths.Resolve(entry.ModFolder, entry.RelPath), token, attempts: 2).ConfigureAwait(false))
                {
                    settings.PendingCleanup.Remove(entry);
                    cleaned++;
                }
            }
            catch (ArgumentException)
            {
                settings.PendingCleanup.Remove(entry);   // a path that leads out of the mod is never deleted
            }
        }
        if (cleaned > 0)
            settings.Save();
        return cleaned;
    }
}
