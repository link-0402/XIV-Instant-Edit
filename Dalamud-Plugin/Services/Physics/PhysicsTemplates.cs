using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// Builds simulators the way the game's own breast physics is built, for any bones a skeleton has:
/// each bone gets a chain of itself and a tip, its swing cone points where the tip rests, and a spring
/// on the bone's parent pulls the tip back to its rest place. Everything is measured on the skeleton,
/// so the result fits skeleton mods' bones as well as vanilla's.
/// </summary>
internal static class PhysicsTemplates
{
    /// <summary> The bones of an area the skeleton has, or none when the area has no template. </summary>
    public static IReadOnlyList<PhysicsBone> Bones(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
        => kind.Template is null ? [] : kind.Bones.Select(skeleton.Get).OfType<PhysicsBone>().Where(bone => bone.Parent.Length > 0).ToList();

    /// <summary> Areas with a template whose bones the skeleton has and no simulator of the file moves yet. </summary>
    public static IReadOnlyList<PhysicsAreaKind> Addable(PhybFile file, PhysicsSkeleton skeleton)
    {
        var moved = file.Simulators.Where(s => !s.Excluded).SelectMany(PhysicsAreas.RootBones).ToHashSet(StringComparer.Ordinal);
        return PhysicsAreas.Kinds.Where(kind => kind.Template != null && Bones(kind, skeleton) is { Count: > 0 } bones &&
                                                 bones.All(bone => !moved.Contains(bone.Name))).ToList();
    }

    /// <summary> A new simulator for an area, or null when the skeleton lacks its bones. </summary>
    public static PhybSimulator? Build(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        if (kind.Template is not { } template)
            return null;
        var bones = Bones(kind, skeleton);
        if (bones.Count == 0)
            return null;
        var simulator = new PhybSimulator { Group = kind.Group };
        foreach (var bone in bones)
        {
            var index = (short)simulator.Chains.Count;
            var rest = Vector3.Transform(template.Tip, Quaternion.Normalize(bone.Rotation));
            var axis = rest.LengthSquared() > 1e-12f ? Vector3.Normalize(rest) * 0.1f : Vector3.UnitX * 0.1f;
            simulator.Chains.Add(new PhybChain
            {
                Dampening = template.Damping,
                MaxSpeed = template.MaxSpeed,
                RepulsionStrength = 1f,
                LastBoneOffset = template.Tip,
                Type = 0,
                Nodes =
                [
                    new PhybNode
                    {
                        Bone = PhybName.From(bone.Name),
                        GravityScale = 1f,
                        ConeMaxAngle = Radians(template.SwingDegrees),
                        ConeAxisOffset = axis,
                    },
                    new PhybNode
                    {
                        Radius = template.TipRadius,
                        AttractByAnimation = template.FollowAnimation,
                        WindScale = template.TipWind,
                        GravityScale = template.TipGravity,
                        ConeMaxAngle = MathF.PI,
                        ConeAxisOffset = Vector3.UnitX,
                    },
                ],
            });
            // The tip's rest place in the parent's space: where the spring pulls it back to.
            simulator.Attracts.Add(new PhybAttract
            {
                Bone = PhybName.From(bone.Parent),
                Offset = bone.Position + rest,
                Chain = index,
                Node = 1,
                Stiffness = template.Firmness,
            });
        }
        return simulator;
    }

    /// <summary>
    /// A spring for a chain that has none: pulls its tip back to where it rests, measured on the
    /// skeleton. Null when the skeleton lacks the chain's bone.
    /// </summary>
    public static PhybAttract? RestSpring(PhybChain chain, short chainIndex, PhysicsSkeleton skeleton, float stiffness)
    {
        if (chain.Nodes.Count < 2 || skeleton.Get(chain.Nodes[0].Bone.Text) is not { Parent.Length: > 0 } bone)
            return null;
        // Only a two-node chain's tip hangs straight from the bone; longer chains keep their own springs.
        if (chain.Nodes.Count != 2)
            return null;
        var rest = Vector3.Transform(chain.LastBoneOffset, Quaternion.Normalize(bone.Rotation));
        return new PhybAttract
        {
            Bone = PhybName.From(bone.Parent),
            Offset = bone.Position + rest,
            Chain = chainIndex,
            Node = 1,
            Stiffness = stiffness,
        };
    }

    public static float Radians(float degrees) => degrees * MathF.PI / 180f;
    public static float Degrees(float radians) => radians * 180f / MathF.PI;
}
