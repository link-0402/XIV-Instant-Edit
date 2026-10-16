using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// Stage 1 — find what to port. The canvas is the game browser (gear by slot, character
/// features by race and id); the inspector shows what the game ships for the highlighted row,
/// so it can be checked before it joins the modpack.
/// </summary>
internal sealed class BrowsePanel : IStagePanel
{
    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly ItemBrowserPanel _browser;

    public BrowsePanel(StudioHost host, PortSession session)
    {
        _host  = host;
        _session = session;
        _browser = new ItemBrowserPanel(host, session);
    }

    public StageId         Id      => StageId.Browse;
    public string          Title   => "Browse";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Search;
    public string          Purpose => "Find gear or a character feature and add it to the modpack.";

    public void DrawCanvas() => _browser.Draw();

    public void DrawInspector()
    {
        var subject = _browser.Highlighted;
        if (subject == null)
        {
            Gui.HintWrapped("Click a row to see what the game ships for it. Press + on it, or drag it onto the list on the left, to add it to the modpack.");
            return;
        }

        bool inPack = _session.IsInModpack(subject.Key);
        Gui.SectionHeader(subject.DisplayName, inPack ? "Already in the modpack." : null);

        if (subject is GearSubject gear)
        {
            Gui.LabeledValue("Slot", SlotInfo.DisplayLabelMap[gear.Item.Slot]);
            Gui.LabeledValue("Model", gear.Item.ModelIdDisplay, "Model set id and variant.");
        }
        else if (subject is FeatureSubject feature)
        {
            Gui.LabeledValue("Race", feature.Race.DisplayName);
            Gui.LabeledValue("Id", feature.IdDisplay);
        }
        else if (subject is WeaponSubject weapon)
        {
            Gui.LabeledValue("Class", weapon.Model.Category.Length > 0 ? weapon.Model.Category : "Weapon");
            Gui.LabeledValue("Model", weapon.Model.OffHand ? $"{weapon.IdDisplay}, off hand" : weapon.IdDisplay, "Weapon id and body.");
            Gui.LabeledValue("Game path", weapon.ModelGamePath(default));
        }
        Gui.LabeledValue("Materials", subject.MaterialFolder(subject.BaseRace), "Every .mtrl the mod writes for this goes here.");
        Gui.LabeledValue("Textures", subject.TextureFolder);

        ImGui.Spacing();
        if (subject.MultiRace)
        {
            var native = _host.GameData.GetNativeRaces(subject);
            ImGui.TextColored(Theme.Muted, subject is GearSubject ? "Races with their own model" : "Races that have this id");
            if (ImGui.BeginTable("##BrowseRaces", 2, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var rg in RaceInfo.AllRaces)
                {
                    ImGui.TableNextColumn();
                    bool has = native.Contains(rg);
                    Gui.Icon(has ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus, has ? Theme.Good : Theme.Faint);
                    ImGui.SameLine();
                    ImGui.TextColored(has ? Theme.Muted : Theme.Faint, rg.DisplayName);
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        if (inPack)
        {
            if (Gui.IconTextButton(FontAwesomeIcon.ArrowRight, "Open in Details", "Show this item's set-up."))
                _session.Focus(new Target(TargetKind.Item, subject.Key));
        }
        else if (Gui.IconTextButton(FontAwesomeIcon.Plus, "Add to modpack"))
        {
            _session.AddItem(subject);
        }
    }
}
