using System.Globalization;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Ui;

/// <summary> The texts the texture compression card shows. Dalamud-free. </summary>
internal static class TextureCompressionViews
{
    /// <summary> "612.35 MiB": sizes in units of 1024. </summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.00} {units[unit]}");
    }

    public static string Textures(int count)
        => count == 1 ? "1 texture" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} textures");

    /// <summary> What compression is doing right now. </summary>
    public static string Status(CompressionPhase phase, string progress) => phase switch
    {
        CompressionPhase.Off => "Off.",
        CompressionPhase.Watching => "On: textures your character loads are checked a few seconds later.",
        CompressionPhase.Waiting => "New textures loaded. Waiting until your character is out of combat, cutscenes, group pose and loading screens.",
        CompressionPhase.Checking => "Checking your character's textures",
        _ => progress.Length > 0 ? progress : "Working",
    };

    /// <summary> Everything compressed so far and how much smaller it made the files. </summary>
    public static string Totals(IReadOnlyList<CompressedTexture> entries)
    {
        if (entries.Count == 0)
            return "Nothing is compressed yet.";
        var saved = entries.Sum(entry => entry.OriginalLength - entry.CompressedLength);
        return $"{Textures(entries.Count)} compressed so far, {Bytes(saved)} smaller in total.";
    }

    /// <summary> The originals the backup folder holds, each shared copy once. </summary>
    public static string Backups((int Files, long Bytes) totals)
        => totals.Files == 0 ? string.Empty : $"Their originals take {Bytes(totals.Bytes)} in the cache folder's texture-backups folder.";

    /// <summary> The last run's result, when it compressed something. </summary>
    public static string LastRun(CompressionRun run)
        => run.Compressed == 0
            ? string.Empty
            : $"Last run: {Textures(run.Compressed)} from {Bytes(run.BytesBefore)} to {Bytes(run.BytesAfter)}, {run.Finished.ToLocalTime():t}.";

    /// <summary> How many of the textures the character wears the check keeps as they are. </summary>
    public static string Kept(CompressionRun run)
        => run.Kept.Count switch
        {
            0 => string.Empty,
            1 => "1 texture you wear stays uncompressed, because compressing would visibly change it.",
            var count => $"{Textures(count)} you wear stay uncompressed, because compressing would visibly change them.",
        };

    public static string Restored(CompressionRestore result)
    {
        var parts = new List<string>
        {
            result.Restored == 0 ? "Nothing needed restoring." : $"Restored {Textures(result.Restored)}.",
        };
        if (result.Changed.Count == 1)
            parts.Add("1 texture changed since it was compressed, so it was left as it is and keeps its backup.");
        else if (result.Changed.Count > 1)
            parts.Add($"{Textures(result.Changed.Count)} changed since they were compressed, so they were left as they are and keep their backups.");
        if (result.Failed.Count > 0)
            parts.Add($"{Textures(result.Failed.Count)} couldn't be restored.");
        parts.Add("Automatic compression is off.");
        return string.Join(" ", parts);
    }

    /// <summary> Whether a folder is inside Windows' temporary folder, which cleanup tools empty. </summary>
    public static bool InTempFolder(string folder, string temp)
    {
        try
        {
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
