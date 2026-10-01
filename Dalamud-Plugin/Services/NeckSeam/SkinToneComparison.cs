using System.Numerics;

namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// Two skin materials compared for a tone match: the target (the part that changes) and the base (the
/// skin it should match), read where the target touches the base or over each whole part when they
/// don't touch. Holds the findings the dialog shows and the numbers the fix uses: the diffuse gain,
/// the skin tone influence to take on, the mask offsets and the material settings to copy.
/// </summary>
internal sealed class SkinToneComparison
{
    /// <summary> A colour difference this big in any channel (5 of 255) shows as a seam. </summary>
    public const float ColourLimit = 0.02f;
    /// <summary> Skin tone influence and mask differences this big show, as at the neck. </summary>
    public const float ChannelLimit = 0.03f;
    /// <summary> Colour differences below a step of 255 aren't worth a fix. </summary>
    private const float ColourFloor = 1f / 255f;
    /// <summary> Below this skin tone influence the detail tile doesn't show, so its settings don't matter. </summary>
    private const float TileVisible = 0.05f;
    /// <summary> Tile sizes this far apart (in repeats per metre) show; vanilla's own seams differ by up to about 1.3 times. </summary>
    private const float TileRatioLow = 0.7f, TileRatioHigh = 1.4f;

    private IReadOnlyList<NeckSeamFinding>? _findings;

    public required SkinTonePart Target { get; init; }
    public required SkinTonePart Base { get; init; }
    /// <summary> Target vertices touching the base's skin; 0 when they don't touch and the whole parts were read. </summary>
    public required int Points { get; init; }
    public required SkinToneSample TargetSample { get; init; }
    public required SkinToneSample BaseSample { get; init; }

    public bool Touching => Points > 0;
    public IReadOnlyList<NeckSeamFinding> Findings => _findings ??= BuildFindings();
    public NeckSeamSeverity Worst => Findings.Count == 0 ? NeckSeamSeverity.Ok : Findings.Max(f => f.Severity);
    /// <summary> In sentences: "fac_e skin". </summary>
    public string TargetName => Target.Short + " skin";
    public string BaseName => Base.Short + " skin";

    public static SkinToneComparison Build(SkinTonePart target, SkinTonePart @base, SkinToneContact? contact) => contact is { } touching
        ? new SkinToneComparison { Target = target, Base = @base, Points = touching.Points, TargetSample = touching.Target, BaseSample = touching.Base }
        : new SkinToneComparison { Target = target, Base = @base, Points = 0, TargetSample = target.Whole, BaseSample = @base.Whole };

    public bool ColourDiffers => TargetSample.Colour is { } t && BaseSample.Colour is { } b && MaxAbs(t - b) > ColourFloor;
    public bool InfluenceDiffers => TargetSample.Influence is { } t && BaseSample.Influence is { } b && MathF.Abs(t - b) > ChannelLimit;
    public bool ShineDiffers => MaskOffset != Vector3.Zero;

    /// <summary> What the mask's red, green and blue move by to reach the base's; channels within <see cref="ChannelLimit"/> stay. </summary>
    public Vector3 MaskOffset
    {
        get
        {
            if (TargetSample.Mask is not { } t || BaseSample.Mask is not { } b)
                return Vector3.Zero;
            var d = b - t;
            static float Keep(float v) => MathF.Abs(v) > ChannelLimit ? v : 0f;
            return new Vector3(Keep(d.X), Keep(d.Y), Keep(d.Z));
        }
    }

    /// <summary>
    /// The per-channel factor for the target's diffuse that brings its colour to the base's. When the
    /// material settings aren't matched too, it also makes up for a different g_DiffuseColor; with them
    /// matched, the target takes the base's multiplier and the textures alone differ.
    /// </summary>
    public Vector3? Gain(bool settingsMatched)
    {
        if (TargetSample.Colour is not { } t || BaseSample.Colour is not { } b)
            return null;
        var gain = new Vector3(Ratio(b.X, t.X), Ratio(b.Y, t.Y), Ratio(b.Z, t.Z));
        if (!settingsMatched)
        {
            var kt = Rgb(Target.Skin.Constant(SkinMaterial.DiffuseColor));
            var kb = Rgb(Base.Skin.Constant(SkinMaterial.DiffuseColor));
            gain *= new Vector3(Ratio(kb.X, kt.X), Ratio(kb.Y, kt.Y), Ratio(kb.Z, kt.Z));
        }
        return gain;
    }

