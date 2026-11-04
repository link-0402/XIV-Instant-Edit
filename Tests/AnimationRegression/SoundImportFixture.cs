using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;

/// <summary>
/// Sounds from links and any audio or video file: link and time parsing, the ffmpeg and yt-dlp
/// arguments, progress and error mapping, checksum lists, title cleaning, the tools install against
/// a fake GitHub, and, when ffmpeg is on PATH, real conversions of generated tones.
/// </summary>
internal static class SoundImportFixture
{
    // Made up: 11 characters of the video id alphabet.
    private const string Id = "abcDEF12_-3";
    private const string Watch = "https://www.youtube.com/watch?v=" + Id;

    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Links(check);
        Times(check);
        Arguments(check);
        YtDlpOutput(check);
        Titles(check);
        Checksums(check);
        Imports(check, reject);
        await Install(check);
        await WithFfmpeg(check, reject);
    }

    private static void Links(Action<bool, string> check)
    {
        static LinkRequest? Request(string url) => LinkInput.Parse(url, "", "").Request;
        var plain = LinkInput.Parse(Watch, "", "");
        check(plain is { Ok: true, Error: null, LinkStart: null } && plain.Request! is { Url: Watch, VideoId: Id, ToEnd: true } request &&
              request.Start == TimeSpan.Zero && request.End == TimeSpan.FromMinutes(10),
            "a watch link parses to its id, from the start, capped at 10 minutes");
        check(new[]
              {
                  "https://youtube.com/watch?v=" + Id, "http://m.youtube.com/watch?v=" + Id, "https://music.youtube.com/watch?v=" + Id + "&feature=share",
                  "https://youtu.be/" + Id, "youtu.be/" + Id, "www.youtube.com/watch?v=" + Id, "https://www.youtube.com/shorts/" + Id,
                  "https://www.youtube.com/live/" + Id + "?si=x", "  https://WWW.YouTube.com/watch?v=" + Id + "  ",
              }.All(url => Request(url)?.Url == Watch),
            "youtube.com, www., m., music. and youtu.be links, watch, shorts and live, all normalize to the watch link");
        check(Request("https://www.youtube.com/watch?v=" + Id + "&list=PLmadeup123&index=4")?.Url == Watch &&
              Request("https://youtu.be/" + Id + "?list=PLmadeup123")?.Url == Watch,
            "playlist parameters are dropped");
        check(LinkInput.Parse("https://open.spotify.com/track/madeup", "", "").Error == LinkInput.NoSpotify &&
              LinkInput.Parse("spotify:track:madeup", "", "").Error == LinkInput.NoSpotify &&
              LinkInput.Parse("https://spotify.link/madeup", "", "").Error == LinkInput.NoSpotify,
            "Spotify links are refused with their own line");
        check(new[]
              {
                  "https://vimeo.com/123", "https://notyoutube.com/watch?v=" + Id, "https://youtube.com.example.com/watch?v=" + Id,
                  "ftp://youtube.com/watch?v=" + Id, "not a link at all",
              }.All(url => LinkInput.Parse(url, "", "").Error == LinkInput.OnlyYouTube),
            "other hosts and schemes are refused as not YouTube");
        check(new[]
              {
                  "https://www.youtube.com/playlist?list=PLmadeup123", "https://www.youtube.com/watch?v=short", "https://www.youtube.com/",
                  "https://www.youtube.com/watch?v=" + Id + "x", "https://youtu.be/", "https://www.youtube.com/shorts/",
              }.All(url => LinkInput.Parse(url, "", "").Error == LinkInput.NoVideo),
            "links without a video id are refused");
        check(LinkInput.Parse("", "", "").Error == LinkInput.NeedLink && LinkInput.Parse(null, null, null).Error == LinkInput.NeedLink,
            "an empty link asks for one");

        var seconds = new (string T, double Expected)[] { ("90", 90), ("90s", 90), ("1m30s", 90), ("1h2m3s", 3723), ("2m", 120), ("1h", 3600) };
        check(seconds.All(c => LinkInput.Parse($"https://youtu.be/{Id}?t={c.T}", "", "") is { LinkStart: { } start, Request: { } r } &&
                               start.TotalSeconds == c.Expected && r.Start == start && r.End == start + TimeSpan.FromMinutes(10)),
            "t= as 90, 90s, 1m30s, 1h2m3s, 2m and 1h fills Start");
        check(LinkInput.Parse($"https://www.youtube.com/watch?v={Id}#t=1m5s", "", "").LinkStart == TimeSpan.FromSeconds(65),
            "a t= in the fragment counts too");
        check(new[] { "abc", "1m1h", "m30s", "1x", "1h2h", "" }.All(t => LinkInput.Parse($"https://youtu.be/{Id}?t={t}", "", "") is { LinkStart: null, Ok: true }),
            "an unreadable t= is ignored");
        check(LinkInput.Parse($"https://youtu.be/{Id}?t=90", "0:10", "").Request?.Start == TimeSpan.FromSeconds(10),
            "a typed Start wins over the link's t=");
        check(LinkInput.Parse($"https://youtu.be/{Id}?t=90", "", "0:30").Error == LinkInput.EndBeforeStart,
            "a link start past the typed End is refused");
    }

    private static void Times(Action<bool, string> check)
    {
        var good = new (string Text, double Seconds)[]
        {
            ("90", 90), ("90.5", 90.5), (" 7 ", 7), ("0:59", 59), ("1:02.5", 62.5), ("75:00", 4500), ("1:02:03", 3723), ("0:00:00.25", 0.25), (".5", 0.5),
        };
        check(good.All(c => LinkInput.TryParseTime(c.Text, out var t) && Math.Abs(t.TotalSeconds - c.Seconds) < 1e-9),
            "times read as seconds, m:ss and h:mm:ss with decimals");
        check(new[] { "", " ", "abc", "-5", "1e3", "1:60", "1:60:00", "1::2", "1:2:3:4", "1:", ":30", "1.5:00", "1,5", "999999999" }
                .All(t => !LinkInput.TryParseTime(t, out _)),
            "bad times are refused without exceptions");
        check(LinkInput.Parse(Watch, "abc", "").Error == LinkInput.BadStart && LinkInput.Parse(Watch, "", "x").Error == LinkInput.BadEnd,
            "an unreadable Start or End says which");
        check(LinkInput.Parse(Watch, "1:00", "0:30").Error == LinkInput.EndBeforeStart && LinkInput.Parse(Watch, "1:00", "60").Error == LinkInput.EndBeforeStart,
            "End before or equal to Start is refused");
        check(LinkInput.Parse(Watch, "0:30", "10:31").Error == LinkInput.TooLong &&
              LinkInput.Parse(Watch, "0", "10:00").Request is { ToEnd: false } exact && exact.End == TimeSpan.FromMinutes(10),
            "clips over 10 minutes are refused; exactly 10 is fine");
        check(LinkInput.Parse(Watch, "5:00", "").Request is { ToEnd: true } open && open.End == TimeSpan.FromMinutes(15),
            "an empty End runs 10 minutes after Start");
        check(LinkInput.Format(TimeSpan.FromSeconds(62.5)) == "1:02.5" && LinkInput.Format(TimeSpan.FromSeconds(3723)) == "1:02:03" &&
              LinkInput.Format(TimeSpan.Zero) == "0:00" && LinkInput.Format(TimeSpan.FromSeconds(90)) == "1:30",
            "times format as m:ss or h:mm:ss");
    }

    private static void Arguments(Action<bool, string> check)
    {
        var trimmed = AudioConvert.Arguments(@"D:\Clips\in put 'x'.mp3", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), @"D:\Work\out.ogg");
        check(trimmed.SequenceEqual(["-hide_banner", "-nostdin", "-y", "-ss", "1.5", "-to", "3", "-i", @"D:\Clips\in put 'x'.mp3", "-vn", "-map", "0:a:0",
                  "-c:a", "libvorbis", "-q:a", "5", "-ar", "48000", "-ac", "2", @"D:\Work\out.ogg"]),
            "ffmpeg's arguments trim, take the first audio stream and encode 48 kHz stereo Vorbis");
        var whole = AudioConvert.Arguments("in.mp4", null, null, "out.ogg");
        check(!whole.Contains("-ss") && !whole.Contains("-to") && whole[3] == "-i" && whole[^1] == "out.ogg",
            "without start and end, ffmpeg converts the whole file");

        var request = LinkInput.Parse(Watch, "1:30", "2:30.25").Request!;
        var yt = LinkAudioImport.Arguments(@"D:\Tools", @"D:\Work\job", request);
        static bool Pair(IReadOnlyList<string> args, string name, string value) =>
            Enumerable.Range(0, args.Count - 1).Any(i => args[i] == name && args[i + 1] == value);
        check(yt[0] == "--no-config" && yt.Contains("--no-playlist") && yt.Contains("--no-simulate") && yt.Contains("--newline") &&
              Pair(yt, "-f", "bestaudio[ext=m4a]/bestaudio/best") && Pair(yt, "--match-filter", "!is_live") && Pair(yt, "--download-sections", "*90-150.25") &&
              Pair(yt, "--ffmpeg-location", @"D:\Tools") && Pair(yt, "-o", @"D:\Work\job\audio.%(ext)s") && yt[^1] == Watch &&
              Enumerable.Range(0, yt.Count - 2).Any(i => yt[i] == "--print-to-file" && yt[i + 1] == "title" && yt[i + 2] == @"D:\Work\job\title.txt"),
            "yt-dlp's arguments download only the part, refuse live streams and print the title to a file");
        check(LinkAudioImport.Arguments("t", @"D:\100%\job", request).Contains(@"D:\100%%\job\audio.%(ext)s"),
            "a % in the work folder is escaped for yt-dlp's templates");
    }

    private static void YtDlpOutput(Action<bool, string> check)
    {
        check(LinkAudioImport.ProgressPercent("[download]  42.0% of    3.20MiB at  1.00MiB/s ETA 00:02") == 42 &&
              LinkAudioImport.ProgressPercent("[download] 100% of    3.20MiB in 00:00:03") == 100 &&
              LinkAudioImport.ProgressPercent("[download] Destination: audio.webm") == null &&
              LinkAudioImport.ProgressPercent("[youtube] Extracting URL") == null,
            "yt-dlp's [download] percentages are read, other lines give nothing");
        check(LinkAudioImport.ProgressPercent("size=     512KiB time=00:00:30.00 bitrate= 139.8kbits/s speed=10x", TimeSpan.FromMinutes(1)) == 50 &&
              LinkAudioImport.ProgressPercent("size=     512KiB time=00:02:00.00 bitrate=", TimeSpan.FromMinutes(1)) == 100 &&
              LinkAudioImport.ProgressPercent("size=     512KiB time=00:00:30.00 bitrate=") == null,
            "ffmpeg's time= counts against the part's length when known");
        check(new LinkImportProgress(LinkImportPhase.Downloading, 42.4).Text == "Downloading 42%" &&
              new LinkImportProgress(LinkImportPhase.Downloading).Text == "Downloading" &&
              new LinkImportProgress(LinkImportPhase.GettingTools).Text == "Getting tools" &&
              new LinkImportProgress(LinkImportPhase.Converting).Text == "Converting",
            "the phases read as the popup shows them");

        var cases = new (string Line, string Expected)[]
        {
            ($"ERROR: [youtube] {Id}: Private video. Sign in if you've been granted access to this video", LinkAudioImport.Private),
            ($"ERROR: [youtube] {Id}: Sign in to confirm your age. This video may be inappropriate for some users.", LinkAudioImport.AgeRestricted),
            ($"ERROR: [youtube] {Id}: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies for the authentication.", LinkAudioImport.BotCheck),
            ($"ERROR: [youtube] {Id}: Sign in to confirm you’re not a bot.", LinkAudioImport.BotCheck),
            ($"ERROR: [youtube] {Id}: Video unavailable. The uploader has not made this video available in your country", LinkAudioImport.Region),
            ($"ERROR: [youtube] {Id}: This video is not available in your country", LinkAudioImport.Region),
            ($"ERROR: [youtube] {Id}: Video unavailable", LinkAudioImport.Unavailable),
            ($"[download] Made up title does not pass filter (!is_live), skipping ..", LinkAudioImport.Live),
            ($"ERROR: [youtube] {Id}: This live event will begin in 3 hours.", LinkAudioImport.Live),
            ($"ERROR: [youtube] {Id}: Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>", LinkAudioImport.Network),
            ("ERROR: Unable to download webpage: The read operation timed out", LinkAudioImport.Network),
            ($"ERROR: [youtube] {Id}: Something odd happened", "The download failed. Something odd happened."),
        };
        foreach (var (line, expected) in cases)
            check(LinkAudioImport.ErrorMessage(["[youtube] Extracting URL", "WARNING: a warning", line]) == expected, $"yt-dlp's \"{line[..Math.Min(40, line.Length)]}\" shows as \"{expected}\"");
        check(LinkAudioImport.ErrorMessage([]) == LinkAudioImport.Failed, "no output reads as a failed download");
        check(LinkAudioImport.ErrorMessage(["ERROR: " + new string('x', 300)]).Length < 150, "a long error line is cut short");
        check(LinkAudioImport.ErrorMessage([$"ERROR: [youtube] {Id}: Waiting... then gave up…"]) is var dotted &&
              !dotted.Contains("...", StringComparison.Ordinal) && !dotted.Contains('…'), "an error line keeps no ellipsis for the game font");
        check(LinkAudioImport.ErrorMessage([$"ERROR: [youtube] {Id}: Private video", "WARNING: Video unavailable"]) == LinkAudioImport.Private,
            "error lines decide over warnings");

        var section = new LinkRequest(Watch, Id, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), false);
        check(LinkAudioImport.SectionStart(TimeSpan.FromSeconds(15), section) == TimeSpan.FromSeconds(10) &&
              LinkAudioImport.SectionStart(TimeSpan.FromSeconds(5.0001), section) == null &&
              LinkAudioImport.SectionStart(null, section) == null &&
              LinkAudioImport.SectionStart(TimeSpan.FromSeconds(600), section with { End = TimeSpan.FromSeconds(610), ToEnd = true }) == null,
            "a part that runs from the video's start keeps only its asked-for stretch; one of the right length, of unknown length or to the end is kept");
        check(LinkAudioImport.IsExtractorError(["ERROR: [download] ffmpeg: https://x.googlevideo.com/videoplayback: Server returned 403 Forbidden (access denied)"]),
            "a 403 from ffmpeg's download is retried once");
        check(LinkAudioImport.IsExtractorError([$"ERROR: [youtube] {Id}: Unable to extract uploader id; please report this issue on GitHub"]) &&
              LinkAudioImport.IsExtractorError(["WARNING: [youtube] nsig extraction failed", "ERROR: [youtube] x: Requested format is not available"]),
            "extractor failures are recognized for an update and retry");
        check(!LinkAudioImport.IsExtractorError([$"ERROR: [youtube] {Id}: Private video. please report this issue"]) &&
              !LinkAudioImport.IsExtractorError(["ERROR: Unable to download webpage: <urlopen error timed out>"]) &&
              !LinkAudioImport.IsExtractorError(["[download] x does not pass filter (!is_live), skipping"]) &&
              !LinkAudioImport.IsExtractorError([$"ERROR: [youtube] {Id}: Something odd happened"]),
            "private, network, live and unknown failures don't trigger an update");
    }

    private static void Titles(Action<bool, string> check)
    {
        check(LinkAudioImport.CleanTitle("My: Song / Name?*") == "My Song Name" && LinkAudioImport.CleanTitle("a\tb\nc  d") == "a b c d" &&
              LinkAudioImport.CleanTitle("  dots...  ") == "dots" && LinkAudioImport.CleanTitle("Café <Live>") == "Café Live",
            "titles lose path characters, keep letters and collapse spaces");
        check(LinkAudioImport.CleanTitle(null) == "sound" && LinkAudioImport.CleanTitle(" ?*/ ") == "sound" &&
              LinkAudioImport.CleanTitle("CON") == "sound" && LinkAudioImport.CleanTitle("nul.txt") == "sound",
            "empty and reserved titles become \"sound\"");
        var cut = LinkAudioImport.CleanTitle(new string('a', 59) + "\U0001F3B5 tail");
        check(LinkAudioImport.CleanTitle(new string('b', 100)).Length == 60 && cut == new string('a', 59) && !char.IsSurrogate(cut[^1]),
            "titles stop at 60 characters without splitting a character");
    }

    private static void Checksums(Action<bool, string> check)
    {
        var a = new string('A', 64);
        var b = new string('0', 63) + "f";
        var sums = MediaTools.ParseChecksums($"{a}  yt-dlp.exe\n{b} *ffmpeg-master-latest-win64-gpl.zip\r\nnot a line\n{new string('1', 63)}  short\n{a}  ./other\n\n");
        check(sums.Count == 3 && sums["YT-DLP.EXE"] == new string('a', 64) && sums["ffmpeg-master-latest-win64-gpl.zip"] == b && sums.ContainsKey("other"),
            "checksum lists read both '<hex>  <name>' and '<hex> *<name>', lowercase, names without case");
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        check(MediaTools.IsStale(now.AddDays(-8), now) && !MediaTools.IsStale(now.AddDays(-6), now), "yt-dlp is updated when older than 7 days");
    }

    private static void Imports(Action<bool, string> check, Action<Action, string> reject)
    {
        check(AudioImport.NeedsConverter(".MP3") && AudioImport.NeedsConverter(".webm") && AudioImport.NeedsConverter(".mkv") &&
              !AudioImport.NeedsConverter(".wav") && !AudioImport.NeedsConverter(".scd") && !AudioImport.NeedsConverter(".txt") &&
              AudioImport.DirectExtensions.All(AudioImport.Extensions.Contains),
            "audio and video types other than wav, ogg and scd go through ffmpeg");
        var temp = Path.Combine(Path.GetTempPath(), "InstantEdit-SoundImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var mp3 = Path.Combine(temp, "Made Up Song.mp3");
            File.WriteAllBytes(mp3, [1, 2, 3]);
            string? message = null;
            try { AudioImport.FromFile(mp3, AudioFixture.Template(), 1f); }
            catch (InvalidDataException e) { message = e.Message; }
            check(message == AudioImport.NeedsTools, "an mp3 without the sound tools asks for them");
            try { AudioImport.FromFile(mp3, AudioFixture.Template(), 1f, Path.Combine(temp, "missing", "ffmpeg.exe")); message = null; }
            catch (InvalidDataException e) { message = e.Message; }
            check(message == AudioImport.NeedsTools, "an ffmpeg path that doesn't exist counts as missing tools");

            string? converted = null;
            var encoded = "SEDBSSCF"u8.ToArray().Concat(new byte[] { 7, 7 }).ToArray();
            var song = TimelineImports.Sound(mp3, 0.5f, (path, _) => { converted = path; return (encoded, 2f); });
            check(converted == mp3 && song.GamePath == $"sound/instant_edit/made_up_song_{TimelineImports.Hash8(encoded)}.scd" && song.Seconds == 2f,
                "the timeline's sound import sends an mp3 to the converter, named after the file");
            reject(() => TimelineImports.Sound(mp3, 1, null), "an mp3 needs the converter");
            var text = Path.Combine(temp, "notes.txt");
            File.WriteAllBytes(text, [1]);
            message = null;
            try { TimelineImports.Sound(text, 1, (_, _) => (encoded, 1)); }
            catch (InvalidDataException e) { message = e.Message; }
            check(message == "Pick an audio or video file.", "other file types are refused, asking for an audio or video file");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // ---- Tools install against a fake GitHub ------------------------------------------------

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool Offline { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            await Task.Delay(5, cancellationToken);
            if (Offline)
                throw new HttpRequestException("No such host is known.");
            return files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        return stream.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task Install(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEdit-MediaTools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ytDlp = Encoding.ASCII.GetBytes("MZ not really yt-dlp");
            var ffmpeg = Encoding.ASCII.GetBytes("MZ not really ffmpeg");
            var ffprobe = Encoding.ASCII.GetBytes("MZ not really ffprobe");
            const string top = "ffmpeg-n0.0-made-up-win64-gpl";
            var zip = Zip(("../../escaped/bin/ffmpeg.exe", [6, 6, 6]), ($"{top}/doc/ffmpeg.exe", [5]), ($"{top}/bin/ffmpeg.exe", ffmpeg),
                ($"{top}/bin/ffprobe.exe", ffprobe), ($"{top}/bin/ffplay.exe", [4]), ($"{top}/LICENSE.txt", [3]));
            Dictionary<string, byte[]> Files(string ytHash, string zipHash, byte[]? zipBytes = null) => new()
            {
                [MediaTools.YtDlpUrl] = ytDlp,
                [MediaTools.YtDlpChecksumsUrl] = Encoding.ASCII.GetBytes($"{Sha([1])}  yt-dlp_linux\n{ytHash}  yt-dlp.exe\n"),
                [MediaTools.FfmpegUrl] = zipBytes ?? zip,
                [MediaTools.FfmpegChecksumsUrl] = Encoding.ASCII.GetBytes($"{zipHash} *{MediaTools.FfmpegAsset}\n{Sha([2])}  other.zip\n"),
            };
            string[] Parts(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*.part") : [];

            // A good release installs the three tools and nothing else from the zip.
            var good = Path.Combine(root, "good", "tools");
            var github = new FakeGitHub(Files(Sha(ytDlp), Sha(zip)));
            var logs = new List<string>();
            using (var tools = new MediaTools(good, github, logs.Add))
            {
                check(!tools.Status.Installed && !tools.Installed, "nothing is installed in a new folder");
                var reports = new List<MediaToolsProgress>();
                await Task.WhenAll(tools.InstallAsync(new SyncProgress(reports), CancellationToken.None),
                    tools.InstallAsync(null, CancellationToken.None));
                check(tools.Installed && tools.Status is { Installed: true, YtDlpVersion: null } &&
                      File.ReadAllBytes(tools.YtDlpPath).SequenceEqual(ytDlp) && File.ReadAllBytes(tools.FfmpegPath).SequenceEqual(ffmpeg) &&
                      File.ReadAllBytes(tools.FfprobePath).SequenceEqual(ffprobe),
                    "a release that matches its checksums installs yt-dlp, ffmpeg and ffprobe");
                check(Directory.GetFiles(good).Select(Path.GetFileName).Order().SequenceEqual(["ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe"]) &&
                      !Directory.Exists(Path.Combine(root, "escaped")) && !Directory.Exists(Path.Combine(root, "good", "escaped")),
                    "only bin/ffmpeg.exe and bin/ffprobe.exe come out of the zip, and an entry naming ../ writes nowhere");
                check(github.Requests.Count(u => u == MediaTools.YtDlpUrl) == 1 && github.Requests.Count(u => u == MediaTools.FfmpegUrl) == 1,
                    "a second install waits for the first and then finds nothing to fetch");
                check(reports.Count > 0 && reports.Any(r => r.File == MediaTools.YtDlpAsset) && reports.Last().Received == zip.Length,
                    "downloads report their progress");
                // A running tool holds its file open: Remove leaves every tool in place.
                using (new FileStream(tools.FfmpegPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var refused = false;
                    try { tools.Remove(); }
                    catch (IOException) { refused = true; }
                    check(refused && File.Exists(tools.YtDlpPath) && File.Exists(tools.FfmpegPath) && File.Exists(tools.FfprobePath),
                        "Remove with a tool in use keeps all three");
                }
                tools.Remove();
                check(!tools.Installed && !Directory.Exists(good) && !tools.Status.Installed, "Remove deletes the tools and their folder");
            }

            // A yt-dlp that doesn't match leaves nothing, and ffmpeg isn't fetched after it.
            var badYt = Path.Combine(root, "bad-yt");
            github = new FakeGitHub(Files(Sha([9]), Sha(zip)));
            using (var tools = new MediaTools(badYt, github))
            {
                var error = await Failure(() => tools.InstallAsync(null, CancellationToken.None));
                check(error is InvalidDataException { Message: MediaTools.ChecksumMismatch } && !File.Exists(tools.YtDlpPath) && Parts(badYt).Length == 0 &&
                      !github.Requests.Contains(MediaTools.FfmpegUrl),
                    "a yt-dlp download that fails its checksum is deleted and nothing runs from it");
            }

            // A zip that doesn't match leaves no ffmpeg, no .part and no zip.
            var badZip = Path.Combine(root, "bad-zip");
            using (var tools = new MediaTools(badZip, new FakeGitHub(Files(Sha(ytDlp), Sha([9])))))
            {
                var error = await Failure(() => tools.InstallAsync(null, CancellationToken.None));
                check(error is InvalidDataException { Message: MediaTools.ChecksumMismatch } && File.Exists(tools.YtDlpPath) &&
                      !File.Exists(tools.FfmpegPath) && !File.Exists(tools.FfprobePath) && Directory.GetFiles(badZip).Length == 1,
                    "an ffmpeg zip that fails its checksum is deleted before anything is extracted");
            }

            // A zip without ffprobe installs neither exe.
            var noProbe = Zip(($"{top}/bin/ffmpeg.exe", ffmpeg));
            var half = Path.Combine(root, "half");
            using (var tools = new MediaTools(half, new FakeGitHub(Files(Sha(ytDlp), Sha(noProbe), noProbe))))
            {
                var error = await Failure(() => tools.InstallAsync(null, CancellationToken.None));
                check(error is InvalidDataException && !File.Exists(tools.FfmpegPath) && Parts(half).Length == 0,
                    "a zip without ffprobe leaves no half install");
            }

            // No network: one short line.
            var offline = Path.Combine(root, "offline");
            using (var tools = new MediaTools(offline, new FakeGitHub(Files(Sha(ytDlp), Sha(zip))) { Offline = true }))
            {
                var error = await Failure(() => tools.InstallAsync(null, CancellationToken.None));
                check(error is IOException { Message: MediaTools.DownloadFailed } && !File.Exists(tools.YtDlpPath),
                    "an unreachable GitHub reports that the tools couldn't be downloaded");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class SyncProgress(List<MediaToolsProgress> reports) : IProgress<MediaToolsProgress>
    {
        public void Report(MediaToolsProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }

    private static async Task<Exception?> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { return e; }
        return null;
    }

    // ---- Real conversions, when ffmpeg is on PATH -------------------------------------------

    private static string? FindFfmpeg() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => { try { return Path.Combine(folder.Trim('"'), "ffmpeg.exe"); } catch (ArgumentException) { return ""; } })
        .FirstOrDefault(File.Exists);

    private static async Task WithFfmpeg(Action<bool, string> check, Action<Action, string> reject)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            Console.WriteLine("ffmpeg is not on PATH: skipping the conversion scenarios.");
            check(true, "conversion scenarios skipped without ffmpeg");
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "InstantEdit-SoundConvert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var before = Directory.Exists(ToolProcess.WorkRoot) ? Directory.GetDirectories(ToolProcess.WorkRoot).ToHashSet() : [];
        try
        {
            var template = AudioFixture.Template();
            foreach (var (name, codec) in new[] { ("tone 'one' a.mp3", "libmp3lame"), ("tone two.m4a", "aac"), ("tone three.webm", "libopus") })
            {
                var file = Path.Combine(temp, name);
                var made = await ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                    "-c:a", codec, file], TimeSpan.FromMinutes(1), CancellationToken.None);
                if (made.ExitCode != 0)
                {
                    Console.WriteLine($"This ffmpeg can't write {codec}: skipping {Path.GetExtension(name)}.");
                    continue;
                }
                var (scd, seconds) = AudioImport.FromFile(file, template, 1f, ffmpeg);
                var info = ScdFile.Info(scd);
                var audio = ScdFile.Read(scd).Audio[0];
                check(ScdFile.IsScd(scd) && Math.Abs(seconds - 2) < 0.15 && info.SoundCount == 1 && Math.Abs(info.SoundSeconds[0] - 2) < 0.15 &&
                      audio is { Channels: 2, SampleRate: 48000 },
                    $"a 2-second {Path.GetExtension(name)} tone converts to a 48 kHz stereo SCD of about 2 seconds ({seconds:F2} s)");
                var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
                if (File.Exists(ffprobe))
                    check(await AudioConvert.DurationAsync(ffprobe, file, CancellationToken.None) is { } length && Math.Abs(length.TotalSeconds - 2) < 0.15,
                        $"ffprobe reads the {Path.GetExtension(name)} tone as about 2 seconds");
            }
            check(await AudioConvert.DurationAsync(Path.Combine(temp, "no-such-ffprobe.exe"), Path.Combine(temp, "x.mp3"), CancellationToken.None) == null,
                "a missing ffprobe gives no length instead of an error");

            var video = Path.Combine(temp, "silent video.mp4");
            var madeVideo = await ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=black:s=16x16:d=1",
                "-c:v", "mpeg4", video], TimeSpan.FromMinutes(1), CancellationToken.None);
            if (madeVideo.ExitCode == 0)
            {
                string? message = null;
                try { AudioImport.FromFile(video, template, 1f, ffmpeg); }
                catch (InvalidDataException e) { message = e.Message; }
                check(message == AudioConvert.NoAudio, "a video without sound says the file has no audio");
            }
            var broken = Path.Combine(temp, "broken.mp3");
            File.WriteAllBytes(broken, Encoding.ASCII.GetBytes("this is not audio at all, just text pretending"));
            string? brokenMessage = null;
            try { AudioImport.FromFile(broken, template, 1f, ffmpeg); }
            catch (InvalidDataException e) { brokenMessage = e.Message; }
            check(brokenMessage is { } m && m.StartsWith("The file couldn", StringComparison.Ordinal) && !m.Contains(temp, StringComparison.OrdinalIgnoreCase) &&
                  !m.Contains('\n'),
                $"a file ffmpeg can't read gives one line without its folder ({brokenMessage})");
            reject(() => AudioImport.FromFile(broken, template, 1f, ffmpeg), "a broken mp3 is refused");

            var endless = new[] { "-hide_banner", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440", "-f", "null", "-" };
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var error = await Failure(() => ToolProcess.RunAsync(ffmpeg, endless, TimeSpan.FromMinutes(1), cancel.Token));
                check(error is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(10), "cancelling stops a running tool");
            }
            var timedOut = await Failure(() => ToolProcess.RunAsync(ffmpeg, endless, TimeSpan.FromMilliseconds(300), CancellationToken.None));
            check(timedOut is TimeoutException, "a tool running past its timeout is stopped");

            var after = Directory.Exists(ToolProcess.WorkRoot) ? Directory.GetDirectories(ToolProcess.WorkRoot) : [];
            check(after.All(before.Contains), "conversions leave no work folders behind");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }
}
