using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// Converts set-ups saved when mesh slots were a separate layer (a slot could reuse another
/// slot's material, or rename its .mtrl) into the one-material-per-slot form the editor uses now.
/// </summary>
internal static class MeshSlotFolding
{
    /// <summary>
    /// Returns one material per slot, in slot order, named after the slot's effective name (its
    /// override, or its material's own name), so every .mtrl keeps its game path. Materials no slot
    /// used are appended after them, so nothing is lost. When the slots already are one per
    /// material in order with no overrides, <paramref name="materials"/> comes back unchanged.
    /// </summary>
    /// <param name="unused">Names of the appended materials — they now write a .mtrl, which they did not before.</param>
    public static List<MaterialSetup> Fold(List<MaterialSetup> materials, List<ModelMaterialSlot> slots, out List<string> unused)
    {
        unused = new List<string>();
        if (materials.Count == 0)
            return materials;

        // The old editor always kept exactly one slot per material, padding with identity slots
        // and dropping extras; apply the same rule first so a short or missing list means the same.
        slots = slots.Take(materials.Count).ToList();
        while (slots.Count < materials.Count)
            slots.Add(new ModelMaterialSlot { MaterialIndex = slots.Count });

        if (IsIdentity(materials, slots))
            return materials;

        var folded = new List<MaterialSetup>(slots.Count);
        var used = new HashSet<int>();
        foreach (var slot in slots)
        {
            int idx = Math.Clamp(slot.MaterialIndex, 0, materials.Count - 1);
            used.Add(idx);
            var copy = materials[idx].Clone();
            if (!string.IsNullOrWhiteSpace(slot.NameOverride))
                copy.Name = slot.NameOverride;
            folded.Add(copy);
        }

        for (int i = 0; i < materials.Count; i++)
        {
            if (used.Contains(i)) continue;
            folded.Add(materials[i].Clone());
            unused.Add(materials[i].Name);
        }

        return folded;
    }

    private static bool IsIdentity(List<MaterialSetup> materials, List<ModelMaterialSlot> slots)
        => slots.Count == materials.Count
        && slots.Select((s, i) => s.MaterialIndex == i && string.IsNullOrWhiteSpace(s.NameOverride)).All(x => x);
}
