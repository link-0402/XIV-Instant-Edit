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
/// and a timeline item that plays a KILL clip ends the effect. One dance mod's effects schedule their
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

    public const string StopBeforeStart = "Stops at is before the effect starts";
    public const string NothingScheduled = "This effect has nothing scheduled to stop";
    public const string NoRoomForKill = "This effect can't be stopped early";
    public const string LoopsUntil = "This effect loops: Stops at must be by frame ";

    /// <summary>The first frame the effect's schedule kills it at, whether or not it would end anyway; null when it never does.</summary>
    internal static int? KillsAt(byte[] avfx) => ReadSchedule(avfx).Kills is { Count: > 0 } kills ? kills.Min(k => k.At) : null;

    /// <summary>
    /// The effect with its schedule killing it at <paramref name="stop"/> frames after it starts, by a KILL
    /// at a local frame its timeline plays (see <see cref="Reaches"/>). Moving a KILL beats adding one:
    /// a KILL on a timeline of its own (all it plays are kills, as one dance mod's at 908) moves by its
    /// scheduler item's start, in every Item and Trgr copy; another moves by its own start, in every Item
    /// container, when its scheduler item starts by the stop. Without a KILL the schedule reaches, one
    /// reached only while its item or scheduler entry is off is turned on in every copy and moved the
    /// same way; without that either, the first timeline scheduled by the stop that plays the frame gets
    /// a KILL clip and an Item container holding its items and one playing it. A kill that can't be
    /// moved there is past the stop already and stays, so no start goes below 0. Throws
    /// <see cref="InvalidDataException"/> with <see cref="StopBeforeStart"/> when nothing scheduled
    /// (besides timelines of kills) starts by the stop, <see cref="NothingScheduled"/>, <see cref="NoRoomForKill"/> when every
    /// timeline that could take a KILL has an item naming the clip it would be, or <see cref="LoopsUntil"/>
    /// and the last frame that works when those timelines loop before the stop.
    /// </summary>
    public static byte[] WithKill(byte[] avfx, int stop)
    {
        var (timelines, schedulers) = ReadBlocks(avfx);
        // The effect starts with its first scheduled timeline that plays more than kills; a stop before that is refused, whatever moves.
        var starts = schedulers.SelectMany(s => s.Items.Take(s.Count))
            .Where(e => e.Get("bEna", 1) != 0 && e.Get("TlNo", -1) is var number && number >= 0 && number < timelines.Count && !KillsOnly(timelines[number]))
            .Select(e => e.Get("StTm", 0)).ToList();
        if (starts.Count > 0 && stop < starts.Min()) throw new InvalidDataException(StopBeforeStart);
        var output = (byte[])avfx.Clone();
        var on = new List<ScheduledKill>();
        var off = new List<ScheduledKill>();
        foreach (var scheduler in schedulers)
            for (var index = 0; index < Math.Min(scheduler.Count, scheduler.Items.Count); index++)
            {
                var entry = scheduler.Items[index];
                var number = entry.Get("TlNo", -1);
                if (number < 0 || number >= timelines.Count)
                    continue;
                var timeline = timelines[number];
                var kills = Enumerable.Range(0, timeline.Items.Count).Where(i => Kills(timeline, timeline.Items[i])).ToList();
                // A timeline of kills moves by its scheduler item, so one of them (the first to play) does.
                if (KillsOnly(timeline) && kills.Count > 0)
                    kills = [kills.OrderBy(i => timeline.Items[i].Get("bEna", 1) == 0).ThenBy(i => timeline.Items[i].Get("StTm", 0)).First()];
                foreach (var item in kills)
                {
                    var kill = new ScheduledKill(scheduler, index, entry.Get("StTm", 0), timeline, item);
                    if (Movable(kill, stop))
                        (entry.Get("bEna", 1) != 0 && timeline.Items[item].Get("bEna", 1) != 0 ? on : off).Add(kill);
                }
            }
        if (on.Count > 0)
            foreach (var kill in on)
                Move(avfx, output, kill, stop, turnOn: false);
        else if (off.Count > 0)
            Move(avfx, output, off[0], stop, turnOn: true);
        else
            output = AddKill(avfx, output, timelines, schedulers, stop);
        if (KillsAt(output) != stop) throw new InvalidDataException("Stops at could not be set.");
        return output;
    }

    /// <summary>
    /// Whether a timeline plays its local frame <paramref name="frame"/>: one that doesn't loop plays
    /// every frame, a looping one only those before its loop end (LpEd), since it goes back to its loop
    /// start there. The mod library has no KILL on a looping timeline at all (124 of 124 sampled are on
    /// timelines that don't loop), so nothing says a later one fires.
    /// </summary>
    private static bool Reaches(TimelineBlock timeline, int frame) =>
        frame >= 0 && (!Loops(timeline) || frame < timeline.Fields.Get("LpEd", 0));

    private static bool Loops(TimelineBlock timeline) =>
        timeline.Fields.Get("LpEd", 0) > timeline.Fields.Get("LpSt", 0) && timeline.Fields.Get("LpSt", 0) >= 0;

    /// <summary>Whether every item a timeline plays is a kill, so moving its scheduler item moves nothing else.</summary>
    private static bool KillsOnly(TimelineBlock timeline) => timeline.Items.All(i => i.Get("bEna", 1) == 0 || Kills(timeline, i));

    /// <summary>
    /// Whether a scheduled kill can be put at <paramref name="stop"/>: on a timeline of kills, by a
    /// scheduler start of 0 or later; else by a local start its timeline plays, with its scheduler item
    /// starting by the stop.
    /// </summary>
    private static bool Movable(ScheduledKill kill, int stop)
    {
        var local = kill.Timeline.Items[kill.Item].Get("StTm", 0);
        return KillsOnly(kill.Timeline)
            ? stop - local >= 0 && Reaches(kill.Timeline, local)
            : kill.Start <= stop && Reaches(kill.Timeline, stop - kill.Start);
    }

    /// <summary>A scheduled kill put at <paramref name="stop"/> (see <see cref="Movable"/>), its scheduler item and KILL item turned on too when asked.</summary>
    private static void Move(byte[] avfx, byte[] output, ScheduledKill kill, int stop, bool turnOn)
    {
        if (KillsOnly(kill.Timeline))
        {
            WriteScheduled(avfx, output, kill.Scheduler, kill.Index, stop - kill.Timeline.Items[kill.Item].Get("StTm", 0), turnOn);
            if (turnOn)
                MoveKill(avfx, output, kill, null, turnOn: true);
        }
        else
        {
            if (turnOn)
                WriteScheduled(avfx, output, kill.Scheduler, kill.Index, null, turnOn: true);
            MoveKill(avfx, output, kill, stop - kill.Start, turnOn);
        }
    }

    /// <summary>A scheduler item (by its index) that starts a timeline at <paramref name="Start"/>, and that timeline's KILL item.</summary>
    private sealed record ScheduledKill(SchedulerBlock Scheduler, int Index, int Start, TimelineBlock Timeline, int Item);

    /// <summary>A scheduler chunk: how many items it has, the items of its last Item chunk, and every chunk that repeats them (Item and Trgr).</summary>
    private sealed record SchedulerBlock(int Count, List<Entry> Items, List<(int Start, int Size)> Copies);

    /// <summary>
    /// A timeline chunk's payload: its Item containers, the items of the last, its clips' types, where its
    /// first Clip chunk starts (null without one), and its own fields (TICn, CpCn).
    /// </summary>
    private sealed record TimelineBlock(int Start, int Size, List<(int Start, int Size)> Containers, List<Entry> Items, List<int> Clips,
        int? FirstClip, Entry Fields);

    private static (List<TimelineBlock> Timelines, List<SchedulerBlock> Schedulers) ReadBlocks(byte[] avfx)
    {
        var length = RootLength(avfx);
        var timelines = new List<TimelineBlock>();
        var schedulers = new List<SchedulerBlock>();
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, 8 + length))
            if (name == "TmLn")
            {
                var containers = new List<(int Start, int Size)>();
                var clips = new List<int>();
                int? firstClip = null;
                var fields = new Entry();
                foreach (var (field, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
                    if (field == "Item") containers.Add((at, count));
                    else if (field == "Clip")
                    {
                        firstClip ??= at - 8;
                        clips.Add(count >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)) : 0);
                    }
                    else if (count >= 4) fields.Fields[field] = (BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)), at, 4);
                var items = containers.Count > 0 ? Entries(avfx, containers[^1].Start, containers[^1].Size) : [];
                timelines.Add(new TimelineBlock(start, size, containers, items, clips, firstClip, fields));
            }
            else if (name == "Schd")
            {
                var count = 0;
                List<Entry> items = [];
                var copies = new List<(int Start, int Size)>();
                foreach (var (field, at, bytes) in AnimationDependencies.Chunks(avfx, start, start + size))
                    if (field == "ItCn" && bytes >= 4) count = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at));
                    else if (field is "Item" or "Trgr")
                    {
                        copies.Add((at, bytes));
                        if (field == "Item") items = Entries(avfx, at, bytes);
                    }
                schedulers.Add(new SchedulerBlock(count, items, copies));
            }
        return (timelines, schedulers);
    }

    private static bool Kills(TimelineBlock timeline, Entry item) =>
        item.Get("ClNo", -1) is var clip && clip >= 0 && clip < timeline.Clips.Count && timeline.Clips[clip] == KillClip;

    /// <summary>A KILL item's local start set to <paramref name="local"/> (null keeps it), in every Item container that holds it; turned on too when asked.</summary>
    private static void MoveKill(byte[] avfx, byte[] output, ScheduledKill kill, int? local, bool turnOn)
    {
        var last = kill.Timeline.Items[kill.Item];
        foreach (var (start, size) in kill.Timeline.Containers)
        {
            var entries = Entries(avfx, start, size);
            if (kill.Item >= entries.Count)
                continue;
            var copy = entries[kill.Item];
            if (copy.Get("ClNo", -1) != last.Get("ClNo", -1) || copy.Get("StTm", 0) != last.Get("StTm", 0))
                continue;
            if (local is { } frame)
                WriteStart(output, copy, frame);
            if (turnOn && copy.Fields.TryGetValue("bEna", out var enabled))
                TurnOn(output, enabled);
        }
    }

    /// <summary>A scheduler item's start set to <paramref name="start"/> (null keeps it) in every Item and Trgr chunk that repeats it; turned on too when asked.</summary>
    private static void WriteScheduled(byte[] avfx, byte[] output, SchedulerBlock scheduler, int index, int? start, bool turnOn)
    {
        var item = scheduler.Items[index];
        foreach (var (at, size) in scheduler.Copies)
        {
            var entries = Entries(avfx, at, size);
            if (index >= Math.Min(entries.Count, scheduler.Count) || entries[index] is not { } entry ||
                entry.Get("StTm", 0) != item.Get("StTm", 0) || entry.Get("TlNo", -1) != item.Get("TlNo", -1))
                continue;
            if (start is { } frame)
                WriteStart(output, entry, frame);
            if (turnOn && entry.Fields.TryGetValue("bEna", out var enabled))
                TurnOn(output, enabled);
        }
    }

    private static void WriteStart(byte[] output, Entry entry, int frame)
    {
        if (!entry.Fields.TryGetValue("StTm", out var time) || time.Size < 4)
            throw new InvalidDataException("Stops at could not be set.");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(time.At), frame);
    }

    private static void TurnOn(byte[] output, (int Value, int At, int Size) field)
    {
        output.AsSpan(field.At, field.Size).Clear();
        output[field.At] = 1;
    }

    /// <summary>
    /// The effect with a KILL clip added to the timeline scheduled first (the first of those starting
    /// together) that plays the stop's local frame and has no playing item naming the new clip's index, and an Item container after its
    /// last one holding its items and one that plays the clip at <paramref name="stop"/>; its item and
    /// clip counts and the sizes around them grow.
    /// </summary>
    private static byte[] AddKill(byte[] avfx, byte[] output, List<TimelineBlock> timelines, List<SchedulerBlock> schedulers, int stop)
    {
        var scheduled = schedulers.SelectMany(s => s.Items.Take(s.Count))
            .Where(e => e.Get("bEna", 1) != 0 && e.Get("TlNo", -1) is var number && number >= 0 && number < timelines.Count)
            .OrderBy(e => e.Get("StTm", 0)).ToList();
        if (scheduled.Count == 0) throw new InvalidDataException(NothingScheduled);
        if (stop < scheduled[0].Get("StTm", 0)) throw new InvalidDataException(StopBeforeStart);
        // An item may name a clip its timeline lacks; the new clip must not be the one an item that plays names, or that item would kill the effect.
        var roomy = scheduled.Where(e => e.Get("StTm", 0) <= stop && timelines[e.Get("TlNo", -1)] is var candidate &&
                                         candidate.Items.All(i => i.Get("bEna", 1) == 0 || i.Get("ClNo", -1) != candidate.Clips.Count)).ToList();
        if (roomy.Count == 0) throw new InvalidDataException(NoRoomForKill);
        // A looping timeline goes back before its loop end, so the kill must come before it.
        var first = roomy.FirstOrDefault(e => Reaches(timelines[e.Get("TlNo", -1)], stop - e.Get("StTm", 0)))
                    ?? throw new InvalidDataException(LoopsUntil + roomy.Max(e => e.Get("StTm", 0) + timelines[e.Get("TlNo", -1)].Fields.Get("LpEd", 0) - 1));
        var start = first.Get("StTm", 0);
        var timeline = timelines[first.Get("TlNo", -1)];
        if (!timeline.Fields.Fields.TryGetValue("TICn", out var itemCount) || !timeline.Fields.Fields.TryGetValue("CpCn", out var clipCount))
            throw new InvalidDataException("The effect's timeline has no item or clip count.");
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(itemCount.At), timeline.Items.Count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(clipCount.At), timeline.Clips.Count + 1);
        // The new item's fields as library items that name a clip have them (bEna is 4 bytes; a KILL ends a frame after it starts),
        // after the items before it in a new container.
        var at = stop - start;
        byte[] item =
        [
            .. TimelineEffectLooks.Leaf("bEna", 1), .. TimelineEffectLooks.Leaf("StTm", at), .. TimelineEffectLooks.Leaf("EdTm", at + 1),
            .. TimelineEffectLooks.Leaf("BdNo", -1), .. TimelineEffectLooks.Leaf("EfNo", -1), .. TimelineEffectLooks.Leaf("EmNo", -1),
            .. TimelineEffectLooks.Leaf("Plfm", 0), .. TimelineEffectLooks.Leaf("ClNo", timeline.Clips.Count),
        ];
        var last = timeline.Containers.Count > 0 ? timeline.Containers[^1] : ((int Start, int Size)?)null;
        byte[] items = last is { } found ? [.. avfx.AsSpan(found.Start, found.Size), .. item] : item;
        var itemAt = last is { } previous ? previous.Start + previous.Size + (-previous.Size & 3) : timeline.FirstClip ?? timeline.Start + timeline.Size;
        int[] sizes = [4, timeline.Start - 4];
        return TimelineEffectLooks.Grown(output,
        [
            new TimelineEffectLooks.Addition(itemAt, TimelineEffectLooks.Block("Item", items), sizes),
            new TimelineEffectLooks.Addition(timeline.Start + timeline.Size, TimelineEffectLooks.Block("Clip", KillPayload()), sizes),
        ]);
    }

    /// <summary>A KILL clip: its type, then ints (0, 0, 0, 1) (no fade, hide), floats (-1, 0, 0, 0) as most library KILLs have, and 128 zero bytes.</summary>
    private static byte[] KillPayload()
    {
        var payload = new byte[164];
        BinaryPrimitives.WriteInt32LittleEndian(payload, KillClip);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 1);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(20), -1);
        return payload;
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
