using System;
using System.IO;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// The folder one extraction run writes into: <c>outputDir/name</c> when it is new or holds only an
/// earlier extraction of the same package, else the first free <c>name_2</c>, <c>name_3</c>, and so on.
/// An earlier extraction is moved aside rather than deleted, and only removed once the new run has
/// finished. A run that stops or fails is removed and the earlier one put back, so stopping a
/// re-extraction loses nothing.
/// </summary>
internal sealed class ExtractionFolder
{
    private const string AsideSuffix = ".previous";
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
            // One left behind by a run that never finished (the game closed) is that package's too.
            if (Directory.Exists(aside) && isOwnRun(aside))
                Directory.Delete(aside, recursive: true);
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

    /// <summary>The run finished: the earlier extraction goes.</summary>
    public void Finish()
    {
        if (_aside == null)
            return;
        try { Directory.Delete(_aside, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as name.previous; the next run clears it */ }
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
