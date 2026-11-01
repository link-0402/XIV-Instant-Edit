using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>Which workspace: your character's, or a partner's by its preset.</summary>
internal readonly record struct WorkspaceKey(Guid? Preset)
{
    public static WorkspaceKey Self => default;

    public bool IsSelf => Preset == null;

    /// <summary>The key in ImGui ids and file names: "self", or the preset's Id.</summary>
    public string Id => Preset is { } preset ? preset.ToString("N") : "self";
}

/// <summary>
/// The services a workspace's parts act through; a partner's playback, Live and preview act on its actor only.
/// <paramref name="Files"/> is shared by every workspace; <paramref name="ActorName"/> names the actor ("You", or the partner).
/// </summary>
internal sealed record WorkspaceServices(AnimationEditService? Animations, TimelineCatalog Catalog, AnimationPreviewService Preview,
    AnimationPlayback Playback, MotionLiveDisplay? MotionDisplay, AnimationRecorder Recorder, IDataManager Data, IFramework Framework,
    ITextureProvider Textures, IPluginLog Log, GameSoundPlayer? SoundPlayer, PreviewFiles Files, Func<string> ActorName);

/// <summary>What a workspace takes from the Timeline window around it.</summary>
internal interface ITimelineHost
{
    bool IsOpen { get; }
    BonesWindow Bones { get; }
    PoseBrowserWindow Poses { get; }
    ShortcutKeys Keys { get; }
    PoseThumbnails Thumbnails { get; }
    string? AnimationError { get; }

    /// <summary>Opens the window and brings it to the front.</summary>
    void Show();

    /// <summary>Closes the window, once the question about unsaved changes was answered.</summary>
    void Close();
}

/// <summary>
/// One actor's timeline editor for one animation clip: its motion, expressions, lip sync, sounds, effects
/// and props on lanes like a video editor, its length, a side panel (properties, source, animated
/// bones, saved edits, recorder), pickers and imports, a loader that plays any animation on the GPose
/// actor, a save footer and a GPose preview. Slow work runs on tasks polled every framework update
/// (<see cref="Tick"/>), so the draw path never reads files. Each part is a class of its own in Ui/Timeline,
/// sharing a <see cref="TimelineSession"/>; this workspace builds them, reads clips in and lays the parts
/// out. The Timeline window hosts it. The bones on the character are in TimelineWorkspace.Overlay.cs.
/// </summary>
internal sealed partial class TimelineWorkspace : IBonesOverlay, IDisposable
{
    private enum PendingAction : byte { None, Close, Switch }

    private readonly ITimelineHost host;
    private readonly TimelineSession session;
    private readonly TimelineReader reader;
    private readonly MotionDecode decode;
    private readonly EffectLookEditor effectLooks;
    private readonly EffectPlaceEditor effectPlace;
    private readonly SoundFiles soundFiles;
    private readonly TimelinePickers pickers;
    private readonly SoundInspector soundInspector;
    private readonly TimelinePreview timelinePreview;
    private readonly PlayBar playBar;
    // The one line of notes under the bars.
    private readonly StatusLine statusLine = new();
    private readonly BonePosing posing;
    private readonly BoneEditor boneEditor;
    private readonly PoseSourcePanel poseSources;
    private readonly LayerPanel layerPanel;
    private readonly BonesPanel bonesPanel;
    private readonly TimelineTexts texts;
    private readonly TimelineClipBand clipBand;
    private readonly KeyMarkers keyMarkers;
    private readonly CutPanel cutPanel;
    private readonly TimelineCanvas canvas;
    private readonly CurveView curveView;
    private readonly ShortcutKeys keys;
    private readonly PropertiesPanel properties;
    private readonly SoundSaveCheck soundSave;
    private readonly SaveFooter saveFooter;
    private readonly RecentEditsPage recentEdits;
    private readonly TimelineLoader loader;
    private readonly LivePosePanel livePose;
    private readonly SidePanel sidePanel;
    private readonly StartPage startPage;
    private readonly TimelineToolbar toolbar;
    private readonly TimelineMenus menus;
    private readonly BlenderMotionPanel blenderMotion;
    private readonly BonesWindow bonesWindow;
    private readonly PoseBrowserWindow poseBrowserWindow;
    private readonly PoseBrowser poseBrowser;
    private readonly TimelineAutosaver autosaver;
    // Plays a sound file in game for the sound inspector's listen buttons; null when unavailable.
    private readonly GameSoundPlayer? soundPlayer;

    private string title = string.Empty;
    private string subtitle = string.Empty;

    private PendingAction pending;
    private (AnimationCapture Capture, bool Startup)? pendingOpen;
    // The saved edit the pending open brings back (Edit again).
    private AnimationEditJournal? pendingAgain;
    // The kept edits the clip being read gets back (Restore).
    private TimelineAutosave? pendingRestore;
    // The timeline Edit face left (Back returns to it) and its unsaved edits, which come back with it; null for none.
    private (AnimationCapture Capture, bool Startup)? parkedClip;
    private TimelineAutosave? parkedEdits;
    private bool closeConfirmed;
    // The start page shows over the open clip, which keeps running behind it (Start; Back to the clip ends it).
    private bool startShown;
    private float footerHeight;
    private float propertiesWidth;
    private bool disposed;
    // The catalog's face lengths with an expression's own face, kept while neither changes.
    private IReadOnlyDictionary<string, int>? framesKnown;
    private AnimationTimelineSource? framesSource;
    private IReadOnlyDictionary<string, int>? framesMerged;
    private int? framesFace;

