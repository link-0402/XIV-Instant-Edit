using System.Numerics;

namespace InstantEdit.Services.NeckSeam;

/// <summary> Which tone match to build: the material that changes, the one it should match, and which parts of the match. </summary>
internal sealed record SkinToneFixOptions(string TargetPath, string BasePath, bool Colour, bool Influence, bool Shine, bool Settings)
{
    public bool Any => Colour || Influence || Shine || Settings;
}

/// <summary> A texture the tone match adds for its target material alone: the path the material now requests, the texture it was made from, and its pixels. </summary>
internal sealed record SkinToneNewTexture(string Kind, uint Sampler, string GamePath, byte[] Original, SeamImage Image, string Material);

/// <summary> The tone match's files, what they change, and the target measured again. </summary>
internal sealed class SkinToneFix
{
    public required string TargetPath { get; init; }
    public required SeamFileOutput? Material { get; init; }
    /// <summary> Textures written over their files: only the target's own copies from an earlier match. </summary>
    public required IReadOnlyList<SeamTextureOutput> Textures { get; init; }
    /// <summary> New textures for the target material alone, which the changed material points at. </summary>
    public required IReadOnlyList<SkinToneNewTexture> NewTextures { get; init; }
    public required IReadOnlyList<string> Changes { get; init; }
    /// <summary> The measured values the fix changes, before and after. </summary>
    public required IReadOnlyList<string> Expected { get; init; }
    public bool Empty => Material is null && Textures.Count == 0 && NewTextures.Count == 0;
}

