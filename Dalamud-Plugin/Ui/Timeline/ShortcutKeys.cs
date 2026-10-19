using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline's shortcut keys. Real key presses don't always reach ImGui: in GPose the game's key
/// state had C and Q down while ImGui never saw them, and Q lowered the camera instead. So a key also
/// counts when the game's key state has it, and a key taken from there is cleared so the game doesn't
/// act on it as well. Callers check focus, typing and modifiers first, so only keys they use are taken.
/// </summary>
internal sealed partial class ShortcutKeys(IKeyState keys)
{
    // Keys taken from the game's state and still held down: their key repeats aren't new presses.
    private readonly HashSet<VirtualKey> held = [];
    private int frame = -1;

    public bool Ctrl => ImGui.GetIO().KeyCtrl || Down(VirtualKey.CONTROL);
    public bool Shift => ImGui.GetIO().KeyShift || Down(VirtualKey.SHIFT);
    public bool Alt => ImGui.GetIO().KeyAlt || Down(VirtualKey.MENU);

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
        if (ImGui.IsKeyPressed(key, repeat))
            return true;
        if (!keys.IsVirtualKeyValid(virtualKey) || !keys[virtualKey])
            return false;
        // The game sets the key again on each key repeat, so clearing it here never hides a new press.
        keys[virtualKey] = false;
        return held.Add(virtualKey) || repeat;
    }

    private static bool Down(VirtualKey key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
