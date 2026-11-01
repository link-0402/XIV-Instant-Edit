namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Builds each Timeline workspace's services the first time it is asked for them, through the plugin's builder, and
/// disposes a partner's (playback, Live, preview) when its preset goes or the plugin unloads. Your workspace's are the
/// plugin's own and stay its to dispose.
/// </summary>
internal sealed class WorkspaceServiceFactory(Func<WorkspaceKey, WorkspaceServices> build) : IDisposable
{
    private readonly Dictionary<WorkspaceKey, WorkspaceServices> built = [];

    public WorkspaceServices For(WorkspaceKey key)
    {
        if (!built.TryGetValue(key, out var services))
            built[key] = services = build(key);
        return services;
    }

    /// <summary>Disposes a partner workspace's services: Live puts its motion back, playback its actor, the preview removes its mod.</summary>
    public void Release(WorkspaceKey key)
    {
        if (key.IsSelf || !built.Remove(key, out var services))
            return;
        services.MotionDisplay?.Dispose();
        services.Playback.Dispose();
        services.Preview.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in built.Keys.Where(key => !key.IsSelf).ToArray())
            Release(key);
    }
}
