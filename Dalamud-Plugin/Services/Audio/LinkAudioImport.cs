using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Audio;

internal enum LinkImportPhase
{
    GettingTools,
    Downloading,
    Converting,
}

/// <summary>Where a link import is, for the popup's spinner line.</summary>
internal readonly record struct LinkImportProgress(LinkImportPhase Phase, double? Percent = null)
{
    public string Text => Phase switch
    {
        LinkImportPhase.GettingTools => "Getting tools",
        LinkImportPhase.Downloading => Percent is { } percent ? $"Downloading {percent.ToString("0", CultureInfo.InvariantCulture)}%" : "Downloading",
        _ => "Converting",
    };
}

/// <summary>
/// Brings a part of a YouTube video in as a sound: yt-dlp downloads only that part into a work
/// folder under %TEMP%/InstantEdit/sound-import, ffmpeg turns it into Ogg named after the video's
/// title, and the caller's import takes that file. The work folder goes afterwards, whether the job
/// succeeded, failed or was cancelled. Dalamud-free.
/// </summary>
internal static partial class LinkAudioImport
{
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
    public const int MaxTitleLength = 60;
    private const int MaxErrorLength = 120;

    public const string Private = "This video is private.";
    public const string AgeRestricted = "This video is age-restricted.";
    public const string BotCheck = "YouTube wants a sign-in for this video. Try again later.";
    public const string Region = "This video isn't available in your region.";
    public const string Unavailable = "This video is unavailable.";
    public const string Live = "Live streams can't be used.";
    public const string Network = "Couldn't reach YouTube.";
    public const string Failed = "The download failed.";

    private static readonly string[] NetworkMarkers =
    [
        "Unable to download webpage", "Failed to resolve", "getaddrinfo failed", "Name or service not known", "timed out",
        "Connection refused", "Connection reset", "No route to host", "Network is unreachable", "Temporary failure in name resolution",
        "urlopen error", "Unable to connect", "Connection aborted",
    ];

    // What yt-dlp prints when YouTube changed and the extractor needs an update.
    private static readonly string[] ExtractorMarkers =
    [
        "Unable to extract", "extraction failed", "nsig", "Requested format is not available", "please report this issue",
        "HTTP Error 403", "403 Forbidden", "Signature",
    ];

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    [GeneratedRegex(@"^\[download\]\s+(\d+(?:\.\d+)?)%")]
    private static partial Regex DownloadPercent();

