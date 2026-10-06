using System.Numerics;

namespace InstantEdit.Services.Physics;

internal enum PhysicsIssueLevel
{
    /// <summary> The game could misread the file: saving and previewing are refused. </summary>
    Error,
    /// <summary> Works, but probably not as meant. </summary>
    Warning,
}

/// <summary> A one-click fix for an issue. </summary>
internal enum PhysicsFix
{
    None,
    /// <summary> Swing limits above 180° become 180°, negative ones 0°. </summary>
    ClampSwing,
    /// <summary> The simulator moves to the chest group, so gear that turns off breast physics stops it. </summary>
    ChestGroup,
    /// <summary> Springs, pins, connectors and alignments on points that don't exist, and shapes the file lacks, are removed. </summary>
    RemoveBroken,
}

/// <summary> A problem in one simulator (or the file's shapes, at -1), with an optional fix. </summary>
internal sealed record PhysicsIssue(PhysicsIssueLevel Level, int Simulator, string Text, PhysicsFix Fix = PhysicsFix.None);

/// <summary>
/// Checks a physics file before it is previewed or saved. Errors are what the game can't simulate:
/// chains without a bone and a tip, and values that aren't numbers. Everything else is a warning,
/// because popular presets ship it and the game copes: points past a chain's end (VFXEditor numbers
/// points from 1 in its preview, the game from 0), shapes the file doesn't define, a point without a
/// bone before the tip, bones the character's skeleton lacks, swing limits over 180° and body areas
/// in groups that unrelated gear turns off. Simulators the builder leaves out are skipped.
/// </summary>
internal static class PhysicsValidation
{
    private const float SwingLimit = MathF.PI + 1e-4f;

    public static IReadOnlyList<PhysicsIssue> Check(PhybFile file, PhysicsSkeleton? skeleton)
    {
        var issues = new List<PhysicsIssue>();
        var shapes = file.Collision.Names.Select(name => name.Text).ToList();
        var shapeSet = shapes.ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in shapes.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new(PhysicsIssueLevel.Warning, -1, $"Two collision shapes are named {duplicate.Key}; simulators can only use the first."));
        CheckShapes(file.Collision, skeleton, issues);

