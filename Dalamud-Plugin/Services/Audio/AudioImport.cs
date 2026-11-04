using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Turns a sound file the user picked into SCD bytes for a timeline sound event. WAV and Ogg
/// Vorbis are decoded and built on the vanilla template (<see cref="ScdBuilder.TemplatePath"/>,
/// which the caller reads from game data); an SCD passes through unchanged once it parses, so its
/// own volume stays. Any other audio or video file goes through ffmpeg to Ogg first. Seconds is the
/// first sound's length, the one sound index 0 plays. Dalamud-free.
/// </summary>
internal static class AudioImport
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const string NeedsTools = "Download the sound tools in Settings to use this file type.";

    /// <summary>The files read directly; the rest of <see cref="Extensions"/> need ffmpeg.</summary>
    public static ImmutableArray<string> DirectExtensions { get; } = [".wav", ".ogg", ".scd"];

    public static ImmutableArray<string> Extensions { get; } =
    [
        ".wav", ".ogg", ".scd",
        ".mp3", ".m4a", ".aac", ".flac", ".opus", ".wma", ".aif", ".aiff", ".mka",
        ".webm", ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv",
    ];

    /// <summary>Whether <paramref name="extension"/> (with its dot) is a file type that converts through ffmpeg.</summary>
    public static bool NeedsConverter(string extension) =>
        Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && !DirectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <param name="ffmpegPath">ffmpeg, for the file types that convert through it; without it those are refused.</param>
    /// <remarks>Blocks while ffmpeg runs: call it off the framework thread, as the timeline's import task does.</remarks>
    public static (byte[] Scd, float Seconds) FromFile(string path, byte[] template, float volume, string? ffmpegPath = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The sound file does not exist.", path);
        if (NeedsConverter(file.Extension))
        {
            // No input limit: a large video still makes a small Ogg; the converted size is checked instead.
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new InvalidDataException(NeedsTools);
            var work = ToolProcess.NewWorkFolder();
            try
            {
                var ogg = AudioConvert.ToOggAsync(ffmpegPath, file.FullName, null, null, work, cancellationToken).GetAwaiter().GetResult();
                return FromBytes(".ogg", ogg, template, volume);
            }
            finally
            {
                ToolProcess.DeleteWorkFolder(work);
            }
        }
        if (file.Length > MaxFileBytes)
            throw new InvalidDataException("Sound files over 256 MiB are not supported.");
        return FromBytes(file.Extension, File.ReadAllBytes(path), template, volume);
    }

    /// <summary>The same as <see cref="FromFile"/> for bytes already read, picked by the file extension.</summary>
    public static (byte[] Scd, float Seconds) FromBytes(string extension, byte[] bytes, byte[] template, float volume)
    {
        if (bytes.Length > MaxFileBytes)
            throw new InvalidDataException("Sound files over 256 MiB are not supported.");
        byte[] scd;
        switch (extension.ToLowerInvariant())
        {
            case ".wav" or ".wave":
                scd = ScdBuilder.Build(template, WavReader.Read(bytes), volume);
                break;
            case ".ogg" or ".oga":
                scd = ScdBuilder.Build(template, OggReader.Read(bytes), volume);
                break;
            case ".scd":
                if (!ScdFile.IsScd(bytes))
                    throw new InvalidDataException("This is not an SCD sound file.");
                scd = bytes;
                break;
            default:
                throw new InvalidDataException(NeedsConverter(extension) ? NeedsTools : "Pick an audio or video file.");
        }
        var info = ScdFile.Info(scd);
        if (info.SoundCount == 0)
            throw new InvalidDataException("The SCD file has no sounds.");
        return (scd, (float)info.SoundSeconds[0]);
    }
}
