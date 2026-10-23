using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Bends a pin's chain on one pose so its end reaches an aim, and holds the end's turn. Works on a
/// sample's local transforms and their model space side by side: it rewrites chain bones' local
/// rotations and refreshes model space below them, parents before children, as
/// <see cref="MotionEvaluator.ModelSpace"/> composes it. A limb's turns are the smallest rotations between
/// a segment's old and new direction, so its bones keep their twist; a chain's even curl turns every bone
/// about one axis, which twists a bent chain's bones a little. Bone lengths come from the pose and never
/// change. No joint limits: limbs bend in the plane the motion gives them, other chains spread
/// the bend over their bones. Pure.
/// </summary>
internal static class PinSolver
{
    /// <summary>The share of a limb's length over which it eases straight, so it never snaps.</summary>
    public const float SoftZone = 0.02f;
    public const int CcdPasses = 16;
    /// <summary>How close a chain's end has to come to its aim, in metres (0.1 mm).</summary>
    public const float CcdTolerance = 0.0001f;
    /// <summary>Under this bend, in degrees, a limb's own bend plane is noise: the plane settles onto the rest pose's.</summary>
    public const float SettleDegrees = 5;
    // sin(1°): a chain bent about this much curls its own way rather than the top bone's steadying way.
    private const float SteadyBend = 0.0175f;
    private const float Tiny = 1e-6f;

    /// <summary>
    /// How far from its root a limb <paramref name="reach"/> long puts its end when aimed
    /// <paramref name="d"/> away: unchanged up to the soft zone, then easing toward full reach, never past it.
    /// </summary>
    public static float SoftDistance(float d, float reach) => SoftDistance(d, reach, SoftZone * reach);

    /// <summary><see cref="SoftDistance(float, float)"/> with a soft zone <paramref name="zone"/> long; none never goes past reach.</summary>
    public static float SoftDistance(float d, float reach, float zone)
    {
        if (d <= reach - zone)
            return d;
        if (zone <= Tiny)
            return Math.Min(d, reach);
        return reach - zone + zone * (1 - MathF.Exp(-(d - reach + zone) / zone));
    }

    /// <summary>The limb's bend normal in the rest pose, in its upper bone's own space; zero when the rest limb is straight.</summary>
    public static Vector3 RestNormal(SkeletonDescription skeleton, PinChain chain)
    {
        if (chain.Shape != PinChainShape.Limb)
            return Vector3.Zero;
        var rest = MotionEvaluator.ModelSpace(skeleton, skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray());
        var a = rest[chain.Root].Position;
        var normal = Vector3.Cross(rest[chain.Mid].Position - a, rest[chain.End].Position - a);
        var scale = Vector3.Distance(rest[chain.Mid].Position, a) * Vector3.Distance(rest[chain.End].Position, rest[chain.Mid].Position);
        if (!(normal.Length() > 1e-4f * scale) || scale < Tiny)
            return Vector3.Zero;
        return Vector3.Normalize(Vector3.Transform(normal, Quaternion.Inverse(Quaternion.Normalize(rest[chain.Root].Rotation))));
    }

    /// <summary>
    /// Bends <paramref name="chain"/> so its end comes to <paramref name="aim"/> (model space). A limb first
    /// turns the bones above it, evenly from the top and only as far as it takes to bring the aim within
    /// the limb's reach, then bends on the motion's own plane (<paramref name="restNormal"/> decides for a
    /// straight limb), easing short of an aim beyond its reach. Other chains use damped CCD.
    /// </summary>
    public static void Reach(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, Vector3 aim, Vector3 restNormal,
        float weight = 1)
    {
        if (!Finite(aim))
            return;
        switch (chain.Shape)
        {
            case PinChainShape.Limb:
                ReachLimb(skeleton, local, model, chain, aim, restNormal, weight);
                break;
            case PinChainShape.Chain:
                ReachChain(skeleton, local, model, chain.Bones.AsSpan(), chain.End, aim);
                break;
        }
    }

    /// <summary><paramref name="bone"/>'s transform in its pin's space: model space, or relative to <paramref name="relativeTo"/>'s model transform when that is a bone.</summary>
    public static MotionTransform PinSpace(MotionTransform[] model, int bone, int relativeTo) =>
        relativeTo < 0 ? model[bone] : MotionEvaluator.Relative(model[relativeTo], model[bone]);

