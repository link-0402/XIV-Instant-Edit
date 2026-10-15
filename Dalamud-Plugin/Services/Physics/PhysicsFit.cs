using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// What a fit sets: a collision shape's size, the size of one chain's moving points, a chain's swing
/// limit, or where a breast's tip sits.
/// </summary>
internal enum PhysicsFitTarget
{
    Capsule,
    Sphere,
    Chain,
    Swing,
    Place,
}

/// <summary> Where the fit puts a breast's tip. </summary>
internal enum PhysicsTipAt
{
    /// <summary>
    /// On the front of the breast, at the nipple, where presets put it: the tip moves as the nipple does and
    /// settings work like a preset's, but its size covers only the front, so arms can sink into the sides.
    /// </summary>
    Nipple,

    /// <summary>
    /// In the middle of the breast, sized to fill it, so arms bump into it from any side. The tip is shorter,
    /// so the same settings swing the breast more.
    /// </summary>
    Middle,
}

/// <summary>
/// A tip moved to its place on its breast: the chain's new tip offset, how far the tip moves, and the
/// chain bone's parent and rest place in it, which turn the swing cone and a spring aimed at the old
/// tip along with it.
/// </summary>
internal sealed record PhysicsTipMove(Vector3 Offset, float Distance, string Parent, Vector3 BonePosition, Quaternion BoneRotation);

/// <summary>
/// A setting measured on the body: <see cref="Name"/> is the shape's name, or for a chain its bone,
/// with the simulator and chain in <see cref="Simulator"/> and <see cref="Chain"/>. Sizes are radii in
/// model units (metres), swings degrees, tip places their tip length (the move itself in <see cref="Move"/>).
/// A tip's size also has <see cref="FittedAtPlace"/>, the size that fills the part once the same chain's
/// tip moves.
/// </summary>
internal sealed record PhysicsFitItem(PhysicsFitTarget Target, string Name, int Simulator, int Chain, string Label, string Detail, float Current,
    float Fitted, PhysicsTipMove? Move = null, float? FittedAtPlace = null)
{
    /// <summary> The fit differs from the file: half a millimetre, half a degree, or a tip moved half a centimetre. </summary>
    public bool Changes => Target switch
    {
        PhysicsFitTarget.Swing => MathF.Abs(Fitted - Current) >= 0.5f,
        PhysicsFitTarget.Place => Move is { Distance: >= 0.005f },
        _ => MathF.Abs(Fitted - Current) >= 0.0005f || FittedAtPlace is { } moved && MathF.Abs(moved - Current) >= 0.0005f,
    };

    /// <summary> What this item sets, given whether its chain's tip moves too. </summary>
    public float FittedWith(bool placeMoves) => placeMoves && FittedAtPlace is { } moved ? moved : Fitted;
}

/// <summary>
/// The settings a fit measured, and what it couldn't measure, in plain words. <see cref="BreastTips"/>: the
/// file has breast tips the fit can place, so where they go is worth choosing.
/// </summary>
internal sealed record PhysicsFitResult(IReadOnlyList<PhysicsFitItem> Items, IReadOnlyList<string> Notes, bool BreastTips = false);

/// <summary>
/// Sizes a physics file's collision to a measured body. Only collision that body areas or the skirt use
/// counts: hair keeps the shapes the game made for it. The rules, checked on the game's own files and
/// presets:
/// <list type="bullet">
/// <item>A shape becomes the body's own thickness: the middle distance of the skin around it, over the
/// points its bones carry most. The game's shoulder capsules measure exactly their 6 cm this way.</item>
/// <item>A shape the skirt bumps into leaves room for the skirt points' own size, and never shrinks: the
/// game's 2 cm thighs plus the skirt's 4 cm put the skirt on the skin, and its calves keep skirts off.</item>
/// <item>A body area's tip becomes as wide as the part around its chain, but reaches past the skin ahead
/// of it only a little, so it fills a breast as presets do without swelling a broad butt. A tip that sits
/// outside the skin keeps its size: the preset made it so on purpose.</item>
/// </list>
/// Skin counts by area, so a dense nipple mesh doesn't outweigh the breast around it. A helper bone
/// without skin of its own, like the elbow's, is measured on the limb it sits on.
/// Beyond collision, body areas whose swing would let their skin sink into the torso get a lower limit
/// (<see cref="PhysicsClearance"/>); limits are only ever lowered.
/// </summary>
internal static class PhysicsFit
{
    /// <summary> How much of a point a shape's bones must carry for it to count as that limb. </summary>
    private const float LimbWeight = 0.5f;

