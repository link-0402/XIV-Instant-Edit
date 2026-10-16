using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// The lower half of Build mode's Set-up view: the Penumbra metadata an item needs beside its files.
/// EQDP follows the configured models and materials and is only reported; EQP (gear) and EST (hair)
/// are edited here. EQP is written only while a flag differs from the game's own, EST for every race
/// of a hair port, and EQDP only where the game does not already grant what the port writes.
/// </summary>
internal sealed class ItemMetaPanel
{
    private readonly StudioHost _host;
    private readonly PortSession _session;

    public ItemMetaPanel(StudioHost host, PortSession session)
    {
        _host  = host;
        _session = session;
    }

    public void Draw(PortItem item)
    {
        Gui.SectionHeader("Metadata", "Written into the mod's meta.json as Penumbra manipulations. " +
                                     "Everything here is an override of what the game ships.");

        if (item.Subject is GearSubject gear)
        {
            DrawEqdp(item, gear);
            ImGui.Spacing();
            DrawEqp(item, gear);
        }
        else if (item.Subject is FeatureSubject { Kind: SubjectKind.Hair } hair)
        {
            DrawEst(item, hair);
        }
        else if (item.Subject is WeaponSubject)
        {
            Gui.HintWrapped("Weapons need no metadata here. Their IMC variants are pointed at your material automatically.");
        }
        else
        {
            Gui.Hint($"A {item.Subject.KindLabel.ToLowerInvariant()} needs no metadata — its files replace the game's directly.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EQDP — derived from the race models
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawEqdp(PortItem item, GearSubject gear)
    {
        var slot = gear.Item.Slot;
        var gameData = _host.GameData;
        bool accessory = SlotInfo.IsAccessory(slot);

        Heading("EQDP", "race deformer parameters", "automatic");
        Gui.Tooltip("Per race, the game records whether it has a model and a material of its own for this item. " +
                   "A race this item writes files for needs those bits, or the game keeps loading another race's. " +
                   "Follows the race models and materials — nothing to set here.");

        if (item.Models.Count == 0 && item.Materials.Count == 0)
        {
            Gui.Hint("(nothing set up yet, so no entries are needed)");
            return;
        }

        // Exactly what the build claims: a model for every race it writes one for, and a material
        // for each base race the materials are named after (male c0101, female c0201).
        var native = gameData.GetNativeRaces(item.Subject);
        var claims = new Dictionary<RaceGender, ushort>();
        foreach (var model in item.Models.Where(m => !native.Contains(m.RaceGender)))
            claims[model.RaceGender] = (ushort)(claims.GetValueOrDefault(model.RaceGender) | EqdpInfo.Model(slot));
        if (item.Materials.Count > 0)
            foreach (var race in gameData.MaterialRaces(item.Subject, item.Models).OfType<RaceGender>())
                claims[race] = (ushort)(claims.GetValueOrDefault(race) | EqdpInfo.Material(slot));

        int added = 0;
        foreach (var model in item.Models)
        {
            bool needed = !native.Contains(model.RaceGender);
            Gui.Icon(needed ? FontAwesomeIcon.Plus : FontAwesomeIcon.Check, needed ? Theme.Accent : Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, needed
                ? $"{model.RaceGender.DisplayName}: model entry added, so this race uses its own model"
                : $"{model.RaceGender.DisplayName}: has a vanilla model, no model entry needed");
        }

        foreach (var (race, claimed) in claims.OrderBy(c => c.Key.RaceCode, StringComparer.Ordinal))
        {
            ushort vanilla = (ushort)(gameData.VanillaEqdp(race, accessory, gear.Item.ModelId) & EqdpInfo.Mask(slot));
            ushort missing = (ushort)(claimed & ~vanilla);
            if (missing != 0) added++;

            if ((claimed & EqdpInfo.Material(slot)) == 0)
                continue;   // the model side is already listed above

            bool hasMaterial = (vanilla & EqdpInfo.Material(slot)) != 0;
            Gui.Icon(hasMaterial ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, hasMaterial ? Theme.Good : Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, hasMaterial
                ? $"{race.DisplayName}: the game already looks its material up here"
                : $"{race.DisplayName}: material entry added, so this item's .mtrl is found");
            Gui.Tooltip($"Materials are written as mt_c{race.RaceCode} materials for this gender, and every race of it " +
                       "that has no material of its own looks here.");
        }

        Gui.Hint(added == 0
            ? $"Nothing to add — the game already covers {SlotInfo.DisplayLabelMap[slot]} set {gear.Item.ModelId}."
            : $"{added} entr{(added == 1 ? "y" : "ies")} for {SlotInfo.DisplayLabelMap[slot]} set {gear.Item.ModelId}.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EQP — what the piece hides or shows
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawEqp(PortItem item, GearSubject gear)
    {
        var slot = gear.Item.Slot;
        if (!EqpInfo.HasEntry(slot))
        {
            Heading("EQP", "equipment parameters", "not used by accessories");
            return;
        }

        ulong vanilla = _host.GameData.VanillaEqp(gear.Item.ModelId);
        ulong current = item.Meta.EqpEntry ?? vanilla;
        var changes = EqpInfo.Differences(slot, vanilla, current);

        Heading("EQP", "what this piece hides or shows",
            changes.Count == 0 ? "as the game has it" : $"{changes.Count} change(s)");
        Gui.Tooltip("These flags let the game hide parts of other gear (a long coat hiding the thighs) or keep " +
                   "them visible. They apply to every item sharing this model id.");

        if (vanilla == 0)
        {
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, "The game has no entry for this set id, so every flag starts off.");
        }

        var flags = EqpInfo.Flags(slot);
        int columns = ImGui.GetContentRegionAvail().X > Theme.S(560) ? 3 : 2;
        if (ImGui.BeginTable("##EqpFlags", columns, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var flag in flags)
            {
                ImGui.TableNextColumn();
                bool on = (current & flag.Bit) != 0;
                bool changed = (vanilla & flag.Bit) != (current & flag.Bit);
                if (changed) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                if (ImGui.Checkbox($"{flag.Label}##eqp{flag.Bit}", ref on))
                {
                    ulong next = on ? current | flag.Bit : current & ~flag.Bit;
                    item.Meta.EqpEntry = next == vanilla ? null : next;
                    _session.MarkDirty();
                }
                if (changed) ImGui.PopStyleColor();
                Gui.Tooltip(changed ? $"{flag.Help}\n\nChanged from the game's own value." : flag.Help);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        using (Gui.Disabled(item.Meta.EqpEntry == null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Undo, "Reset to vanilla",
                    item.Meta.EqpEntry == null ? "Already the game's own entry — nothing is written." : $"Drops: {string.Join(", ", changes)}"))
            {
                item.Meta.EqpEntry = null;
                _session.MarkDirty();
            }
        }
        if (changes.Count > 0)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, string.Join(", ", changes));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EST — which skeleton a hair uses per race
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Filter typed into the "from another hair" picker, shared by the rows (one is open at a time).</summary>
    private string _estFilter = string.Empty;

    private void DrawEst(PortItem item, FeatureSubject hair)
    {
        int set = item.Meta.EstByRace.Count;
        Heading("EST", "extra skeleton table", set == 0 ? "as the game has it" : $"{set} race(s) set");
        Gui.Tooltip("Which skeleton the game loads for this hair id on each race. A hairstyle with its own " +
                   "bones (physics) needs an entry, and a race the game has no entry for needs one to load it at all.");

        if (item.Models.Count == 0)
        {
            Gui.Hint("(add a race model first \u2014 each race gets its own entry)");
            return;
        }

        if (!ImGui.BeginTable("##Est", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("Race",     ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Vanilla",  ImGuiTableColumnFlags.WidthFixed, Theme.S(70));
        ImGui.TableSetupColumn("Skeleton", ImGuiTableColumnFlags.WidthFixed, Theme.S(190));
        ImGui.TableSetupColumn("",         ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        var gameData = _host.GameData;
        ushort? baseSkeleton = hair.BaseRace is { } baseRace ? gameData.VanillaHairSkeleton(baseRace, hair.Id) : null;

        foreach (var model in item.Models.ToList())
        {
            var race = model.RaceGender;
            ImGui.TableNextRow();
            ImGui.PushID(race.RaceCode);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(race.DisplayName);

            var vanilla = gameData.VanillaHairSkeleton(race, hair.Id);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (vanilla is { } v)
                ImGui.TextColored(Theme.Muted, v.ToString("D4"));
            else
                ImGui.TextColored(Theme.Faint, "none");

            // The value shown: this race's override, else its vanilla entry, else the base race's.
            bool overridden = item.Meta.EstByRace.TryGetValue(race.RaceCode, out var stored);
            int value = overridden ? stored : vanilla ?? baseSkeleton ?? 0;

            ImGui.TableNextColumn();
            float pickW = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Copy, "From hair");
            ImGui.SetNextItemWidth(-(pickW + ImGui.GetStyle().ItemSpacing.X));
            if (ImGui.InputInt("##skeleton", ref value, 1, 1))
                SetSkeleton(item, race, value, vanilla);
            Gui.Tooltip("Skeleton id for this race. Matching the game's own value writes nothing.");

            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Copy, "From hair",
                    "Take the skeleton another of this race's hairstyles uses \u2014 the number rarely matches the hair id itself."))
            {
                _estFilter = string.Empty;
                ImGui.OpenPopup("##fromhair");
            }
            DrawFromHairPopup(item, race, hair, vanilla);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            DrawEstStatus(item, race, hair, (ushort)System.Math.Clamp(value, 0, ushort.MaxValue), vanilla, overridden);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void SetSkeleton(PortItem item, RaceGender race, int value, ushort? vanilla)
    {
        ushort clamped = (ushort)System.Math.Clamp(value, 0, ushort.MaxValue);
        if (vanilla == clamped) item.Meta.EstByRace.Remove(race.RaceCode);
        else                    item.Meta.EstByRace[race.RaceCode] = clamped;
        _session.MarkDirty();
    }

    /// <summary>
    /// Every hairstyle of this race that has a skeleton entry, so one can be borrowed by id: the
    /// skeleton number is not the hair id, so picking the hair is the only way to know it.
    /// </summary>
    private void DrawFromHairPopup(PortItem item, RaceGender race, FeatureSubject hair, ushort? vanilla)
    {
        if (!ImGui.BeginPopup("##fromhair"))
            return;

        var entries = _host.GameData.HairSkeletonEntries(race);
        ImGui.TextColored(Theme.Muted, $"{race.DisplayName}: {entries.Count} hairstyle(s) with a skeleton");
        ImGui.SetNextItemWidth(Theme.S(180));
        ImGui.InputTextWithHint("##estsearch", "hair id", ref _estFilter, 8);

        if (ImGui.BeginChild("##estlist", new Vector2(Theme.S(220), Theme.S(240)), false))
        {
            var filter = _estFilter.Trim();
            foreach (var (hairId, skeleton) in entries)
            {
                if (filter.Length > 0 && !hairId.ToString().Contains(filter) && !hairId.ToString("D4").Contains(filter))
                    continue;

                bool self = hairId == hair.Id;
                if (self) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                if (ImGui.Selectable($"Hair {hairId}  \u2192  skeleton {skeleton:D4}{(self ? "  (this one)" : "")}"))
                {
                    SetSkeleton(item, race, skeleton, vanilla);
                    ImGui.CloseCurrentPopup();
                }
                if (self) ImGui.PopStyleColor();
                Gui.Tooltip($"Uses the same skeleton as this race's hair {hairId}. A generated dummy model takes its bones from that hair too.");
            }
        }
        ImGui.EndChild();

        if (entries.Count == 0)
            Gui.HintWrapped("This race has no hairstyle with a skeleton entry, so there is nothing to copy.");
        ImGui.EndPopup();
    }

    /// <summary>The right-hand column: whether this value is written, and whether the game can load it.</summary>
    private void DrawEstStatus(PortItem item, RaceGender race, FeatureSubject hair, ushort skeleton, ushort? vanilla, bool overridden)
    {
        var gameData = _host.GameData;
        if (skeleton == 0)
        {
            ImGui.TextColored(Theme.Faint, "no skeleton");
            return;
        }

        if (!gameData.HasHairSkeleton(race, skeleton))
        {
            ImGui.TextColored(Theme.Warn, $"this race has no skeleton h{skeleton:D4}");
            Gui.Tooltip($"chara/human/c{race.RaceCode}/skeleton/hair/h{skeleton:D4}/ does not exist, so the game cannot load it.");
            return;
        }

        var source = gameData.HairUsingSkeleton(race, skeleton);
        var from = source != null && source != hair.Id ? $" (as hair {source})" : string.Empty;

        // Every race set up here gets an Est manipulation; the accent only marks the ones whose
        // value differs from what the game ships.
        bool differs = vanilla != skeleton;
        ImGui.TextColored(differs ? Theme.Accent : Theme.Muted,
            differs ? $"entry written{from}" : $"entry written, as the game has it{from}");
        Gui.Tooltip(source != null && source != hair.Id
            ? $"Written into the mod as an Est manipulation. A generated dummy model for this race is built on hair {source}'s skeleton."
            : "Written into the mod as an Est manipulation, so the mod says outright which skeleton this hair loads.");
    }

    private static void Heading(string name, string what, string state)
    {
        ImGui.TextColored(Theme.Accent, name);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, what);
        ImGui.SameLine();
        Gui.AlignRight(ImGui.CalcTextSize(state).X);
        ImGui.TextColored(Theme.Faint, state);
    }
}
