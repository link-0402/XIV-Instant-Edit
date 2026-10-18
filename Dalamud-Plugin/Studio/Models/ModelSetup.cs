using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// A model configured for one race/gender of the selected item. <see cref="SourcePath"/>
/// points to a local .mdl file that gets copied into the mod at that race's game path.
/// Races without a native model entry by default need an Eqdp meta manipulation so
/// Penumbra tells the game to use this race's own model instead of falling back to
/// another race's file — see <see cref="ModEqdpOverride"/>.
/// </summary>
[Serializable]
public class RaceModelEntry
{
    public PlayerRace   Race       { get; set; } = PlayerRace.Midlander;
    public PlayerGender Gender     { get; set; } = PlayerGender.Male;

    /// <summary>Local .mdl file copied into the mod for this race. Empty until set up.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="SourcePath"/> is a plain copy of the vanilla model for this
    /// race, dropped in as a starting point for the user to edit externally (e.g. in
    /// Blender via InstantEdit) rather than an already-finished custom model.
    /// </summary>
    public bool IsVanillaDummy { get; set; }

    /// <summary>
    /// When true, this race's model is picked from <see cref="VariantPaths"/> instead of
    /// <see cref="SourcePath"/> — each file becomes one option of a switch group in the
    /// mod, the same way <see cref="TextureSlot.UseVariants"/> does for textures.
    /// </summary>
    public bool UseVariants { get; set; }

    /// <summary>
    /// When true, the build generates this race's model instead of copying one in: the race's
    /// vanilla skeleton with one empty mesh part per configured material, each already pointing at
    /// that material. A starting point to model over in Blender, and enough for a port that only
    /// replaces materials and textures.
    /// </summary>
    public bool UseDummy { get; set; }

    /// <summary>
    /// Local .mdl files that become variant options when <see cref="UseVariants"/> is set.
    /// Explicitly picked files rather than a folder scan, since model exports don't
    /// usually live one-per-folder the way texture variant sets do.
    /// </summary>
    public List<string> VariantPaths { get; set; } = new();

    /// <summary>
    /// Which of the item's materials each mesh of <see cref="SourcePath"/> is switched to, one entry
    /// per mesh of that file's first LOD — see <see cref="ModelMaterialLinks"/>. Meshes past the end of
    /// the list (including the whole list of a model nobody opened) read as
    /// <see cref="ModelMaterialLinks.Auto"/>.
    /// </summary>
    public List<int> MaterialLinks { get; set; } = new();

    /// <summary>
    /// The same, one list per file in <see cref="VariantPaths"/> and in the same order: every variant
    /// starts on the automatic match, which is the set-up its neighbours get, and can then be
    /// changed on its own.
    /// </summary>
    public List<List<int>> VariantMaterialLinks { get; set; } = new();

    /// <summary>
    /// The mesh parts set to a material of another item in the pack: their entry in
    /// <see cref="MaterialLinks"/> (or a variant's list) is <see cref="ModelMaterialLinks.Shared"/>,
    /// and the material it means is here.
    /// </summary>
    public List<SharedMaterialLink> SharedLinks { get; set; } = new();

    public RaceGender RaceGender => new(Race, Gender);

    /// <summary>A variant's material links, as stored — null when that variant has never been changed.</summary>
    public IReadOnlyList<int>? VariantLinks(int index)
        => index >= 0 && index < VariantMaterialLinks.Count ? VariantMaterialLinks[index] : null;

    /// <summary>A variant's material links to write into, creating the lists up to it.</summary>
    public List<int> EditVariantLinks(int index)
    {
        while (VariantMaterialLinks.Count <= index)
            VariantMaterialLinks.Add(new List<int>());
        return VariantMaterialLinks[index];
    }

    /// <summary>The links of the model's own file (<paramref name="variant"/> -1) or of one variant file.</summary>
    public IReadOnlyList<int>? LinksOf(int variant) => variant < 0 ? MaterialLinks : VariantLinks(variant);

    /// <summary>The material of another item a mesh part is set to, or null.</summary>
    public SharedMaterialLink? SharedLink(int variant, int slot)
        => SharedLinks.Find(l => l.Variant == variant && l.Slot == slot);

