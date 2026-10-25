using System.Collections.Concurrent;
using System.Text.Json;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>
/// How the game hands a startup over to its loop, as measured on the character played: the loop starts at its
/// frame 0 this many timeline frames before the startup's end and cross-fades in until then, so the startup's last
/// frame shows with the loop's frame of that number. The length is the game's own, per startup and loop timeline
/// (10 on Red Ranger Pose A, 20 on Ground sit pose 1, the same with or without mods), and no file or sheet holds
/// it. Kept in the config folder by timeline keys (<see cref="KeyOf"/>), which sibling rows of one startup share.
/// </summary>
internal sealed class LoopFades
{
    /// <summary>The fade a match uses for a startup never played since: the commonest one measured.</summary>
    public const int Default = 10;
    /// <summary>The longest fade a match takes.</summary>
    public const int Most = 60;

    private const string FileName = "LoopFades.json";
    private readonly string? path;
    private readonly Action<string>? warn;
    private readonly ConcurrentDictionary<string, int> fades = new(StringComparer.Ordinal);
    private Task saving = Task.CompletedTask;

    /// <summary>A timeline's catalog key, which names it in the fades; null, or none set, names it by its id.</summary>
    internal Func<ushort, string?>? KeyOf { get; set; }

    /// <summary>The latest save, done once it has written.</summary>
    internal Task Saving => saving;

    /// <param name="configDirectory">Where the fades are kept; null keeps them in memory only.</param>
    public LoopFades(string? configDirectory, Action<string>? warn = null)
    {
        this.warn = warn;
        if (configDirectory == null)
            return;
        path = Path.Combine(configDirectory, FileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllBytes(path)) is { } saved)
                foreach (var (key, frames) in saved)
                    if (frames is >= 1 and <= Most)
                        fades[key] = frames;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            warn?.Invoke("The measured loop fades could not be read: " + e.Message);
        }
    }

    /// <summary>The fade measured from <paramref name="startup"/> into <paramref name="loop"/>, or null when it never played.</summary>
    public int? Measured(ushort startup, ushort loop) => fades.TryGetValue(Key(startup, loop), out var frames) ? frames : null;

    /// <summary>Keeps a measured fade, saved when it changed. Out of range values are dropped.</summary>
    public void Record(ushort startup, ushort loop, int frames)
    {
        if (frames is < 1 or > Most || startup == 0 || loop == 0)
            return;
        var key = Key(startup, loop);
        if (fades.TryGetValue(key, out var known) && known == frames)
            return;
        fades[key] = frames;
        if (path == null)
            return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<string, int>(fades, StringComparer.Ordinal));
        // Off the framework thread, one save after another so the newest lands last.
        saving = saving.ContinueWith(_ =>
        {
            try { AnimationJournalStore.WriteAtomic(path, bytes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke("A measured loop fade could not be saved: " + e.Message);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The timeline frames a startup <paramref name="duration"/> seconds long has left at <paramref name="time"/>, its
    /// time when the loop starts; null when the numbers can't be a hand-off.
    /// </summary>
    internal static int? FramesLeft(float duration, float time)
    {
        if (!float.IsFinite(duration) || !float.IsFinite(time) || time < 0 || time >= duration)
            return null;
        var frames = (int)MathF.Round((duration - time) * TimelineDocument.Fps);
        return frames is >= 1 and <= Most ? frames : null;
    }

    // Ids saved before keys were used never match a keyed timeline again; they stay in the file unused.
    private string Key(ushort startup, ushort loop) => (KeyOf?.Invoke(startup) ?? startup.ToString()) + ">" + (KeyOf?.Invoke(loop) ?? loop.ToString());
}
