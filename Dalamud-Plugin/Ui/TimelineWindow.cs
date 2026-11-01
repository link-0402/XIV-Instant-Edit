using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Timeline window: it hosts a <see cref="TimelineWorkspace"/> per actor, each with its own clip, edits, preview and
/// Live, and shows one of them. It keeps what the workspaces share: the Bones window, the bones on the character, the
/// pose browser, the shortcut keys and the pose thumbnails. Every workspace is ticked on the framework update while the
/// window is open, so a hidden or collapsed one keeps running.
/// </summary>
public sealed class TimelineWindow : Window, IDisposable, ITimelineHost
{
    private readonly WorkspaceServiceFactory services;
    private readonly WorkspaceServices selfServices;
    private readonly IFramework framework;
    private readonly Dictionary<WorkspaceKey, TimelineWorkspace> workspaces = [];
    private readonly BonesWindow bonesWindow;
    private readonly BonesOverlay overlayWindow;
    private readonly PoseBrowserWindow poseBrowserWindow;
    private readonly PoseThumbnails poseThumbnails;
    private readonly ShortcutKeys keys;
    private readonly string? animationError;
    private Func<bool>? mainListens;
    // The dialogs the start page's rows open, such as Send animation to Blender; drawn every frame.
    private Action? drawDialogs;
    // What the plugin attached, replayed into every workspace this window makes.
    private (Action? Settings, Action? Recorder)? tools;
    private Main.AnimationDelivery? blender;
    private (Action Draw, Func<int> Playing)? playingNow;
    private Action? partnersPage;
    private (Configuration Config, Action Save)? poseFolders;
    private Func<ushort?, CancellationToken, Task<HandTipFacts>>? handTips;
    private bool disposed;

    internal TimelineWindow(WorkspaceServiceFactory services, string? animationError, IFramework framework, ITextureProvider textures, IKeyState keyState)
        : base("Timeline (Beta)###XIVInstantEditTimeline")
    {
        this.services = services;
        this.framework = framework;
        this.animationError = animationError;
        selfServices = services.For(WorkspaceKey.Self);
        bonesWindow = new BonesWindow(this);
        overlayWindow = new BonesOverlay(this);
        poseBrowserWindow = new PoseBrowserWindow(this);
        poseThumbnails = new PoseThumbnails(textures);
        keys = new ShortcutKeys(keyState, framework);
        Shown = Workspace(WorkspaceKey.Self);

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
        framework.Update += OnUpdate;
    }

    /// <summary>The workspace the window shows.</summary>
    internal TimelineWorkspace Shown { get; private set; }

    private AnimationEditService? Animations => selfServices.Animations;

    BonesWindow ITimelineHost.Bones => bonesWindow;
    PoseBrowserWindow ITimelineHost.Poses => poseBrowserWindow;
    ShortcutKeys ITimelineHost.Keys => keys;
    PoseThumbnails ITimelineHost.Thumbnails => poseThumbnails;
    string? ITimelineHost.AnimationError => animationError;
    void ITimelineHost.Close() => IsOpen = false;

    /// <summary>The workspace of <paramref name="key"/>, made with what the plugin attached the first time it is asked for.</summary>
    private TimelineWorkspace Workspace(WorkspaceKey key)
    {
        if (workspaces.TryGetValue(key, out var found))
            return found;
        var made = new TimelineWorkspace(key, services.For(key), this);
        if (tools is { } attachedTools)
            made.AttachTools(attachedTools.Settings, attachedTools.Recorder);
        if (blender != null)
            made.AttachBlender(blender);
        if (playingNow is { } playing)
            made.AttachPlayingNow(playing.Draw, playing.Playing);
        if (partnersPage != null)
            made.AttachPartners(partnersPage);
        if (poseFolders is { } folders)
            made.AttachPoseFolders(folders.Config, folders.Save);
        made.HandTips = handTips;
        workspaces[key] = made;
        return made;
    }

    // Every workspace keeps running while the window is open, collapsed or not, shown or not.
    private void OnUpdate(IFramework _)
    {
        if (!IsOpen || Animations is not { } animations)
            return;
        // The listener runs while this window is open: the loader and the clip's skeleton choice read it.
        animations.StartObservation();
        foreach (var workspace in workspaces.Values)
            workspace.Tick();
    }

    public void DrawFileDialog()
    {
        foreach (var workspace in workspaces.Values)
            workspace.DrawFileDialog();
    }

    /// <summary>
    /// What the Timeline window borrows from the main window: Settings, where the skeleton library is
    /// rebuilt, and the live pose recorder, which sends to Blender through it.
    /// </summary>
    internal void AttachTools(Action? settings, Action? recorderPanel)
    {
        tools = (settings, recorderPanel);
        foreach (var workspace in workspaces.Values)
            workspace.AttachTools(settings, recorderPanel);
    }

    /// <summary>The main window's connection to Blender, which Send motion to Blender and Motion from Blender go through.</summary>
    internal void AttachBlender(Main.AnimationDelivery delivery)
    {
        blender = delivery;
        foreach (var workspace in workspaces.Values)
            workspace.AttachBlender(delivery);
    }

    /// <summary>
    /// The main window's Playing now page for the start page, how many animations play now for its sidebar, and the
    /// dialogs its rows open, which this window draws every frame.
    /// </summary>
    internal void AttachPlayingNow(Action draw, Func<int> playing, Action dialogs)
    {
        playingNow = (draw, playing);
        drawDialogs = dialogs;
        foreach (var workspace in workspaces.Values)
            workspace.AttachPlayingNow(draw, playing);
    }

