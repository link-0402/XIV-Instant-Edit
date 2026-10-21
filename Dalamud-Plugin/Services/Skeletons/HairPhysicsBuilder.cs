using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Physics;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// The physics file of a custom hair skeleton: one simulator per chain kind (side hair per side), each
/// chain's bones as nodes followed by an unnamed tip, values from the kind's preset, and the collision
/// shapes vanilla hair of that kind uses, copied from the race's h0182 physics.
/// </summary>
internal static class HairPhysicsBuilder
{
    // Simulator order; Left only matters for side hair.
    private static readonly (string Kind, bool Left)[] Simulators =
        [("bangs", true), ("back", true), ("ponytail", true), ("side", true), ("side", false)];

    /// <summary><c>chara/human/c0801/skeleton/hair/h4321/phy_c0801h4321.phyb</c>; the mod-relative path is the same.</summary>
    public static string PhysicsPath(int genderRace, int skeletonId)
        => $"chara/human/c{genderRace:D4}/skeleton/hair/h{skeletonId:D4}/phy_c{genderRace:D4}h{skeletonId:D4}.phyb";

    /// <summary>
    /// Builds the physics for a validated rig. <paramref name="bones"/> is <see cref="HairSkeletonLayout.Bones"/>'s
    /// output. Without chains the file turns physics off. Without <paramref name="donor"/> nothing collides;
    /// a shape the donor lacks is left out with a note. <paramref name="previous"/> is the file the last export
    /// wrote: a chain over the same bones in the same group keeps its tuning (axes and tip offset are rebuilt),
    /// its simulator keeps that simulator's settings, and a copied shape keeps the previous file's shape of
    /// the same name and type. <paramref name="previousKinds"/> are the chain kinds that export recorded, by
    /// first bone; when given, a chain keeps its tuning only if its kind is unchanged.
    /// </summary>
    public static PhybFile Build(HairSkeletonRequest rig, ImmutableArray<SkeletonBone> bones, PhybFile? donor, PhybFile? previous,
        ICollection<string> notes, IReadOnlyDictionary<string, string>? previousKinds = null)
    {
        if (rig.Chains is not { Count: > 0 } chains)
            return new PhybFile { Version = 0, DataType = 0 };
        var model = HairSkeletonLayout.ModelSpace(bones);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < bones.Length; i++)
            index[bones[i].Name] = i;
        // Game +X is the character's left.
        bool IsLeft(HairSkeletonChainRequest chain) => model[index[chain.Bones![0]]].Position.X >= 0;

