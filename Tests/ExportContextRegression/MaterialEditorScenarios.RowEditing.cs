using System.Numerics;
using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The colour-row editor's maths: the detail pattern's 2 x 2 transform as scale, rotation and shear; colours in square-root space; the swatches' base colour times half 11.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunRowEditing()
    {
        CheckTileTransform();
        CheckRowMath();
    }

    private static bool Near(float a, float b, float tolerance = 1e-3f) => MathF.Abs(a - b) <= tolerance;

    private static void CheckTileTransform()
    {
        var identity = TileTransform.Decompose(16, 0, 0, 16);
        Require(Near(identity.ScaleU, 16) && Near(identity.ScaleV, 16) && Near(identity.RotationDeg, 0) && Near(identity.ShearDeg, 0),
            "16 x identity reads as scale 16 by 16, no rotation, no shear");

        var turned = TileTransform.Compose(16, 16, 90, 0);
        Require(Near(turned.uu, 0) && Near(turned.uv, -16) && Near(turned.vu, 16) && Near(turned.vv, 0),
            "a 90 degree rotation composes to (0, -16, 16, 0)");

        var random = new Random(41);
        var worst = 0f;
        for (var i = 0; i < 200; i++)
        {
            var (uu, uv, vu, vv) = (random.NextSingle() * 64 - 32, random.NextSingle() * 64 - 32, random.NextSingle() * 64 - 32, random.NextSingle() * 64 - 32);
            if (MathF.Abs(uu * vv - uv * vu) < 1 || MathF.Sqrt(uu * uu + vu * vu) < 0.5f)
                continue;
            var parts = TileTransform.Decompose(uu, uv, vu, vv);
            var back = TileTransform.Compose(parts.ScaleU, parts.ScaleV, parts.RotationDeg, parts.ShearDeg);
            worst = new[] { worst, MathF.Abs(back.uu - uu), MathF.Abs(back.uv - uv), MathF.Abs(back.vu - vu), MathF.Abs(back.vv - vv) }.Max();
        }
        Require(worst < 1e-3f, $"random transforms decompose and compose back within 0.001 (worst {worst:0.######})");
    }

    private static void CheckRowMath()
    {
        var stored = new Vector3(4f, -0.25f, 0f);
        var edit = RowMath.ToEdit(stored);
        Require(edit == new Vector3(2f, -0.5f, 0f) && RowMath.ToStored(edit) == stored,
            "colours are edited as signed square roots and stored as signed squares");

        // The Dawntrail packages' composite passes multiply the base colour by half 11; the glow is col2.rgb times the scene's glow scale only.
        var row = DtColorRow.Default with { Diffuse = new Vector3(0.5f, 0.25f, 1f), Specular = new Vector3(0.1f), Emissive = new Vector3(0.75f, 0f, 0.5f), Exposure = 0f };
        Require(RowMath.Swatches(row, legacyShader: false) == (Vector3.Zero, row.Specular, row.Emissive),
            "on character.shpk a row with half 11 = 0 shows a black base and its glow colour as it is");
        Require(RowMath.Swatches(row with { Exposure = 2f }, legacyShader: false).Base == new Vector3(1f, 0.5f, 2f),
            "half 11 = 2 shows the base colour twice as bright");
        Require(RowMath.Swatches(row, legacyShader: true) == (row.Diffuse, row.Specular, row.Emissive),
            "legacy shaders don't read half 11, so their swatches show the colours as stored");
        Require(ShaderLabels.RowField(11) == "Base brightness", "half 11 is labelled as the base brightness, not a glow value");
    }
}
