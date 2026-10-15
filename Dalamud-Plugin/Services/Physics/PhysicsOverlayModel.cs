using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary> What a drawn element of the overlay stands for; each has a colour, and a hovered control lights its part. </summary>
internal enum PhysicsOverlayPart
{
    /// <summary> A chain's bones and the lines between its points. </summary>
    Chain,
    /// <summary> The balls on the moving points that bump into shapes. </summary>
    Tip,
    /// <summary> The cone the bone may swing in. </summary>
    Cone,
    /// <summary> The point a chain's spring pulls its tip toward. </summary>
    Spring,
    /// <summary> A collision shape the chains bump into, or any shape in the shapes view. </summary>
    Shape,
    /// <summary> A collision shape the chains don't bump into, drawn dim. </summary>
    OtherShape,
}

/// <summary> Which parts the overlay draws; a hovered part shows whatever this says. </summary>
[Flags]
internal enum PhysicsOverlayShown
{
    None = 0,
    Chain = 1,
    Tip = 2,
    Cone = 4,
    Spring = 8,
    Shapes = 16,
    OtherShapes = 32,
    /// <summary> The area and what it bumps into; the file's other shapes stay out of the way. </summary>
    Default = Chain | Tip | Cone | Spring | Shapes,
    All = Default | OtherShapes,
}

/// <summary> The control the mouse is over: its part, and the shape's name for a shape row. </summary>
internal readonly record struct PhysicsOverlayHover(PhysicsOverlayPart? Part, string? Shape = null)
{
    public static readonly PhysicsOverlayHover None = new(null);
}

internal sealed record PhysicsOverlaySegment(Vector3 From, Vector3 To, PhysicsOverlayPart Part, bool Lit, bool Dashed = false);

/// <summary> A circle in model space: around <paramref name="Normal"/>, or facing the camera when it has none. </summary>
internal sealed record PhysicsOverlayCircle(Vector3 Center, Vector3? Normal, float Radius, PhysicsOverlayPart Part, bool Lit);

/// <summary> A dot, with a label shown while its part is lit. </summary>
internal sealed record PhysicsOverlayMark(Vector3 At, PhysicsOverlayPart Part, bool Lit, string? Label = null);

internal sealed record PhysicsOverlayCapsule(string Name, Vector3 A, Vector3 B, float Radius, PhysicsOverlayPart Part, bool Lit);

internal sealed record PhysicsOverlaySphere(string Name, Vector3 Center, float Radius, PhysicsOverlayPart Part, bool Lit);

/// <summary> Everything the overlay draws for one frame, in the character's model space. </summary>
internal sealed class PhysicsOverlayShapes
{
    public List<PhysicsOverlaySegment> Segments { get; } = [];
    public List<PhysicsOverlayCircle> Circles { get; } = [];
    public List<PhysicsOverlayMark> Marks { get; } = [];
    public List<PhysicsOverlayCapsule> Capsules { get; } = [];
    public List<PhysicsOverlaySphere> Spheres { get; } = [];

    public bool IsEmpty => Segments.Count + Circles.Count + Marks.Count + Capsules.Count + Spheres.Count == 0;
}

/// <summary>
/// What the builder shows on the character for the chains being edited: each chain's bones and tip,
/// the balls on its moving points, the swing cone of its bone, where its spring pulls, and the file's
/// collision shapes, lit when the chains bump into them. Built from the live pose (physics runs
/// before Customize+ scales bones, so offsets turn and move with a bone but never scale). Pure data,
/// testable without the game; anything a bone or frame is missing for is left out.
/// </summary>
internal static class PhysicsOverlayModel
{
    /// <summary> Lines drawn from the cone's point to its rim. </summary>
    public const int ConeLines = 4;

    /// <summary>
    /// The overlay for <paramref name="targets"/>. With <paramref name="allShapes"/> (the shapes view)
    /// every shape counts as used; otherwise the ones the chains bump into are lit shapes and the rest
    /// dim. The hovered control's part is lit, a hovered shape by name, and shows even when
    /// <paramref name="shown"/> leaves its part out.
    /// </summary>
    public static PhysicsOverlayShapes Build(PhybFile file, IReadOnlyList<(int Simulator, int Chain)> targets, bool allShapes,
        PhysicsOverlayHover hover, PhysicsSkeleton skeleton, Func<string, PhysicsFrame?> frame, PhysicsOverlayShown shown = PhysicsOverlayShown.All)
    {
        var shapes = new PhysicsOverlayShapes();
        // Chains of areas turned off aren't in the file the game gets, so they draw nothing and bump into nothing.
        var active = targets.Where(target => target.Simulator >= 0 && target.Simulator < file.Simulators.Count && !file.Simulators[target.Simulator].Excluded &&
                                             target.Chain >= 0 && target.Chain < file.Simulators[target.Simulator].Chains.Count).ToList();
        foreach (var (s, c) in active)
            Chain(file.Simulators[s], c, hover, skeleton, frame, shapes);
        Collision(file, active, allShapes, hover, frame, shapes);
        if (shown != PhysicsOverlayShown.All)
        {
            shapes.Segments.RemoveAll(segment => !Shows(shown, segment.Part, segment.Lit));
            shapes.Circles.RemoveAll(circle => !Shows(shown, circle.Part, circle.Lit));
            shapes.Marks.RemoveAll(mark => !Shows(shown, mark.Part, mark.Lit));
            shapes.Capsules.RemoveAll(capsule => !Shows(shown, capsule.Part, capsule.Lit));
            shapes.Spheres.RemoveAll(sphere => !Shows(shown, sphere.Part, sphere.Lit));
        }
        return shapes;
    }

