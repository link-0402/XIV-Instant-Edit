using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The timeline editor for one animation clip: its motion, expressions, lip sync, sounds, effects
/// and props on lanes like a video editor, its length, a side panel (properties, source, animated
/// bones, saved edits, recorder), pickers and imports, a loader that plays any animation on the GPose
/// actor, a save footer and a GPose preview. Slow work runs on tasks polled every frame, so the draw
/// path never reads files. Split by concern: each part lives in its own partial file.
/// </summary>
public sealed partial class TimelineWindow : Window, IDisposable, IBonesOverlay
{
    private enum PendingAction : byte { None, Close, Switch }

    private readonly TimelineSession session;
    private readonly MotionDecode decode;
    private readonly EffectLookEditor effectLooks;
    private readonly SoundFiles soundFiles;
    private readonly TimelinePickers pickers;
    private readonly SoundInspector soundInspector;
    private readonly TimelinePreview timelinePreview;
    private readonly BonePosing posing;
    private readonly BoneEditor boneEditor;
    private readonly PoseSourcePanel poseSources;
    private readonly string? animationError;
    // Plays a sound file in game for the sound inspector's listen buttons; null when unavailable.
    private readonly GameSoundPlayer? soundPlayer;

    private Task<AnimationTimelineSource>? loadTask;
    private CancellationTokenSource? loadCancel;
    private (AnimationCapture Capture, bool Startup)? loading;
    private bool loadKeepsView;
    // The saved edit a load brings back with its bone layers (Edit again), until it ends.
    private AnimationEditJournal? loadingAgain;
    // The load follows an undo and may bring back the layers of the edit it went back to.
    private bool loadingAfterUndo;
    private string? loadError;
    // The failed load was an Edit again.
    private bool loadErrorAgain;
    private string title = string.Empty;
    private string subtitle = string.Empty;
    private bool canReplace;

    private PendingAction pending;
    private (AnimationCapture Capture, bool Startup)? pendingOpen;
    // The saved edit the pending open brings back (Edit again).
    private AnimationEditJournal? pendingAgain;
    private bool closeConfirmed;
    private float footerHeight;
    private float propertiesWidth;
    private bool disposed;
    // The catalog's face lengths with an expression's own face, kept while neither changes.
    private IReadOnlyDictionary<string, int>? framesKnown;
    private AnimationTimelineSource? framesSource;
    private IReadOnlyDictionary<string, int>? framesMerged;