    internal TimelineWorkspace(WorkspaceKey key, WorkspaceServices services, ITimelineHost host)
    {
        Key = key;
        this.host = host;
        session = new TimelineSession(services.Animations, services.Catalog, services.Preview, services.Playback, services.MotionDisplay,
            services.Data, services.Framework, services.Log) { WorkspaceId = key.Id, ActorName = services.ActorName, Files = services.Files };
        reader = new TimelineReader(session);
        soundPlayer = services.SoundPlayer;
        bonesWindow = host.Bones;
        poseBrowserWindow = host.Poses;
        keys = host.Keys;
        decode = new MotionDecode(session);
        texts = new TimelineTexts(session);
        clipBand = new TimelineClipBand(session, decode);
        keyMarkers = new KeyMarkers(session, decode);
        cutPanel = new CutPanel(session, decode, clipBand);
        effectLooks = new EffectLookEditor(session);
        effectPlace = new EffectPlaceEditor(session, effectLooks);
        soundFiles = new SoundFiles(session);
        pickers = new TimelinePickers(session, soundFiles);
        soundInspector = new SoundInspector(session, soundFiles, pickers, soundPlayer);
        timelinePreview = new TimelinePreview(session, decode, effectLooks, soundFiles);
        playBar = new PlayBar(session, timelinePreview);
        posing = new BonePosing(session, decode);
        boneEditor = new BoneEditor(session, decode, posing, this);
        var poseOptions = new PoseKeyOptions(posing);
        poseBrowser = new PoseBrowser(session, decode, posing, poseOptions, host.Thumbnails);
        poseSources = new PoseSourcePanel(session, decode, posing, services.Recorder, poseOptions, poseBrowserWindow);
        layerPanel = new LayerPanel(session, decode, posing, bonesWindow);
        canvas = new TimelineCanvas(session, posing, clipBand, pickers, soundFiles, texts, keys);
        curveView = new CurveView(session, decode, posing);
        properties = new PropertiesPanel(session, effectPlace, new PropPlaceEditor(session), effectLooks, soundInspector, cutPanel, layerPanel, texts, pickers)
            { OpenFace = OpenFace };
        soundSave = new SoundSaveCheck(session, soundFiles);
        saveFooter = new SaveFooter(session, timelinePreview, decode, effectLooks, soundFiles, soundSave, texts, () => reader.Busy,
            afterUndo =>
            {
                ForgetSounds();
                Load(source!.Capture, source.Startup, true, afterUndo: afterUndo);
            },
            EditAgain);
        recentEdits = new RecentEditsPage(session, saveFooter, timelinePreview, () => reader.Busy, EditAgain);
        loader = new TimelineLoader(session, timelinePreview, Open);
        blenderMotion = new BlenderMotionPanel(session, decode);
        sidePanel = new SidePanel(properties, effectLooks, soundInspector,
            new ClipView(session, posing, timelinePreview, blenderMotion, Open));
        startPage = new StartPage(session, loader, recentEdits, () => sidePanel.Recorder);
        livePose = new LivePosePanel(session);
        toolbar = new TimelineToolbar(session, pickers, loader, livePose, texts, saveFooter, bonesWindow, blenderMotion, playBar);
        menus = new TimelineMenus(session, canvas, toolbar, blenderMotion, curveView, bonesWindow, timelinePreview, ShowStart, Reload, Open);
        bonesPanel = new BonesPanel(session, host.AnimationError, posing, boneEditor, poseSources, timelinePreview, playBar, () => saveFooter.RepairTarget,
            this, services.Textures, keys, () =>
        {
            // Save pose: the pose browser at its last folder, the name to type in focused.
            poseBrowserWindow.IsOpen = true;
            poseBrowserWindow.BringToFront();
            poseBrowser.FocusSave();
        });
        session.KeepReadOnlyWhile(() => reader.Busy || saveFooter.Busy || blenderMotion.Working);
        autosaver = new TimelineAutosaver(session, Restore, key.IsSelf ? null : key.Id);
    }

    internal WorkspaceKey Key { get; }

    internal TimelineSession Session => session;

    internal TimelinePreview Preview => timelinePreview;

    public void DrawFileDialog() => session.FileDialog.Draw();

    /// <summary>
    /// What the Timeline window borrows from the main window: Settings, where the skeleton library is
    /// rebuilt, and the live pose recorder, which sends to Blender through it.
    /// </summary>
    internal void AttachTools(Action? settings, Action? recorderPanel) => sidePanel.AttachTools(settings, recorderPanel);

    /// <summary>The main window's connection to Blender, which Send motion to Blender and Motion from Blender go through.</summary>
    internal void AttachBlender(Main.AnimationDelivery delivery) => blenderMotion.Attach(delivery);

    /// <summary>The main window's Playing now page for the start page and how many animations play now for its sidebar.</summary>
    internal void AttachPlayingNow(Action draw, Func<int> playing) => startPage.AttachPlayingNow(draw, playing);

