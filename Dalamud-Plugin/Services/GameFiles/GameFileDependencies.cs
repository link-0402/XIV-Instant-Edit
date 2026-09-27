using System.Collections.Immutable;
using Lumina.Data.Files;

namespace InstantEdit.Services.GameFiles;

/// <summary>A texture a vanilla material uses, and whether the game has it.</summary>
internal sealed record GameFileTexture(string GamePath, string Usage, bool Found);

/// <summary>
/// A material a vanilla model draws with: the name the model stores, the game path it resolves to
/// in material variant <paramref name="Variant"/>, whether the game has it, its textures and, when
/// it couldn't be used, why.
/// </summary>
internal sealed record GameFileMaterial(string Name, string GamePath, int Variant, bool Found,
    ImmutableArray<GameFileTexture> Textures, string Problem);

/// <summary>A vanilla model's materials and textures, and the material variants its IMC file gives it.</summary>
internal sealed record GameModelDependencies(string ModelPath, ImmutableArray<int> Variants,
    ImmutableArray<GameFileMaterial> Materials, ImmutableArray<string> Warnings);

internal static class GameFileDependencies
{
    /// <summary>
    /// A model's material variants: its IMC default variant's first, then every other material folder
    /// its variants use, in ID order. Character parts, and models whose IMC can't be read, use 1.
    /// </summary>
    public static ImmutableArray<int> MaterialVariants(GameModelId id, Func<string, byte[]?> read)
    {
        var part = GameModelPaths.ImcPart(id);
        if (part < 0 || GameModelPaths.ImcPath(id) is not { } path)
            return [1];
        try
        {
            if (read(path) is not { Length: > 0 } bytes)
                return [1];
            var imc = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<ImcFile>(bytes);
            if ((imc.PartMask & (1 << part)) == 0)
                return [1];
            // The file stores only the parts in its mask, in slot order.
            var index = 0;
            for (var bit = 0; bit < part; bit++)
                if ((imc.PartMask & (1 << bit)) != 0)
                    index++;
            var entry = imc.GetParts()[index];
            int first = entry.DefaultVariant.MaterialId;
            var others = (entry.Variants ?? []).Select(variant => (int)variant.MaterialId)
                .Where(material => material > 0 && material != first).Distinct().Order().ToArray();
            if (first > 0)
                return [first, .. others];
            return others.Length > 0 ? [.. others] : [1];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [1];
        }
    }

    /// <summary>
    /// The materials the model at <paramref name="modelPath"/> draws with, in each of
    /// <paramref name="variants"/>, and their textures. A texture is the DX11 file its material names
    /// when the game has one, otherwise the path the material stores. Throws
    /// <see cref="InvalidDataException"/> for model bytes that aren't a readable MDL.
    /// </summary>
    public static GameModelDependencies Resolve(string modelPath, byte[] modelBytes, IReadOnlyList<int> variants,
        Func<string, bool> exists, Func<string, byte[]?> read)
    {
        var names = MaterialPreviewBundleBuilder.ReadUsedModelMaterials(modelBytes)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var warnings = new List<string>();
        var materials = new List<GameFileMaterial>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var variant in variants.Count > 0 ? variants : [1])
            {
                var path = VanillaMaterialPaths.Resolve(modelPath, name, variant);
                if (path is null)
                {
                    warnings.Add($"{name} is not a material path the game can use.");
                    break;
                }
                if (!exists(path) && VanillaMaterialPaths.MainHandMaterial(path) is { } mainHand && exists(mainHand))
                    path = mainHand;
                if (!seen.Add(path))
                    continue;
                materials.Add(Material(name, path, variant, exists, read));
            }
        }
        return new GameModelDependencies(PathRules.NormalizeGamePath(modelPath), [.. variants], [.. materials], [.. warnings]);
    }

    private static GameFileMaterial Material(string name, string path, int variant, Func<string, bool> exists,
        Func<string, byte[]?> read)
    {
        if (!exists(path))
            return new GameFileMaterial(name, path, variant, false, [], "The game has no file at this path.");
        IReadOnlyList<MaterialPreviewBundleBuilder.MaterialTextureReference> references;
        try
        {
            references = read(path) is { Length: > 0 } bytes
                ? MaterialPreviewBundleBuilder.ReadMaterialTextures(bytes)
                : throw new InvalidDataException("the file is empty");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GameFileMaterial(name, path, variant, true, [], $"The material could not be read: {e.Message}");
        }
        var textures = new List<GameFileTexture>();
        foreach (var reference in references)
        {
            var dx11 = !reference.Dx11Path.Equals(reference.StoredPath, StringComparison.OrdinalIgnoreCase) && exists(reference.Dx11Path);
            var texture = dx11 ? reference.Dx11Path : reference.StoredPath;
            if (textures.Any(item => item.GamePath.Equals(texture, StringComparison.OrdinalIgnoreCase)))
                continue;
            textures.Add(new GameFileTexture(texture, reference.Usage, dx11 || exists(texture)));
        }
        return new GameFileMaterial(name, path, variant, true, [.. textures], "");
    }
}
