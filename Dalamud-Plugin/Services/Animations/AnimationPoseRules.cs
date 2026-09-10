using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal static class AnimationPoseRules
{
    public static PoseStack Filter(PoseStack stack, PoseComponents components) => stack with
    {
        Position = components.HasFlag(PoseComponents.Position) ? stack.Position : Vector3.Zero,
        Rotation = components.HasFlag(PoseComponents.Rotation) ? stack.Rotation : Quaternion.Identity,
        Scale = components.HasFlag(PoseComponents.Scale) ? stack.Scale : Vector3.Zero,
        Ik = stack.Ik with { Enabled = stack.Ik.Enabled && components.HasFlag(PoseComponents.Position) },
    };

    public static bool Empty(PoseStack s) => s.Position.LengthSquared() < 1e-16f &&
        s.Scale.LengthSquared() < 1e-16f && Math.Abs(Quaternion.Dot(s.Rotation, Quaternion.Identity)) > 1 - 1e-7f && !s.Ik.Enabled;

    public static bool Same(PoseSnapshot a, PoseSnapshot b) => a.MainTimeline == b.MainTimeline &&
        a.UpperTimeline == b.UpperTimeline && a.FaceTimeline == b.FaceTimeline && a.Global == b.Global &&
        a.AdapterIdentity == b.AdapterIdentity && a.Bones.Length == b.Bones.Length &&
        a.Bones.Zip(b.Bones).All(p => p.First.Id == p.Second.Id && p.First.Face == p.Second.Face &&
            p.First.Stacks.SequenceEqual(p.Second.Stacks) &&
            (p.First.IkChain.IsDefault && p.Second.IkChain.IsDefault || !p.First.IkChain.IsDefault && !p.Second.IkChain.IsDefault && p.First.IkChain.SequenceEqual(p.Second.IkChain)));

    public static PoseSnapshot RemoveBaked(PoseSnapshot pose, AnimationBakeRequest request) => pose with
    {
        Bones = pose.Bones.Select(b => !request.SelectedBones.Contains(b.Id) ? b : b with
        {
            Stacks = b.Stacks.Select(s => (Original: s, Remaining: Filter(s, PoseComponents.All & ~request.Components)))
                .Where(s => s.Original == s.Remaining || !Empty(s.Remaining)).Select(s => s.Remaining).ToImmutableArray(),
        }).Where(b => b.Stacks.Length != 0).ToImmutableArray(),
    };

    public static void Validate(PoseStack s)
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(s.Position) || !Finite(s.Scale) || !float.IsFinite(s.Rotation.LengthSquared()) ||
            s.Rotation.LengthSquared() < 0.0001f || !Finite(s.Ik.Axis) || (s.Propagate & ~PoseComponents.All) != 0)
            throw new InvalidDataException("LivePose contains a non-finite or unsupported transform.");
        if (s.Ik.Enabled && (s.Ik.Type is < 0 or > 1 || s.Ik.Depth is < 0 or > 512 ||
                s.Ik.Iterations is < 0 or > 1000 || s.Ik.First is < 0 or > 512 ||
                s.Ik.Second is < 0 or > 512 || s.Ik.End is < 0 or > 512))
            throw new InvalidDataException("LivePose contains unsupported IK parameters.");
    }

    public static int SampleCount(float duration, int sourceFrames)
    {
        if (!float.IsFinite(duration) || duration < 0 || duration > 3600)
            throw new InvalidDataException("Animation duration is invalid or exceeds one hour.");
        var count = Math.Max(2, Math.Max(sourceFrames, checked((int)Math.Ceiling(duration * 30d) + 1)));
        if (count > 216001) throw new InvalidDataException("Animation sampling exceeds the frame limit.");
        return count;
    }
}
