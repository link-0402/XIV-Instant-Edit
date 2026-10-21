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
    /// a shape the donor lacks is left out with a note. <paramref name="previous"/> isn't used yet.
    /// </summary>
    public static PhybFile Build(HairSkeletonRequest rig, ImmutableArray<SkeletonBone> bones, PhybFile? donor, PhybFile? previous,
        ICollection<string> notes)
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
        foreach (var (kind, left) in Simulators)
        {
            var members = chains.Where(chain => chain.Kind == kind && (kind != "side" || IsLeft(chain) == left)).ToList();
            if (members.Count == 0)
                continue;
            var simulator = new PhybSimulator { Group = HairPhysicsPresets.Group(kind, left) };
            if (donor is not null)
                foreach (var name in HairPhysicsPresets.Shapes(kind, left))
                {
                    if (CopyShape(donor.Collision, file.Collision, name))
                        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From(name), Type = PhybCollisionType.Outside });
                    else if (missing.Add(name))
                        notes.Add($"The game's hair physics has no {name}, so the hair doesn't collide with it.");
                }
            var flags = (1u << simulator.Collisions.Count) - 1;
            var preset = HairPhysicsPresets.For(kind);
            foreach (var chain in members)
                simulator.Chains.Add(Chain(chain, preset, bones, model, index, flags));
            file.Simulators.Add(simulator);
        }
        file.DataType = file.Collision.IsEmpty ? 2u : 3u;
        return file;
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
            chain.Nodes.Add(Node(PhybName.From(bone.Name), i == 0 ? preset.Root : preset.Middle, scale, Vector3.Normalize(axis), flags));
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

    /// <summary>Copies the donor's capsule, sphere or plane of that name once; false when the donor has none.</summary>
    private static bool CopyShape(PhybCollision donor, PhybCollision target, string name)
    {
        bool Named(PhybName shape) => shape.Text == name;
        if (target.Names.Any(Named))
            return true;
        if (donor.Capsules.FirstOrDefault(s => Named(s.Name)) is { } capsule)
            target.Capsules.Add(capsule.Clone());
        else if (donor.Spheres.FirstOrDefault(s => Named(s.Name)) is { } sphere)
            target.Spheres.Add(sphere.Clone());
        else if (donor.NormalPlanes.FirstOrDefault(s => Named(s.Name)) is { } plane)
            target.NormalPlanes.Add(plane.Clone());
        else
            return false;
        return true;
    }
}
