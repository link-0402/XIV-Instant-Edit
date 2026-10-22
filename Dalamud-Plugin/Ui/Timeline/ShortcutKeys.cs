using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline's shortcut keys. Real key presses don't always reach ImGui: in GPose the game's key
/// state had C and Q down while ImGui never saw them, and Q lowered the camera instead. So a key also
/// counts when the game's key state has it, and a key taken from there is cleared so the game doesn't
/// act on it as well. Clearing it while drawing is too late, though: the game has already acted on it
/// that frame (each arrow repeat still turned the GPose camera). So while a Timeline window has the
/// keyboard (<see cref="Claim"/>), the shortcut keys are taken from the game's state at the start of the
/// next game frame, before the camera reads them, and kept for the draw.
/// </summary>
internal sealed partial class ShortcutKeys : IDisposable
{
    private readonly IKeyState keys;
    private readonly IFramework framework;
    // Keys a shortcut has asked about: the ones taken from the game while a window has the keyboard.
    private readonly HashSet<VirtualKey> watched = [];
    // Keys taken from the game's state at the start of this game frame, for this frame's draw.
    private readonly HashSet<VirtualKey> taken = [];
    // Keys taken from the game's state and still held down: their key repeats aren't new presses.
    private readonly HashSet<VirtualKey> held = [];
    private int frame = -1;
    private bool claimed;

    public ShortcutKeys(IKeyState keys, IFramework framework)
    {
        this.keys = keys;
        this.framework = framework;
        framework.Update += Take;
    }

    public bool Ctrl => ImGui.GetIO().KeyCtrl || Down(VirtualKey.CONTROL);
    public bool Shift => ImGui.GetIO().KeyShift || Down(VirtualKey.SHIFT);
    public bool Alt => ImGui.GetIO().KeyAlt || Down(VirtualKey.MENU);

    public void Dispose() => framework.Update -= Take;

    /// <summary>
    /// A window has the keyboard this frame; the next game frame takes the shortcut keys from the game.
    /// Key handlers call it each frame, where they tell ImGui they want the keyboard.
    /// </summary>
    public void Claim() => claimed = true;

    /// <summary> Forgets released keys, once a frame; the Timeline calls it every frame it draws. </summary>
    public void Refresh()
    {
        var now = ImGui.GetFrameCount();
        if (now == frame)
            return;
        frame = now;
        held.RemoveWhere(key => !Down(key));
    }

    /// <summary> Whether the key went down this frame (with <paramref name="repeat"/>, also its key repeats). </summary>
    public bool Pressed(ImGuiKey key, VirtualKey virtualKey, bool repeat = false)
    {
        Refresh();
        watched.Add(virtualKey);
        if (ImGui.IsKeyPressed(key, repeat))
            return true;
        if (!taken.Remove(virtualKey))
        {
            if (!keys.IsVirtualKeyValid(virtualKey) || !keys[virtualKey])
                return false;
            keys[virtualKey] = false;
        }
        // The game sets the key again on each key repeat, so clearing it never hides a new press.
        return held.Add(virtualKey) || repeat;
    }

    // Runs before the game's own update, which is where the GPose camera reads the keys.
    private void Take(IFramework _)
    {
        taken.Clear();
        if (!claimed)
            return;
        claimed = false;
        foreach (var key in watched)
        {
            if (!keys.IsVirtualKeyValid(key) || !keys[key])
                continue;
            keys[key] = false;
            taken.Add(key);
        }
    }

    private static bool Down(VirtualKey key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
