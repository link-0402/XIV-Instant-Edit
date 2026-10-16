using System.Collections.Generic;
using System.IO;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>Which texture slots of a material a stash material's textures fill.</summary>
internal static class StashApply
{
    /// <summary>
    /// The slot each texture goes into: diffuse, normal, and a mask into the mask slot (or the
    /// specular slot of a material that has no mask slot). Occlusion and emission have no slot of
    /// their own and are left out. A diffuse that is a folder goes in as a variant folder.
    /// </summary>
    public static IReadOnlyList<(TextureType Type, string Path, bool Folder)> Plan(
        IReadOnlyDictionary<TexturePurpose, string> textures, IReadOnlyCollection<TextureType> materialHas)
    {
        var plan = new List<(TextureType, string, bool)>();
        if (textures.TryGetValue(TexturePurpose.Diffuse, out var diffuse))
            plan.Add((TextureType.Diffuse, diffuse, Directory.Exists(diffuse)));
        if (textures.TryGetValue(TexturePurpose.Normal, out var normal))
            plan.Add((TextureType.Normal, normal, false));
        if (textures.TryGetValue(TexturePurpose.Mask, out var mask))
            plan.Add((Contains(materialHas, TextureType.Mask) || !Contains(materialHas, TextureType.Specular) ? TextureType.Mask : TextureType.Specular, mask, false));
        return plan;
    }

    private static bool Contains(IReadOnlyCollection<TextureType> types, TextureType type)
    {
        foreach (var t in types)
            if (t == type)
                return true;
        return false;
    }
}
