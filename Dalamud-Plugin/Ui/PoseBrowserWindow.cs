using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

/// <summary>
/// The pose browser: your pose folders as tiles, where a click keys a pose in the Timeline window's selected
/// pose layer at its playhead and the next click replaces it. It follows the Timeline window, which owns
/// everything it shows and draws it.
/// </summary>
internal sealed class PoseBrowserWindow : Window
{
    private readonly TimelineWindow timeline;

    internal PoseBrowserWindow(TimelineWindow timeline)
        : base("Pose browser###XIVInstantEditPoses")
    {
        this.timeline = timeline;
        Size = new Vector2(980, 690);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        // The tiles scroll in a child of their own; the footer stays.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        AllowPinning = true;
        RespectCloseHotkey = true;
    }

    public override void Draw() => timeline.DrawPoseBrowser();

    public override void OnClose() => timeline.PoseBrowserClosed();
}
