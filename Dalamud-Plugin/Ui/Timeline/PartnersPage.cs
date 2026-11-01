using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Actors;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The start page's Partners page: one row per preset with its links and Spawn or Despawn, and the editor New preset and
/// Edit open. The plugins' lists are read when the page first draws and again every 5 seconds while it shows.
/// </summary>
/// <param name="unsaved">Whether a preset's Timeline workspace has unsaved edits, which its Delete question names.</param>
/// <param name="deleted">A preset was deleted: its Timeline workspace and kept edits go.</param>
internal sealed class PartnersPage(PartnerService partners, Func<Guid, bool>? unsaved = null, Action<Guid>? deleted = null)
{
    private const string EditPopup = "##partner-edit";
    private const int MaxName = 21;
    private const double ListsEvery = 5;

    // The plugins' lists and whether each is loaded, as last read, and when (ImGui time).
    private IReadOnlyList<PartnerLink> collections = [], designs = [], profiles = [];
    private bool penumbra, glamourer, customizePlus;
    private double listsRead = double.NegativeInfinity;

    // Why the last Spawn of a preset was refused, until its next Spawn or Despawn.
    private readonly Dictionary<Guid, string> refused = [];
    // The preset whose row asks "Delete {name}?".
    private Guid? confirming;

    // The editor: opened at the page's own level on the next draw (a menu item can't open it from inside its popup).
    private bool openEditor;
    private Guid editId;
    private string editName = string.Empty;
    private PartnerLink? editCollection, editDesign, editProfile;
    private string? editError;
    private string collectionSearch = string.Empty, designSearch = string.Empty, profileSearch = string.Empty;

    public void Draw()
    {
        if (ImGui.GetTime() - listsRead >= ListsEvery)
            ReadLists();
        var inGPose = partners.Actors.InGPose;
        if (!inGPose)
        {
            Widgets.HintWrapped(PartnerService.NotInGPose);
            ImGui.Spacing();
        }
        if (ImGui.Button("New preset###partner-new"))
            Edit(null);
        if (openEditor)
        {
            openEditor = false;
            ImGui.OpenPopup(EditPopup);
        }
        DrawEditor();
        ImGui.Spacing();

        var presets = partners.Store.Presets;
        if (confirming is { } asked && !presets.Any(preset => preset.Id == asked))
            confirming = null;
        if (presets.IsEmpty)
        {
            Widgets.EmptyState(FontAwesomeIcon.UserFriends, "No partners yet", "Make one with New preset.");
            return;
        }
        foreach (var preset in presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var id = ImRaii.PushId(preset.Id.ToString("N"));
            DrawRow(preset, inGPose);
            ImGui.Separator();
        }
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

    /// <summary>One preset: its name and Spawn or Despawn, its links under them, then its problems; right-click for Edit and Delete.</summary>
    private void DrawRow(PartnerPreset preset, bool inGPose)
    {
        var view = partners.Find(preset.Id);
        using (ImRaii.Group())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(preset.Name);
            var label = view == null ? "Spawn" : "Despawn";
            var width = Math.Max(ImGui.CalcTextSize("Spawn").X, ImGui.CalcTextSize("Despawn").X) + ImGui.GetStyle().FramePadding.X * 2;
            // Placed with SetCursorPosX: inside a group SameLine's offset counts from where the group starts.
            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX() + Theme.Gap, ImGui.GetContentRegionMax().X - width));
            if (view is { Stage: PartnerStage.Waiting or PartnerStage.Drawn })
            {
                // The spinner sits at the button's right edge, in the button's place.
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - ImGui.GetFrameHeight());
                Widgets.Spinner();
            }
            else
            {
                using (ImRaii.Disabled(view == null && !inGPose))
                {
                    if (ImGui.Button($"{label}###partner-spawn-{preset.Id:N}", new Vector2(width, 0)))
                        Toggle(preset, view);
                }
                if (view == null && !inGPose)
                    TimelineUi.Tip(PartnerService.NotInGPose);
            }
            DrawLinks(preset);
        }
        using (var menu = ImRaii.ContextPopupItem($"##partner-menu-{preset.Id:N}"))
        {
            if (menu.Success)
            {
                if (ImGui.MenuItem("Edit"))
                    Edit(preset);
                if (ImGui.MenuItem("Delete"))
                    confirming = preset.Id;
            }
        }

        if (refused.TryGetValue(preset.Id, out var refusal))
            Problem(refusal);
        if (view != null)
            foreach (var problem in view.Problems)
                Problem(problem);

        if (confirming == preset.Id)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(TimelineWindowViews.DeleteQuestion(preset.Name, unsaved?.Invoke(preset.Id) == true));
            ImGui.SameLine(0, Theme.Gap);
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
    }

    private void Toggle(PartnerPreset preset, PartnerView? view)
    {
        refused.Remove(preset.Id);
        if (view != null)
        {
            partners.Despawn(preset.Id);
            return;
        }
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

    /// <summary>The set links joined by " · ", each by its name now; one gone from its loaded plugin's list in the warning colour.</summary>
    private void DrawLinks(PartnerPreset preset)
    {
        var first = true;
        Link(preset.Collection, penumbra, collections, "Penumbra", ref first);
        Link(preset.Design, glamourer, designs, "Glamourer", ref first);
        Link(preset.Profile, customizePlus, profiles, "Customize+", ref first);
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
        {
            ImGui.TextColored(Theme.Hint, link.Name);
            TimelineUi.Tip($"{plugin} isn't loaded");
        }
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

    private void DrawEditor()
    {
        using var popup = ImRaii.Popup(EditPopup);
        if (!popup.Success)
            return;
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
