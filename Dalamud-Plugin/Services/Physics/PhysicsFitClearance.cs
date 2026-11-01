using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// Keeps a fit's sizes clear where physics runs: in the skeleton's rest pose, without Customize+ or the bust, with the
/// places and sizes the chosen rows write. Each moving point of a body area's chain keeps <see cref="Margin"/> between its
/// surface and that of every shape it stays out of (Inside shapes are skipped), so two breasts fitted to a Customize+ body
/// don't push each other apart all the time. When a pair is closer, the rows give way: the shape's size first, then the
/// point's, never below <see cref="PhysicsFit.Smallest"/>, and a value no chosen row sets is never touched.
/// Only body areas count: skirt points sit on the shapes by design (the skirt fit leaves room for their size), and hair
/// rests on shoulders. A ball on the point's own part counts too: kept on its bone, it would push its own tip all the time.
/// Geometry only, so ticking a row runs it again at once.
/// </summary>
internal static class PhysicsFitClearance
{
    public const float Margin = 0.01f;

    private const int Rounds = 8;

    // A pair lowered to exactly the margin comes back within float noise of it (and sizes go through centimetres on the way in).
    private const float Slack = 1e-5f;

    /// <summary>
    /// <paramref name="items"/> with the chosen sizes held back where they'd come within <see cref="Margin"/> at rest: a list of
    /// the same length and order, every row it doesn't lower the same instance, lowered rows copies whose detail says why.
    /// </summary>
    public static IReadOnlyList<PhysicsFitItem> Hold(PhybFile file, IReadOnlyList<PhysicsFitItem> items, IReadOnlySet<int> chosen, PhysicsSkeleton skeleton)
    {
        var held = items.ToArray();
        var picked = chosen.Where(i => i >= 0 && i < held.Length).Order().ToList();
        var rest = PhysicsBody.RestPose(skeleton);
        for (var round = 0; round < Rounds; round++)
        {
            var applied = file.Clone();
            PhysicsFit.Apply(applied, picked.Select(i => held[i]));
            if (!Lower(applied, held, picked, rest, skeleton))
                break;
        }
        return held;
    }