    [GeneratedRegex(@"\btime=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex FfmpegTime();

    /// <summary>yt-dlp's arguments, without the executable. Only the request's part is downloaded; a live stream is refused.</summary>
    public static IReadOnlyList<string> Arguments(string toolsDirectory, string workDirectory, LinkRequest request)
    {
        // Output names are yt-dlp templates, where a bare % would start a field.
        var work = workDirectory.Replace("%", "%%", StringComparison.Ordinal);
        return
        [
            "--no-config", "--no-playlist", "--no-simulate", "--newline",
            // An m4a section starts where it was asked to; a webm/opus one starts at 0:00 (see SectionStart).
            "-f", "bestaudio[ext=m4a]/bestaudio/best",
            "--match-filter", "!is_live",
            "--download-sections", $"*{AudioConvert.Seconds(request.Start)}-{AudioConvert.Seconds(request.End)}",
            "--ffmpeg-location", toolsDirectory,
            "--print-to-file", "title", Path.Combine(work, "title.txt"),
            "-o", Path.Combine(work, "audio.%(ext)s"),
            request.Url,
        ];
    }

    /// <summary>
    /// The download's percentage from one output line: yt-dlp's <c>[download]  42.0%</c>, or, when a part
    /// is fetched through ffmpeg, its <c>time=</c> against <paramref name="sectionLength"/>. Null for other lines.
    /// </summary>
    public static double? ProgressPercent(string line, TimeSpan? sectionLength = null)
    {
        var match = DownloadPercent().Match(line);
        if (match.Success)
            return Math.Clamp(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100);
        if (sectionLength is not { TotalSeconds: > 0 } length)
            return null;
        match = FfmpegTime().Match(line);
        if (!match.Success)
            return null;
        var seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0 +
                      int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60.0 +
                      double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return Math.Clamp(seconds / length.TotalSeconds * 100, 0, 100);
    }

    /// <summary>
    /// Where to start in the downloaded part so it holds only the asked-for stretch, or null when it already does.
    /// Measured 2026-11-04: from YouTube's webm/opus stream, yt-dlp's section runs from 0:00 to the end time
    /// (0:10-0:15 gave 15 s starting at 0:00), so a part over a second longer than asked keeps its last stretch.
    /// A part to the end of the video has no known length and is left as it is.
    /// </summary>
    public static TimeSpan? SectionStart(TimeSpan? downloaded, LinkRequest request)
    {
        if (request.ToEnd || downloaded is not { } length)
            return null;
        var wanted = request.End - request.Start;
        return length - wanted > TimeSpan.FromSeconds(1) ? length - wanted : null;
    }

    /// <summary>Whether yt-dlp skipped the video because it is live or not started yet.</summary>
    public static bool IsLiveRefusal(IEnumerable<string> lines) =>
        lines.Any(l => l.Contains("does not pass filter", StringComparison.OrdinalIgnoreCase) && l.Contains("is_live", StringComparison.Ordinal) ||
                       l.Contains("This live event", StringComparison.OrdinalIgnoreCase));

    /// <summary>One short line for what went wrong, from yt-dlp's output.</summary>
    public static string ErrorMessage(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        if (IsLiveRefusal(all))
            return Live;
        var errors = ErrorLines(all);
        if (Known(errors) is { } known)
            return known;
        var last = errors.LastOrDefault();
        if (last == null)
            return Failed;
        // The game font draws an ellipsis as dashes.
        last = StripPrefix(last).Replace("…", ".", StringComparison.Ordinal).Replace("...", ".", StringComparison.Ordinal);
        if (last.Length > MaxErrorLength)
            last = last[..MaxErrorLength].TrimEnd();
        return last.Length == 0 ? Failed : $"{Failed} {last.TrimEnd('.')}.";
    }

    /// <summary>Whether the failure looks like YouTube changed under yt-dlp, which an update may fix.</summary>
    public static bool IsExtractorError(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        if (IsLiveRefusal(all))
            return false;
        var errors = ErrorLines(all);
        return Known(errors) == null &&
               errors.Any(l => ExtractorMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> ErrorLines(List<string> lines)
    {
        var errors = lines.Where(l => l.TrimStart().StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
        return errors.Count > 0 ? errors : lines.Where(l => l.Trim().Length > 0).TakeLast(1).ToList();
    }

    private static string? Known(List<string> errors)
    {
        bool Has(string text) => errors.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (Has("Private video"))
            return Private;
        if (Has("Sign in to confirm your age"))
            return AgeRestricted;
        // YouTube's bot check, with a straight or a curly apostrophe; its advice is for yt-dlp's command line.
        if (Has("confirm you're not a bot") || Has("confirm you’re not a bot"))
            return BotCheck;
        // Both "not available in your country" and "has not made this video available in your country".
        if (Has("available in your country"))
            return Region;
        if (Has("Video unavailable"))
            return Unavailable;
        if (NetworkMarkers.Any(Has))
            return Network;
        return null;
    }

    // "ERROR: [youtube] abcdefghijk: Something" reads as "Something".
    private static string StripPrefix(string line)
    {
        line = line.Trim();
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            line = line[6..].TrimStart();
        if (line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is > 0 and var close)
        {
            line = line[(close + 2)..];
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon is > 0 and < 20 && !line[..colon].Contains(' '))
                line = line[(colon + 2)..];
        }
        return line.Trim();
    }

    /// <summary>A video title as a file name: no path or control characters, single spaces, at most 60 characters, "sound" when nothing is left.</summary>
    public static string CleanTitle(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new StringBuilder();
        foreach (var rune in (title ?? "").EnumerateRunes())
        {
            var bad = Rune.IsControl(rune) || rune.IsBmp && invalid.Contains((char)rune.Value) || Rune.IsWhiteSpace(rune);
            if (bad)
            {
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
                continue;
            }
            text.Append(rune.ToString());
        }
        var cleaned = text.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaxTitleLength)
        {
            // Cuts between characters, never inside a surrogate pair or a combined emoji.
            var cut = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(cleaned);
            while (elements.MoveNext() && cut.Length + elements.GetTextElement().Length <= MaxTitleLength)
                cut.Append(elements.GetTextElement());
            cleaned = cut.ToString().TrimEnd('.', ' ');
        }
        var stem = cleaned.Split('.')[0].Trim();
        if (cleaned.Length == 0 || ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return "sound";
        return cleaned;
    }

    /// <summary>
    /// Downloads the request's part, converts it to &lt;title&gt;.ogg and hands that path to
    /// <paramref name="import"/> (the timeline's sound import), returning what it returns.
    /// </summary>
    /// <exception cref="InvalidDataException">The download or conversion failed; the message is one short line.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the tools' processes are killed.</exception>
    public static async Task<T> RunAsync<T>(MediaTools tools, LinkRequest request, Func<string, T> import, IProgress<LinkImportProgress>? progress,
        CancellationToken cancellationToken, Action<string>? log = null)
    {
        var work = ToolProcess.NewWorkFolder();
        try
        {
            progress?.Report(new LinkImportProgress(LinkImportPhase.GettingTools));
            if (!tools.Installed)
                throw new InvalidOperationException(MediaTools.NotInstalled);
            await tools.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new LinkImportProgress(LinkImportPhase.Downloading));
            var result = await Download(tools, request, work, progress, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 && IsExtractorError(result.Lines))
            {
                log?.Invoke("yt-dlp failed with an extractor error; updating and trying once more.");
                try
                {
                    await tools.UpdateYtDlpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log?.Invoke($"yt-dlp update failed: {e.Message}");
                }
                foreach (var file in Directory.GetFiles(work))
                    File.Delete(file);
                result = await Download(tools, request, work, progress, cancellationToken).ConfigureAwait(false);
            }
            if (IsLiveRefusal(result.Lines))
                throw new InvalidDataException(Live);
            var audio = Directory.GetFiles(work, "audio.*").FirstOrDefault(f =>
                !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
            if (result.ExitCode != 0 || audio == null)
            {
                log?.Invoke($"yt-dlp exited with {result.ExitCode}: {string.Join(" | ", result.Errors.TakeLast(5))}");
                throw new InvalidDataException(ErrorMessage(result.Lines));
            }

            progress?.Report(new LinkImportProgress(LinkImportPhase.Converting));
            var from = SectionStart(await AudioConvert.DurationAsync(tools.FfprobePath, audio, cancellationToken).ConfigureAwait(false), request);
            if (from != null)
                log?.Invoke($"yt-dlp's part runs from the video's start; keeping its last {AudioConvert.Seconds(request.End - request.Start)} s.");
            var ogg = await AudioConvert.ToOggAsync(tools.FfmpegPath, audio, from, null, work, cancellationToken, log).ConfigureAwait(false);
            var titleFile = Path.Combine(work, "title.txt");
            var title = File.Exists(titleFile) ? File.ReadLines(titleFile).FirstOrDefault() : null;
            var named = Path.Combine(work, "sound", CleanTitle(title) + ".ogg");
            Directory.CreateDirectory(Path.GetDirectoryName(named)!);
            await File.WriteAllBytesAsync(named, ogg, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return import(named);
        }
        finally
        {
            ToolProcess.DeleteWorkFolder(work, log);
        }
    }

    private static async Task<ToolResult> Download(MediaTools tools, LinkRequest request, string work, IProgress<LinkImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var length = request.ToEnd ? (TimeSpan?)null : request.End - request.Start;
        void Line(string line)
        {
            if (ProgressPercent(line, length) is { } percent)
                progress?.Report(new LinkImportProgress(LinkImportPhase.Downloading, percent));
        }
        try
        {
            return await ToolProcess.RunAsync(tools.YtDlpPath, Arguments(tools.Directory, work, request), DownloadTimeout, cancellationToken,
                Line, Line, work).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidDataException("The download took longer than 15 minutes.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("yt-dlp didn't start.");
        }
    }
}