    /// <summary>The start page's Partners page; its sidebar entry shows once this is attached.</summary>
    internal void AttachPartners(Action draw) => startPage.AttachPartners(draw);

    /// <summary>The settings the pose browser keeps its folders in.</summary>
    internal void AttachPoseFolders(Configuration config, Action save) => poseBrowser.Attach(config, save);

    /// <summary>
    /// Shows the start page, on <paramref name="entry"/> or with null on the last entry shown. An open clip stays open and
    /// keeps running behind it.
    /// </summary>
    internal void ShowStart(StartEntry? entry)
    {
        startShown = true;
        if (entry is { } shown)
            startPage.Current = shown;
    }

    /// <summary>Back from the start page to the open clip, with its view and playhead as they were.</summary>
    internal void BackToClip() => startShown = false;

    /// <summary>The open clip's title, for the Bones window's title; null with no editable clip open.</summary>
    internal string? ClipTitle => source?.Document != null && editor != null ? title : null;

    /// <summary>The Bones window's whole content; called from its draw.</summary>
    internal void DrawBones() => bonesPanel.Draw();

    /// <summary>The pose browser's whole content; called from its draw.</summary>
    internal void DrawPoseBrowser() => poseBrowser.Draw();

    internal void PoseBrowserClosed() => poseBrowser.Closed();

    /// <summary>Reads whether the live character has fingertip bones; the plugin sets it once the physics builder exists.</summary>
    internal Func<ushort?, CancellationToken, Task<HandTipFacts>>? HandTips { get => session.HandTips; set => session.HandTips = value; }

    /// <summary>A drag runs on the lanes, which Escape cancels: Escape must not also close the window then.</summary>
    internal bool HoldsEscape { get; private set; }

    // The shared state and the bone helpers under the names TimelineWorkspace.Overlay.cs reads.
    private AnimationEditService? animations => session.Animations;
    private AnimationPlayback playback => session.Playback;
    private MotionLiveDisplay? motionDisplay => session.MotionDisplay;
    private AnimationTimelineSource? source { get => session.Source; set => session.Source = value; }
    private TimelineEditor? editor { get => session.Editor; set => session.Editor = value; }
    private bool ReadOnly => session.ReadOnly;
    private AnimationTimelineSource Fresh(AnimationTimelineSource current) => session.Fresh(current);
    private bool liveWanted => timelinePreview.LiveWanted;
    private bool liveStarted => timelinePreview.LiveStarted;
    private ushort? previewActor => timelinePreview.Actor;
    private PoseSelection selection => posing.Selection;
    private void KeepSelection(SkeletonDescription skeleton) => posing.KeepSelection(skeleton);
    private (Dictionary<string, int> Index, int[][] Children) BoneTree(SkeletonDescription skeleton) => posing.BoneTree(skeleton);
    private HashSet<string>? AnimatedBones(AnimationClip clip) => posing.AnimatedBones(clip);
    private void ClickBone(string bone, SkeletonDescription skeleton) => posing.ClickBone(bone, skeleton);
    private BonePosing.BoneFrames? FramesAt(AnimationTimelineSource loaded, TimelineDocument doc, MotionLayer layer, int frame) =>
        posing.FramesAt(loaded, doc, layer, frame);
    private List<PoseTarget>? Targets(MotionLayer layer, int frame, SkeletonDescription skeleton, AnimationTimelineSource loaded, TimelineDocument doc) =>
        posing.Targets(layer, frame, skeleton, loaded, doc);
    private static BoneState StateOf(string bone, MotionLayer? layer, int frame, HashSet<string>? animated, LayerBoneSets? layerBones) =>
        BonePosing.StateOf(bone, layer, frame, animated, layerBones);
    private LayerBoneSets? BonesOf(MotionLayer? layer, SkeletonDescription skeleton) => posing.BonesOf(layer, skeleton);
    private static string StateText(BoneState state, MotionLayer? layer) => BonePosing.StateText(state, layer);
    private static Vector4 StateColour(BoneState state, MotionLayer? layer) => BonePosing.StateColour(state, layer);
    private static bool IsBelow(SkeletonDescription skeleton, int bone, int ancestor) => BonePosing.IsBelow(skeleton, bone, ancestor);
    private static float MotionEnd(AnimationTimelineSource loaded, TimelineDocument doc) => BonePosing.MotionEnd(loaded, doc);
    private bool characterAxes => boneEditor.CharacterAxes;

    // The overlay's state as the bone editor and the Bones window see it.
    bool IBonesOverlay.Shown => OverlayWanted;
    BoneOverlayMode IBonesOverlay.OnCharacter { get => showOnCharacter; set => showOnCharacter = value; }
    GizmoMode IBonesOverlay.Gizmo { get => gizmoMode; set => gizmoMode = value; }
    float IBonesOverlay.DragBend { get => dragBend; set => dragBend = value; }
    DragHolds IBonesOverlay.Holds => dragHolds;
    string? IBonesOverlay.GizmoWaits(MotionLayer layer, int frame, SkeletonDescription skeleton) =>
        GizmoOperation(layer, frame, skeleton, out var reason) == null ? reason : null;

