using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The free camera: flies the game's camera away from the character, in GPose or out of it (/ie cam). The game places its camera
/// around its target every frame, so a hook on the camera's update (enabled only while the free camera is on) moves it to the free
/// camera's spot right after. The game turns its own camera as ever (mouse drag, keys, with the game's own settings), its pitch limits
/// widened to the free camera's, and each frame's turn turns the free camera. Its zoom goes back each frame and its own view before its
/// next update, so the game never works from the free camera's view and never zooms into first person. The moving keys are read on the
/// framework update, before the game reads them, and taken from the game while it has the keyboard; while Ctrl or Alt is held it
/// doesn't fly and takes none, so their shortcuts reach the game. The wheel over the game sets the speed, read from the game's input.
/// Turning it off, Escape outside GPose, or entering or leaving GPose gives the game its camera back as it was.
/// </summary>
internal sealed unsafe partial class FreeCamera : IDisposable
{
    public const string NotLoggedIn = "Log in to fly the camera.";
    public const string InFirstPerson = "Leave first person to fly the camera.";

    // Room the game's zoom keeps above its nearest while flying: more than one wheel notch, so the wheel never reaches first person.
    private const float ZoomRoom = 3f;

    private delegate void UpdateDelegate(CameraBase* camera);

    // The keys taken from the game while flying. Shift stays the game's too, so its own and macro shortcuts with it still work.
    private static readonly VirtualKey[] MoveKeys =
        [VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.SPACE, VirtualKey.Q, VirtualKey.E];

    private readonly IGameInteropProvider interop;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    // One hook per camera update function met; the flown camera's is the one enabled.
    private readonly Dictionary<nint, Hook<UpdateDelegate>> hooks = [];
    private Hook<UpdateDelegate>? hook;
    private FreeCameraPose pose;
    // The camera flown, whether in GPose, and its zoom mode, angles, pitch limits and zoom when the free camera came on: the zoom goes
    // back every frame, the limits at the end, and the angles and zoom at the end unless the game switched the mode meanwhile.
    private nint camera;
    private bool gposing;
    private CameraZoomMode zoomMode;
    private bool modeSwitched;
    private float distance;
    private float interpDistance;
    private float dirH;
    private float dirV;
    private float dirVMin;
    private float dirVMax;
    // The game's angles after its last update: the next update's change is that frame's turn.
    private float lastDirH;
    private float lastDirV;
    // The zoom the game's camera keeps while flying.
    private float held;
    // The game's own view from its last update, put back before its next one.
    private Vector3 gamePosition;
    private Vector3 gameLookAt;
    private Vector3 gameUp;
    private bool gameViewSaved;
    // Escape stopped the free camera and is still held.
    private bool escapeHeld;
    private bool disposed;

    public FreeCamera(IGameInteropProvider interop, IFramework framework, IClientState clientState, IKeyState keys, IPluginLog log)
    {
        this.interop = interop;
        this.framework = framework;
        this.clientState = clientState;
        this.keys = keys;
        this.log = log;
        framework.Update += Tick;
    }

    /// <summary>Whether the free camera flies the game's camera now.</summary>
    public bool On { get; private set; }

    /// <summary>How fast it flies, in metres a second; Shift goes faster.</summary>
    public float Speed { get; private set; } = FreeCameraMotion.DefaultSpeed;

    /// <summary>Why it can't come on now; null when it can.</summary>
    public string? Blocked
    {
        get
        {
            var active = Active();
            if (!clientState.IsLoggedIn || active == null)
                return NotLoggedIn;
            // Flying from first person would show the character without a head.
            return !clientState.IsGPosing && active->ZoomMode == CameraZoomMode.FirstPerson ? InFirstPerson : null;
        }
    }

    /// <summary>Turns the free camera on at the game camera's spot, or off. Game thread only.</summary>
    public void Toggle()
    {
        if (On)
            Off();
        else
            TurnOn();
    }

