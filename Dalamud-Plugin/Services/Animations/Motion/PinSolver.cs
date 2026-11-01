using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Bends a pin's chain on one pose so its end reaches an aim, and holds the end's turn. Works on a
/// sample's local transforms and their model space side by side: it rewrites chain bones' local
/// rotations and refreshes model space below them, parents before children, as
/// <see cref="MotionEvaluator.ModelSpace"/> composes it. A limb's turns are the smallest rotations between
/// a segment's old and new direction, the upper bone also rolled so the mid joint only bends about its own
/// hinge, so the lower bone keeps its twist; a chain's even curl turns every bone
/// about one axis, which twists a bent chain's bones a little. Bone lengths come from the pose and never
/// change. A limb's knee or elbow bends only forward, as far as <see cref="MaxBend"/>, in the plane the motion gives it,
/// turned about the line to the aim by a bend angle; other chains have no limits and spread the bend over their bones. Pure.
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
    /// <summary>The most a knee or an elbow bends, in degrees from straight.</summary>
    public const float MaxBend = 150;
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
    /// straight limb), easing short of an aim beyond its reach or so near that it would bend past <see cref="MaxBend"/>.
    /// A limb then turns whole about the line from its upper bone to the aim by <paramref name="bend"/> degrees,
    /// times <paramref name="weight"/>. Other chains use damped CCD. A run of solves of one limb (a drag's frames) passes one
    /// <paramref name="side"/>: each then bends on the plane the one before left, so the limb keeps its side as the aim moves.
    /// </summary>
    public static void Reach(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, Vector3 aim, Vector3 restNormal,
        float weight = 1, float bend = 0, LimbSide? side = null)
    {
        if (!Finite(aim))
            return;
        switch (chain.Shape)
        {
            case PinChainShape.Limb:
                ReachLimb(skeleton, local, model, chain, aim, restNormal, weight, float.IsFinite(bend) ? bend : 0, side);
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
        Reach(skeleton, local, model, chain, aim.Position, restNormal, weight, pin.Bend);
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

    /// <summary>
    /// How far from its root a limb puts its end when aimed <paramref name="d"/> away, at the near end: unchanged down to
    /// the soft zone (<paramref name="zone"/> long) above <paramref name="fold"/>, then easing toward it, never nearer.
    /// </summary>
    public static float FoldDistance(float d, float fold, float zone)
    {
        if (d >= fold + zone)
            return d;
        if (zone <= Tiny)
            return Math.Max(d, fold);
        return fold + zone - zone * (1 - MathF.Exp(-(fold + zone - d) / zone));
    }

    /// <summary>How far apart a limb's root and end are when its mid joint bends <see cref="MaxBend"/> degrees.</summary>
    public static float Folded(float l1, float l2)
    {
        var inside = (180 - MaxBend) * MathF.PI / 180;
        return MathF.Sqrt(Math.Max(0, l1 * l1 + l2 * l2 - 2 * l1 * l2 * MathF.Cos(inside)));
    }

    private static void ReachLimb(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, Vector3 aim, Vector3 restNormal,
        float weight, float bend, LimbSide? side)
    {
        var l1 = Vector3.Distance(model[chain.Mid].Position, model[chain.Root].Position);
        var l2 = Vector3.Distance(model[chain.End].Position, model[chain.Mid].Position);
        var reach = l1 + l2;
        var soft = SoftReach(model, chain);
        var zone = reach - soft;
        // The bend plane is the motion's, read before anything turns, in the upper bone's own space.
        var rootInverse = Quaternion.Inverse(Quaternion.Normalize(model[chain.Root].Rotation));
        var bendNormal = Vector3.Transform(BendNormal(model, chain, restNormal, weight), rootInverse);
        // The mid joint's own hinge in the upper bone's space, and how far the pose bends it: a straight limb's hinge is noise.
        var upper = model[chain.Mid].Position - model[chain.Root].Position;
        var lower = model[chain.End].Position - model[chain.Mid].Position;
        var hinge = Vector3.Transform(Vector3.Cross(upper, lower), rootInverse);
        var bent = l1 < Tiny || l2 < Tiny ? 0 : MathF.Acos(Math.Clamp(Vector3.Dot(upper, lower) / (l1 * l2), -1, 1)) * 180 / MathF.PI;
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
        // The knee or elbow never folds past MaxBend, nor further than the motion folds it already, easing into that as into full reach.
        var now = Vector3.Distance(model[chain.End].Position, a);
        var fold = Math.Min(Folded(l1, l2), now);
        var near = FoldDistance(SoftDistance(d, reach, zone), fold, Math.Min(SoftZone * reach, Math.Max(0, now - fold)));
        var target = a + direction * Math.Max(near, Math.Abs(l1 - l2) + Tiny);

        // Bent on the plane the solve picked, the upper bone rolling the least way. An aim far round the limb can leave that bending the
        // mid joint backwards in its upper bone's own space (the plane stays put while the upper bone swings, so the roll takes the other
        // side): then the mirrored plane, same aim. When neither bends forward, the upper bone rolls as far as it takes to bend it
        // forward, on whichever plane needs the smaller roll.
        var rootLocal = local[chain.Root];
        var midLocal = local[chain.Mid];
        var normal = Vector3.Transform(bendNormal, Quaternion.Normalize(model[chain.Root].Rotation));
        // The way the pose's own hinge has to end up for the mid joint to bend forward: along it, or against it for a pose bent backwards.
        var keep = Vector3.Dot(hinge, bendNormal) < 0 ? -1 : 1;
        float Try(Vector3 plane, int roll, bool again)
        {
            if (again)
            {
                local[chain.Root] = rootLocal;
                local[chain.Mid] = midLocal;
                Refresh(skeleton, local, model, chain.Root);
            }
            return Bend(skeleton, local, model, chain, a, direction, target, plane, hinge, bent, roll, bendNormal);
        }
        if (side is { Normal: var carried } && carried != Vector3.Zero)
        {
            // A run of solves bends on the plane the one before left. A pick made afresh each time swaps the mid joint over to the
            // limb's other side in one solve where the aim passes the plane's normal, and the mirrored plane is that swap: only the roll.
            Try(carried, 0, false);
            if (Backward(model, chain, bendNormal, l1, l2))
                Try(carried, keep, true);
        }
        else
        {
            Try(normal, 0, false);
            if (Backward(model, chain, bendNormal, l1, l2))
            {
                Try(-normal, 0, true);
                if (Backward(model, chain, bendNormal, l1, l2))
                {
                    var mirrored = Try(-normal, keep, true);
                    if (Try(normal, keep, true) > mirrored)
                        Try(-normal, keep, true);
                }
            }
        }
        // Kept before the bend angle turns it, so a run's bend stays the angle asked rather than building up; a limb about straight
        // keeps the plane it had.
        var solved = Vector3.Cross(model[chain.Mid].Position - a, model[chain.End].Position - model[chain.Mid].Position);
        if (side != null && solved.Length() > SteadyBend * l1 * l2)
            side.Normal = Vector3.Normalize(solved);
        // The bend angle turns the whole limb about the line to the aim, the end staying on it; the mid joint bends as before.
        var turn = bend * Math.Clamp(weight, 0, 1);
        if (MathF.Abs(turn) > 1e-4f)
            Rotate(skeleton, local, model, chain.Root, Quaternion.CreateFromAxisAngle(direction, turn * MathF.PI / 180));
    }

    /// <summary>
    /// Bends the limb so its end comes to <paramref name="target"/>: the upper bone turns to put the mid joint on the side of
    /// <paramref name="normal"/>'s plane (model space), rolls so the pose's <paramref name="hinge"/> (upper bone space, bent
    /// <paramref name="bent"/> degrees) lies across that plane, and the lower bone points the end at the target. The roll goes the
    /// least way with <paramref name="roll"/> 0; with 1 or -1 it lays the hinge along the plane's normal or against it, however far
    /// that is, and a pose about straight lays <paramref name="reference"/> (the plane the solve meant, upper bone space) along it.
    /// Returns the roll's full angle, in radians.
    /// </summary>
    private static float Bend(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, PinChain chain, Vector3 a, Vector3 direction,
        Vector3 target, Vector3 normal, Vector3 hinge, float bent, int roll, Vector3 reference)
    {
        var l1 = Vector3.Distance(model[chain.Mid].Position, a);
        var l2 = Vector3.Distance(model[chain.End].Position, model[chain.Mid].Position);
        var reached = Vector3.Distance(target, a);
        var b = model[chain.Mid].Position;
        var side = Side(normal, direction, b - a);
        var cos = Math.Clamp((l1 * l1 + reached * reached - l2 * l2) / (2 * l1 * reached), -1, 1);
        var knee = a + l1 * (cos * direction + MathF.Sqrt(1 - cos * cos) * side);

        Turn(skeleton, local, model, chain.Root, b - a, knee - a);
        // The upper bone rolls about itself, the least way, until the hinge lies across the new bend plane: the mid joint then only
        // bends, and a solve made again from its own result (a drag) never builds up a roll there.
        var plane = Vector3.Cross(knee - a, target - knee);
        var rootRotation = Quaternion.Normalize(model[chain.Root].Rotation);
        var carried = Vector3.Transform(hinge, rootRotation);
        var angle = 0f;
        if (plane.Length() > Tiny && roll == 0 && carried.Length() > Tiny && bent > 0)
        {
            var least = Between(carried, Vector3.Dot(carried, plane) < 0 ? -plane : plane);
            angle = 2 * MathF.Acos(Math.Clamp(Math.Abs(least.W), 0, 1));
            Rotate(skeleton, local, model, chain.Root, MotionEvaluator.Pow(least, AnimationPoseRules.SmoothStep(Math.Clamp(bent / SettleDegrees, 0, 1))));
        }
        else if (plane.Length() > Tiny && roll != 0)
        {
            // A pose about straight has no hinge of its own: the plane the solve meant stands in for it, laid along the new one.
            var own = bent >= SettleDegrees && carried.Length() > Tiny;
            var along = Vector3.Normalize(knee - a);
            var from = own ? carried : Vector3.Transform(reference, rootRotation);
            from -= Vector3.Dot(from, along) * along;
            if (from.Length() > Tiny)
            {
                // About the upper bone itself, which both lie across, so a half turn has no other axis to pick.
                var to = (own ? roll : 1) * plane;
                angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), along), Vector3.Dot(from, to));
                Rotate(skeleton, local, model, chain.Root, Quaternion.CreateFromAxisAngle(along, angle));
                angle = MathF.Abs(angle);
            }
        }
        var mid = model[chain.Mid].Position;
        Turn(skeleton, local, model, chain.Mid, model[chain.End].Position - mid, target - mid);
        return angle;
    }

    /// <summary>
    /// Whether the limb's mid joint bends against <paramref name="normal"/> (the plane the solve meant, in the upper bone's own
    /// space), or square across it, which bends it sideways. Never for a limb about straight, which bends neither way.
    /// </summary>
    private static bool Backward(MotionTransform[] model, PinChain chain, Vector3 normal, float l1, float l2)
    {
        var upper = model[chain.Mid].Position - model[chain.Root].Position;
        var lower = model[chain.End].Position - model[chain.Mid].Position;
        var own = Vector3.Transform(Vector3.Cross(upper, lower), Quaternion.Inverse(Quaternion.Normalize(model[chain.Root].Rotation)));
        if (normal.Length() < Tiny || own.Length() <= SteadyBend * l1 * l2)
            return false;
        return Vector3.Dot(Vector3.Normalize(own), Vector3.Normalize(normal)) < 1e-3f;
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
    public static Quaternion Between(Vector3 from, Vector3 to)
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

/// <summary>
/// The side a limb bent to, carried from one <see cref="PinSolver.Reach"/> to the next of a run (a drag's frames, each solved
/// from the pose the drag began on), so the knee or elbow moves round with the aim rather than swapping sides.
/// </summary>
internal sealed class LimbSide
{
    /// <summary>The bend plane's unit normal the last solve left, in model space, before its bend angle turned it; zero before the first.</summary>
    public Vector3 Normal { get; set; }
}
