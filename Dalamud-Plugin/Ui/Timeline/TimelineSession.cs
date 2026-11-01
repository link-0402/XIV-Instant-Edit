using System.Collections.Immutable;
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
        LoopPose = new LoopPoseReader(this);
    }

    /// <summary>The workspace's key in ids and file names: "self", or a partner preset's Id.</summary>
    public string WorkspaceId { get; init; } = "self";

    /// <summary>The workspace's actor as texts name it: "You", or the partner's name.</summary>
    public Func<string> ActorName { get; init; } = () => "You";

    /// <summary>The game paths every workspace's preview shows, shared by them all; null without a game.</summary>
    public PreviewFiles? Files { get; init; }

    /// <summary>Reads whether the live character's skeleton and hand model have fingertip bones; set by the plugin, null without a game.</summary>
    public Func<CancellationToken, Task<HandTipFacts>>? HandTips { get; set; }

    /// <summary>The open startup's loop read for Match startup to loop, shared by the Add menus and the layer panel.</summary>
    public LoopPoseReader LoopPose { get; }

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
    /// <summary>Whether the playhead followed the game's play of the clip last frame (Preview, Live or Follow playing).</summary>
    public bool PlayheadFollows { get; set; }

    /// <summary>Files imported for the document, by game path: sounds, effects and the copies a fix or a look makes.</summary>
    public Dictionary<string, TimelineImport> Imports { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ushort, AtchTable?> attachTables = [];

    /// <summary>The game's own attach table of a race (101 for c0101), read once; null when the game has none or it can't be read.</summary>
    public AtchTable? AttachTable(ushort race)
    {
        if (!attachTables.TryGetValue(race, out var table))
        {
            try { table = Data.GetFile(AtchTable.GamePath(race))?.Data is { } bytes ? AtchTable.Parse(bytes) : null; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Log.Warning(e, $"Could not read the attach table of c{race:D4}.");
                table = null;
            }
            attachTables[race] = table;
        }
        return table;
    }

    /// <summary> Whether the document must not change now: nothing to edit, or a save, undo or reload is running. </summary>
    public bool ReadOnly => Source?.Document == null || busy();

    /// <summary> What else keeps the document read-only: a read, save or undo running, set once the window has built its parts. </summary>
    public void KeepReadOnlyWhile(Func<bool> running) => busy = running;

    /// <summary> An imported file's own bytes by its game path, such as a sound added to a set. </summary>
    public byte[]? ImportBytes(string gamePath) =>
        Imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes) ? bytes : null;

    /// <summary>The open clip's rebuild for the character's race, chosen in the save footer; null keeps its race.</summary>
    public RaceRebuild? Rebuild { get; set; }

    /// <summary>
    /// The skeleton <paramref name="loaded"/>'s motion is edited on: its rebuild's, else the one it was made for;
    /// null while it is unknown.
    /// </summary>
    public SkeletonDescription? EditingSkeleton(AnimationTimelineSource loaded)
    {
        var fresh = Fresh(loaded);
        return fresh.Rebuild?.Target.Skeleton ?? fresh.Clip.Resolution?.Selected?.Skeleton;
    }

    /// <summary>The open clip's <see cref="EditingSkeleton(AnimationTimelineSource)"/>; null while nothing is open.</summary>
    public SkeletonDescription? EditingSkeleton() => Source is { } source ? EditingSkeleton(source) : null;

    /// <summary>
    /// The source with the clip's latest skeleton match: choosing a skeleton under Source after
    /// opening the timeline must still allow a length change. Only a capture of the same file counts:
    /// the Live preview plays a copy under the same capture, and its match knows no choice. The
    /// capture gets the match too, since saves, face edits and reloads send it. With a motion from Blender in
    /// the document, that motion is the source's base motion; with a <see cref="Rebuild"/>, the source carries it.
    /// </summary>
    public AnimationTimelineSource Fresh(AnimationTimelineSource current)
    {
        // A motion from Blender in the document is the motion everything edits, previews and saves. The
        // same source, motion and rebuild give the same instance, so per-frame callers don't churn.
        var matched = Matched(current);
        var replacement = Editor?.Document.Replacement;
        var rebuild = Rebuild;
        if (replacement == null && rebuild == null)
            return matched;
        if (freshCache is { } cached && ReferenceEquals(cached.Source, matched) && ReferenceEquals(cached.Replacement, replacement) &&
            cached.Rebuild == rebuild)
            return cached.Result;
        var result = matched.WithReplacement(replacement) with { Rebuild = rebuild };
        freshCache = (matched, replacement, rebuild, result);
        return result;
    }

    private (AnimationTimelineSource Source, MotionReplacement? Replacement, RaceRebuild? Rebuild, AnimationTimelineSource Result)? freshCache;

    /// <summary>
    /// Every frame: keeps the open clip's latest skeleton match in <see cref="Source"/>. A motion from Blender
    /// is never kept there: it is the document's, so undo takes it away, and <see cref="Fresh"/> adds it.
    /// </summary>
    public void KeepMatch()
    {
        if (Source is { } current)
            Source = Matched(current);
    }

    /// <summary><paramref name="current"/> with the clip's latest skeleton match, without the document's motion from Blender (see <see cref="Fresh"/>).</summary>
    public AnimationTimelineSource Matched(AnimationTimelineSource current)
    {
        var resolution = LatestMatch(current.Capture.Id, current.Clip, current.Startup);
        return ReferenceEquals(resolution, current.Clip.Resolution) ? current : WithResolution(current, resolution);
    }

    /// <summary>
    /// <paramref name="current"/>'s capture with its loop's latest skeleton match, to read the loop while its startup
    /// is open: the capture kept with the startup holds the loop as it was when the startup opened.
    /// </summary>
    public AnimationCapture WithLoopMatch(AnimationTimelineSource current)
    {
        var loop = current.Capture.Clip;
        var resolution = LatestMatch(current.Capture.Id, loop, false);
        return ReferenceEquals(resolution, loop.Resolution) ? current.Capture : current.Capture with { Clip = loop with { Resolution = resolution } };
    }

    /// <summary>The object index of the workspace's actor in GPose; null outside it or when its actor isn't there.</summary>
    public ushort? ActorIndex
    {
        get
        {
            try { return Playback.ActorIndex(); }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>
    /// The listener's recent clips of this workspace's actor: as they come while the listener follows it, as they were
    /// while it follows another; outside GPose your own character's.
    /// </summary>
    public ImmutableArray<AnimationCapture> History => Animations is not { } service ? []
        : ActorIndex is { } index ? service.Observer.HistoryAt(index) : service.Observer.History;

    /// <summary>The listener's latest skeleton match for <paramref name="clip"/>, the loop or startup of capture <paramref name="captureId"/>; its own when there is none.</summary>
    private SkeletonResolution? LatestMatch(string captureId, AnimationClip clip, bool startup)
    {
        if (Animations == null)
            return clip.Resolution;
        foreach (var capture in History)
        {
            if (capture.Id != captureId)
                continue;
            var latest = startup ? capture.Startup : capture.Clip;
            if (latest != null && latest.GamePath == clip.GamePath && latest.BindingFingerprint == clip.BindingFingerprint &&
                latest.SourceContext == clip.SourceContext)
                return latest.Resolution;
            break;
        }
        // The capture shows another file now (the preview copy), so a choice made under Source
        // reaches the timeline through the match kept for its own file.
        return Animations.Observer.LatestResolution(clip) ?? clip.Resolution;
    }

    /// <summary>
    /// <paramref name="current"/> with its clip matched to <paramref name="resolution"/>, in its capture too, and in the face
    /// an expression plays: that face is the expression's clip.
    /// </summary>
    private static AnimationTimelineSource WithResolution(AnimationTimelineSource current, SkeletonResolution? resolution)
    {
        var clip = current.Clip with { Resolution = resolution };
        var face = current.Face is { } played ? played with { Clip = played.Clip with { Resolution = resolution },
            Capture = played.Capture with { Clip = played.Capture.Clip with { Resolution = resolution } } } : null;
        return current with
        {
            Clip = clip, Capture = current.Startup ? current.Capture with { Startup = clip } : current.Capture with { Clip = clip }, Face = face,
        };
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
