using System.Numerics;

namespace InstantEdit.Ui.Timeline;

/// <summary> Where the free camera is: its spot in the world and which way it looks, as yaw and pitch in radians. </summary>
internal readonly record struct FreeCameraPose(Vector3 Position, float Yaw, float Pitch);

/// <summary> One frame's moving keys: each axis -1, 0 or 1 by the keys held, and whether it goes fast. </summary>
internal readonly record struct FreeCameraInput(float Forward, float Right, float Up, bool Fast);

/// <summary>
/// The free camera's motion, without the game: yaw 0 looks along +Z, Y is up and the screen's right is forward × up, as the game's
/// camera has it. W and S fly where the camera looks, A and D slide sideways, Space and Ctrl straight up and down.
/// </summary>
internal static class FreeCameraMotion
{
    public const float DefaultSpeed = 1.5f;
    public const float MinSpeed = 0.1f;
    public const float MaxSpeed = 50f;
    // Shift's speed-up and a wheel notch's step.
    public const float FastFactor = 4f;
    public const float WheelStep = 1.25f;
    // Short of straight up or down, where the look-at would line up with the up vector.
    public static readonly float MaxPitch = 85 * MathF.PI / 180;

    /// <summary> The pose of a camera at <paramref name="position"/> looking at <paramref name="lookAt"/>; level along +Z when they meet. </summary>
    public static FreeCameraPose From(Vector3 position, Vector3 lookAt)
    {
        var direction = lookAt - position;
        if (!(direction.LengthSquared() > 1e-12f))
            return new FreeCameraPose(position, 0, 0);
        direction = Vector3.Normalize(direction);
        var pitch = Math.Clamp(MathF.Asin(Math.Clamp(direction.Y, -1, 1)), -MaxPitch, MaxPitch);
        return new FreeCameraPose(position, MathF.Atan2(direction.X, direction.Z), pitch);
    }

    /// <summary> The direction the pose looks in, one metre long. </summary>
    public static Vector3 Forward(FreeCameraPose pose) =>
        new(MathF.Sin(pose.Yaw) * MathF.Cos(pose.Pitch), MathF.Sin(pose.Pitch), MathF.Cos(pose.Yaw) * MathF.Cos(pose.Pitch));

    /// <summary> The screen's right in the world, level. </summary>
    public static Vector3 Right(FreeCameraPose pose) => new(-MathF.Cos(pose.Yaw), 0, MathF.Sin(pose.Yaw));

    /// <summary> The screen's up in the world, square to where the pose looks and to its level right: the view never rolls. </summary>
    public static Vector3 Up(FreeCameraPose pose) => Vector3.Cross(Right(pose), Forward(pose));

    /// <summary> The point the game's camera looks at: a metre ahead. </summary>
    public static Vector3 LookAt(FreeCameraPose pose) => pose.Position + Forward(pose);

    /// <summary>
    /// The pose turned by <paramref name="yaw"/> and <paramref name="pitch"/> radians, as the game's camera turned this frame (its
    /// DirH and DirV turn the view the same way); the pitch stays short of straight up or down.
    /// </summary>
    public static FreeCameraPose Turn(FreeCameraPose pose, float yaw, float pitch)
    {
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch))
            return pose;
        return pose with
        {
            Yaw = MathF.IEEERemainder(pose.Yaw + yaw, MathF.Tau),
            Pitch = Math.Clamp(pose.Pitch + pitch, -MaxPitch, MaxPitch),
        };
    }

    /// <summary> The pose after <paramref name="seconds"/> of <paramref name="input"/> at <paramref name="speed"/> metres a second. </summary>
    public static FreeCameraPose Step(FreeCameraPose pose, FreeCameraInput input, float speed, float seconds)
    {
        var move = Forward(pose) * input.Forward + Right(pose) * input.Right + Vector3.UnitY * input.Up;
        if (move.LengthSquared() > 1)
            move = Vector3.Normalize(move);
        var distance = speed * (input.Fast ? FastFactor : 1) * Math.Max(0, seconds);
        return pose with { Position = pose.Position + move * distance };
    }

    /// <summary> The speed after <paramref name="notches"/> wheel notches (up is faster), kept between the limits. </summary>
    public static float Speed(float speed, float notches) =>
        notches == 0 || !float.IsFinite(notches) ? speed : Math.Clamp(speed * MathF.Pow(WheelStep, notches), MinSpeed, MaxSpeed);
}