    /// <summary> Unsaved edits of an editable timeline. </summary>
    // The edits of the timeline Edit face left count too: opening another clip would lose them.
    internal bool HasUnsavedChanges => editor is { Dirty: true } && source?.Document != null || parkedEdits != null;

    /// <summary>
    /// Edit face: opens face <paramref name="motion"/> in <paramref name="gamePath"/> on its own, for the open clip's
    /// character. The open timeline waits with its unsaved edits until Back.
    /// </summary>
    internal void OpenFace(string gamePath, string motion)
    {
        if (animations is not { } service || source is not { Document: not null } current || editor == null || reader.Busy)
            return;
        parkedClip = (current.Capture, current.Startup);
        parkedEdits = editor.Dirty ? autosaver.Snapshot(editor) : null;
        timelinePreview.Stop();
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        pendingRestore = null;
        autosaver.Error = null;
        source = null;
        session.CatalogKey = null;
        title = TimelineCatalogBuilder.Readable(motion);
        saveFooter.ForgetMotion();
        blenderMotion.Forget();
        session.LoopPose.Drop();
        reader.StartFace(service, current.Capture, gamePath, motion, timelinePreview.Stopping ?? Task.CompletedTask);
    }

    /// <summary>Back from Edit face: the timeline it left, with its unsaved edits.</summary>
    private void Back()
    {
        if (parkedClip is not { } clip)
            return;
        var edits = parkedEdits;
        parkedClip = null;
        parkedEdits = null;
        if (edits != null)
            Restore(edits);
        else
            Load(clip.Capture, clip.Startup, false);
    }

    /// <summary>
    /// Shows the timeline of <paramref name="capture"/>'s clip, or of its startup. The clip already
    /// shown is read again only when its file changed (<see cref="ChangedCapture"/>). With unsaved
    /// changes it first asks, inline, whether to discard them.
    /// </summary>
    internal void Open(AnimationCapture capture, bool startup)
    {
        host.Show();
        if (animations == null)
            return;
        startup &= capture.Startup != null;
        // The clip already open, such as its row on the start page: back to it. Another clip leaves the start page
        // only once it loads, so Keep editing stays on the page.
        if (Showing(capture, startup) && ChangedCapture() == null)
        {
            startShown = false;
            return;
        }
        if (HasUnsavedChanges)
        {
            pending = PendingAction.Switch;
            pendingOpen = (capture, startup);
            pendingAgain = null;
            return;
        }
        Load(capture, startup, false);
    }

    /// <summary>
    /// Reads the open clip again (Animation, Reload), on the newest capture of it when its file changed. With unsaved
    /// changes it first asks, inline, whether to discard them, as <see cref="Open"/> does.
    /// </summary>
    private void Reload()
    {
        // A face from Edit face is read through its clip's capture, which would open the clip instead.
        if (source is not { } current || reader.Busy || current.Document?.Face != null)
            return;
        var capture = ChangedCapture() ?? current.Capture;
        if (HasUnsavedChanges)
        {
            pending = PendingAction.Switch;
            pendingOpen = (capture, current.Startup);
            pendingAgain = null;
            return;
        }
        Load(capture, current.Startup, false);
    }

    /// <summary>
    /// Opens the clip <paramref name="journal"/>'s edit saved, with that edit's bone layers back on the
    /// motion from before it (Edit again), so saving changes them instead of stacking new ones on top.
    /// With unsaved changes it asks first, like <see cref="Open"/>.
    /// </summary>
    internal void EditAgain(AnimationEditJournal journal) => EditAgain(journal, null);

    /// <summary>
    /// <see cref="EditAgain(AnimationEditJournal)"/> with <paramref name="capture"/>, the clip's capture to use;
    /// null for the best one the listener heard (see <see cref="AnimationEditService.EditAgainCapture"/>).
    /// </summary>
    private void EditAgain(AnimationEditJournal journal, AnimationCapture? capture)
    {
        host.Show();
        // The start page stays until the clip loads (Load), so Keep editing stays on it.
        if (animations is not { } service || journal.Request.Timeline is not { } edit)
            return;
        capture ??= AnimationEditService.EditAgainCapture(journal, session.History, session.ActorIndex ?? journal.Request.Capture.ActorIndex);
        if (HasUnsavedChanges)
        {
            pending = PendingAction.Switch;
            pendingOpen = (capture, edit.Startup);
            pendingAgain = journal;
            return;
        }
        Load(capture, edit.Startup, false, journal);
    }

    /// <summary>
    /// Opens the clip of edits an earlier run kept (<see cref="TimelineAutosaver"/>) with them as unsaved edits. A
    /// base motion from a layered save comes back as after an undo to it; the file and base must be the same ones.
    /// </summary>
    private void Restore(TimelineAutosave kept)
    {
        host.Show();
        startShown = false;
        // The kept capture names the character of the run that kept it; the clip is opened on the newest capture of it.
        var capture = animations != null
            ? AnimationEditService.LatestCapture(kept.Capture, kept.Startup, session.History, session.ActorIndex ?? kept.Capture.ActorIndex)
            : kept.Capture;
        Load(capture, kept.Startup, false, afterUndo: kept.BaseHash != null);
        pendingRestore = kept;
    }

