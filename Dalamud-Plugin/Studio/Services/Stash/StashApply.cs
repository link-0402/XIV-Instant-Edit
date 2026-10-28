using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>Which texture slots of a material a stash material's textures fill.</summary>
internal static class StashApply
{
    /// <summary>
    /// The slot each texture goes into: diffuse, normal, and a mask into the mask slot (or the
    /// specular slot of a material that has no mask slot). Occlusion, emission and matcaps have no
    /// slot of their own and are left out. A diffuse that is a folder goes in as a variant folder.
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

    /// <summary>
    /// Points the material's texture slots at the plan's files, adding slots it does not have yet. A
    /// folder goes in as a variant folder; a file or folder that isn't there is skipped. Returns what
    /// went in, for a message ("diffuse", "normal (3 variant(s))").
    /// </summary>
    public static List<string> Fill(MaterialSetup material, IReadOnlyList<(TextureType Type, string Path, bool Folder)> plan)
    {
        var applied = new List<string>();
        foreach (var (type, path, folder) in plan)
        {
            if (folder ? !Directory.Exists(path) : !File.Exists(path))
                continue;
            var slot = GetOrAddTextureSlot(material, type);
            slot.SourcePath    = path;
            slot.UseVariants   = folder;
            slot.UseWhiteDummy = false;
            var label = TextureTypeInfo.Labels[(int)type].ToLowerInvariant();
            applied.Add(folder ? $"{label} ({StashFolders.CountImages(path)} variant(s))" : label);
        }
        return applied;
    }

    /// <summary>Finds the material's slot for a texture role, adding one when it has none.</summary>
    private static TextureSlot GetOrAddTextureSlot(MaterialSetup mat, TextureType type)
    {
        var existing = mat.Textures.FirstOrDefault(t => t.Type == type);
        if (existing != null)
            return existing;

        var slot = new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type), Compression = TextureCompression.Auto };
        mat.Textures.Add(slot);
        return slot;
    }

    private static bool Contains(IReadOnlyCollection<TextureType> types, TextureType type)
    {
        foreach (var t in types)
            if (t == type)
                return true;
        return false;
    }
}
