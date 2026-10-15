using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace InstantEdit.Ui;

/// <summary>
/// The physics builder's drawing on the character: a window over the whole game screen, kept behind
/// every other window and taking no input, where the builder draws the area being edited (its tip,
/// swing cone, spring and collision shapes) while it's open. Like <see cref="BonesOverlay"/>.
/// </summary>
internal sealed class PhysicsOverlay : Window
{
    private const ImGuiWindowFlags Still = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;

    private readonly PhysicsWindow physics;

    internal PhysicsOverlay(PhysicsWindow physics)
        : base("##XIVInstantEditPhysicsOverlay", Still, true)
    {
        this.physics = physics;
        // Always open; whether it draws is the builder's call.
        IsOpen = true;
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        InhibitAtkCollision = false;
    }

    public override bool DrawConditions() => physics.OverlayWanted;

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        // Behind every other window, the builder included.
        ImGuiP.BringWindowToDisplayBack(ImGuiP.GetCurrentWindow());
        physics.DrawOverlay();
    }
}
