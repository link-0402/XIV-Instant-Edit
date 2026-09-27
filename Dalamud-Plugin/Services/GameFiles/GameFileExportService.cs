using Dalamud.Plugin.Services;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.GameFiles;

/// <summary>
/// Runs one Game Files export at a time in the background: the files from the game's data, and
/// decoded skeletons read by Havok on the game's framework thread, one skeleton per frame. Also
/// cleans up old exports in the cache folder, never while an export runs.
/// </summary>
internal sealed class GameFileExportService(GameFileBrowserService browser, ModelSkeletonResolver? skeletons, IPluginLog log,
    string pluginVersion) : IDisposable
{
    private readonly CancellationTokenSource life = new();
    private CancellationTokenSource? running;
    private GameExportProgress? progress;
    private GameExportResult? lastResult;

    /// <summary>Raised on a background thread when an export ends, however it ends.</summary>
    public event Action<GameExportResult>? Finished;

    public bool Busy => Volatile.Read(ref running) != null;

    public GameExportProgress? Progress => Volatile.Read(ref progress);

    public GameExportResult? LastResult => Volatile.Read(ref lastResult);

    /// <summary>Whether decoded skeletons can be written: they need the game's Havok.</summary>
    public bool CanDecodeSkeletons => skeletons != null;

    /// <summary>Starts exporting <paramref name="models"/> to <paramref name="root"/>; false while another export runs.</summary>
    public bool Start(IReadOnlyList<GameModelEntry> models, string root, GameExportOptions options)
    {
        var export = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        if (Interlocked.CompareExchange(ref running, export, null) != null)
        {
            export.Dispose();
            return false;
        }
        Volatile.Write(ref progress, new GameExportProgress("Files", 0, models.Count, ""));
        var source = new GameExportSource(browser.Read, browser.Exists, browser.SkeletonFiles.For,
            skeletons == null ? null : DecodeAsync, id => browser.Names.For(id), id => browser.Names.PlayerSelectable(id),
            pluginVersion, browser.GameVersion);
        _ = Task.Run(async () =>
        {
            GameExportResult result;
            try
            {
                result = await new GameFileExporter(source).RunAsync(models, root, options,
                    update => Volatile.Write(ref progress, update), export.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.Error(e, "The Game Files export failed.");
                result = new GameExportResult(GameExportResult.Failed, root, null, 0, 0, 0, [], e.Message);
            }
            Volatile.Write(ref lastResult, result);
            Volatile.Write(ref progress, null);
            Volatile.Write(ref running, null);
            export.Dispose();
            try { Finished?.Invoke(result); }
            catch (Exception e) { log.Warning(e, "Could not report the Game Files export result."); }
        });
        return true;
    }

    /// <summary>
    /// Automatic cleanup of the exports in the cache folder <paramref name="cacheRoot"/>: removes the
    /// files no export has written for <see cref="GameExportPaths.CacheRetention"/>. Skipped while an
    /// export runs (the next cleanup gets them), and blocks exports while it runs. Throws when the
    /// folder isn't a valid Instant Edit cache.
    /// </summary>
    public (int Files, long Bytes) CleanCache(string cacheRoot)
    {
        var folder = GameExportPaths.CacheExportFolder(cacheRoot);
        if (!Directory.Exists(folder))
            return (0, 0);
        TextureFiles.ValidateCache(cacheRoot);
        TextureFiles.EnsureLocalPath(folder);
        var cleanup = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        if (Interlocked.CompareExchange(ref running, cleanup, null) != null)
        {
            cleanup.Dispose();
            return (0, 0);
        }
        try
        {
            return GameExportPaths.Clean(folder, DateTime.UtcNow - GameExportPaths.CacheRetention, cleanup.Token);
        }
        finally
        {
            Volatile.Write(ref running, null);
            cleanup.Dispose();
        }
    }

    public void Cancel()
    {
        try { Volatile.Read(ref running)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<(ModelSkeletonPayload?, string?)> DecodeAsync(string modelPath, CancellationToken token)
    {
        // No object index and no character: the game's own skeleton files and EST tables.
        var result = await skeletons!.ResolveAsync(modelPath, -1, 0, token).ConfigureAwait(false);
        if (result.Skeleton == null)
            return (null, result.Problem);
        try { return (result.Skeleton.ToPayload(), null); }
        catch (InvalidDataException e) { return (null, e.Message); }
    }

    // Cancels a running export without waiting: its skeleton reads wait on framework ticks.
    public void Dispose() => life.Cancel();
}
