using System.Globalization;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Turns any audio or video file ffmpeg reads into Ogg Vorbis: the first audio stream, 48 kHz
/// stereo, quality 5. The result goes on to <see cref="OggReader"/> and <see cref="ScdBuilder"/>
/// like a picked .ogg. Dalamud-free.
/// </summary>
internal static class AudioConvert
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    public const string NoAudio = "The file has no audio.";
    private const int MaxErrorLength = 120;

    /// <summary>ffmpeg's arguments, without the executable.</summary>
    public static IReadOnlyList<string> Arguments(string input, TimeSpan? start, TimeSpan? end, string output)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        if (start is { } from)
            arguments.AddRange(["-ss", Seconds(from)]);
        if (end is { } to)
            arguments.AddRange(["-to", Seconds(to)]);
        arguments.AddRange(["-i", input, "-vn", "-map", "0:a:0", "-c:a", "libvorbis", "-q:a", "5", "-ar", "48000", "-ac", "2", output]);
        return arguments;
    }

    /// <summary>A time as seconds with up to three decimals, the form ffmpeg and yt-dlp both read.</summary>
    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// One short line for a failed run: <see cref="NoAudio"/> when the input has no audio stream,
    /// otherwise the last stderr line that says something, with the input's folder left out.
    /// </summary>
    public static string ErrorLine(IEnumerable<string> stderr, string? input = null)
    {
        string? last = null;
        foreach (var raw in stderr)
        {
            var line = raw.Trim();
            if (line.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
                return NoAudio;
            if (Meaningful(line))
                last = line;
        }
        if (last == null)
            return "The file couldn't be converted.";
        // "[in#0 @ 000001d2] Error opening input: ..." reads as "Error opening input: ...".
        if (last.StartsWith('[') && last.IndexOf("] ", StringComparison.Ordinal) is > 0 and var close)
            last = last[(close + 2)..];
        if (!string.IsNullOrEmpty(input))
            last = last.Replace(input, Path.GetFileName(input), StringComparison.OrdinalIgnoreCase);
        if (last.Length > MaxErrorLength)
            last = last[..MaxErrorLength].TrimEnd();
        return "The file couldn't be converted: " + last.TrimEnd('.') + ".";
    }

    private static bool Meaningful(string line) =>
        line.Length > 0 &&
        !line.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("Exiting", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("To ignore this", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("Error opening output file", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("Error opening output files", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file's length as ffprobe reads it; null when ffprobe can't tell (a failure here never stops an import).</summary>
    public static async Task<TimeSpan?> DurationAsync(string ffprobePath, string input, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ToolProcess.RunAsync(ffprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", input],
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && result.Output.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } text &&
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts <paramref name="input"/> (from <paramref name="start"/> to <paramref name="end"/> when
    /// given) into Ogg Vorbis bytes, written through out.ogg in <paramref name="workDirectory"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">ffmpeg failed (one short line), or the result is over <see cref="AudioImport.MaxFileBytes"/>.</exception>
    public static async Task<byte[]> ToOggAsync(string ffmpegPath, string input, TimeSpan? start, TimeSpan? end, string workDirectory,
        CancellationToken cancellationToken, Action<string>? log = null)
    {
        var output = Path.Combine(workDirectory, "out.ogg");
        ToolResult result;
        try
        {
            result = await ToolProcess.RunAsync(ffmpegPath, Arguments(input, start, end, output), Timeout, cancellationToken,
                workingDirectory: workDirectory).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidDataException("Converting took longer than 5 minutes.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log?.Invoke($"ffmpeg didn't start: {e.Message}");
            throw new InvalidDataException("The sound converter didn't start.");
        }
        var file = new FileInfo(output);
        if (result.ExitCode != 0 || !file.Exists)
        {
            log?.Invoke($"ffmpeg exited with {result.ExitCode}: {string.Join(" | ", result.Errors.TakeLast(5))}");
            throw new InvalidDataException(ErrorLine(result.Errors, input));
        }
        if (file.Length > AudioImport.MaxFileBytes)
            throw new InvalidDataException("The converted sound is over 256 MiB.");
        return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
