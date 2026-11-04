using System.Numerics;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Physics;

/// <summary>
/// Builds simulators the way the game's own breast physics is built, for any bones a skeleton has:
/// each bone gets a chain of itself and a tip, its swing cone points where the tip rests, and a spring
/// on the bone's parent pulls the tip toward a point a little beyond its rest place. Everything is
/// measured on the skeleton, so the result fits skeleton mods' bones as well as vanilla's.
/// </summary>
internal static class PhysicsTemplates
{
    /// <summary>
    /// How far out a new area's spring pulls, in tip lengths from the bone along the line to the tip's
    /// rest place: the game's breasts (c0801) pull a 14.5 cm tip toward a point 19.4 cm out.
    /// </summary>
    public const float SpringReach = 1.34f;

    /// <summary> The bones of an area the skeleton has (every chain's for a chain area), or none when the area has no template. </summary>
    public static IReadOnlyList<PhysicsBone> Bones(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        if (kind.Chains != null)
            return Chains(kind, skeleton).SelectMany(chain => chain).ToList();
        if (kind.Template is null)
            return [];
        IEnumerable<string> names = kind.BonePrefix is null ? kind.Bones : skeleton.Names.Where(kind.Owns).Order(StringComparer.Ordinal);
        return names.Select(skeleton.Get).OfType<PhysicsBone>().Where(bone => bone.Parent.Length > 0).ToList();
    }