/// <summary>
/// Builds a tone match: the target's whole diffuse scaled per channel to the base's colour, its
/// normal map's blue (the skin tone influence) set to the base's, its mask moved to the base's shine,
/// and the base's material settings. The changed textures never overwrite the files they came from,
/// which other materials may read (a face mod's null_normal.tex, a shared body texture): each becomes
/// a new file next to the target material's textures, and only the target material points at it.
/// Matching again writes over those files.
/// </summary>
internal static class SkinToneFixer
{
    /// <param name="materialBase">Material bytes to build on instead of the files read, by game path (a material another fix changed).</param>
    public static SkinToneFix Build(SkinToneReport report, SkinToneFixOptions options, IReadOnlyDictionary<string, byte[]>? materialBase = null)
    {
        var targetIndex = report.IndexOf(options.TargetPath) ?? throw new InvalidDataException("The skin material to change is no longer drawn; measure again.");
        var baseIndex = report.IndexOf(options.BasePath) ?? throw new InvalidDataException("The skin material to match is no longer drawn; measure again.");
        if (targetIndex == baseIndex)
            throw new InvalidOperationException("Pick two different skin materials to match.");
        var comparison = report.Compare(targetIndex, baseIndex);
        var target = comparison.Target;
        var skin = target.Skin;
        var baseSkin = materialBase?.GetValueOrDefault(comparison.Base.MaterialPath) is { } changedBase ? SkinMaterial.Read(changedBase) : null;
        var changes = new List<string>();
        var expected = new List<string>();

        var constants = options.Settings ? comparison.Settings(options.Influence, baseSkin).Plan(1f).Face : new Dictionary<uint, float[]>();
        Vector3? gain = options.Colour ? comparison.Gain(options.Settings) : null;
        if (gain is { } g && MathF.Max(MathF.Abs(g.X - 1), MathF.Max(MathF.Abs(g.Y - 1), MathF.Abs(g.Z - 1))) < 0.002f)
            gain = null;
        (float From, float To)? influence = options.Influence && comparison.InfluenceDiffers &&
                                            comparison.TargetSample.Influence is { } fromInfluence && comparison.BaseSample.Influence is { } toInfluence
            ? (fromInfluence, toInfluence)
            : null;
        var maskOffset = options.Shine ? comparison.MaskOffset : Vector3.Zero;

        // The textures to change, by their index in the material's texture table, decoded at full size once.
        var work = new Dictionary<int, (uint Sampler, byte[] Original, SeamImage Image)>();
        SeamImage? Edit(uint sampler)
        {
            if (!skin.Samplers.TryGetValue(sampler, out var index))
                return null;
            if (work.TryGetValue(index, out var existing))
                return existing.Image;
            var requested = PathRules.Dx11TexturePath(skin.Textures[index], skin.TextureFlags[index]);
            var bytes = target.Input.Textures.FirstOrDefault(p => string.Equals(PathRules.NormalizeGamePath(p.Key), requested, StringComparison.OrdinalIgnoreCase)).Value
                        ?? throw new InvalidDataException($"The {NeckSeamFixer.Label(sampler)} texture {requested} of {target.FileName} could not be read; measure again.");
            var image = SeamTextures.Decode(bytes, skin.FlagsFor(sampler));
            work[index] = (sampler, bytes, image);
            return image;
        }

        if (gain is { } factor && Edit(SkinMaterial.DiffuseSampler) is { } diffuse)
        {
            Remap(diffuse, r => r * factor.X, g => g * factor.Y, b => b * factor.Z);
            changes.Add($"Diffuse: × {factor.X:0.000} red, × {factor.Y:0.000} green, × {factor.Z:0.000} blue");
            if (comparison.TargetSample.Colour is { } before && comparison.BaseSample.Colour is { } wanted)
                expected.Add($"Skin colour: {SkinToneComparison.Rgb(before)} → {SkinToneComparison.Rgb(Vector3.Clamp(before * factor, Vector3.Zero, Vector3.One))} " +
                             $"(the {comparison.BaseName}: {SkinToneComparison.Rgb(wanted)})");
        }
        if (influence is { } blue && Edit(SkinMaterial.NormalSampler) is { } normal)
        {
            // Scaled where the target has influence to scale, so lower values (nails, lips) keep their share; raised evenly where it has none.
            Remap(normal, null, null, blue.From > 0.02f ? b => b * blue.To / blue.From : b => b + (blue.To - blue.From));
            changes.Add($"Normal map blue (skin tone influence): {blue.From:0.00} → {blue.To:0.00}");
            expected.Add($"Skin tone influence: {blue.From:0.00} → {blue.To:0.00} (the {comparison.BaseName}: {blue.To:0.00})");
        }
        if (maskOffset != Vector3.Zero && Edit(SkinMaterial.MaskSampler) is { } mask)
        {
            Remap(mask, r => r + maskOffset.X, g => g + maskOffset.Y, b => b + maskOffset.Z);
            var parts = new List<string>();
            void Line(string name, float offset, float? from, float? to)
            {
                if (offset == 0)
                    return;
                parts.Add($"{name} {offset:+0.00;-0.00}");
                if (from is { } f && to is { } t)
                    expected.Add($"{name}: {f:0.00} → {Math.Clamp(f + offset, 0, 1):0.00} (the {comparison.BaseName}: {t:0.00})");
            }
            Line("Specular strength", maskOffset.X, comparison.TargetSample.Mask?.X, comparison.BaseSample.Mask?.X);
            Line("Roughness", maskOffset.Y, comparison.TargetSample.Mask?.Y, comparison.BaseSample.Mask?.Y);
            Line("Subsurface scattering", maskOffset.Z, comparison.TargetSample.Mask?.Z, comparison.BaseSample.Mask?.Z);
            changes.Add("Mask: " + string.Join(", ", parts));
        }

        var textures = new List<SeamTextureOutput>();
        var newTextures = new List<SkinToneNewTexture>();
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, (sampler, original, image)) in work.OrderBy(p => p.Key))
        {
            var stored = skin.Textures[index];
            var flags = skin.TextureFlags[index];
            var kind = $"skin {NeckSeamFixer.Label(sampler)} texture";
            var copy = TonePath(target.MaterialPath, Role(sampler));
            if (string.Equals(stored, copy, StringComparison.OrdinalIgnoreCase))
            {
                // The target's own copy from an earlier match: written over like any texture a fix changes.
                textures.Add(new SeamTextureOutput(kind, sampler, PathRules.Dx11TexturePath(stored, flags), original, image, [target.MaterialPath]));
                continue;
            }
            rewrites[stored] = copy;
            newTextures.Add(new SkinToneNewTexture(kind, sampler, PathRules.Dx11TexturePath(copy, flags), original, image, target.MaterialPath));
            changes.Add($"New {kind} {FileName(copy)} for {target.FileName}, made from {FileName(stored)}");
        }

        SeamFileOutput? material = null;
        if (constants.Count > 0 || rewrites.Count > 0)
        {
            var bytes = materialBase?.GetValueOrDefault(target.MaterialPath) ?? target.Input.Bytes;
            if (constants.Count > 0)
            {
                bytes = SkinMaterial.Read(bytes).WithConstants(constants);
                changes.Add($"Skin material {target.FileName}: {NeckSeamFixer.Describe(constants)}");
            }
            if (rewrites.Count > 0)
                bytes = PenumbraService.RewriteMaterialTexturePaths(bytes, rewrites);
            material = new SeamFileOutput("skin material", target.MaterialPath, bytes);
        }
        return new SkinToneFix
        {
            TargetPath = target.MaterialPath, Material = material, Textures = textures, NewTextures = newTextures,
            Changes = changes.Select(c => "Skin tone: " + c).ToList(), Expected = expected,
        };
    }

    /// <summary>
    /// Where a tone match puts a material's texture: the texture folder beside its material folder, named
    /// after the material and the texture's role (…/f0002/material/mt_c0801f0002_fac_e.mtrl, "base" →
    /// …/f0002/texture/c0801f0002_fac_e_tone_base.tex).
    /// </summary>
    internal static string TonePath(string materialPath, string role)
    {
        var path = PathRules.NormalizeGamePath(materialPath);
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? [] : path[..slash].Split('/').ToList();
        var materials = folder.FindLastIndex(segment => string.Equals(segment, "material", StringComparison.OrdinalIgnoreCase));
        if (materials >= 0)
            folder = [.. folder.Take(materials), "texture"];
        var stem = Path.GetFileNameWithoutExtension(path[(slash + 1)..]);
        if (stem.StartsWith("mt_", StringComparison.OrdinalIgnoreCase))
            stem = stem[3..];
        return (folder.Count == 0 ? "" : string.Join('/', folder) + "/") + $"{stem}_tone_{role}.tex";
    }

    private static string Role(uint sampler) => sampler switch
    {
        SkinMaterial.DiffuseSampler => "base",
        SkinMaterial.NormalSampler => "norm",
        _ => "mask",
    };

    /// <summary> Changes the red, green and blue of every texel by a function of that channel alone (null keeps it), through a table of its 256 values. </summary>
    private static void Remap(SeamImage image, Func<float, float>? red, Func<float, float>? green, Func<float, float>? blue)
    {
        byte[]? Table(Func<float, float>? change)
        {
            if (change is null)
                return null;
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
                table[i] = (byte)Math.Clamp((int)MathF.Round(change(i / 255f) * 255f), 0, 255);
            return table;
        }
        byte[]? r = Table(red), g = Table(green), b = Table(blue);
        var rgba = image.Rgba;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (r is not null) rgba[i] = r[rgba[i]];
            if (g is not null) rgba[i + 1] = g[rgba[i + 1]];
            if (b is not null) rgba[i + 2] = b[rgba[i + 2]];
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