    /// <summary>Sets a mesh part to <see cref="ModelMaterialLinks.Auto"/>, <see cref="ModelMaterialLinks.Keep"/> or one of the item's own materials.</summary>
    public void SetLink(int variant, int slot, int value)
    {
        ModelMaterialLinks.Store(variant < 0 ? MaterialLinks : EditVariantLinks(variant), slot, value);
        SharedLinks.RemoveAll(l => l.Variant == variant && l.Slot == slot);
    }

    /// <summary>Sets a mesh part to a material of another item: the build writes a copy of it for this item.</summary>
    public void SetSharedLink(int variant, int slot, uint item, Guid material)
    {
        SetLink(variant, slot, ModelMaterialLinks.Shared);
        SharedLinks.Add(new SharedMaterialLink { Variant = variant, Slot = slot, Item = item, Material = material });
    }

    /// <summary>Drops a variant's links along with the variant itself, so the rest stay on their own files.</summary>
    public void RemoveVariant(int index)
    {
        if (index >= 0 && index < VariantPaths.Count)
            VariantPaths.RemoveAt(index);
        if (index >= 0 && index < VariantMaterialLinks.Count)
            VariantMaterialLinks.RemoveAt(index);
        SharedLinks.RemoveAll(l => l.Variant == index);
        foreach (var link in SharedLinks)
            if (link.Variant > index)
                link.Variant--;
    }

    /// <summary>Forgets every variant file's links, when the list of files is replaced.</summary>
    public void ClearVariantLinks()
    {
        VariantMaterialLinks.Clear();
        SharedLinks.RemoveAll(l => l.Variant >= 0);
    }

    /// <summary>Where this race's model goes in the mod — see <see cref="PackPlace"/>. Null goes with the item.</summary>
    public string? Option { get; set; }

    /// <summary>The group whose options the variant files become, one per file (matched by file name).</summary>
    public Guid? VariantGroup { get; set; }

    public RaceModelEntry Clone() => new()
    {
        Option               = Option,
        VariantGroup         = VariantGroup,
        Race                 = Race,
        Gender               = Gender,
        SourcePath           = SourcePath,
        IsVanillaDummy       = IsVanillaDummy,
        UseVariants          = UseVariants,
        UseDummy             = UseDummy,
        VariantPaths         = VariantPaths.ToList(),
        MaterialLinks        = MaterialLinks.ToList(),
        VariantMaterialLinks = VariantMaterialLinks.Select(l => l.ToList()).ToList(),
        SharedLinks          = SharedLinks.Select(l => l.Clone()).ToList(),
    };
}

/// <summary>
/// A mesh part of a race model set to a material of another item in the pack — a top's material on
/// the matching bottoms, say. The build writes a copy of that material for this item, named for it
/// and in its own folder, that points at the original's textures.
/// </summary>
[Serializable]
public class SharedMaterialLink
{
    /// <summary>The variant file the mesh part belongs to, or -1 for the model's own file.</summary>
    public int Variant { get; set; } = -1;

    /// <summary>The mesh's index in that file (see <see cref="RaceModelEntry.MaterialLinks"/>).</summary>
    public int Slot { get; set; }

    /// <summary>The item the material belongs to (its key).</summary>
    public uint Item { get; set; }

    /// <summary>The material's <see cref="MaterialSetup.Id"/>.</summary>
    public Guid Material { get; set; }

    public SharedMaterialLink Clone() => new() { Variant = Variant, Slot = Slot, Item = Item, Material = Material };
}

/// <summary>
/// One material slot of the dummy model: which of the item's configured Materials
/// (by index into the Materials list) fills it, and an optional override for the
/// slot's on-disk material name (empty = use the assigned Material's own Name).
/// There is always exactly one slot per configured Material; slots exist so the
/// user can reassign/rename without reordering the Materials list itself.
/// </summary>
[Serializable]
public class ModelMaterialSlot
{
    public int    MaterialIndex { get; set; }
    public string NameOverride  { get; set; } = string.Empty;

    public ModelMaterialSlot Clone() => new() { MaterialIndex = MaterialIndex, NameOverride = NameOverride };
}
