using System;
using System.IO;

namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// The one place paths are compared. Penumbra keys a mod's files by game path (forward slashes,
/// compared without case) and points them at files relative to the mod folder, written with
/// backslashes by Penumbra itself and with forward slashes by many tools. Stored strings are never
/// rewritten — only the keys used to compare them are normalised here.
/// </summary>
internal static class PathNormalizer
{
    /// <summary>A game path as a comparison key: forward slashes, no leading slash, lower case.</summary>
    public static string GamePath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>A mod-relative path as a comparison key: forward slashes, no leading slash, lower case.</summary>
    public static string RelKey(string relPath)
        => relPath.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>A mod-relative path as written into meta.json: backslashes, as Penumbra writes them.</summary>
    public static string RelPathForMeta(string relPath)
        => relPath.Replace('/', '\\').Trim().TrimStart('\\');

    public static bool SameRel(string a, string b) => string.Equals(RelKey(a), RelKey(b), StringComparison.Ordinal);

    public static bool SameGamePath(string a, string b) => string.Equals(GamePath(a), GamePath(b), StringComparison.Ordinal);

    /// <summary>Whether a meta.json file value is an absolute path rather than one inside the mod folder (rare, legacy).</summary>
    public static bool IsAbsolute(string relPath)
        => relPath.Length > 2 && (Path.IsPathFullyQualified(relPath) || relPath.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>
    /// Whether a relative path stays inside the mod folder: not rooted, and no ".." segment. Every
    /// path this plugin writes to goes through this, so a crafted meta.json cannot make it write
    /// anywhere else.
    /// </summary>
    public static bool IsSafeRel(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath) || IsAbsolute(relPath) || Path.IsPathRooted(relPath))
            return false;
        foreach (var part in relPath.Replace('\\', '/').Split('/'))
            if (part == "..")
                return false;
        return true;
    }

    /// <summary>The file on disk a meta.json value points at: relative values under the mod folder, absolute ones as they are.</summary>
    public static string ToDiskPath(string modFolder, string relPath)
        => IsAbsolute(relPath)
            ? relPath
            : Path.Combine(modFolder, relPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    /// <summary>Whether a string looks like a game path at all: a folder, a file name and an extension, no drive or backslash.</summary>
    public static bool LooksLikeGamePath(string path)
    {
        var p = path.Trim();
        if (p.Length < 5 || p.Contains(':') || p.Contains('\\') || p.StartsWith('/') || p.Contains("//", StringComparison.Ordinal))
            return false;
        int slash = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        return slash > 0 && dot > slash + 1 && dot < p.Length - 1;
    }

    /// <summary>A file or folder name made safe for a path segment (used for option folders inside a mod).</summary>
    public static string SafeSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "_" : result;
    }
}