    /// <summary>
    /// The window closes: with unsaved changes it asks first and returns false, and the window stays open until Discard.
    /// Otherwise everything this workspace runs ends, its clip closes, and it returns true.
    /// </summary>
    internal bool Close()
    {
        if (!closeConfirmed && HasUnsavedChanges)
        {
            pending = PendingAction.Close;
            pendingOpen = null;
            return false;
        }
        closeConfirmed = false;
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        pendingRestore = null;
        autosaver.Closed();
        loader.Reset();
        livePose.ForgetNote();
        // The Bones window poses this window's clip; it goes with it, and so do its bones on the character.
        bonesWindow.IsOpen = false;
        // So does the pose browser, which keys into it.
        poseBrowserWindow.IsOpen = false;
        poseBrowser.Forget();
        EndGizmo(true);
        timelinePreview.Stop();
        blenderMotion.Forget();
        decode.Forget();
        curveView.Forget();
        poseSources.Drop();
        session.LoopPose.Drop();
        reader.Stop();
        source = null;
        session.CatalogKey = null;
        saveFooter.ForgetMotion();
        effectLooks.Forget();
        ForgetSounds();
        // A sound played to listen to stops being served with the window.
        _ = soundPlayer?.StopAsync();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (source != null)
            autosaver.Flush(editor);
        reader.Dispose();
        decode.Forget();
        poseSources.Drop();
        poseBrowser.Closed();
        session.LoopPose.Drop();
    }

    private bool Showing(AnimationCapture capture, bool startup)
    {
        var shown = reader.Reading ?? (source is { } current ? (current.Capture, current.Startup) : null);
        if (shown is not { } open || open.Capture.Id != capture.Id || open.Startup != startup)
            return false;
        var before = ClipOf(open.Capture, open.Startup);
        var after = ClipOf(capture, startup);
        return before.GamePath == after.GamePath && before.BindingFingerprint == after.BindingFingerprint;
    }

    private static AnimationClip ClipOf(AnimationCapture capture, bool startup) => startup && capture.Startup is { } clip ? clip : capture.Clip;

    /// <summary>
    /// The listener's newest capture of the open clip when it heard a file other than the one read and
    /// the one opened: the file changed in the mods since (<see cref="TimelineWindowViews.ChangedCapture"/>).
    /// Null when unknown, unchanged, or while a load, save or preview runs (a preview's temporary mod
    /// changes the file the listener hears).
    /// </summary>
    private AnimationCapture? ChangedCapture()
    {
        if (animations == null || source is not { } current || reader.Busy || saveFooter.Working ||
            session.Preview.Active || session.Preview.Busy || timelinePreview.Building)
            return null;
        return TimelineWindowViews.ChangedCapture(session.History, current);
    }

    /// <summary>
    /// Reads the clip's timeline on a task. <paramref name="keepView"/> reloads the clip already
    /// shown after a save or undo, keeping its view and playhead. With <paramref name="again"/> the
    /// clip comes with that saved edit's bone layers back (Edit again). <paramref name="afterUndo"/> brings
    /// back the layers of the edit an undone save went back to, if there is one, as the saved state.
    /// </summary>
    private void Load(AnimationCapture capture, bool startup, bool keepView, AnimationEditJournal? again = null, bool afterUndo = false)
    {
        if (animations is not { } service)
            return;
        // What the loader plays keeps playing while its timeline opens; a preview or Live ends.
        if (playback.Active && timelinePreview.Fingerprint == null && !liveWanted && !session.Preview.Active && !timelinePreview.Building)
            timelinePreview.StopTemporaryMod();
        else
            timelinePreview.Stop();
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        pendingRestore = null;
        autosaver.Error = null;
        if (!keepView)
        {
            // Another clip: the timeline Edit face left is let go (opening asked first when it had edits).
            // It shows in place of the start page; a clip read again after a save or undo leaves the page shown.
            startShown = false;
            parkedClip = null;
            parkedEdits = null;
            source = null;
            session.CatalogKey = null;
            title = AnimationPresentation.AnimationName(capture, startup);
            // Another clip, or this one opened afresh: its own file is the motion to edit.
            saveFooter.ForgetMotion();
            blenderMotion.Forget();
            session.LoopPose.Drop();
        }
        // The preview's temporary mod must be gone first, or the read would get the previewed file.
        reader.Start(service, capture, startup, keepView, again, afterUndo, timelinePreview.Stopping ?? Task.CompletedTask);
    }

    private void PollLoad()
    {
        if (reader.Poll() is { } read)
            Apply(read.Loaded, read.KeepView, read.Again, read.AfterUndo);
    }

