using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>What a gear model's part tags make: an IMC group, or why there is none, and the tags of another slot that would never hide.</summary>
internal sealed record PartTogglePlan(ModGroupOut? Group, string? Problem, IReadOnlyList<string> OtherSlotTags);

/// <summary>
/// The IMC group a build makes for a gear model whose parts are tagged with the slot's variant
/// attributes (atr_tv_a to atr_tv_j on a body, and the like), as the Blender add-on's Attribute Toggle
/// Group does: one option per tag, all on to start, each switching its bit of the IMC attribute mask.
///
/// Penumbra writes the group's entry to every variant and picks it over the mod's own IMC entries,
/// so it has to carry what those say. With materials of its own the build points every variant at
/// v0001, and the group does too; that only works when the variants agree on their decal, effect,
/// animation and sound, so where they differ no group is made. The group is always on, so with
/// materials it would also point the game's own gear at v0001 while the item's option is off: an item
/// in an option gets no group then. Without materials the group changes the mask alone and every
/// variant keeps the game's entry.
/// </summary>
internal static class PartToggleGroups
{
    /// <summary>A variant tag of any slot, _a to _j: the ten bits of an IMC attribute mask.</summary>
    private static readonly Regex VariantTag = new("^atr_(?:mv|tv|gv|dv|sv|ev|nv|wv|rv)_([a-j])$", RegexOptions.CultureInvariant);

    public static PartTogglePlan Plan(string groupName, EquipSlot slot, ushort setId, IEnumerable<string> attributes,
        IReadOnlyList<ModImcOverride>? variants, bool forcesMaterial, int priority, bool inOption = false)
    {
        var prefix = SlotInfo.VariantAttributePrefix(slot);
        var suffixes = new SortedSet<char>();
        var other = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            var match = VariantTag.Match(attribute);
            if (!match.Success)
                continue;
            if (attribute.StartsWith(prefix, StringComparison.Ordinal))
                suffixes.Add(match.Groups[1].Value[0]);
            else
                other.Add(attribute);
        }

        if (suffixes.Count == 0)
            return new PartTogglePlan(null, null, other.ToList());
        if (variants is not { Count: > 0 })
            return new PartTogglePlan(null, "the game has no IMC file for this item, so its parts can't be toggled", other.ToList());

        if (forcesMaterial && inOption)
            return new PartTogglePlan(null,
                "the item is in an option, and an always-on toggle group would give the game's own gear its materials. Set up the part toggles in Penumbra instead",
                other.ToList());
        var entry = variants[0];
        if (forcesMaterial && variants.Any(v => v.DecalId != entry.DecalId || v.VfxId != entry.VfxId
                || v.MaterialAnimationId != entry.MaterialAnimationId || v.SoundId != entry.SoundId))
            return new PartTogglePlan(null,
                "its variants differ in decals, effects or sounds, and a toggle group would undo that. Set up the part toggles in Penumbra instead",
                other.ToList());

        var key = $"parts|{SlotInfo.KeyMap[slot]}|{setId}";
        var group = new ModGroupOut
        {
            Id              = StableId(key),
            Name            = groupName,
            Description     = $"Shows or hides the parts tagged {prefix}a to {prefix}j.",
            Priority        = priority,
            DefaultSettings = (1L << suffixes.Count) - 1,
            Imc             = new ModImcGroupOut { Slot = slot, SetId = setId, Entry = entry, OnlyAttributes = !forcesMaterial },
        };
        foreach (var suffix in suffixes)
            group.Options.Add(new ModOptionOut
            {
                Id            = StableId($"{key}|{suffix}"),
                Name          = char.ToUpperInvariant(suffix).ToString(),
                Description   = $"Parts tagged {prefix}{suffix}.",
                AttributeMask = (ushort)(1 << (suffix - 'a')),
            });
        return new PartTogglePlan(group, null, other.ToList());
    }

    /// <summary>The tag a part of another slot's family should have on this slot: atr_tv_b on a necklace is atr_nv_b.</summary>
    public static string Renamed(EquipSlot slot, string tag) => $"{SlotInfo.VariantAttributePrefix(slot)}{tag[^1]}";

    /// <summary>The same id for the same item every build, so Penumbra keeps what the player picked when the mod is built again.</summary>
    private static Guid StableId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
