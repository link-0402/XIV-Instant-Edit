using System.Numerics;

namespace InstantEdit.Services.Materials;

/// <summary>
/// A colour row's detail-pattern transform (UU, UV, VU, VV) as the editor shows it: scale across
/// and down, rotation and shear, in that order of application (M = rotation x shear x scale).
/// </summary>
public static class TileTransform
{
    public static (float ScaleU, float ScaleV, float RotationDeg, float ShearDeg) Decompose(float uu, float uv, float vu, float vv)
    {
        var scaleU = MathF.Sqrt(uu * uu + vu * vu);
        var angle = scaleU > 1e-6f ? MathF.Atan2(vu, uu) : 0f;
        var (sin, cos) = MathF.SinCos(angle);
        // The second column turned back by the rotation is scaleV x (tan shear, 1).
        var scaleV = -sin * uv + cos * vv;
        var shear = MathF.Abs(scaleV) > 1e-6f ? MathF.Atan((cos * uv + sin * vv) / scaleV) : 0f;
        return (scaleU, scaleV, angle * 180f / MathF.PI, shear * 180f / MathF.PI);
    }

    public static (float uu, float uv, float vu, float vv) Compose(float scaleU, float scaleV, float rotationDeg, float shearDeg)
    {
        var (sin, cos) = MathF.SinCos(rotationDeg * MathF.PI / 180f);
        var tan = MathF.Tan(shearDeg * MathF.PI / 180f);
        return (cos * scaleU, (cos * tan - sin) * scaleV, sin * scaleU, (sin * tan + cos) * scaleV);
    }
}

/// <summary>Conversions between how colour rows store values and how the editor shows them.</summary>
public static class RowMath
{
    /// <summary>Colours are edited as square roots (keeping the sign), so the picker spreads dark colours out as the game's lighting does.</summary>
    public static Vector3 ToEdit(Vector3 stored) => new(SignedSqrt(stored.X), SignedSqrt(stored.Y), SignedSqrt(stored.Z));

    public static Vector3 ToStored(Vector3 edit) => new(edit.X * MathF.Abs(edit.X), edit.Y * MathF.Abs(edit.Y), edit.Z * MathF.Abs(edit.Z));

    /// <summary>Glow exposure in stops: 1 is 0 stops, every 4 times brighter is one stop more; 0 is minus infinity.</summary>
    public static float ExposureToStops(float exposure) => exposure <= 0 ? float.NegativeInfinity : MathF.Log2(exposure) / 2f;

    public static float StopsToExposure(float stops) => float.IsNegativeInfinity(stops) ? 0f : MathF.Pow(4f, stops);

    private static float SignedSqrt(float value) => MathF.CopySign(MathF.Sqrt(MathF.Abs(value)), value);
}
