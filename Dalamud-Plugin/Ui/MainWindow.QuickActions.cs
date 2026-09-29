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
        QuickActionCard("##quick-neck-seam", FontAwesomeIcon.UserCheck, "Fix skin seams",
            "Compares your character's skin where its models meet, the way the game's skin shader draws them: the face and body at the neck, " +
            "and the body parts at the wrists, waist and ankles. Builds a preview mod that closes gaps and fixes normals, skin settings and textures.",
            DrawNeckSeamAction);
        DrawCharacterWeightCard();
        DrawPaintSkinCard();
        DrawHeelsOffsetCard();
    }

    private void DrawNeckSeamAction()
    {
        if (_neckSeam is null)
        {
            Widgets.Hint("Unavailable: the skin seam tools did not start.");
            return;
        }
        if (NeckSeamCharacter() is not { } character)
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character has no face or body models loaded. Refresh the list once your character is drawn.");
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

    /// <summary> Your character when it has a face or body part model, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? NeckSeamCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _quickSnapshot))
        {
            _quickSnapshot = items;
            _neckSeamCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player
                                                              && NeckSeamCapture.HasSkinModels(item.ResourceRoots));
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
