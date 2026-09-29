using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private IReadOnlyList<OnScreenObject>? _quickSnapshot;
    private OnScreenObject? _neckSeamCharacter;

    /// <summary> One card per task that works on your whole character rather than on a single file. </summary>
    private void DrawQuickActionsTab()
    {
        ImGui.Spacing();
        using var scroll = ImRaii.Child("##quick-actions", Vector2.Zero, false);
        if (!scroll.Success)
            return;
        QuickActionCard("##quick-neck-seam", FontAwesomeIcon.UserCheck, "Fix neck seam",
            "Compares your character's face and body where they meet at the neck, the way the game's skin shader draws them, " +
            "and builds a preview mod that fixes the connection data, skin settings and textures.",
            DrawNeckSeamAction);
        DrawHeelsOffsetCard();
    }

    private void DrawNeckSeamAction()
    {
        if (_neckSeam is null)
        {
            Widgets.Hint("Unavailable: the neck seam tools did not start.");
            return;
        }
        if (NeckSeamCharacter() is not { } character)
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character has no face model loaded. Refresh the list once your character is drawn.");
            return;
        }

        using (ImRaii.Disabled(Volatile.Read(ref _neckSeamBusy) != 0))
        {
            if (ImGui.Button("Check and fix##quick-neck-seam"))
                OpenNeckSeam(character);
        }
        if (_neckSeam.PreviewFor(character.Name) is not null)
            Widgets.HintWrapped("A preview mod is active. Open the check to apply it to your mods or discard it.");
    }

    /// <summary> Your character when it has a face model, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? NeckSeamCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _quickSnapshot))
        {
            _quickSnapshot = items;
            _neckSeamCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player
                                                              && NeckSeamCapture.HasFaceModel(item.ResourceRoots));
        }
        return _neckSeamCharacter;
    }

    /// <summary> A bordered card: icon and title, a wrapped description, then the action's controls. </summary>
    private static void QuickActionCard(string id, FontAwesomeIcon icon, string title, string description, Action body)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Theme.Scaled(10, 8));
        using var table = ImRaii.Table(id, 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.RowBg);
        if (!table.Success)
            return;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        Widgets.Icon(icon, Theme.Accent);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, title);
        Widgets.HintWrapped(description);
        ImGui.Spacing();
        body();
    }
}
