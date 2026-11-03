using System.Collections.Immutable;
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
    ITextureProvider Textures, IPluginLog Log, GameSoundPlayer? SoundPlayer, PreviewFiles Files, Func<string> ActorName, TimelinePlayGroup Group);

/// <summary>
/// The play bar's controls over every actor's workspace with an editable clip at once: Live, Preview, Play and Pause, From
/// start and Stop. The Timeline window holds it.
/// </summary>
internal interface ITimelineTransport
{
    /// <summary>The actors whose Live is on, the shown one first.</summary>
    IReadOnlyList<string> LiveActors { get; }

    /// <summary>Live is on for any actor.</summary>
    bool AnyLive { get; }

    /// <summary>Why Live can come on for none of them; null when it can for one.</summary>
    string? LiveBlocked { get; }

    /// <summary>Turns Live off for every actor while it is on for any, else on for every actor it can come on for.</summary>
    void ToggleLive();

    /// <summary>Builds every actor's preview and plays it, one build after another.</summary>
    void Preview();

    /// <summary>Any actor's preview or Live plays now.</summary>
    bool Playing { get; }

    /// <summary>Any actor has a preview or Live shown, which Play, Pause and From start act on.</summary>
    bool AnyShown { get; }

    /// <summary>Any actor has something Stop ends: a preview, Live, a build or a clip playing.</summary>
    bool AnyActive { get; }

    /// <summary>Any actor's preview is being built or waits to be, which reports in that actor's own notes.</summary>
    bool Building { get; }

    /// <summary>Pauses every actor while any plays, else plays every actor with something shown.</summary>
    void PlayPause();

    /// <summary>Plays every shown preview again from frame 0.</summary>
    void FromStart();

    /// <summary>Ends every actor's preview and Live and puts the actors back.</summary>
    void Stop();
}

/// <summary>What a workspace takes from the Timeline window around it.</summary>
internal interface ITimelineHost
{
    bool IsOpen { get; }
    BonesWindow Bones { get; }
    PoseBrowserWindow Poses { get; }
    ShortcutKeys Keys { get; }
    FreeCamera FreeCamera { get; }
    PoseThumbnails Thumbnails { get; }
    string? AnimationError { get; }

    /// <summary>The window's layout, the same for every workspace: the dock, the side panel's width, the curve strip, the loader.</summary>
    WindowLayout Layout { get; }

    /// <summary>The play bar's controls over every actor at once.</summary>
    ITimelineTransport Transport { get; }

    /// <summary>The other actors' playback notes, each with its actor's name in front, for this workspace's status line.</summary>
    IEnumerable<StatusNote> OtherNotes(WorkspaceKey self);

    /// <summary>Opens the window and brings it to the front.</summary>
    void Show();

    /// <summary>
    /// The dock's actors: You and the spawned partners, a click switching to one, with the + popup of presets; folded
    /// (<paramref name="folded"/>), their icons alone. Nothing without the partner spawner.
    /// </summary>
    void DrawActors(bool folded);

    /// <summary>Why <paramref name="key"/>'s actor can't be acted on yet ("Partner One is still spawning."); null when it can.</summary>
    string? Waiting(WorkspaceKey key);

    /// <summary>
    /// The other workspaces' ready link views, as the last framework update found them: what a link can join. Asking keeps their
    /// motion read for a couple of seconds, so call it only while the picker is open.
    /// </summary>
    IReadOnlyList<LinkView> LinkPartners(WorkspaceKey self);

    /// <summary>
    /// <paramref name="key"/>'s ready link view as the last framework update found it, null when it wasn't read. Asks for nothing, so
    /// what draws every frame can call it.
    /// </summary>
    LinkView? LinkView(WorkspaceKey key);

    /// <summary>The other workspaces with an editable body clip to link to, as the last framework update found them; reads no motion.</summary>
    IReadOnlyList<LinkCandidate> LinkCandidates(WorkspaceKey self);

    /// <summary>Whether another workspace has an editable body clip to link to, for the Add menu; reads no motion.</summary>
    bool HasLinkPartner(WorkspaceKey self);

    /// <summary>
    /// Adds <paramref name="link"/> as a link layer named <paramref name="name"/> to <paramref name="partner"/>'s document on the next
    /// framework update, through that workspace's own editor (its own key and undo step), if its clip is still
    /// <paramref name="expectedPath"/> at <paramref name="expectedBinding"/>; dropped otherwise.
    /// </summary>
    void AddLinkBack(WorkspaceKey partner, MotionLink link, string name, string expectedPath, int expectedBinding);

    /// <summary>The actor of a workspace by its preset, as link texts name it: "You" for null, the partner's name, null once its preset is gone.</summary>
    string? ActorName(Guid? preset);

