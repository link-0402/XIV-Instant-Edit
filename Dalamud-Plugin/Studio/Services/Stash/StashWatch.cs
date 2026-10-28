using System;
using System.IO;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// What the stash reads again when files change under it, asked every couple of seconds: the folder
/// list when a folder comes or goes where it lists from, or a file lands at the top of one (Blender
/// saving into a new folder), and the folder shown when Blender's list of exports in it is written.
/// </summary>
internal sealed class StashWatch
{
    private string?[] _roots = Array.Empty<string?>();
    private long _listed;
    private string? _shown;
    private DateTime _exported;

    /// <summary>Notes the folders are about to be listed. Called first, so a change made while listing shows next time.</summary>
    public void Listing(string outputDir, string? blenderDir)
    {
        _roots = new[] { outputDir, blenderDir };
        _listed = Stamp(_roots);
    }

    /// <summary>Notes <paramref name="folder"/> is about to be read. Called first, so a write made while reading shows next time.</summary>
    public void Showing(string folder)
    {
        _shown = folder;
        _exported = WrittenAt(folder);
    }

    /// <summary>Whether to list the folders again: something changed in them since they were listed.</summary>
    public bool FoldersChanged() => Stamp(_roots) != _listed;

    /// <summary>Whether to read the folder shown again: Blender's list of exports in it was written since it was read.</summary>
    public bool ShownChanged() => _shown != null && WrittenAt(_shown) != _exported;

    /// <summary>
    /// The write times of each root and of the folders directly in it, in one value. A folder's write
    /// time moves when a file or folder is added to it, removed or renamed, so this changes for a new
    /// folder, and again when a file lands at its top later: Blender writes its list after the model.
    /// </summary>
    private static long Stamp(string?[] roots)
    {
        long stamp = 0;
        foreach (var root in roots)
        {
            if (root == null)
                continue;
            try
            {
                var info = new DirectoryInfo(root);
                if (!info.Exists)
                    continue;
                long folders = 0;
                foreach (var folder in info.EnumerateDirectories())
                    folders = unchecked(folders + folder.LastWriteTimeUtc.Ticks);   // in whatever order they come
                stamp = unchecked((stamp * 31 + info.LastWriteTimeUtc.Ticks) * 31 + folders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A folder that can't be read adds nothing; it shows as a change once it can be.
            }
        }
        return stamp;
    }

    /// <summary>When Blender's list of exports in <paramref name="folder"/> was last written; the same early date while it has none.</summary>
    private static DateTime WrittenAt(string folder)
    {
        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(folder, BlenderExports.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
