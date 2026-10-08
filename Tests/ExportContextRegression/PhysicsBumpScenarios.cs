using InstantEdit.Services.Physics;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Which shapes chains bump into, as the game reads a point's bits: the simulator's shapes, then the
/// chain's own. The builder's "Collision with" list shows that, and ticking or unticking a shape changes
/// only the chosen chains' bits, renumbering every chain's bits when a shape joins or leaves a list.
/// </summary>
internal static class PhysicsBumpScenarios
{
    public static void Run()
    {
        CheckState();
        CheckAdd();
        CheckRemove();
        CheckChainShapes();
        CheckLimit();
        CheckBrokenLinks();
        CheckNames();
    }

    private static PhybCollisionRef Ref(string name) => new() { Name = PhybName.From(name), Type = PhybCollisionType.Outside };

    private static PhybChain Chain(string bone, params uint[] flags)
    {
        var chain = new PhybChain { Nodes = [new PhybNode { Bone = PhybName.From(bone) }] };
        foreach (var bits in flags)
            chain.Nodes.Add(new PhybNode { CollisionFlags = bits });
        return chain;
    }

    /// <summary> The game's skirt: left chains bump into the left thigh (bit 3), right chains into the right one (bit 2). </summary>
    private static PhybFile Skirt()
    {
        var file = new PhybFile();
        foreach (var name in new[] { "asi_r_c", "asi_l_c", "asi_r_a", "asi_l_a" })
            file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From(name), StartBone = PhybName.From("j_asi_a_l"), EndBone = PhybName.From("j_asi_a_l") });
        var simulator = new PhybSimulator { Group = 2, Collisions = [Ref("asi_r_c"), Ref("asi_l_c"), Ref("asi_r_a"), Ref("asi_l_a")] };
        simulator.Chains.Add(Chain("j_sk_f_a_l", 8, 8, 8));
        simulator.Chains.Add(Chain("j_sk_f_a_r", 4, 4, 4));
        file.Simulators.Add(simulator);
        return file;
    }

    /// <summary> Tiramisu's chest: six arm shapes listed, and points whose bits (300) pick the hands and the right elbow. </summary>
    private static PhybFile Chest()
    {
        var file = new PhybFile();
        string[] names = ["forearm_l", "forearm_r", "hand_l", "hand_r", "elbow_l", "elbow_r", "upper_l"];
        foreach (var name in names)
            file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From(name), StartBone = PhybName.From("j_te_l"), EndBone = PhybName.From("j_ude_b_l") });
        var simulator = new PhybSimulator { Group = 1, Collisions = names[..6].Select(Ref).ToList() };
        simulator.Chains.Add(Chain("j_mune_l", 300));
        simulator.Chains.Add(Chain("j_mune_r", 300));
        file.Simulators.Add(simulator);
        return file;
    }

    private static void CheckState()
    {
        var skirt = Skirt();
        Require(PhysicsCollisions.State(skirt, [(0, 0)], "asi_l_a") == PhysicsBumps.All && PhysicsCollisions.State(skirt, [(0, 0)], "asi_r_a") == PhysicsBumps.None &&
                PhysicsCollisions.State(skirt, [(0, 0), (0, 1)], "asi_l_a") == PhysicsBumps.Some,
            "physics bumps: the game's left skirt bumps into the left thigh only, so both sides together bump into each thigh partly");
        var chest = Chest();
        Require(PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "hand_l") == PhysicsBumps.All &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "elbow_r") == PhysicsBumps.All &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "forearm_l") == PhysicsBumps.None &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "elbow_l") == PhysicsBumps.None,
            "physics bumps: a shape the simulator lists but no point's bit picks isn't bumped into");
    }

    private static void CheckAdd()
    {
        var chest = Chest();
        chest.Simulators[0].Chains[0].Nodes[1].ContinuousCollisionFlags = 300;
        Require(PhysicsCollisions.Add(chest, [(0, 0)], "forearm_l") && PhysicsCollisions.State(chest, [(0, 0)], "forearm_l") == PhysicsBumps.All &&
                PhysicsCollisions.State(chest, [(0, 1)], "forearm_l") == PhysicsBumps.None &&
                chest.Simulators[0].Chains[0].Nodes[1].CollisionFlags == 301 && chest.Simulators[0].Chains[0].Nodes[1].ContinuousCollisionFlags == 301,
            "physics bumps: ticking a shape sets its bit on the chosen chains only, continuous bits included where the point has them");
        Require(PhysicsCollisions.Add(chest, [(0, 0), (0, 1)], "upper_l") && chest.Simulators[0].Collisions.Count == 7 &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "upper_l") == PhysicsBumps.All &&
                (chest.Simulators[0].Chains[1].Nodes[1].CollisionFlags & ~0b1111111u) == 0 &&
                chest.Simulators[0].Flags.HasFlag(PhybSimulatorFlags.CollisionsHandled),
            "physics bumps: ticking a shape the area doesn't list adds it, and bits that named nothing are dropped");
    }

    private static void CheckRemove()
    {
        var chest = Chest();
        PhysicsCollisions.Remove(chest, [(0, 0), (0, 1)], "hand_l");
        var simulator = chest.Simulators[0];
        Require(simulator.Collisions.Select(r => r.Name.Text).SequenceEqual(["forearm_l", "forearm_r", "hand_r", "elbow_l", "elbow_r"]) &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "hand_r") == PhysicsBumps.All &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "elbow_r") == PhysicsBumps.All &&
                PhysicsCollisions.State(chest, [(0, 0), (0, 1)], "hand_l") == PhysicsBumps.None,
            "physics bumps: a shape no point bumps into any more leaves the list, and the shapes after it keep their points");
        var skirt = Skirt();
        PhysicsCollisions.Remove(skirt, [(0, 0)], "asi_l_a");
        Require(skirt.Simulators[0].Collisions.Count == 3 && PhysicsCollisions.State(skirt, [(0, 1)], "asi_r_a") == PhysicsBumps.All &&
                PhysicsCollisions.State(skirt, [(0, 0)], "asi_l_a") == PhysicsBumps.None,
            "physics bumps: unticking a shape on one chain leaves the other chains' bits alone");
        PhysicsCollisions.Remove(skirt, [(0, 1)], "asi_r_a");
        Require(skirt.Simulators[0].Collisions.Select(r => r.Name.Text).SequenceEqual(["asi_r_c", "asi_l_c"]),
            "physics bumps: shapes no point uses any more are dropped from the list");
    }

    private static void CheckChainShapes()
    {
        // The game's cloak (t6190): the simulator lists a sphere and an ellipsoid, the chain its own ellipsoid, at bit 2.
        var file = new PhybFile();
        var simulator = new PhybSimulator { Collisions = [Ref("cloak_sphere"), Ref("cloak_ellipsoid")] };
        var chain = Chain("j_ex_top_e_l", 6);
        chain.Collisions.Add(Ref("cloak_own"));
        simulator.Chains.Add(chain);
        simulator.Chains.Add(Chain("j_ex_top_e_r", 1));
        file.Simulators.Add(simulator);
        Require(PhysicsCollisions.State(file, [(0, 0)], "cloak_own") == PhysicsBumps.All && PhysicsCollisions.State(file, [(0, 0)], "cloak_sphere") == PhysicsBumps.None,
            "physics bumps: bits past the simulator's shapes name the chain's own");
        PhysicsCollisions.Add(file, [(0, 1)], "arm");
        Require(simulator.Collisions.Count == 3 && chain.Nodes[1].CollisionFlags == 0b1010 &&
                PhysicsCollisions.State(file, [(0, 0)], "cloak_own") == PhysicsBumps.All && PhysicsCollisions.State(file, [(0, 0)], "arm") == PhysicsBumps.None,
            "physics bumps: a new simulator shape moves each chain's own shapes up a bit");
        PhysicsCollisions.Remove(file, [(0, 0)], "cloak_own");
        Require(chain.Collisions.Count == 0 && chain.Nodes[1].CollisionFlags == 0b10,
            "physics bumps: a chain's own shape no point uses any more leaves the chain");
    }

    private static void CheckLimit()
    {
        var file = new PhybFile();
        var simulator = new PhybSimulator { Collisions = Enumerable.Range(0, 32).Select(i => Ref($"shape_{i}")).ToList() };
        simulator.Chains.Add(Chain("j_mune_l", 0));
        file.Simulators.Add(simulator);
        Require(!PhysicsCollisions.Add(file, [(0, 0)], "one_more") && simulator.Collisions.Count == 32 && simulator.Chains[0].Nodes[1].CollisionFlags == 0 &&
                PhysicsCollisions.Add(file, [(0, 0)], "shape_31") && simulator.Chains[0].Nodes[1].CollisionFlags == 1u << 31,
            "physics bumps: a chain can bump into 32 shapes; a 33rd is refused without changing anything");
    }

    private static void CheckBrokenLinks()
    {
        var file = new PhybFile();
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("kept"), StartBone = PhybName.From("j_te_l"), EndBone = PhybName.From("j_te_l") });
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("own"), StartBone = PhybName.From("j_te_l"), EndBone = PhybName.From("j_te_l") });
        var simulator = new PhybSimulator { Collisions = [Ref("gone"), Ref("kept")] };
        var chain = Chain("j_mune_l", 0b110);
        chain.Collisions.Add(Ref("own"));
        simulator.Chains.Add(chain);
        file.Simulators.Add(simulator);
        PhysicsValidation.Apply(file, PhysicsFix.RemoveBroken, 0);
        Require(simulator.Collisions.Single().Name.Text == "kept" && chain.Collisions.Single().Name.Text == "own" && chain.Nodes[1].CollisionFlags == 0b11 &&
                PhysicsCollisions.State(file, [(0, 0)], "own") == PhysicsBumps.All,
            "physics bumps: removing broken links keeps the chain's own shapes on their renumbered bits");
    }

    /// <summary> The list names each shape by the body part it covers, followed by the shape's own name. </summary>
    private static void CheckNames()
    {
        var file = new PhybFile();
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("forearm_l"), StartBone = PhybName.From("j_te_l"), EndBone = PhybName.From("j_ude_b_l") });
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("asi_r_a"), StartBone = PhybName.From("j_asi_a_r"), EndBone = PhybName.From("j_asi_b_r") });
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("arms"), StartBone = PhybName.From("j_te_l"), EndBone = PhybName.From("j_te_r") });
        file.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("Belly"), Bone = PhybName.From("iv_fukubu_phys") });
        Require(PhysicsShapeNames.Label(file.Collision, "forearm_l", null) == "Left forearm (forearm_l)" &&
                PhysicsShapeNames.Label(file.Collision, "asi_r_a", null) == "Right thigh (asi_r_a)" &&
                PhysicsShapeNames.Label(file.Collision, "arms", null) == "Left hand to right hand (arms)" &&
                PhysicsShapeNames.Label(file.Collision, "Belly", null) == "Belly" &&
                PhysicsShapeNames.Label(file.Collision, "missing", null) == "missing",
            "physics bumps: shapes are named by the body part they cover, the part nearer the body for limbs");
    }
}
