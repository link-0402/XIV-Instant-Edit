using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Actors;
using InstantEdit.Services.Animations.Preview;

namespace InstantEdit.Ui.Timeline;

/// <summary>What an actor's workspace does now, for the dot by its name.</summary>
internal enum ActorActivity : byte { Idle, Playing, Live }

/// <summary>
/// The dock's actors: You and every partner spawned through the plugin, one click to edit one (it becomes the GPose target),
/// right-click to despawn a partner; the + popup lists the presets, spawns one with a click, makes a new one and edits or
/// deletes one on its right-click. Folded, the actors are round icons. The plugins' lists are read when the popup first
/// draws and again every 5 seconds while it or the editor shows.
/// </summary>
/// <param name="shown">The workspace the window shows.</param>
/// <param name="target">Makes the GPose actor at an object index the GPose target, which shows its workspace.</param>
/// <param name="activity">What an actor's workspace does now.</param>
/// <param name="unsaved">Whether a preset's Timeline workspace has unsaved edits, which its Delete question names.</param>
/// <param name="deleted">A preset was deleted: its Timeline workspace and kept edits go.</param>
internal sealed class ActorList(PartnerService partners, Func<WorkspaceKey> shown, Action<int> target, Func<WorkspaceKey, ActorActivity> activity,
    Func<Guid, bool>? unsaved = null, Action<Guid>? deleted = null)
{
    private const string SpawnPopup = "##actor-spawn";
    private const string EditPopup = "##partner-edit";
    private const int MaxName = 21;
    private const double ListsEvery = 5;

    // The plugins' lists and whether each is loaded, as last read, and when (ImGui time).
    private IReadOnlyList<PartnerLink> collections = [], designs = [], profiles = [];
    private bool penumbra, glamourer, customizePlus;
    private double listsRead = double.NegativeInfinity;

    // Why the last Spawn of a preset was refused, until its next Spawn or Despawn.
    private readonly Dictionary<Guid, string> refused = [];
    // The preset whose row in the + popup asks "Delete {name}?".
    private Guid? confirming;

    // The editor: opened at the list's own level on the next draw (a popup's item can't open it from inside its popup).
    private bool openEditor;
    private Guid editId;
    private string editName = string.Empty;
    private PartnerLink? editCollection, editDesign, editProfile;
    private string? editError;
    private string collectionSearch = string.Empty, designSearch = string.Empty, profileSearch = string.Empty;

    /// <summary>"Actor" with the + button, then a row per actor and why a spawn was refused or a partner has problems.</summary>
    public void Draw()
    {
        var inGPose = partners.Actors.InGPose;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Actor");
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight()));
        if (Widgets.IconButton("##actor-add", FontAwesomeIcon.UserPlus, "Spawn a partner"))
            ImGui.OpenPopup(SpawnPopup);
        DrawSpawnPopup(inGPose);
        DrawEditorAtListLevel();

        var current = shown();
        DrawRow(WorkspaceKey.Self, "You", null, current.IsSelf, inGPose);
        foreach (var preset in partners.Store.Presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
            if (partners.Find(preset.Id) is { } view)
                DrawRow(new WorkspaceKey(preset.Id), view.Name, view, current.Preset == preset.Id, inGPose);

        foreach (var preset in partners.Store.Presets)
        {
            if (refused.TryGetValue(preset.Id, out var refusal))
                Problem($"{preset.Name}: {refusal}");
            if (partners.Find(preset.Id) is { } view)
                foreach (var problem in view.Problems)
                    Problem($"{view.Name}: {problem}");
        }
    }

    /// <summary>The folded dock's actors: You and each spawned partner as a round icon, the shown one filled.</summary>
    public void DrawFolded()
    {
        var current = shown();
        var inGPose = partners.Actors.InGPose;
        DrawIcon(WorkspaceKey.Self, "You", null, current.IsSelf, inGPose);
        foreach (var preset in partners.Store.Presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
            if (partners.Find(preset.Id) is { } view)
                DrawIcon(new WorkspaceKey(preset.Id), view.Name, view, current.Preset == preset.Id, inGPose);
    }

    /// <summary>One actor: its dot or figure and its name; a click shows it, a partner's right-click despawns it.</summary>
    private void DrawRow(WorkspaceKey key, string name, PartnerView? view, bool selected, bool inGPose)
    {
        using var id = ImRaii.PushId(key.Id);
        // You are always there; a partner once it has spawned.
        var ready = view == null || view.Stage == PartnerStage.Ready;
        var left = ImGui.GetCursorPosX();
        if (ImGui.Selectable("##actor", selected, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())) && ready && !selected && inGPose)
            target(view?.ObjectIndex ?? AnimationPlayback.GPosePlayerIndex);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!ready ? TimelineWindowViews.Spawning(name) : selected ? $"Editing {name}." : $"Edit {name}.");
        if (view != null)
        {
            using var menu = ImRaii.ContextPopupItem("##actor-menu");
            if (menu.Success && ImGui.MenuItem("Despawn"))
            {
                refused.Remove(view.PresetId);
                partners.Despawn(view.PresetId);
            }
        }
        ImGui.SameLine(left + Theme.Scaled(4));
        DrawActivity(key);
        ImGui.SameLine(left + Theme.Scaled(22));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(name);
        if (view is { Stage: PartnerStage.Waiting or PartnerStage.Drawn })
        {
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight()));
            Widgets.Spinner();
        }
    }

    /// <summary>A dot in the colour of what the actor's workspace does, or a muted figure while it does nothing.</summary>
    private void DrawActivity(WorkspaceKey key)
    {
        var now = activity(key);
        if (now == ActorActivity.Idle)
        {
            Widgets.Icon(FontAwesomeIcon.User, Theme.Muted);
            return;
        }
        var size = ImGui.GetFrameHeight();
        var at = ImGui.GetCursorScreenPos() + new Vector2(Theme.Scaled(6), size / 2);
        ImGui.GetWindowDrawList().AddCircleFilled(at, Theme.Scaled(4), ImGui.GetColorU32(now == ActorActivity.Live ? Theme.Success : Theme.AnimationKind));
        ImGui.Dummy(new Vector2(Theme.Scaled(12), size));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(now == ActorActivity.Live ? "Live is on." : "Playing.");
    }

    /// <summary>One actor of the folded dock: a round button with "You"'s figure or the partner's initials.</summary>
    private void DrawIcon(WorkspaceKey key, string name, PartnerView? view, bool selected, bool inGPose)
    {
        using var id = ImRaii.PushId(key.Id);
        var ready = view == null || view.Stage == PartnerStage.Ready;
        var size = new Vector2(Theme.Scaled(24));
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), (ImGui.GetContentRegionAvail().X - size.X) / 2 + ImGui.GetCursorPosX()));
        bool clicked;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, size.X / 2))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, selected))
        {
            if (view == null)
            {
                using var font = ImRaii.PushFont(UiBuilder.IconFont);
                clicked = ImGui.Button(FontAwesomeIcon.User.ToIconString(), size);
            }
            else
                clicked = ImGui.Button(TimelineWindowViews.Initials(name), size);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!ready ? TimelineWindowViews.Spawning(name) : name);
        if (clicked && ready && !selected && inGPose)
            target(view?.ObjectIndex ?? AnimationPlayback.GPosePlayerIndex);
    }

    /// <summary>
    /// The + popup: every preset, a click spawns it (a spawned one is ticked), right-click edits or deletes it, then New preset.
    /// </summary>
    private void DrawSpawnPopup(bool inGPose)
    {
        using var popup = ImRaii.Popup(SpawnPopup);
        if (!popup.Success)
        {
            confirming = null;
            return;
        }
        if (ImGui.IsWindowAppearing() || ImGui.GetTime() - listsRead >= ListsEvery)
            ReadLists();
        var presets = partners.Store.Presets;
        if (presets.IsEmpty)
            ImGui.TextColored(Theme.Hint, "No presets yet.");
        else if (!inGPose)
            ImGui.TextColored(Theme.Hint, PartnerService.NotInGPose);
        foreach (var preset in presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var id = ImRaii.PushId(preset.Id.ToString("N"));
            var view = partners.Find(preset.Id);
            using (ImRaii.Disabled(!inGPose && view == null))
            {
                if (ImGui.Selectable(preset.Name + "##preset", view != null, ImGuiSelectableFlags.DontClosePopups) && view == null)
                {
                    Spawn(preset);
                    ImGui.CloseCurrentPopup();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                DrawPresetTooltip(preset, view != null);
            using (var menu = ImRaii.ContextPopupItem("##preset-menu"))
            {
                if (menu.Success)
                {
                    if (ImGui.MenuItem("Edit"))
                    {
                        Edit(preset);
                        ImGui.CloseCurrentPopup();
                    }
                    if (ImGui.MenuItem("Delete"))
                        confirming = preset.Id;
                }
            }
            if (confirming == preset.Id)
                DrawDeleteQuestion(preset);
        }
        ImGui.Separator();
        if (ImGui.Selectable("New preset##preset-new"))
            Edit(null);
    }

    /// <summary>A preset's links joined by " · ", one gone from its loaded plugin's list marked missing, and what a click does.</summary>
    private void DrawPresetTooltip(PartnerPreset preset, bool spawned)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextUnformatted(spawned ? "Spawned. Right-click to edit or delete it." : "Click to spawn. Right-click to edit or delete it.");
        var first = true;
        Link(preset.Collection, penumbra, collections, "Penumbra", ref first);
        Link(preset.Design, glamourer, designs, "Glamourer", ref first);
        Link(preset.Profile, customizePlus, profiles, "Customize+", ref first);
    }

    /// <summary>"Delete {name}?" with Delete and Keep, under the preset in the + popup.</summary>
    private void DrawDeleteQuestion(PartnerPreset preset)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(TimelineWindowViews.DeleteQuestion(preset.Name, unsaved?.Invoke(preset.Id) == true));
        if (ImGui.Button("Delete###partner-delete-go"))
        {
            confirming = null;
            refused.Remove(preset.Id);
            partners.Delete(preset.Id);
            deleted?.Invoke(preset.Id);
        }
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Button("Keep###partner-delete-keep"))
            confirming = null;
    }

    private void ReadLists()
    {
        listsRead = ImGui.GetTime();
        penumbra = partners.Collections.Available;
        glamourer = partners.Looks.Available;
        customizePlus = partners.Profiles.Available;
        collections = penumbra ? partners.Collections.List() : [];
        designs = glamourer ? partners.Looks.List() : [];
        profiles = customizePlus ? partners.Profiles.List() : [];
    }

    private void Spawn(PartnerPreset preset)
    {
        refused.Remove(preset.Id);
        string? refusal;
        // The service has undone the spawn and logged the error before it rethrows.
        try { refusal = partners.Spawn(preset); }
        catch (Exception) { refusal = PartnerService.LinksFailed; }
        if (refusal != null)
            refused[preset.Id] = refusal;
    }

    private static void Problem(string text)
    {
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Warning, text);
    }

    private static void Link(PartnerLink? link, bool available, IReadOnlyList<PartnerLink> list, string plugin, ref bool first)
    {
        if (link == null)
            return;
        if (!first)
        {
            ImGui.SameLine(0, 0);
            ImGui.TextColored(Theme.Hint, " · ");
            ImGui.SameLine(0, 0);
        }
        first = false;
        // Without the plugin nothing can be called missing.
        if (!available)
            ImGui.TextColored(Theme.Hint, $"{link.Name} ({plugin} isn't loaded)");
        else if (Listed(list, link.Id) is { } now)
            ImGui.TextColored(Theme.Hint, now.Name);
        else
            ImGui.TextColored(Theme.Warning, link.Name + " (missing)");
    }

    private static PartnerLink? Listed(IReadOnlyList<PartnerLink> list, Guid id)
    {
        foreach (var item in list)
            if (item.Id == id)
                return item;
        return null;
    }

    /// <summary>Fills the editor from <paramref name="preset"/>, or empty for a new one, and opens it on the next draw.</summary>
    private void Edit(PartnerPreset? preset)
    {
        editId = preset?.Id ?? Guid.NewGuid();
        editName = preset?.Name ?? string.Empty;
        editCollection = preset?.Collection;
        editDesign = preset?.Design;
        editProfile = preset?.Profile;
        editError = null;
        openEditor = true;
    }

    /// <summary>The editor, opened at the list's own level once an item of the + popup asked for it.</summary>
    private void DrawEditorAtListLevel()
    {
        if (openEditor)
        {
            openEditor = false;
            ReadLists();
            ImGui.OpenPopup(EditPopup);
        }
        using var popup = ImRaii.Popup(EditPopup);
        if (!popup.Success)
            return;
        if (ImGui.GetTime() - listsRead >= ListsEvery)
            ReadLists();
        var labels = Math.Max(ImGui.CalcTextSize("Collection").X, Math.Max(ImGui.CalcTextSize("Design").X, ImGui.CalcTextSize("Profile").X)) + Theme.Gap;
        var field = Theme.Scaled(200);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(labels + field);
        ImGui.InputTextWithHint("##partner-edit-name", "Name", ref editName, MaxName);
        Picker("Collection", "##partner-edit-collection", "Search collections", "Penumbra", penumbra, collections, ref editCollection, ref collectionSearch, labels, field);
        Picker("Design", "##partner-edit-design", "Search designs", "Glamourer", glamourer, designs, ref editDesign, ref designSearch, labels, field);
        Picker("Profile", "##partner-edit-profile", "Search profiles", "Customize+", customizePlus, profiles, ref editProfile, ref profileSearch, labels, field);
        if (ImGui.Button("Save##partner-edit-save"))
        {
            if (partners.Store.Save(new PartnerPreset(editId, editName, editCollection, editDesign, editProfile), partners.Actors.PlayerName, out var error))
                ImGui.CloseCurrentPopup();
            else
                editError = error;
        }
        if (editError != null)
            Widgets.HintWrapped(editError);
    }

    /// <summary>A link's dropdown: a search box, None, then the plugin's list; disabled while the plugin isn't loaded.</summary>
    private static void Picker(string label, string id, string hint, string plugin, bool available, IReadOnlyList<PartnerLink> list, ref PartnerLink? link,
        ref string search, float labels, float field)
    {
        var left = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.SameLine(left + labels);
        ImGui.SetNextItemWidth(field);
        var preview = link == null ? "None" : available && Listed(list, link.Id) == null ? link.Name + " (missing)" : link.Name;
        using (ImRaii.Disabled(!available))
        {
            using var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLarge);
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                {
                    search = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }
                Widgets.SearchBox(id + "-search", ref search, hint);
                if (ImGui.Selectable("None##none", link == null))
                    link = null;
                foreach (var item in list)
                {
                    if (search.Length > 0 && !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{item.Name}##{item.Id:N}", link?.Id == item.Id))
                        link = item;
                }
            }
        }
        if (!available)
            TimelineUi.Tip($"{plugin} isn't loaded");
    }
}
