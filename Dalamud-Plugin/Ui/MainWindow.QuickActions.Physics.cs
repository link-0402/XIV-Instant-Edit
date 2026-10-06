using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private Action? _openPhysics;

    internal void AttachPhysics(Action open) => _openPhysics = open;

    private QuickAction PhysicsCard => new("body-physics", FontAwesomeIcon.Feather, "Body physics",
        "Builds body physics for the skeleton your character uses. Start from the game's file, the physics your mods load or any preset, " +
        "tune each area with sliders, preview it on your character, then save it to a mod you can keep changing.",
        DrawPhysicsAction);

    private void DrawPhysicsAction()
    {
        using (ImRaii.Disabled(_openPhysics is null))
        {
            if (ImGui.Button("Open the builder##quick-physics"))
                _openPhysics?.Invoke();
        }
        if (_openPhysics is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The physics builder didn't start.");
    }
}