    private void Apply(AnimationTimelineSource loaded, bool keepView, AnimationEditJournal? again = null, bool afterUndo = false)
    {
        var restore = pendingRestore;
        pendingRestore = null;
        var sameClip = source != null && TimelineSession.ClipKey(source) == TimelineSession.ClipKey(loaded);
        // Edit again, or an undo back to a layered edit: the saved layers apply to the motion from before
        // their edit while this clip stays open.
        if (again != null || afterUndo)
            saveFooter.KeepBaseOf(loaded);
        // A clip read again after this window saved bone layers into it keeps editing them on the motion
        // from before: the carried layers come back, and the base stays the motion they apply to.
        loaded = saveFooter.WithMotionBase(loaded);
        source = loaded;
        pickers.ClipLoaded();
        session.CatalogKey = TimelineWindowViews.CatalogKey(loaded.Capture.LoadedResourcePaths, loaded.Clip.GamePath, loaded.Resource.ModRoot);
        title = AnimationPresentation.AnimationName(loaded.Capture, loaded.Startup);
        subtitle = $"{AnimationPresentation.AnimationState(loaded.Capture, loaded.Startup)} · " +
                   (loaded.Resource.ModName ?? loaded.Resource.ModDirectory ?? "Game Data");
        texts.Forget();
        effectLooks.ForgetEdit();
        soundInspector.ForgetEdit();
        if (!sameClip)
        {
            effectLooks.Forget();
            ForgetSounds();
            canvas.ResetScroll();
            sidePanel.ClearFilter();
            livePose.ForgetNote();
        }
        saveFooter.Loaded(loaded, sameClip, again != null);
        if (restore != null && !TimelineAutosaver.Fits(loaded, restore))
        {
            autosaver.Error = "The animation changed since its edits were kept, so they can't come back onto it.";
            restore = null;
        }
        if (loaded.Document is not { } doc)
            return;
        var carried = saveFooter.TakeCarriedLayers(loaded);
        if (carried is { Unsaved: false } saved)
            doc = saved.Document;
        if (editor == null)
        {
            editor = new TimelineEditor(doc);
            editor.VisibleLength = pickers.VisibleFrames;
        }
        else if (keepView && sameClip)
        {
            var view = editor.View;
            var playhead = editor.Playhead;
            editor.Reset(doc);
            editor.View = view.Clamp(TimelineEdits.Extent(doc));
            editor.Playhead = playhead;
        }
        else
        {
            editor.Reset(doc);
            if (!sameClip)
                editor.UnfoldAll();
        }
        // After an undo the layers stay as unsaved edits: one undo step away from the file.
        if (carried is { Unsaved: true } unsaved)
            editor.Commit(unsaved.Document);
        // Kept edits come back the same way: one undo step away from the file. Fields an older build didn't
        // keep come from the file, so the edits save.
        if (restore != null)
        {
            foreach (var import in restore.Imports)
                session.Imports[import.GamePath] = import with { Files = import.Files.WithComparers(StringComparer.OrdinalIgnoreCase) };
            editor.Commit(TimelineEdits.FillFields(restore.Restored, doc, restore.Format >= TimelineAutosave.PlacesFormat));
        }
    }

    /// <summary>
    /// Facial motions' own lengths: the catalog's, read from the game's face files, with the face an
    /// expression plays as the collection resolves it, which a mod may have made a moving one, and a face session's face
    /// at its edited length. The same instance until any of them changes, so the layout is rebuilt only then.
    /// </summary>
    private IReadOnlyDictionary<string, int> MotionFrames(AnimationTimelineSource current, TimelineDocument document)
    {
        var known = session.Catalog.Data.MotionFrames;
        if (!current.Standalone)
            return known;
        if (!ReferenceEquals(known, framesKnown) || !ReferenceEquals(current, framesSource) || document.Face?.Frames != framesFace || framesMerged == null)
        {
            framesKnown = known;
            framesSource = current;
            framesFace = document.Face?.Frames;
            framesMerged = known.SetItems(current.EditedFrames(document));
        }
        return framesMerged;
    }

    /// <summary> Before the window begins: the menu bar shows while an editable clip is open and the start page isn't shown. </summary>
    internal void PreDraw(ref ImGuiWindowFlags flags)
    {
        var menu = animations != null && source?.Document != null && editor != null && !startShown;
        flags = menu ? flags | ImGuiWindowFlags.MenuBar : flags & ~ImGuiWindowFlags.MenuBar;
    }

    /// <summary>
    /// Every framework update while the window is open, shown or not: the reads, saves, imports, previews and Live
    /// polled, and the open clip's playback kept in step. No ImGui here: this runs outside the ImGui frame.
    /// </summary>
    internal void Tick()
    {
        if (animations == null)
            return;
        PollLoad();
        autosaver.Poll(editor);
        saveFooter.Poll();
        pickers.PollImport();
        timelinePreview.Poll();
        pickers.PollLengths();
        loader.PollLibrary();
        loader.Poll();
        startPage.Poll(source == null || startShown);
        // Every frame: the bone rows' order, Live and the save all read the clip's match.
        session.KeepMatch();
        if (source?.Document == null || editor == null)
            return;
        // Other parts read the texts every frame, also while the start page shows.
        texts.Refresh(editor.Document);
        livePose.Poll(editor, source);
        // A finished Still pose, face hold or Blender read goes in here, so the start page over the clip doesn't hold it.
        blenderMotion.Poll(editor);
        session.LoopPose.Poll(editor);
        if (session.CatalogKey != null)
            session.Catalog.Request(session.CatalogKey);
        editor.MotionFrames = MotionFrames(source, editor.Document);
        editor.FaceMotion = source.Face?.Clip.Name;
        editor.ResidentMotions = session.Catalog.Data.ResidentMotions;
        editor.MotionPacks = session.Catalog.Data.MotionPacks;
        editor.BoneOrder = session.EditingSkeleton(source) is { } skeleton ? BoneTree(skeleton).Index : null;
        (editor.Markers, editor.ShownMarkers) = keyMarkers.For(editor, source);
        editor.MarkerBones = posing.Selection.Bones;

        timelinePreview.Dragging = OverlayDragging;
        timelinePreview.Update(editor, source);
    }

