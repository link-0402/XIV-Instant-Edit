using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;

namespace InstantEdit.Services.TextureCompression;

/// <summary> How one material reads a texture: the sampler, the shader package and the material's transparency. </summary>
internal sealed record TextureUse
{
    public const uint NormalSampler = 0x0C5EC1F1;
    public const uint MaskSampler = 0x8A4E82B6;
    public const uint IndexSampler = 0x565F8FD8;
    public const uint DiffuseSampler = 0x115306BE;
    /// <summary> g_AlphaThreshold: a cut-out keeps pixels whose opacity reaches it. </summary>
    public const uint AlphaThresholdConstant = 0x29AC0223;
    /// <summary> The material flag that blends transparency instead of cutting it out. </summary>
    public const uint TranslucencyFlag = 0x10;

    /// <summary> The sampler's id, or 0 when only Penumbra's name or the file name gave the role. </summary>
    public uint SamplerId { get; init; }
    public required TextureRole Role { get; init; }
    /// <summary> "character.shpk"; empty when the material couldn't be read. </summary>
    public string ShaderPackage { get; init; } = string.Empty;
    /// <summary> The material's game path, for messages. </summary>
    public string Material { get; init; } = string.Empty;
    public float AlphaThreshold { get; init; }
    public bool Translucent { get; init; }

    /// <summary>
    /// The channel the shader reads opacity from: blue in the normal maps of the gear shaders
    /// (character, characterlegacy, glass, stockings and the other character* packages but tattoo),
    /// alpha in hair normal maps. -1 when this use reads no opacity.
    /// </summary>
    public int OpacityChannel
    {
        get
        {
            if (Role != TextureRole.Normal)
                return -1;
            if (ShaderPackage.Equals("hair.shpk", StringComparison.OrdinalIgnoreCase))
                return 3;
            return ShaderPackage.StartsWith("character", StringComparison.OrdinalIgnoreCase) &&
                   !ShaderPackage.Equals("charactertattoo.shpk", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        }
    }

    /// <summary> Pixels below the threshold aren't drawn at all: an opacity channel read by a material that cuts out. </summary>
    public bool CutsOut => OpacityChannel >= 0 && !Translucent && AlphaThreshold > 0;
}

/// <summary> A mod texture the character renders, with every material that reads it. </summary>
internal sealed record CompressionCandidate
{
    /// <summary> The mod file, as a full path. </summary>
    public required string File { get; init; }
    /// <summary> The file's mod; its hash is filled in when the file is read. </summary>
    public required PreviewSource Source { get; init; }
    public required IReadOnlyList<string> GamePaths { get; init; }
    public required IReadOnlyList<TextureUse> Uses { get; init; }
    /// <summary> The file belongs to a preview mod of another Quick Action, which is left alone. </summary>
    public bool InPreview { get; init; }

    public string Label => Source.Label;
    public string FileName => Path.GetFileName(Source.RelativePath.Length > 0 ? Source.RelativePath : File);
}

/// <summary>
/// Collects the textures a character renders from mods from its resource tree, each file once
/// however many materials use it. A texture's uses come from its material: the samplers that read
/// its path decide its role, and the material's shader package, alpha threshold and translucency
/// flag decide whether a channel holds a cut-out. Dalamud-free; reading materials is delegated.
/// </summary>
internal static class CompressionCapture
{
    /// <summary> Textures with fewer pixels than this (128 × 128) weigh too little to be worth a conversion. </summary>
    public const int MinPixels = 128 * 128;

    /// <summary>
    /// Uncompressed 2D color textures that block compression fits: whole 4 × 4 tiles, not tiny, and
    /// no larger than the plugin reads. Formats already compressed, and single-channel or float
    /// ones, stay as they are.
    /// </summary>
    public static bool Compressible(TexInfo info)
        => info.Is2D && TextureCost.IsCompressibleColour(info.Format) && info.Width % 4 == 0 && info.Height % 4 == 0 &&
           (long)info.Width * info.Height >= MinPixels && Math.Max(info.Width, info.Height) <= TextureFiles.MaxDimension;

