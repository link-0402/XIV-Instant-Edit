using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>The stash's folders: extractions under the importer's output folder (listed, counted, removed) and folders the user opened.</summary>
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

    // ── Opened folders ───────────────────────────────────────────────────────
    // Folders of loose files the user opened in the stash (a VRChat avatar's raw resources, say).
    // They are only remembered: the stash never writes into them or deletes them.

    /// <summary>Remembers <paramref name="folder"/> in <paramref name="saved"/> by its full path. Returns why it wasn't, or null.</summary>
    public static string? AddOpened(List<string> saved, string folder)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(full))
                return "That folder isn't there.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase))
            return "Pick a folder, not a whole drive.";
        if (!saved.Contains(full, StringComparer.OrdinalIgnoreCase))
            saved.Add(full);
        return null;
    }

    /// <summary>The remembered folders that are still there, in the order they were opened.</summary>
    public static IReadOnlyList<string> Opened(IEnumerable<string> saved)
        => saved.Where(f => { try { return Directory.Exists(f); } catch (Exception) { return false; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Forgets an opened folder. Its files are not touched.</summary>
    public static void Forget(List<string> saved, string folder)
        => saved.RemoveAll(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));

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