    /// <summary>The start page's Partners page; its sidebar entry shows once this is attached.</summary>
    internal void AttachPartners(Action draw)
    {
        partnersPage = draw;
        foreach (var workspace in workspaces.Values)
            workspace.AttachPartners(draw);
    }

    /// <summary>How many recorded edits did not finish and need an undo; none while an edit is still running.</summary>
    internal static int RecoveryAttention(AnimationEditService service) => RecentEditsPage.RecoveryAttention(service);

    /// <summary>The header's Start button: "Start", or "Start (2)" with 2 <paramref name="unfinished"/> edits.</summary>
    internal static string StartLabel(int unfinished) => unfinished > 0 ? $"Start ({unfinished})" : "Start";

    /// <summary>
    /// Whether Back can run: not while a read runs, nor while the face that is open (<paramref name="open"/>, null when its
    /// read failed) has unsaved edits (<paramref name="dirty"/>).
    /// </summary>
    internal static bool CanGoBack(AnimationTimelineSource? open, bool dirty, bool busy) => !busy && !(dirty && open?.Document != null);

    /// <summary>Shows the start page of the shown workspace, on <paramref name="entry"/> or on the last entry shown.</summary>
    internal void ShowStart(StartEntry? entry) => Shown.ShowStart(entry);

    internal BonesWindow Bones => bonesWindow;

    internal BonesOverlay Overlay => overlayWindow;

    internal PoseBrowserWindow Poses => poseBrowserWindow;

    /// <summary>The shown clip's title, for the Bones window's title; null with no editable clip open.</summary>
    internal string? ClipTitle => Shown.ClipTitle;

    /// <summary>The Bones window's whole content; called from its draw.</summary>
    internal void DrawBones() => Shown.DrawBones();

    /// <summary>The pose browser's whole content; called from its draw.</summary>
    internal void DrawPoseBrowser() => Shown.DrawPoseBrowser();

    internal void PoseBrowserClosed() => Shown.PoseBrowserClosed();

    /// <summary>Whether the bones show on the shown workspace's character now.</summary>
    internal bool OverlayWanted => Shown.OverlayWanted;

    /// <summary>A gizmo drag is running: the overlay takes the mouse.</summary>
    internal bool OverlayDragging => Shown.OverlayDragging;

    /// <summary>The overlay's whole content; called from its draw.</summary>
    internal void DrawBonesOverlay() => Shown.DrawBonesOverlay();

    /// <summary>Ends a gizmo drag the overlay no longer draws; called every frame by the overlay.</summary>
    internal void CheckGizmo() => Shown.CheckGizmo();

    /// <summary>The settings the pose browser keeps its folders in.</summary>
    internal void AttachPoseFolders(Configuration config, Action save)
    {
        poseFolders = (config, save);
        foreach (var workspace in workspaces.Values)
            workspace.AttachPoseFolders(config, save);
    }

    /// <summary>Whether the main window keeps the listener running too; the Timeline window leaves it on then.</summary>
    internal void AttachListening(Func<bool> mainWindowListens) => mainListens = mainWindowListens;

    /// <summary>Reads whether the live character has fingertip bones; the plugin sets it once the physics builder exists.</summary>
    internal Func<ushort?, CancellationToken, Task<HandTipFacts>>? HandTips
    {
        get => handTips;
        set
        {
            handTips = value;
            foreach (var workspace in workspaces.Values)
                workspace.HandTips = value;
        }
    }

    /// <summary>The Timeline window listens while it is open: the loader and the clip's skeleton choice need it.</summary>
    internal bool Listens => IsOpen;

    /// <summary>Opens the window as it is: its clip, or with none the loader.</summary>
    internal void Show()
    {
        IsOpen = true;
        BringToFront();
    }

    void ITimelineHost.Show() => Show();

    /// <summary>Shows the timeline of <paramref name="capture"/>'s clip in the shown workspace.</summary>
    internal void Open(AnimationCapture capture, bool startup) => Shown.Open(capture, startup);

    /// <summary>Opens the clip <paramref name="journal"/>'s edit saved in the shown workspace (Edit again).</summary>
    internal void EditAgain(AnimationEditJournal journal) => Shown.EditAgain(journal);

    public override void OnClose()
    {
        // Closing with unsaved changes asks first; the window stays open until Discard.
        if (!Shown.Close())
        {
            IsOpen = true;
            return;
        }
        // The listener stays on while the Send character card still waits on it.
        if (mainListens?.Invoke() != true)
            Animations?.StopObservation();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
        foreach (var workspace in workspaces.Values)
            workspace.Dispose();
        keys.Dispose();
        poseThumbnails.Dispose();
    }

    /// <summary> Before the window begins: the shown workspace sets the menu bar. </summary>
    public override void PreDraw()
    {
        var flags = Flags;
        Shown.PreDraw(ref flags);
        Flags = flags;
    }

    public override void Draw()
    {
        RespectCloseHotkey = true;
        keys.Refresh();
        // At the window's own level, so a dialog opened from a page inside a child keeps its popup.
        if (Animations != null)
            drawDialogs?.Invoke();
        using (ImRaii.PushId(Shown.Key.Id))
            Shown.Draw();
        // Escape cancels a drag; it must not also close the window.
        RespectCloseHotkey = !Shown.HoldsEscape;
    }
}
