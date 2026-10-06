using System.Numerics;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Rotations as three angles in degrees, for typing and dragging in the bone panels. The angles turn
/// about X, then Y, then Z of the frame they are given in, composed q = X·Y·Z: each one turns about an
/// axis the ones after it have already turned, the same as three nested gimbals.
/// </summary>
internal static class MotionAngles
{
    private const float Degree = MathF.PI / 180;

    public static Quaternion FromEuler(Vector3 degrees) => Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * Degree) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * Degree) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * Degree));

    /// <summary>The angles <see cref="FromEuler"/> makes <paramref name="rotation"/> from; Y stays within ±90 degrees.</summary>
    public static Vector3 ToEuler(Quaternion rotation)
    {
        // Row-vector matrices: M is the transpose of the column-vector R = Rx·Ry·Rz.
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        var sinY = Math.Clamp(m.M31, -1f, 1f);
        var y = MathF.Asin(sinY);
        float x, z;
        if (MathF.Abs(sinY) < .99999f)
        {
            x = MathF.Atan2(-m.M32, m.M33);
            z = MathF.Atan2(-m.M21, m.M11);
        }
        else
        {
            // Gimbal lock: X and Z turn about the same axis, so Z takes none of it.
            x = MathF.Atan2(m.M23, m.M22);
            z = 0;
        }
        return new Vector3(x, y, z) / Degree;
    }

    /// <summary>Angles wrapped into (-180, 180], so a field never shows 359 for -1.</summary>
    public static Vector3 Wrap(Vector3 degrees) => new(Wrap(degrees.X), Wrap(degrees.Y), Wrap(degrees.Z));

    private static float Wrap(float degrees)
    {
        if (!float.IsFinite(degrees))
            return 0;
        degrees %= 360;
        return degrees > 180 ? degrees - 360 : degrees <= -180 ? degrees + 360 : degrees;
    }
}
