using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Left and right on a skeleton: a bone's twin and side from its name, the bone above both twins,
/// and rotations and positions mirrored across the character's middle. Shared by the Bones window's
/// Mirror and by symmetry layers.
/// </summary>
internal static class MotionMirror
{
    /// <summary>The bone's twin on the other side ("_l" and "_r" endings), or null for a bone in the middle.</summary>
    public static string? Twin(string bone) =>
        bone.Length > 2 && bone[^2] == '_' ? bone[^1] switch { 'l' => bone[..^1] + "r", 'r' => bone[..^1] + "l", _ => null } : null;

    /// <summary>Which side a bone is on: 'l', 'r', or '\0' in the middle.</summary>
    public static char Side(string bone) => Twin(bone) == null ? '\0' : bone[^1];

    /// <summary>The nearest bone above both <paramref name="a"/> and <paramref name="b"/>; -1 when they share none.</summary>
    public static int CommonAncestor(SkeletonDescription skeleton, int a, int b)
    {
        // Climb from both parents to the same depth, then together until they meet.
        var bones = skeleton.Bones;
        int x = bones[a].Parent, y = bones[b].Parent;
        int Depth(int bone)
        {
            var depth = 0;
            for (; bone >= 0; bone = bones[bone].Parent)
                depth++;
            return depth;
        }
        int dx = Depth(x), dy = Depth(y);
        for (; dx > dy; dx--) x = bones[x].Parent;
        for (; dy > dx; dy--) y = bones[y].Parent;
        while (x != y)
        {
            x = bones[x].Parent;
            y = bones[y].Parent;
        }
        return x;
    }

    /// <summary>A rotation mirrored across the character's middle at rest (its YZ plane): turns about X stay, about Y and Z reverse.</summary>
    public static Quaternion Rotation(Quaternion rotation) => new(rotation.X, -rotation.Y, -rotation.Z, rotation.W);

    /// <summary>A position or move mirrored across the character's middle at rest.</summary>
    public static Vector3 Position(Vector3 position) => new(-position.X, position.Y, position.Z);

    /// <summary>A turn in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Quaternion Rotation(Quaternion rotation, Quaternion body) =>
        Quaternion.Normalize(body * Rotation(Quaternion.Inverse(body) * rotation * body) * Quaternion.Inverse(body));

    /// <summary>A move in character space mirrored across the middle of a body turned <paramref name="body"/> from rest.</summary>
    public static Vector3 Position(Vector3 position, Quaternion body) =>
        Vector3.Transform(Position(Vector3.Transform(position, Quaternion.Inverse(body))), body);
}