    /// <summary>
    /// A chain area's chains on the skeleton: from each of its bones whose parent isn't one, down the first
    /// child that is. Only bones whose rest place is known count.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PhysicsBone>> Chains(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        var owned = skeleton.Names.Where(name => kind.Owns(name) && skeleton.Rest(name) is { Parent.Length: > 0 }).Order(StringComparer.Ordinal).ToList();
        var set = owned.ToHashSet(StringComparer.Ordinal);
        var chains = new List<IReadOnlyList<PhysicsBone>>();
        foreach (var root in owned.Where(name => !set.Contains(skeleton.ParentOf(name))))
        {
            var chain = new List<PhysicsBone>();
            for (var bone = root; bone != null; bone = owned.FirstOrDefault(name => skeleton.ParentOf(name) == bone))
                chain.Add(skeleton.Rest(bone)!);
            chains.Add(chain);
        }
        return chains;
    }

    /// <summary>
    /// Areas that can be built whose bones the skeleton has and that nothing moves yet: no simulator of the
    /// file, and none of <paramref name="elsewhere"/>, the bones the character's other physics files move.
    /// </summary>
    public static IReadOnlyList<PhysicsAreaKind> Addable(PhybFile file, PhysicsSkeleton skeleton, IReadOnlySet<string>? elsewhere = null)
    {
        var moved = Moved(file);
        return PhysicsAreas.Kinds.Where(kind => kind.CanBuild && Bones(kind, skeleton) is { Count: > 0 } bones &&
                                                 bones.All(bone => !moved.Contains(bone.Name) && elsewhere?.Contains(bone.Name) != true)).ToList();
    }

    /// <summary> Every bone the file's simulators that are on name in their chains. </summary>
    public static HashSet<string> Moved(PhybFile file)
        => file.Simulators.Where(s => !s.Excluded).SelectMany(s => s.Chains).SelectMany(c => c.Nodes)
            .Where(node => !node.Bone.IsEmpty).Select(node => node.Bone.Text).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The game's own areas that have no template, such as the skirt, tail or earrings, that no simulator
    /// of the file moves yet: indices of <paramref name="game"/>'s simulators, copied as they are.
    /// </summary>
    public static IReadOnlyList<int> AddableFromGame(PhybFile file, PhybFile game)
    {
        var moved = file.Simulators.Where(s => !s.Excluded).SelectMany(PhysicsAreas.RootBones).ToHashSet(StringComparer.Ordinal);
        var result = new List<int>();
        for (var i = 0; i < game.Simulators.Count; i++)
        {
            var info = PhysicsAreas.Classify(game.Simulators[i]);
            if (info.Bones.Count > 0 && info.Kinds.Any(kind => kind.Template is null) && info.Bones.All(bone => !moved.Contains(bone)))
                result.Add(i);
        }
        return result;
    }

    /// <summary> A new simulator for an area, or null when the skeleton lacks its bones. </summary>
    public static PhybSimulator? Build(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        if (kind.Chains != null)
            return BuildChains(kind, skeleton);
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
                Friction = template.Friction,
                CollisionDampening = template.BumpDamping,
                RepulsionStrength = template.Repulsion,
                LastBoneOffset = template.Tip,
                Type = template.Capsules ? 1u : 0u,
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
            // In the parent's space, past the tip's rest place on the line from the bone, as the game's breasts.
            if (template.Firmness != 0)
                simulator.Attracts.Add(new PhybAttract
                {
                    Bone = PhybName.From(bone.Parent),
                    Offset = bone.Position + rest * SpringReach,
                    Chain = index,
                    Node = 1,
                    Stiffness = template.Firmness,
                });
        }
        return simulator;
    }

    /// <summary>
    /// A chain area's simulator: a chain per <see cref="Chains"/>, each bone's swing measured from the line to
    /// the next point, then the tip. Hair takes the hair skeleton presets of where each chain hangs.
    /// </summary>
    private static PhybSimulator? BuildChains(PhysicsAreaKind kind, PhysicsSkeleton skeleton)
    {
        var chains = Chains(kind, skeleton);
        if (chains.Count == 0)
            return null;
        var rest = kind.Part == PhysicsPart.Hair ? PhysicsBody.RestPose(skeleton) : null;
        var simulator = new PhybSimulator { Group = kind.Group };
        foreach (var bones in chains)
        {
            var template = rest != null ? HairChains(HairPlacement(bones[0], rest)) : kind.Chains!;
            // Each bone's next point in its own space: the next bone, then the tip along the last bone's X axis.
            var tipLength = template.TipLength > 0 ? template.TipLength : bones.Count > 1 ? bones[^1].Position.Length() : 0.05f;
            var tip = new Vector3(tipLength, 0, 0);
            var steps = bones.Skip(1).Select(bone => bone.Position).Append(tip).ToList();
            var length = steps.Sum(step => step.Length());
            var scale = template.ReferenceLength > 0 && length > 0 ? Math.Clamp(template.ReferenceLength / length, 0.5f, 1f) : 1f;
            var chain = new PhybChain
            {
                Dampening = template.Damping,
                MaxSpeed = template.MaxSpeed,
                RepulsionStrength = 1f,
                LastBoneOffset = tip,
                Type = 0,
            };
            for (var i = 0; i < bones.Count; i++)
            {
                // In the space of the bone's parent, where the game measures the swing.
                var axis = Vector3.Transform(steps[i], Quaternion.Normalize(bones[i].Rotation));
                chain.Nodes.Add(Node(PhybName.From(bones[i].Name), i == 0 ? template.Root : template.Middle, scale,
                    axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitX));
            }
            chain.Nodes.Add(Node(PhybName.Empty, template.Tip, scale, Vector3.UnitX));
            simulator.Chains.Add(chain);
        }
        return simulator;
    }

    private static PhybNode Node(PhybName bone, PhysicsNodeTemplate values, float scale, Vector3 axis) => new()
    {
        Bone = bone,
        Radius = values.Radius,
        AttractByAnimation = values.Follow,
        WindScale = values.Wind,
        GravityScale = values.Gravity,
        // A 180 degree swing is free and stays so.
        ConeMaxAngle = values.SwingDegrees >= 180 ? MathF.PI : Radians(values.SwingDegrees * scale),
        ConeAxisOffset = axis,
    };

    /// <summary>
    /// Where a hair chain hangs, from its top bone's place relative to the head (model +Z is the front):
    /// "bangs" in front, "back" behind, "side" between. Back when the head's rest place isn't known.
    /// </summary>
    internal static string HairPlacement(PhysicsBone root, IReadOnlyDictionary<string, PhysicsFrame> rest)
    {
        if (!rest.TryGetValue(root.Parent, out var head))
            return "back";
        var offset = head.Turn(root.Position);
        return offset.Z > 0.04f ? "bangs" : offset.Z < -0.04f ? "back" : "side";
    }

    /// <summary> A hair chain kind's starting values, the ones Instant Edit's hair skeletons get. </summary>
    public static PhysicsChainTemplate HairChains(string kind)
    {
        var preset = HairPhysicsPresets.For(kind);
        static PhysicsNodeTemplate Values(HairNodePreset node) => new(node.Radius, node.AttractByAnimation, node.WindScale, node.GravityScale, node.ConeDegrees);
        return new PhysicsChainTemplate(preset.Dampening, Values(preset.Root), Values(preset.Middle), Values(preset.Tip),
            MaxSpeed: HairPhysicsPresets.MaxSpeed, ReferenceLength: preset.ReferenceLength);
    }

    /// <summary>
    /// A spring for a chain that has none: pulls its tip toward where it rests, anchored past that
    /// place as the game's breasts are (<see cref="SpringReach"/>). Null when the skeleton lacks the chain's bone.
    /// </summary>
    public static PhybAttract? RestSpring(PhybChain chain, short chainIndex, PhysicsSkeleton skeleton, float stiffness)
    {
        if (chain.Nodes.Count < 2 || skeleton.Get(chain.Nodes[0].Bone.Text) is not { Parent.Length: > 0 } bone)
            return null;
        // Only a tip right after the bone hangs straight from it, whatever points follow; longer chains keep their own springs.
        if (PhysicsKnobs.TipIndex(chain) != 1)
            return null;
        var rest = Vector3.Transform(chain.LastBoneOffset, Quaternion.Normalize(bone.Rotation));
        return new PhybAttract
        {
            Bone = PhybName.From(bone.Parent),
            Offset = bone.Position + rest * SpringReach,
            Chain = chainIndex,
            Node = 1,
            Stiffness = stiffness,
        };
    }

    public static float Radians(float degrees) => degrees * MathF.PI / 180f;
    public static float Degrees(float radians) => radians * 180f / MathF.PI;
}
