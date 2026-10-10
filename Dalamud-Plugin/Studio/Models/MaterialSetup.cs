using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Models;

/// <summary>
/// A user-defined material for a ported item: which shader it uses, and the
/// list of textures that belong to it.
/// </summary>
[Serializable]
public class MaterialSetup
{
    /// <summary>Display name of the material.</summary>
    public string Name { get; set; } = "Material";

    /// <summary>Shader / material type used by this material.</summary>
    public ShaderType ShaderType { get; set; } = ShaderType.Character;

    /// <summary>Textures assigned to this material, in order.</summary>
    public List<TextureSlot> Textures { get; set; } = new();

    /// <summary>Build mode: where its .mtrl files (and its textures, unless put elsewhere) go in the mod — see <see cref="PackPlace"/>. Null goes with the item.</summary>
    public string? Option { get; set; }

    public MaterialSetup Clone()
    {
        var copy = new MaterialSetup
        {
            Name       = Name,
            ShaderType = ShaderType,
            Option     = Option,
        };
        foreach (var t in Textures)
            copy.Textures.Add(t.Clone());
        return copy;
    }

    /// <summary>
    /// Creates a new material from a preset, with its shader and texture slots
    /// pre-configured and named after the material (e.g. "mt_c0101e0164_top_d").
    /// </summary>
    public static MaterialSetup FromPreset(MaterialPreset preset, string name)
    {
        var material = new MaterialSetup
        {
            Name       = name,
            ShaderType = preset.Shader,
        };
        foreach (var type in preset.Textures)
        {
            material.Textures.Add(new TextureSlot
            {
                Type    = type,
                Postfix = MaterialNaming.DefaultPostfix(type),
            });
        }
        return material;
    }
}

/// <summary>One texture assignment inside a material.</summary>
[Serializable]
public class TextureSlot
{
    /// <summary>What the texture is used for (diffuse, normal, …).</summary>
    public TextureType Type { get; set; } = TextureType.Diffuse;

    /// <summary>
    /// User-editable postfix appended to the material's name to form the texture
    /// name, e.g. material "mt_c0201e0025_top_b" + postfix "normal" →
    /// "mt_c0201e0025_top_b_normal". Combined with the item's material folder to
    /// form the game path.
    /// </summary>
    public string Postfix { get; set; } = string.Empty;

    /// <summary>
    /// Path to a local image file (png/jpeg/dds) that fills this texture slot.
    /// Converted to .tex when the mod is created.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// The old BC7-or-nothing flag. Still saved and loaded so set-ups made before the format was a
    /// choice keep working; <see cref="CompressionMode"/> wins once one has been picked.
    /// </summary>
    public bool CompressBc7 { get; set; }

    /// <summary>The format picked for this slot, or null while it still follows <see cref="CompressBc7"/>.</summary>
    public TextureCompression? CompressionMode { get; set; }

    /// <summary>How this texture is written: what the build compresses it to, if anything.</summary>
    public TextureCompression Compression
    {
        get => CompressionMode ?? (CompressBc7 ? TextureCompression.Bc7 : TextureCompression.None);
        set
        {
            CompressionMode = value;
            CompressBc7     = value == TextureCompression.Bc7;   // keep the old flag readable
        }
    }

    /// <summary>
    /// When true, <see cref="SourcePath"/> points to a folder of image files
    /// instead of a single file. Each image becomes a variant of this texture
    /// slot, switched via a single-select group in the mod.
    /// </summary>
    public bool UseVariants { get; set; }

    /// <summary>
    /// When true, this slot is filled with a placeholder texture instead of a local file (or
    /// variant folder) — a quick way to bind a slot without a source image of your own. Which
    /// placeholder depends on the texture role: a mask can use a generated white square (sized
    /// via <see cref="WhiteDummySize"/>) or a bundled metal texture; a normal map always uses a
    /// bundled flat (no-bump) map, since a white square is the wrong "no bump" colour for one.
    /// See the DummyTextureLibrary service for the available options per role.
    /// </summary>
    public bool UseWhiteDummy { get; set; }

    /// <summary>
    /// Which placeholder fills this slot when <see cref="UseWhiteDummy"/> is set. Empty selects
    /// the role's default (the generated option, where the role has one).
    /// </summary>
    public string DummyPreset { get; set; } = string.Empty;

    /// <summary>Side length in pixels of the generated dummy texture (power of two, 16–4096).</summary>
    public int WhiteDummySize { get; set; } = 32;

    /// <summary>
    /// When set, this slot writes no texture of its own: the material points at this game path,
    /// which another material (usually on another model, e.g. the same hair for a second race)
    /// already writes. Set when a material is pasted with "keep texture paths". The source fields
    /// above are kept, so the slot can be made separate again without re-picking files.
    /// </summary>
    public string SharedGamePath { get; set; } = string.Empty;

    public bool IsShared => !string.IsNullOrEmpty(SharedGamePath);

    /// <summary>Build mode: where the texture goes in the mod — see <see cref="PackPlace"/>. Null goes with its material.</summary>
    public string? Option { get; set; }

    /// <summary>Build mode: the group whose options a folder of variants becomes, one per image (matched by file name).</summary>
    public Guid? VariantGroup { get; set; }

    public TextureSlot Clone() => new()
    {
        Option         = Option,
        VariantGroup   = VariantGroup,
        SharedGamePath = SharedGamePath,
        Type           = Type,
        Postfix        = Postfix,
        SourcePath     = SourcePath,
        CompressBc7     = CompressBc7,
        CompressionMode = CompressionMode,
        UseVariants    = UseVariants,
        UseWhiteDummy  = UseWhiteDummy,
        DummyPreset    = DummyPreset,
        WhiteDummySize = WhiteDummySize,
    };
}
