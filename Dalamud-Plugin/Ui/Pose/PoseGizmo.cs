using System.Numerics;

namespace InstantEdit.Ui.Pose;

/// <summary>
/// The math between the game's camera, the posed character and the gizmo on it, free of the game and
/// ImGui: the character's place in the world, where a point of the world lands on the screen, the
/// camera's projection made usable for ImGuizmo, and the turn and move a gizmo drag makes. The gizmo
/// works in the character's model space, so its world axes are the character's. Adapted from Brio's
/// posing overlay (GPL-3.0).
/// </summary>
internal static class PoseGizmo
{
    /// <summary>The character's model space to the world, as the game places its skeleton: scaled, turned, then moved.</summary>
    public static Matrix4x4 World(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position);

    /// <summary>A bone's gizmo in model space: turned and placed like the bone, unscaled.</summary>
    public static Matrix4x4 Bone(Vector3 position, Quaternion rotation) =>
        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(position);

    /// <summary>
    /// The camera's projection with an ordinary depth range for ImGuizmo. The game's runs reversed toward
    /// an endless far plane; only the depth terms change (as in Brio).
    /// </summary>
    public static Matrix4x4 Projection(Matrix4x4 game, float near, float far)
    {
        game.M33 = -(far + near) / (far - near);
        game.M43 = -(2f * far * near) / (far - near);
        return game;
    }

    /// <summary>
    /// Where <paramref name="point"/> lands on a screen at <paramref name="origin"/> of <paramref name="size"/>
    /// pixels, seen through <paramref name="toClip"/> (model or world to clip space); false behind the camera.
    /// </summary>
    public static bool Project(Vector3 point, Matrix4x4 toClip, Vector2 origin, Vector2 size, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), toClip);
        if (clip.W < 1e-4f)
        {
            screen = default;
            return false;
        }
        screen = origin + new Vector2((clip.X / clip.W + 1) * .5f * size.X, (1 - clip.Y / clip.W) * .5f * size.Y);
        return true;
    }

    /// <summary>
    /// The turn and the move from <paramref name="before"/> to <paramref name="after"/>, two gizmo matrices in
    /// model space: as the character sees them, the turn made on the left of the rotation and the move added.
    /// </summary>
    public static (Quaternion Turn, Vector3 Move) Delta(Matrix4x4 before, Matrix4x4 after)
    {
        var turn = Quaternion.Normalize(Rotation(after) * Quaternion.Inverse(Rotation(before)));
        // One of the two signs of the same turn, kept the same for every drag.
        if (turn.W < 0)
            turn = Quaternion.Negate(turn);
        return (turn, after.Translation - before.Translation);
    }

    /// <summary>The rotation of a gizmo matrix, any scale taken out.</summary>
    public static Quaternion Rotation(Matrix4x4 matrix) =>
        Matrix4x4.Decompose(matrix, out _, out var rotation, out _) ? Quaternion.Normalize(rotation) : Quaternion.Identity;
}
