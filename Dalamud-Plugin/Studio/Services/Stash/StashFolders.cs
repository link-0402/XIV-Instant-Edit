using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>The extraction folders under the importer's output folder: listing, counting and removing them.</summary>
internal static class StashFolders
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".tex", ".tif", ".tiff" };

    /// <summary>
    /// Every subfolder of <paramref name="outputDir"/> an extraction made, newest first. Only a folder
    /// holding an extraction's own bookkeeping (stash.json, its report or the Sims file list) counts:
    /// the output folder may be one the user picked, with folders of their own in it.
    /// </summary>
    public static IReadOnlyList<string> ListExtractions(string outputDir)
    {
        IEnumerable<string> folders;
        try
        {
            if (!Directory.Exists(outputDir))
                return Array.Empty<string>();
            folders = Directory.EnumerateDirectories(outputDir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        return folders.Where(IsExtraction)
            .Select(d => (Folder: d, When: StashManifest.Read(d)?.ExtractedUtc is { } at && at > DateTime.MinValue ? at : SafeWriteTime(d)))
            .OrderByDescending(x => x.When)
            .Select(x => x.Folder)
            .ToList();
    }

    /// <summary>Whether an extraction made <paramref name="folder"/>: it holds stash.json, extract-report.txt or the Sims importer's file list.</summary>
    public static bool IsExtraction(string folder)
    {
        try
        {
            return File.Exists(Path.Combine(folder, StashManifest.FileName))
                || File.Exists(Path.Combine(folder, "extract-report.txt"))
                || File.Exists(Path.Combine(folder, Sims.SimsPackageInspector.ManifestName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static DateTime SafeWriteTime(string folder)
    {
        try { return Directory.GetLastWriteTimeUtc(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>How many images sit directly in <paramref name="folder"/> (a folder of variants).</summary>
    public static int CountImages(string folder)
    {
        try { return Directory.EnumerateFiles(folder).Count(f => ImageExtensions.Contains(Path.GetExtension(f))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Deletes an extraction folder. Only a folder strictly inside <paramref name="outputDir"/> is
    /// ever removed. Returns why it was not, or null once it is gone.
    /// </summary>
    public static string? Remove(string outputDir, string folder)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir)) + Path.DirectorySeparatorChar;
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!(target + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || target.Length + 1 <= root.Length)
                return "Only extractions inside the output folder can be removed.";
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                return "This folder is a link; remove it in Explorer.";
            if (!IsExtraction(target))
                return "This folder wasn't made by an extraction, so it is left alone.";
            Directory.Delete(target, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
