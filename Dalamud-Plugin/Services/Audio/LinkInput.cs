using System.Globalization;

namespace InstantEdit.Services.Audio;

/// <summary>
/// A checked link import: the normalized watch link, its video id and the part to download.
/// <paramref name="ToEnd"/> means End was left empty, so <paramref name="End"/> is the cap.
/// </summary>
internal sealed record LinkRequest(string Url, string VideoId, TimeSpan Start, TimeSpan End, bool ToEnd);

/// <summary>
/// The result of <see cref="LinkInput.Parse"/>: a request, or a short reason to show under the fields.
/// <paramref name="LinkStart"/> is the link's own t= time, for the popup to put in an empty Start.
/// </summary>
internal readonly record struct LinkParse(LinkRequest? Request, string? Error, TimeSpan? LinkStart)
{
    public bool Ok => Request != null;
}

/// <summary>
/// Reads what the user typed in the link popup. Cheap and exception-free, so the popup can call it
/// every frame: YouTube watch, shorts and live links (playlist parameters dropped), a t= start, and
/// start and end times capped at <see cref="MaxLength"/>. Dalamud-free.
/// </summary>
internal static class LinkInput
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);
    private const int IdLength = 11;
    // Keeps times far inside TimeSpan's range.
    private const double MaxSeconds = 100 * 3600;

    public const string NeedLink = "Paste a YouTube link.";
    public const string OnlyYouTube = "Only YouTube links can be used.";
    public const string NoSpotify = "Spotify links can't be used.";
    public const string NoVideo = "This link has no video.";
    public const string BadStart = "Start isn't a time.";
    public const string BadEnd = "End isn't a time.";
    public const string EndBeforeStart = "End must be after Start.";
    public const string TooLong = "Clips can be up to 10 minutes.";

    public static LinkParse Parse(string? url, string? startText, string? endText)
    {
        var (id, linkStart, error) = ReadLink(url?.Trim() ?? "");
        if (error != null)
            return new LinkParse(null, error, linkStart);

        TimeSpan start;
        if (string.IsNullOrWhiteSpace(startText))
            start = linkStart ?? TimeSpan.Zero;
        else if (!TryParseTime(startText, out start))
            return new LinkParse(null, BadStart, linkStart);

        TimeSpan end;
        var toEnd = string.IsNullOrWhiteSpace(endText);
        if (toEnd)
            end = start + MaxLength;
        else if (!TryParseTime(endText!, out end))
            return new LinkParse(null, BadEnd, linkStart);
        else if (end <= start)
            return new LinkParse(null, EndBeforeStart, linkStart);
        else if (end - start > MaxLength)
            return new LinkParse(null, TooLong, linkStart);

        return new LinkParse(new LinkRequest("https://www.youtube.com/watch?v=" + id, id!, start, end, toEnd), null, linkStart);
    }

    private static (string? Id, TimeSpan? Start, string? Error) ReadLink(string text)
    {
        if (text.Length == 0)
            return (null, null, NeedLink);
        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return (null, null, NoSpotify);
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (null, null, OnlyYouTube);
        var host = uri.Host.ToLowerInvariant().TrimEnd('.');
        if (host is "spotify.com" or "spotify.link" || host.EndsWith(".spotify.com", StringComparison.Ordinal) ||
            host.EndsWith(".spotify.link", StringComparison.Ordinal))
            return (null, null, NoSpotify);
        if (uri.Scheme is not ("http" or "https"))
            return (null, null, OnlyYouTube);

        var query = Parameters(uri.Query);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id;
        switch (host)
        {
            case "youtu.be":
                id = segments.Length > 0 ? segments[0] : null;
                break;
            case "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com":
                if (segments.Length == 1 && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
                    id = Value(query, "v");
                else if (segments.Length >= 2 && (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
                                                  segments[0].Equals("live", StringComparison.OrdinalIgnoreCase)))
                    id = segments[1];
                else
                    id = null;
                break;
            default:
                return (null, null, OnlyYouTube);
        }
        // t= in the query, or in the fragment the way some share links put it.
        var linkStart = TryParseLinkTime(Value(query, "t") ?? Value(Parameters(uri.Fragment), "t"), out var t) ? t : (TimeSpan?)null;
        if (id == null || !IsVideoId(id))
            return (null, linkStart, NoVideo);
        return (id, linkStart, null);
    }

    private static bool IsVideoId(string id) =>
        id.Length == IdLength && id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private static List<(string Key, string Value)> Parameters(string text)
    {
        var list = new List<(string, string)>();
        foreach (var pair in text.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            list.Add((Uri.UnescapeDataString(key), Uri.UnescapeDataString(value.Replace('+', ' '))));
        }
        return list;
    }

    private static string? Value(List<(string Key, string Value)> parameters, string key) =>
        parameters.FirstOrDefault(p => p.Key == key).Value is { Length: > 0 } value ? value : null;

    /// <summary>A link's t= value: 90, 90s, 1m30s or 1h2m3s.</summary>
    public static bool TryParseLinkTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text))
            return false;
        var plain = text.EndsWith('s') ? text[..^1] : text;
        if (plain.Length > 0 && plain.All(char.IsAsciiDigit))
            return Seconds(plain, out time);
        double total = 0;
        var number = 0L;
        var digits = 0;
        var last = int.MaxValue;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiDigit(c))
            {
                if (++digits > 6)
                    return false;
                number = number * 10 + (c - '0');
                continue;
            }
            var (order, scale) = c switch { 'h' => (2, 3600), 'm' => (1, 60), 's' => (0, 1), _ => (-1, 0) };
            // Units come once each, largest first, each after a number.
            if (order < 0 || order >= last || digits == 0)
                return false;
            total += number * (double)scale;
            last = order;
            number = 0;
            digits = 0;
        }
        if (digits > 0 || total > MaxSeconds)
            return false;
        time = TimeSpan.FromSeconds(total);
        return true;
    }

    /// <summary>A typed time: seconds (90, 90.5), m:ss (1:02.5) or h:mm:ss, decimals on the seconds only.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts[^1].Length == 0)
            return false;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            return false;
        if (parts.Length > 1 && seconds >= 60)
            return false;
        double total = seconds;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 6 || !part.All(char.IsAsciiDigit))
                return false;
            var value = int.Parse(part, CultureInfo.InvariantCulture);
            // The minutes of h:mm:ss stay under an hour.
            if (parts.Length == 3 && i == 1 && value >= 60)
                return false;
            total += value * (parts.Length - 1 - i == 2 ? 3600.0 : 60.0);
        }
        if (total > MaxSeconds)
            return false;
        time = TimeSpan.FromSeconds(total);
        return true;
    }

    private static bool Seconds(string digits, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (digits.Length > 6)
            return false;
        time = TimeSpan.FromSeconds(int.Parse(digits, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>A time the way the popup shows it: m:ss or h:mm:ss, with decimals when it has them.</summary>
    public static string Format(TimeSpan time)
    {
        var seconds = (time.TotalSeconds % 60).ToString("00.###", CultureInfo.InvariantCulture);
        var minutes = (long)time.TotalMinutes;
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00}:{seconds}" : $"{minutes}:{seconds}";
    }
}
