namespace InstantEdit.Services.Physics;

/// <summary> How many of some chains' moving points bump into a shape. </summary>
internal enum PhysicsBumps
{
    None,
    Some,
    All,
}

/// <summary>
/// Which shapes a chain's moving points bump into. Each point has a bit per shape: bit n names the
/// simulator's shape n, and the bits after the simulator's shapes name the chain's own (the game's
/// gear files set bit 2 for a chain shape after two simulator shapes). The continuous collision bits
/// copy the collision bits wherever a file uses them. Bits past both lists name nothing; presets often
/// set them (255 for two shapes), and an edit here drops them.
/// </summary>
internal static class PhysicsCollisions
{
    /// <summary> A point has 32 bits, so a chain can bump into 32 shapes at most. </summary>
    public const int MaxShapes = 32;

    /// <summary> The shape each of a chain's collision bits names, from bit 0. </summary>
    public static IReadOnlyList<PhybCollisionRef> BitShapes(PhybSimulator simulator, PhybChain chain)
        => simulator.Collisions.Concat(chain.Collisions).Take(MaxShapes).ToList();

    /// <summary> The shapes a moving point bumps into, by its bits. </summary>
    public static IEnumerable<PhybCollisionRef> Shapes(PhybSimulator simulator, PhybChain chain, PhybNode node)
    {
        var shapes = BitShapes(simulator, chain);
        for (var bit = 0; bit < shapes.Count; bit++)
            if ((node.CollisionFlags & (1u << bit)) != 0)
                yield return shapes[bit];
    }

    /// <summary> Whether every, some or none of the chains' moving points bump into <paramref name="shape"/>. </summary>
    public static PhysicsBumps State(PhybFile file, IEnumerable<(int Simulator, int Chain)> chains, string shape)
    {
        int points = 0, bumping = 0;
        foreach (var (simulator, chain) in Valid(file, chains))
            foreach (var node in chain.Nodes.Skip(1))
            {
                points++;
                if (Shapes(simulator, chain, node).Any(reference => reference.Name.Text == shape))
                    bumping++;
            }
        return bumping == 0 ? PhysicsBumps.None : bumping == points ? PhysicsBumps.All : PhysicsBumps.Some;
    }

    /// <summary> Whether any of the chains has a moving point, so ticking or unticking a shape changes something. </summary>
    public static bool HasPoints(PhybFile file, IEnumerable<(int Simulator, int Chain)> chains) => Valid(file, chains).Any();

