using System.Security.Cryptography;
using InstantEdit.Models;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Services.NeckSeam;

/// <summary> Where one file the seam analysis read came from, with its hash at that moment. </summary>
internal sealed record NeckSeamSource : PreviewSource;

/// <summary> The analysis input gathered from an actor's resource tree, and the sources of the files a fix can change. </summary>
internal sealed record NeckSeamCaptured(NeckSeamInput Input, IReadOnlyDictionary<string, NeckSeamSource> Sources)
{
    public NeckSeamSource? Source(string gamePath)
        => Sources.TryGetValue(PathRules.NormalizeGamePath(gamePath), out var source) ? source : null;
}

/// <summary>
/// Collects the neck seam analysis input from an On Screen snapshot: the face model with its
/// materials and their textures, and every other model that has a body skin material. Textures a
/// material names but the tree doesn't list come from game data. Dalamud-free; reading is delegated.
/// </summary>
internal static class NeckSeamCapture
{
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <param name="read">Reads a resolved file: a full path, or a game path for game data. Null when unavailable.</param>
    public static NeckSeamCaptured Capture(IReadOnlyList<ResourceNode> roots, Func<string, byte[]?> read, byte[]? racialDeformers)
    {
        var models = Flatten(roots).Where(node => node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList();
        var faceNode = models.FirstOrDefault(node => NeckSeamAnalyzer.IsFaceModel(node.GamePath))
            ?? throw new InvalidDataException("This character has no face model loaded.");
        var sources = new Dictionary<string, NeckSeamSource>(StringComparer.OrdinalIgnoreCase);
        var faceBytes = ReadNode(faceNode, read, sources)
            ?? throw new InvalidDataException("The face model could not be read.");
        var face = new NeckSeamModelInput(faceNode.GamePath, faceBytes, Materials(faceNode, read, sources, _ => true, recordTextures: true));

        var bodies = new List<NeckSeamModelInput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { faceNode.GamePath + "\n" + faceNode.ActualPath };
        foreach (var node in models)
        {
            if (!seen.Add(node.GamePath + "\n" + node.ActualPath))
                continue;
            // Body skin materials are recorded too: the skin settings can meet on the body's side.
            var skins = Materials(node, read, sources, material => material.IsBodySkin, recordTextures: false);
            if (skins.Count == 0 || ReadNode(node, read, null) is not { } bytes)
                continue;
            bodies.Add(new NeckSeamModelInput(node.GamePath, bytes, skins));
        }
        return new NeckSeamCaptured(new NeckSeamInput(face, bodies, racialDeformers), sources);
    }

    /// <summary> Whether the tree has the face model <see cref="Capture"/> needs. </summary>
    public static bool HasFaceModel(IReadOnlyList<ResourceNode> roots)
        => Flatten(roots).Any(node => node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && NeckSeamAnalyzer.IsFaceModel(node.GamePath));

    private static IEnumerable<ResourceNode> Flatten(IEnumerable<ResourceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    /// <summary> A model's materials (those <paramref name="wanted"/> admits) with the textures they request. </summary>
    private static List<NeckSeamMaterialInput> Materials(ResourceNode model, Func<string, byte[]?> read,
        Dictionary<string, NeckSeamSource>? sources, Func<SkinMaterial, bool> wanted, bool recordTextures)
    {
        var result = new List<NeckSeamMaterialInput>();
        foreach (var node in Flatten(model.Children).Where(n => n.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)))
        {
            if (result.Any(m => string.Equals(m.GamePath, node.GamePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            var bytes = ReadNode(node, read, null);
            if (bytes is null)
                continue;
            SkinMaterial material;
            try { material = SkinMaterial.Read(bytes); }
            catch (InvalidDataException) { continue; }
            if (!wanted(material))
                continue;
            if (sources is not null)
                sources[PathRules.NormalizeGamePath(node.GamePath)] = SourceOf(node, bytes);

            var textureNodes = Flatten(node.Children).Where(n => n.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).ToList();
            var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var sampler in new[] { SkinMaterial.DiffuseSampler, SkinMaterial.NormalSampler, SkinMaterial.MaskSampler })
            {
                if (material.RequestedTextureFor(sampler) is not { } requested || textures.ContainsKey(requested))
                    continue;
                var textureNode = textureNodes.FirstOrDefault(n => string.Equals(PathRules.NormalizeGamePath(n.GamePath), requested, StringComparison.OrdinalIgnoreCase));
                byte[]? textureBytes;
                if (textureNode is not null)
                {
                    textureBytes = ReadNode(textureNode, read, null);
                    if (textureBytes is not null && sources is not null && recordTextures)
                        sources[requested] = SourceOf(textureNode, textureBytes);
                }
                else
                {
                    // Penumbra leaves unchanged vanilla textures out of some trees; the game loads them from its data.
                    textureBytes = read(requested);
                    if (textureBytes is not null && sources is not null && recordTextures)
                        sources[requested] = new NeckSeamSource
                        {
                            GamePath = requested, ActualPath = requested, State = ResourceSourceState.GameData, Sha256 = Hash(textureBytes),
                        };
                }
                if (textureBytes is not null)
                    textures[requested] = textureBytes;
            }
            result.Add(new NeckSeamMaterialInput(node.GamePath, bytes, textures));
        }
        return result;
    }

    private static byte[]? ReadNode(ResourceNode node, Func<string, byte[]?> read, Dictionary<string, NeckSeamSource>? sources)
    {
        if (node.SourceState is not (ResourceSourceState.LoadedMod or ResourceSourceState.GameData) || node.ActualPath.Length == 0)
            return null;
        var bytes = read(node.ActualPath);
        if (bytes is not null && sources is not null)
            sources[PathRules.NormalizeGamePath(node.GamePath)] = SourceOf(node, bytes);
        return bytes;
    }

    private static NeckSeamSource SourceOf(ResourceNode node, byte[] bytes) => new()
    {
        GamePath = PathRules.NormalizeGamePath(node.GamePath),
        ActualPath = node.ActualPath,
        State = node.SourceState,
        ModName = node.SourceModName ?? string.Empty,
        ModDirectory = node.SourceModDirectory ?? string.Empty,
        ModRootPath = node.SourceModRootPath ?? string.Empty,
        RelativePath = node.SourceState == ResourceSourceState.LoadedMod ? (node.SourceRelativePath ?? string.Empty).Replace('\\', '/') : string.Empty,
        ModStableId = node.SourceModStableId,
        Sha256 = Hash(bytes),
    };

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary> One file of a preview mod: what it replaces, where it sits in the mod, and where it came from. </summary>
internal sealed record NeckSeamPreviewFile
{
    public required string Kind { get; init; }
    /// <summary> The path the game requests for the original. </summary>
    public required string GamePath { get; init; }
    /// <summary> The path the preview mod maps (a new path for textures, so shared textures elsewhere don't change). </summary>
    public required string PreviewGamePath { get; init; }
    public required string PreviewRelativePath { get; init; }
    public required string PreviewSha256 { get; init; }
    public required NeckSeamSource Source { get; init; }
    /// <summary> For the material: the preview's texture paths and the stored paths they replace. </summary>
    public Dictionary<string, string> TextureRewrites { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary> A neck seam preview mod the plugin created and has not applied or discarded yet. </summary>
internal sealed record NeckSeamPreview : IPreviewModRecord
{
    public required Guid Id { get; init; }
    public required string ModDirectory { get; init; }
    public required Guid ModIdentifier { get; init; }
    public required Guid CollectionId { get; init; }
    public required string CollectionName { get; init; }
    public required string ActorName { get; init; }
    public required ushort ObjectIndex { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required List<NeckSeamPreviewFile> Files { get; init; }
    public List<string> Changes { get; init; } = [];
}

/// <summary> Previews persisted in the plugin's config folder, so a reload can still apply or discard them. </summary>
internal sealed class NeckSeamPreviewStore(string configDirectory)
    : PreviewModStore<NeckSeamPreview>(configDirectory, "NeckSeamPreviews.json", "neck seam previews");