    internal TimelineWindow(AnimationEditService? animations, string? animationError, TimelineCatalog catalog,
        AnimationPreviewService preview, AnimationPlayback playback, MotionLiveDisplay? motionDisplay, AnimationRecorder recorder, IDataManager data,
        IFramework framework, ITextureProvider textures, IPluginLog log, GameSoundPlayer? soundPlayer = null)
        : base("Timeline###XIVInstantEditTimeline")
    {
        session = new TimelineSession(animations, catalog, preview, playback, motionDisplay, data, framework, log,
            () => loadTask != null || saveTask != null || pendingSaveId != null || undoPending || soundCheck != null || soundAsk != null);
        decode = new MotionDecode(session);
        effectLooks = new EffectLookEditor(session);
        soundFiles = new SoundFiles(session);
        pickers = new TimelinePickers(session, soundFiles);
        soundInspector = new SoundInspector(session, soundFiles, pickers, soundPlayer);
        timelinePreview = new TimelinePreview(session, decode, effectLooks, soundFiles, () => repairTarget);
        posing = new BonePosing(session, decode);
        boneEditor = new BoneEditor(session, decode, posing, this);
        poseSources = new PoseSourcePanel(session, decode, posing, recorder);
        this.soundPlayer = soundPlayer;
        this.animationError = animationError;
        this.textures = textures;
        bonesWindow = new BonesWindow(this);
        overlayWindow = new BonesOverlay(this);

        Size = new Vector2(1000, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        // The canvas reads the wheel itself; the window around it must not scroll.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        AllowPinning = true;
        RespectCloseHotkey = true;
    }

    public void DrawFileDialog() => session.FileDialog.Draw();

    // The shared state under the names TimelineWindow.Overlay.cs reads. It stays a partial of this
    // window until the Body Physics work in it is committed; then it can take a TimelineSession too.
    private AnimationEditService? animations => session.Animations;
    private AnimationPlayback playback => session.Playback;
    private MotionLiveDisplay? motionDisplay => session.MotionDisplay;
    private AnimationTimelineSource? source { get => session.Source; set => session.Source = value; }
    private TimelineEditor? editor { get => session.Editor; set => session.Editor = value; }
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
    private (MotionTransform[] Local, MotionTransform[] Model)? MotionPose(AnimationTimelineSource loaded, int frame) => posing.MotionPose(loaded, frame);
    private List<PoseTarget>? Targets(MotionLayer layer, int frame, SkeletonDescription skeleton, AnimationTimelineSource loaded, TimelineDocument doc) =>
        posing.Targets(layer, frame, skeleton, loaded, doc);
    private static BoneState StateOf(string bone, MotionLayer? layer, int frame, HashSet<string>? animated) => BonePosing.StateOf(bone, layer, frame, animated);
    private static string StateText(BoneState state, MotionLayer? layer) => BonePosing.StateText(state, layer);
    private static Vector4 StateColour(BoneState state, MotionLayer? layer) => BonePosing.StateColour(state, layer);
    private static bool IsBelow(SkeletonDescription skeleton, int bone, int ancestor) => BonePosing.IsBelow(skeleton, bone, ancestor);
    private static float MotionEnd(AnimationTimelineSource loaded, TimelineDocument doc) => BonePosing.MotionEnd(loaded, doc);
    private bool characterAxes => boneEditor.CharacterAxes;

    bool IBonesOverlay.Shown => OverlayWanted;
    bool IBonesOverlay.OnCharacter { get => showOnCharacter; set => showOnCharacter = value; }
    bool IBonesOverlay.GizmoMoves { get => gizmoMove; set => gizmoMove = value; }
    string? IBonesOverlay.GizmoWaits(MotionLayer layer, int frame) => GizmoOperation(layer, frame, out var reason) == null ? reason : null;
    private bool ReadOnly => session.ReadOnly;
    private AnimationTimelineSource Fresh(AnimationTimelineSource current) => session.Fresh(current);

    private TimelineCatalog catalog => session.Catalog;
    private AnimationPreviewService preview => session.Preview;
    private IDataManager data => session.Data;
    private IFramework framework => session.Framework;
    private IPluginLog log => session.Log;
    private FileDialogManager fileDialog => session.FileDialog;

    /// <summary> Unsaved edits of an editable timeline. </summary>
    private bool HasUnsavedChanges => editor is { Dirty: true } && source?.Document != null;

    /// <summary>Opens the window as it is: its clip, or with none the loader.</summary>
    internal void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    /// <summary>
    /// Shows the timeline of <paramref name="capture"/>'s clip, or of its startup. The clip already
    /// shown is read again only when its file changed (<see cref="ChangedCapture"/>). With unsaved
    /// changes it first asks, inline, whether to discard them.
    /// </summary>
    internal void Open(AnimationCapture capture, bool startup)
    {
        IsOpen = true;
        BringToFront();
        if (animations == null)
            return;
        startup &= capture.Startup != null;
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
        IsOpen = true;
        BringToFront();
        if (animations is not { } service || journal.Request.Timeline is not { } edit)
            return;
        capture ??= AnimationEditService.EditAgainCapture(journal, service.Observer.History);
        if (HasUnsavedChanges)
        {
            pending = PendingAction.Switch;
            pendingOpen = (capture, edit.Startup);
            pendingAgain = journal;
            return;
        }
        Load(capture, edit.Startup, false, journal);
    }

    public override void OnClose()
    {
        // Closing with unsaved changes asks first; the window stays open until Discard.
        if (!closeConfirmed && HasUnsavedChanges)
        {
            pending = PendingAction.Close;
            pendingOpen = null;
            IsOpen = true;
            return;
        }
        closeConfirmed = false;
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        loaderWaiting = null;
        loaderMessage = null;
        livePoseReading = false;
        livePoseNote = null;
        livePoseClearable = null;
        // The listener stays on while the Animations tab still shows it.
        if (mainListens?.Invoke() != true)
            animations?.StopObservation();
        // The Bones window poses this window's clip; it goes with it, and so do its bones on the character.
        bonesWindow.IsOpen = false;
        EndGizmo(true);
        timelinePreview.Stop();
        decode.Forget();
        poseSources.Drop();
        loadCancel?.Cancel();
        loadTask = null;
        loading = null;
        loadingAgain = null;
        loadingAfterUndo = false;
        loadError = null;
        source = null;
        session.CatalogKey = null;
        motionBase = null;
        motionCarry = null;
        effectLooks.Forget();
        ForgetSounds();
        // A sound played to listen to stops being served with the window.
        _ = soundPlayer?.StopAsync();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        loadCancel?.Cancel();
        loadCancel?.Dispose();
        loadCancel = null;
        decode.Forget();
        poseSources.Drop();
    }

    private bool Showing(AnimationCapture capture, bool startup)
    {
        var shown = loading ?? (source is { } current ? (current.Capture, current.Startup) : null);
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
        if (animations == null || source is not { } current || loadTask != null || saveTask != null || pendingSaveId != null ||
            undoPending || preview.Active || preview.Busy || timelinePreview.Building)
            return null;
        return TimelineWindowViews.ChangedCapture(animations.Observer.History, current);
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
        if (playback.Active && timelinePreview.Fingerprint == null && !liveWanted && !preview.Active && !timelinePreview.Building)
            timelinePreview.StopTemporaryMod();
        else
            timelinePreview.Stop();
        loadCancel?.Cancel();
        loadCancel?.Dispose();
        loadCancel = new CancellationTokenSource();
        var token = loadCancel.Token;
        loading = (capture, startup);
        loadingAgain = again;
        loadingAfterUndo = afterUndo;
        loadKeepsView = keepView;
        loadError = null;
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        if (!keepView)
        {
            source = null;
            session.CatalogKey = null;
            title = AnimationPresentation.AnimationName(capture, startup);
            // Another clip, or this one opened afresh: its own file is the motion to edit.
            motionBase = null;
            motionCarry = null;
        }
        // The preview's temporary mod must be gone first, or the read would get the previewed file.
        var ending = timelinePreview.Stopping ?? Task.CompletedTask;
        loadTask = Task.Run(async () =>
        {
            await ending;
            await preview.StopAsync();
            return again != null ? await service.LoadTimelineAgainAsync(again, capture, token)
                : afterUndo ? await service.LoadTimelineAfterUndoAsync(capture, startup, token)
                : await service.LoadTimelineAsync(capture, startup, token);
        }, token);
    }

    private void PollLoad()
    {
        if (loadTask is not { IsCompleted: true } task)
            return;
        loadTask = null;
        loading = null;
        var again = loadingAgain;
        loadingAgain = null;
        var afterUndo = loadingAfterUndo;
        loadingAfterUndo = false;
        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            loadError = TimelineSession.ErrorText(task.Exception);
            loadErrorAgain = again != null;
            return;
        }
        Apply(task.Result, loadKeepsView, again, afterUndo);
    }