    /// <summary>
    /// The other workspaces whose link layers follow <paramref name="self"/>'s open clip, by actor name, as the last framework update
    /// found them (<see cref="TimelineWindowViews.LinkedBy"/>); reads no motion.
    /// </summary>
    IReadOnlyList<string> LinkedBy(WorkspaceKey self);
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
    private enum PendingAction : byte { None, Switch }

    private readonly ITimelineHost host;
    private readonly TimelineSession session;
    private readonly TimelineReader reader;
    private readonly MotionDecode decode;
    private readonly EffectLookEditor effectLooks;
    private readonly EffectPlaceEditor effectPlace;
    private readonly SoundFiles soundFiles;
    private readonly TimelinePickers pickers;
    private readonly LinkPicker linkPicker;
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
    private readonly TimelineDock dock;
    private readonly TimelineToolbar toolbar;
    private readonly LayerMenu layerMenu;
    private readonly LengthPopup lengthPopup;
    private readonly BonesKeptPage bonesKept;
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
    private float footerHeight;
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
            services.Data, services.Framework, services.Log) { WorkspaceId = key.Id, ActorName = services.ActorName, Files = services.Files, Group = services.Group };
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
        playBar = new PlayBar(timelinePreview, host.Transport);
        posing = new BonePosing(session, decode);
        blenderMotion = new BlenderMotionPanel(session, decode);
        livePose = new LivePosePanel(session);
        layerMenu = new LayerMenu(session, livePose, blenderMotion);
        // The Add menus open the link picker, and link texts name partners, through the session.
        session.PartnerName = host.ActorName;
        session.LinkedBy = () => host.LinkedBy(key);
        session.LinkPicker = linkPicker = new LinkPicker(session, posing, host, key);
        boneEditor = new BoneEditor(session, decode, posing, this);
        var poseOptions = new PoseKeyOptions(posing);
        poseBrowser = new PoseBrowser(session, decode, posing, poseOptions, host.Thumbnails);
        poseSources = new PoseSourcePanel(session, decode, posing, services.Recorder, poseOptions, poseBrowserWindow);
        layerPanel = new LayerPanel(session, decode, posing, bonesWindow);
        canvas = new TimelineCanvas(session, posing, clipBand, pickers, soundFiles, texts, keys, layerMenu);
        curveView = new CurveView(session, decode, posing, host.Layout);
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
        loader = new TimelineLoader(session, timelinePreview, host.Layout, Open);
        sidePanel = new SidePanel(properties, effectLooks, soundInspector);
        dock = new TimelineDock(session, host.Layout, host, loader, recentEdits, blenderMotion, () => sidePanel.Recorder,
            () => saveFooter.Working || host.Transport.Building);
        toolbar = new TimelineToolbar(session, pickers, loader, livePose, texts, saveFooter, bonesWindow, blenderMotion, playBar, host.FreeCamera, curveView);
        lengthPopup = new LengthPopup(session, blenderMotion);
        bonesKept = new BonesKeptPage(session, timelinePreview);
        bonesPanel = new BonesPanel(session, host.AnimationError, posing, boneEditor, poseSources, timelinePreview, playBar,
            this, services.Textures, keys, () =>
        {
            // Save pose: the pose browser at its last folder, the name to type in focused.
            poseBrowserWindow.IsOpen = true;
            poseBrowserWindow.BringToFront();
            poseBrowser.FocusSave();
        }, bonesKept, layerMenu);
        session.KeepReadOnlyWhile(() => reader.Busy || saveFooter.Busy || blenderMotion.Working);
        autosaver = new TimelineAutosaver(session, Restore, key.IsSelf ? null : key.Id);
    }

    internal WorkspaceKey Key { get; }

    internal TimelineSession Session => session;

    internal TimelinePreview Preview => timelinePreview;

    public void DrawFileDialog() => session.FileDialog.Draw();

    /// <summary>The main window's live pose recorder, which sends to Blender through it; the dock's Blender tab shows it.</summary>
    internal void AttachRecorder(Action? recorderPanel) => sidePanel.AttachRecorder(recorderPanel);

    /// <summary>The main window's connection to Blender, which Send motion to Blender and Motion from Blender go through.</summary>
    internal void AttachBlender(Main.AnimationDelivery delivery) => blenderMotion.Attach(delivery);

    /// <summary>The main window's Now playing list for the dock and how many animations play now for its tab.</summary>
    internal void AttachPlayingNow(Action draw, Func<int> playing) => dock.AttachPlayingNow(draw, playing);

    /// <summary>The settings the pose browser keeps its folders in.</summary>
    internal void AttachPoseFolders(Configuration config, Action save) => poseBrowser.Attach(config, save);

    /// <summary>The open clip's title, for the Bones window's title; null with no editable clip open.</summary>
    internal string? ClipTitle => source?.Document != null && editor != null ? title : null;

    /// <summary>The Bones window's whole content; called from its draw.</summary>
    internal void DrawBones() => bonesPanel.Draw();

    /// <summary>The pose browser's whole content; called from its draw.</summary>
    internal void DrawPoseBrowser() => poseBrowser.Draw();

    internal void PoseBrowserClosed() => poseBrowser.Closed();

    /// <summary>Reads whether the live character has fingertip bones; the plugin sets it once the physics builder exists.</summary>
    internal Func<ushort?, CancellationToken, Task<HandTipFacts>>? HandTips { get => session.HandTips; set => session.HandTips = value; }

    /// <summary>Reads the bones the game's physics moves on the GPose actor, for spring layers; the plugin sets it.</summary>
    internal Func<ushort?, CancellationToken, Task<ImmutableHashSet<string>?>>? PhysicsBones
    {
        get => session.ReadPhysicsBones;
        set => session.ReadPhysicsBones = value;
    }

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

    /// <summary>The partner tracks of this clip's link layers (<see cref="PartnerLinks"/>); the same instance while none changed.</summary>
    internal MotionLinkSet Links => session.Links;

    /// <summary>Each planned link layer's state, by layer key.</summary>
    internal ImmutableDictionary<string, LinkState> LinkStates => session.LinkStates;

    /// <summary>
    /// What a link or the picker sees of this workspace before its motion is read, on the framework thread with no ImGui: null
    /// without an editable body clip (none open, a face, an expression on its own). Reads nothing and starts no read.
    /// </summary>
    internal LinkCandidate? LinkCandidate()
    {
        if (animations == null || source is not { Document: not null } loaded || editor == null || editor.Document.Face != null ||
            loaded.MotionTarget.Standalone)
            return null;
        return new LinkCandidate(Key, session.ActorName(), loaded.Clip.GamePath, loaded.Clip.BindingIndex, editor.Document, session.Busy,
            session.EditingSkeleton(loaded), decode.FailedFor(loaded));
    }

    /// <summary>
    /// What the other workspaces' links see of this one, on the framework thread with no ImGui: the <see cref="LinkCandidate"/> with
    /// the clip as it plays and the actor's size. Starts reading the motion, so only a workspace a link or the picker wants is asked;
    /// null without an editable body clip or while the motion is still read.
    /// </summary>
    internal LinkView? LinkView()
    {
        if (LinkCandidate() is not { } candidate || decode.DecodedPose(source!) is not { } decoded)
            return null;
        return new LinkView(Key, candidate.Name, candidate.GamePath, candidate.Binding, decode.OutputMotion(editor!, decoded),
            candidate.Document, candidate.ReadOnly, playback.ActorScale());
    }

    /// <summary>
    /// A partner's link back: adds <paramref name="link"/> as a link layer named <paramref name="name"/> through this workspace's own
    /// editor, so this document gives it its key and it is one undo step here. True when it is done or can't be (no editable body
    /// clip, no room, the same link already here, a bone that can't be linked or no skeleton to tell); false while an edit runs here,
    /// to try again on the next update.
    /// </summary>
    internal bool AddLinkLayer(MotionLink link, string name)
    {
        if (source?.Document == null || editor == null)
            return true;
        // A read or save running, or a drag, here: Commit would end the drag, and the clip may still change.
        if (session.Busy || editor.Gesture != TimelineGesture.None || editor.History.InGesture)
            return false;
        // A second link of the same bones would move the bone twice as far.
        if (MotionLinks.HasLink(editor.Document.Motion, link))
            return true;
        if (session.EditingSkeleton(source) is not { } skeleton)
            return true;
        var index = MotionEvaluator.Index(skeleton);
        if (!index.TryGetValue(link.Bone, out var bone) || !LinkPicker.Linkable(skeleton, index, bone))
            return true;
        editor.Commit(MotionEdits.AddLink(editor.Document, link, name, out _));
        return true;
    }

    /// <summary>No clip is open, being read or asked about, and no read failed: the window may open what plays now.</summary>
    internal bool Idle => animations != null && source == null && !reader.Busy && reader.Error == null && pending == PendingAction.None;

    /// <summary>The play bar acts on this workspace too: it has an editable clip and its actor is there.</summary>
    internal bool OnTransport => animations != null && source?.Document != null && editor != null && host.Waiting(Key) == null;

    /// <summary>Live is on, or waits to start.</summary>
    internal bool LiveOn => timelinePreview.LiveWanted;

    /// <summary>Why Live can't come on here now; null while it is on or can come on.</summary>
    internal string? LiveBlocked => OnTransport ? timelinePreview.LiveBlockedNow(editor!, source!) : "No animation is open.";

    /// <summary>Turns Live on or off here, for the play bar over every actor; nothing while it can't come on.</summary>
    internal void SetLive(bool on)
    {
        if (OnTransport && timelinePreview.LiveWanted != on && !session.ReadOnly && (!on || LiveBlocked == null))
            timelinePreview.ToggleLive(editor!, source!);
    }

    /// <summary>Builds and plays this clip's preview, now or after the build running, for the play bar over every actor.</summary>
    internal void StartPreview()
    {
        if (OnTransport)
            timelinePreview.QueuePreview(editor!, source!, saveFooter.RepairTarget);
    }

    /// <summary>A preview or Live is shown here.</summary>
    internal bool PreviewShown => timelinePreview.Fingerprint != null;

    /// <summary>The clip plays here now.</summary>
    internal bool Playing => playback.Active && !playback.Paused;

    /// <summary>Something here that Stop ends: a preview, Live, a build or a clip playing.</summary>
    internal bool PlaybackActive => PreviewShown || playback.Active || session.Preview.Active || timelinePreview.Building || LiveOn;

    /// <summary>The preview is being built here, or waits to be.</summary>
    internal bool PreviewBuilding => timelinePreview.Building;

    /// <summary>The clip plays in the play group with another actor's.</summary>
    internal bool InGroup => timelinePreview.InGroup;

    /// <summary>Plays or pauses here to match <paramref name="play"/>; in the play group the group does it once.</summary>
    internal void SetPlaying(bool play) => timelinePreview.SetPlaying(play);

    /// <summary>Plays the shown preview again from frame 0.</summary>
    internal void Replay()
    {
        if (editor != null)
            timelinePreview.Replay(editor);
    }

    /// <summary>Ends the preview and Live here and puts the actor back.</summary>
    internal void StopPlayback() => timelinePreview.Stop();

    /// <summary>The preview's and Live's notes, for the shown workspace's status line.</summary>
    internal IEnumerable<StatusNote> PlaybackNotes() => OnTransport ? timelinePreview.Notes(editor!, source!, saveFooter.RepairTarget) : [];

    /// <summary> Unsaved edits of an editable timeline. </summary>
    // The edits of the timeline Edit face left count too: opening another clip would lose them. So does a partner that changed
    // what a link layer follows since the last save.
    internal bool HasUnsavedChanges => (editor is { Dirty: true } || session.LinksUnsaved) && source?.Document != null || parkedEdits != null;

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
        // The clip already open, such as its row in Now playing, stays as it is.
        if (Showing(capture, startup) && ChangedCapture() == null)
            return;
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
        // The kept capture names the character of the run that kept it; the clip is opened on the newest capture of it.
        var capture = animations != null
            ? AnimationEditService.LatestCapture(kept.Capture, kept.Startup, session.History, session.ActorIndex ?? kept.Capture.ActorIndex)
            : kept.Capture;
        Load(capture, kept.Startup, false, afterUndo: kept.BaseHash != null);
        pendingRestore = kept;
    }

    /// <summary>
    /// The window closes (the window asked about unsaved changes first) or the preset went: everything this workspace runs
    /// ends and its clip closes. <paramref name="shared"/> closes the window's Bones window and pose browser too and stops a
    /// sound playing to listen to; a hidden workspace leaves them to the one shown.
    /// </summary>
    internal void Close(bool shared = true)
    {
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        pendingRestore = null;
        autosaver.Closed();
        loader.Reset();
        livePose.ForgetNote();
        if (shared)
        {
            // The Bones window poses this window's clip; it goes with it, and so do its bones on the character.
            bonesWindow.IsOpen = false;
            // So does the pose browser, which keys into it.
            poseBrowserWindow.IsOpen = false;
            poseBrowser.Forget();
        }
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
        if (shared)
            _ = soundPlayer?.StopAsync();
    }

    /// <summary>Another workspace shows now: a gizmo drag on this one's actor ends.</summary>
    internal void Hidden() => EndGizmo(true);

    /// <summary>
    /// The partner despawned (Despawn, GPose end, a zone change or a lost actor): Preview and Live stop and its temporary mod
    /// goes without a redraw, the actor being gone. The clip and its unsaved edits stay.
    /// </summary>
    internal void ActorGone()
    {
        EndGizmo(true);
        timelinePreview.ActorGone();
    }

    /// <summary>The preset spawned again: the open clip points at the new actor, so saves check it; Preview and Live stay off.</summary>
    internal void ActorBack(ushort index, ulong actorId, long address) => session.Retarget(index, actorId, address);

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
            bonesKept.ClearFilter();
            livePose.ForgetNote();
        }
        saveFooter.Loaded(loaded, sameClip, again != null);
        // Edit again: the file holds the links as that save baked them.
        if (again != null)
            session.SavedPrints = TimelineSession.SavedPrintsOf(again);
        // After an undo (or kept edits on a layered save's base) the file is again what an earlier edit saved, whose layers came
        // back: the links are saved as that edit baked them, or not known without one.
        else if (afterUndo)
            session.SavedPrints = animations != null && AnimationEditService.SavedLayersJournal(animations.Recovery, loaded) is { } back
                ? TimelineSession.SavedPrintsOf(back) : MotionLinks.NoPrints;
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
        dock.Poll();
        // Every frame: the bone rows' order, Live and the save all read the clip's match.
        session.KeepMatch();
        if (source?.Document == null || editor == null)
        {
            // No editable clip plays through the Timeline here, so none is in the play group.
            timelinePreview.ClearMember();
            return;
        }
        // Other parts read the texts every frame, also while another workspace shows.
        texts.Refresh(editor.Document);
        livePose.Poll(editor, source);
        // A finished face hold or Blender read goes in here, also while another workspace shows.
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

        var layout = host.Layout;
        var area = ImGui.GetContentRegionAvail();
        // The splitter with room on each side, so the dock and the timeline don't touch.
        var margin = Theme.Scaled(6);
        var splitter = Theme.Scaled(6) + margin * 2;
        var rest = Theme.Scaled(MainMinimum) + splitter;
        // The width set stays as it was set: a narrow window only narrows the dock while it is narrow.
        var dockWidth = layout.DockFolded ? Theme.Scaled(40) : WindowLayout.Dock(layout.DockWidth, area.X, rest, Theme.Scale);
        using (var side = ImRaii.Child("##timeline-dock", new Vector2(dockWidth, area.Y), false))
        {
            if (side.Success)
                dock.Draw(host.Waiting(Key));
        }
        ImGui.SameLine(0, margin);
        if (layout.DockFolded)
            ImGui.Dummy(new Vector2(Theme.Scaled(6), area.Y));
        else
        {
            var low = Theme.Scaled(WindowLayout.MinimumDock);
            var dragged = dockWidth;
            Widgets.Splitter("##timeline-dock-splitter", ref dragged, low, Math.Max(low, Math.Min(Theme.Scaled(WindowLayout.MaximumDock), area.X - rest)), area.Y);
            if (dragged != dockWidth)
                layout.DockWidth = dragged;
        }
        ImGui.SameLine(0, margin);
        // The timeline's ids are this actor's, so its popups and child windows stay its own; the dock's are shared, so it keeps
        // its scroll and popups when the actor changes.
        using var id = ImRaii.PushId(Key.Id);
        using var main = ImRaii.Child("##timeline-main", new Vector2(0, area.Y), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (main.Success)
            DrawMain();
    }

    /// <summary>The narrowest the timeline beside the dock gets, before scaling.</summary>
    private const float MainMinimum = 560;

    /// <summary>
    /// Right of the dock: the banners, then with no clip open where to pick one; with one the header, the action bar, the
    /// status line, the lanes beside the side panel, and the save row.
    /// </summary>
    private void DrawMain()
    {
        DrawPendingBanner();
        if (pending == PendingAction.None)
            autosaver.Draw(HasUnsavedChanges || reader.Busy);
        if (source == null)
        {
            DrawEmpty();
            return;
        }
        if (source.Document == null || editor == null)
        {
            DrawHeader(source);
            // Back in the header may have started reading the timeline Edit face left.
            if (source == null)
                return;
            Widgets.Banner("##timeline-read-only", FeedbackSeverity.Warning,
                "This timeline can't be edited. " + (source.ReadOnlyReason ?? "Its file could not be read."));
            ImGui.Spacing();
            loader.DrawStatus(dock.JobNotes());
            return;
        }
        DrawHeader(source);
        if (reader.Error is { } readError)
            Widgets.Banner("##timeline-reload-error", FeedbackSeverity.Error, "Reading the timeline again failed: " + readError);
        if (pending == PendingAction.None && ChangedCapture() is { } changed)
        {
            Widgets.Banner("##timeline-file-changed", FeedbackSeverity.Info, "This file changed in your mods.");
            if (ImGui.Button("Reload##timeline-file-changed"))
                Open(changed, source.Startup);
            ImGui.Spacing();
        }
        // Back in the header, or Reload, may have started reading a clip.
        if (source == null)
            return;
        CheckGizmo();
        toolbar.Draw(editor);
        statusLine.Draw("##timeline", toolbar.Notes(editor).Concat(timelinePreview.Notes(editor, source, saveFooter.RepairTarget)).Concat(dock.JobNotes())
            .Concat(host.OtherNotes(Key)));

        var avail = ImGui.GetContentRegionAvail();
        if (footerHeight <= 0)
            footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var height = Math.Max(Theme.Scaled(120), avail.Y - footerHeight - ImGui.GetStyle().ItemSpacing.Y);
        DrawBody(editor, avail.X, height);
        // Edit face in the side panel may have started reading the face.
        if (source == null)
            return;

        var footerStart = ImGui.GetCursorPosY();
        saveFooter.Draw(editor, source);
        footerHeight = Math.Max(1, ImGui.GetCursorPosY() - footerStart);

        pickers.Draw(editor);
        linkPicker.Draw(editor, false);
        var playhead = editor.Playhead;
        canvas.HandleKeys(editor, playBar.PlayKey);
        if (editor.Playhead != playhead)
            timelinePreview.PauseForStep();
        // Escape cancels a drag; it must not also close the window.
        HoldsEscape = editor.Gesture != TimelineGesture.None;
    }

    /// <summary>
    /// No clip open: the spinner line while one is read; a read that failed, with Back when Edit face couldn't open its face;
    /// else where to pick one. Then the loader's line with the dock's job.
    /// </summary>
    private void DrawEmpty()
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
            Widgets.Banner("##timeline-load-error", FeedbackSeverity.Error,
                (reader.ErrorAgain ? "The edit could not be opened again. " : "The timeline could not be read: ") + readError);
            ImGui.Spacing();
            // A face Edit face couldn't open: the timeline it was opened from waits with its edits.
            if (parkedClip != null)
            {
                DrawBack(null);
                ImGui.Spacing();
            }
        }
        else
            Widgets.EmptyState(FontAwesomeIcon.Running, "Pick an animation on the left", "What plays now, or any game animation.");
        loader.DrawStatus(dock.JobNotes());
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
    /// The header, two lines tall: the Live switch; Back after Edit face, the clip's name, which clears the selection, and
    /// under it its state and mod; on the right the frame and the time over the total, which open the length popup. A
    /// timeline that can't be edited shows its name alone.
    /// </summary>
    private void DrawHeader(AnimationTimelineSource current)
    {
        var style = ImGui.GetStyle();
        var start = ImGui.GetCursorPos();
        var height = ImGui.GetFrameHeight() * 2 + style.ItemSpacing.Y;
        var editable = current.Document != null && editor != null;
        var left = start.X;
        if (editable)
        {
            var switchWidth = SwitchWidth();
            DrawLive(editor!, new Vector2(switchWidth, height));
            ImGui.SetCursorPos(new Vector2(start.X + switchWidth + Theme.Scaled(6), start.Y));
            DrawPreview(current, new Vector2(switchWidth, height));
            left = start.X + switchWidth * 2 + Theme.Scaled(6) + Theme.Scaled(10);
        }
        // The frame and time first, so the name knows where to stop.
        var timeLeft = editable ? DrawTime(editor!, start.Y, height) : ImGui.GetContentRegionMax().X;
        var room = Math.Max(Theme.Scaled(40), timeLeft - Theme.Scaled(12) - left);

        ImGui.SetCursorPos(new Vector2(left, start.Y));
        if (parkedClip != null)
        {
            DrawBack(current);
            ImGui.SameLine(0, Theme.Gap);
        }
        Widgets.Icon(current.Standalone || current.Document?.Face != null ? FontAwesomeIcon.Smile : FontAwesomeIcon.Running, Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        bool name;
        var nameWidth = Math.Max(1, Math.Min(ImGui.CalcTextSize(title).X, left + room - ImGui.GetCursorPosX()));
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Label))
            name = ImGui.Selectable(title + "##timeline-clip-name", false, ImGuiSelectableFlags.None, new Vector2(nameWidth, 0));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(editor != null ? title + "\nClick to clear the selection." : title);
        if (name)
            editor?.ClearSelection();
        ImGui.SetCursorPos(new Vector2(left, start.Y + ImGui.GetFrameHeight() + style.ItemSpacing.Y));
        ImGui.AlignTextToFramePadding();
        var at = ImGui.GetCursorScreenPos();
        ImGui.PushClipRect(at, at + new Vector2(room, ImGui.GetFrameHeight()), true);
        ImGui.TextColored(Theme.Muted, subtitle);
        ImGui.PopClipRect();
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + (editable ? height : ImGui.GetFrameHeight() * 2) + style.ItemSpacing.Y));
    }

    /// <summary>The line under "Live off".</summary>
    private const string LiveOffHint = "Click to show edits";

    /// <summary>The line under "LIVE".</summary>
    private const string LiveOnHint = "Playing animation";

    /// <summary>The lines under "Preview": before it is built, while it builds, and once it was built for this clip.</summary>
    private const string PreviewHint = "Build and play", PreviewBuildingHint = "Building", PreviewBuiltHint = "Built, play again";

    /// <summary>The Live and Preview switches' width: room for their longest fixed text, so they keep their size as they change.</summary>
    private static float SwitchWidth() => Math.Max(Theme.Scaled(96),
        new[] { LiveOffHint, LiveOnHint, PreviewHint, PreviewBuiltHint }.Max(text => ImGui.CalcTextSize(text).X) + Theme.Scaled(20));

    /// <summary>
    /// Preview, as big as Live beside it: builds every actor's edited clip and plays it. Outlined in purple once a preview was
    /// built for this clip; greyed while it can't start, saying why.
    /// </summary>
    private void DrawPreview(AnimationTimelineSource current, Vector2 size)
    {
        var blocked = timelinePreview.PreviewBlocked();
        var enabled = blocked == null;
        var built = timelinePreview.BuiltOnce(current);
        var at = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##timeline-preview", size) && enabled;
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (hovered)
            ImGui.SetTooltip((blocked ?? "Build the edited clips, redraw the GPose actors with them and play them. Nothing is saved.") + "\n" +
                             TimelinePreview.PreviewHints);
        var draw = ImGui.GetWindowDrawList();
        var rounding = Theme.Scaled(6);
        var alpha = enabled ? 1f : .45f;
        var edge = built ? Theme.PreviewBuilt : Theme.Info;
        draw.AddRectFilled(at, at + size, TimelineUi.U32(Theme.WithAlpha(edge, .1f + (hovered && enabled ? .1f : 0))), rounding);
        draw.AddRect(at, at + size, TimelineUi.U32(Theme.WithAlpha(edge, (built ? 1f : .6f) * alpha)), rounding, ImDrawFlags.None, Theme.Scaled(2));
        const string label = "Preview";
        var hint = timelinePreview.Building ? PreviewBuildingHint : built ? PreviewBuiltHint : PreviewHint;
        var line = ImGui.GetTextLineHeight();
        string glyph;
        float glyphWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            glyph = FontAwesomeIcon.Video.ToIconString();
            glyphWidth = ImGui.CalcTextSize(glyph).X;
        }
        var gap = Theme.Scaled(5);
        var top = at.Y + (size.Y - line * 2 - Theme.Scaled(2)) / 2;
        var labelLeft = at.X + (size.X - glyphWidth - gap - ImGui.CalcTextSize(label).X) / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(new Vector2(labelLeft, top), TimelineUi.U32(Theme.WithAlpha(edge, alpha)), glyph);
        draw.AddText(new Vector2(labelLeft + glyphWidth + gap, top), TimelineUi.U32(Theme.WithAlpha(Theme.Text, alpha)), label);
        draw.AddText(new Vector2(at.X + (size.X - ImGui.CalcTextSize(hint).X) / 2, top + line + Theme.Scaled(2)),
            TimelineUi.U32(Theme.WithAlpha(Theme.Muted, alpha)), hint);
        if (clicked)
            host.Transport.Preview();
    }

    /// <summary>
    /// The Live switch, big and <paramref name="size"/>, for every actor at once: a red dot and "Live off" while off, green
    /// "LIVE" and "Playing animation" while on; greyed while it can come on for none, saying why.
    /// </summary>
    private void DrawLive(TimelineEditor current, Vector2 size)
    {
        var transport = host.Transport;
        var on = transport.AnyLive;
        var blocked = on ? null : transport.LiveBlocked;
        var enabled = (on || blocked == null) && !session.ReadOnly;
        var at = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##timeline-live", size) && enabled;
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        if (hovered)
            ImGui.SetTooltip(TimelineWindowViews.LiveTip(on, transport.LiveActors, blocked, timelinePreview.LiveHint(current)));
        var draw = ImGui.GetWindowDrawList();
        var rounding = Theme.Scaled(6);
        var alpha = enabled ? 1f : .45f;
        var colour = on ? Theme.Success : Theme.Error;
        draw.AddRectFilled(at, at + size, TimelineUi.U32(Theme.WithAlpha(colour, (on ? .32f : .1f) + (hovered && enabled ? .1f : 0))), rounding);
        draw.AddRect(at, at + size, TimelineUi.U32(Theme.WithAlpha(colour, (on ? 1f : .6f) * alpha)), rounding, ImDrawFlags.None, Theme.Scaled(2));
        var label = on ? "LIVE" : "Live off";
        var hint = on ? LiveOnHint : LiveOffHint;
        var line = ImGui.GetTextLineHeight();
        var dot = Theme.Scaled(4);
        var labelWidth = ImGui.CalcTextSize(label).X + dot * 2 + Theme.Scaled(5);
        var top = at.Y + (size.Y - line * 2 - Theme.Scaled(2)) / 2;
        var labelLeft = at.X + (size.X - labelWidth) / 2;
        draw.AddCircleFilled(new Vector2(labelLeft + dot, top + line / 2), dot, TimelineUi.U32(Theme.WithAlpha(on ? Theme.Success : Theme.Error, alpha)));
        draw.AddText(new Vector2(labelLeft + dot * 2 + Theme.Scaled(5), top), TimelineUi.U32(Theme.WithAlpha(Theme.Text, alpha)), label);
        // Long actor names are cut at the switch's edge, never drawn past it.
        var pad = Theme.Scaled(6);
        draw.PushClipRect(at + new Vector2(pad, 0), at + size - new Vector2(pad, 0), true);
        draw.AddText(new Vector2(Math.Max(at.X + pad, at.X + (size.X - ImGui.CalcTextSize(hint).X) / 2), top + line + Theme.Scaled(2)),
            TimelineUi.U32(Theme.WithAlpha(Theme.Muted, alpha)), hint);
        draw.PopClipRect();
        if (clicked)
            transport.ToggleLive();
    }

    /// <summary>
    /// The frame over the total frames and the time over the total time, right-aligned at the header's top, <paramref name="height"/>
    /// tall; a click opens the length popup. Each number sits in a slot as wide as the total's, and the time shows whole seconds
    /// while it plays, so the text keeps still. Returns where it starts.
    /// </summary>
    private float DrawTime(TimelineEditor current, float top, float height)
    {
        // While the clip drives the playhead (a preview, Live, Follow or the play group), whole seconds.
        var pair = TimelineWindowViews.TimePair(current.Playhead, current.Document.Length, current.PlayTime != null);
        var frameSlot = ImGui.CalcTextSize(pair.Frames).X;
        var timeSlot = ImGui.CalcTextSize(pair.Length).X;
        var frameTail = ImGui.CalcTextSize(" / " + pair.Frames).X;
        var timeTail = ImGui.CalcTextSize(" / " + pair.Length).X;
        var width = Math.Max(frameSlot + frameTail, timeSlot + timeTail);
        var left = ImGui.GetContentRegionMax().X - width;
        ImGui.SetCursorPos(new Vector2(left, top));
        if (ImGui.InvisibleButton("##timeline-time", new Vector2(width, height)))
            ImGui.OpenPopup("##timeline-length");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.FixedLength ? "An expression's timeline keeps its length."
                : current.Document.LengthChanged ? "The new length. Saving resamples the motion to it. Click to change it."
                : "Click to change the length.");
        var total = current.Document.LengthChanged ? Theme.Warning : Theme.Muted;
        var line = ImGui.GetTextLineHeight();
        var gap = (height - line * 2) / 3;
        var right = left + width;
        TimeLine(new Vector2(right - frameTail - ImGui.CalcTextSize(pair.Frame).X, top + gap), pair.Frame, Theme.Text);
        TimeLine(new Vector2(right - frameTail, top + gap), " / " + pair.Frames, total);
        TimeLine(new Vector2(right - timeTail - ImGui.CalcTextSize(pair.Time).X, top + gap * 2 + line), pair.Time, Theme.Text);
        TimeLine(new Vector2(right - timeTail, top + gap * 2 + line), " / " + pair.Length, total);
        using (var popup = ImRaii.Popup("##timeline-length"))
        {
            if (popup.Success)
                lengthPopup.Draw(current);
        }
        return left;
    }

    /// <summary>One piece of the header's frame and time, at <paramref name="at"/> in the window.</summary>
    private static void TimeLine(Vector2 at, string text, Vector4 colour)
    {
        ImGui.SetCursorPos(at);
        ImGui.TextColored(colour, text);
    }

    /// <summary> The inline question before unsaved changes are lost: Discard or Keep editing, never a modal. </summary>
    private void DrawPendingBanner()
    {
        if (pending == PendingAction.None)
            return;
        if (pendingOpen is not { } next)
            return;
        var text = pendingAgain == null && source != null && next.Capture.Id == source.Capture.Id && next.Startup == source.Startup
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
        if (pendingOpen is not { } next)
        {
            pending = PendingAction.None;
            return;
        }
        Load(next.Capture, next.Startup, false, pendingAgain);
    }

    /// <summary> The lanes on the left, the curves under them when shown, and the side panel on the right, with a splitter between them. </summary>
    private void DrawBody(TimelineEditor current, float width, float height)
    {
        var layout = host.Layout;
        var splitter = Theme.Scaled(6);
        if (layout.SideWidth <= 0)
            layout.SideWidth = Theme.Scaled(340);
        var minimumCanvas = Theme.Scaled(320);
        var minimumProperties = Theme.Scaled(260);
        // The width set stays as it was set: a narrow window only narrows the panel while it is narrow.
        var side = Math.Clamp(layout.SideWidth, minimumProperties, Math.Max(minimumProperties, width - minimumCanvas - splitter));
        var canvasWidth = Math.Max(Theme.Scaled(120), width - side - splitter);
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
        if (dragged != canvasWidth)
            layout.SideWidth = side - (dragged - canvasWidth);
        ImGui.SameLine(0, 0);
        using var panel = ImRaii.Child("##timeline-properties", new Vector2(0, height), true);
        if (panel.Success && source != null)
            sidePanel.Draw(current);
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
