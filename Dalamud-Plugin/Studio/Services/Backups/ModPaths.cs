using System;
using System.IO;

namespace InstantEdit.Studio.Services.Backups;

/// <summary>
/// The one way clean-up and repairs turn a mod-relative path into a file: it must stay inside the
/// mod folder, with no "..", no rooted path, and no symlink or junction on the way that could lead
/// out of it.
/// </summary>
internal static class ModPaths
{
    /// <summary>The full path of <paramref name="relPath"/> inside <paramref name="modFolder"/>; throws <see cref="ArgumentException"/> when it would lead out.</summary>
    public static string Resolve(string modFolder, string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath) || Path.IsPathRooted(relPath) || relPath.Contains(':'))
            throw new ArgumentException($"Expected a path inside the mod, got '{relPath}'.", nameof(relPath));

        var root = Path.GetFullPath(modFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{relPath}' leads out of the mod folder.", nameof(relPath));

        if (Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("The mod folder is a link to somewhere else.", nameof(modFolder));
        var cursor = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if (!File.Exists(cursor) && !Directory.Exists(cursor))
                continue;
            if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"'{relPath}' goes through a link ({cursor}).", nameof(relPath));
        }
        return full;
    }
}
