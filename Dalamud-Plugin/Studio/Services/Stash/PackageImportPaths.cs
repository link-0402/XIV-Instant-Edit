using System;
using System.IO;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// Where the package importer extracts to: a folder of the Instant Edit cache, so extractions sit
/// with the plugin's other working files and follow the cache location set in Settings.
/// </summary>
internal static class PackageImportPaths
{
    /// <summary>The cache's folder for extractions; one of the cache's owned root folders.</summary>
    public const string FolderName = "package-imports";

    public static string DefaultOutput(string cacheRoot) => Path.Combine(cacheRoot, FolderName);

    /// <summary>
    /// The folder to extract into: <paramref name="saved"/> when the user picked one, else the cache
    /// default. The old default under %TEMP% (InstantEdit\sims-import) counts as never picked.
    /// </summary>
    public static string ChooseOutput(string saved, string cacheRoot, string tempPath)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return DefaultOutput(cacheRoot);
        var oldDefault = Path.Combine(tempPath, "InstantEdit", "sims-import");
        return string.Equals(Normal(saved), Normal(oldDefault), StringComparison.OrdinalIgnoreCase)
            ? DefaultOutput(cacheRoot)
            : saved;
    }

    private static string Normal(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
