using System.IO.Compression;
using System.Security.Cryptography;

namespace InstantEdit.Services.Audio;

/// <summary>Which sound tools are on disk, and yt-dlp's version once <see cref="MediaTools.RefreshAsync"/> has read it.</summary>
internal sealed record MediaToolsStatus(bool YtDlp, bool Ffmpeg, bool Ffprobe, string? YtDlpVersion)
{
    public bool Installed => YtDlp && Ffmpeg && Ffprobe;
}

/// <summary>A download's progress: the file being fetched, bytes so far and the total when the server gives one.</summary>
internal readonly record struct MediaToolsProgress(string File, long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp(Received / (double)Total.Value, 0, 1) : null;
}

/// <summary>
/// yt-dlp, ffmpeg and ffprobe in the plugin's own tools folder (durable, not %TEMP%). Downloads go
/// to a .part file, are checked against the SHA-256 list of the same release and only then move into
/// place, so nothing ever runs from a file that failed the check. Only bin/ffmpeg.exe and
/// bin/ffprobe.exe come out of the ffmpeg zip. One install, update or removal runs at a time; a
/// second request waits for it. Dalamud-free: logs through the callback.
/// </summary>
internal sealed class MediaTools : IDisposable
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string YtDlpAsset = "yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public const string FfmpegChecksumsUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";
    public const string FfmpegAsset = "ffmpeg-master-latest-win64-gpl.zip";
    public static readonly TimeSpan UpdateAge = TimeSpan.FromDays(7);

    // The ffmpeg "latest" tag is rebuilt daily, so a mismatch can be a rebuild between the list and the zip: trying again fixes that.
    public const string ChecksumMismatch = "The download didn't match its checksum. Try again.";
    public const string NotInstalled = "Download the sound tools in Settings first.";
    public const string DownloadFailed = "Couldn't download the sound tools.";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(3);
    private static readonly string[] FfmpegFiles = ["ffmpeg.exe", "ffprobe.exe"];

    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<string>? log;
    private volatile MediaToolsStatus status;

    /// <param name="handler">For tests; GitHub is reached with the default handler and the system proxy otherwise.</param>
    public MediaTools(string toolsDirectory, HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        Directory = toolsDirectory;
        this.log = log;
        http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // A 200 MB zip outlasts the default 100 s; cancellation stops it instead.
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("InstantEdit");
        status = OnDisk(null);
    }

    public string Directory { get; }
    public string YtDlpPath => Path.Combine(Directory, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(Directory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(Directory, "ffprobe.exe");

    /// <summary>The last known state, cheap enough to read every frame. <see cref="RefreshAsync"/> reads the yt-dlp version.</summary>
    public MediaToolsStatus Status => status;

    /// <summary>Whether all three tools are on disk now.</summary>
    public bool Installed => File.Exists(YtDlpPath) && File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>Whether an install, update or removal is running.</summary>
    public bool Busy => gate.CurrentCount == 0;

    /// <summary>Checks the files again and asks yt-dlp for its version (<c>yt-dlp --version</c>).</summary>
    public async Task<MediaToolsStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        string? version = null;
        if (File.Exists(YtDlpPath))
        {
            try
            {
                var result = await ToolProcess.RunAsync(YtDlpPath, ["--version"], VersionTimeout, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                    version = result.Output.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"yt-dlp --version failed: {e.Message}");
            }
        }
        return status = OnDisk(version);
    }

    /// <summary>Downloads whichever tools are missing.</summary>
    /// <exception cref="InvalidDataException">A download didn't match its checksum, or the zip lacks a tool.</exception>
    /// <exception cref="IOException">GitHub couldn't be reached or the folder couldn't be written.</exception>
    public async Task InstallAsync(IProgress<MediaToolsProgress>? progress, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(YtDlpPath))
            {
                var expected = await Checksum(YtDlpChecksumsUrl, YtDlpAsset, cancellationToken).ConfigureAwait(false);
                var part = YtDlpPath + ".part";
                await Download(YtDlpUrl, part, expected, YtDlpAsset, progress, cancellationToken).ConfigureAwait(false);
                File.Move(part, YtDlpPath, overwrite: true);
                log?.Invoke("Installed yt-dlp.");
            }
            if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
            {
                var expected = await Checksum(FfmpegChecksumsUrl, FfmpegAsset, cancellationToken).ConfigureAwait(false);
                var zip = Path.Combine(Directory, FfmpegAsset + ".part");
                try
                {
                    await Download(FfmpegUrl, zip, expected, FfmpegAsset, progress, cancellationToken).ConfigureAwait(false);
                    ExtractFfmpeg(zip, Directory);
                }
                finally
                {
                    TryDelete(zip);
                }
                log?.Invoke("Installed ffmpeg and ffprobe.");
            }
        }
        catch (HttpRequestException e)
        {
            log?.Invoke($"Sound tools download failed: {e.Message}");
            throw new IOException(DownloadFailed, e);
        }
        finally
        {
            status = OnDisk(status.YtDlpVersion);
            gate.Release();
        }
    }

    /// <summary>Runs <c>yt-dlp -U</c>, which fetches and checks the newest release itself.</summary>
    /// <exception cref="IOException">The update failed.</exception>
    public async Task UpdateYtDlpAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Update(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Updates yt-dlp first when it is older than <see cref="UpdateAge"/>; a failed update is logged
    /// and the old one is used, since it may still work.
    /// </summary>
    public async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(YtDlpPath) || !IsStale(File.GetLastWriteTimeUtc(YtDlpPath), DateTime.UtcNow))
                return;
            try
            {
                await Update(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"yt-dlp update skipped: {e.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Deletes the tools and any unfinished download.</summary>
    /// <exception cref="IOException">A tool is running.</exception>
    public void Remove()
    {
        // Called from the draw thread: never wait for an install to finish.
        if (!gate.Wait(0))
            throw new IOException("The sound tools are being downloaded. Try again later.");
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;
            var tools = new[] { YtDlpPath, FfmpegPath, FfprobePath }.Where(File.Exists).ToList();
            // A running tool keeps its file open: check them all first, so a busy one leaves the others in place.
            foreach (var tool in tools)
                using (new FileStream(tool, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            foreach (var file in tools.Concat(System.IO.Directory.GetFiles(Directory, "*.part")))
                File.Delete(file);
            if (!System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
                System.IO.Directory.Delete(Directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Removing the sound tools failed: {e.Message}");
            throw new IOException("The sound tools are in use. Try again later.", e);
        }
        finally
        {
            status = OnDisk(null);
            gate.Release();
        }
    }

    public static bool IsStale(DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - lastWriteUtc > UpdateAge;

    /// <summary>
    /// A release's checksum list: lines of <c>&lt;hex&gt;  &lt;name&gt;</c> or <c>&lt;hex&gt; *&lt;name&gt;</c>.
    /// Names are matched without case; hashes come back lowercase; other lines are skipped.
    /// </summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOfAny([' ', '\t']);
            if (space != 64 || !line[..64].All(char.IsAsciiHexDigit))
                continue;
            var name = line[space..].TrimStart(' ', '\t').TrimStart('*');
            if (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            if (name.Length > 0)
                sums.TryAdd(name, line[..64].ToLowerInvariant());
        }
        return sums;
    }

    /// <summary>
    /// Copies bin/ffmpeg.exe and bin/ffprobe.exe out of a checked zip into <paramref name="directory"/>,
    /// whatever the zip's top folder is called. Entry names are only compared, never used as paths,
    /// so an entry such as ../../x.exe can't write outside the folder.
    /// </summary>
    /// <exception cref="InvalidDataException">The zip lacks one of the two.</exception>
    public static void ExtractFfmpeg(string zipPath, string directory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var parts = new List<(string Part, string Target)>();
        try
        {
            foreach (var name in FfmpegFiles)
            {
                var entry = archive.Entries.FirstOrDefault(e => IsBinEntry(e.FullName, name))
                            ?? throw new InvalidDataException($"The ffmpeg download has no {name}.");
                var target = Path.Combine(directory, name);
                var part = target + ".part";
                parts.Add((part, target));
                using var source = entry.Open();
                using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(file);
            }
            foreach (var (part, target) in parts)
                File.Move(part, target, overwrite: true);
        }
        finally
        {
            foreach (var (part, _) in parts)
                TryDelete(part);
        }
    }

    private static bool IsBinEntry(string fullName, string name)
    {
        var segments = fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && !segments.Contains("..") && !fullName.StartsWith('/') &&
               segments[^1].Equals(name, StringComparison.OrdinalIgnoreCase) &&
               segments[^2].Equals("bin", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> Checksum(string url, string asset, CancellationToken cancellationToken)
    {
        var text = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseChecksums(text).TryGetValue(asset, out var hash)
            ? hash
            : throw new InvalidDataException($"The release has no checksum for {asset}.");
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="part"/>, hashing as it goes; anything but a match deletes the file.</summary>
    private async Task Download(string url, string part, string expected, string label, IProgress<MediaToolsProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new MediaToolsProgress(label, received, total));
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"{label}: SHA-256 {actual}, expected {expected}.");
                throw new InvalidDataException(ChecksumMismatch);
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private async Task Update(CancellationToken cancellationToken)
    {
        if (!File.Exists(YtDlpPath))
            throw new IOException(NotInstalled);
        ToolResult result;
        try
        {
            result = await ToolProcess.RunAsync(YtDlpPath, ["-U"], UpdateTimeout, cancellationToken, l => log?.Invoke("yt-dlp: " + l),
                l => log?.Invoke("yt-dlp: " + l), Directory).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception)
        {
            throw new IOException("Couldn't update yt-dlp.", e);
        }
        // 100 is yt-dlp's "updated, restart to use it".
        if (result.ExitCode is not (0 or 100))
            throw new IOException("Couldn't update yt-dlp.");
        // -U leaves the file alone when it is already current; the date marks the check so it waits another week.
        File.SetLastWriteTimeUtc(YtDlpPath, DateTime.UtcNow);
    }

    private MediaToolsStatus OnDisk(string? version)
    {
        var ytDlp = File.Exists(YtDlpPath);
        return new MediaToolsStatus(ytDlp, File.Exists(FfmpegPath), File.Exists(FfprobePath), ytDlp ? version : null);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }
}
