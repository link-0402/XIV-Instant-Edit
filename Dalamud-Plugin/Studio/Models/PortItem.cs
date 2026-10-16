using System.Collections.Generic;
using System.Linq;

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

    /// <summary>An animation item's files and races; null for every other kind of item.</summary>
    public AnimationSetup? Animation { get; set; }

    /// <summary>One mesh slot per material, in order — the saved format and the builder still speak in slots.</summary>
    public List<ModelMaterialSlot> IdentitySlots()
    {
        var slots = new List<ModelMaterialSlot>(Materials.Count);
        for (int i = 0; i < Materials.Count; i++)
            slots.Add(new ModelMaterialSlot { MaterialIndex = i });
        return slots;
    }

    /// <summary>One line on what is set up: models and materials (and textures), or an animation's timelines and files.</summary>
    public string Summary(bool withTextures)
    {
        if (Animation is { } animation)
        {
            int files = animation.Timelines.Sum(t => (string.IsNullOrWhiteSpace(t.PapSource) ? 0 : 1) + (string.IsNullOrWhiteSpace(t.TmbSource) ? 0 : 1));
            return $"{animation.Timelines.Count} animation(s) · {files} file(s)";
        }
        var text = $"{Models.Count} {(Subject.HasRaces ? "race model(s)" : "model(s)")} · {Materials.Count} material(s)";
        return withTextures ? $"{text} · {Materials.Sum(m => m.Textures.Count)} texture(s)" : text;
    }

    /// <summary>Whether anything would be built for this item.</summary>
    public bool HasWork => Materials.Count > 0 || Models.Exists(m => !string.IsNullOrWhiteSpace(m.SourcePath) || m.UseVariants || m.UseDummy)
                        || Animation is { HasWork: true };
}
