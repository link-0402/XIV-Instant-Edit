using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// What the effect check (<see cref="TimelineEffectCheck"/>) reads from the effect an Async VFX
/// entry starts, and the copy of it that keeps going.
/// </summary>
/// <param name="Loops">The frame ranges its timelines loop over.</param>
/// <param name="EndsAt">The frame its own schedule kills it at although it would keep going; null when it never ends itself.</param>
/// <param name="SoundSeconds">How long its longest sound that should loop but can't lasts; null when none (see <see cref="TimelineEffectFiles.UnloopedSeconds"/>).</param>
/// <param name="Looping">The copy without that kill and with those sounds looping; null when nothing needs one or it couldn't be made.</param>
internal sealed record TimelineEffectInfo(ImmutableArray<(int Start, int End)> Loops, int? EndsAt = null, double? SoundSeconds = null,
    TimelineImport? Looping = null);

/// <summary>
/// Reads and edits the parts of effect (AVFX) and sound (SCD) files that decide how long an effect
/// keeps going. Layouts follow VFXEditor: a scheduler (Schd) starts timelines (TmLn) at frames,
/// and a timeline item that plays a KILL clip ends the effect. Red Carpet's effects schedule their
/// own kill at frame 908, so they stop there even when the timeline keeps them looping; its music
/// is marked to loop but has no loop points, so it plays once.
/// </summary>
internal static class TimelineEffectFiles
{
    /// <summary>A clip's type, "KILL" stored as an int.</summary>
    private const int KillClip = 0x4B494C4C;

    /// <summary>
    /// The frame the effect's own schedule kills it at, when it would otherwise keep going (a
    /// scheduled timeline loops or has an item without an end); null when it never ends itself.
    /// One-shot effects that clean up after themselves are not counted.
    /// </summary>
    public static int? EndsAt(byte[] avfx)
    {
        var schedule = ReadSchedule(avfx);
        return schedule.KeepsGoing && schedule.Kills.Count > 0 ? schedule.Kills.Min(k => k.At) : null;
    }

