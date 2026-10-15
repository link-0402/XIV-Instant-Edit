using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What the parts of the Timeline window share: the services, the clip that is open with its editor,
/// and the files imported into it. The window owns it and hands it to each part.
/// </summary>
internal sealed class TimelineSession
{
    private Func<bool> busy = () => false;
    private readonly object templateLock = new();
    private byte[]? scdTemplate;

    internal TimelineSession(AnimationEditService? animations, TimelineCatalog catalog, AnimationPreviewService preview,
        AnimationPlayback playback, MotionLiveDisplay? motionDisplay, IDataManager data, IFramework framework, IPluginLog log)
    {
        Animations = animations;
        Catalog = catalog;
        Preview = preview;
        Playback = playback;
        MotionDisplay = motionDisplay;
        Data = data;
        Framework = framework;
        Log = log;
    }

    public AnimationEditService? Animations { get; }
    public TimelineCatalog Catalog { get; }
    public AnimationPreviewService Preview { get; }
    public AnimationPlayback Playback { get; }
    /// <summary>Shows bone layers live on the GPose actor; null when animation editing is unavailable.</summary>
    public MotionLiveDisplay? MotionDisplay { get; }
    public IDataManager Data { get; }
    public IFramework Framework { get; }
    public IPluginLog Log { get; }
    public FileDialogManager FileDialog { get; } = new();

    /// <summary>The clip shown, null before one is read.</summary>
    public AnimationTimelineSource? Source { get; set; }
    /// <summary>The editor of the clip's document; kept from clip to clip.</summary>
    public TimelineEditor? Editor { get; set; }
    /// <summary>The catalog's lists for the clip's race; null for a clip not made for a player race.</summary>
    public TimelineCatalogKey? CatalogKey { get; set; }

    /// <summary>Files imported for the document, by game path: sounds, effects and the copies a fix or a look makes.</summary>
    public Dictionary<string, TimelineImport> Imports { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Whether the document must not change now: nothing to edit, or a save, undo or reload is running. </summary>
    public bool ReadOnly => Source?.Document == null || busy();

    /// <summary> What else keeps the document read-only: a read, save or undo running, set once the window has built its parts. </summary>
    public void KeepReadOnlyWhile(Func<bool> running) => busy = running;

    /// <summary> An imported file's own bytes by its game path, such as a sound added to a set. </summary>
    public byte[]? ImportBytes(string gamePath) =>
        Imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes) ? bytes : null;

    /// <summary>
    /// The source with the clip's latest skeleton match: choosing a skeleton under Source after
    /// opening the timeline must still allow a length change. Only a capture of the same file counts:
    /// the Live preview plays a copy under the same capture, and its match knows no choice. The
    /// capture gets the match too, since saves, face edits and reloads send it.
    /// </summary>
    public AnimationTimelineSource Fresh(AnimationTimelineSource current)
    {
        if (Animations == null)
            return current;
        foreach (var capture in Animations.Observer.History)
        {
            if (capture.Id != current.Capture.Id)
                continue;
            var clip = current.Startup ? capture.Startup : capture.Clip;
            if (clip != null && clip.GamePath == current.Clip.GamePath && clip.BindingFingerprint == current.Clip.BindingFingerprint &&
                clip.SourceContext == current.Clip.SourceContext)
                return ReferenceEquals(clip.Resolution, current.Clip.Resolution) ? current : WithResolution(current, clip.Resolution);
            break;
        }
        // The capture shows another file now (the preview copy), so a choice made under Source
        // reaches the timeline through the match kept for its own file.
        return Animations.Observer.LatestResolution(current.Clip) is { } latest && !ReferenceEquals(latest, current.Clip.Resolution)
            ? WithResolution(current, latest)
            : current;
    }

    /// <summary><paramref name="current"/> with its clip matched to <paramref name="resolution"/>, in its capture too.</summary>
    private static AnimationTimelineSource WithResolution(AnimationTimelineSource current, SkeletonResolution? resolution)
    {
        var clip = current.Clip with { Resolution = resolution };
        return current with { Clip = clip, Capture = current.Startup ? current.Capture with { Startup = clip } : current.Capture with { Clip = clip } };
    }

    /// <summary> A key for "the same clip", which survives a reload after a save. </summary>
    public static string ClipKey(AnimationTimelineSource current) => $"{current.Capture.Id}|{current.Startup}|{current.Clip.GamePath}";

    /// <summary> <see cref="ClipKey(AnimationTimelineSource)"/> for a capture's loop or startup. </summary>
    public static string ClipKey(AnimationCapture capture, bool startup) =>
        $"{capture.Id}|{startup}|{(startup ? capture.Startup : capture.Clip)?.GamePath}";

    public static string ErrorText(Exception? error)
    {
        var inner = error is AggregateException aggregate ? aggregate.InnerException ?? aggregate : error;
        return inner?.Message is { Length: > 0 } message ? message : "Something went wrong.";
    }

    /// <summary> The vanilla sound .wav and .ogg imports are built on, read once from game data off the draw path. </summary>
    public byte[] ScdTemplate()
    {
        lock (templateLock)
        {
            if (scdTemplate != null)
                return scdTemplate;
            byte[]? bytes = null;
            try { bytes = Data.GetFile(ScdBuilder.TemplatePath)?.Data; }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Warning(e, "Could not read the template sound for imports."); }
            if (bytes == null)
                throw new InvalidDataException("The game's template sound could not be read, so .wav and .ogg files can't be imported. An .scd still works.");
            try { ScdBuilder.CheckTemplate(bytes); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warning(e, "The template sound for imports changed.");
                throw new InvalidDataException("The game's template sound changed, so .wav and .ogg files can't be imported. An .scd still works.");
            }
            return scdTemplate = bytes;
        }
    }

    public bool GameFileExists(string gamePath)
    {
        try { return Data.FileExists(gamePath); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }
}
