using System.Collections.Concurrent;
using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.Animations.Timeline;

internal enum TimelineCatalogState : byte { Idle, Loading, Ready, Failed }

/// <summary>
/// What the timeline editor offers to place, built in the background for the player's race, face
/// and the clip's source mod, and kept per key for the session. Draw code only reads the latest
/// published lists; nothing here does IO on the caller's thread.
/// </summary>
internal sealed class TimelineCatalog : IDisposable
{
    private const int MaxCatalogs = 4;
    private const int MaxSeconds = 4096;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource life = new();
    private readonly object sync = new();
    private readonly Dictionary<TimelineCatalogKey, TimelineCatalogData> built = [];
    private readonly List<TimelineCatalogKey> recent = [];
    private readonly ConcurrentDictionary<string, float?> seconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> measuring = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<TimelineCatalogSheets> sheets;
    private CancellationTokenSource? build;
    private TimelineCatalogKey? key;
    private volatile TimelineCatalogData current = TimelineCatalogData.Empty;
    private volatile TimelineCatalogState state;
    private volatile string status = "";

    /// <param name="soundSeconds">Reads an SCD's length in seconds, null when it can't tell; may also be set later.</param>
    /// <param name="clipSeconds">Reads a PAP's clip lengths in seconds by binding index (through the game's Havok); may also be set later.</param>
    public TimelineCatalog(IDataManager data, IPluginLog log, Func<byte[], float?>? soundSeconds = null,
        Func<byte[], CancellationToken, IReadOnlyDictionary<int, float>?>? clipSeconds = null)
    {
        this.data = data;
        this.log = log;
        // Not cached on failure, so a later build tries the sheets again.
        sheets = new(ReadSheets, LazyThreadSafetyMode.PublicationOnly);
        SoundSeconds = soundSeconds;
        ClipSeconds = clipSeconds;
    }

    /// <summary>The SCD length reader the audio code provides; sound lengths stay unknown without it.</summary>
    public Func<byte[], float?>? SoundSeconds { get; set; }

    /// <summary>The PAP clip length reader; lip shape and blink lengths stay unknown without it.</summary>
    public Func<byte[], CancellationToken, IReadOnlyDictionary<int, float>?>? ClipSeconds { get; set; }

    public TimelineCatalogState State => state;

    /// <summary>A short plain line about the catalog, such as why it failed; empty when ready.</summary>
    public string Status => status;

    /// <summary>What the current lists were built for; null before the first request.</summary>
    public TimelineCatalogKey? Key { get { lock (sync) return key; } }

    /// <summary>The lists for <see cref="Key"/>; empty until they are ready.</summary>
    public TimelineCatalogData Data => current;

    /// <summary>
    /// Makes <paramref name="next"/> the current catalog: published at once when it was built
    /// before, otherwise built in the background. Cheap to call every frame with the same key.
    /// </summary>
    public void Request(TimelineCatalogKey next)
    {
        lock (sync)
        {
            if (next.Equals(key)) return;
            key = next;
            if (built.TryGetValue(next, out var known))
            {
                Touch(next);
                build?.Cancel();
                build = null;
                Publish(known, TimelineCatalogState.Ready, "");
                return;
            }
            Start(next);
        }
    }

    /// <summary>Builds the current catalog again, for example after the source mod changed.</summary>
    public void Rebuild()
    {
        lock (sync)
        {
            if (key == null) return;
            built.Remove(key);
            recent.Remove(key);
            Start(key);
        }
    }

