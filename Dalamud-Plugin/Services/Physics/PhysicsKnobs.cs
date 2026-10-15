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
    /// <summary> A spring aimed further than this many degrees from where its point rests drags it instead of firming it up. </summary>
    public const float SpringAimLimit = 15;

    /// <summary> A knob's name, how it's rounded and its unit, as its slider and the undo text show it. </summary>
    public static (string Name, int Decimals, string Unit) Describe(PhysicsKnob knob) => knob switch
    {
        PhysicsKnob.Firmness => ("Firmness", 4, ""),
        PhysicsKnob.Damping => ("Settle", 3, ""),
        PhysicsKnob.Swing => ("Swing limit", 0, "°"),
        PhysicsKnob.Weight => ("Weight", 2, ""),
        PhysicsKnob.Follow => ("Follow pose", 3, ""),
        PhysicsKnob.Length => ("Tip length", 1, " cm"),
        PhysicsKnob.MaxSpeed => ("Max speed", 1, " m/s"),
        PhysicsKnob.Wind => ("Wind", 4, ""),
        PhysicsKnob.Size => ("Tip size", 1, " cm"),
        PhysicsKnob.Friction => ("Friction", 3, ""),
        PhysicsKnob.BumpDamping => ("Bump damping", 3, ""),
        _ => ("Push back", 3, ""),
    };

    public static string Label(PhysicsKnob knob) => Describe(knob).Name;

    /// <summary> The value of <paramref name="knob"/> on a chain; null when it has none, like firmness without a spring. </summary>
    public static float? Read(PhybSimulator simulator, int chain, PhysicsKnob knob)
    {
        if (chain < 0 || chain >= simulator.Chains.Count || simulator.Chains[chain].Nodes.Count < 2)
            return null;
        var own = simulator.Chains[chain];
        var tip = own.Nodes[^1];
        return knob switch
        {
            PhysicsKnob.Firmness => MainSpring(simulator, chain) is { } spring ? MathF.Abs(spring.Stiffness) : null,
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
                // A negative spring pushes away from its point (presets use it to lift); the slider sets its strength, never its sign.
                if (MainSpring(simulator, chain) is { } spring)
                    spring.Stiffness = spring.Stiffness < 0 ? -MathF.Abs(value) : MathF.Abs(value);
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

    /// <summary>
    /// How far, in degrees, a spring pulls its point away from where the point rests, seen from the
    /// point before it, in the skeleton's rest pose. The game moves the point a share (the stiffness) of
    /// the way to the spring's point each step, and a negative share away from it, so only a spring aimed
    /// at the rest place firms it up; one aimed elsewhere drags it that way, harder the firmer it is.
    /// Null when a bone is missing.
    /// </summary>
    public static float? SpringAim(PhybSimulator simulator, PhybAttract spring, PhysicsSkeleton skeleton)
        => SpringPull(simulator, spring, skeleton) is { } pull
            ? PhysicsTemplates.Degrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(pull.Resting), Vector3.Normalize(pull.Pull)), -1, 1)))
            : null;

    /// <summary>
    /// What a chain's spring does, named for its slider: "Firmness" when it pulls the tip back to where
    /// it rests, otherwise the way it moves the tip on the body ("Lift", "Pull down" and so on). The
    /// game's model space has +Y up and +Z forward. Plain "Spring" when a bone is missing or it pulls
    /// straight along the bone, where the tip can't follow.
    /// </summary>
    public static string SpringName(PhybSimulator simulator, PhybAttract spring, PhysicsSkeleton skeleton)
    {
        if (SpringPull(simulator, spring, skeleton) is not { } pull || SpringAim(simulator, spring, skeleton) is not { } aim)
            return "Spring";
        if (aim <= SpringAimLimit)
            return "Firmness";
        var along = Vector3.Normalize(pull.Resting);
        var sideways = pull.Pull - along * Vector3.Dot(pull.Pull, along);
        if (sideways.Length() < pull.Pull.Length() * 0.25f)
            return "Spring";
        var x = MathF.Abs(sideways.X);
        var y = MathF.Abs(sideways.Y);
        var z = MathF.Abs(sideways.Z);
        if (y >= x && y >= z)
            return sideways.Y > 0 ? "Lift" : "Pull down";
        if (z >= x)
            return sideways.Z > 0 ? "Pull forward" : "Pull back";
        return MathF.Sign(sideways.X) == -MathF.Sign(pull.Point.X) ? "Pull together" : "Pull apart";
    }

    /// <summary>
    /// A spring's point and the place it acts on, in the rest pose: the rest direction from the point
    /// before it to the point, and the way the spring moves it (toward its anchor, or away when negative).
    /// </summary>
    private static (Vector3 Resting, Vector3 Pull, Vector3 Point)? SpringPull(PhybSimulator simulator, PhybAttract spring, PhysicsSkeleton skeleton)
    {
        if (spring.Chain < 0 || spring.Chain >= simulator.Chains.Count)
            return null;
        var chain = simulator.Chains[spring.Chain];
        if (spring.Node < 1 || spring.Node >= chain.Nodes.Count || chain.Nodes.Count < 2)
            return null;
        Vector3? Rest(int node)
        {
            if (node < chain.Nodes.Count - 1 || !chain.Nodes[node].Bone.IsEmpty)
                return RestFrame(skeleton, chain.Nodes[node].Bone.Text)?.Position;
            // The tip sits at the chain's last offset from the bone before it.
            return RestFrame(skeleton, chain.Nodes[node - 1].Bone.Text)?.Place(chain.LastBoneOffset);
        }
        if (Rest(spring.Node - 1) is not { } pivot || Rest(spring.Node) is not { } point ||
            RestFrame(skeleton, spring.Bone.Text)?.Place(spring.Offset) is not { } anchor)
            return null;
        var rest = point - pivot;
        var pull = anchor - pivot;
        if (rest.LengthSquared() < 1e-10f || pull.LengthSquared() < 1e-10f)
            return null;
        return (rest, spring.Stiffness < 0 ? -pull : pull, point);
    }

    /// <summary> A body bone's rest place in model space, from its parents' rest places. </summary>
    private static PhysicsFrame? RestFrame(PhysicsSkeleton skeleton, string name)
    {
        var path = new List<PhysicsBone>();
        for (var bone = skeleton.Get(name); bone != null && path.Count < 512; bone = bone.Parent == bone.Name ? null : skeleton.Get(bone.Parent))
            path.Add(bone);
        if (path.Count == 0)
            return null;
        var frame = PhysicsFrame.Rest(Vector3.Zero, Quaternion.Identity);
        for (var i = path.Count - 1; i >= 0; i--)
            frame = PhysicsFrame.Rest(frame.Position + Vector3.Transform(path[i].Position, frame.Rotation), Quaternion.Normalize(frame.Rotation * Quaternion.Normalize(path[i].Rotation)));
        return frame;
    }

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
