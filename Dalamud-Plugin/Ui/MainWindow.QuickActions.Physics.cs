using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private Action? _openPhysics;

    internal void AttachPhysics(Action open) => _openPhysics = open;

    private QuickAction PhysicsCard => new("body-physics", FontAwesomeIcon.Feather, "Body physics builder",
        "Build and customize a body physics preset for your character with collissions fit around your body and Customize+ profile." +
        "Easily tune each area with sliders to your liking, preview the result right away, then save it permanently to a mod.",
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
