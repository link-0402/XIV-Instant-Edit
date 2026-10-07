using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The timeline editor for one animation clip: its motion, expressions, lip sync, sounds, effects
/// and props on lanes like a video editor, its length, a properties panel, pickers and imports, a
/// save footer like the rebakes' and a GPose preview. Slow work runs on tasks polled every frame,
/// so the draw path never reads files. Split by concern: canvas, properties, pickers and imports,
/// save footer and preview bar each live in their own partial file.
/// </summary>
public sealed partial class TimelineWindow : Window, IDisposable
{
    private enum PendingAction : byte { None, Close, Switch }

    private readonly AnimationEditService? animations;
    private readonly string? animationError;
    private readonly TimelineCatalog catalog;
    private readonly AnimationPreviewService preview;
    private readonly AnimationPlayback playback;
    // Shows bone layers live on the GPose actor; null when animation editing is unavailable.
    private readonly MotionLiveDisplay? motionDisplay;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly FileDialogManager fileDialog = new();

    private AnimationTimelineSource? source;
    private TimelineEditor? editor;
    private TimelineCatalogKey? catalogKey;
    private Task<AnimationTimelineSource>? loadTask;
    private CancellationTokenSource? loadCancel;
    private (AnimationCapture Capture, bool Startup)? loading;
    private bool loadKeepsView;
    // The saved edit a load brings back with its bone layers (Edit again), until it ends.
    private AnimationEditJournal? loadingAgain;
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
        IFramework framework, ITextureProvider textures, IPluginLog log)
        : base("Timeline###XIVInstantEditTimeline")
    {
        this.animations = animations;
        this.recorder = recorder;
        this.animationError = animationError;
        this.catalog = catalog;
        this.preview = preview;
        this.playback = playback;
        this.motionDisplay = motionDisplay;
        this.data = data;
        this.framework = framework;
        this.textures = textures;
        this.log = log;
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

    public void DrawFileDialog() => fileDialog.Draw();

    /// <summary> Unsaved edits of an editable timeline. </summary>
    private bool HasUnsavedChanges => editor is { Dirty: true } && source?.Document != null;

    /// <summary> Whether the document must not change now: nothing to edit, or a save, undo or reload is running. </summary>
    private bool ReadOnly => source?.Document == null || loadTask != null || saveTask != null || pendingSaveId != null || undoPending;

    /// <summary>
    /// Shows the timeline of <paramref name="capture"/>'s clip, or of its startup. With unsaved
    /// changes to another clip it first asks, inline, whether to discard them.
    /// </summary>
    internal void Open(AnimationCapture capture, bool startup)
    {
        IsOpen = true;
        BringToFront();
        if (animations == null)
            return;
        startup &= capture.Startup != null;
        if (Showing(capture, startup))
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
        // The Bones window poses this window's clip; it goes with it, and so do its bones on the character.
        bonesWindow.IsOpen = false;
        EndGizmo(true);
        StopPreview();
        ForgetDecode();
        DropPose();
        loadCancel?.Cancel();
        loadTask = null;
        loading = null;
        loadingAgain = null;
        loadError = null;
        source = null;
        catalogKey = null;
        motionBase = null;
        motionCarry = null;
        ForgetEffects();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        loadCancel?.Cancel();
        loadCancel?.Dispose();
        loadCancel = null;
        ForgetDecode();
        DropPose();
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

    /// <summary> A key for "the same clip", which survives a reload after a save. </summary>
    private static string ClipKey(AnimationTimelineSource current) => $"{current.Capture.Id}|{current.Startup}|{current.Clip.GamePath}";

    /// <summary>
    /// Reads the clip's timeline on a task. <paramref name="keepView"/> reloads the clip already
    /// shown after a save or undo, keeping its view and playhead. With <paramref name="again"/> the
    /// clip comes with that saved edit's bone layers back (Edit again).
    /// </summary>
    private void Load(AnimationCapture capture, bool startup, bool keepView, AnimationEditJournal? again = null)
    {
        if (animations is not { } service)
            return;
        StopPreview();
        loadCancel?.Cancel();
        loadCancel?.Dispose();
        loadCancel = new CancellationTokenSource();
        var token = loadCancel.Token;
        loading = (capture, startup);
        loadingAgain = again;
        loadKeepsView = keepView;
        loadError = null;
        pending = PendingAction.None;
        pendingOpen = null;
        pendingAgain = null;
        if (!keepView)
        {
            source = null;
            catalogKey = null;
            title = AnimationPresentation.AnimationName(capture, startup);
            // Another clip, or this one opened afresh: its own file is the motion to edit.
            motionBase = null;
            motionCarry = null;
        }
        // The preview's temporary mod must be gone first, or the read would get the previewed file.
        var ending = previewStop ?? Task.CompletedTask;
        loadTask = Task.Run(async () =>
        {
            await ending;
            await preview.StopAsync();
            return again == null
                ? await service.LoadTimelineAsync(capture, startup, token)
                : await service.LoadTimelineAgainAsync(again, capture, token);
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
        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            loadError = ErrorText(task.Exception);
            loadErrorAgain = again != null;
            return;
        }
        Apply(task.Result, loadKeepsView, again);
    }

    private void Apply(AnimationTimelineSource loaded, bool keepView, AnimationEditJournal? again = null)
    {
        var sameClip = source != null && ClipKey(source) == ClipKey(loaded);
        // Edit again: the saved layers apply to the motion from before their edit while this clip stays open.
        if (again != null && loaded.BaseMotion is { } kept && loaded.BaseHash is { } keptHash)
            motionBase = (ClipKey(loaded), kept, loaded.BaseFingerprint ?? string.Empty, keptHash);
        // A clip read again after this window saved bone layers into it keeps editing them on the motion
        // from before: the carried layers come back, and the base stays the motion they apply to.
        loaded = WithMotionBase(loaded);
        source = loaded;
        canReplace = AnimationResources.CanReplace(loaded.Resource);
        catalogKey = TimelineWindowViews.CatalogKey(loaded.Capture.LoadedResourcePaths, loaded.Clip.GamePath, loaded.Resource.ModRoot);
        title = AnimationPresentation.AnimationName(loaded.Capture, loaded.Startup);
        subtitle = $"{AnimationPresentation.AnimationState(loaded.Capture, loaded.Startup)} · " +
                   (loaded.Resource.ModName ?? loaded.Resource.ModDirectory ?? "Game Data");
        labelDocument = null;
        pendingLengths.Clear();
        lookEditing = false;
        if (!sameClip)
        {
            ForgetEffects();
            modName = TimelineWindowViews.DefaultModName(AnimationPresentation.DefaultModName(loaded.Capture));
            destination = AnimationDestination.NewMod;
            saveError = null;
            saveMessage = null;
            lastSaveId = null;
            scrollY = 0;
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
            editor.VisibleLength = VisibleFrames;
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

    /// <summary>
    /// The source with the clip's latest skeleton match: choosing a skeleton in the Source tab after
    /// opening the timeline must still allow a length change.
    /// </summary>
    private AnimationTimelineSource Fresh(AnimationTimelineSource current)
    {
        if (animations == null)
            return current;
        foreach (var capture in animations.Observer.History)
        {
            if (capture.Id != current.Capture.Id)
                continue;
            var clip = current.Startup ? capture.Startup : capture.Clip;
            if (clip != null && clip.GamePath == current.Clip.GamePath && clip.BindingFingerprint == current.Clip.BindingFingerprint &&
                !ReferenceEquals(clip.Resolution, current.Clip.Resolution))
                return current with { Clip = current.Clip with { Resolution = clip.Resolution } };
            break;
        }
        return current;
    }

    public override void Draw()
    {
        RespectCloseHotkey = true;
        if (animations == null)
        {
            Widgets.Banner("##timeline-unavailable", FeedbackSeverity.Warning, animationError ?? "Animation editing is unavailable.");
            return;
        }
        PollLoad();
        PollSave();
        PollImport();
        PollPreview();
        PollLengths();
        if (source != null && ImGui.GetFrameCount() % 30 == 0)
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
            return;
        }
        if (loadError != null)
            Widgets.Banner("##timeline-reload-error", FeedbackSeverity.Error, "Reading the timeline again failed: " + loadError);
        if (catalogKey != null)
            catalog.Request(catalogKey);
        editor.MotionFrames = MotionFrames(source);
        editor.BoneOrder = source.Clip.Resolution?.Selected?.Skeleton is { } skeleton ? BoneTree(skeleton).Index : null;

        SyncPlayback(editor, source);
        UpdateLive(editor, source);
        CheckGizmo();
        DrawToolbar(editor);
        DrawPreviewBar(editor, source);
        DrawMessages(editor);

        var avail = ImGui.GetContentRegionAvail();
        if (footerHeight <= 0)
            footerHeight = ImGui.GetFrameHeightWithSpacing() * 3 + ImGui.GetStyle().ItemSpacing.Y * 2;
        var height = Math.Max(Theme.Scaled(120), avail.Y - footerHeight - ImGui.GetStyle().ItemSpacing.Y);
        DrawBody(editor, avail.X, height);

        var footerStart = ImGui.GetCursorPosY();
        DrawSaveFooter(editor, source);
        footerHeight = Math.Max(1, ImGui.GetCursorPosY() - footerStart);

        DrawPicker(editor);
        HandleKeys(editor);
        // Escape cancels a drag; it must not also close the window.
        RespectCloseHotkey = editor.Gesture == TimelineGesture.None;
    }

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
            return;
        }
        Widgets.EmptyState(FontAwesomeIcon.Film, "No timeline open",
            "Select an animation in the Animations tab, then press Open timeline in its Timeline tab.");
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

    /// <summary> The lanes on the left and the selected event's properties on the right, split like the Mod Browser. </summary>
    private void DrawBody(TimelineEditor current, float width, float height)
    {
        var splitter = Theme.Scaled(6);
        if (propertiesWidth <= 0)
            propertiesWidth = Theme.Scaled(300);
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
        if (panel.Success)
            DrawProperties(current);
    }

    private static string ErrorText(Exception? error)
    {
        var inner = error is AggregateException aggregate ? aggregate.InnerException ?? aggregate : error;
        return inner?.Message is { Length: > 0 } message ? message : "Something went wrong.";
    }
}
