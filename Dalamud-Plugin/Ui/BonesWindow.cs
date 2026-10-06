using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

/// <summary>
/// The Bones window: the body's bone map and a list of every bone to pick bones on, and the panel
/// that turns, moves and scales them in the Timeline window's selected bone layer at its playhead.
/// It follows the Timeline window, which owns everything it shows and draws it.
/// </summary>
internal sealed class BonesWindow : Window
{
    private readonly TimelineWindow timeline;

    internal BonesWindow(TimelineWindow timeline)
        : base("Bones###XIVInstantEditBones")
    {
        this.timeline = timeline;
        Size = new Vector2(1200, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        // The map and the rotation ball read the wheel themselves.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        AllowPinning = true;
        RespectCloseHotkey = true;
    }

    public override void Draw() => timeline.DrawBones();
}
