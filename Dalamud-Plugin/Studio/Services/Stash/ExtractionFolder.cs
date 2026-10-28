using System;
using System.IO;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// The folder one extraction run writes into: <c>outputDir/name</c> when it is new or holds only an
/// earlier extraction of the same package, else the first free <c>name_2</c>, <c>name_3</c>, and so on.
/// An earlier extraction is moved aside rather than deleted, and only removed once the new run has
/// finished; the models Blender saved into it move into the new run. A run that stops or fails is
/// removed and the earlier one put back, so stopping a re-extraction loses nothing.
/// </summary>
internal sealed class ExtractionFolder
{
    /// <summary>The ending of an earlier extraction's folder while it is moved aside; the stash never lists one.</summary>
    internal const string AsideSuffix = ".previous";
    private readonly string? _aside;

    private ExtractionFolder(string path, string? aside, bool numbered)
    {
        Path = path;
        _aside = aside;
        Numbered = numbered;
    }

    public string Path { get; }

    /// <summary>Whether the run went into a numbered folder because the plain one holds something else.</summary>
    public bool Numbered { get; }

    /// <param name="isOwnRun">Whether a folder holds nothing but an earlier extraction of this package.</param>
    public static ExtractionFolder Claim(string outputDir, string name, Func<string, bool> isOwnRun)
    {
        for (int n = 1; n < 1000; n++)
        {
            var folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(outputDir, n == 1 ? name : $"{name}_{n}"));
            if (!Directory.Exists(folder))
                return new ExtractionFolder(folder, null, n > 1);
            if (isOwnRun(folder) && MoveAside(folder, isOwnRun) is { } aside)
                return new ExtractionFolder(folder, aside, n > 1);
        }
        throw new IOException($"No free output folder for {name} in {outputDir}.");
    }

    /// <summary>Moves an earlier run out of the way. Null when it can't be (a file in it is open, say): the run then uses another folder.</summary>
    private static string? MoveAside(string folder, Func<string, bool> isOwnRun)
    {
        var aside = folder + AsideSuffix;
        try
        {
            // One left behind by a run that never finished (the game closed), or whose Blender models
            // couldn't be moved on, is that package's too: its Blender models come back first.
            if (Directory.Exists(aside) && isOwnRun(aside))
            {
                CarryBlenderExports(aside, folder);
                Directory.Delete(aside, recursive: true);
            }
            if (Directory.Exists(aside) || File.Exists(aside))
                return null;
            Directory.Move(folder, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The run finished: the earlier extraction goes, once the models Blender saved into it (Blender/ and its sidecar) are moved into this run.</summary>
    public void Finish()
    {
        if (_aside == null)
            return;
        try
        {
            CarryBlenderExports(_aside, Path);
            Directory.Delete(_aside, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as name.previous; the next run takes its Blender models and clears it */ }
    }

    /// <summary>
    /// Moves the models Blender saved into an earlier run into <paramref name="to"/>: Blender/ first,
    /// then its sidecar, so a move that fails never leaves a sidecar listing models left behind.
    /// Files already there are kept.
    /// </summary>
    private static void CarryBlenderExports(string from, string to)
    {
        MoveInto(System.IO.Path.Combine(from, BlenderExports.FolderName), System.IO.Path.Combine(to, BlenderExports.FolderName));
        var sidecar = System.IO.Path.Combine(from, BlenderExports.FileName);
        var target = System.IO.Path.Combine(to, BlenderExports.FileName);
        if (File.Exists(sidecar) && !File.Exists(target))
            File.Move(sidecar, target);
    }

    /// <summary>Moves a folder into another of the same name; a file already there under the same name is kept.</summary>
    private static void MoveInto(string from, string to)
    {
        if (!Directory.Exists(from))
            return;
        if (!Directory.Exists(to))
        {
            Directory.Move(from, to);
            return;
        }
        foreach (var file in Directory.GetFiles(from))
        {
            var target = System.IO.Path.Combine(to, System.IO.Path.GetFileName(file));
            if (!File.Exists(target) && !Directory.Exists(target))
                File.Move(file, target);
        }
        foreach (var folder in Directory.GetDirectories(from))
            MoveInto(folder, System.IO.Path.Combine(to, System.IO.Path.GetFileName(folder)));
    }

    /// <summary>The run stopped or failed: what it wrote goes, and the earlier extraction comes back.</summary>
    public void Abandon()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            if (_aside != null)
                Directory.Move(_aside, Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the earlier run stays as name.previous */ }
    }
}
