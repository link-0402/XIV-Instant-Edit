using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Turns a sound file the user picked into SCD bytes for a timeline sound event. WAV and Ogg
/// Vorbis are decoded and built on the vanilla template (<see cref="ScdBuilder.TemplatePath"/>,
/// which the caller reads from game data); an SCD passes through unchanged once it parses, so its
/// own volume stays. Seconds is the first sound's length, the one sound index 0 plays. Dalamud-free.
/// </summary>
internal static class AudioImport
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public static ImmutableArray<string> Extensions { get; } = [".wav", ".ogg", ".scd"];

    public static (byte[] Scd, float Seconds) FromFile(string path, byte[] template, float volume)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The sound file does not exist.", path);
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
                throw new InvalidDataException("Only .wav, .ogg and .scd sound files can be used.");
        }
        var info = ScdFile.Info(scd);
        if (info.SoundCount == 0)
            throw new InvalidDataException("The SCD file has no sounds.");
        return (scd, (float)info.SoundSeconds[0]);
    }
}
