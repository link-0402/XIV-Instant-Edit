using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary> A setting the builder shows as a slider for an area's chains. </summary>
internal enum PhysicsKnob
{
    Firmness,
    Damping,
    Swing,
    Weight,
    Follow,
    Length,
    MaxSpeed,
    Wind,
    Size,
    Friction,
    BumpDamping,
    PushBack,
}

/// <summary>
/// What each slider reads and writes on a chain, in the units the builder shows: swing in degrees,
/// length and size in centimetres. The bone's settings live on the chain or its first point; the
/// moving points share the tip's, each keeping its share of it.
/// </summary>
internal static class PhysicsKnobs
{
    /// <summary> The value of <paramref name="knob"/> on a chain; null when it has none, like firmness without a spring. </summary>
    public static float? Read(PhybSimulator simulator, int chain, PhysicsKnob knob)
    {
        if (chain < 0 || chain >= simulator.Chains.Count || simulator.Chains[chain].Nodes.Count < 2)
            return null;
        var own = simulator.Chains[chain];
        var tip = own.Nodes[^1];
        return knob switch
        {
            PhysicsKnob.Firmness => MainSpring(simulator, chain)?.Stiffness,
            PhysicsKnob.Damping => own.Dampening,
            PhysicsKnob.Swing => PhysicsTemplates.Degrees(own.Nodes[0].ConeMaxAngle),
            PhysicsKnob.Weight => tip.GravityScale,
            PhysicsKnob.Follow => tip.AttractByAnimation,
            PhysicsKnob.Length => own.LastBoneOffset.Length() * 100,
            PhysicsKnob.MaxSpeed => own.MaxSpeed,
            PhysicsKnob.Wind => tip.WindScale,
            PhysicsKnob.Size => tip.Radius * 100,
            PhysicsKnob.Friction => own.Friction,
            PhysicsKnob.BumpDamping => own.CollisionDampening,
            PhysicsKnob.PushBack => own.RepulsionStrength,
            _ => null,
        };
    }

    /// <summary> Sets <paramref name="knob"/> on a chain as its slider does. Firmness on a chain without a spring changes nothing. </summary>
    public static void Write(PhybSimulator simulator, int chain, PhysicsKnob knob, float value)
    {
        if (chain < 0 || chain >= simulator.Chains.Count || simulator.Chains[chain].Nodes.Count < 2)
            return;
        var own = simulator.Chains[chain];
        switch (knob)
        {
            case PhysicsKnob.Firmness:
                if (MainSpring(simulator, chain) is { } spring)
                    spring.Stiffness = value;
                break;
            case PhysicsKnob.Damping:
                own.Dampening = value;
                break;
            case PhysicsKnob.Swing:
                SetSwing(own, PhysicsTemplates.Radians(value));
                break;
            case PhysicsKnob.Weight:
                SetMoving(own, node => node.GravityScale, (node, set) => node.GravityScale = set, value);
                break;
            case PhysicsKnob.Follow:
                SetMoving(own, node => node.AttractByAnimation, (node, set) => node.AttractByAnimation = set, value);
                break;
            case PhysicsKnob.Length:
                var offset = own.LastBoneOffset;
                own.LastBoneOffset = offset.LengthSquared() > 1e-12f ? Vector3.Normalize(offset) * (value / 100) : new Vector3(value / 100, 0, 0);
                break;
            case PhysicsKnob.MaxSpeed:
                own.MaxSpeed = value;
                break;
            case PhysicsKnob.Wind:
                SetMoving(own, node => node.WindScale, (node, set) => node.WindScale = set, value);
                break;
            case PhysicsKnob.Size:
                SetMoving(own, node => node.Radius, (node, set) => node.Radius = set, value / 100);
                break;
            case PhysicsKnob.Friction:
                own.Friction = value;
                break;
            case PhysicsKnob.BumpDamping:
                own.CollisionDampening = value;
                break;
            case PhysicsKnob.PushBack:
                own.RepulsionStrength = value;
                break;
        }
    }

    /// <summary> The spring that holds a chain's tip: its strongest one on a moving point. </summary>
    public static PhybAttract? MainSpring(PhybSimulator simulator, int chain)
        => simulator.Attracts.Where(attract => attract.Chain == chain && attract.Node >= 1).MaxBy(attract => MathF.Abs(attract.Stiffness));

    /// <summary> Sets the bone's swing limit; the points between bone and tip keep their share of it. </summary>
    public static void SetSwing(PhybChain chain, float radians)
    {
        var old = chain.Nodes[0].ConeMaxAngle;
        chain.Nodes[0].ConeMaxAngle = radians;
        for (var n = 1; n < chain.Nodes.Count - 1; n++)
            chain.Nodes[n].ConeMaxAngle = old > 1e-6f ? Math.Clamp(chain.Nodes[n].ConeMaxAngle * radians / old, 0, MathF.PI) : radians;
    }

    /// <summary> Sets a value on the tip; the points between bone and tip keep their share of it. </summary>
    public static void SetMoving(PhybChain chain, Func<PhybNode, float> get, Action<PhybNode, float> set, float value)
    {
        var tip = chain.Nodes[^1];
        var old = get(tip);
        for (var n = 1; n < chain.Nodes.Count - 1; n++)
            set(chain.Nodes[n], MathF.Abs(old) > 1e-9f ? get(chain.Nodes[n]) * value / old : value);
        set(tip, value);
    }
}
