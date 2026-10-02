using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Painter;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private IReadOnlyList<OnScreenObject>? _paintSkinSnapshot;
    private (OnScreenObject Character, ResourceView Face)? _paintSkinCharacter;

    private QuickAction PaintSkinCard => new("paint-skin", FontAwesomeIcon.PaintRoller, "Paint my skin",
        "Opens your character's bare skin in Substance Painter as one project: the torso, hands, legs and feet based on your mods affecting Smallclothes / Nothing." +
        "The can can be optionally included if you need to paint across the neck seam for tattoos covering the neck.",
        DrawPaintSkinAction);

    private void DrawPaintSkinAction()
    {
        if (_painter is null)
        {
            Widgets.Hint("Unavailable: the Substance Painter tools did not start.");
            return;
        }
        if (!_config.PainterIntegrationEnabled)
        {
            Widgets.HintWrapped("Turn on Paint textures in Substance Painter on the Editors tab of Settings to use this.");
            if (ImGui.Button("Open Settings##quick-paint-skin"))
                _openSettings();
            return;
        }
        if (PaintSkinCharacter() is not ({ } character, { } face))
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character has no face model loaded. Refresh the list once your character is drawn.");
            return;
        }

        using (ImRaii.Disabled(Volatile.Read(ref _painterBusy) != 0))
        {
            if (ImGui.Button("Paint my skin##quick-paint-skin"))
                StartSkinPainter(character, face);
        }
    }

    /// <summary> Your character and the face it draws, looked up again only when the snapshot changes. </summary>
    private (OnScreenObject Character, ResourceView Face)? PaintSkinCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _paintSkinSnapshot))
        {
            _paintSkinSnapshot = items;
            _paintSkinCharacter = null;
            foreach (var item in items.Where(item => item.PresentationCategory == ActorPresentationCategory.Player))
            {
                var face = item.ResourceRoots.Select(ResourceViews.FromNode).SelectMany(ResourceViews.Flatten)
                    .FirstOrDefault(node => node.IsModel && ResourceViews.IsSafeModel(node) && NeckSeamAnalyzer.IsFaceModel(node.GamePath));
                if (face is null)
                    continue;
                _paintSkinCharacter = (item, face);
                break;
            }
        }
        return _paintSkinCharacter;
    }

    private void StartSkinPainter(OnScreenObject character, ResourceView face)
    {
        // Every resource counts, vanilla ones too, whatever On Screen shows. The body parts are the
        // smallclothes, which the character may not wear now: the Painter service looks them up.
        var roots = character.ResourceRoots.Select(ResourceViews.FromNode).ToList();
        var actor = new ActorView(character, character.PresentationCategory.ToString(), Safe(character.Name), roots, character.ObjectIndex);
        // Models of other races are always shaped for the character here: the body only meets the face
        // at the neck in the shape the game gives it.
        PreparePainter(actor, ModelRef(face), [], PainterScope.Skin, scale: true, "Reading your character's smallclothes, face, materials and textures");
    }
}
