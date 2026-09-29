using Dalamud.Utility;
using Lumina.Data;
using Lumina.Data.Files;
using InstantEdit.Models;
using InstantEdit.Services.Painter;

namespace InstantEdit.Services;

/// <summary> A texture a material samples, resolved against the actor's loaded resources. </summary>
/// <param name="GamePath">The path the game loads (the DX11 variant when the material asks for it).</param>
/// <param name="SourcePath">An absolute file for mod or external resources; otherwise the game data path to read.</param>
/// <param name="Locator">Mod or game location; null when the file lies outside Penumbra's mod folder.</param>
/// <param name="Problem">Why the texture could not be read; empty when it was.</param>
public sealed record TexturePlanTexture(uint SamplerId, string Usage, int UvSet, string GamePath, string SourcePath,
    SourceResourceLocator? Locator, uint Format, int Width, int Height, string Problem)
{
    public bool IsVanilla => Locator?.Kind == "game";
    public bool IsModded => Locator?.Kind == "mod";
}

/// <summary> A material the model draws with, and the textures it samples. </summary>
public sealed record TexturePlanMaterial(string ModelMaterial, string GamePath, string SourcePath, string ShaderPackage,
    IReadOnlyList<TexturePlanTexture> Textures, string Problem)
{
    /// <summary> Shader-header flags: 0x01 hides back faces, 0x10 blends by opacity instead of cutting it off. </summary>
    public uint? Flags { get; init; }

    /// <summary> g_AlphaThreshold: materials that don't blend discard texels whose opacity is below it. </summary>
    public float? AlphaThreshold { get; init; }

    public TexturePlanColorSet? ColorSet { get; init; }
}

/// <summary> A material's colorset table, rows of <see cref="RowWidth"/> values (16 legacy, 32 Dawntrail). </summary>
public sealed record TexturePlanColorSet(int RowWidth, float[] Values)
{
    public int Rows => Values.Length / RowWidth;
}

public sealed record ModelTexturePlan(string ModelGamePath, IReadOnlyList<TexturePlanMaterial> Materials, IReadOnlyList<string> Warnings);

