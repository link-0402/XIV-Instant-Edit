namespace InstantEdit.Studio.Services.Project;

/// <summary>
/// The one lock around reading-then-writing a mod's meta.json and sidecar. The render thread
/// (descriptive edits, taking a file out, saving set-ups) and an update of the mod on a worker both
/// rewrite those files from what is on disk; holding this while doing so keeps either from writing
/// back a copy that misses the other's change. Held only for that read and write, never while files
/// are converted or copied.
/// </summary>
internal static class ModFiles
{
    public static readonly object Gate = new();
}
