using System.Collections.Generic;

namespace InstantEdit.Studio.Models;

/// <summary>
/// One entry of the modpack as the editor holds it: what it replaces, the race models, and
/// the materials. Materials are the model's mesh slots, in order — material <c>i</c> is what
/// the model's <c>i</c>-th material slot references, and is written as one .mtrl (one per race
/// for hair) named after the material.
/// </summary>
internal sealed class PortItem
{
    public PortItem(PortSubject subject) => Subject = subject;

    public PortSubject Subject { get; }
    public uint Key => Subject.Key;

    public List<RaceModelEntry> Models    { get; set; } = new();
    public List<MaterialSetup>  Materials { get; set; } = new();

    /// <summary>Penumbra metadata overrides for this item (EQP, EST); empty means "as the game ships it".</summary>
    public ItemMeta Meta { get; set; } = new();

    /// <summary>One mesh slot per material, in order — the saved format and the builder still speak in slots.</summary>
    public List<ModelMaterialSlot> IdentitySlots()
    {
        var slots = new List<ModelMaterialSlot>(Materials.Count);
        for (int i = 0; i < Materials.Count; i++)
            slots.Add(new ModelMaterialSlot { MaterialIndex = i });
        return slots;
    }

    /// <summary>Whether anything would be built for this item.</summary>
    public bool HasWork => Materials.Count > 0 || Models.Exists(m => !string.IsNullOrWhiteSpace(m.SourcePath) || m.UseVariants || m.UseDummy);
}
