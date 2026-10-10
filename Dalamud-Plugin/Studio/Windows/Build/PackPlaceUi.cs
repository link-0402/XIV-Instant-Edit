using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>The pickers Build mode uses to say where something goes in the mod: a place, or the group a slot of variants fills.</summary>
internal static class PackPlaceUi
{
    /// <summary>
    /// A combo of every place a file can go — the default files, then each group's options — and,
    /// when <paramref name="inherited"/> is given, going with what it belongs to (null). Returns the
    /// new choice when one was made.
    /// </summary>
    public static bool Combo(string id, PortSession session, string? place, string? inherited, out string? picked, float width = -1)
    {
        picked = place;
        bool changed = false;
        var preview = place == null && inherited != null ? inherited : session.Label(place);
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo(id, preview, ImGuiComboFlags.HeightLarge))
            return false;

        if (inherited != null && ImGui.Selectable(inherited, place == null))
        {
            picked = null;
            changed = true;
        }
        if (ImGui.Selectable("Default files", place == string.Empty))
        {
            picked = PackPlace.Default;
            changed = true;
        }
        foreach (var group in session.Groups)
        {
            if (group.Options.Count == 0)
                continue;
            ImGui.TextColored(Theme.Muted, group.Name);
            foreach (var option in group.Options)
            {
                var value = PackPlace.Of(group.Id, option.Id);
                if (ImGui.Selectable($"    {option.Name}##{value}", place == value))
                {
                    picked = value;
                    changed = true;
                }
            }
        }
        if (session.Groups.Count == 0)
            ImGui.TextColored(Theme.Faint, "No groups yet — make one in the left pane (By option).");
        ImGui.EndCombo();
        return changed;
    }

    /// <summary>A labelled place picker on one line of an inspector.</summary>
    public static void Row(PortSession session, string id, string? place, string? inherited, Action<string?> set, string help)
    {
        Gui.Label("Goes into", help);
        if (Combo(id, session, place, inherited, out var picked))
            set(picked);
    }

    /// <summary>The group a slot of variants fills: one of the pack's groups, or one of its own (made for it).</summary>
    public static void VariantGroupRow(PortSession session, string id, PackGroup? current, Action<Guid?> set)
    {
        Gui.Label("Options of", "Each variant file becomes the option of this group made for its name. Several slots of variants can fill one group: files of the same name then go into the same option.");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(id, current != null ? $"{current.Name}  ({current.Options.Count} options)" : "a group of its own"))
        {
            if (ImGui.Selectable("A group of its own (made for it)", false))
                set(null);
            foreach (var group in session.Groups)
                if (ImGui.Selectable($"{group.Name}  ({group.Options.Count} options)##{group.Id}", group == current))
                    set(group.Id);
            ImGui.EndCombo();
        }
        if (current != null)
        {
            Gui.Label(string.Empty);
            if (ImGui.SmallButton($"Open {current.Name}##{id}open"))
                session.Focus(Target.ForPackGroup(current.Id));
            Gui.Tooltip("Rename it and its options, change how they are picked, or put them in another order.");
        }
    }

    /// <summary>A badge for a tile placed somewhere other than what it belongs to (or anywhere but the default files, for an item).</summary>
    public static Badge? PlaceBadge(PortSession session, string place, string parentPlace)
        => place == parentPlace ? null : new Badge(session.Find(place)?.Option.Name ?? "default files", Theme.Accent, $"Goes into {session.Label(place)}.");

    public static List<Badge> With(this List<Badge> badges, Badge? badge)
    {
        if (badge is { } b)
            badges.Add(b);
        return badges;
    }
}