    /// <summary> How much of a point an area's bones must carry for it to move with the area. </summary>
    private const float PartWeight = 0.1f;

    private const float Smallest = 0.005f;
    private const float Largest = 0.5f;

    public static PhysicsFitResult Measure(PhybFile file, PhysicsBody body, PhysicsSkeleton skeleton, PhysicsTipAt tipAt = PhysicsTipAt.Nipple)
    {
        var items = new List<PhysicsFitItem>();
        var notes = new List<string>();
        var users = ShapeUsers(file);
        var breastTips = false;

        foreach (var capsule in file.Collision.Capsules)
        {
            if (!users.TryGetValue(capsule.Name.Text, out var use) || !use.Fits)
                continue;
            var label = PhysicsShapeNames.Label(file.Collision, capsule.Name.Text, skeleton);
            if (!body.Pose.TryGetValue(capsule.StartBone.Text, out var start) || !body.Pose.TryGetValue(capsule.EndBone.Text, out var end))
            {
                notes.Add($"{capsule.Name.Text}: your skeleton lacks its bones.");
                continue;
            }
            var a = start.Place(capsule.StartOffset);
            var b = end.Place(capsule.EndOffset);
            var thickness = Vector3.Distance(a, b) < 0.01f
                ? AroundPlace(body, skeleton, capsule.StartBone.Text, (a + b) / 2)
                : AroundSegment(body, Region(body, skeleton, PathBones(capsule.StartBone.Text, capsule.EndBone.Text, skeleton)), a, b, 0.1f, 0.9f)
                  ?? AroundLimb(body, skeleton, capsule.StartBone.Text, (a + b) / 2);
            if (thickness is not { } measured)
            {
                notes.Add($"{capsule.Name.Text}: none of your skin moves with {capsule.StartBone.Text}.");
                continue;
            }
            items.Add(Shape(PhysicsFitTarget.Capsule, capsule.Name.Text, label, capsule.Radius, measured, use));
        }

        foreach (var sphere in file.Collision.Spheres)
        {
            if (!users.TryGetValue(sphere.Name.Text, out var use) || !use.Fits)
                continue;
            if (!body.Pose.TryGetValue(sphere.Bone.Text, out var frame))
            {
                notes.Add($"{sphere.Name.Text}: your skeleton lacks {sphere.Bone.Text}.");
                continue;
            }
            if (AroundPlace(body, skeleton, sphere.Bone.Text, frame.Place(sphere.Offset)) is not { } measured)
            {
                notes.Add($"{sphere.Name.Text}: none of your skin moves with {sphere.Bone.Text}.");
                continue;
            }
            items.Add(Shape(PhysicsFitTarget.Sphere, sphere.Name.Text, PhysicsShapeNames.Label(file.Collision, sphere.Name.Text, skeleton),
                sphere.Thickness, measured, use));
        }

        for (var s = 0; s < file.Simulators.Count; s++)
        {
            var simulator = file.Simulators[s];
            if (simulator.Excluded)
                continue;
            for (var c = 0; c < simulator.Chains.Count; c++)
            {
                var chain = simulator.Chains[c];
                if (chain.Nodes.Count < 2 || PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text) is not { Body: true } kind)
                    continue;
                var collides = CollidesOutside(simulator, chain);
                var breast = kind.Part is PhysicsPart.Chest or PhysicsPart.Pecs;
                if (!collides && !breast)
                    continue;
                var root = chain.Nodes[0].Bone.Text;
                var name = $"{kind.Label}{Side(root)}";
                if (!body.Pose.TryGetValue(root, out var rootFrame) || TipPlace(body, chain) is not { } tip)
                {
                    notes.Add($"{name}: your skeleton lacks {root}.");
                    continue;
                }
                var bones = Region(body, skeleton, chain.Nodes.Select(node => node.Bone.Text).Where(bone => bone.Length > 0));
                PhysicsTipMove? move = null;
                float? middleSize = null;
                if (breast && chain.Nodes[^1].Bone.IsEmpty)
                {
                    breastTips = true;
                    if (Front(body, bones, rootFrame.Position) is { } front)
                    {
                        if (tipAt == PhysicsTipAt.Nipple)
                            move = MoveTo(body, skeleton, chain, tip, front);
                        else if (Middle(body, bones, rootFrame.Position, front) is { } middle)
                        {
                            move = MoveTo(body, skeleton, chain, tip, middle.Centre);
                            middleSize = Clamp(middle.Radius);
                        }
                    }
                    if (move is null)
                        notes.Add($"{name}: where its tip belongs couldn't be measured.");
                }
                if (move != null)
                {
                    var to = tipAt == PhysicsTipAt.Nipple ? "onto the front of your breast, where the game's tips sit" : "into the middle of your breast, so its size covers the whole breast";
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Place, root, s, c, $"{name}: tip place",
                        $"Moves the tip {Centimetres(move.Distance)} cm {to}. {LeverText(chain.LastBoneOffset.Length(), move.Offset.Length())}",
                        chain.LastBoneOffset.Length(), move.Offset.Length(), move));
                }
                if (!collides)
                    continue;
                var measured = AroundTip(body, bones, rootFrame.Position, tip, AheadOfSkin(kind.Part));
                float? moved = null;
                if (middleSize is { } filled)
                {
                    // A tip already in the middle takes the middle's size where it is.
                    if (move is { Distance: >= 0.005f })
                        moved = filled;
                    else
                        measured = filled;
                }
                else if (move is { Distance: >= 0.005f } && TipPlace(body, chain, move.Offset) is { } front &&
                         AroundTip(body, bones, rootFrame.Position, front, AheadOfSkin(kind.Part), onSkin: true) is { } there)
                    moved = Clamp(there);
                const string fills = "Fills the part around the tip.";
                if (measured is null)
                    notes.Add($"{name}: none of your skin moves with {root}.");
                else if (float.IsPositiveInfinity(measured.Value))
                {
                    // A tip outside the skin keeps its size, unless the fit moves it onto the skin.
                    if (moved is { } size)
                        items.Add(new PhysicsFitItem(PhysicsFitTarget.Chain, root, s, c, $"{name}: tip size", fills + " Only changes with the tip place.",
                            chain.Nodes[^1].Radius, chain.Nodes[^1].Radius, FittedAtPlace: size));
                    else
                        notes.Add($"{name}: its tip sits outside your skin, so its size stays.");
                }
                else
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Chain, root, s, c, $"{name}: tip size", fills,
                        chain.Nodes[^1].Radius, Clamp(measured.Value), FittedAtPlace: moved));
            }
        }
        Swings(file, body, skeleton, items, notes);
        return new PhysicsFitResult(items, notes, breastTips);
    }

    private static string Centimetres(float metres) => (metres * 100).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a new tip length does to the jiggle. The solver moves the tip by distances, which turn the bone
    /// less the further out the tip sits, so the same settings swing a shorter tip more, by the lengths' ratio.
    /// </summary>
    internal static string LeverText(float from, float to)
    {
        if (!(from > 1e-4f) || !(to > 1e-4f) || MathF.Abs(from / to - 1) < 0.1f)
            return "Tip length barely changes, so the jiggle stays the same.";
        return $"Tip length {Centimetres(from)} to {Centimetres(to)} cm, so the same settings swing the breast about " +
               $"{(from / to).ToString("0.0", CultureInfo.InvariantCulture)}x as much.";
    }

    /// <summary> Body areas whose swing lets their skin sink into the torso get the swing that stays clear. </summary>
    private static void Swings(PhybFile file, PhysicsBody body, PhysicsSkeleton skeleton, List<PhysicsFitItem> items, List<string> notes)
    {
        PhysicsClearance.Torso? torso = null;
        for (var s = 0; s < file.Simulators.Count; s++)
        {
            var simulator = file.Simulators[s];
            if (simulator.Excluded)
                continue;
            for (var c = 0; c < simulator.Chains.Count; c++)
            {
                var chain = simulator.Chains[c];
                if (chain.Nodes.Count < 2 || PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text) is not { Body: true } kind ||
                    kind.Part is not (PhysicsPart.Chest or PhysicsPart.Pecs or PhysicsPart.Butt or PhysicsPart.Belly or PhysicsPart.Thighs))
                    continue;
                torso ??= PhysicsClearance.TorsoOf(body, skeleton);
                if (PhysicsClearance.Degrees(body, skeleton, torso, simulator, c) is not { } clear)
                    continue;
                var current = Math.Min(180, PhysicsKnobs.Read(simulator, c, PhysicsKnob.Swing) ?? 180);
                if (current <= clear + 0.5f)
                    continue;
                var root = chain.Nodes[0].Bone.Text;
                items.Add(new PhysicsFitItem(PhysicsFitTarget.Swing, root, s, c, $"{kind.Label}{Side(root)}: swing",
                    "How far it can swing before your skin there would sink into your body.", current, clear));
            }
        }
        if (torso is { Count: 0 })
            notes.Add("Swing: none of your skin is carried by your hips and spine, so swings weren't measured.");
    }

    /// <summary>
    /// Sets <paramref name="items"/> in the file: chains keep their middle points' share of the tip's size,
    /// a tip's size is the one for its new place when its place is among the items, and a moved tip turns
    /// its swing cone and a spring aimed at it along with it.
    /// </summary>
    public static void Apply(PhybFile file, IEnumerable<PhysicsFitItem> items)
    {
        var chosen = items.ToList();
        foreach (var item in chosen)
        {
            switch (item.Target)
            {
                case PhysicsFitTarget.Capsule:
                    foreach (var capsule in file.Collision.Capsules.Where(shape => shape.Name.Text == item.Name))
                        capsule.Radius = item.Fitted;
                    break;
                case PhysicsFitTarget.Sphere:
                    foreach (var sphere in file.Collision.Spheres.Where(shape => shape.Name.Text == item.Name))
                        sphere.Thickness = item.Fitted;
                    break;
                case PhysicsFitTarget.Chain or PhysicsFitTarget.Swing or PhysicsFitTarget.Place:
                    if (item.Simulator < 0 || item.Simulator >= file.Simulators.Count ||
                        item.Chain < 0 || item.Chain >= file.Simulators[item.Simulator].Chains.Count)
                        break;
                    var simulator = file.Simulators[item.Simulator];
                    var chain = simulator.Chains[item.Chain];
                    if (chain.Nodes.Count < 2 || chain.Nodes[0].Bone.Text != item.Name)
                        break;
                    if (item.Target == PhysicsFitTarget.Place)
                    {
                        if (item.Move is { } move)
                            MoveTip(simulator, item.Chain, move);
                        break;
                    }
                    var knob = item.Target == PhysicsFitTarget.Swing ? PhysicsKnob.Swing : PhysicsKnob.Size;
                    var fitted = item.FittedWith(MovesPlace(chosen, item));
                    // Sizes are metres here and centimetres on the slider.
                    PhysicsKnobs.Write(simulator, item.Chain, knob, knob == PhysicsKnob.Size ? fitted * 100 : fitted);
                    break;
            }
        }
    }

    /// <summary> Whether <paramref name="items"/> move the tip of <paramref name="item"/>'s chain. </summary>
    public static bool MovesPlace(IEnumerable<PhysicsFitItem> items, PhysicsFitItem item)
        => items.Any(other => other.Target == PhysicsFitTarget.Place && other.Simulator == item.Simulator && other.Chain == item.Chain && other.Changes);

    /// <summary>
    /// Puts a chain's tip at its new offset. The bone's swing cone and a spring from its parent that were
    /// aimed at the old tip (within <see cref="PhysicsKnobs.SpringAimLimit"/> degrees) turn to the new one,
    /// the spring keeping its reach past the tip; anything aimed elsewhere stays.
    /// </summary>
    private static void MoveTip(PhybSimulator simulator, int index, PhysicsTipMove move)
    {
        var chain = simulator.Chains[index];
        // In the parent's space, from the bone: where the tip rests now and where it will.
        var from = Vector3.Transform(chain.LastBoneOffset, move.BoneRotation);
        var to = Vector3.Transform(move.Offset, move.BoneRotation);
        chain.LastBoneOffset = move.Offset;
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
            return;
        var turn = Between(from, to);
        var reach = to.Length() / from.Length();
        var bone = chain.Nodes[^2];
        if (Degrees(bone.ConeAxisOffset, from) <= PhysicsKnobs.SpringAimLimit)
            bone.ConeAxisOffset = Vector3.Transform(bone.ConeAxisOffset, turn);
        foreach (var spring in simulator.Attracts.Where(attract => attract.Chain == index && attract.Node == chain.Nodes.Count - 1 &&
                                                                   attract.Bone.Text == move.Parent))
        {
            var aim = spring.Offset - move.BonePosition;
            if (Degrees(aim, from) <= PhysicsKnobs.SpringAimLimit)
                spring.Offset = move.BonePosition + Vector3.Transform(aim, turn) * reach;
        }
    }

    private static float Degrees(Vector3 a, Vector3 b)
        => a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f
            ? float.PositiveInfinity
            : PhysicsTemplates.Degrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)));

    /// <summary> The shortest turn from one direction to another. </summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var axis = Vector3.Cross(from, to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (axis.LengthSquared() < 1e-12f)
            return dot > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(PhysicsOverlayModel.Basis(from).U, MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    /// <summary> Who bumps into a shape: body areas, the skirt (with its points' largest size), or only others such as hair. </summary>
    private sealed class ShapeUse
    {
        public bool Body;
        public float SkirtSize = -1;
        public bool Fits => Body || SkirtSize >= 0;
    }

    private static Dictionary<string, ShapeUse> ShapeUsers(PhybFile file)
    {
        var users = new Dictionary<string, ShapeUse>(StringComparer.Ordinal);
        foreach (var simulator in file.Simulators.Where(simulator => !simulator.Excluded))
            foreach (var chain in simulator.Chains)
            {
                if (chain.Nodes.Count < 2)
                    continue;
                var kind = PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text);
                var skirt = kind?.Part == PhysicsPart.Skirt;
                if (kind is not { Body: true } && !skirt)
                    continue;
                for (var n = 1; n < chain.Nodes.Count; n++)
                    foreach (var shape in Shapes(simulator, chain, chain.Nodes[n]))
                    {
                        if (!users.TryGetValue(shape, out var use))
                            users[shape] = use = new ShapeUse();
                        if (skirt)
                            use.SkirtSize = Math.Max(use.SkirtSize, chain.Nodes[n].Radius);
                        else
                            use.Body = true;
                    }
            }
        return users;
    }

    /// <summary> The shapes a moving point stays out of, by its bits over its simulator's and its chain's own shapes. </summary>
    private static IEnumerable<string> Shapes(PhybSimulator simulator, PhybChain chain, PhybNode node)
        => PhysicsCollisions.Shapes(simulator, chain, node).Where(reference => reference.Type != PhybCollisionType.Inside)
            .Select(reference => reference.Name.Text);

    private static bool CollidesOutside(PhybSimulator simulator, PhybChain chain)
        => chain.Nodes.Skip(1).Any(node => Shapes(simulator, chain, node).Any());

    private static PhysicsFitItem Shape(PhysicsFitTarget target, string name, string label, float current, float measured, ShapeUse use)
    {
        if (use.Body || use.SkirtSize < 0)
            return new PhysicsFitItem(target, name, -1, -1, label, "Your skin's thickness around it.", current, Clamp(measured));
        // Skirt points sit on the cloth: the shape plus their size reaches the skin. Thinner legs keep the room the file gives.
        var fitted = Math.Max(current, Clamp(measured - use.SkirtSize));
        return new PhysicsFitItem(target, name, -1, -1, label, $"Leaves {use.SkirtSize * 100:0.#} cm for the skirt's own size.", current, fitted);
    }

    private static float Clamp(float size) => MathF.Round(Math.Clamp(size, Smallest, Largest), 3);

    /// <summary> Where a chain's tip is: its bone, or the place the chain gives it from the bone before it. </summary>
    private static Vector3? TipPlace(PhysicsBody body, PhybChain chain)
    {
        var tip = chain.Nodes[^1];
        if (tip.Bone.Text.Length > 0 && body.Pose.TryGetValue(tip.Bone.Text, out var own))
            return own.Position;
        return TipPlace(body, chain, chain.LastBoneOffset);
    }

    /// <summary> Where a chain's tip would be at <paramref name="offset"/> from the bone before it. </summary>
    private static Vector3? TipPlace(PhysicsBody body, PhybChain chain, Vector3 offset)
        => body.Pose.TryGetValue(chain.Nodes[^2].Bone.Text, out var frame) ? frame.Place(offset) : null;

    /// <summary> The share of a breast's skin, furthest out along it, whose middle is its front. </summary>
    private const float FrontShare = 0.03f;

    /// <summary>
    /// A breast's front: the middle of the skin furthest out (the outer <see cref="FrontShare"/> by area)
    /// along the breast's centre line, which runs from the bone through the middle of the skin it carries
    /// most. Null without that skin.
    /// </summary>
    private static Vector3? Front(PhysicsBody body, IReadOnlySet<int> bones, Vector3 root)
    {
        var points = new List<int>();
        var middle = Vector3.Zero;
        var total = 0f;
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (body.WeightOn(i, bones) < LimbWeight)
                continue;
            points.Add(i);
            middle += body.Points[i] * body.Areas[i];
            total += body.Areas[i];
        }
        if (!(total > 0))
            return null;
        var axis = middle / total - root;
        if (axis.LengthSquared() < 1e-8f)
            return null;
        axis = Vector3.Normalize(axis);
        if (Percentile(points.Select(i => (Vector3.Dot(body.Points[i] - root, axis), body.Areas[i])).ToList(), 1 - FrontShare) is not { } edge)
            return null;
        var front = Vector3.Zero;
        var area = 0f;
        // A millimetre short of the edge, so skin equally far out counts whole and not by rounding.
        foreach (var i in points.Where(i => Vector3.Dot(body.Points[i] - root, axis) >= edge - 0.001f))
        {
            front += body.Points[i] * body.Areas[i];
            area += body.Areas[i];
        }
        return area > 0 ? front / area : null;
    }

    /// <summary> How far along a breast's centre line, from its bone to its front, its middle may sit. </summary>
    private const float MiddleFrom = 0.4f, MiddleTo = 0.9f;

    /// <summary>
    /// A breast's middle and size: the ball on its centre line whose surface follows the breast's skin
    /// best (the skin's distances from it spread least, each point counting by area and by how much the
    /// breast carries it), with the middle distance of that skin as its size. The middle stays between
    /// <see cref="MiddleFrom"/> and <see cref="MiddleTo"/> of the way to the front, so the tip keeps a
    /// lever to swing the breast by. Null without skin.
    /// </summary>
    private static (Vector3 Centre, float Radius)? Middle(PhysicsBody body, IReadOnlySet<int> bones, Vector3 root, Vector3 front)
    {
        var line = front - root;
        var length = line.Length();
        if (length < 0.01f)
            return null;
        var points = new List<(Vector3 Point, float Weight)>();
        for (var i = 0; i < body.Points.Length; i++)
            if (body.WeightOn(i, bones) is var weight and >= PartWeight)
                points.Add((body.Points[i], weight * body.Areas[i]));
        if (points.Count == 0)
            return null;
        (Vector3 Centre, float Spread)? best = null;
        // Two millimetre steps.
        var steps = Math.Max(1, (int)(length * (MiddleTo - MiddleFrom) / 0.002f));
        for (var step = 0; step <= steps; step++)
        {
            var centre = root + line * (MiddleFrom + (MiddleTo - MiddleFrom) * step / steps);
            float total = 0, sum = 0, squares = 0;
            foreach (var (point, weight) in points)
            {
                var distance = Vector3.Distance(point, centre);
                total += weight;
                sum += distance * weight;
                squares += distance * distance * weight;
            }
            var mean = sum / total;
            var spread = squares / total - mean * mean;
            if (best is null || spread < best.Value.Spread)
                best = (centre, spread);
        }
        if (best is not { } found)
            return null;
        var distances = points.Select(pair => (Vector3.Distance(pair.Point, found.Centre), pair.Weight)).ToList();
        return Percentile(distances, 0.5f) is { } radius ? (found.Centre, radius) : null;
    }

    /// <summary>
    /// Moving a chain's tip to <paramref name="place"/>. Only a tip the chain places itself, without a bone
    /// of its own, moves. Null without the bones.
    /// </summary>
    private static PhysicsTipMove? MoveTo(PhysicsBody body, PhysicsSkeleton skeleton, PhybChain chain, Vector3 tip, Vector3 place)
    {
        if (!chain.Nodes[^1].Bone.IsEmpty)
            return null;
        var before = chain.Nodes[^2].Bone.Text;
        if (!body.Pose.TryGetValue(before, out var frame) || skeleton.Get(before) is not { Parent.Length: > 0 } bone)
            return null;
        var offset = Vector3.Transform(place - frame.Position, Quaternion.Conjugate(frame.Rotation));
        return new PhysicsTipMove(offset, Vector3.Distance(place, tip), bone.Parent, bone.Position, Quaternion.Normalize(bone.Rotation));
    }

    /// <summary>
    /// The middle distance of the limb's skin from the segment: points the bones carry most, beside the
    /// part of the segment from <paramref name="from"/> to <paramref name="to"/> (0 is a, 1 is b).
    /// </summary>
    private static float? AroundSegment(PhysicsBody body, IReadOnlySet<int> bones, Vector3 a, Vector3 b, float from, float to)
    {
        var axis = b - a;
        var lengthSquared = axis.LengthSquared();
        var values = new List<(float Value, float Area)>();
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (body.WeightOn(i, bones) < LimbWeight)
                continue;
            var t = Vector3.Dot(body.Points[i] - a, axis) / lengthSquared;
            if (t >= from && t <= to)
                values.Add((Vector3.Distance(body.Points[i], a + t * axis), body.Areas[i]));
        }
        return Percentile(values, 0.5f);
    }

    /// <summary>
    /// The thickness around a shape at one place: the middle distance of the skin its bone carries most,
    /// or, for a helper bone without skin of its own (like the elbow's), of the limb it sits on, across
    /// three centimetres either side of the place.
    /// </summary>
    private static float? AroundPlace(PhysicsBody body, PhysicsSkeleton skeleton, string bone, Vector3 place)
    {
        var values = new List<(float Value, float Area)>();
        var own = Region(body, skeleton, [bone]);
        for (var i = 0; i < body.Points.Length; i++)
            if (body.WeightOn(i, own) >= LimbWeight)
                values.Add((Vector3.Distance(body.Points[i], place), body.Areas[i]));
        return Percentile(values, 0.5f) ?? AroundLimb(body, skeleton, bone, place);
    }

    /// <summary> The middle distance of the skin of the limb a bone sits on, across three centimetres either side of a place. </summary>
    private static float? AroundLimb(PhysicsBody body, PhysicsSkeleton skeleton, string bone, Vector3 place)
    {
        if (Limb(skeleton, bone) is not var (limb, next) || !body.Pose.TryGetValue(limb, out var a) || !body.Pose.TryGetValue(next, out var b))
            return null;
        var length = Vector3.Distance(a.Position, b.Position);
        if (length < 0.01f)
            return null;
        var at = Vector3.Dot(place - a.Position, b.Position - a.Position) / (length * length);
        var reach = 0.03f / length;
        return AroundSegment(body, Region(body, skeleton, [limb]), a.Position, b.Position, at - reach, at + reach);
    }

    /// <summary>
    /// The size that fills a body area's part around its tip: as wide as the part is around its chain
    /// (nine tenths of the skin that moves with it, a tenth of its weight or more, lies within), but
    /// sticking out past the skin ahead of the tip by no more than <paramref name="ahead"/>, so a tip
    /// near the surface of a broad part, like a butt, stays small. Infinity when the tip lies past the
    /// part's skin, and null without any such skin. A tip the fit put on the front of the skin
    /// (<paramref name="onSkin"/>) is never outside: it sits on the skin furthest out, past most of it.
    /// </summary>
    private static float? AroundTip(PhysicsBody body, IReadOnlySet<int> bones, Vector3 root, Vector3 tip, float ahead, bool onSkin = false)
    {
        var axis = tip - root;
        var length = axis.Length();
        if (length < 1e-4f)
            return null;
        axis /= length;
        var along = new List<(float Value, float Area)>();
        var side = new List<(float Value, float Area)>();
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (body.WeightOn(i, bones) < PartWeight)
                continue;
            var offset = body.Points[i] - root;
            var forward = Vector3.Dot(offset, axis);
            along.Add((forward, body.Areas[i]));
            side.Add(((offset - forward * axis).Length(), body.Areas[i]));
        }
        if (Percentile(along, 0.9f) is not { } front || Percentile(side, 0.9f) is not { } width)
            return null;
        var depth = front - length;
        return depth < -0.005f && !onSkin ? float.PositiveInfinity : Math.Min(width, Math.Max(depth, 0) + ahead);
    }

    /// <summary>
    /// How far a tip's size may reach past the skin ahead of it. Breast presets reach 3 to 4.5 cm past
    /// the nipple, where little bumps into it, to cover the sides the arms meet; hands meet a butt, belly
    /// or thigh head on, so those stay close to the skin.
    /// </summary>
    private static float AheadOfSkin(PhysicsPart part) => part is PhysicsPart.Chest or PhysicsPart.Pecs ? 0.04f : 0.01f;

    /// <summary> The limb a bone sits on: the nearest joint (j_ bone) at or above it, and the joint after it. </summary>
    private static (string Limb, string Next)? Limb(PhysicsSkeleton skeleton, string bone)
    {
        foreach (var joint in Ancestors(bone, skeleton))
        {
            if (!joint.StartsWith("j_", StringComparison.Ordinal))
                continue;
            var next = skeleton.Body.FirstOrDefault(child => child.Parent == joint && child.Name.StartsWith("j_", StringComparison.Ordinal) &&
                                                             !child.Name.StartsWith("j_buki", StringComparison.Ordinal) &&
                                                             !child.Name.StartsWith("j_sk_", StringComparison.Ordinal));
            return next is null ? null : (joint, next.Name);
        }
        return null;
    }

    /// <summary> The value below which <paramref name="share"/> of the area lies; null without any. </summary>
    internal static float? Percentile(List<(float Value, float Area)> values, float share)
    {
        var total = values.Sum(value => value.Area);
        if (values.Count == 0 || !(total > 0))
            return null;
        values.Sort((x, y) => x.Value.CompareTo(y.Value));
        var reached = 0f;
        foreach (var (value, area) in values)
        {
            reached += area;
            if (reached >= share * total)
                return value;
        }
        return values[^1].Value;
    }

    /// <summary>
    /// The body bones a shape or area stands for: each named bone, and the bones under it that aren't
    /// joints of their own (j_ bones such as the next limb or the fingers), like a breast's iv_c_mune.
    /// </summary>
    internal static HashSet<int> Region(PhysicsBody body, PhysicsSkeleton skeleton, IEnumerable<string> bones)
    {
        var children = skeleton.Body.ToLookup(bone => bone.Parent, StringComparer.Ordinal);
        var set = new HashSet<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in bones)
            Add(bone, true);
        return set;

        void Add(string bone, bool named)
        {
            if ((!named && bone.StartsWith("j_", StringComparison.Ordinal)) || !seen.Add(bone))
                return;
            if (body.BoneIndex(bone) is var index and >= 0)
                set.Add(index);
            foreach (var child in children[bone])
                Add(child.Name, false);
        }
    }

    /// <summary> The bones from one bone to another through the skeleton: up from each to where they meet. </summary>
    internal static IReadOnlyList<string> PathBones(string from, string to, PhysicsSkeleton skeleton)
    {
        var up = Ancestors(from, skeleton);
        var other = Ancestors(to, skeleton);
        var meet = up.FirstOrDefault(other.Contains);
        var path = up.TakeWhile(bone => bone != meet).Concat(other.TakeWhile(bone => bone != meet)).ToList();
        if (meet is not null)
            path.Add(meet);
        return path.Distinct().ToList();
    }

    private static List<string> Ancestors(string bone, PhysicsSkeleton skeleton)
    {
        var result = new List<string>();
        for (var current = bone; current.Length > 0 && result.Count < 512; current = skeleton.Get(current)?.Parent ?? string.Empty)
            result.Add(current);
        return result;
    }

    private static string Side(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) ? ", left" : bone.EndsWith("_r", StringComparison.Ordinal) ? ", right" : string.Empty;
}