    /// <summary>The effect with every schedule item that kills it at a later frame turned off. Its kill trigger still works.</summary>
    public static byte[] WithoutScheduledKills(byte[] avfx)
    {
        var schedule = ReadSchedule(avfx);
        var output = (byte[])avfx.Clone();
        foreach (var kill in schedule.Kills)
        {
            // The scheduler writes its items again at the start of every Item and Trgr chunk; turn off each copy.
            foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, kill.Scheduler.Start, kill.Scheduler.Start + kill.Scheduler.Size))
            {
                if (name is not ("Item" or "Trgr")) continue;
                var entries = Entries(avfx, start, size);
                if (kill.Index < Math.Min(entries.Count, kill.ItemCount) &&
                    entries[kill.Index] is var entry && entry.Get("StTm", 0) == kill.Start && entry.Get("TlNo", -1) == kill.Timeline &&
                    entry.Fields.TryGetValue("bEna", out var enabled))
                    output.AsSpan(enabled.At, enabled.Size).Clear();
            }
        }
        if (EndsAt(output) != null) throw new InvalidDataException("The effect still ends itself.");
        return output;
    }

    /// <summary>The effect with every emitter sound named <paramref name="from"/> renamed to <paramref name="to"/>.</summary>
    public static byte[] WithSound(byte[] avfx, string from, string to)
    {
        var length = RootLength(avfx);
        using var stream = new MemoryStream(avfx.Length + to.Length + 8);
        stream.Write(avfx, 0, 8);
        var renamed = 0;
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, 8 + length))
        {
            if (name != "Emit")
            {
                WriteChunk(stream, avfx.AsSpan(start - 8, 4), avfx.AsSpan(start, size));
                continue;
            }
            using var emitter = new MemoryStream(size + to.Length + 8);
            foreach (var (field, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            {
                if (field == "SdNm" && count > 0 && AnimationDependencies.ReadString(avfx, at, at + count).Equals(from, StringComparison.OrdinalIgnoreCase))
                {
                    WriteChunk(emitter, avfx.AsSpan(at - 8, 4), [.. Encoding.ASCII.GetBytes(to), 0]);
                    renamed++;
                }
                else WriteChunk(emitter, avfx.AsSpan(at - 8, 4), avfx.AsSpan(at, count));
            }
            WriteChunk(stream, avfx.AsSpan(start - 8, 4), emitter.ToArray());
        }
        var output = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length - 8);
        if (renamed == 0 || !AnimationDependencies.ReadVfxPaths(output).Sounds.Contains(to, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"The effect doesn't play {from}.");
        return output;
    }

    /// <summary>
    /// How long the longest sound of <paramref name="scd"/> lasts that is marked to loop but whose
    /// audio has no loop points, so it plays once, counting only sounds of at least
    /// <paramref name="minSeconds"/> (shorter ones are left alone, since the author never heard
    /// them loop); null when none. Only Vorbis and MS-ADPCM audio, whose loop units are known, count.
    /// </summary>
    public static double? UnloopedSeconds(byte[] scd, double minSeconds) =>
        Unlooped(ScdFile.Read(scd), minSeconds).Select(a => (double?)a.Seconds).Max();

    /// <summary>The sound file with loop points over the whole of each audio <see cref="UnloopedSeconds"/> counts.</summary>
    public static byte[] WithLoopPoints(byte[] scd, double minSeconds)
    {
        var file = ScdFile.Read(scd);
        var output = (byte[])scd.Clone();
        foreach (var audio in Unlooped(file, minSeconds))
        {
            // Vorbis loops by byte offsets into the stream, MS-ADPCM by sample (VFXEditor's ScdVorbis, ScdAdpcm).
            var end = audio.Codec == ScdCodec.Vorbis ? audio.DataLength : checked((int)Math.Round(audio.Seconds * audio.SampleRate));
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(audio.Offset + 16), 0);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(audio.Offset + 20), end);
        }
        if (UnloopedSeconds(output, minSeconds) != null) throw new InvalidDataException("The sound still plays once.");
        return output;
    }

    /// <summary>
    /// The copy of the effect at <paramref name="effectPath"/> that keeps going: its scheduled kill
    /// turned off when <paramref name="endsItself"/>, and each of <paramref name="sounds"/> (game
    /// path and file) replaced by a copy with loop points. Every copy gets its own content-named
    /// game path under instant_edit/ and is mapped beside the file it copies. Each copy can be made
    /// the same way from another file of its original (see <see cref="TimelineImport.Rebuild"/>):
    /// its kill turned off when it has one, the sounds it plays renamed to their looping copies.
    /// </summary>
    public static TimelineImport LoopingCopy(string effectPath, byte[] avfx, bool endsItself,
        IEnumerable<(string Path, byte[] Scd)> sounds, double minSeconds)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var beside = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var rebuild = ImmutableDictionary.CreateBuilder<string, Func<byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);
        var copy = endsItself ? WithoutScheduledKills(avfx) : avfx;
        var renamed = new List<(string From, string To)>();
        foreach (var (path, scd) in sounds)
        {
            var looped = WithLoopPoints(scd, minSeconds);
            var soundPath = TimelineImports.SoundPath(Path.GetFileNameWithoutExtension(path) + "_loop.scd", looped);
            files[soundPath] = looped;
            beside[soundPath] = path;
            rebuild[soundPath] = other => WithLoopPoints(other, minSeconds);
            copy = WithSound(copy, path, soundPath);
            renamed.Add((path, soundPath));
        }
        var gamePath = TimelineImports.EffectPath(Path.GetFileNameWithoutExtension(effectPath) + "_loop.avfx", copy);
        if (!AnimationDependencies.SafeGamePath(gamePath)) throw new InvalidDataException("The effect's name cannot be used in a game path.");
        files[gamePath] = copy;
        beside[gamePath] = effectPath;
        rebuild[gamePath] = other =>
        {
            var result = endsItself && EndsAt(other) != null ? WithoutScheduledKills(other) : other;
            var plays = AnimationDependencies.ReadVfxPaths(other).Sounds;
            foreach (var (from, to) in renamed)
                if (plays.Contains(from, StringComparer.OrdinalIgnoreCase))
                    result = WithSound(result, from, to);
            return result;
        };
        return new TimelineImport(gamePath, files.ToImmutable(), 0, beside.ToImmutable(), rebuild.ToImmutable());
    }

    private static IEnumerable<ScdAudio> Unlooped(ScdFile file, double minSeconds) =>
        file.Sounds.Where(s => s.Loops).SelectMany(s => s.Tracks).Select(t => t.Audio).Distinct()
            .Where(i => i >= 0 && i < file.Audio.Length).Select(i => file.Audio[i])
            .Where(a => a.Codec is ScdCodec.Vorbis or ScdCodec.MsAdpcm && a.DataLength > 0 && !a.Cut &&
                        a.LoopEnd <= a.LoopStart && a.Seconds >= minSeconds);

    /// <summary>A scheduler chunk's payload, how many items it has, and one of its items that kills the effect at <paramref name="At"/>.</summary>
    private sealed record Kill((int Start, int Size) Scheduler, int ItemCount, int Index, int Start, int Timeline, int At);

    private sealed record Schedule(List<Kill> Kills, bool KeepsGoing);

    /// <summary>A run of fields: an entry of a scheduler's or timeline's item list.</summary>
    private sealed class Entry
    {
        public Dictionary<string, (int Value, int At, int Size)> Fields { get; } = new(StringComparer.Ordinal);
        public int Get(string name, int fallback) => Fields.TryGetValue(name, out var field) ? field.Value : fallback;
    }

    private static Schedule ReadSchedule(byte[] avfx)
    {
        var length = RootLength(avfx);
        var timelines = new List<(bool KeepsGoing, int? KillAt)>();
        var schedulers = new List<(int Start, int Size)>();
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, 8 + length))
            if (name == "TmLn") timelines.Add(ReadTimeline(avfx, start, size));
            else if (name == "Schd") schedulers.Add((start, size));
        var kills = new List<Kill>();
        var keepsGoing = false;
        foreach (var scheduler in schedulers)
        {
            var count = 0;
            List<Entry> items = [];
            foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, scheduler.Start, scheduler.Start + scheduler.Size))
                if (name == "ItCn" && size >= 4) count = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(start));
                else if (name == "Item") items = Entries(avfx, start, size); // each Item chunk repeats the ones before; the last has them all
            for (var index = 0; index < Math.Min(count, items.Count); index++)
            {
                var item = items[index];
                var timeline = item.Get("TlNo", -1);
                if (item.Get("bEna", 1) == 0 || timeline < 0 || timeline >= timelines.Count) continue;
                var at = item.Get("StTm", 0);
                if (timelines[timeline].KillAt is { } killAt && at + killAt > 0) kills.Add(new Kill(scheduler, count, index, at, timeline, at + killAt));
                else if (timelines[timeline].KeepsGoing) keepsGoing = true;
            }
        }
        return new Schedule(kills, keepsGoing);
    }

    /// <summary>Whether a timeline keeps going (it loops, or an item has no end), and the first frame it plays a KILL clip.</summary>
    private static (bool KeepsGoing, int? KillAt) ReadTimeline(byte[] avfx, int start, int size)
    {
        int loopStart = 0, loopEnd = 0;
        List<Entry> items = [];
        var clips = new List<int>();
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
        {
            if (name == "LpSt" && count >= 4) loopStart = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at));
            else if (name == "LpEd" && count >= 4) loopEnd = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at));
            else if (name == "Item") items = Entries(avfx, at, count);
            else if (name == "Clip") clips.Add(count >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)) : 0);
        }
        var enabled = items.Where(i => i.Get("bEna", 1) != 0).ToList();
        var keepsGoing = loopEnd > loopStart && loopStart >= 0 || enabled.Any(i => i.Get("ClNo", -1) < 0 && i.Get("EdTm", 0) == -1);
        var kills = enabled.Where(i => i.Get("ClNo", -1) is var clip && clip >= 0 && clip < clips.Count && clips[clip] == KillClip)
            .Select(i => (int?)i.Get("StTm", 0)).Min();
        return (keepsGoing, kills);
    }

    /// <summary>The entries of an item list: a new entry starts whenever a field comes again.</summary>
    private static List<Entry> Entries(byte[] avfx, int start, int size)
    {
        var entries = new List<Entry>();
        Entry? current = null;
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
        {
            if (current == null || current.Fields.ContainsKey(name)) entries.Add(current = new Entry());
            var value = count >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)) : count >= 1 ? avfx[at] : 0;
            current.Fields[name] = (value, at, Math.Min(count, 4));
        }
        return entries;
    }

    private static int RootLength(byte[] avfx)
    {
        if (avfx.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("The effect exceeds the file size limit.");
        if (avfx.Length < 8 || !avfx.AsSpan(0, 4).SequenceEqual("XFVA"u8)) throw new InvalidDataException("Invalid AVFX header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(4));
        if (length < 0 || length != avfx.Length - 8) throw new InvalidDataException("Invalid AVFX length.");
        return length;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        name.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], payload.Length);
        stream.Write(header);
        stream.Write(payload);
        for (var pad = (-payload.Length) & 3; pad > 0; pad--) stream.WriteByte(0);
    }
}