    /// <summary> One round: every pair closer than the margin lowers what the chosen rows set. False when nothing was lowered. </summary>
    private static bool Lower(PhybFile applied, PhysicsFitItem[] held, List<int> picked, IReadOnlyDictionary<string, PhysicsFrame> rest, PhysicsSkeleton skeleton)
    {
        var chosen = picked.Select(i => held[i]).ToList();
        // What this round has lowered so far: shape rows' sizes, the factor a chain's tip size scales its points by, and point sizes.
        var shapes = new Dictionary<int, float>();
        var scales = new Dictionary<int, float>();
        var points = new Dictionary<(int Row, int Node), float>();
        var reasons = new Dictionary<int, string>();
        for (var s = 0; s < applied.Simulators.Count; s++)
        {
            var simulator = applied.Simulators[s];
            if (simulator.Excluded)
                continue;
            for (var c = 0; c < simulator.Chains.Count; c++)
            {
                var chain = simulator.Chains[c];
                if (chain.Nodes.Count < 2 || PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text) is not { Body: true } kind)
                    continue;
                var tip = PhysicsKnobs.TipIndex(chain);
                var sizeRow = Row(held, picked, item => item.Target is PhysicsFitTarget.Chain or PhysicsFitTarget.Points && item.Simulator == s && item.Chain == c);
                for (var n = 1; n < chain.Nodes.Count; n++)
                {
                    if (Place(chain, n, tip, rest) is not { } at)
                        continue;
                    foreach (var reference in PhysicsCollisions.Shapes(simulator, chain, chain.Nodes[n]).Where(reference => reference.Type != PhybCollisionType.Inside))
                    {
                        var name = reference.Name.Text;
                        if (Shape(applied.Collision, name, rest) is not { } shape)
                            continue;
                        var shapeRow = Row(held, picked, item => item.Target is PhysicsFitTarget.Capsule or PhysicsFitTarget.Sphere && item.Name == name);
                        var shapeSize = shapeRow is { } r && shapes.TryGetValue(r, out var lowered) ? lowered : shape.Radius;
                        var pointSize = PointSize(chain, n, tip, sizeRow, held, scales, points);
                        var deficit = Margin - (Vector3.Distance(at, shape.Nearest(at)) - pointSize - shapeSize);
                        if (deficit <= Slack)
                            continue;
                        // The shape gives way first, down to the smallest size.
                        if (shapeRow is { } row && shapeSize > PhysicsFit.Smallest)
                        {
                            var give = Math.Min(deficit, shapeSize - PhysicsFit.Smallest);
                            shapes[row] = shapeSize - give;
                            deficit -= give;
                            var point = n == tip ? "tip" : $"point {n + 1}";
                            reasons.TryAdd(row, $" Kept 1 cm off the {Side(chain.Nodes[0].Bone.Text)}{PhysicsFit.PartNoun(kind)}'s {point}, which it would push all the time.");
                        }
                        // Then the point, when a chosen row sets its size.
                        if (deficit > Slack && sizeRow is { } size && n <= tip && pointSize > PhysicsFit.Smallest)
                        {
                            var smaller = pointSize - Math.Min(deficit, pointSize - PhysicsFit.Smallest);
                            if (held[size].Target == PhysicsFitTarget.Chain)
                                scales[size] = scales.GetValueOrDefault(size, 1) * smaller / pointSize;
                            else
                                points[(size, n)] = smaller;
                            var label = PhysicsShapeNames.Label(applied.Collision, name, skeleton);
                            reasons.TryAdd(size, $" Kept 1 cm off {char.ToLowerInvariant(label[0])}{label[1..]}, which would push it all the time.");
                        }
                    }
                }
            }
        }
        if (reasons.Count == 0)
            return false;
        foreach (var (row, reason) in reasons)
        {
            var item = held[row];
            var detail = item.Detail.Contains(" Kept 1 cm off ", StringComparison.Ordinal) ? item.Detail : item.Detail + reason;
            var place = PhysicsFit.MovesPlace(chosen, item);
            var tipNow = item.FittedWith(place);
            switch (item.Target)
            {
                case PhysicsFitTarget.Capsule or PhysicsFitTarget.Sphere:
                    held[row] = item with { Fitted = shapes[row], Detail = detail };
                    break;
                case PhysicsFitTarget.Chain:
                    held[row] = WithTip(item, Math.Max(PhysicsFit.Smallest, tipNow * scales.GetValueOrDefault(row, 1)), place) with { Detail = detail };
                    break;
                case PhysicsFitTarget.Points when item.Points is { } sizes:
                    var count = sizes.Count;
                    var lowered = sizes.Select((value, n) => n < count - 1 ? points.GetValueOrDefault((row, n + 1), value) : value).ToList();
                    var tipSize = points.GetValueOrDefault((row, count), tipNow);
                    var changed = WithTip(item, tipSize, place);
                    lowered[^1] = changed.Fitted;
                    held[row] = changed with { Points = lowered, Detail = detail };
                    break;
            }
        }
        return true;
    }

    /// <summary> A row with the tip size <c>Apply</c> writes set: the one for its new place while that's chosen, else the one for where it is. </summary>
    private static PhysicsFitItem WithTip(PhysicsFitItem item, float size, bool place)
        => place && item.FittedAtPlace is not null ? item with { FittedAtPlace = size } : item with { Fitted = size };

    /// <summary> The first chosen row that matches, by its index. </summary>
    private static int? Row(PhysicsFitItem[] held, List<int> picked, Func<PhysicsFitItem, bool> match)
    {
        foreach (var i in picked)
            if (match(held[i]))
                return i;
        return null;
    }

    /// <summary> A point's size so far this round: as the applied rows set it, less what this round already took off. </summary>
    private static float PointSize(PhybChain chain, int n, int tip, int? row, PhysicsFitItem[] held, Dictionary<int, float> scales,
        Dictionary<(int Row, int Node), float> points)
    {
        var radius = chain.Nodes[n].Radius;
        if (row is not { } size || n > tip)
            return radius;
        return held[size].Target == PhysicsFitTarget.Chain ? radius * scales.GetValueOrDefault(size, 1) : points.GetValueOrDefault((size, n), radius);
    }

    /// <summary>
    /// Where a moving point rests: on its bone, or for the tip without one at the chain's offset from the bone before it.
    /// Null for a point the builder can't place (a bone the skeleton lacks, a second point without a bone).
    /// </summary>
    private static Vector3? Place(PhybChain chain, int n, int tip, IReadOnlyDictionary<string, PhysicsFrame> rest)
    {
        var node = chain.Nodes[n];
        if (!node.Bone.IsEmpty)
            return rest.TryGetValue(node.Bone.Text, out var frame) ? frame.Position : null;
        return n == tip && rest.TryGetValue(chain.Nodes[n - 1].Bone.Text, out var before) ? before.Place(chain.LastBoneOffset) : null;
    }

    /// <summary> A capsule's or sphere's place at rest: its size and the nearest point of its core to a place. Null for other shapes. </summary>
    private static (float Radius, Func<Vector3, Vector3> Nearest)? Shape(PhybCollision collision, string name, IReadOnlyDictionary<string, PhysicsFrame> rest)
    {
        if (collision.Capsules.FirstOrDefault(capsule => capsule.Name.Text == name) is { } capsule)
        {
            if (!rest.TryGetValue(capsule.StartBone.Text, out var start) || !rest.TryGetValue(capsule.EndBone.Text, out var end))
                return null;
            var a = start.Place(capsule.StartOffset);
            var b = end.Place(capsule.EndOffset);
            return (capsule.Radius, at =>
            {
                var axis = b - a;
                var t = axis.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(at - a, axis) / axis.LengthSquared(), 0, 1);
                return a + t * axis;
            });
        }
        if (collision.Spheres.FirstOrDefault(sphere => sphere.Name.Text == name) is { } sphere && rest.TryGetValue(sphere.Bone.Text, out var frame))
        {
            var centre = frame.Place(sphere.Offset);
            return (sphere.Thickness, _ => centre);
        }
        return null;
    }

    private static string Side(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) ? "left " : bone.EndsWith("_r", StringComparison.Ordinal) ? "right " : string.Empty;
}
