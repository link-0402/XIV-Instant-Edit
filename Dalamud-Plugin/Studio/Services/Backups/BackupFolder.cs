using System;
using System.IO;

namespace InstantEdit.Studio.Services.Backups;

/// <summary>The cache folder backups go into, made sure of on the worker an operation runs on. Dalamud-free.</summary>
internal static class BackupFolder
{
    /// <summary>The cache folder, or null with what stops it being used (missing drive, a link, a folder it doesn't own).</summary>
    public static string? TryResolve(Func<string> cacheRoot, out string problem)
    {
        try
        {
            problem = string.Empty;
            return cacheRoot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"Nothing was changed: the backup folder couldn't be used ({ex.Message.TrimEnd('.')}).";
            return null;
        }
    }
}