    /// <summary>
    /// The material settings that differ, with the target in the face's place and the base in the body's,
    /// so <see cref="NeckSeamMaterialMatch.Plan"/> at 1 changes only the target. The detail tile counts
    /// only where it shows on both sides, which needs skin tone influence: with
    /// <paramref name="influenceMatched"/> the target takes the base's.
    /// </summary>
    /// <param name="baseSkin">The base material to match, when a fix built with it changed it already (the neck's body material).</param>
    public NeckSeamMaterialMatch Settings(bool influenceMatched, SkinMaterial? baseSkin = null)
    {
        var target = Target.Skin;
        var @base = baseSkin ?? Base.Skin;
        var baseInfluence = BaseSample.Influence ?? 1f;
        var targetInfluence = influenceMatched ? baseInfluence : TargetSample.Influence ?? 1f;
        var tiles = targetInfluence > TileVisible && baseInfluence > TileVisible;

        var targetScale = target.Constant(SkinMaterial.TileScale);
        var baseScale = @base.Constant(SkinMaterial.TileScale);
        var ratio = targetScale.Length > 0 && baseScale.Length > 0 && Base.TileDensity > 0 && Target.TileDensity > 0
            ? targetScale.Average() * Target.TileDensity / (baseScale.Average() * Base.TileDensity)
            : 1f;
        var targetAlpha = target.Constant(SkinMaterial.TileAlpha)[0];
        var baseAlpha = @base.Constant(SkinMaterial.TileAlpha)[0];
        // A body skin material's tile strength is also scaled by its normal map's alpha (a face's alpha is the lip mask).
        var targetNormalAlpha = target.IsBodySkin ? TargetSample.NormalAlpha ?? 1f : 1f;
        var baseNormalAlpha = @base.IsBodySkin ? BaseSample.NormalAlpha ?? 1f : 1f;
        var targetIndex = target.Constant(SkinMaterial.TileIndex)[0];
        var baseIndex = @base.Constant(SkinMaterial.TileIndex)[0];

        var other = new Dictionary<uint, (float[], float[])>();
        foreach (var id in new[]
                 {
                     SkinMaterial.DiffuseColor, SkinMaterial.SsaoMask, SkinMaterial.NormalScale, SkinMaterial.SheenRate, SkinMaterial.SheenTintRate,
                     SkinMaterial.SheenAperture, SkinMaterial.TextureMipBias, SkinMaterial.TileMipBiasOffset,
                 })
        {
            var t = target.Constant(id);
            var b = @base.Constant(id);
            if (t.Length == b.Length && t.Zip(b).Any(p => MathF.Abs(p.First - p.Second) > 1e-3f))
                other[id] = (t, b);
        }
        return new NeckSeamMaterialMatch
        {
            FaceTileScale = targetScale, BodyTileScale = baseScale, FaceDensity = Target.TileDensity, BodyDensity = Base.TileDensity,
            TileScaleOff = tiles && ratio is < TileRatioLow or > TileRatioHigh,
            FaceTileAlpha = targetAlpha, BodyTileAlpha = baseAlpha, FaceNormalAlpha = targetNormalAlpha, BodyNormalAlpha = baseNormalAlpha,
            TileAlphaOff = tiles && MathF.Abs(targetAlpha * targetNormalAlpha - baseAlpha * baseNormalAlpha) > 0.05f,
            FaceTileIndex = targetIndex, BodyTileIndex = baseIndex, TileIndexOff = tiles && MathF.Abs(targetIndex - baseIndex) > 0.01f,
            Other = other,
        };
    }