    /// <summary> Whether <see cref="Add"/> would succeed, without changing anything. </summary>
    public static bool CanAdd(PhybFile file, IEnumerable<(int Simulator, int Chain)> chains, string shape)
    {
        foreach (var simulator in Valid(file, chains).Select(target => target.Simulator).Distinct())
        {
            var listed = simulator.Collisions.FindIndex(reference => reference.Name.Text == shape);
            if (listed >= MaxShapes || (listed < 0 && simulator.Chains.Any(chain => simulator.Collisions.Count + chain.Collisions.Count >= MaxShapes)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Makes every moving point of the chains bump into <paramref name="shape"/>, listing it in the
    /// simulator when it isn't yet. False, with nothing changed, when a chain would need a 33rd bit.
    /// </summary>
    public static bool Add(PhybFile file, IReadOnlyList<(int Simulator, int Chain)> chains, string shape)
    {
        if (!CanAdd(file, chains, shape))
            return false;
        var targets = Valid(file, chains).ToList();
        foreach (var simulator in targets.Select(target => target.Simulator).Distinct())
        {
            if (simulator.Collisions.Any(reference => reference.Name.Text == shape))
                continue;
            // The new shape takes the bit after the simulator's shapes, where each chain's own shapes start; they move up one.
            var at = simulator.Collisions.Count;
            Renumber(simulator, bit => bit < at ? bit : bit + 1);
            simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From(shape), Type = PhybCollisionType.Outside });
        }
        foreach (var (simulator, chain) in targets)
        {
            var bit = 1u << simulator.Collisions.FindIndex(reference => reference.Name.Text == shape);
            foreach (var node in chain.Nodes.Skip(1))
            {
                // Points that collide continuously keep the same bits for it.
                if (node.ContinuousCollisionFlags != 0)
                    node.ContinuousCollisionFlags |= bit;
                node.CollisionFlags |= bit;
            }
            simulator.Flags |= PhybSimulatorFlags.CollisionsHandled;
        }
        return true;
    }

    /// <summary>
    /// Stops every moving point of the chains bumping into <paramref name="shape"/>. A simulator or chain
    /// shape no point bumps into any more leaves its list, and the bits after it move down.
    /// </summary>
    public static void Remove(PhybFile file, IReadOnlyList<(int Simulator, int Chain)> chains, string shape)
    {
        var targets = Valid(file, chains).ToList();
        foreach (var (simulator, chain) in targets)
        {
            var shapes = BitShapes(simulator, chain);
            var mask = 0u;
            for (var bit = 0; bit < shapes.Count; bit++)
                if (shapes[bit].Name.Text == shape)
                    mask |= 1u << bit;
            foreach (var node in chain.Nodes.Skip(1))
            {
                node.CollisionFlags &= ~mask;
                node.ContinuousCollisionFlags &= ~mask;
            }
        }
        foreach (var simulator in targets.Select(target => target.Simulator).Distinct())
        {
            for (var index = Math.Min(simulator.Collisions.Count, MaxShapes) - 1; index >= 0; index--)
            {
                if (simulator.Collisions[index].Name.Text != shape || simulator.Chains.Any(chain => Uses(chain, 1u << index)))
                    continue;
                // The bits move while the lists still have their old lengths.
                var removed = index;
                Renumber(simulator, bit => bit < removed ? bit : bit == removed ? -1 : bit - 1);
                simulator.Collisions.RemoveAt(index);
            }
            // Only the chains being edited lose an own shape they no longer use.
            foreach (var chain in targets.Where(target => target.Simulator == simulator).Select(target => target.Chain))
                for (var own = chain.Collisions.Count - 1; own >= 0; own--)
                {
                    var removed = simulator.Collisions.Count + own;
                    if (chain.Collisions[own].Name.Text != shape || removed >= MaxShapes || Uses(chain, 1u << removed))
                        continue;
                    Renumber(chain, simulator.Collisions.Count + chain.Collisions.Count, bit => bit < removed ? bit : bit == removed ? -1 : bit - 1);
                    chain.Collisions.RemoveAt(own);
                }
        }
    }

    // A point that collides continuously with a shape uses it even without the normal bit.
    private static bool Uses(PhybChain chain, uint bit) => chain.Nodes.Skip(1).Any(node => ((node.CollisionFlags | node.ContinuousCollisionFlags) & bit) != 0);

    private static IEnumerable<(PhybSimulator Simulator, PhybChain Chain)> Valid(PhybFile file, IEnumerable<(int Simulator, int Chain)> chains)
    {
        foreach (var (s, c) in chains.Distinct())
            if (s >= 0 && s < file.Simulators.Count && c >= 0 && c < file.Simulators[s].Chains.Count && file.Simulators[s].Chains[c].Nodes.Count > 1)
                yield return (file.Simulators[s], file.Simulators[s].Chains[c]);
    }

    /// <summary> Moves every chain's bits by <paramref name="map"/> (-1 drops a bit), over the bits its lists had. </summary>
    private static void Renumber(PhybSimulator simulator, Func<int, int> map)
    {
        foreach (var chain in simulator.Chains)
            Renumber(chain, simulator.Collisions.Count + chain.Collisions.Count, map);
    }

    private static void Renumber(PhybChain chain, int named, Func<int, int> map)
    {
        foreach (var node in chain.Nodes)
        {
            node.CollisionFlags = Map(node.CollisionFlags, named, map);
            node.ContinuousCollisionFlags = Map(node.ContinuousCollisionFlags, named, map);
        }
    }

    // Bits past the named shapes name nothing and are dropped.
    private static uint Map(uint flags, int named, Func<int, int> map)
    {
        var result = 0u;
        for (var bit = 0; bit < Math.Min(named, MaxShapes); bit++)
            if ((flags & (1u << bit)) != 0 && map(bit) is var moved and >= 0 and < MaxShapes)
                result |= 1u << moved;
        return result;
    }
}