    private void Apply(AnimationTimelineSource loaded, bool keepView, AnimationEditJournal? again = null, bool afterUndo = false)
    {
        var sameClip = source != null && TimelineSession.ClipKey(source) == TimelineSession.ClipKey(loaded);
        // Edit again, or an undo back to a layered edit: the saved layers apply to the motion from before
        // their edit while this clip stays open.
        if ((again != null || afterUndo) && loaded.BaseMotion is { } kept && loaded.BaseHash is { } keptHash)
            motionBase = (TimelineSession.ClipKey(loaded), kept, loaded.BaseFingerprint ?? string.Empty, keptHash);
        // A clip read again after this window saved bone layers into it keeps editing them on the motion
        // from before: the carried layers come back, and the base stays the motion they apply to.
        loaded = WithMotionBase(loaded);
        source = loaded;
        pickers.ClipLoaded();
        canReplace = AnimationResources.CanReplace(loaded.Resource);
        session.CatalogKey = TimelineWindowViews.CatalogKey(loaded.Capture.LoadedResourcePaths, loaded.Clip.GamePath, loaded.Resource.ModRoot);
        title = AnimationPresentation.AnimationName(loaded.Capture, loaded.Startup);
        subtitle = $"{AnimationPresentation.AnimationState(loaded.Capture, loaded.Startup)} · " +
                   (loaded.Resource.ModName ?? loaded.Resource.ModDirectory ?? "Game Data");
        labelDocument = null;
        effectLooks.ForgetEdit();
        soundInspector.ForgetEdit();
        if (!sameClip)
        {
            effectLooks.Forget();
            ForgetSounds();
            modName = TimelineWindowViews.DefaultModName(AnimationPresentation.DefaultModName(loaded.Capture));
            destination = AnimationDestination.NewMod;
            saveError = null;
            saveMessage = null;
            lastSaveId = null;
            scrollY = 0;
            repairTarget = null;
            animatedBoneFilter = string.Empty;
            livePoseNote = null;
            livePoseClearable = null;
        }
        if (again != null && loaded.BaseMotion != null)
        {
            // Saving again writes over the edit's file; the new save's journal keeps it for Undo.
            destination = canReplace ? AnimationDestination.InPlace : AnimationDestination.NewMod;
            saveMessage = "The saved layers are back.";
        }
        if (loaded.Document is not { } doc)
            return;
        var carried = TakeCarriedLayers(loaded);
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
                editor.FoldAll();
        }
        // After an undo the layers stay as unsaved edits: one undo step away from the file.
        if (carried is { Unsaved: true } unsaved)
            editor.Commit(unsaved.Document);
    }

    /// <summary>
    /// Facial motions' own lengths: the catalog's, read from the game's face files, with the face an
    /// expression plays as the collection resolves it, which a mod may have made a moving one.
    /// The same instance until either changes, so the layout is rebuilt only then.
    /// </summary>
    private IReadOnlyDictionary<string, int> MotionFrames(AnimationTimelineSource current)
    {
        var known = catalog.Data.MotionFrames;
        if (!current.Standalone)
            return known;
        if (!ReferenceEquals(known, framesKnown) || !ReferenceEquals(current, framesSource) || framesMerged == null)
        {
            framesKnown = known;
            framesSource = current;
            framesMerged = known.SetItems(current.OwnFrames);
        }
        return framesMerged;
    }

    public override void Draw()
    {
        RespectCloseHotkey = true;
        if (animations == null)
        {
            Widgets.Banner("##timeline-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation editing is unavailable.");
            if (drawRecorder != null)
            {
                ImGui.Spacing();
                drawRecorder();
            }
            return;
        }
        // The listener runs while this window is open: the loader and the clip's skeleton choice read it.
        animations.StartObservation();
        PollLoad();
        PollSave();
        pickers.PollImport();
        timelinePreview.Poll();
        pickers.PollLengths();
        PollLibrary();
        PollLoader();
        // Every frame: the bone rows' order, Live and the save all read the clip's match.
        if (source != null)
            source = Fresh(source);

        DrawPendingBanner();
        if (source == null)
        {
            DrawNothingOpen();
            return;
        }
        DrawHeader(source);
        if (source.Document == null || editor == null)
        {
            ImGui.Spacing();
            Widgets.Banner("##timeline-read-only", FeedbackSeverity.Warning,
                "This timeline can't be edited. " + (source.ReadOnlyReason ?? "Its file could not be read."));
            ImGui.Spacing();
            DrawLoadButton();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "Load another animation");
            DrawLoaderStatus();
            return;
        }
        PollLivePose(editor, source);
        if (loadError != null)
            Widgets.Banner("##timeline-reload-error", FeedbackSeverity.Error, "Reading the timeline again failed: " + loadError);
        if (pending == PendingAction.None && ChangedCapture() is { } changed)
        {
            Widgets.Banner("##timeline-file-changed", FeedbackSeverity.Info, "This file changed in your mods.");
            if (ImGui.Button("Reload##timeline-file-changed"))
                Open(changed, source.Startup);
            ImGui.Spacing();
        }
        if (session.CatalogKey != null)
            catalog.Request(session.CatalogKey);
        editor.MotionFrames = MotionFrames(source);
        editor.ResidentMotions = catalog.Data.ResidentMotions;
        editor.BoneOrder = source.Clip.Resolution?.Selected?.Skeleton is { } skeleton ? BoneTree(skeleton).Index : null;

        timelinePreview.Update(editor, source);
        CheckGizmo();
        DrawToolbar(editor);
        timelinePreview.DrawBar(editor, source);
        DrawMessages(editor);

        var avail = ImGui.GetContentRegionAvail();
        if (footerHeight <= 0)
            footerHeight = ImGui.GetFrameHeightWithSpacing() * 3 + ImGui.GetStyle().ItemSpacing.Y * 2;
        var height = Math.Max(Theme.Scaled(120), avail.Y - footerHeight - ImGui.GetStyle().ItemSpacing.Y);
        DrawBody(editor, avail.X, height);
        // A link in the side panel (the Source page's startup Open) may have started reading another clip.
        if (source == null)
            return;

        var footerStart = ImGui.GetCursorPosY();
        DrawSaveFooter(editor, source);
        footerHeight = Math.Max(1, ImGui.GetCursorPosY() - footerStart);

        pickers.Draw(editor);
        HandleKeys(editor);
        // Escape cancels a drag; it must not also close the window.
        RespectCloseHotkey = editor.Gesture == TimelineGesture.None;
    }

    /// <summary>
    /// Before a clip is open: the loader fills the window, with the saved edits and the recorder
    /// beside it in tabs.
    /// </summary>
    private void DrawNothingOpen()
    {
        if (loadTask != null)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, title.Length > 0 ? $"Reading the timeline of {title}" : "Reading the timeline");
            return;
        }
        if (loadError != null)
        {
            Widgets.Banner("##timeline-load-error", FeedbackSeverity.Error,
                (loadErrorAgain ? "The edit could not be opened again. " : "The timeline could not be read: ") + loadError);
            ImGui.Spacing();
        }
        DrawLoaderStatus();
        using var tabs = ImRaii.TabBar("##timeline-start");
        if (!tabs.Success)
            return;
        if (ImGui.BeginTabItem("Load an animation"))
        {
            Widgets.HintWrapped("Pick an animation to play on your GPose character and edit it here, " +
                                "or open one from the Animations tab.");
            ImGui.Spacing();
            DrawLoader(ImGui.GetContentRegionAvail().Y);
            ImGui.EndTabItem();
        }
        DrawEditsTab();
        if (drawRecorder != null && ImGui.BeginTabItem("Record live pose"))
        {
            DrawPage(drawRecorder);
            ImGui.EndTabItem();
        }
    }

    /// <summary> The clip's name and state, the mod it comes from and its length. </summary>
    private void DrawHeader(AnimationTimelineSource current)
    {
        Widgets.Icon(current.Standalone ? FontAwesomeIcon.Smile : FontAwesomeIcon.Running, Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, title);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, subtitle);
        if (current.Document == null || editor == null)
            return;
        RefreshDocumentTexts(editor.Document);
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(16);
        var right = ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(lengthText).X;
        ImGui.SameLine(Math.Max(after, right));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(editor.Document.LengthChanged ? Theme.Warning : Theme.Muted, lengthText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(editor.Document.LengthChanged ? "The new length. Saving resamples the motion to it."
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
            IsOpen = false;
            return;
        }
        Load(next.Capture, next.Startup, false, pendingAgain);
    }

    /// <summary> The lanes on the left and the side panel on the right, with a splitter between them. </summary>
    private void DrawBody(TimelineEditor current, float width, float height)
    {
        var splitter = Theme.Scaled(6);
        if (propertiesWidth <= 0)
            propertiesWidth = Theme.Scaled(320);
        var minimumCanvas = Theme.Scaled(320);
        var minimumProperties = Theme.Scaled(220);
        propertiesWidth = Math.Clamp(propertiesWidth, minimumProperties, Math.Max(minimumProperties, width - minimumCanvas - splitter));
        var canvasWidth = Math.Max(Theme.Scaled(120), width - propertiesWidth - splitter);
        DrawCanvas(current, new Vector2(canvasWidth, height));
        ImGui.SameLine(0, 0);
        var dragged = canvasWidth;
        Widgets.Splitter("##timeline-splitter", ref dragged, minimumCanvas, Math.Max(minimumCanvas, width - minimumProperties - splitter), height);
        propertiesWidth -= dragged - canvasWidth;
        ImGui.SameLine(0, 0);
        using var panel = ImRaii.Child("##timeline-properties", new Vector2(0, height), true);
        if (panel.Success && source != null)
            DrawSidePanel(current, source);
    }

    /// <summary> Forgets the sounds read for another clip. </summary>
    private void ForgetSounds()
    {
        soundFiles.Forget();
        pathOrigins.Clear();
        // A save's check of the mod, or its question, belongs to the clip it was for.
        soundCheck = null;
        soundCheckDocument = null;
        soundAsk = null;
        soundInPlace = null;
        // So is a listen's error.
        soundInspector.Forget();
    }

}
