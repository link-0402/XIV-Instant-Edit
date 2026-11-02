using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Actors;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
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
    private readonly FreeCamera freeCamera;
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
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly ITargetManager targets;
    // The spawned partners, whose presets get workspaces of their own; null until the plugin attaches them.
    private PartnerService? partners;
    // Tells the main window which partner shows (null for you): Playing now names it and the recorder records it.
    private Action<string?>? showingPartner;
    private string? shownPartner;
    // Each partner workspace's spawn its clip is tied to; none while the partner is despawned.
    private readonly Dictionary<WorkspaceKey, int> tied = [];
    // Presets deleted since the last framework update, whose workspaces go then.
    private readonly List<Guid> deleted = [];
    // Link backs asked for since the last framework update, added to their workspaces then.
    private readonly List<(WorkspaceKey Partner, MotionLink Link, string Name, string Path, int Binding)> linkBacks = [];
    // The partner tracks of every workspace's link layers, built after the workspaces' own update.
    private readonly PartnerLinks links;
    // The window's close waits for the question about unsaved changes; Discard confirms it.
    private bool closeAsked;
    private bool closeConfirmed;
    private bool disposed;

    internal TimelineWindow(WorkspaceServiceFactory services, string? animationError, IFramework framework, ITextureProvider textures, IKeyState keyState,
        IObjectTable objects, IClientState clientState, ITargetManager targets, IGameInteropProvider interop)
        : base("Timeline (Beta)###XIVInstantEditTimeline")
    {
        this.services = services;
        this.framework = framework;
        this.animationError = animationError;
        this.objects = objects;
        this.clientState = clientState;
        this.targets = targets;
        selfServices = services.For(WorkspaceKey.Self);
        links = new PartnerLinks((error, message) => selfServices.Log.Warning(error, message));
        bonesWindow = new BonesWindow(this);
        overlayWindow = new BonesOverlay(this);
        poseBrowserWindow = new PoseBrowserWindow(this);
        poseThumbnails = new PoseThumbnails(textures);
        keys = new ShortcutKeys(keyState, framework);
        freeCamera = new FreeCamera(interop, framework, clientState, keyState, selfServices.Log);
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
    FreeCamera ITimelineHost.FreeCamera => freeCamera;
    PoseThumbnails ITimelineHost.Thumbnails => poseThumbnails;
    string? ITimelineHost.AnimationError => animationError;

    /// <summary>The actor as the window names it: "You", or the partner's name.</summary>
    private string NameOf(WorkspaceKey key) => services.For(key).ActorName();

    /// <summary>The workspace the GPose target shows: a spawned partner's own, else yours.</summary>
    private WorkspaceKey ShownKey()
    {
        if (partners == null)
            return WorkspaceKey.Self;
        var target = targets.GPoseTarget is ICharacter { Address: not 0 } character ? character.ObjectIndex : (ushort?)null;
        return WorkspaceChoice.Shown(clientState.IsGPosing, target, partners.PresetAt);
    }

    /// <summary>
    /// Ties each partner workspace to its actor: a despawn ends its Preview and Live, a spawn again points its clip at the new
    /// actor. Its clip and unsaved edits stay either way.
    /// </summary>
    private void TieActors()
    {
        if (partners == null)
            return;
        foreach (var (key, workspace) in workspaces)
        {
            if (key.Preset is not { } preset)
                continue;
            var view = partners.Find(preset);
            var spawn = view is { Stage: PartnerStage.Ready } ? view.Spawn : (int?)null;
            if (tied.TryGetValue(key, out var was) && spawn != was)
            {
                workspace.ActorGone();
                tied.Remove(key);
            }
            if (spawn is { } now && !tied.ContainsKey(key))
            {
                if (AnimationRuntime.Identity(objects[view!.ObjectIndex]) is { } actor)
                    workspace.ActorBack(actor.Index, actor.ActorId, actor.Address);
                tied[key] = now;
            }
        }
    }

    bool ITimelineHost.DrawActorSwitch()
    {
        if (partners == null || !clientState.IsGPosing)
            return false;
        var tracked = partners.Store.Presets.Select(preset => partners.Find(preset.Id)).OfType<PartnerView>().ToArray();
        if (tracked.Length == 0 && Shown.Key.IsSelf)
            return false;
        var name = NameOf(Shown.Key);
        ImGui.AlignTextToFramePadding();
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Label))
            open = ImGui.Selectable(name + "##timeline-actor", false, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(name).X, 0));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Whose clip this is. Click to edit another actor's; it becomes the GPose target.");
        if (open)
            ImGui.OpenPopup("##timeline-actors");
        using var popup = ImRaii.Popup("##timeline-actors");
        if (!popup.Success)
            return true;
        if (ImGui.Selectable("You##timeline-actor-you", Shown.Key.IsSelf))
            Target(AnimationPlayback.GPosePlayerIndex);
        foreach (var view in tracked)
        {
            var ready = view.Stage == PartnerStage.Ready;
            using (ImRaii.Disabled(!ready))
            {
                if (ImGui.Selectable($"{view.Name}##timeline-actor-{view.PresetId:N}", Shown.Key.Preset == view.PresetId))
                    Target(view.ObjectIndex);
            }
            if (!ready && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(TimelineWindowViews.Spawning(view.Name));
        }
        return true;
    }

    /// <summary>Makes the GPose character at <paramref name="index"/> the GPose target, which shows its workspace.</summary>
    private void Target(int index)
    {
        if (objects[index] is { Address: not 0 } actor)
            targets.GPoseTarget = actor;
    }

    string? ITimelineHost.Waiting(WorkspaceKey key) =>
        key.Preset is { } preset && partners?.Find(preset) is { Stage: not PartnerStage.Ready } view ? TimelineWindowViews.Spawning(view.Name) : null;

    /// <summary>
    /// The spawned partners, whose presets get workspaces of their own, and how the main window learns which partner shows
    /// (null for you).
    /// </summary>
    internal void AttachPartnerService(PartnerService service, Action<string?> showing)
    {
        partners = service;
        showingPartner = showing;
    }

    /// <summary>Whether the preset's workspace has unsaved edits, for the Partners page's Delete question.</summary>
    internal bool HasUnsaved(Guid preset) => workspaces.TryGetValue(new WorkspaceKey(preset), out var workspace) && workspace.HasUnsavedChanges;

    /// <summary>
    /// The preset was deleted (from the Partners page, inside a workspace's draw): its workspace, its kept edits and its services
    /// go on the next framework update.
    /// </summary>
    internal void PresetDeleted(Guid preset) => deleted.Add(preset);

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
        made.Session.Group?.Add(made.Preview);
        return made;
    }

    // Every workspace keeps running while the window is open, collapsed or not, shown or not.
    private void OnUpdate(IFramework _)
    {
        DropDeleted();
        if (!IsOpen || Animations is not { } animations)
            return;
        // The listener runs while this window is open: the loader and the clip's skeleton choice read it.
        animations.StartObservation();
        TieActors();
        SyncShown();
        TellShown(Shown.Key.IsSelf ? null : NameOf(Shown.Key));
        foreach (var workspace in workspaces.Values)
            workspace.Tick();
        // The links read each clip as this frame's update left it.
        AddLinkBacks();
        links.Update(workspaces.Values, clientState.IsGPosing, PresetExists);
        // After every workspace's own update, so every clip it plays again starts on this framework frame.
        selfServices.Group.Update();
    }

    /// <summary>Whether the partner preset still exists.</summary>
    private bool PresetExists(Guid preset) => partners?.Store.Presets.Any(known => known.Id == preset) == true;

    IReadOnlyList<LinkView> ITimelineHost.LinkPartners(WorkspaceKey self)
    {
        links.Want(self);
        return [.. links.Views.Where(view => view.Key != self)];
    }

    LinkView? ITimelineHost.LinkView(WorkspaceKey key) => links.Views.FirstOrDefault(view => view.Key == key);

    IReadOnlyList<LinkCandidate> ITimelineHost.LinkCandidates(WorkspaceKey self) => [.. links.Candidates.Where(candidate => candidate.Key != self)];

    bool ITimelineHost.HasLinkPartner(WorkspaceKey self) => links.HasPartner(self);

    // From the presets, not the workspaces' services, which a preset without a workspace would build.
    string? ITimelineHost.ActorName(Guid? preset) =>
        preset is not { } id ? "You"
        : partners?.Store.Presets.FirstOrDefault(known => known.Id == id) is { } stored ? partners.Find(id)?.Name ?? stored.Name
        : null;

    IReadOnlyList<string> ITimelineHost.LinkedBy(WorkspaceKey self) =>
        links.Candidates.FirstOrDefault(candidate => candidate.Key == self) is { } mine
            ? TimelineWindowViews.LinkedBy(links.Candidates.Where(candidate => candidate.Key != self).Select(candidate => (candidate.Name, candidate.Document)),
                self.Preset, mine.GamePath, mine.Binding)
            : [];

    void ITimelineHost.AddLinkBack(WorkspaceKey partner, MotionLink link, string name, string expectedPath, int expectedBinding) =>
        linkBacks.Add((partner, link, name, expectedPath, expectedBinding));

    /// <summary>
    /// The link backs asked for since the last update, outside any draw: each through its workspace's own editor while that clip is
    /// still the one the link names; one waiting on an edit there tries again on the next update.
    /// </summary>
    private void AddLinkBacks()
    {
        if (linkBacks.Count == 0)
            return;
        var waiting = new List<(WorkspaceKey, MotionLink, string, string, int)>();
        foreach (var back in linkBacks)
        {
            if (!workspaces.TryGetValue(back.Partner, out var workspace) || workspace.Session.Source?.Clip is not { } clip ||
                !string.Equals(clip.GamePath, back.Path, StringComparison.OrdinalIgnoreCase) || clip.BindingIndex != back.Binding)
                continue;
            if (!workspace.AddLinkLayer(back.Link, back.Name))
                waiting.Add(back);
        }
        linkBacks.Clear();
        linkBacks.AddRange(waiting);
    }

    /// <summary>Tells the main window which partner shows, null for you: Playing now names it and the recorder records it.</summary>
    private void TellShown(string? partner)
    {
        if (partner == shownPartner)
            return;
        shownPartner = partner;
        showingPartner?.Invoke(partner);
    }

    /// <summary>
    /// The workspaces of presets deleted since the last update go, outside any draw: the actor is already despawned, so
    /// first as a despawn, then closed, leaving the shared Bones window and pose browser alone unless that workspace showed.
    /// </summary>
    private void DropDeleted()
    {
        if (deleted.Count == 0)
            return;
        foreach (var preset in deleted)
        {
            var key = new WorkspaceKey(preset);
            if (workspaces.Remove(key, out var workspace))
            {
                var shown = ReferenceEquals(Shown, workspace);
                if (shown)
                    Shown = Workspace(WorkspaceKey.Self);
                workspace.Session.Group?.Remove(workspace.Preview);
                workspace.ActorGone();
                workspace.Close(shown);
                workspace.Dispose();
            }
            tied.Remove(key);
            try { Animations?.Journals.ClearAutosave(key.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { selfServices.Log.Warning(e, "Could not remove a partner's kept timeline edits."); }
            services.Release(key);
        }
        deleted.Clear();
    }

    /// <summary>Shows the GPose target's workspace, made the first time that partner is the target.</summary>
    private TimelineWorkspace SyncShown()
    {
        var shown = Workspace(ShownKey());
        if (!ReferenceEquals(shown, Shown))
        {
            Shown.Hidden();
            Shown = shown;
        }
        return shown;
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
    internal void ShowStart(StartEntry? entry) => SyncShown().ShowStart(entry);

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

    /// <summary>Shows the timeline of <paramref name="capture"/>'s clip in the GPose target's workspace.</summary>
    internal void Open(AnimationCapture capture, bool startup) => SyncShown().Open(capture, startup);

    /// <summary>Opens the clip <paramref name="journal"/>'s edit saved in the GPose target's workspace (Edit again).</summary>
    internal void EditAgain(AnimationEditJournal journal) => SyncShown().EditAgain(journal);

    /// <summary>The actors whose workspaces have unsaved changes, you first: what the close question names.</summary>
    private List<string> UnsavedNames() =>
        workspaces.Where(pair => pair.Value.HasUnsavedChanges).OrderBy(pair => pair.Key.IsSelf ? 0 : 1).Select(pair => NameOf(pair.Key)).ToList();

    public override void OnClose()
    {
        // Closing with unsaved changes in any workspace asks first; the window stays open until Discard.
        if (!closeConfirmed && UnsavedNames().Count > 0)
        {
            closeAsked = true;
            IsOpen = true;
            return;
        }
        closeConfirmed = false;
        closeAsked = false;
        // The game gets its camera back.
        freeCamera.Off();
        // Every Preview and Live ends, and every workspace's clip closes.
        foreach (var workspace in workspaces.Values)
            workspace.Close();
        // So does every link, with its builds.
        linkBacks.Clear();
        links.Forget();
        // The main window's recorder and Playing now are yours again.
        TellShown(null);
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
        links.Dispose();
        foreach (var workspace in workspaces.Values)
            workspace.Dispose();
        keys.Dispose();
        freeCamera.Dispose();
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
        if (closeAsked)
            DrawCloseQuestion();
        using (ImRaii.PushId(Shown.Key.Id))
            Shown.Draw();
        // Escape cancels a drag; it must not also close the window.
        RespectCloseHotkey = !Shown.HoldsEscape;
    }

    /// <summary> The inline question before every workspace's unsaved changes are lost: Discard or Keep editing, never a modal. </summary>
    private void DrawCloseQuestion()
    {
        var names = UnsavedNames();
        if (names.Count == 0)
        {
            closeAsked = false;
            return;
        }
        Widgets.Banner("##timeline-unsaved-close", FeedbackSeverity.Warning, TimelineWindowViews.CloseQuestion(names));
        if (ImGui.Button("Discard##timeline-close"))
        {
            closeConfirmed = true;
            IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing##timeline-close"))
            closeAsked = false;
        ImGui.Spacing();
    }
}
