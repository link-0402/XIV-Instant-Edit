using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>The materials fitted to a model, and what the message says about them.</summary>
/// <param name="Materials">One per name in <paramref name="ModelNames"/>, in that order.</param>
/// <param name="ModelNames">The model's material each was made for, as the model names it.</param>
/// <param name="WithDiffuse">How many got a diffuse from the stash.</param>
/// <param name="WithTextures">How many got any texture from the stash.</param>
internal sealed record MaterialFitResult(IReadOnlyList<MaterialSetup> Materials, IReadOnlyList<string> ModelNames, int WithDiffuse, int WithTextures);

/// <summary>
/// One material for each material a model names, for a Build item that has none yet: named as
/// + Material names them, with the stash's textures in their slots and placeholders in the rest. Left
/// on Auto, the model's meshes then land on them by position, as after Match vanilla materials.
/// </summary>
internal static class MaterialFit
{
    public const string GearPreset = "Gear";
    public const string GearWithDiffusePreset = "Gear with Diffuse";

    /// <summary>
    /// The materials a model's meshes need, once each in mesh order: <paramref name="meshMaterials"/> is
    /// each mesh's own material name (see <see cref="ModelFileFacts.Materials"/>). Names alike but for case,
    /// the slash or .mtrl are one; shared body groups (see <see cref="ModelMaterialLinks.IsSharedFeature"/>)
    /// are left out, so the positions are the ones Auto links pair by.
    /// </summary>
    public static IReadOnlyList<string> NeededNames(IReadOnlyList<string> meshMaterials)
    {
        var needed = new List<string>();
        foreach (var name in meshMaterials)
            if (!ModelMaterialLinks.IsSharedFeature(name) && ModelMaterialLinks.IndexOfName(needed, name) < 0)
                needed.Add(name);
        return needed;
    }

    /// <summary>
    /// The materials for a model of <paramref name="subject"/>. Gear and weapons get "Gear with Diffuse"
    /// where the stash has a diffuse on disk for the material, else "Gear"; other subjects get
    /// <paramref name="otherPreset"/> (the one the + Material menu suggests), or the gear rule without one.
    /// Each is named after its position and <paramref name="namingRace"/>, its slots are filled from
    /// <paramref name="texturesFor"/> (a model's material name to the stash's textures, see
    /// <see cref="ModelTextureLookup.TexturesFor"/>), and every slot left empty gets its role's
    /// placeholder: a flat normal, a white mask, a white index.
    /// </summary>
    public static MaterialFitResult Fit(IReadOnlyList<string> meshMaterials, Func<string, IReadOnlyDictionary<TexturePurpose, string>> texturesFor,
        PortSubject subject, RaceGender? namingRace, MaterialPreset? otherPreset)
    {
        var names = NeededNames(meshMaterials);
        var materials = new List<MaterialSetup>();
        int withDiffuse = 0, withTextures = 0;
        for (int i = 0; i < names.Count; i++)
        {
            // Only what is there counts, so the preset and the slots agree.
            var found = texturesFor(names[i]).Where(t => File.Exists(t.Value) || Directory.Exists(t.Value)).ToDictionary(t => t.Key, t => t.Value);
            var preset = subject.Kind is not (SubjectKind.Gear or SubjectKind.Weapon) && otherPreset != null
                ? otherPreset
                : Preset(found.ContainsKey(TexturePurpose.Diffuse) ? GearWithDiffusePreset : GearPreset);
            var material = MaterialSetup.FromPreset(preset, subject.DefaultMaterialName(i + 1, namingRace));

            // The preset's slots only: a hair has no use for the stash's diffuse.
            var slots = material.Textures.Select(t => t.Type).ToList();
            var applied = StashApply.Fill(material, StashApply.Plan(found, slots).Where(p => slots.Contains(p.Type)).ToList());
            foreach (var slot in material.Textures.Where(t => string.IsNullOrWhiteSpace(t.SourcePath)))
                slot.UseWhiteDummy = true;

            if (material.Textures.Any(t => t.Type == TextureType.Diffuse && !t.UseWhiteDummy))
                withDiffuse++;
            if (applied.Count > 0)
                withTextures++;
            materials.Add(material);
        }
        return new MaterialFitResult(materials, names, withDiffuse, withTextures);
    }

    private static MaterialPreset Preset(string name) => MaterialPresets.All.First(p => p.Name == name);
}