    /// <summary> Whether an element draws: its part is ticked, or it's lit by the hovered control. </summary>
    private static bool Shows(PhysicsOverlayShown shown, PhysicsOverlayPart part, bool lit)
        => lit || shown.HasFlag(part switch
        {
            PhysicsOverlayPart.Chain => PhysicsOverlayShown.Chain,
            PhysicsOverlayPart.Tip => PhysicsOverlayShown.Tip,
            PhysicsOverlayPart.Cone => PhysicsOverlayShown.Cone,
            PhysicsOverlayPart.Spring => PhysicsOverlayShown.Spring,
            PhysicsOverlayPart.Shape => PhysicsOverlayShown.Shapes,
            _ => PhysicsOverlayShown.OtherShapes,
        });

    private static void Chain(PhybSimulator simulator, int index, PhysicsOverlayHover hover, PhysicsSkeleton skeleton, Func<string, PhysicsFrame?> frame,
        PhysicsOverlayShapes shapes)
    {
        var chain = simulator.Chains[index];
        var places = Places(chain, frame);
        var chainLit = hover.Part == PhysicsOverlayPart.Chain;
        var tipLit = hover.Part == PhysicsOverlayPart.Tip;
        for (var n = 0; n < places.Count; n++)
        {
            if (places[n] is not { } at)
                continue;
            if (n > 0 && places[n - 1] is { } before)
                shapes.Segments.Add(new PhysicsOverlaySegment(before, at, PhysicsOverlayPart.Chain, chainLit));
            var tip = n == places.Count - 1;
            shapes.Marks.Add(new PhysicsOverlayMark(at, PhysicsOverlayPart.Chain, chainLit,
                tip && n > 0 ? Centimetres(Vector3.Distance(places[n - 1] ?? at, at)) : null));
            if (n > 0 && chain.Nodes[n].Radius > 1e-5f)
            {
                shapes.Circles.Add(new PhysicsOverlayCircle(at, null, chain.Nodes[n].Radius, PhysicsOverlayPart.Tip, tipLit));
                if (tip)
                    shapes.Marks.Add(new PhysicsOverlayMark(at, PhysicsOverlayPart.Tip, tipLit, Centimetres(chain.Nodes[n].Radius)));
            }
        }
        if (places.Count >= 2 && places[0] is { } apex && places[1] is { } next)
            Cone(chain, apex, next, hover, skeleton, frame, shapes);
        if (PhysicsKnobs.MainSpring(simulator, index) is { } spring && spring.Node >= 0 && spring.Node < places.Count && places[spring.Node] is { } pulled &&
            frame(spring.Bone.Text)?.Place(spring.Offset) is { } anchor)
        {
            var lit = hover.Part == PhysicsOverlayPart.Spring;
            shapes.Segments.Add(new PhysicsOverlaySegment(pulled, anchor, PhysicsOverlayPart.Spring, lit, Dashed: true));
            shapes.Marks.Add(new PhysicsOverlayMark(anchor, PhysicsOverlayPart.Spring, lit, PhysicsKnobs.SpringName(simulator, spring, skeleton)));
        }
    }

    /// <summary> Where each of a chain's points is: its bone's place, or for the empty tip the chain's offset from the bone before it. </summary>
    private static List<Vector3?> Places(PhybChain chain, Func<string, PhysicsFrame?> frame)
    {
        var places = new List<Vector3?>(chain.Nodes.Count);
        for (var n = 0; n < chain.Nodes.Count; n++)
        {
            var node = chain.Nodes[n];
            if (!node.Bone.IsEmpty)
                places.Add(frame(node.Bone.Text)?.Position);
            else if (n == chain.Nodes.Count - 1 && n > 0 && !chain.Nodes[n - 1].Bone.IsEmpty)
                places.Add(frame(chain.Nodes[n - 1].Bone.Text)?.Place(chain.LastBoneOffset));
            else
                places.Add(null);
        }
        return places;
    }

