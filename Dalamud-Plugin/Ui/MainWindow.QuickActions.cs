using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string AddQuickActionPopup = "##quick-add";

    private IReadOnlyList<OnScreenObject>? _quickSnapshot;
    private OnScreenObject? _neckSeamCharacter;
    private QuickAction[]? _quickActions;

    /// <summary>
    /// A Quick Actions card. The key is what the configuration stores when the card is removed from the tab or moved,
    /// so it never changes once released. Removing the card calls TurnOff, which stops whatever the card runs on its own.
    /// A card with IsOn runs on its own while it is on: it joins the automatic group and turns green while on.
    /// </summary>
    private sealed record QuickAction(string Key, FontAwesomeIcon Icon, string Title, string Description, Action Body,
        Action? TurnOff = null, Func<bool>? IsOn = null)
    {
        public bool Automatic => IsOn is not null;
    }

    /// <summary> Every card in its built-in order. The tab sorts them with <see cref="OrderedQuickActions"/>. </summary>
    private QuickAction[] QuickActions => _quickActions ??=
    [
        TextureCompressionCard,
        HeelsOffsetCard,
        NeckSeamCard,
        PhysicsCard,
        PaintSkinCard,
        SendCharacterCard,
    ];

    private QuickAction NeckSeamCard => new("skin-seams", FontAwesomeIcon.UserCheck, "Fix skin seams",
        "Automatically fix skin seams between model parts, such as on the neck, wrists, ankles and belly. The neck fix is safe, but use the other options carefully. " +
        "Builds a preview mod from the fixed files. Open the check window again to put the contents of the preview mod into the original mods. " +
        "It can also match one skin part's tone to another, such as a face mod's ears to the face.",
        DrawNeckSeamAction);

    /// <summary> One card per task that works on your whole character rather than on a single file, minus the ones the user removed. </summary>
    private void DrawQuickActionsTab()
    {
        ImGui.Spacing();
        DrawQuickActionsHeader();
        using var scroll = ImRaii.Child("##quick-actions", Vector2.Zero, false);
        if (!scroll.Success)
            return;
        var automatic = ShownQuickActions(true);
        var oneTime = ShownQuickActions(false);
        DrawQuickActionGroup(automatic);
        if (automatic.Count > 0 && oneTime.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
        DrawQuickActionGroup(oneTime);
        if (automatic.Count + oneTime.Count == 0)
            Widgets.HintWrapped("You removed every card. Add them back with the + button above.");
    }

    private void DrawQuickActionGroup(List<QuickAction> group)
    {
        for (var i = 0; i < group.Count; i++)
        {
            if (i > 0)
                ImGui.Spacing();
            QuickActionCard(group, i);
        }
    }

    /// <summary> Automatic cards first, then one-time ones, each group in the order the user moved its cards into. </summary>
    private List<QuickAction> OrderedQuickActions()
    {
        var order = _config.QuickActionOrder;
        return QuickActions
            .Select((action, builtIn) => (action, builtIn, moved: order.IndexOf(action.Key)))
            .OrderBy(card => card.action.Automatic ? 0 : 1)
            .ThenBy(card => card.moved < 0 ? int.MaxValue : card.moved)
            .ThenBy(card => card.builtIn)
            .Select(card => card.action)
            .ToList();
    }

    private List<QuickAction> ShownQuickActions(bool automatic) =>
        OrderedQuickActions().Where(action => action.Automatic == automatic && QuickActionShown(action.Key)).ToList();

    /// <summary> Swaps a card with the next shown card of its group above or below, then stores the order of every card. </summary>
    private void MoveQuickAction(QuickAction action, QuickAction neighbour)
    {
        var order = OrderedQuickActions();
        var from = order.IndexOf(action);
        var to = order.IndexOf(neighbour);
        (order[from], order[to]) = (order[to], order[from]);
        _config.QuickActionOrder = order.Select(card => card.Key).ToList();
        _saveConfig();
    }

    /// <summary> The + button that adds removed cards back, right-aligned over the cards' remove buttons. </summary>
    private void DrawQuickActionsHeader()
    {
        var button = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - button - Theme.Scaled(10)));
        var hidden = QuickActions.Count(action => !QuickActionShown(action.Key));
        if (Widgets.IconButton("##quick-add-button", FontAwesomeIcon.Plus,
                hidden == 0 ? "Every Quick Actions card is on the tab. Remove one with its trash button." : "Add a removed card back",
                hidden > 0))
            ImGui.OpenPopup(AddQuickActionPopup);

        using var popup = ImRaii.Popup(AddQuickActionPopup);
        if (!popup.Success)
            return;
        foreach (var action in OrderedQuickActions())
        {
            if (!QuickActionShown(action.Key) && ImGui.MenuItem($"{action.Title}##quick-add-{action.Key}"))
                SetQuickActionShown(action, true);
        }
        if (hidden > 1)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Add all"))
                foreach (var action in QuickActions)
                    SetQuickActionShown(action, true);
        }
    }

    private bool QuickActionShown(string key) => !_config.HiddenQuickActions.Contains(key);

    /// <summary> Adds a card back, or removes it and turns off what it automates. Adding it back leaves those off. </summary>
    private void SetQuickActionShown(QuickAction action, bool shown)
    {
        var changed = shown ? _config.HiddenQuickActions.Remove(action.Key) : QuickActionShown(action.Key);
        if (!changed)
            return;
        if (!shown)
        {
            _config.HiddenQuickActions.Add(action.Key);
            action.TurnOff?.Invoke();
        }
        _saveConfig();
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

    /// <summary>
    /// A bordered card: icon, title, move and remove buttons, a wrapped description, then the action's controls.
    /// An automatic card that is on gets a green border, background and icon.
    /// </summary>
    private void QuickActionCard(List<QuickAction> group, int index)
    {
        var action = group[index];
        var on = action.IsOn?.Invoke() == true;
        using var id = ImRaii.PushId(action.Key);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Theme.Scaled(10, 8));
        // The table takes its border colour when it begins, so the push ends there and never reaches tables inside the card.
        var border = ImRaii.PushColor(ImGuiCol.TableBorderStrong, Theme.Success, on);
        using var table = ImRaii.Table("##quick-card", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.RowBg);
        border.Dispose();
        if (!table.Success)
            return;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (on)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.SuccessBg));
        Widgets.Icon(action.Icon, on ? Theme.Success : Theme.Accent);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, action.Title);
        var button = ImGui.GetFrameHeight();
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(Theme.Gap, ImGui.GetContentRegionAvail().X - button * 3));
        var kind = action.Automatic ? "automatic" : "one-time";
        using (ImRaii.Disabled(index == 0))
        {
            if (Widgets.GhostIconButton("##quick-up", FontAwesomeIcon.ChevronUp, $"Move up among the {kind} cards", new Vector2(button)))
                MoveQuickAction(action, group[index - 1]);
        }
        ImGui.SameLine(0, 0);
        using (ImRaii.Disabled(index == group.Count - 1))
        {
            if (Widgets.GhostIconButton("##quick-down", FontAwesomeIcon.ChevronDown, $"Move down among the {kind} cards", new Vector2(button)))
                MoveQuickAction(action, group[index + 1]);
        }
        ImGui.SameLine(0, 0);
        if (Widgets.GhostIconButton("##quick-remove", FontAwesomeIcon.Trash,
                "Remove this card from Quick Actions. This also turns off what it does automatically. Add it back with the + button at the top.",
                new Vector2(button)))
            SetQuickActionShown(action, false);
        Widgets.HintWrapped(action.Description);
        ImGui.Spacing();
        action.Body();
    }
}
