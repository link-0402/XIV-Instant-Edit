using System.Collections.Immutable;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
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

    /// <summary>The one playhead over every workspace's clip that plays, shared by them all; null without a game.</summary>
    public TimelinePlayGroup? Group { get; init; }

    /// <summary>Reads whether the live character's skeleton and hand model have fingertip bones; set by the plugin, null without a game.</summary>
    public Func<ushort?, CancellationToken, Task<HandTipFacts>>? HandTips { get; set; }

    /// <summary>
    /// Reads the bones the game's physics moves on the GPose actor at an object index (null: unknown, as outside GPose); set by the
    /// plugin, null without a game.
    /// </summary>
    public Func<ushort?, CancellationToken, Task<ImmutableHashSet<string>?>>? ReadPhysicsBones { get; set; }

    /// <summary>
    /// The bones the game's physics moves on the workspace's actor, read for the open clip, and again after a redraw, by <see cref="KeepPhysicsBones"/>;
    /// null while unknown or when they can't be read, which leaves nothing out.
    /// </summary>
    public ImmutableHashSet<string>? PhysicsBones { get; private set; }

    // What PhysicsBones was read for (the clip, the actor and the redraws seen), the clip and actor it belongs to, and the read running.
    private string? physicsKey;
    private string? physicsPlace;
    private (string Key, Task<ImmutableHashSet<string>?> Task, CancellationTokenSource Cancel)? physicsRead;

    // Counts redraws: one can load other physics files (a physics mod switched on), so the next ask reads them again.
    private static int physicsRedraws;

    /// <summary>Called, on any thread, when Penumbra redrew a character: every workspace reads its physics bones again.</summary>
    public static void PhysicsMayHaveChanged() => Interlocked.Increment(ref physicsRedraws);

    /// <summary>
    /// Keeps <see cref="PhysicsBones"/> to the open clip and actor: takes a finished read, and starts one, off the draw, the first
    /// time it is asked for a clip or an actor it wasn't read for, or after a redraw. Cheap to call every frame.
    /// </summary>
    public void KeepPhysicsBones()
    {
        if (physicsRead is { Task.IsCompleted: true } done)
        {
            physicsRead = null;
            done.Cancel.Dispose();
            // A failure is read here so it isn't reported as unobserved; a read cancelled for another clip runs again on coming back.
            _ = done.Task.Exception;
            physicsKey = done.Task.IsCanceled ? null : done.Key;
            // A cancelled read keeps the bones there were: after a redraw they stand until the new read ends.
            if (!done.Task.IsCanceled)
                PhysicsBones = done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
        }
        if (Source is not { } source || ReadPhysicsBones is not { } read)
            return;
        var actor = ActorIndex;
        var place = $"{ClipKey(source)}|{actor}|";
        var key = place + Volatile.Read(ref physicsRedraws);
        if (physicsRead is { } running)
        {
            if (running.Key != key)
                running.Cancel.Cancel();
            return;
        }
        if (physicsKey == key)
            return;
        // Another clip or actor starts from nothing; a redraw keeps the old bones until the new ones are read.
        if (physicsPlace != place)
            PhysicsBones = null;
        physicsPlace = place;
        physicsKey = null;
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        physicsRead = (key, Task.Run(() => ReadLoaded(read, actor, token), token), cancel);
    }

    // Right after a redraw the character's files are still loading and the tree lists no physics file: an empty read is tried
    // again for a few seconds before it counts.
    private static async Task<ImmutableHashSet<string>?> ReadLoaded(Func<ushort?, CancellationToken, Task<ImmutableHashSet<string>?>> read,
        ushort? actor, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            var bones = await read(actor, token).ConfigureAwait(false);
            if (bones is not { IsEmpty: true } || attempt == 10)
                return bones;
            await Task.Delay(500, token).ConfigureAwait(false);
        }
    }

    // The faces' own lengths by expression library for the open clip's collection, race and face (packFramesKey), the
    // libraries asked for and not read yet, and the one read running.
    private readonly Dictionary<string, ImmutableDictionary<string, int>> packFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> packsAsked = [];
    private (string Pack, Task<ImmutableDictionary<string, int>> Task)? packRead;
    private (Guid Collection, string Race, string Face, string Clip)? packFramesKey;

    /// <summary>Moves on whenever <see cref="PackFrames"/> learns a library's lengths or starts over.</summary>
    public int PackFramesVersion { get; private set; }

    /// <summary>
    /// The own lengths of the faces in expression library <paramref name="pack"/> as the open clip's collection resolves it, by
    /// motion name: a mod's library may hold moving faces, which only their file knows the length of. Null while unknown, and for
    /// libraries the game has itself (the catalog lists those). Asking for one reads it soon (<see cref="KeepPackFrames"/>).
    /// </summary>
    public ImmutableDictionary<string, int>? PackFrames(string? pack)
    {
        if (pack == null || packFramesKey is not { } key)
            return null;
        if (packFrames.TryGetValue(pack, out var known))
            return known;
        if (!string.Equals(packRead?.Pack, pack, StringComparison.OrdinalIgnoreCase) && !packsAsked.Contains(pack, StringComparer.OrdinalIgnoreCase))
        {
            // Read once whether the game has it: a library it has needs no reading.
            if (GameFileExists(AnimationEditService.FacePackPath(key.Race, key.Face, pack)))
                packFrames[pack] = ImmutableDictionary<string, int>.Empty;
            else
                packsAsked.Add(pack);
        }
        return null;
    }

    /// <summary>
    /// Every frame: takes a finished library read and starts the next one asked for, one at a time so many libraries don't
    /// load at once. Starts over for another clip, collection, race or face; a library changed while the clip is open is
    /// read again only when it opens again.
    /// </summary>
    public void KeepPackFrames()
    {
        (Guid, string, string, string)? key = Source is { } source && CatalogKey is { } catalog
            ? (source.Capture.CollectionId, catalog.RaceCode, catalog.Face, ClipKey(source))
            : null;
        if (key != packFramesKey)
        {
            packFramesKey = key;
            packFrames.Clear();
            packsAsked.Clear();
            packRead = null;
            PackFramesVersion++;
        }
        if (packRead is { Task.IsCompleted: true } done)
        {
            packRead = null;
            if (done.Task.Exception is { } error)
                Log.Debug(error, $"Could not read the face lengths of {done.Pack}.");
            packFrames[done.Pack] = done.Task.IsCompletedSuccessfully ? done.Task.Result : ImmutableDictionary<string, int>.Empty;
            PackFramesVersion++;
        }
        if (packRead != null || packsAsked.Count == 0 || packFramesKey is not { } wanted || Animations is not { } service)
            return;
        var pack = packsAsked[0];
        packsAsked.RemoveAt(0);
        packRead = (pack, Task.Run(() => service.FacePackFramesAsync(wanted.Collection, wanted.Race, wanted.Face, pack, CancellationToken.None)));
    }

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
    /// <summary>Edits wait: no editable clip, a read or save running, or (in the play group) the shared frame past this clip's end.</summary>
    public bool ReadOnly => Busy || Editor?.PastEnd == true;

    /// <summary>No editable clip, or a read or save running. What stops Live and holds the autosave; past the end doesn't.</summary>
    public bool Busy => Source?.Document == null || busy();

    /// <summary> What else keeps the document read-only: a read, save or undo running, set once the window has built its parts. </summary>
    public void KeepReadOnlyWhile(Func<bool> running) => busy = running;

    /// <summary> An imported file's own bytes by its game path, such as a sound added to a set. </summary>
    public byte[]? ImportBytes(string gamePath) =>
        Imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes) ? bytes : null;

    /// <summary>The open clip's rebuild for the character's race, chosen in the save footer; null keeps its race.</summary>
    public RaceRebuild? Rebuild { get; set; }

    /// <summary>
    /// The partner tracks of the document's link layers, set by <see cref="PartnerLinks"/> on the framework update: the same
    /// instance while no track was added, replaced or removed, as the views and Live compare it by reference.
    /// </summary>
    public MotionLinkSet Links { get; set; } = MotionLinkSet.Empty;

    /// <summary>Each planned link layer's state, by layer key; replaced only when a state changed, so views can key on it.</summary>
    public ImmutableDictionary<string, LinkState> LinkStates { get; set; } = PartnerLinks.NoStates;

    /// <summary>A link track of this workspace is being built or waits to be: a save or Preview start waits for it.</summary>
    public bool LinksBuilding { get; set; }

    /// <summary>A link's partner as texts name it, by preset: "You" for null, the partner's name, null once its preset is gone. Set by the workspace.</summary>
    public Func<Guid?, string?> PartnerName { get; set; } = preset => preset == null ? "You" : null;

    /// <summary>The other workspaces whose link layers follow this clip, by actor name (<see cref="ITimelineHost.LinkedBy"/>). Set by the workspace.</summary>
    public Func<IReadOnlyList<string>> LinkedBy { get; set; } = () => [];

    /// <summary>The Link layer picker the Add menus open and the link panel asks for partners; set by the workspace.</summary>
    public LinkPicker? LinkPicker { get; set; }

    /// <summary>
    /// The content of the partner tracks the clip's file holds its active link layers baked with, by layer key (<see cref="LinkTrack.Print"/>):
    /// from the last save in this window, or the saved edit Edit again opened; empty before either.
    /// </summary>
    public ImmutableDictionary<string, string> SavedPrints { get; set; } = MotionLinks.NoPrints;

    /// <summary>The open document's link layers follow partner tracks of other content than its file holds (see <see cref="MotionLinks.Unsaved"/>).</summary>
    public bool LinksUnsaved => Editor is { } editor && MotionLinks.Unsaved(editor.Document.Motion, Links, SavedPrints);

    /// <summary>The prints <paramref name="journal"/>'s save baked its links with, for <see cref="SavedPrints"/> once Edit again opens it.</summary>
    public static ImmutableDictionary<string, string> SavedPrintsOf(AnimationEditJournal journal) =>
        journal.Request.Timeline?.LinkPrints is { Count: > 0 } prints ? prints : MotionLinks.NoPrints;

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

    /// <summary>
    /// <paramref name="capture"/> pointed at another actor: a respawned partner is a new object, and the save checks the actor the
    /// capture names. Its clip, Id and collection stay, so a preset whose collection changed is still refused.
    /// </summary>
    public static AnimationCapture Retargeted(AnimationCapture capture, ushort index, ulong actorId, long address) =>
        capture with { ActorIndex = index, ActorId = actorId, ActorAddress = address };

    /// <summary>The open clip, its expression's face too, points at the actor now at <paramref name="index"/> (a respawn).</summary>
    public void Retarget(ushort index, ulong actorId, long address)
    {
        if (Source is not { } current)
            return;
        var moved = current with { Capture = Retargeted(current.Capture, index, actorId, address) };
        if (moved.Face is { } face)
            moved = moved with { Face = face with { Capture = Retargeted(face.Capture, index, actorId, address) } };
        Source = moved;
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
