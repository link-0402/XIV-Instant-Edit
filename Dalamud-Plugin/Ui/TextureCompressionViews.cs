using System.Globalization;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Ui;

/// <summary> The texts the texture optimization card shows. Dalamud-free. </summary>
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
        CompressionPhase.Waiting => "Waiting until your character is out of combat, cutscenes, group pose and loading screens.",
        CompressionPhase.Checking => "Checking your character's textures",
        _ => progress.Length > 0 ? progress : "Working",
    };

    public static string Hairstyles(int count)
        => count == 1 ? "1 hairstyle" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} hairstyles");

    /// <summary> Everything optimized so far and how much smaller it made the files. </summary>
    public static string Totals(IReadOnlyList<CompressedTexture> entries, IReadOnlyList<RefitGroup> refits)
    {
        if (entries.Count == 0 && refits.Count == 0)
            return "Nothing is optimized yet.";
        var saved = entries.Sum(entry => entry.OriginalLength - entry.CompressedLength) + refits.Sum(group => group.BytesSaved);
        return $"{What(entries.Count, entries.Count(entry => entry.Shrunk), refits.Count)} so far, {Bytes(saved)} smaller in total.";
    }

    /// <summary> "2 textures optimized (1 of one color, shrunk to 32 × 32) and 1 hairstyle refit", leaving out what is none. </summary>
    private static string What(int textures, int shrunk, int hairstyles)
    {
        var parts = new List<string>();
        if (textures > 0)
            parts.Add($"{Textures(textures)} optimized{OneColor(shrunk)}");
        if (hairstyles > 0)
            parts.Add($"{Hairstyles(hairstyles)} refit");
        return string.Join(" and ", parts);
    }

    /// <summary> " (2 of one color, shrunk to 32 × 32)", or nothing when none were shrunk. </summary>
    private static string OneColor(int shrunk)
        => shrunk == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({shrunk:N0} of one color, shrunk to {SingleColor.Size} × {SingleColor.Size})");

    /// <summary> The originals the backup folder holds, each shared copy once, and how long they stay. </summary>
    public static string Backups((int Files, long Bytes) totals, bool automaticCleanup)
        => totals.Files == 0
            ? string.Empty
            : automaticCleanup
                ? string.Create(CultureInfo.InvariantCulture, $"Backups: {Bytes(totals.Bytes)}, kept for {TextureBackupStore.Retention.TotalDays:0} days.")
                : $"Backups: {Bytes(totals.Bytes)}, kept until restored (automatic cache cleanup is off).";

    /// <summary> The last run's result, when it optimized something. </summary>
    public static string LastRun(CompressionRun run)
        => run.Compressed == 0 && run.Refit == 0
            ? string.Empty
            : $"Last run: {What(run.Compressed, run.Shrunk, run.Refit)}, {Bytes(run.BytesBefore - run.BytesAfter)} smaller, {run.Finished.ToLocalTime():t}.";

    /// <summary> How many worn hair textures use only part of their space but can't be refit safely. </summary>
    public static string NotRefit(CompressionRun run)
        => run.NotRefit.Count switch
        {
            0 => string.Empty,
            1 => "1 hair texture you wear uses only part of its space but can't be refit safely. Hover for why.",
            var count => string.Create(CultureInfo.InvariantCulture,
                $"{count:N0} hair textures you wear use only part of their space but can't be refit safely. Hover for why."),
        };

    /// <summary> How many of the textures the character wears the check keeps as they are. </summary>
    public static string Kept(CompressionRun run)
        => run.Kept.Count switch
        {
            0 => string.Empty,
            1 => "1 texture you wear stays as it is, because compressing would visibly change it.",
            var count => $"{Textures(count)} you wear stay as they are, because compressing would visibly change them.",
        };

    /// <summary> How many of the textures the character wears stay as they are because their originals were restored. </summary>
    public static string LeftRestored(CompressionRun run)
        => run.Restored.Count switch
        {
            0 => string.Empty,
            1 => "1 restored file is skipped. Hover for which.",
            var count => string.Create(CultureInfo.InvariantCulture, $"{count:N0} restored files are skipped. Hover for which."),
        };

    public static string Restored(CompressionRestore result)
    {
        var restored = new List<string>();
        if (result.Restored > 0)
            restored.Add(Textures(result.Restored));
        if (result.Hairstyles > 0)
            restored.Add(Hairstyles(result.Hairstyles));
        var parts = new List<string>
        {
            restored.Count == 0 ? "Nothing to restore on your character." : $"Restored {string.Join(" and ", restored)}.",
        };
        if (result.Changed.Count == 1)
            parts.Add("1 file changed since it was optimized, so it was left as it is and keeps its backup.");
        else if (result.Changed.Count > 1)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{result.Changed.Count:N0} files changed since they were optimized, so they were left as they are and keep their backups."));
        if (result.Failed.Count == 1)
            parts.Add("1 file couldn't be restored.");
        else if (result.Failed.Count > 1)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{result.Failed.Count:N0} files couldn't be restored."));
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