    internal void Draw()
    {
        HoldsEscape = false;
        if (animations == null)
        {
            Widgets.Banner("##timeline-unavailable", FeedbackSeverity.Warning, host.AnimationError ?? "Animation editing is unavailable.");
            if (sidePanel.Recorder is { } recorder)
            {
                ImGui.Spacing();
                recorder();
            }
            return;
        }

        DrawPendingBanner();
        if (pending == PendingAction.None)
            autosaver.Draw(HasUnsavedChanges || reader.Busy);
        if (source == null)
        {
            DrawStart(null);
            return;
        }
        if (source.Document == null || editor == null)
        {
            if (startShown)
            {
                DrawStart(title);
                return;
            }
            DrawHeader(source);
            ImGui.Spacing();
            Widgets.Banner("##timeline-read-only", FeedbackSeverity.Warning,
                "This timeline can't be edited. " + (source.ReadOnlyReason ?? "Its file could not be read."));
            ImGui.Spacing();
            if (ImGui.Button("Start page##timeline-read-only"))
                ShowStart(null);
            loader.DrawStatus();
            return;
        }
        if (!startShown)
        {
            menus.Draw(editor, source);
            // Open startup or loop, or Reload, may have started reading a clip.
            if (source?.Document == null)
                return;
            DrawHeader(source);
        }
        if (!startShown && reader.Error is { } readError)
            Widgets.Banner("##timeline-reload-error", FeedbackSeverity.Error, "Reading the timeline again failed: " + readError);
        if (!startShown && pending == PendingAction.None && ChangedCapture() is { } changed)
        {
            Widgets.Banner("##timeline-file-changed", FeedbackSeverity.Info, "This file changed in your mods.");
            if (ImGui.Button("Reload##timeline-file-changed"))
                Open(changed, source.Startup);
            ImGui.Spacing();
        }
        CheckGizmo();
        // The start page over the clip: Tick keeps the clip running; the bars, lanes and save row wait.
        if (startShown)
        {
            DrawStart(title);
            return;
        }
        toolbar.Draw(editor, source);
        statusLine.Draw("##timeline", toolbar.Notes(editor).Concat(timelinePreview.Notes(editor, source, saveFooter.RepairTarget)));

        var avail = ImGui.GetContentRegionAvail();
        if (footerHeight <= 0)
            footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var height = Math.Max(Theme.Scaled(120), avail.Y - footerHeight - ImGui.GetStyle().ItemSpacing.Y);
        DrawBody(editor, avail.X, height);
        // A link in the side panel (the Source page's startup Open) may have started reading another clip.
        if (source == null)
            return;

        var footerStart = ImGui.GetCursorPosY();
        saveFooter.Draw(editor, source);
        footerHeight = Math.Max(1, ImGui.GetCursorPosY() - footerStart);

        pickers.Draw(editor);
        var playhead = editor.Playhead;
        canvas.HandleKeys(editor);
        if (editor.Playhead != playhead)
            timelinePreview.PauseForStep();
        // Escape cancels a drag; it must not also close the window.
        HoldsEscape = editor.Gesture != TimelineGesture.None;
    }