    private void TurnOn()
    {
        var active = Active();
        if (disposed || Blocked != null || active == null)
            return;
        var update = (nint)active->CameraBase.VirtualTable->Update;
        if (!hooks.TryGetValue(update, out var found))
        {
            try
            {
                Hook<UpdateDelegate>? created = null;
                created = interop.HookFromAddress<UpdateDelegate>(update, updated => Detour(created!, updated));
                hooks[update] = found = created;
            }
            catch (Exception e)
            {
                log.Warning(e, "Could not hook the camera's update; the free camera stays off.");
                return;
            }
        }
        hook = found;
        camera = (nint)active;
        gposing = clientState.IsGPosing;
        zoomMode = active->ZoomMode;
        modeSwitched = false;
        distance = active->Distance;
        interpDistance = active->InterpDistance;
        dirH = lastDirH = active->DirH;
        dirV = lastDirV = active->DirV;
        dirVMin = active->DirVMin;
        dirVMax = active->DirVMax;
        held = Math.Min(Math.Max(distance, active->MinDistance + ZoomRoom), Math.Max(active->MaxDistance, active->MinDistance));
        gameViewSaved = false;
        pose = FreeCameraMotion.From(active->CameraBase.SceneCamera.Position, active->CameraBase.SceneCamera.LookAtVector);
        On = true;
        hook.Enable();
    }

    /// <summary>Gives the game its camera back where it was; nothing when off.</summary>
    public void Off()
    {
        if (!On)
            return;
        On = false;
        hook?.Disable();
        hook = null;
        // The game's own pitch limits come back on the camera flown, while the game still has it.
        var manager = CameraManager.Instance();
        if (manager != null && ((nint)manager->GetActiveCamera() == camera || (nint)manager->Camera == camera))
        {
            ((Camera*)camera)->DirVMin = dirVMin;
            ((Camera*)camera)->DirVMax = dirVMax;
        }
        // Zoomed or turned behind the free camera: the game's camera goes back to where it was, in the same mode and camera.
        var active = Active();
        if (!modeSwitched && clientState.IsGPosing == gposing && active != null && (nint)active == camera)
        {
            active->Distance = distance;
            active->InterpDistance = interpDistance;
            active->DirH = dirH;
            active->DirV = dirV;
            if (gameViewSaved)
                SetView(active, gamePosition, gameLookAt, gameUp);
        }
        camera = 0;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= Tick;
        Off();
        foreach (var created in hooks.Values)
            created.Dispose();
        hooks.Clear();
    }

    private static Camera* Active()
    {
        var manager = CameraManager.Instance();
        return manager == null ? null : manager->GetActiveCamera();
    }

    private static void SetView(Camera* flown, Vector3 position, Vector3 lookAt, Vector3 up)
    {
        flown->CameraBase.SceneCamera.Position = position;
        flown->CameraBase.SceneCamera.LookAtVector = lookAt;
        flown->CameraBase.SceneCamera.Vector_1 = up;
    }

    // Wraps the game's own update of every camera of its kind: only the flown one, while it is the active one, moves.
    private void Detour(Hook<UpdateDelegate> self, CameraBase* updated)
    {
        if (!On || self != hook || (nint)updated != camera || (nint)Active() != camera)
        {
            self.Original(updated);
            return;
        }
        var flown = (Camera*)updated;
        // The game's update starts from its own view and zoom, not the free camera's.
        if (gameViewSaved)
            SetView(flown, gamePosition, gameLookAt, gameUp);
        flown->Distance = held;
        flown->InterpDistance = held;
        // The game turns as far as the free camera looks, so its turn and the free camera's never part at a limit.
        flown->DirVMin = -FreeCameraMotion.MaxPitch;
        flown->DirVMax = FreeCameraMotion.MaxPitch;
        self.Original(updated);
        ref var scene = ref flown->CameraBase.SceneCamera;
        gamePosition = scene.Position;
        gameLookAt = scene.LookAtVector;
        gameUp = scene.Vector_1;
        gameViewSaved = true;
        if (flown->ZoomMode != zoomMode)
        {
            // The game switched between first and third person, which flips its angles: not a turn.
            zoomMode = flown->ZoomMode;
            modeSwitched = true;
        }
        else
            pose = FreeCameraMotion.Turn(pose, flown->DirH - lastDirH, flown->DirV - lastDirV);
        lastDirH = flown->DirH;
        lastDirV = flown->DirV;
        flown->Distance = held;
        flown->InterpDistance = held;
        // Up from the free camera's own view too: the game's leans with its own pitch and would roll the view.
        SetView(flown, pose.Position, FreeCameraMotion.LookAt(pose), FreeCameraMotion.Up(pose));
    }