    /// <param name="readMaterial">Reads a material node's file (a mod file or game data); null when it can't.</param>
    /// <param name="isPreviewMod">Whether a mod folder holds a preview mod.</param>
    public static IReadOnlyList<CompressionCandidate> Collect(IReadOnlyList<ResourceNode> roots, Func<ResourceNode, SkinMaterial?> readMaterial,
        Func<string, bool> isPreviewMod)
    {
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, SkinMaterial?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            Visit(root, null, groups, materials, readMaterial);
        return groups.Values.Select(group => new CompressionCandidate
        {
            File = group.File,
            Source = PreviewSource.Of(group.First, string.Empty),
            GamePaths = group.GamePaths,
            Uses = group.Uses,
            InPreview = isPreviewMod(group.First.SourceModDirectory ?? string.Empty),
        }).OrderBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary> Every mod file in the tree, as full paths: the models, materials and textures a character has loaded from mods. </summary>
    public static IReadOnlySet<string> ModFiles(IReadOnlyList<ResourceNode> roots)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ResourceNode>(roots);
        while (pending.TryPop(out var node))
        {
            if (node.SourceState == ResourceSourceState.LoadedMod && FullPath(node.ActualPath) is { } file)
                files.Add(file);
            foreach (var child in node.Children)
                pending.Push(child);
        }
        return files;
    }

    private sealed class Group(ResourceNode first, string file)
    {
        public ResourceNode First { get; } = first;
        public string File { get; } = file;
        public List<string> GamePaths { get; } = [];
        public List<TextureUse> Uses { get; } = [];
    }

    private static void Visit(ResourceNode node, ResourceNode? material, Dictionary<string, Group> groups, Dictionary<string, SkinMaterial?> materials,
        Func<ResourceNode, SkinMaterial?> readMaterial)
    {
        if (IsKind(node, ".tex") && node.SourceState == ResourceSourceState.LoadedMod && FullPath(node.ActualPath) is { } file)
        {
            if (!groups.TryGetValue(file, out var group))
                groups[file] = group = new Group(node, file);
            var gamePath = PathRules.NormalizeGamePath(node.GamePath);
            if (gamePath.Length > 0 && !group.GamePaths.Contains(gamePath, StringComparer.OrdinalIgnoreCase))
                group.GamePaths.Add(gamePath);
            foreach (var use in UsesOf(node, gamePath, material, materials, readMaterial))
                if (!group.Uses.Contains(use))
                    group.Uses.Add(use);
        }
        var childMaterial = IsKind(node, ".mtrl") ? node : material;
        foreach (var child in node.Children)
            Visit(child, childMaterial, groups, materials, readMaterial);
    }

    /// <summary>
    /// The material's samplers that read the texture's path. When the material can't be read, or
    /// no sampler names the path, Penumbra's sampler name (or else the file name) gives the role.
    /// </summary>
    private static IEnumerable<TextureUse> UsesOf(ResourceNode texture, string gamePath, ResourceNode? materialNode,
        Dictionary<string, SkinMaterial?> materials, Func<ResourceNode, SkinMaterial?> readMaterial)
    {
        SkinMaterial? material = null;
        if (materialNode is not null)
        {
            var key = materialNode.SourceState + "\n" + materialNode.ActualPath;
            if (!materials.TryGetValue(key, out material))
                materials[key] = material = readMaterial(materialNode);
        }
        var materialPath = materialNode is null ? string.Empty : PathRules.NormalizeGamePath(materialNode.GamePath);
        var threshold = material is not null && material.HasConstant(TextureUse.AlphaThresholdConstant) && material.Constant(TextureUse.AlphaThresholdConstant) is { Length: > 0 } values
            ? values[0]
            : 0f;
        var translucent = material is not null && (material.MaterialFlags & TextureUse.TranslucencyFlag) != 0;
        var found = false;
        if (material is not null)
            foreach (var sampler in material.Samplers.Keys)
            {
                if (!string.Equals(material.RequestedTextureFor(sampler), gamePath, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(material.TextureFor(sampler), gamePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                found = true;
                yield return new TextureUse
                {
                    SamplerId = sampler, Role = RoleOf(sampler), ShaderPackage = material.ShaderPackage, Material = materialPath,
                    AlphaThreshold = threshold, Translucent = translucent,
                };
            }
        if (!found)
            yield return new TextureUse
            {
                Role = TextureRoleClassifier.Classify(ResourceKinds.Texture, texture.Name, texture.GamePath, texture.ActualPath),
                ShaderPackage = material?.ShaderPackage ?? string.Empty, Material = materialPath, AlphaThreshold = threshold, Translucent = translucent,
            };
    }

    private static TextureRole RoleOf(uint sampler) => sampler switch
    {
        TextureUse.NormalSampler => TextureRole.Normal,
        TextureUse.MaskSampler => TextureRole.Mask,
        TextureUse.IndexSampler => TextureRole.Index,
        TextureUse.DiffuseSampler => TextureRole.Base,
        _ => TextureRole.Other,
    };

    private static bool IsKind(ResourceNode node, string extension)
        => node.GamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string? FullPath(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
