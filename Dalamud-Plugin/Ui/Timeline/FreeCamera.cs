using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline's free camera: flies the GPose camera away from the character. The game places its camera around the GPose target
/// every frame, so a hook on the camera's update (enabled only while the free camera is on) moves it to the free camera's spot right
/// after. The game's own turn of its camera that frame (mouse drag, keys, with the game's own settings) turns the free camera, and the
/// game's angles go back each frame so they never reach its limits. The moving keys are read on the framework update, before the
/// game reads them, and taken from the game while it has the keyboard; the wheel over the game sets the speed. Turning it off, or
/// leaving GPose, gives the game its camera back as it was.
/// </summary>
internal sealed unsafe partial class FreeCamera : IDisposable
{
    public const string NotInGPose = "Enter GPose to fly the camera.";

    private delegate void UpdateDelegate(CameraBase* camera);

    private static readonly VirtualKey[] MoveKeys =
        [VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.SPACE, VirtualKey.CONTROL, VirtualKey.SHIFT];

    private readonly IGameInteropProvider interop;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IKeyState keys;
    private readonly IPluginLog log;
    private Hook<UpdateDelegate>? hook;
    private FreeCameraPose pose;
    // The camera flown, and its angles and distance when the free camera came on: the angles go back every frame, all three at the end.
    private nint camera;
    private float distance;
    private float dirH;
    private float dirV;
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

    /// <summary>Whether the free camera flies the GPose camera now.</summary>
    public bool On { get; private set; }

    /// <summary>How fast it flies, in metres a second; Shift goes faster.</summary>
    public float Speed { get; private set; } = FreeCameraMotion.DefaultSpeed;

    /// <summary>Why it can't come on now (<see cref="NotInGPose"/>); null when it can.</summary>
    public string? Blocked => clientState.IsGPosing ? null : NotInGPose;

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
        try
        {
            hook ??= interop.HookFromAddress<UpdateDelegate>(update, Detour);
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not hook the camera's update; the free camera stays off.");
            return;
        }
        if (hook.Address != update)
        {
            log.Warning("The GPose camera's update isn't the one hooked; the free camera stays off.");
            return;
        }
        camera = (nint)active;
        distance = active->Distance;
        dirH = active->DirH;
        dirV = active->DirV;
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
        // Zoomed with the wheel behind the free camera: the game's camera goes back to where it was.
        var active = Active();
        if (clientState.IsGPosing && active != null && (nint)active == camera)
        {
            active->Distance = distance;
            active->DirH = dirH;
            active->DirV = dirV;
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
        hook?.Dispose();
        hook = null;
    }

    private static Camera* Active()
    {
        var manager = CameraManager.Instance();
        return manager == null ? null : manager->GetActiveCamera();
    }

    // After the game's own update of every camera of its kind: only the flown one, while it is the active one, moves.
    private void Detour(CameraBase* updated)
    {
        hook!.Original(updated);
        if (!On || (nint)updated != camera || (nint)Active() != camera)
            return;
        var flown = (Camera*)updated;
        pose = FreeCameraMotion.Turn(pose, flown->DirH - dirH, flown->DirV - dirV);
        flown->DirH = dirH;
        flown->DirV = dirV;
        flown->CameraBase.SceneCamera.Position = pose.Position;
        flown->CameraBase.SceneCamera.LookAtVector = FreeCameraMotion.LookAt(pose);
    }

    // Runs before the game's own update, which reads the keys and then updates the camera.
    private void Tick(IFramework update)
    {
        if (!On)
            return;
        if (!clientState.IsGPosing || (nint)Active() != camera)
        {
            Off();
            return;
        }
        var io = ImGui.GetIO();
        if (!io.WantCaptureMouse)
            Speed = FreeCameraMotion.Speed(Speed, io.MouseWheel);
        var seconds = (float)Math.Min(update.UpdateDelta.TotalSeconds, 0.1);
        pose = FreeCameraMotion.Step(pose, Input(io), Speed, seconds);
    }

    /// <summary>
    /// The moving keys held now, taken from the game so it doesn't act on them too; none while a window or a text field has the
    /// keyboard, or another program does.
    /// </summary>
    private FreeCameraInput Input(ImGuiIOPtr io)
    {
        if (io.WantCaptureKeyboard || io.WantTextInput || !GameInFront() || ShortcutKeys.GameTyping())
            return default;
        foreach (var key in MoveKeys)
            if (keys.IsVirtualKeyValid(key) && keys[key])
                keys[key] = false;
        return new FreeCameraInput(Axis(VirtualKey.W, VirtualKey.S), Axis(VirtualKey.D, VirtualKey.A), Axis(VirtualKey.SPACE, VirtualKey.CONTROL),
            ShortcutKeys.Down(VirtualKey.SHIFT));
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