        for (var s = 0; s < file.Simulators.Count; s++)
        {
            var simulator = file.Simulators[s];
            if (simulator.Excluded)
                continue;
            var area = PhysicsAreas.Classify(simulator);
            void Add(PhysicsIssueLevel level, string text, PhysicsFix fix = PhysicsFix.None) => issues.Add(new(level, s, text, fix));

            if (!Finite(simulator.Gravity) || !Finite(simulator.Wind))
                Add(PhysicsIssueLevel.Error, "Gravity or wind isn't a number.");
            var broken = new List<string>();
            foreach (var reference in simulator.Collisions.Concat(simulator.CollisionConnectors))
                if (!shapeSet.Contains(reference.Name.Text))
                    broken.Add($"the shape {Shown(reference.Name.Text)}, which the file doesn't have");

            var swingTooWide = false;
            for (var c = 0; c < simulator.Chains.Count; c++)
            {
                var chain = simulator.Chains[c];
                var label = ChainLabel(chain, c);
                if (chain.Nodes.Count < 2)
                {
                    Add(PhysicsIssueLevel.Error, $"{label} needs its bone and a tip.");
                    continue;
                }
                if (chain.Nodes[0].Bone.IsEmpty)
                    Add(PhysicsIssueLevel.Error, $"{label} starts without a bone.");
                for (var n = 1; n < chain.Nodes.Count - 1; n++)
                    if (chain.Nodes[n].Bone.IsEmpty)
                        Add(PhysicsIssueLevel.Warning, $"{label}: point {n + 1} has no bone, so the points after it may not move as meant. Only the tip usually has none.");
                if (!Finite(chain.Dampening) || !Finite(chain.MaxSpeed) || !Finite(chain.Friction) || !Finite(chain.CollisionDampening) ||
                    !Finite(chain.RepulsionStrength) || !Finite(chain.LastBoneOffset) ||
                    chain.Nodes.Any(node => !Finite(node.Radius) || !Finite(node.AttractByAnimation) || !Finite(node.WindScale) ||
                                            !Finite(node.GravityScale) || !Finite(node.ConeMaxAngle) || !Finite(node.ConeAxisOffset) ||
                                            !Finite(node.ConstraintPlaneNormal)))
                    Add(PhysicsIssueLevel.Error, $"{label} has a value that isn't a number.");
                if (chain.Nodes.Any(node => node.Radius < 0))
                    Add(PhysicsIssueLevel.Error, $"{label} has a negative collision size.");
                if (chain.Dampening is < 0 or > 1)
                    Add(PhysicsIssueLevel.Warning, $"{label}: damping is usually between 0 and 1.");
                swingTooWide |= chain.Nodes.Any(node => node.ConeMaxAngle is < 0 or > SwingLimit);
                foreach (var reference in chain.Collisions)
                    if (!shapeSet.Contains(reference.Name.Text))
                        broken.Add($"the shape {Shown(reference.Name.Text)}, which the file doesn't have");
                if (skeleton != null)
                    for (var n = 0; n < chain.Nodes.Count - 1; n++)
                        if (!chain.Nodes[n].Bone.IsEmpty && !skeleton.Has(chain.Nodes[n].Bone.Text))
                        {
                            Add(PhysicsIssueLevel.Warning, $"{label}: your skeleton has no {chain.Nodes[n].Bone.Text}, so this chain does nothing on you.");
                            break;
                        }
            }
            if (swingTooWide)
                Add(PhysicsIssueLevel.Warning, "A swing limit is over 180° or below 0°.", PhysicsFix.ClampSwing);

            bool Point(short chain, short node) => chain >= 0 && chain < simulator.Chains.Count && node >= 0 && node < simulator.Chains[chain].Nodes.Count;
            foreach (var attract in simulator.Attracts)
            {
                if (!Point(attract.Chain, attract.Node))
                    broken.Add($"a spring on chain {attract.Chain + 1}, point {attract.Node + 1}");
                if (!Finite(attract.Offset) || !Finite(attract.Stiffness))
                    Add(PhysicsIssueLevel.Error, "A spring has a value that isn't a number.");
                if (skeleton != null && !attract.Bone.IsEmpty && !skeleton.Has(attract.Bone.Text))
                    Add(PhysicsIssueLevel.Warning, $"A spring hangs from {attract.Bone.Text}, which your skeleton doesn't have.");
            }
            foreach (var pin in simulator.Pins)
                if (!Point(pin.Chain, pin.Node))
                    broken.Add($"a pin on chain {pin.Chain + 1}, point {pin.Node + 1}");
            foreach (var connector in simulator.Connectors)
                if (!Point(connector.Chain1, connector.Node1) || !Point(connector.Chain2, connector.Node2))
                    broken.Add($"a connector between chain {connector.Chain1 + 1}, point {connector.Node1 + 1} and chain {connector.Chain2 + 1}, point {connector.Node2 + 1}");
            foreach (var spring in simulator.Springs)
                if (!Point(spring.Chain1, spring.Node1) || !Point(spring.Chain2, spring.Node2))
                    broken.Add($"a spring between chain {spring.Chain1 + 1}, point {spring.Node1 + 1} and chain {spring.Chain2 + 1}, point {spring.Node2 + 1}");
            foreach (var alignment in simulator.PostAlignments)
                if (!Point(alignment.Chain, alignment.Node) || !shapeSet.Contains(alignment.CollisionName.Text))
                    broken.Add($"an alignment on chain {alignment.Chain + 1}, point {alignment.Node + 1}");
            if (broken.Count > 0)
                Add(PhysicsIssueLevel.Warning, broken.Count == 1
                    ? $"Links to something that doesn't exist: {broken[0]}. The game probably ignores it."
                    : $"{broken.Count} links point at things that don't exist, such as {broken[0]}. The game probably ignores them.",
                    PhysicsFix.RemoveBroken);

            if (area.Body && simulator.Group != PhysicsAreas.ChestGroup)
                Add(PhysicsIssueLevel.Warning, PhysicsGroups.IsHair(simulator.Group)
                    ? $"In a hair group ({simulator.Group}): hats that turn off hair physics stop it, and tops that turn off breast physics don't."
                    : $"In group {simulator.Group}: tops that turn off breast physics don't stop it.", PhysicsFix.ChestGroup);
        }
        return issues;
    }

    private static void CheckShapes(PhybCollision collision, PhysicsSkeleton? skeleton, List<PhysicsIssue> issues)
    {
        if (collision.Capsules.Any(c => !Finite(c.StartOffset) || !Finite(c.EndOffset) || !Finite(c.Radius)) ||
            collision.Spheres.Any(c => !Finite(c.Offset) || !Finite(c.Thickness)))
            issues.Add(new(PhysicsIssueLevel.Error, -1, "A collision shape has a value that isn't a number."));
        if (skeleton == null)
            return;
        var bones = collision.Capsules.SelectMany(c => new[] { (c.Name.Text, c.StartBone.Text), (c.Name.Text, c.EndBone.Text) })
            .Concat(collision.Spheres.Select(c => (c.Name.Text, c.Bone.Text)))
            .Concat(collision.Ellipsoids.Select(c => (c.Name.Text, c.Bone.Text)))
            .Concat(collision.NormalPlanes.Select(c => (c.Name.Text, c.Bone.Text)))
            .Concat(collision.ThreePointPlanes.Select(c => (c.Name.Text, c.Bone.Text)));
        foreach (var (shape, bone) in bones.Distinct())
            if (bone.Length > 0 && !skeleton.Has(bone))
                issues.Add(new(PhysicsIssueLevel.Warning, -1, $"The collision shape {shape} sits on {bone}, which your skeleton doesn't have."));
    }

    /// <summary> Applies a fix to a simulator. </summary>
    public static void Apply(PhybFile file, PhysicsFix fix, int simulator)
    {
        if (simulator < 0 || simulator >= file.Simulators.Count)
            return;
        var target = file.Simulators[simulator];
        switch (fix)
        {
            case PhysicsFix.ClampSwing:
                foreach (var node in target.Chains.SelectMany(chain => chain.Nodes))
                    node.ConeMaxAngle = Math.Clamp(node.ConeMaxAngle, 0, MathF.PI);
                break;
            case PhysicsFix.ChestGroup:
                target.Group = PhysicsAreas.ChestGroup;
                break;
            case PhysicsFix.RemoveBroken:
                RemoveBroken(file, target);
                break;
        }
    }

    /// <summary> Removes the simulator's links to points and shapes that don't exist. </summary>
    private static void RemoveBroken(PhybFile file, PhybSimulator simulator)
    {
        var shapes = file.Collision.Names.Select(name => name.Text).ToHashSet(StringComparer.Ordinal);
        bool Point(short chain, short node) => chain >= 0 && chain < simulator.Chains.Count && node >= 0 && node < simulator.Chains[chain].Nodes.Count;
        simulator.Attracts.RemoveAll(a => !Point(a.Chain, a.Node));
        simulator.Pins.RemoveAll(p => !Point(p.Chain, p.Node));
        simulator.Connectors.RemoveAll(c => !Point(c.Chain1, c.Node1) || !Point(c.Chain2, c.Node2));
        simulator.Springs.RemoveAll(s => !Point(s.Chain1, s.Node1) || !Point(s.Chain2, s.Node2));
        simulator.PostAlignments.RemoveAll(a => !Point(a.Chain, a.Node) || !shapes.Contains(a.CollisionName.Text));
        simulator.CollisionConnectors.RemoveAll(r => !shapes.Contains(r.Name.Text));
        // A node's collision bits count the simulator's shapes, then its chain's own: each kept shape keeps its bit, renumbered.
        var keptSimulator = simulator.Collisions.Where(r => shapes.Contains(r.Name.Text)).ToList();
        foreach (var chain in simulator.Chains)
        {
            var before = PhysicsCollisions.BitShapes(simulator, chain);
            var keptChain = chain.Collisions.Where(r => shapes.Contains(r.Name.Text)).ToList();
            var after = keptSimulator.Concat(keptChain).ToList();
            if (after.Count != simulator.Collisions.Count + chain.Collisions.Count)
                foreach (var node in chain.Nodes)
                {
                    node.CollisionFlags = Renumbered(node.CollisionFlags, before, after);
                    node.ContinuousCollisionFlags = Renumbered(node.ContinuousCollisionFlags, before, after);
                }
            chain.Collisions = keptChain;
        }
        simulator.Collisions = keptSimulator;
    }

    /// <summary> Bits over the shapes <paramref name="before"/> named, moved to where the same shapes are <paramref name="after"/>. </summary>
    private static uint Renumbered(uint flags, IReadOnlyList<PhybCollisionRef> before, List<PhybCollisionRef> after)
    {
        var result = 0u;
        for (var bit = 0; bit < before.Count && bit < PhysicsCollisions.MaxShapes; bit++)
            if ((flags & (1u << bit)) != 0 && after.IndexOf(before[bit]) is var moved and >= 0 and < PhysicsCollisions.MaxShapes)
                result |= 1u << moved;
        return result;
    }

    /// <summary> "Chain j_mune_l", or "Chain 3" when it has no bone. </summary>
    public static string ChainLabel(PhybChain chain, int index)
        => chain.Nodes.Count > 0 && !chain.Nodes[0].Bone.IsEmpty ? $"Chain {chain.Nodes[0].Bone.Text}" : $"Chain {index + 1}";

    private static string Shown(string name) => name.Length == 0 ? "(no name)" : name;
    private static bool Finite(float value) => float.IsFinite(value);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
