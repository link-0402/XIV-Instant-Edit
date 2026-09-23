namespace InstantEdit.Ui;

/// <summary> The editable resource kinds a resource row represents. </summary>
[Flags]
internal enum ResourceKinds : byte
{
    None = 0,
    Model = 1,
    Texture = 2,
    Material = 4,
    Editable = Model | Texture | Material,
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
        return kinds;

        bool HasExtension(string extension)
            => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
               actualPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> The kinds a resource-type filter admits, or null when it admits every resource. </summary>
    public static ResourceKinds? ForFilter(string? filter)
        => string.IsNullOrWhiteSpace(filter)
            // The default "Tree Structure" view (no explicit filter) still omits
            // resources IE cannot edit (animations, skeletons, VFX, etc.) rather
            // than showing every resource Penumbra reports.
            ? ResourceKinds.Editable
            : filter switch
            {
                "Models" => ResourceKinds.Model,
                "Textures" => ResourceKinds.Texture,
                "Materials" => ResourceKinds.Material,
                _ => null,
            };
}
