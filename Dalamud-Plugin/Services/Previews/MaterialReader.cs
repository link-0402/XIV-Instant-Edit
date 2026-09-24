using Lumina.Data.Files;

namespace InstantEdit.Services.Previews;

/// <summary> One texture slot of a material: its resolved game path and what the shader uses it for. </summary>
public sealed record MaterialTextureSlot(string GamePath, string Usage);

/// <summary> The part of a material a hover card needs. </summary>
public sealed record MaterialPreview(IReadOnlyList<MaterialTextureSlot> Textures);

/// <summary> Reads a material's texture slots without touching the game. </summary>
public static class MaterialReader
{
    public static MaterialPreview Read(byte[] bytes)
    {
        var material = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<MtrlFile>(bytes);
        var usages = MaterialPreviewBundleBuilder.ReadTextureUsages(bytes);
        var slots = new List<MaterialTextureSlot>(material.TextureOffsets.Length);
        for (var i = 0; i < material.TextureOffsets.Length; i++)
        {
            var texture = material.TextureOffsets[i];
            var path = PathRules.Dx11TexturePath(PathRules.ReadNullTerminated(material.Strings, texture.Offset), texture.Flags);
            slots.Add(new MaterialTextureSlot(path, i < usages.Count ? usages[i] : "other"));
        }
        return new MaterialPreview(slots);
    }
}