    /// <summary>
    /// One pin on one pose: aims its bone from where the pose has it toward <paramref name="target"/> (in the
    /// pin's space) by <paramref name="weight"/>, on the straight path, bends the chain to reach the aim and,
    /// when the pin holds its turn, turns the bone to the aim's turn. Nothing at no weight or with nothing to bend.
    /// </summary>
    public static void Pin(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, MotionPin pin, int relativeTo,
        MotionTransform target, float weight, Vector3 restNormal)
    {
        if (!(weight > 0) || chain.Shape == PinChainShape.None)
            return;
        weight = Math.Min(weight, 1);
        var spot = relativeTo < 0 ? target : MotionEvaluator.Compose(model[relativeTo], target);
        var aim = MotionEvaluator.Lerp(model[chain.End], spot, weight);
        Reach(skeleton, local, model, chain, aim.Position, restNormal, weight);
        if (pin.HoldTurn)
            HoldTurn(skeleton, local, model, chain.End, aim.Rotation);
    }

    /// <summary>Turns <paramref name="bone"/> so its model rotation is <paramref name="rotation"/> under its parent as it stands, and refreshes what hangs below.</summary>
    public static void HoldTurn(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int bone, Quaternion rotation)
    {
        var parent = skeleton.Bones[bone].Parent;
        var parentRotation = parent >= 0 ? Quaternion.Normalize(model[parent].Rotation) : Quaternion.Identity;
        local[bone] = local[bone] with { Rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * Quaternion.Normalize(rotation)) };
        Refresh(skeleton, local, model, bone);
    }

    /// <summary>Recomputes <paramref name="model"/> for <paramref name="from"/> and every bone below it; parents come before children.</summary>
    public static void Refresh(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int from)
    {
        var bones = skeleton.Bones;
        Span<bool> below = bones.Length <= 1024 ? stackalloc bool[bones.Length] : new bool[bones.Length];
        below[from] = true;
        var parent = bones[from].Parent;
        model[from] = parent < 0 ? local[from] : MotionEvaluator.Compose(model[parent], local[from]);
        for (var b = from + 1; b < bones.Length; b++)
        {
            parent = bones[b].Parent;
            if (parent < 0 || !below[parent])
                continue;
            below[b] = true;
            model[b] = MotionEvaluator.Compose(model[parent], local[b]);
        }
    }

    /// <summary>
    /// How far from its upper bone a limb chain reaches its end exactly, before the soft zone eases it
    /// short of full reach: the whole limb's length less the zone. The zone never starts below where the
    /// motion has the limb, so an aim at the motion's own spot leaves it there: a nearly straight standing
    /// leg isn't pulled short, and a fade doesn't pop.
    /// </summary>
    public static float SoftReach(MotionTransform[] model, PinChain chain)
    {
        var reach = Vector3.Distance(model[chain.Mid].Position, model[chain.Root].Position) +
                    Vector3.Distance(model[chain.End].Position, model[chain.Mid].Position);
        var zone = Math.Min(SoftZone * reach, Math.Max(0, reach - Vector3.Distance(model[chain.End].Position, model[chain.Root].Position)));
        return reach - zone;
    }

    private static void ReachLimb(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, Vector3 aim, Vector3 restNormal,
        float weight)
    {
        var l1 = Vector3.Distance(model[chain.Mid].Position, model[chain.Root].Position);
        var l2 = Vector3.Distance(model[chain.End].Position, model[chain.Mid].Position);
        var reach = l1 + l2;
        var soft = SoftReach(model, chain);
        var zone = reach - soft;
        // The bend plane is the motion's, read before anything turns, in the upper bone's own space.
        var rootInverse = Quaternion.Inverse(Quaternion.Normalize(model[chain.Root].Rotation));
        var bendNormal = Vector3.Transform(BendNormal(model, chain, restNormal, weight), rootInverse);
        // Bones above the limb turn only to bring the aim within the limb's reach.
        var above = chain.Bones.AsSpan(0, chain.Bones.Length - 2);
        if (!above.IsEmpty && Vector3.Distance(aim, model[chain.Root].Position) > soft)
        {
            var root = model[chain.Root].Position;
            var spot = aim + Vector3.Normalize(root - aim) * soft;
            Ccd(skeleton, local, model, above, chain.Root, spot, CcdTolerance);
        }

        var a = model[chain.Root].Position;
        var toAim = aim - a;
        var d = toAim.Length();
        if (d < Tiny || reach < Tiny)
            return;
        var direction = toAim / d;
        if (l1 < Tiny || l2 < Tiny)
        {
            // A limb with a segment of no length can't bend; it only points at the aim.
            PointAt(skeleton, local, model, chain.Root, l1 < Tiny ? chain.End : chain.Mid, a, aim);
            return;
        }
        var target = a + direction * Math.Max(SoftDistance(d, reach, zone), Math.Abs(l1 - l2) + Tiny);
        var reached = Vector3.Distance(target, a);

        var b = model[chain.Mid].Position;
        var side = Side(Vector3.Transform(bendNormal, Quaternion.Normalize(model[chain.Root].Rotation)), direction, b - a);
        var cos = Math.Clamp((l1 * l1 + reached * reached - l2 * l2) / (2 * l1 * reached), -1, 1);
        var knee = a + l1 * (cos * direction + MathF.Sqrt(1 - cos * cos) * side);

        Turn(skeleton, local, model, chain.Root, b - a, knee - a);
        var mid = model[chain.Mid].Position;
        Turn(skeleton, local, model, chain.Mid, model[chain.End].Position - mid, target - mid);
    }

    /// <summary>
    /// The plane the limb bends in, as its unit normal (model space): the motion's own, turned to the side
    /// the rest pose bends so a knee or elbow never bends backwards, and settling onto the rest pose's plane
    /// (carried by the upper bone) as the motion's bend falls under <see cref="SettleDegrees"/>, where its
    /// own plane is noise. A straight rest limb takes the upper bone's +Z crossed with the limb instead.
    /// The correction comes in with <paramref name="weight"/>, so at no pull the motion's own plane stays.
    /// Zero when nothing gives a plane.
    /// </summary>
    private static Vector3 BendNormal(MotionTransform[] model, PinChain chain, Vector3 restNormal, float weight)
    {
        var a = model[chain.Root].Position;
        var upper = model[chain.Mid].Position - a;
        var lower = model[chain.End].Position - model[chain.Mid].Position;
        var whole = model[chain.End].Position - a;
        var raw = Vector3.Cross(upper, whole);
        var own = raw.Length() > 1e-6f * upper.Length() * whole.Length() ? Vector3.Normalize(raw) : Vector3.Zero;
        var rootRotation = Quaternion.Normalize(model[chain.Root].Rotation);
        var carried = restNormal != Vector3.Zero
            ? Vector3.Transform(restNormal, rootRotation)
            : Vector3.Cross(Vector3.Transform(Vector3.UnitZ, rootRotation), whole);
        Vector3 goal;
        if (carried.Length() > Tiny)
        {
            carried = Vector3.Normalize(carried);
            var forward = own == Vector3.Zero ? carried : Vector3.Dot(own, carried) < 0 ? -own : own;
            var cos = upper.Length() < Tiny || lower.Length() < Tiny ? 1 : Vector3.Dot(Vector3.Normalize(upper), Vector3.Normalize(lower));
            var bend = MathF.Acos(Math.Clamp(cos, -1, 1)) * 180 / MathF.PI;
            goal = Vector3.Lerp(carried, forward, AnimationPoseRules.SmoothStep(Math.Clamp(bend / SettleDegrees, 0, 1)));
            goal = goal.Length() > Tiny ? Vector3.Normalize(goal) : forward;
        }
        else
            goal = own;
        if (own == Vector3.Zero)
            return goal;
        // Turned toward the goal rather than blended: a goal about opposite (a knee bent backwards) would blend
        // through nothing and flip the knee in one sample. Opposite planes turn about the limb, which both lie across.
        var axis = Vector3.Cross(own, goal);
        var angle = MathF.Atan2(axis.Length(), Vector3.Dot(own, goal));
        if (axis.Length() < 1e-4f)
            axis = whole;
        return Vector3.Normalize(Vector3.Transform(own, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Math.Clamp(weight, 0, 1) * angle)));
    }

    /// <summary>
    /// The unit direction, at right angles to <paramref name="direction"/>, the mid joint bends toward in
    /// the plane of <paramref name="normal"/>; when the aim lies along the normal, the side
    /// <paramref name="upper"/> is on now.
    /// </summary>
    private static Vector3 Side(Vector3 normal, Vector3 direction, Vector3 upper)
    {
        var side = Vector3.Cross(direction, normal);
        if (side.Length() > Tiny)
            return Vector3.Normalize(side);
        side = upper - Vector3.Dot(upper, direction) * direction;
        if (side.Length() > Tiny)
            return Vector3.Normalize(side);
        side = Vector3.Cross(direction, Vector3.UnitX);
        return Vector3.Normalize(side.Length() > Tiny ? side : Vector3.Cross(direction, Vector3.UnitY));
    }

    /// <summary>
    /// Damped CCD: each pass turns each bone, top first, by a share 1 / (bones left) of the smallest turn
    /// that points <paramref name="end"/> at <paramref name="aim"/> from that bone's joint, so the bend
    /// spreads over the chain. Stops once the end is within <paramref name="tolerance"/>.
    /// </summary>
    private static void Ccd(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<int> bones, int end, Vector3 aim,
        float tolerance)
    {
        for (var pass = 0; pass < CcdPasses; pass++)
        {
            if (Vector3.Distance(model[end].Position, aim) <= tolerance)
                return;
            for (var i = 0; i < bones.Length; i++)
            {
                var joint = model[bones[i]].Position;
                var from = model[end].Position - joint;
                var to = aim - joint;
                if (from.Length() < Tiny || to.Length() < Tiny)
                    continue;
                var turn = MotionEvaluator.Pow(Between(from, to), 1f / (bones.Length - i));
                Rotate(skeleton, local, model, bones[i], turn);
            }
        }
    }

    /// <summary>
    /// A chain other than a limb: damped CCD spreads the turn toward the aim over the chain. CCD can't pull
    /// a nearly straight chain in, or straighten one out, to an aim nearer or farther than its end: from
    /// each joint the end and the aim then lie on almost one line. What is left is mostly that distance,
    /// so the chain then curls (or uncurls) evenly until its top joint is as far from its end as from the
    /// aim, and its top bone points it at the aim, which with the right distance lands the end on it.
    /// </summary>
    private static void ReachChain(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<int> bones, int end, Vector3 aim)
    {
        var count = bones.Length;
        // The axis the chain curls about, read from the motion before CCD turns anything, in the top bone's own space.
        var curl = count < 2 ? Vector3.Zero : Vector3.Transform(CurlAxis(skeleton, model, bones, end), Quaternion.Inverse(Quaternion.Normalize(model[bones[0]].Rotation)));
        Ccd(skeleton, local, model, bones, end, aim, CcdTolerance);
        if (Vector3.Distance(model[end].Position, aim) <= CcdTolerance)
            return;
        if (curl != Vector3.Zero)
            Curl(skeleton, local, model, bones, end, aim, Vector3.Transform(curl, Quaternion.Normalize(model[bones[0]].Rotation)));
        var top = model[bones[0]].Position;
        Turn(skeleton, local, model, bones[0], model[end].Position - top, aim - top);
    }

    /// <summary>
    /// The axis a chain curls about (model space): the way the motion bends it, summed over its joints,
    /// steadied by a fixed axis of the top bone (its own axis most across the chain) that decides when the
    /// chain is about straight, so the curl keeps one side from sample to sample.
    /// </summary>
    private static Vector3 CurlAxis(SkeletonDescription skeleton, MotionTransform[] model, ReadOnlySpan<int> bones, int end)
    {
        var count = bones.Length;
        var bend = Vector3.Zero;
        var scale = 0f;
        for (var i = 0; i + 1 < count; i++)
        {
            var next = i + 2 < count ? bones[i + 2] : end;
            var segment = model[bones[i + 1]].Position - model[bones[i]].Position;
            var following = model[next].Position - model[bones[i + 1]].Position;
            bend += Vector3.Cross(segment, following);
            scale += segment.Length() * following.Length();
        }
        // The top bone's own axis most across its first segment: fixed for the chain, carried by the bone.
        var along = skeleton.Bones[bones[1]].Reference.Position;
        var steady = Vector3.UnitX;
        foreach (var candidate in (ReadOnlySpan<Vector3>)[Vector3.UnitY, Vector3.UnitZ])
            if (MathF.Abs(Vector3.Dot(candidate, along)) < MathF.Abs(Vector3.Dot(steady, along)))
                steady = candidate;
        steady = Vector3.Transform(steady, Quaternion.Normalize(model[bones[0]].Rotation));
        // A bend of about a degree outweighs the steadying axis.
        return bend + steady * (scale * SteadyBend);
    }

    /// <summary>
    /// Curls the chain evenly, the same turn at every joint about <paramref name="axis"/> (or back, to
    /// straighten it), by the smallest angle that puts the end as far from the top joint as
    /// <paramref name="aim"/> is. Leaves it when no curl gets there.
    /// </summary>
    private static void Curl(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<int> bones, int end, Vector3 aim,
        Vector3 axis)
    {
        var count = bones.Length;
        var top = model[bones[0]].Position;
        var chord = model[end].Position - top;
        var want = Vector3.Distance(aim, top);
        if (MathF.Abs(want - chord.Length()) <= CcdTolerance / 2 || chord.Length() < Tiny)
            return;
        // Only the part of the axis across the chain curls it.
        var along = Vector3.Normalize(chord);
        axis -= Vector3.Dot(axis, along) * along;
        if (axis.Length() < Tiny)
            return;
        axis = Vector3.Normalize(axis);
        Span<Vector3> segments = count < 64 ? stackalloc Vector3[count] : new Vector3[count];
        for (var i = 0; i < count; i++)
            segments[i] = model[i + 1 < count ? bones[i + 1] : end].Position - model[bones[i]].Position;

        // Step out from no curl both ways to the first angle past the wanted distance, then halve between.
        const int steps = 64;
        var most = MathF.PI / count;
        var shorter = want < chord.Length();
        float low = 0, high = float.NaN;
        for (var step = 1; step <= steps && float.IsNaN(high); step++)
            foreach (var sign in (ReadOnlySpan<float>)[1, -1])
            {
                var angle = sign * most * step / steps;
                if (Chord(segments, axis, angle) <= want == shorter)
                {
                    high = angle;
                    low = sign * most * (step - 1) / steps;
                    break;
                }
            }
        if (float.IsNaN(high))
            return;
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) / 2;
            if (Chord(segments, axis, middle) <= want == shorter) high = middle;
            else low = middle;
        }
        var turn = Quaternion.CreateFromAxisAngle(axis, (low + high) / 2);
        foreach (var bone in bones)
            Rotate(skeleton, local, model, bone, turn);
    }

    /// <summary>The distance from the top joint to the end once every joint turns <paramref name="angle"/> more about <paramref name="axis"/>.</summary>
    private static float Chord(ReadOnlySpan<Vector3> segments, Vector3 axis, float angle)
    {
        var sum = Vector3.Zero;
        for (var i = 0; i < segments.Length; i++)
            sum += Vector3.Transform(segments[i], Quaternion.CreateFromAxisAngle(axis, angle * (i + 1)));
        return sum.Length();
    }

    /// <summary>Turns <paramref name="bone"/> so the bone <paramref name="toward"/> below it points from <paramref name="joint"/> at <paramref name="aim"/>.</summary>
    private static void PointAt(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int bone, int toward, Vector3 joint, Vector3 aim)
    {
        var from = model[toward].Position - joint;
        if (from.Length() >= Tiny)
            Turn(skeleton, local, model, bone, from, aim - joint);
    }

    /// <summary>Turns <paramref name="bone"/> by the smallest rotation taking <paramref name="from"/> to <paramref name="to"/> (model space).</summary>
    private static void Turn(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int bone, Vector3 from, Vector3 to)
    {
        if (from.Length() < Tiny || to.Length() < Tiny)
            return;
        Rotate(skeleton, local, model, bone, Between(from, to));
    }

    /// <summary>Applies a model-space <paramref name="turn"/> to <paramref name="bone"/> about its joint.</summary>
    private static void Rotate(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int bone, Quaternion turn)
    {
        HoldTurn(skeleton, local, model, bone, Quaternion.Normalize(turn * Quaternion.Normalize(model[bone].Rotation)));
    }

    /// <summary>The smallest rotation taking direction <paramref name="from"/> to direction <paramref name="to"/>.</summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var dot = Vector3.Dot(from, to);
        if (dot < -1 + 1e-6f)
        {
            // Opposite: any axis at right angles will do.
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.Length() < 1e-3f)
                axis = Vector3.Cross(Vector3.UnitY, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1 + dot));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
