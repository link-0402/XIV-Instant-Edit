using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InstantEdit.Studio.Services.Backups;

/// <summary>Deleting files the game or another program may be holding: a few tries with growing pauses, each under <see cref="Project.ModFiles.Gate"/>.</summary>
internal static class FileRetry
{
    /// <summary>Deletes a file; false when it is still locked after the last try. A missing file counts as deleted.</summary>
    public static async Task<bool> DeleteAsync(string path, CancellationToken token, int attempts = 5)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                // Under the mod files' lock, like every other write into a mod.
                lock (Project.ModFiles.Gate)
                {
                    if (!File.Exists(path))
                        return true;
                    File.Delete(path);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i + 1 >= attempts)
                    return false;
                await Task.Delay(100 * (1 << i), token).ConfigureAwait(false);
            }
        }
        return !File.Exists(path);
    }

    /// <summary>Removes the empty folders under <paramref name="root"/>, deepest first.</summary>
    public static void DeleteEmptyDirectories(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
