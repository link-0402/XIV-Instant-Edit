using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui.Main;

/// <summary> Quick Actions' Fix skin seams: opens the skin seam check for your character. </summary>
internal sealed class SkinSeamCard
{
    private readonly OnScreenService _onScreen;
    private readonly SkinSeamDialog _dialog;
    private IReadOnlyList<OnScreenObject>? _quickSnapshot;
    private OnScreenObject? _neckSeamCharacter;

    public SkinSeamCard(OnScreenService onScreen, SkinSeamDialog dialog)
    {
        _onScreen = onScreen;
        _dialog = dialog;
        Card = new QuickAction("skin-seams", FontAwesomeIcon.UserCheck, "Fix skin seams",
            "Automatically fix skin seams between model parts, such as on the neck, wrists, ankles and belly. The neck fix is safe, but use the other options carefully. " +
            "Builds a preview mod from the fixed files. Open the check window again to put the contents of the preview mod into the original mods. " +
            "It can also match one skin part's tone to another, such as a face mod's ears to the face.",
            DrawNeckSeamAction);
    }

    public QuickAction Card { get; }

    private void DrawNeckSeamAction()
    {
        if (_dialog.Service is null)
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

        using (ImRaii.Disabled(_dialog.Busy.IsBusy))
        {
            if (ImGui.Button("Check and fix##quick-neck-seam"))
                _dialog.Open(character);
        }
        if (_dialog.Service.PreviewFor(character.Name) is not null)
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
}