    /// <summary>
    /// A sound's length in seconds for drawing its clip, or null while it is unknown. The first ask
    /// reads the file in the background (the source mod's file when it maps the path, else the
    /// game's); later asks return the result.
    /// </summary>
    public float? Seconds(string gamePath)
    {
        if (seconds.TryGetValue(KnownKey(gamePath), out var imported)) return imported;
        // Until the lists are ready the source mod's files are unknown, and a game file read now
        // would stand for a mod's sound for good.
        var lists = current;
        string? root;
        lock (sync) root = state == TimelineCatalogState.Ready ? key?.ModRoot ?? "" : null;
        if (root == null) return null;
        // A path's length depends on which mod provides it, so it is cached per source mod.
        var cacheKey = root + "|" + gamePath;
        if (seconds.TryGetValue(cacheKey, out var known)) return known;
        var reader = SoundSeconds;
        if (reader == null || !AnimationDependencies.SafeGamePath(gamePath) || !measuring.TryAdd(cacheKey, 0)) return null;
        var mod = lists.ModFiles.GetValueOrDefault(gamePath);
        _ = Task.Run(() =>
        {
            float? result = null;
            try
            {
                var bytes = mod != null && File.Exists(mod) ? File.ReadAllBytes(mod) : data.GetFile(gamePath)?.Data;
                if (bytes != null && TimelineImports.IsScd(bytes) && reader(bytes) is { } value && float.IsFinite(value) && value >= 0) result = value;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { log.Debug(e, $"Could not read the length of {gamePath}."); }
            Remember(cacheKey, result);
            measuring.TryRemove(cacheKey, out _);
        }, life.Token);
        return null;
    }

    /// <summary>Records a length already known, such as an imported sound's.</summary>
    public void RememberSeconds(string gamePath, float value)
    {
        if (float.IsFinite(value) && value >= 0) Remember(KnownKey(gamePath), value);
    }

    // Imported sounds have their content's hash in the path, so their length holds for every mod.
    private static string KnownKey(string gamePath) => "|known|" + gamePath;

    private void Remember(string gamePath, float? value)
    {
        if (seconds.Count >= MaxSeconds) seconds.Clear();
        seconds[gamePath] = value;
    }

    private void Touch(TimelineCatalogKey used)
    {
        recent.Remove(used);
        recent.Add(used);
        while (recent.Count > MaxCatalogs)
        {
            built.Remove(recent[0]);
            recent.RemoveAt(0);
        }
    }

    private void Publish(TimelineCatalogData value, TimelineCatalogState next, string text)
    {
        current = value;
        status = text;
        state = next;
    }

    // Called under the lock.
    private void Start(TimelineCatalogKey target)
    {
        build?.Cancel();
        var token = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        build = token;
        Publish(TimelineCatalogData.Empty, TimelineCatalogState.Loading, "Finding what can be placed");
        var clips = ClipSeconds;
        _ = Task.Run(() =>
        {
            TimelineCatalogData? result = null;
            string error = "";
            try
            {
                Func<byte[], IReadOnlyDictionary<int, float>?>? lengths = clips == null ? null : bytes =>
                {
                    try { return clips(bytes, token.Token); }
                    catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                    {
                        log.Debug(e, "Could not read the face animation lengths.");
                        return null;
                    }
                };
                result = new TimelineCatalogBuilder(Exists, Read, lengths).Build(sheets.Value, target, token.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                log.Warning(e, "Could not build the timeline catalog.");
                error = "Could not build the lists: " + e.Message;
            }
            lock (sync)
            {
                if (ReferenceEquals(build, token))
                {
                    build = null;
                    if (result != null)
                    {
                        built[target] = result;
                        Touch(target);
                        Publish(result, TimelineCatalogState.Ready, "");
                    }
                    else if (!token.IsCancellationRequested) Publish(TimelineCatalogData.Empty, TimelineCatalogState.Failed, error);
                }
            }
            token.Dispose();
        });
    }

    private bool Exists(string gamePath)
    {
        try { return data.FileExists(gamePath); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    private byte[]? Read(string gamePath)
    {
        try { return data.GetFile(gamePath)?.Data; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Debug(e, $"Could not read the game file {gamePath}.");
            return null;
        }
    }

    /// <summary>The sheet rows the catalog needs, read once on the first build.</summary>
    private TimelineCatalogSheets ReadSheets()
    {
        var emotes = ImmutableArray.CreateBuilder<TimelineCatalogEmote>();
        foreach (var emote in data.GetExcelSheet<Emote>())
        {
            var timelines = emote.ActionTimeline.Where(t => t.RowId != 0 && t.IsValid && !t.Value.Key.IsEmpty)
                .Select(t => new TimelineCatalogTimeline(t.Value.Key.ExtractText(), t.Value.LoadType)).Distinct().ToImmutableArray();
            if (timelines.Length > 0) emotes.Add(new TimelineCatalogEmote(emote.Name.ExtractText(), timelines));
        }
        var facial = data.GetExcelSheet<ActionTimeline>().Where(t => t.LoadType == 0 && !t.Key.IsEmpty)
            .Select(t => t.Key.ExtractText()).Where(k => k.StartsWith("facial/pose/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        var locations = data.GetExcelSheet<VFX>().Select(v => v.Location.ExtractText()).Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        // Weapons and tools: Item ModelMain/ModelSub pack the model, body and variant as 16-bit values.
        var items = ImmutableArray.CreateBuilder<TimelineCatalogItemModel>();
        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.EquipSlotCategory.RowId == 0 || !item.EquipSlotCategory.IsValid) continue;
            var slots = item.EquipSlotCategory.Value;
            if (slots.MainHand != 1 && slots.OffHand != 1) continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var model in new[] { item.ModelMain, item.ModelSub })
                if ((ushort)model != 0)
                    items.Add(new TimelineCatalogItemModel(name, (ushort)model, (ushort)(model >> 16), (ushort)(model >> 32)));
        }
        var weapons = data.GetExcelSheet<WeaponTimeline>().Where(w => w.RowId is > 0 and <= ushort.MaxValue)
            .Select(w => new TimelineWeaponTimeline((ushort)w.RowId, w.File.ExtractText())).Where(w => w.Key.Length > 0).ToImmutableArray();
        return new TimelineCatalogSheets(emotes.ToImmutable(), facial, locations, items.ToImmutable(), weapons);
    }

    public void Dispose()
    {
        life.Cancel();
        lock (sync) build = null;
    }
}
