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

    /// <summary>Every subfolder of <paramref name="outputDir"/> that holds a file, newest extraction first.</summary>
    public static IReadOnlyList<string> ListExtractions(string outputDir)
    {
        try
        {
            if (!Directory.Exists(outputDir))
                return Array.Empty<string>();
            return Directory.EnumerateDirectories(outputDir)
                .Where(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
                .Select(d => (Folder: d, When: StashManifest.Read(d)?.ExtractedUtc ?? Directory.GetLastWriteTimeUtc(d)))
                .OrderByDescending(x => x.When)
                .Select(x => x.Folder)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
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
            Directory.Delete(target, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
