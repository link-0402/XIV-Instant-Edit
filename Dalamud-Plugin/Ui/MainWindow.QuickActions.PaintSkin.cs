using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Painter;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private IReadOnlyList<OnScreenObject>? _paintSkinSnapshot;
    private OnScreenObject? _paintSkinCharacter;

    private void DrawPaintSkinCard()
    {
        ImGui.Spacing();
        QuickActionCard("##quick-paint-skin", FontAwesomeIcon.PaintRoller, "Paint my skin",
            "Opens every part of your character that shows skin in Substance Painter as one project. The body, hands, legs and feet share " +
            "the body skin's textures, so tattoos, freckles and body paint can cross the wrists, waist and ankles. Tick the face in the dialog " +
            "to paint across the neck too.",
            DrawPaintSkinAction);
    }

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
        if (PaintSkinCharacter() is not { } character)
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character has no models loaded. Refresh the list once your character is drawn.");
            return;
        }

        using (ImRaii.Disabled(Volatile.Read(ref _painterBusy) != 0))
        {
            if (ImGui.Button("Paint my skin##quick-paint-skin"))
                StartSkinPainter(character);
        }
    }

    /// <summary> Your character when it has a model loaded, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? PaintSkinCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _paintSkinSnapshot))
        {
            _paintSkinSnapshot = items;
            _paintSkinCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player
                                                               && item.ResourceRoots.Select(ResourceViews.FromNode).SelectMany(ResourceViews.Flatten)
                                                                   .Any(node => node.IsModel && ResourceViews.IsSafeModel(node)));
        }
        return _paintSkinCharacter;
    }

    private void StartSkinPainter(OnScreenObject character)
    {
        // Every model counts, vanilla ones too, whatever On Screen shows.
        var roots = character.ResourceRoots.Select(ResourceViews.FromNode).ToList();
        var actor = new ActorView(character, character.PresentationCategory.ToString(), Safe(character.Name), roots, character.ObjectIndex);
        var models = roots.SelectMany(ResourceViews.Flatten)
            .Where(node => node.IsModel && ResourceViews.IsSafeModel(node))
            .Select(ModelRef)
            .ToList();
        // Models of other races are always shaped for the character here: the body only meets the face
        // at the neck in the shape the game gives it.
        PreparePainter(actor, models[0], models.Skip(1).ToList(), PainterScope.Skin, scale: true, "Reading your character's models, materials and textures");
    }
}
