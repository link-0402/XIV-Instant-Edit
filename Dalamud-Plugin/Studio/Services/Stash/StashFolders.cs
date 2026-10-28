using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// The stash's folders: extractions under the importer's output folder and Blender's under the cache's
/// package-imports (listed, counted, removed), and folders the user opened.
/// </summary>
internal static class StashFolders
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".tex", ".tif", ".tiff" };

    /// <summary>
    /// Every subfolder of <paramref name="outputDir"/> an extraction (or Blender) made, newest first.
    /// Only a folder holding an extraction's own bookkeeping (stash.json, its report, the Sims file
    /// list or Blender's list of exports) counts: the output folder may be one the user picked, with
    /// folders of their own in it.
    /// </summary>
    public static IReadOnlyList<string> ListExtractions(string outputDir) => Newest(Extractions(outputDir));

    /// <summary>
    /// The stash's extractions: those under <paramref name="outputDir"/> and, when the user picked another
    /// output folder, the folders under <paramref name="blenderDir"/> (the cache's package-imports, where
    /// Blender always saves) that hold Blender's list of exports; newest first.
    /// </summary>
    public static IReadOnlyList<string> ListStash(string outputDir, string? blenderDir)
    {
        var folders = Extractions(outputDir);
        if (blenderDir != null && !SameFolder(outputDir, blenderDir))
            folders = folders.Concat(Extractions(blenderDir).Where(f => Holds(f, BlenderExports.FileName)));
        return Newest(folders);
    }

    /// <summary>The folder <paramref name="folder"/> was listed from, to remove it from: <paramref name="blenderDir"/> for one of its own, else the output folder.</summary>
    public static string RootOf(string folder, string outputDir, string? blenderDir)
        => blenderDir != null && SameFolder(Path.GetDirectoryName(folder) ?? string.Empty, blenderDir) ? blenderDir : outputDir;

    private static IEnumerable<string> Extractions(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return Array.Empty<string>();
            // An earlier run moved aside during a re-extraction is the new run's to finish or put back.
            return Directory.EnumerateDirectories(root).ToList()
                            .Where(d => !d.EndsWith(ExtractionFolder.AsideSuffix, StringComparison.OrdinalIgnoreCase) && IsExtraction(d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> Newest(IEnumerable<string> folders)
        => folders.Select(d => (Folder: d, When: StashManifest.Read(d)?.ExtractedUtc is { } at && at > DateTime.MinValue ? at : SafeWriteTime(d)))
                  .OrderByDescending(x => x.When)
                  .Select(x => x.Folder)
                  .ToList();

    /// <summary>
    /// Whether an extraction made <paramref name="folder"/>: it holds stash.json, extract-report.txt, the
    /// Sims importer's file list, or the list of models Blender saved there.
    /// </summary>
    public static bool IsExtraction(string folder) => HoldsImport(folder) || Holds(folder, BlenderExports.FileName);

    /// <summary>
    /// Whether Blender alone made <paramref name="folder"/>: Blender's list of exports is its only
    /// bookkeeping, with none of an import's (stash.json, extract-report.txt, the Sims file list).
    /// </summary>
    public static bool IsFromBlender(string folder) => Holds(folder, BlenderExports.FileName) && !HoldsImport(folder);

    private static bool HoldsImport(string folder)
        => Holds(folder, StashManifest.FileName) || Holds(folder, "extract-report.txt") || Holds(folder, Sims.SimsPackageInspector.ManifestName);

    private static bool Holds(string folder, string file)
    {
        try
        {
            return File.Exists(Path.Combine(folder, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether two typed paths name one folder; a path that can't be read names none.</summary>
    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
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
    /// Deletes an extraction folder. Only a folder strictly inside <paramref name="outputDir"/> (the
    /// folder it was listed from, see <see cref="RootOf"/>) is ever removed. Returns why it was not, or
    /// null once it is gone.
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