public sealed partial class MaterialPreviewBundleBuilder
{
    /// <summary>
    /// Resolves every drawn material of a model and each texture its samplers use, against the
    /// actor's loaded resources. Unlike the dependency capture it never gives up on the whole
    /// model: a material or texture that can't be read carries a problem instead.
    /// </summary>
    public async Task<ModelTexturePlan> BuildTexturePlanAsync(byte[] modelBytes, string modelGamePath,
        IReadOnlyCollection<MaterialResourceCandidate> candidates, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var resources = BuildResourceMap(candidates, warnings);
        var names = ReadUsedModelMaterials(modelBytes)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMaterials)
            .ToArray();
        var materials = new List<TexturePlanMaterial>();
        foreach (var modelMaterial in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materialPath = ResolveMaterialPath(modelGamePath, modelMaterial, resources, warnings);
            if (materialPath is null)
            {
                materials.Add(new TexturePlanMaterial(modelMaterial, "", "", "", [], "The material could not be resolved."));
                continue;
            }
            var material = await ResolvePlanResourceAsync(materialPath, resources, MaxMaterialBytes, cancellationToken).ConfigureAwait(false);
            if (material is null)
            {
                materials.Add(new TexturePlanMaterial(modelMaterial, materialPath, "", "", [], "The material file could not be read."));
                continue;
            }
            MtrlFile mtrl;
            MaterialMetadata metadata;
            try
            {
                mtrl = LooseLuminaFile.Load<MtrlFile>(material.Value.Bytes);
                metadata = ReadMaterialMetadata(material.Value.Bytes, mtrl);
            }
            catch (Exception e)
            {
                materials.Add(new TexturePlanMaterial(modelMaterial, materialPath, material.Value.SourcePath, "", [],
                    $"The material could not be parsed: {e.Message}"));
                continue;
            }

            var textures = new List<TexturePlanTexture>();
            foreach (var sampler in metadata.Samplers)
            {
                if (sampler.TextureIndex == byte.MaxValue || sampler.TextureIndex >= mtrl.TextureOffsets.Length)
                    continue;
                var offset = mtrl.TextureOffsets[sampler.TextureIndex];
                var stored = NormaliseGamePath(ReadString(mtrl.Strings, offset.Offset));
                var usage = TextureUsage(sampler.SamplerId);
                var uvSet = TextureUvSet(sampler.SamplerId);
                if (!IsSafeGameResourcePath(stored, ".tex"))
                {
                    textures.Add(new TexturePlanTexture(sampler.SamplerId, usage, uvSet, stored, "", null, 0, 0, 0,
                        "The material names an invalid texture path."));
                    continue;
                }
                var effective = Dx11TexturePath(stored, offset.Flags);
                var gamePath = effective;
                var resolved = await ResolvePlanResourceAsync(effective, resources, MaxTextureBytes, cancellationToken).ConfigureAwait(false);
                if (resolved is null && !effective.Equals(stored, StringComparison.OrdinalIgnoreCase) && !resources.ContainsKey(effective))
                {
                    resolved = await ResolvePlanResourceAsync(stored, resources, MaxTextureBytes, cancellationToken).ConfigureAwait(false);
                    gamePath = stored;
                }
                if (resolved is null)
                {
                    textures.Add(new TexturePlanTexture(sampler.SamplerId, usage, uvSet, gamePath, "", null, 0, 0, 0,
                        "The texture could not be read."));
                    continue;
                }
                var problem = "";
                TextureHeader header = default;
                try { header = TextureFiles.ReadOriginal(resolved.Value.Bytes).Header; }
                catch (Exception e) { problem = $"The texture is not a supported TEX file: {e.Message}"; }
                var locator = CreateLocator(gamePath, new ResolvedResource(resolved.Value.Bytes, resolved.Value.Rooted ? resolved.Value.SourcePath : null), candidates);
                textures.Add(new TexturePlanTexture(sampler.SamplerId, usage, uvSet, gamePath, resolved.Value.SourcePath, locator,
                    header.Format, header.Width, header.Height, problem));
            }
            materials.Add(new TexturePlanMaterial(modelMaterial, materialPath, material.Value.SourcePath,
                ReadString(mtrl.Strings, mtrl.FileHeader.ShaderPackageNameOffset), textures, "")
            {
                Flags = metadata.Flags,
                AlphaThreshold = ReadShaderConstant(material.Value.Bytes, mtrl, AlphaThresholdConstant),
                ColorSet = ReadPlanColorSet(material.Value.Bytes, mtrl),
            });
        }
        return new ModelTexturePlan(NormaliseGamePath(modelGamePath), materials, BoundWarnings(warnings));
    }

    // g_AlphaThreshold (xivModdingFramework's ConstantId 699138595).
    private const uint AlphaThresholdConstant = 0x29AC0223;

    /// <summary> A shader constant's first value; null when the material doesn't set it. </summary>
    private static float? ReadShaderConstant(byte[] bytes, MtrlFile mtrl, uint id)
    {
        // Same layout ReadMaterialMetadata has already validated for this material.
        var header = checked(DataSetOffset(mtrl) + mtrl.FileHeader.DataSetSize);
        if (header < 0 || header + 12 > bytes.Length)
            return null;
        var valueBytes = BitConverter.ToUInt16(bytes, header);
        var keyCount = BitConverter.ToUInt16(bytes, header + 2);
        var constantCount = BitConverter.ToUInt16(bytes, header + 4);
        var samplerCount = BitConverter.ToUInt16(bytes, header + 6);
        var constants = header + 12 + keyCount * 8;
        var values = constants + constantCount * 8 + samplerCount * 12;
        if (values + valueBytes > bytes.Length)
            return null;
        for (var i = 0; i < constantCount; i++)
        {
            var at = constants + i * 8;
            if (BitConverter.ToUInt32(bytes, at) != id)
                continue;
            var offset = BitConverter.ToUInt16(bytes, at + 4);
            var size = BitConverter.ToUInt16(bytes, at + 6);
            if (size < sizeof(float) || offset % sizeof(float) != 0 || offset + sizeof(float) > valueBytes)
                return null;
            var value = BitConverter.ToSingle(bytes, values + offset);
            return float.IsFinite(value) ? value : null;
        }
        return null;
    }

    private static TexturePlanColorSet? ReadPlanColorSet(byte[] bytes, MtrlFile mtrl)
    {
        // The table sizes ReadColorSet accepts; dye tables that follow it are left out.
        var (tableBytes, rowWidth) = mtrl.FileHeader.DataSetSize switch
        {
            512 or 544 or 1024 => (mtrl.FileHeader.DataSetSize == 1024 ? 1024 : 512, 16),
            2048 or 2112 or 2176 => (2048, 32),
            _ => (0, 0),
        };
        var offset = DataSetOffset(mtrl);
        if (tableBytes == 0 || offset < 0 || offset + tableBytes > bytes.Length)
            return null;
        var values = new float[tableBytes / 2];
        for (var i = 0; i < values.Length; i++)
            values[i] = JsonSafe((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, offset + i * 2)), (float)Half.MaxValue);
        return new TexturePlanColorSet(rowWidth, values);
    }

    /// <summary> Decodes a planned texture's top mip to RGBA, re-reading it from where the plan found it. </summary>
    internal async Task<RgbaImage> DecodeTextureAsync(TexturePlanTexture texture, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (Path.IsPathRooted(texture.SourcePath))
            bytes = await File.ReadAllBytesAsync(texture.SourcePath, cancellationToken).ConfigureAwait(false);
        else
            bytes = (await _data.GetFileAsync<FileResource>(texture.SourcePath, cancellationToken).ConfigureAwait(false))?.Data
                ?? throw new FileNotFoundException($"Game file not found: {texture.SourcePath}");
        var tex = LooseLuminaFile.Load<TexFile>(TextureFiles.NormalizeMipOffsets(bytes));
        var width = (int)tex.Header.Width;
        var height = (int)tex.Header.Height;
        var rgba = tex.GetRgbaImageData();
        if (rgba.Length != checked(width * height * 4))
            throw new InvalidDataException($"{FileName(texture.GamePath)} decoded to an unexpected size.");
        return new RgbaImage(width, height, rgba);
    }

    private readonly record struct PlanResource(byte[] Bytes, string SourcePath, bool Rooted);

    /// <summary>
    /// Like ResolveResourceAsync, but a game path Penumbra redirects to another game path (race
    /// sharing) is read from that other path, and the path actually read is reported.
    /// </summary>
    private async Task<PlanResource?> ResolvePlanResourceAsync(string gamePath, IReadOnlyDictionary<string, List<string>> resources,
        long maxBytes, CancellationToken cancellationToken)
    {
        gamePath = NormaliseGamePath(gamePath);
        var source = gamePath;
        if (resources.TryGetValue(gamePath, out var actualPaths))
        {
            if (actualPaths.Count != 1)
                return null;
            var actual = actualPaths[0];
            if (Path.IsPathRooted(actual))
            {
                var info = new FileInfo(actual);
                if (!info.Exists || info.Length <= 0 || info.Length > maxBytes)
                    return null;
                return new PlanResource(await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false), info.FullName, true);
            }
            var redirected = NormaliseGamePath(actual);
            if (IsSafeGameResourcePath(redirected, ".mtrl", ".tex"))
                source = redirected;
        }
        try
        {
            var file = await _data.GetFileAsync<FileResource>(source, cancellationToken).ConfigureAwait(false);
            return file?.Data is { Length: > 0 } data && data.LongLength <= maxBytes ? new PlanResource(data, source, false) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not read planned resource {GamePath}.", source);
            return null;
        }
    }
}