    /// <summary>
    /// The bone's swing cone: its point on the bone, its axis the cone offset turned by the bone's
    /// parent, as long as the bone's first segment, open as far as the swing limit. The rim is a ring
    /// about the axis, joined to the point by a few lines.
    /// </summary>
    private static void Cone(PhybChain chain, Vector3 apex, Vector3 next, PhysicsOverlayHover hover, PhysicsSkeleton skeleton, Func<string, PhysicsFrame?> frame,
        PhysicsOverlayShapes shapes)
    {
        var bone = chain.Nodes[0];
        if (bone.Bone.IsEmpty || skeleton.Get(bone.Bone.Text) is not { Parent.Length: > 0 } known || frame(known.Parent) is not { } parent)
            return;
        var axis = parent.Turn(bone.ConeAxisOffset);
        var length = Vector3.Distance(apex, next);
        if (axis.LengthSquared() < 1e-10f || length < 1e-5f)
            return;
        axis = Vector3.Normalize(axis);
        var angle = Math.Clamp(bone.ConeMaxAngle, 0, MathF.PI);
        var center = apex + axis * length * MathF.Cos(angle);
        var radius = length * MathF.Sin(angle);
        var lit = hover.Part == PhysicsOverlayPart.Cone;
        shapes.Circles.Add(new PhysicsOverlayCircle(center, axis, radius, PhysicsOverlayPart.Cone, lit));
        var (u, v) = Basis(axis);
        for (var i = 0; i < ConeLines; i++)
        {
            var t = i * MathF.PI * 2 / ConeLines;
            var rim = center + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            shapes.Segments.Add(new PhysicsOverlaySegment(apex, rim, PhysicsOverlayPart.Cone, lit));
            if (i == 0)
                shapes.Marks.Add(new PhysicsOverlayMark(rim, PhysicsOverlayPart.Cone, lit, PhysicsTemplates.Degrees(angle).ToString("0", CultureInfo.InvariantCulture) + "°"));
        }
    }

    private static void Collision(PhybFile file, IReadOnlyList<(int Simulator, int Chain)> targets, bool allShapes, PhysicsOverlayHover hover,
        Func<string, PhysicsFrame?> frame, PhysicsOverlayShapes shapes)
    {
        foreach (var capsule in file.Collision.Capsules)
        {
            if (frame(capsule.StartBone.Text)?.Place(capsule.StartOffset) is not { } a || frame(capsule.EndBone.Text)?.Place(capsule.EndOffset) is not { } b)
                continue;
            var (part, lit) = ShapeState(file, targets, allShapes, hover, capsule.Name.Text);
            shapes.Capsules.Add(new PhysicsOverlayCapsule(capsule.Name.Text, a, b, capsule.Radius, part, lit));
        }
        foreach (var sphere in file.Collision.Spheres)
        {
            if (frame(sphere.Bone.Text)?.Place(sphere.Offset) is not { } center)
                continue;
            var (part, lit) = ShapeState(file, targets, allShapes, hover, sphere.Name.Text);
            shapes.Spheres.Add(new PhysicsOverlaySphere(sphere.Name.Text, center, sphere.Thickness, part, lit));
        }
    }

    /// <summary> A hovered shape is lit; otherwise a shape the chains bump into (or any, in the shapes view) is a shape, the rest dim. </summary>
    private static (PhysicsOverlayPart Part, bool Lit) ShapeState(PhybFile file, IReadOnlyList<(int Simulator, int Chain)> targets, bool allShapes,
        PhysicsOverlayHover hover, string name)
    {
        if (hover.Part == PhysicsOverlayPart.Shape && hover.Shape == name)
            return (PhysicsOverlayPart.Shape, true);
        if (allShapes || PhysicsCollisions.State(file, targets, name) != PhysicsBumps.None)
            return (PhysicsOverlayPart.Shape, false);
        return (PhysicsOverlayPart.OtherShape, false);
    }

    /// <summary> A length in metres as the sliders show it, whatever the system's language: "14.0 cm". </summary>
    private static string Centimetres(float metres) => (metres * 100).ToString("0.0", CultureInfo.InvariantCulture) + " cm";

    /// <summary> Two unit directions at right angles to <paramref name="axis"/> and each other. </summary>
    public static (Vector3 U, Vector3 V) Basis(Vector3 axis)
    {
        var other = MathF.Abs(axis.X) < MathF.Abs(axis.Y) && MathF.Abs(axis.X) < MathF.Abs(axis.Z) ? Vector3.UnitX
            : MathF.Abs(axis.Y) < MathF.Abs(axis.Z) ? Vector3.UnitY : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(axis, other));
        return (u, Vector3.Normalize(Vector3.Cross(axis, u)));
    }
}
