using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// What the mouse is over on the character: a part of one chain, a shape by name, or a chain of another area.
/// <paramref name="Handle"/>: a dot that can be dragged, like the spring's.
/// </summary>
internal readonly record struct PhysicsOverlayTarget(PhysicsOverlayPart Part, int Simulator = -1, int Chain = -1, string? Shape = null, bool Handle = false)
{
    /// <summary> The hover that lights it: this chain's part, this shape, or the whole other area. </summary>
    public PhysicsOverlayHover Hover => Part is PhysicsOverlayPart.Shape or PhysicsOverlayPart.OtherShape
        ? new PhysicsOverlayHover(PhysicsOverlayPart.Shape, Shape)
        : new PhysicsOverlayHover(Part, null, (Simulator, Chain));
}

/// <summary>
/// Finds the overlay element under the mouse, in screen pixels: each element is offered as it's drawn,
/// and within reach a dot that can be dragged wins, then a small part over a big one (tip, spring,
/// bones, cone, another area, then shapes), the nearest among equals. The tip comes before the spring
/// because the spring's line and the bones both start or end inside its ball. Shapes count only along their outline, so the rest of the body
/// stays the game's to click and drag.
/// </summary>
internal sealed class PhysicsOverlayPicker(Vector2 mouse, float reach)
{
    private int bestRank = int.MaxValue;
    private float bestDistance = float.MaxValue;

    public PhysicsOverlayTarget? Hit { get; private set; }

    public void Point(Vector2 at, PhysicsOverlayTarget target) => Offer(Vector2.Distance(at, mouse), target);

    public void Segment(Vector2 a, Vector2 b, PhysicsOverlayTarget target) => Offer(ToSegment(mouse, a, b), target);

    /// <summary> A ball: anywhere inside it counts. </summary>
    public void Disc(Vector2 center, float radius, PhysicsOverlayTarget target) => Offer(Math.Max(0, Vector2.Distance(center, mouse) - radius), target);

    /// <summary> A circle's outline only. </summary>
    public void Ring(Vector2 center, float radius, PhysicsOverlayTarget target) => Offer(MathF.Abs(Vector2.Distance(center, mouse) - radius), target);

    private void Offer(float distance, PhysicsOverlayTarget target)
    {
        if (distance > reach)
            return;
        // A dot that can be dragged wins over everything, the tip ball it sits in when seen end on included.
        var rank = target.Handle ? -1 : Rank(target.Part);
        if (rank > bestRank || rank == bestRank && distance >= bestDistance)
            return;
        (bestRank, bestDistance, Hit) = (rank, distance, target);
    }

    private static int Rank(PhysicsOverlayPart part) => part switch
    {
        PhysicsOverlayPart.Tip => 0,
        PhysicsOverlayPart.Spring => 1,
        PhysicsOverlayPart.Chain => 2,
        PhysicsOverlayPart.Cone => 3,
        PhysicsOverlayPart.OtherArea => 4,
        PhysicsOverlayPart.Shape => 5,
        _ => 6,
    };

    /// <summary> How far <paramref name="point"/> is from the segment between <paramref name="a"/> and <paramref name="b"/>. </summary>
    public static float ToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var along = b - a;
        var length = along.LengthSquared();
        var t = length < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(point - a, along) / length, 0, 1);
        return Vector2.Distance(point, a + along * t);
    }
}

/// <summary> A drag on the character: the move in model space that carries a point along with the mouse, in the camera's plane at the point. </summary>
internal static class PhysicsOverlayDrag
{
    /// <summary>
    /// The move that puts <paramref name="at"/> <paramref name="mouse"/> pixels further on the screen, along
    /// the camera's <paramref name="right"/> and <paramref name="up"/> (model space). <paramref name="project"/>
    /// gives a model point's place on the screen. Null when the point is behind the camera or seen edge on.
    /// </summary>
    public static Vector3? Move(Vector3 at, Vector2 mouse, Func<Vector3, Vector2?> project, Vector3 right, Vector3 up)
    {
        const float step = 0.01f;
        if (project(at) is not { } origin || project(at + right * step) is not { } alongRight || project(at + up * step) is not { } alongUp)
            return null;
        // The screen move of one step along each axis, then the steps that add up to the mouse's move.
        var r = alongRight - origin;
        var u = alongUp - origin;
        var det = r.X * u.Y - r.Y * u.X;
        if (MathF.Abs(det) < 1e-6f)
            return null;
        var a = (mouse.X * u.Y - mouse.Y * u.X) / det;
        var b = (r.X * mouse.Y - r.Y * mouse.X) / det;
        return (right * a + up * b) * step;
    }
}

/// <summary> The tooltip for an element on the character: its setting and value, then whose it is. </summary>
internal static class PhysicsOverlayText
{
    public static string Describe(PhybFile file, PhysicsOverlayTarget target, PhysicsSkeleton? skeleton, bool shapesView)
    {
        if (target.Part is PhysicsOverlayPart.Shape or PhysicsOverlayPart.OtherShape)
        {
            var label = PhysicsShapeNames.Label(file.Collision, target.Shape ?? string.Empty, skeleton);
            return shapesView ? label : $"{label}\n{(target.Part == PhysicsOverlayPart.Shape ? "Collides" : "Doesn't collide")}";
        }
        if (target.Simulator < 0 || target.Simulator >= file.Simulators.Count)
            return string.Empty;
        if (target.Part == PhysicsOverlayPart.OtherArea)
            return PhysicsAreas.Labels(file)[target.Simulator];
        var simulator = file.Simulators[target.Simulator];
        if (target.Chain < 0 || target.Chain >= simulator.Chains.Count)
            return string.Empty;
        var chain = PhysicsValidation.ChainLabel(simulator.Chains[target.Chain], target.Chain);
        var setting = target.Part switch
        {
            PhysicsOverlayPart.Tip => Knob(simulator, target.Chain, PhysicsKnob.Size),
            PhysicsOverlayPart.Cone => Knob(simulator, target.Chain, PhysicsKnob.Swing),
            PhysicsOverlayPart.Spring => Knob(simulator, target.Chain, PhysicsKnob.Firmness,
                skeleton != null && PhysicsKnobs.MainSpring(simulator, target.Chain) is { } spring ? PhysicsKnobs.SpringName(simulator, spring, skeleton) : null),
            _ => Knob(simulator, target.Chain, PhysicsKnob.Length),
        };
        return setting is null ? chain : $"{setting}\n{chain}";
    }

    /// <summary> "Swing limit 30°": a knob's name (or the spring's own) and value, rounded as its slider shows it. </summary>
    private static string? Knob(PhybSimulator simulator, int chain, PhysicsKnob knob, string? name = null)
    {
        if (PhysicsKnobs.Read(simulator, chain, knob) is not { } value)
            return null;
        var (label, decimals, unit) = PhysicsKnobs.Describe(knob);
        return $"{name ?? label} {value.ToString("F" + decimals, CultureInfo.InvariantCulture)}{unit}";
    }
}
