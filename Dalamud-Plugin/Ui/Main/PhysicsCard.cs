using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Main;

/// <summary> Quick Actions' Body physics builder: opens the physics builder window. </summary>
internal sealed class PhysicsCard
{
    private Action? _openPhysics;

    public PhysicsCard()
    {
        Card = new QuickAction("body-physics", FontAwesomeIcon.Feather, "Body physics builder",
            "Build and customize a body physics preset for your character with collissions fit around your body and Customize+ profile." +
            "Easily tune each area with sliders to your liking, preview the result right away, then save it permanently to a mod.",
            DrawPhysicsAction);
    }

    public QuickAction Card { get; }

    public void Attach(Action open) => _openPhysics = open;

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
