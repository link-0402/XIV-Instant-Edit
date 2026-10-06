using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

/// <summary>
/// The bones on the posed character: a window over the whole game screen, kept behind every other
/// window, where the Timeline window draws the GPose character's bones and the gizmo while the Bones
/// window is open. It takes no input itself, so it never covers the game or other windows; the dots and
/// the gizmo read the mouse on their own, and only a gizmo drag makes it take the mouse.
/// </summary>
internal sealed class BonesOverlay : Window
{
    private const ImGuiWindowFlags Still = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;

    private readonly TimelineWindow timeline;

    internal BonesOverlay(TimelineWindow timeline)
        : base("##XIVInstantEditBonesOverlay", Still, true)
    {
        this.timeline = timeline;
        // Always open; whether it draws is the Timeline window's call.
        IsOpen = true;
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        InhibitAtkCollision = false;
    }

    public override bool DrawConditions() => timeline.OverlayWanted;

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        // While the gizmo is dragged the window takes the mouse, so the game's camera stays put.
        Flags = timeline.OverlayDragging ? Still & ~ImGuiWindowFlags.NoInputs : Still;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        // Behind every other window, the Bones window included.
        ImGuiP.BringWindowToDisplayBack(ImGuiP.GetCurrentWindow());
        timeline.DrawBonesOverlay();
    }
}
