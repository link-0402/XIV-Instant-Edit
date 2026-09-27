namespace InstantEdit.Ui;

/// <summary> The editable resource kinds a resource row represents. </summary>
[Flags]
internal enum ResourceKinds : byte
{
    None = 0,
    Model = 1,
    Texture = 2,
    Material = 4,
    /// <summary> A character animation pack (.pap), which can be sent to Blender. </summary>
    Animation = 8,
    Editable = Model | Texture | Material | Animation,
}

/// <summary> The kinds a resource browser lists, and the chips that filter them. </summary>
internal sealed class ResourceKindChipSet
{
    private ResourceKindChipSet(ResourceKinds listed, params (ResourceKinds Kind, string Label)[] chips)
    {
        Listed = listed;
        Chips = chips;
        Counts = [listed, .. chips.Select(chip => chip.Kind)];
    }

    /// <summary> On Screen: models, textures and materials. Animations your character plays have their own tab. </summary>
    public static ResourceKindChipSet OnScreen { get; } = new(ResourceKinds.Model | ResourceKinds.Texture | ResourceKinds.Material,
        (ResourceKinds.Model, "Models"), (ResourceKinds.Texture, "Textures"), (ResourceKinds.Material, "Materials"));

    /// <summary> The Mod Browser also lists a mod's animation files, which can be sent to Blender. </summary>
    public static ResourceKindChipSet ModBrowser { get; } = new(ResourceKinds.Editable,
        (ResourceKinds.Model, "Models"), (ResourceKinds.Texture, "Textures"), (ResourceKinds.Material, "Materials"),
        (ResourceKinds.Animation, "Animations"));

    /// <summary> Every kind the browser lists: what its "All" chip shows. </summary>
    public ResourceKinds Listed { get; }

    public IReadOnlyList<(ResourceKinds Kind, string Label)> Chips { get; }

    /// <summary> What each chip counts; index 0 is the "All" chip, which counts every listed row. </summary>
    public IReadOnlyList<ResourceKinds> Counts { get; }
}

/// <summary> What a texture is used for; the browser colours texture rows by it. </summary>
internal enum TextureRole : byte
{
    /// <summary> Not a texture. </summary>
    None,
    /// <summary> Base colour (diffuse). </summary>
    Base,
    Normal,
    Mask,
    Index,
    /// <summary> Any other texture: specular, catchlight, sphere maps, decals, flow maps, VFX. </summary>
    Other,
}

/// <summary> Classifies texture rows by role. Like the kinds, a row's role is classified once per view. </summary>
internal static class TextureRoleClassifier
{
    private const string SamplerPrefix = "g_Sampler";

    /// <summary>
    /// The role of a texture row. Penumbra names a material's texture nodes after the shader
    /// sampler they are bound to (g_SamplerNormal, …), which is authoritative; Mod Browser rows
    /// and samplers whose names Penumbra could not read fall back to the game's file-name suffixes.
    /// </summary>
    public static TextureRole Classify(ResourceKinds kinds, string? name, string? gamePath, string? actualPath)
        => (kinds & ResourceKinds.Texture) == 0
            ? TextureRole.None
            : FromSamplerName(name) ?? FromFileName(string.IsNullOrEmpty(gamePath) ? actualPath : gamePath);

    /// <summary> The role a shader sampler name implies, or null when the name is not a sampler name. </summary>
    public static TextureRole? FromSamplerName(string? name)
    {
        if (name is null || !name.StartsWith(SamplerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var sampler = name[SamplerPrefix.Length..];
        // Checked in this order so compound names such as TileNormal or WrinklesMask keep their role.
        if (Has("Normal")) return TextureRole.Normal;
        if (Has("Mask")) return TextureRole.Mask;
        if (Has("Index")) return TextureRole.Index;
        if (Has("Diffuse") || Has("Base")) return TextureRole.Base;
        return TextureRole.Other;

        bool Has(string part) => sampler.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> The role the game's texture naming implies: _n/_norm, _m/_mask, _id and _d/_base. </summary>
    public static TextureRole FromFileName(string? path)
    {
        var stem = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        var separator = stem.LastIndexOf('_');
        return (separator < 0 ? string.Empty : stem[(separator + 1)..].ToLowerInvariant()) switch
        {
            "n" or "norm" => TextureRole.Normal,
            "m" or "mask" => TextureRole.Mask,
            "id" => TextureRole.Index,
            "d" or "base" => TextureRole.Base,
            _ => TextureRole.Other,
        };
    }

    /// <summary> The role of a material preview's texture usage ("diffuse", "normal", …). </summary>
    public static TextureRole FromUsage(string? usage)
        => usage switch
        {
            "diffuse" => TextureRole.Base,
            "normal" => TextureRole.Normal,
            "mask" => TextureRole.Mask,
            "index" => TextureRole.Index,
            _ => TextureRole.Other,
        };

    public static string Label(TextureRole role)
        => role switch
        {
            TextureRole.Base => "Base",
            TextureRole.Normal => "Normal",
            TextureRole.Mask => "Mask",
            TextureRole.Index => "Index",
            _ => "Other",
        };
}

/// <summary>
/// Classifies resource rows for the On Screen and Mod Browser type filters. Rows are
/// classified once when their view is built, so drawing only compares flags.
/// </summary>
internal static class ResourceKindClassifier
{
    public static ResourceKinds Classify(string? type, string? gamePath, string? actualPath)
    {
        type ??= string.Empty;
        gamePath ??= string.Empty;
        actualPath ??= string.Empty;
        var kinds = ResourceKinds.None;
        if (type.Contains("model", StringComparison.OrdinalIgnoreCase) || HasExtension(".mdl"))
            kinds |= ResourceKinds.Model;
        if (type.Contains("texture", StringComparison.OrdinalIgnoreCase) || HasExtension(".tex") || HasExtension(".atex"))
            kinds |= ResourceKinds.Texture;
        if (type.Contains("material", StringComparison.OrdinalIgnoreCase) || HasExtension(".mtrl"))
            kinds |= ResourceKinds.Material;
        // Only character animation packs: Penumbra's tree also reports material animations
        // (.pap files that animate gear materials), which have no skeleton to send.
        if (IsCharacterAnimation(gamePath))
            kinds |= ResourceKinds.Animation;
        return kinds;

        bool HasExtension(string extension)
            => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
               actualPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a game path is a character animation pack (<c>chara/human/cNNNN/animation/…/*.pap</c>),
    /// whose clips can be sampled on a character skeleton and sent to Blender.
    /// </summary>
    public static bool IsCharacterAnimation(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = gamePath.Replace('\\', '/').Split('/');
        return parts.Length > 5 &&
               parts[0].Equals("chara", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
               parts[2].Length == 5 && parts[2][0] is 'c' or 'C' && parts[2].Skip(1).All(char.IsAsciiDigit) &&
               parts[3].Equals("animation", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> The kinds a resource-type filter admits, or null when it admits every resource. </summary>
    public static ResourceKinds? ForFilter(string? filter)
        => string.IsNullOrWhiteSpace(filter)
            // The default "Tree Structure" view (no explicit filter) still omits
            // resources IE cannot use (skeletons, VFX, material animations, etc.)
            // rather than showing every resource Penumbra reports.
            ? ResourceKinds.Editable
            : filter switch
            {
                "Models" => ResourceKinds.Model,
                "Textures" => ResourceKinds.Texture,
                "Materials" => ResourceKinds.Material,
                "Animations" => ResourceKinds.Animation,
                _ => null,
            };
}
