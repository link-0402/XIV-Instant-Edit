using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Materials.Presets;

/// <summary>Which colour rows a preset changes.</summary>
public enum RowTarget
{
    All,

    /// <summary>The rows the index texture selects (every row when it can't be read).</summary>
    Used,

    /// <summary>The two rows of the pair selected in the window.</summary>
    Selected,

    /// <summary>The top row of the selected pair (2p, index green 255).</summary>
    SelectedTop,

    /// <summary>The bottom row of the selected pair (2p + 1, index green 0).</summary>
    SelectedBottom,
}

/// <summary>What a preset sees: the material, its shader's facts, the index usage, the selected pair and the decoded mask.</summary>
public sealed record PresetContext(MaterialDocument Doc, ShaderPackageFacts Facts, IndexUsage? Usage, int? SelectedPair, DecodedTexture? Mask, string MaterialGamePath)
{
    /// <summary>The dye template Dyeable gives its rows; 0 until the stain templates are read.</summary>
    public int DyeTemplate { get; init; }

    /// <summary>The window paints the preset's <see cref="MaterialEffectPreset.MaskEdit"/> into the mask, so the mask needs no check.</summary>
    public bool AdjustsMask { get; init; }
}

/// <summary>A texture a preset adds, to be written beside the material on preview and save.</summary>
public sealed record PresetTexture(string Label, string GamePath, byte[] Bytes);

/// <summary>A preset's outcome: the new document, what it changed, what else it needs, and textures it brings.</summary>
public sealed record PresetResult(MaterialDocument Doc, IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings, IReadOnlyList<PresetTexture> NewTextures);

/// <summary>A one-click effect: a recipe of shader, keys, values, texture slots, flags and row edits, plus checks.</summary>
public abstract class MaterialEffectPreset
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>The preset edits colour rows, so the window offers a row target.</summary>
    public virtual bool NeedsRows => false;

    /// <summary>What the material needs for the preset, shown when it doesn't apply.</summary>
    public virtual string Requirement => string.Empty;

    /// <summary>What the mask needs where the preset's rows show; the window offers to paint it (Adjust mask).</summary>
    public virtual MaskEdit? MaskEdit => null;

    public abstract bool Applies(PresetContext context);

    public abstract PresetResult Apply(PresetContext context, RowTarget target);
}