    private List<NeckSeamFinding> BuildFindings()
    {
        var findings = new List<NeckSeamFinding>();
        string t = TargetName, b = BaseName;
        var where = Touching
            ? $"where the {t} touches the {b} ({Points:N0} points within {SkinToneAnalyzer.Reach * 1000:0} mm)"
            : $"over each whole part, since the {t} doesn't touch the {b}";
        if (TargetSample.Colour is { } tc && BaseSample.Colour is { } bc)
        {
            var difference = MaxAbs(tc - bc);
            findings.Add(new NeckSeamFinding("Skin colour", Rgb(tc), Rgb(bc), difference > ColourLimit ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                $"The diffuse texture's colour {where}, the median of the texels there. The largest channel differs by {difference * 255:0} of 255. " +
                "The fix scales every texel of the target's diffuse by the same amount per channel, so its own shading and detail stay.",
                ColourDiffers ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
        float[] kt = Target.Skin.Constant(SkinMaterial.DiffuseColor), kb = Base.Skin.Constant(SkinMaterial.DiffuseColor);
        if (kt.Length == kb.Length && kt.Zip(kb).Any(p => MathF.Abs(p.First - p.Second) > 1e-3f))
            findings.Add(new NeckSeamFinding("Colour multiplier", Join(kt), Join(kb), NeckSeamSeverity.Problem,
                "g_DiffuseColor: skin.shpk multiplies the diffuse texture by it, so different values tint the two parts differently.", NeckSeamFixKind.Material));
        if (TargetSample.Influence is { } ti && BaseSample.Influence is { } bi)
            findings.Add(new NeckSeamFinding("Skin tone influence", $"{ti:0.00}", $"{bi:0.00}", InfluenceDiffers ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                $"How much the character's skin colour tints the diffuse (normal map blue) {where}: the {t} takes {ti * 100:0}% of it, the {b} {bi * 100:0}%." +
                (InfluenceDiffers
                    ? " While they differ, the two only match for one skin colour, and changing the skin colour moves one side only. " +
                      "The fix gives the target the base's value."
                    : ""),
                InfluenceDiffers ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        if (TargetSample.Mask is { } tm && BaseSample.Mask is { } bm)
        {
            void Shine(string title, float target, float @base, string detail)
            {
                var off = MathF.Abs(target - @base) > ChannelLimit;
                findings.Add(new NeckSeamFinding(title, $"{target:0.00}", $"{@base:0.00}", off ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok, detail,
                    off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
            }
            Shine("Specular strength", tm.X, bm.X, $"Mask red {where}: how bright highlights are.");
            Shine("Roughness", tm.Y, bm.Y, $"Mask green {where}: how sharp highlights are.");
            Shine("Subsurface scattering", tm.Z, bm.Z, $"Mask blue {where}: how much light glows through the skin.");
        }
        var settings = Settings(influenceMatched: true);
        if (settings.TileScaleOff)
            findings.Add(new NeckSeamFinding("Skin detail tile size", $"{settings.FaceTiles:0} per m", $"{settings.BodyTiles:0} per m", NeckSeamSeverity.Warning,
                "How often the skin pore tile repeats per metre (g_TileScale over each part's tile UVs).", NeckSeamFixKind.Material));
        if (settings.TileAlphaOff)
            findings.Add(new NeckSeamFinding("Skin detail tile strength", $"{settings.FaceTileStrength:0.00}", $"{settings.BodyTileStrength:0.00}", NeckSeamSeverity.Warning,
                "How strongly the pore tile shows (g_TileAlpha; on body skin also times the normal map's alpha).", NeckSeamFixKind.Material));
        if (settings.TileIndexOff)
            findings.Add(new NeckSeamFinding("Skin detail tile pattern", $"{settings.FaceTileIndex:0}", $"{settings.BodyTileIndex:0}", NeckSeamSeverity.Warning,
                "The pore pattern (g_TileIndex).", NeckSeamFixKind.Material));
        var others = settings.Other.Where(p => p.Key != SkinMaterial.DiffuseColor).ToList();
        if (others.Count > 0)
            findings.Add(new NeckSeamFinding("Other skin settings", $"{others.Count} differ", "", NeckSeamSeverity.Warning,
                "The fix gives the target the base's: " + string.Join("; ", others.Select(o =>
                    $"{SkinMaterial.Names.GetValueOrDefault(o.Key, $"0x{o.Key:X8}")} {Join(o.Value.Item1)} → {Join(o.Value.Item2)}")) + ".",
                NeckSeamFixKind.Material));
        if (MathF.Abs(TargetSample.VertexColour.Z - BaseSample.VertexColour.Z) > 0.1f)
            findings.Add(new NeckSeamFinding("Vertex colour blue", $"{TargetSample.VertexColour.Z:0.00}", $"{BaseSample.VertexColour.Z:0.00}", NeckSeamSeverity.Info,
                "Vertex colour blue scales the specular strength. A difference needs a model edit in Blender.", NeckSeamFixKind.None));
        return findings;
    }

    /// <summary> Whether the chosen fixes change anything: colour, influence, shine, or settings (which depend on the influence choice). </summary>
    public bool AnyFix => ColourDiffers || InfluenceDiffers || ShineDiffers || Settings(true).Any || Settings(false).Any;

    private static float Ratio(float wanted, float current) => current > ColourFloor ? Math.Clamp(wanted / current, 0.25f, 4f) : 1f;
    internal static Vector3 Rgb(float[] values) => values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : values.Length == 1 ? new Vector3(values[0]) : Vector3.One;
    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
    internal static string Rgb(Vector3 colour) => $"{colour.X * 255:0} {colour.Y * 255:0} {colour.Z * 255:0}";
    private static string Join(float[] values) => string.Join(", ", values.Select(v => v.ToString("0.###")));
}