        var file = new PhybFile();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var kept = 0;
        foreach (var (kind, left) in Simulators)
        {
            var members = chains.Where(chain => chain.Kind == kind && (kind != "side" || IsLeft(chain) == left)).ToList();
            if (members.Count == 0)
                continue;
            var simulator = new PhybSimulator { Group = HairPhysicsPresets.Group(kind, left) };
            if (donor is not null)
                foreach (var name in HairPhysicsPresets.Shapes(kind, left))
                {
                    if (CopyShape(donor.Collision, previous?.Collision, file.Collision, name))
                        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From(name), Type = PhybCollisionType.Outside });
                    else if (missing.Add(name))
                        notes.Add($"The game's hair physics has no {name}, so the hair doesn't collide with it.");
                }
            var flags = (1u << simulator.Collisions.Count) - 1;
            var preset = HairPhysicsPresets.For(kind);
            PhybSimulator? tuned = null;
            foreach (var chain in members)
            {
                var built = Chain(chain, preset, bones, model, index, flags);
                if (Previous(previous, simulator.Group, chain, previousKinds) is (var owner, var old))
                {
                    // Collision bits index the simulator's refs, so they only carry over to the same refs.
                    var sameRefs = owner.Collisions.Select(c => c.Name).SequenceEqual(simulator.Collisions.Select(c => c.Name));
                    Keep(old, built, sameRefs);
                    tuned ??= owner;
                    kept++;
                }
                simulator.Chains.Add(built);
            }
            if (tuned is not null)
            {
                simulator.Gravity = tuned.Gravity;
                simulator.Wind = tuned.Wind;
                simulator.ConstraintLoop = tuned.ConstraintLoop;
                simulator.CollisionLoop = tuned.CollisionLoop;
                simulator.Flags = tuned.Flags;
            }
            file.Simulators.Add(simulator);
        }
        if (kept > 0)
            notes.Add($"Kept your physics settings for {kept} of {file.Simulators.Sum(s => s.Chains.Count)} chains.");
        file.DataType = file.Collision.IsEmpty ? 2u : 3u;
        return file;
    }

    /// <summary>
    /// The previous file's chain over the same bones in a simulator of that group, with its simulator; none
    /// when <paramref name="kinds"/> are recorded and name another kind for the chain's first bone.
    /// </summary>
    private static (PhybSimulator Simulator, PhybChain Chain)? Previous(PhybFile? previous, byte group,
        HairSkeletonChainRequest request, IReadOnlyDictionary<string, string>? kinds)
    {
        if (previous is null)
            return null;
        // Back and ponytail share a group, so only the recorded kind tells a changed kind apart.
        if (kinds is not null && (!kinds.TryGetValue(request.Bones![0], out var kind) || kind != request.Kind))
            return null;
        foreach (var simulator in previous.Simulators.Where(s => s.Group == group))
            foreach (var chain in simulator.Chains)
                if (chain.Nodes.Where(n => !n.Bone.IsEmpty).Select(n => n.Bone.Text).SequenceEqual(request.Bones!, StringComparer.Ordinal))
                    return (simulator, chain);
        return null;
    }

    /// <summary>
    /// Copies the tuning of a previous chain over the same bones; axes and the tip offset stay the rig's,
    /// and collision bits stay the fresh ones unless <paramref name="sameRefs"/>.
    /// </summary>
    private static void Keep(PhybChain old, PhybChain chain, bool sameRefs)
    {
        chain.Dampening = old.Dampening;
        chain.MaxSpeed = old.MaxSpeed;
        chain.Friction = old.Friction;
        chain.CollisionDampening = old.CollisionDampening;
        chain.RepulsionStrength = old.RepulsionStrength;
        chain.Type = old.Type;
        var named = old.Nodes.Where(n => !n.Bone.IsEmpty).ToList();
        for (var i = 0; i < named.Count; i++)
            Keep(named[i], chain.Nodes[i], sameRefs);
        if (old.Nodes[^1].Bone.IsEmpty)
            Keep(old.Nodes[^1], chain.Nodes[^1], sameRefs);
    }

    private static void Keep(PhybNode old, PhybNode node, bool sameRefs)
    {
        node.Radius = old.Radius;
        node.AttractByAnimation = old.AttractByAnimation;
        node.WindScale = old.WindScale;
        node.GravityScale = old.GravityScale;
        node.ConeMaxAngle = old.ConeMaxAngle;
        node.ConstraintPlaneNormal = old.ConstraintPlaneNormal;
        if (!sameRefs)
            return;
        node.CollisionFlags = old.CollisionFlags;
        node.ContinuousCollisionFlags = old.ContinuousCollisionFlags;
    }

    /// <summary>
    /// Nodes for the chain's bones, each with its cone axis pointing at the next node in its parent's
    /// space, then the tip. Cones narrow on chains longer than the preset's reference length.
    /// </summary>
    private static PhybChain Chain(HairSkeletonChainRequest request, HairPhysicsPreset preset, ImmutableArray<SkeletonBone> bones,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation)> model, Dictionary<string, int> index, uint flags)
    {
        var path = request.Bones!.Select(name => index[name]).ToList();
        var tip = new Vector3(request.Tip![0], request.Tip[1], request.Tip[2]);
        var points = path.Select(i => model[i].Position).Append(tip).ToList();
        var length = 0f;
        for (var i = 1; i < points.Count; i++)
            length += Vector3.Distance(points[i - 1], points[i]);
        var scale = Math.Clamp(preset.ReferenceLength / length, 0.5f, 1f);

        var last = model[path[^1]];
        var chain = new PhybChain
        {
            Dampening = preset.Dampening,
            MaxSpeed = HairPhysicsPresets.MaxSpeed,
            Friction = HairPhysicsPresets.Friction,
            CollisionDampening = HairPhysicsPresets.CollisionDampening,
            RepulsionStrength = HairPhysicsPresets.RepulsionStrength,
            LastBoneOffset = Vector3.Transform(tip - last.Position, Quaternion.Conjugate(last.Rotation)),
            Type = 0,
        };
        for (var i = 0; i < path.Count; i++)
        {
            var bone = bones[path[i]];
            var axis = Vector3.Transform(points[i + 1] - points[i], Quaternion.Conjugate(model[bone.Parent].Rotation));
            // Validation keeps chain bones 1 mm apart; a shorter step has no direction, and Normalize would give NaN.
            axis = axis.Length() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(axis);
            chain.Nodes.Add(Node(PhybName.From(bone.Name), i == 0 ? preset.Root : preset.Middle, scale, axis, flags));
        }
        chain.Nodes.Add(Node(PhybName.Empty, preset.Tip, scale, Vector3.UnitX, flags));
        return chain;
    }

    private static PhybNode Node(PhybName bone, HairNodePreset preset, float scale, Vector3 axis, uint flags) => new()
    {
        Bone = bone,
        Radius = preset.Radius,
        AttractByAnimation = preset.AttractByAnimation,
        WindScale = preset.WindScale,
        GravityScale = preset.GravityScale,
        // A 180 degree cone is a free swing and stays one.
        ConeMaxAngle = preset.ConeDegrees >= 180 ? MathF.PI : PhysicsTemplates.Radians(preset.ConeDegrees * scale),
        ConeAxisOffset = axis,
        CollisionFlags = flags,
        ContinuousCollisionFlags = 0,
    };

    /// <summary>
    /// Copies the donor's capsule, sphere or plane of that name once, or the previous file's shape of that
    /// name and type when it has one; false when the donor has none.
    /// </summary>
    private static bool CopyShape(PhybCollision donor, PhybCollision? previous, PhybCollision target, string name)
    {
        bool Named(PhybName shape) => shape.Text == name;
        if (target.Names.Any(Named))
            return true;
        if (donor.Capsules.FirstOrDefault(s => Named(s.Name)) is { } capsule)
            target.Capsules.Add((previous?.Capsules.FirstOrDefault(s => Named(s.Name)) ?? capsule).Clone());
        else if (donor.Spheres.FirstOrDefault(s => Named(s.Name)) is { } sphere)
            target.Spheres.Add((previous?.Spheres.FirstOrDefault(s => Named(s.Name)) ?? sphere).Clone());
        else if (donor.NormalPlanes.FirstOrDefault(s => Named(s.Name)) is { } plane)
            target.NormalPlanes.Add((previous?.NormalPlanes.FirstOrDefault(s => Named(s.Name)) ?? plane).Clone());
        else
            return false;
        return true;
    }
}