    // Runs before the game's own update, which reads the keys and then updates the camera.
    private void Tick(IFramework update)
    {
        // The Escape that stopped it stays away from the game until it is let go, or the game would open its menu on it.
        if (escapeHeld)
        {
            escapeHeld = ShortcutKeys.Down(VirtualKey.ESCAPE);
            if (escapeHeld)
                Take(VirtualKey.ESCAPE);
        }
        if (!On)
            return;
        if (clientState.IsGPosing != gposing || (nint)Active() != camera)
        {
            Off();
            return;
        }
        var io = ImGui.GetIO();
        // Escape stops it outside GPose, where it would otherwise open the game's menu; in GPose it leaves GPose as ever.
        if (!gposing && GameHasKeyboard(io) && keys[VirtualKey.ESCAPE])
        {
            Take(VirtualKey.ESCAPE);
            escapeHeld = true;
            Off();
            return;
        }
        // Dalamud gives ImGui the wheel only over its windows, so the wheel over the game is the game's own: the copy without the
        // wheel over the game's windows. One notch a frame at most, whatever its unit.
        var input = UIInputData.Instance();
        if (input != null && !io.WantCaptureMouse)
            Speed = FreeCameraMotion.Speed(Speed, Math.Sign(input->UIFilteredCursorInputs.MouseWheel));
        var seconds = (float)Math.Min(update.UpdateDelta.TotalSeconds, 0.1);
        pose = FreeCameraMotion.Step(pose, Input(io), Speed, seconds);
    }

    /// <summary>
    /// The moving keys held now, all but Shift taken from the game so it doesn't act on them too; none while a window or a text
    /// field has the keyboard, another program does, or Ctrl or Alt is held for a shortcut.
    /// </summary>
    private FreeCameraInput Input(ImGuiIOPtr io)
    {
        if (!GameHasKeyboard(io) || ShortcutKeys.Down(VirtualKey.CONTROL) || ShortcutKeys.Down(VirtualKey.MENU))
            return default;
        foreach (var key in MoveKeys)
            Take(key);
        var up = (ShortcutKeys.Down(VirtualKey.SPACE) || ShortcutKeys.Down(VirtualKey.E) ? 1 : 0) - (ShortcutKeys.Down(VirtualKey.Q) ? 1 : 0);
        return new FreeCameraInput(Axis(VirtualKey.W, VirtualKey.S), Axis(VirtualKey.D, VirtualKey.A), up, ShortcutKeys.Down(VirtualKey.SHIFT));
    }

    // The game's window has the keyboard: no window or text field of ours or the game's, and no other program.
    private static bool GameHasKeyboard(ImGuiIOPtr io) =>
        !io.WantCaptureKeyboard && !io.WantTextInput && GameInFront() && !ShortcutKeys.GameTyping();

    // Takes a key held down from the game, so it doesn't act on it this frame.
    private void Take(VirtualKey key)
    {
        if (keys.IsVirtualKeyValid(key) && keys[key])
            keys[key] = false;
    }

    private static float Axis(VirtualKey plus, VirtualKey minus) => (ShortcutKeys.Down(plus) ? 1 : 0) - (ShortcutKeys.Down(minus) ? 1 : 0);

    // The key state is the whole desktop's: only keys pressed into the game's window count.
    private static bool GameInFront()
    {
        var window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out var process) != 0 && process == (uint)Environment.ProcessId;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);
}
