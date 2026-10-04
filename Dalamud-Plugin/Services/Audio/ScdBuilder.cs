using System.Buffers.Binary;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Builds a one-shot SCD from audio by replacing the single audio entry of a vanilla template the
/// caller reads from game data. The template keeps its layout (a 3D point source), routing and
/// track; the builder writes the MS-ADPCM entry, makes the sound play once at the given volume,
/// and sets the two places that hold the length: the track's wait after KeyOn and the sound's
/// extra-block play length, both the coded length in milliseconds rounded up (as in 7,409 of the
/// 7,851 vanilla one-shots with this track shape). Dalamud-free.
/// </summary>
internal static class ScdBuilder
{
    /// <summary>
    /// The template: the smallest vanilla emote sound (bed_liedown_start) with one sound, track and
    /// audio entry, mono MS-ADPCM, no loop or marker, volume 1, and the track every 6,200 of the
    /// 8,283 single-entry vanilla SCDs use (Version, ReleaseRate, ModulationOff x3, Volume, Pitch,
    /// Panning, FrPanning, ChannelVolumeZeroOne, KeyOn, Interval, KeyOff, End).
    /// </summary>
    public const string TemplatePath = "sound/battle/etc/se_bt_etc_bedin_man_jump.scd";
    public const float MaxVolume = 1f;
    /// <summary>The play-length field inside a sound's extra block (version, reserve, size, then this).</summary>
    private const int ExtraPlayLength = 4;

    private sealed record Template(ScdFile File, ScdSound Sound, ScdAudio Audio, int Interval);

    /// <summary>Refuses a template <see cref="Build"/> cannot use, for example after a game patch changed it.</summary>
    public static void CheckTemplate(byte[] template) => _ = ReadTemplate(template);

    public static byte[] Build(byte[] templateScd, AudioClip clip, float volume)
    {
        if (!(volume >= 0 && volume <= MaxVolume))
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "The volume must be between 0 and 1.");
        var template = ReadTemplate(templateScd);
        var source = AudioResampler.ForScd(clip);
        var data = MsAdpcm.Encode(MsAdpcm.ToPcm16(source.Samples), source.Channels);
        var frames = (long)data.Length / MsAdpcm.BlockAlign(source.Channels) * MsAdpcm.SamplesPerBlock;
        var milliseconds = (int)Math.Ceiling(frames * 1000.0 / source.SampleRate);
        var format = MsAdpcm.FormatHeader(source.Channels, source.SampleRate);
        var at = template.Audio.Offset;
        var end = (long)at + ScdAudio.HeaderSize + format.Length + data.Length;
        var size = (end + 15) & ~15L;
        if (size > int.MaxValue)
            throw new InvalidDataException("The sound is too long for an SCD file.");
        var result = new byte[size];
        templateScd.AsSpan(0, at).CopyTo(result);
        var span = result.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[ScdFile.FileSizeField..], (int)size);
        // The audio entry: data length, channels, rate, codec, no loop, sub-info size, flags without a marker.
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], source.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 8)..], source.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 12)..], (int)ScdCodec.MsAdpcm);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 16)..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 20)..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 24)..], format.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 28)..], template.Audio.Flags & ~ScdAudio.MarkerFlag);
        format.CopyTo(span[(at + ScdAudio.HeaderSize)..]);
        data.CopyTo(span[(at + ScdAudio.HeaderSize + format.Length)..]);
        // The sound plays once at the chosen volume, for the new length.
        var sound = template.Sound;
        BinaryPrimitives.WriteInt32LittleEndian(span[(sound.Offset + 4)..], sound.Attributes & ~ScdSound.LoopAttribute);
        BinaryPrimitives.WriteSingleLittleEndian(span[(sound.Offset + ScdSound.VolumeField)..], volume);
        if (sound.ExtraOffset >= 0)
            BinaryPrimitives.WriteInt32LittleEndian(span[(sound.ExtraOffset + ExtraPlayLength)..], milliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(span[template.Interval..], milliseconds);
        var built = ScdFile.Read(result);
        if (built.Audio is not [{ Codec: ScdCodec.MsAdpcm } audio] || audio.DataLength != data.Length || audio.Channels != source.Channels ||
            built.Sounds is not [{ Loops: false } check] || check.Volume != volume)
            throw new InvalidOperationException("The built SCD did not read back as written.");
        return result;
    }

    private static Template ReadTemplate(byte[] bytes)
    {
        ScdFile file;
        try
        {
            file = ScdFile.Read(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException("The sound template could not be read: " + e.Message, e);
        }
        if (file.Sounds.Length != 1 || file.TrackOffsets.Length != 1 || file.Audio.Length != 1)
            throw new InvalidDataException("The sound template must have exactly one sound, track and audio entry.");
        var sound = file.Sounds[0];
        var audio = file.Audio[0];
        if (sound.Type != ScdSound.Normal || sound.Tracks is not [(0, 0)])
            throw new InvalidDataException("The sound template is not a plain one-shot sound.");
        // Everything before the audio entry is copied as is, so it must be the last entry.
        if (audio.Offset == 0 || file.TrackOffsets.Concat(file.LayoutOffsets).Concat(file.AttributeOffsets).Append(sound.Offset)
                .Append(file.TrackTableOffset).Append(file.AudioTableOffset).Any(o => o >= audio.Offset))
            throw new InvalidDataException("The sound template's audio entry is not the last entry.");
        var commands = ScdTrack.Read(bytes, file.TrackOffsets[0]);
        var keyOn = commands.Select(c => c.Type).ToList().IndexOf(ScdTrack.KeyOn);
        if (keyOn < 0 || commands.Length != keyOn + 4 || commands[keyOn + 1] is not { Type: ScdTrack.Interval } interval ||
            commands[keyOn + 2].Type != ScdTrack.KeyOff || commands[keyOn + 3].Type != ScdTrack.End ||
            commands.Any(c => c.Type is ScdTrack.LoopStart or ScdTrack.LoopEnd or ScdTrack.Jump))
            throw new InvalidDataException("The sound template's track is not KeyOn, wait, KeyOff.");
        if (interval.Next > audio.Offset)
            throw new InvalidDataException("The sound template's track overlaps its audio entry.");
        return new Template(file, sound, audio, interval.Data);
    }
}