    /// <summary>
    /// The start page, while a clip is read the spinner line instead. <paramref name="backTo"/> names the clip open behind
    /// it, which keeps running; null when none is open.
    /// </summary>
    private void DrawStart(string? backTo)
    {
        if (reader.Busy)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, title.Length > 0 ? $"Reading the timeline of {title}" : "Reading the timeline");
            return;
        }
        if (reader.Error is { } readError)
        {
            // With a clip open, the read that failed was that clip's again, worded as the clip view words it.
            Widgets.Banner("##timeline-load-error", FeedbackSeverity.Error, backTo != null ? "Reading the timeline again failed: " + readError
                : (reader.ErrorAgain ? "The edit could not be opened again. " : "The timeline could not be read: ") + readError);
            ImGui.Spacing();
            // A face Edit face couldn't open: the timeline it was opened from waits with its edits. Over an open clip
            // Back to it is the way back.
            if (backTo == null && parkedClip != null)
            {
                DrawBack(null);
                ImGui.Spacing();
            }
        }
        startPage.Draw(ImGui.GetContentRegionAvail().Y, backTo, BackToClip);
    }

    /// <summary>Back from Edit face to the timeline it left; <paramref name="open"/> is the face open, null when it couldn't be read.</summary>
    private void DrawBack(AnimationTimelineSource? open)
    {
        if (parkedClip is not { } parked)
            return;
        var can = TimelineWindow.CanGoBack(open, editor is { Dirty: true }, reader.Busy);
        using (ImRaii.Disabled(!can))
        {
            if (ImGui.SmallButton("Back##timeline-face-back"))
                Back();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!can && !reader.Busy ? "Save or undo the face's changes first."
                : $"Back to {AnimationPresentation.AnimationName(parked.Capture, parked.Startup)}.");
    }

    /// <summary>
    /// The header: Start, marked while an edit did not finish; Back after Edit face; the clip's name, which clears the
    /// selection; its state and mod; and on the right its length.
    /// </summary>
    private void DrawHeader(AnimationTimelineSource current)
    {
        var unfinished = animations is { } service ? TimelineWindow.RecoveryAttention(service) : 0;
        bool start;
        // "###" keeps the button's id when its label gains or loses the count, so a click then isn't dropped.
        using (ImRaii.PushId("##timeline-start-button"))
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, unfinished > 0))
            start = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowLeft, TimelineWindow.StartLabel(unfinished) + "###timeline-start", null, null, null, null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(unfinished switch
            {
                0 => "Go to the start page. This clip stays open.",
                1 => "Go to the start page. An edit did not finish and needs an undo.",
                _ => $"Go to the start page. {unfinished} edits did not finish and need an undo.",
            });
        if (start)
            ShowStart(null);
        ImGui.SameLine(0, Theme.Scaled(12));
        if (parkedClip != null)
        {
            DrawBack(current);
            ImGui.SameLine(0, Theme.Gap);
        }
        Widgets.Icon(current.Standalone || current.Document?.Face != null ? FontAwesomeIcon.Smile : FontAwesomeIcon.Running, Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        bool name;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Label))
            name = ImGui.Selectable(title + "##timeline-clip-name", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(title).X, 0));
        if (editor != null && ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear the selection.");
        if (name)
            editor?.ClearSelection();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, subtitle);
        if (current.Document == null || editor == null)
            return;
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(16);
        var right = ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(texts.LengthText).X;
        ImGui.SameLine(Math.Max(after, right));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(editor.Document.LengthChanged ? Theme.Warning : Theme.Muted, texts.LengthText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(editor.Document.Face != null ? "The length shown. The face's own length is in the clip view."
                : editor.Document.LengthChanged ? "The new length. Saving resamples the motion to it."
                : current.Standalone ? "The expression's timeline length." : "The clip's length.");
    }

    /// <summary> The inline question before unsaved changes are lost: Discard or Keep editing, never a modal. </summary>
    private void DrawPendingBanner()
    {
        if (pending == PendingAction.None)
            return;
        var text = pending == PendingAction.Close || pendingOpen is not { } next
            ? "This timeline has unsaved changes. Close it and lose them?"
            : pendingAgain == null && source != null && next.Capture.Id == source.Capture.Id && next.Startup == source.Startup
                ? "This timeline has unsaved changes. Reload it and lose them?"
            : $"This timeline has unsaved changes. Open {AnimationPresentation.AnimationName(next.Capture, next.Startup)} and lose them?";
        Widgets.Banner("##timeline-unsaved", FeedbackSeverity.Warning, text);
        if (ImGui.Button("Discard"))
            Discard();
        ImGui.SameLine();
        if (ImGui.Button("Keep editing"))
        {
            pending = PendingAction.None;
            pendingOpen = null;
            pendingAgain = null;
        }
        ImGui.Spacing();
    }

    private void Discard()
    {
        if (pending == PendingAction.Close || pendingOpen is not { } next)
        {
            pending = PendingAction.None;
            closeConfirmed = true;
            host.Close();
            return;
        }
        Load(next.Capture, next.Startup, false, pendingAgain);
    }

    /// <summary> The lanes on the left, the curves under them when shown, and the side panel on the right, with a splitter between them. </summary>
    private void DrawBody(TimelineEditor current, float width, float height)
    {
        var splitter = Theme.Scaled(6);
        if (propertiesWidth <= 0)
            propertiesWidth = Theme.Scaled(320);
        var minimumCanvas = Theme.Scaled(320);
        var minimumProperties = Theme.Scaled(220);
        propertiesWidth = Math.Clamp(propertiesWidth, minimumProperties, Math.Max(minimumProperties, width - minimumCanvas - splitter));
        var canvasWidth = Math.Max(Theme.Scaled(120), width - propertiesWidth - splitter);
        // An expression's own timeline has no bones to draw.
        var curves = curveView.Shown && current.Document.Layered;
        var strip = curves ? curveView.Height(height) : 0;
        using (ImRaii.Group())
        {
            canvas.Draw(current, new Vector2(canvasWidth, Math.Max(1, height - strip - (curves ? ImGui.GetStyle().ItemSpacing.Y : 0))));
            if (curves)
                curveView.Draw(current, new Vector2(canvasWidth, strip));
        }
        ImGui.SameLine(0, 0);
        var dragged = canvasWidth;
        Widgets.Splitter("##timeline-splitter", ref dragged, minimumCanvas, Math.Max(minimumCanvas, width - minimumProperties - splitter), height);
        propertiesWidth -= dragged - canvasWidth;
        ImGui.SameLine(0, 0);
        using var panel = ImRaii.Child("##timeline-properties", new Vector2(0, height), true);
        if (panel.Success && source != null)
            sidePanel.Draw(current, source);
    }

    /// <summary> Forgets the sounds read for another clip. </summary>
    private void ForgetSounds()
    {
        soundFiles.Forget();
        properties.ForgetPathOrigins();
        // A save's check of the mod, or its question, belongs to the clip it was for.
        soundSave.Forget();
        // So is a listen's error.
        soundInspector.Forget();
    }
}
