using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>A yes/no modal: drawn every frame at the window root, opened elsewhere with <c>ImGui.OpenPopup(id)</c>.</summary>
internal static class Confirm
{
    /// <summary>Draws the modal while it is open: <paramref name="body"/>, then the confirm button and Cancel.</summary>
    public static void Modal(string id, Action body, string confirmLabel, Action onConfirm, bool danger = false)
    {
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        body();
        ImGui.Spacing();
        if (danger)
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool confirmed = ImGui.Button(confirmLabel, new Vector2(Theme.S(110), 0));
        if (danger)
            ImGui.PopStyleColor();
        if (confirmed)
        {
            onConfirm();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(110), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
