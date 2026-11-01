using System.Globalization;
using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// What a fit sets: a collision shape's size, the size of one chain's moving points, a chain's swing
/// limit, where a body area's tip sits, or each moving point's own size. Stored by name in fit records,
/// so new values go last.
/// </summary>
internal enum PhysicsFitTarget
{
    Capsule,
    Sphere,
    Chain,
    Swing,
    Place,
    Points,
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
/// Where the fit puts a collision shape: its offsets from its bones (a sphere has only <see cref="Start"/>)
/// and how far it moves. With <see cref="NewOn"/>, a sphere on that bone the skirt bumps into, added
/// when the file lacks it.
/// </summary>
internal sealed record PhysicsShapeMove(Vector3 Start, Vector3 End, float Distance, string? NewOn = null);

/// <summary>
/// A move for a skirt bone's Customize+ position (in the bone's own axes, metres) that takes it out by
/// <see cref="Distance"/>, as far as the hips there grew. Physics can't move where a skirt starts.
/// </summary>
internal sealed record PhysicsWaistMove(string Bone, Vector3 Translation, float Distance);

/// <summary>
/// A setting measured on the body: <see cref="Name"/> is the shape's name, or for a chain its bone,
/// with the simulator and chain in <see cref="Simulator"/> and <see cref="Chain"/>. Sizes are radii in
/// model units (metres), swings degrees, tip places their tip length (the move itself in <see cref="Move"/>).
/// A tip's size also has <see cref="FittedAtPlace"/>, the size that fills the part once the same chain's
/// tip moves. A shape the skirt bumps into also has its place in <see cref="Shape"/>. <see cref="Optional"/>
/// items start unticked. Point sizes have one size per moving point in <see cref="Points"/>, the tip's last (it is
/// <see cref="Fitted"/>), and the sizes now in <see cref="PointsNow"/>.
/// </summary>
internal sealed record PhysicsFitItem(PhysicsFitTarget Target, string Name, int Simulator, int Chain, string Label, string Detail, float Current,
    float Fitted, PhysicsTipMove? Move = null, float? FittedAtPlace = null, PhysicsShapeMove? Shape = null, bool Optional = false,
    IReadOnlyList<float>? Points = null, IReadOnlyList<float>? PointsNow = null)
{
    /// <summary> The fit differs from the file: half a millimetre, half a degree, a tip or shape moved half a centimetre, or a new shape. </summary>
    public bool Changes => Target switch
    {
        PhysicsFitTarget.Swing => MathF.Abs(Fitted - Current) >= 0.5f,
        PhysicsFitTarget.Place => Move is { Distance: >= 0.005f },
        PhysicsFitTarget.Points => Points is { } sizes && PointsNow is { } now && sizes.Zip(now).Any(pair => MathF.Abs(pair.First - pair.Second) >= 0.0005f) ||
                                   FittedAtPlace is { } moved && MathF.Abs(moved - Current) >= 0.0005f,
        _ => MathF.Abs(Fitted - Current) >= 0.0005f || FittedAtPlace is { } moved && MathF.Abs(moved - Current) >= 0.0005f ||
             Shape is { Distance: >= 0.005f } || Shape is { NewOn: not null } && Current <= 0,
    };

    /// <summary> What this item sets, given whether its chain's tip moves too. </summary>
    public float FittedWith(bool placeMoves) => placeMoves && FittedAtPlace is { } moved ? moved : Fitted;
}

/// <summary>
/// The settings a fit measured, and what it couldn't measure, in plain words. <see cref="BreastTips"/>: the
/// file has breast tips the fit can place, so where they go is worth choosing. <see cref="Waist"/>: skirt
/// bones to move in Customize+.
/// </summary>
internal sealed record PhysicsFitResult(IReadOnlyList<PhysicsFitItem> Items, IReadOnlyList<string> Notes, bool BreastTips = false,
    IReadOnlyList<PhysicsWaistMove>? Waist = null)
{
    public IReadOnlyList<PhysicsWaistMove> Waist { get; init; } = Waist ?? [];
}

/// <summary>
/// Sizes a physics file's collision to a measured body. Only collision that body areas or the skirt use
/// counts: hair keeps the shapes the game made for it. The rules, checked on the game's own files and
/// presets:
/// <list type="bullet">
/// <item>A shape becomes the body's own thickness: the middle distance of the skin around it, over the
/// points its bones carry most. The game's shoulder capsules measure exactly their 6 cm this way.</item>
/// <item>A shape the skirt bumps into leaves room for the skirt points' own size, and never shrinks: the
/// game's 2 cm thighs plus the skirt's 4 cm put the skirt on the skin, and its calves keep skirts off.
/// Physics runs before Customize+ moves bones, so a shape only the skirt uses also moves to where
/// Customize+ puts its bones, as offsets from where they rest.</item>
/// <item>A skirt without a shape on the butt can get a ball on the hips for each butt cheek, sized like
/// the thighs. Where the skirt starts, only Customize+ can move it, so the fit names the moves.</item>
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

    /// <summary> The smallest size a fit writes, half a centimetre. </summary>
    internal const float Smallest = 0.005f;
    private const float Largest = 0.5f;

    /// <summary> The names of the butt balls the fit adds for the skirt, by side. </summary>
    internal const string SkirtButtPrefix = "skirt_butt_";

    /// <summary>
    /// Fits <paramref name="file"/> to <paramref name="body"/>, measured with Customize+. <paramref name="unshaped"/>
    /// is the same body standing in the skeleton's rest pose without it, where physics places its shapes;
    /// without it, shapes stay where they are and no skirt bone moves are named. <paramref name="originals"/>: for skirt
    /// shapes still where an earlier fit put them, the offsets they had before it (<see cref="PhysicsFitStore.Originals"/>),
    /// which they are measured from, so the Customize+ move is never added twice.
    /// </summary>
    public static PhysicsFitResult Measure(PhybFile file, PhysicsBody body, PhysicsSkeleton skeleton, PhysicsTipAt tipAt = PhysicsTipAt.Nipple,
        PhysicsBody? unshaped = null, IReadOnlyDictionary<(PhysicsFitTarget Target, string Name), (Vector3 Start, Vector3 End)>? originals = null)
    {
        // Without the body at rest shapes stay where they are, and so do shapes body areas bump into too (or the skirt no
        // longer does): those are measured where the file puts them, not from an earlier fit's original.
        (Vector3 Start, Vector3 End)? Original(PhysicsFitTarget target, string name, ShapeUse use)
            => unshaped != null && originals != null && use.SkirtSize >= 0 && !use.Body && originals.TryGetValue((target, name), out var offsets) ? offsets : null;
        var items = new List<PhysicsFitItem>();
        var notes = new List<string>();
        var users = ShapeUsers(file);
        var breastTips = false;
        // Each accessory is named once per area kind it was measured for.
        var accessoryNotes = new HashSet<(string Kind, string Slot)>();
        var rest = unshaped?.Pose ?? body.Pose;

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
            var (startOffset, endOffset) = Original(PhysicsFitTarget.Capsule, capsule.Name.Text, use) ?? (capsule.StartOffset, capsule.EndOffset);
            var a = start.Place(startOffset);
            var b = end.Place(endOffset);
            if (capsule.StartBone.Equals(capsule.EndBone) && Vector3.Distance(a, b) < 0.01f &&
                PartBall(body, skeleton, use, PhysicsFitTarget.Capsule, capsule.Name.Text, label, capsule.StartBone.Text, (a + b) / 2, capsule.Radius, notes,
                    out var ball))
            {
                if (ball != null)
                    items.Add(ball);
                continue;
            }
            var thickness = Vector3.Distance(a, b) < 0.01f
                ? AroundPlace(body, skeleton, capsule.StartBone.Text, (a + b) / 2)
                : AroundSegment(body, Region(body, skeleton, PathBones(capsule.StartBone.Text, capsule.EndBone.Text, skeleton)), a, b, 0.1f, 0.9f)
                  ?? AroundLimb(body, skeleton, capsule.StartBone.Text, (a + b) / 2);
            if (thickness is not { } measured)
            {
                notes.Add($"{capsule.Name.Text}: none of your skin moves with {capsule.StartBone.Text}.");
                continue;
            }
            // How far it moves counts from where the file puts it now.
            var move = ShapeMove(rest, use, notes, capsule.Name.Text, (capsule.StartBone.Text, capsule.StartOffset, a), (capsule.EndBone.Text, capsule.EndOffset, b));
            items.Add(Shape(PhysicsFitTarget.Capsule, capsule.Name.Text, label, capsule.Radius, measured, use, move));
        }

        foreach (var sphere in file.Collision.Spheres)
        {
            // The skirt's butt balls are measured on the butt below.
            if (sphere.Name.Text.StartsWith(SkirtButtPrefix, StringComparison.Ordinal) || !users.TryGetValue(sphere.Name.Text, out var use) || !use.Fits)
                continue;
            if (!body.Pose.TryGetValue(sphere.Bone.Text, out var frame))
            {
                notes.Add($"{sphere.Name.Text}: your skeleton lacks {sphere.Bone.Text}.");
                continue;
            }
            var centre = frame.Place(Original(PhysicsFitTarget.Sphere, sphere.Name.Text, use)?.Start ?? sphere.Offset);
            var sphereLabel = PhysicsShapeNames.Label(file.Collision, sphere.Name.Text, skeleton);
            if (PartBall(body, skeleton, use, PhysicsFitTarget.Sphere, sphere.Name.Text, sphereLabel, sphere.Bone.Text, centre, sphere.Thickness, notes, out var ball))
            {
                if (ball != null)
                    items.Add(ball);
                continue;
            }
            if (AroundPlace(body, skeleton, sphere.Bone.Text, centre) is not { } measured)
            {
                notes.Add($"{sphere.Name.Text}: none of your skin moves with {sphere.Bone.Text}.");
                continue;
            }
            var move = ShapeMove(rest, use, notes, sphere.Name.Text, (sphere.Bone.Text, sphere.Offset, centre));
            items.Add(Shape(PhysicsFitTarget.Sphere, sphere.Name.Text, sphereLabel, sphere.Thickness, measured, use, move));
        }

        var skirt = SkirtChains(file);
        var waist = new List<PhysicsWaistMove>();
        if (skirt.Count > 0)
        {
            Butts(file, body, skeleton, rest, users, skirt, items, notes);
            if (unshaped != null)
                Waist(file, body, unshaped, skeleton, skirt, waist);
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
                var rule = RuleOf(kind);
                if (!collides && rule == TipRule.None)
                    continue;
                var root = chain.Nodes[0].Bone.Text;
                var name = $"{kind.Label}{Side(root)}";
                var tipIndex = PhysicsKnobs.TipIndex(chain);
                if (!body.Pose.TryGetValue(root, out var rootFrame))
                {
                    notes.Add($"{name}: your skeleton lacks {root}.");
                    continue;
                }
                // The tip sits on the bone before it, which may be missing too.
                if (TipPlace(body, chain) is not { } tip)
                {
                    notes.Add($"{name}: your skeleton lacks {chain.Nodes[tipIndex - 1].Bone.Text}.");
                    continue;
                }
                if (!chain.Nodes[tipIndex].Bone.IsEmpty && Vector3.Distance(tip, rootFrame.Position) < 0.001f)
                {
                    notes.Add($"{name}: its tip sits on its bone, so there's nothing to measure.");
                    continue;
                }
                var bones = Region(body, skeleton, chain.Nodes.Select(node => node.Bone.Text).Where(bone => bone.Length > 0));
                foreach (var slot in Accessories(body, bones))
                    if (accessoryNotes.Add((kind.Label, slot)))
                        notes.Add($"Your {slot}'s model moves with {root}, so it was measured for the {kind.Label.ToLowerInvariant()}.");
                PhysicsTipMove? move = null;
                float? middleSize = null;
                // Breasts go where you choose, a member's tip to its end and a testicle's to its middle.
                var toMiddle = rule == TipRule.Middle || rule == TipRule.Breast && tipAt == PhysicsTipAt.Middle;
                if (rule != TipRule.None && chain.Nodes[tipIndex].Bone.IsEmpty)
                {
                    breastTips |= rule == TipRule.Breast;
                    if (Front(body, bones, rootFrame.Position) is { } front)
                    {
                        if (!toMiddle)
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
                    var noun = PartNoun(kind);
                    var to = rule switch
                    {
                        TipRule.Breast when tipAt == PhysicsTipAt.Nipple => "onto the front of your breast, where the game's tips sit",
                        TipRule.Breast => "into the middle of your breast, so its size covers the whole breast",
                        TipRule.End => $"to the end of your {noun}",
                        _ => $"into the middle of your {noun}",
                    };
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Place, root, s, c, $"{name}: tip place",
                        $"Moves the tip {Centimetres(move.Distance)} cm {to}. " +
                        LeverText(chain.LastBoneOffset.Length(), move.Offset.Length(), rule == TipRule.Breast ? "breast" : noun),
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
                var current = chain.Nodes[tipIndex].Radius;
                // The tip's size where it is now; a tip outside the skin keeps its size, unless the fit moves it onto the skin.
                float? tipSize = null;
                var onlyWithPlace = false;
                if (measured is null)
                    notes.Add($"{name}: none of your skin moves with {root}.");
                else if (float.IsPositiveInfinity(measured.Value))
                {
                    if (moved is not null)
                        (tipSize, onlyWithPlace) = (current, true);
                    else
                        notes.Add($"{name}: its tip sits outside your skin, so its size stays.");
                }
                else
                    tipSize = Clamp(measured.Value);

                // A member's or testicle's longer chain: every moving point takes the part's thickness around it, in one row.
                if (rule is TipRule.End or TipRule.Middle && tipIndex >= 2)
                {
                    // Where the tip will be when its place moves, since the last point's thickness reaches halfway to it.
                    var tipThere = move is { Distance: >= 0.005f } && TipPlace(body, chain, move.Offset) is { } newPlace ? newPlace : tip;
                    var places = chain.Nodes.Take(tipIndex).Select(node => body.Pose.TryGetValue(node.Bone.Text, out var at) ? at.Position : (Vector3?)null)
                        .Append(tipThere).ToList();
                    var sizes = PointSizes(body, bones, places);
                    if (tipSize is null && sizes.All(found => found is null))
                        continue;
                    var now = chain.Nodes.Skip(1).Take(tipIndex).Select(node => node.Radius).ToList();
                    var fitted = sizes.Select((found, n) => found is { } thickness ? Clamp(thickness) : now[n]).Append(tipSize ?? current).ToList();
                    var tipLast = moved ?? fitted[^1];
                    var each = string.Join(", ", fitted.Select((value, n) =>
                        $"point {n + 2}: {Centimetres(now[n])} to {Centimetres(n == fitted.Count - 1 ? tipLast : value)} cm"));
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Points, root, s, c, $"{name}: point sizes", $"Fills the part around each point. {each}.",
                        current, fitted[^1], FittedAtPlace: moved, Points: fitted, PointsNow: now));
                }
                else if (tipSize is { } size)
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Chain, root, s, c, $"{name}: tip size", onlyWithPlace ? fills + " Only changes with the tip place." : fills,
                        current, size, FittedAtPlace: moved));
            }
        }
        Swings(file, body, skeleton, items, notes);
        return new PhysicsFitResult(items, notes, breastTips, waist);
    }

    /// <summary>
    /// Where a shape only the skirt bumps into goes: each end where Customize+ puts it (<c>Shaped</c>),
    /// as an offset from its bone at rest, where physics runs. Null for shapes body areas use too, which
    /// stay on their bones, and when nothing moves.
    /// </summary>
    private static PhysicsShapeMove? ShapeMove(IReadOnlyDictionary<string, PhysicsFrame> rest, ShapeUse use, List<string> notes, string name,
        params (string Bone, Vector3 Offset, Vector3 Shaped)[] ends)
    {
        if (use.SkirtSize < 0 || !ends.All(end => rest.ContainsKey(end.Bone)))
            return null;
        var offsets = ends.Select(end => Local(rest[end.Bone], end.Shaped)).ToArray();
        var distance = ends.Max(end => Vector3.Distance(rest[end.Bone].Place(end.Offset), end.Shaped));
        if (distance < 0.005f)
            return null;
        if (use.Body)
        {
            notes.Add($"{name}: Customize+ moves its bones {Centimetres(distance)} cm, but body areas bump into it too, so it stays on them.");
            return null;
        }
        return new PhysicsShapeMove(offsets[0], offsets[^1], distance);
    }

    /// <summary>
    /// A ball on a body part's own bone (a breast's j_mune, or iv_c_mune under it) that other body areas bump into, such as the
    /// other breast, and the part's own chain doesn't: it stands for the part you see, so it moves to the part's middle (<see cref="Middle"/>) and takes its
    /// size. The bone's root sits at the part's edge, where all the skin around it would swallow the other breast. False for
    /// other shapes, which keep their rules; no <paramref name="item"/> when the part has no skin, with a note.
    /// </summary>
    private static bool PartBall(PhysicsBody body, PhysicsSkeleton skeleton, ShapeUse use, PhysicsFitTarget target, string name, string label, string bone,
        Vector3 centre, float current, List<string> notes, out PhysicsFitItem? item)
    {
        item = null;
        // A ball the part's own chain bumps into stays on its bone: moved into the part, it would sit on that chain's tip.
        if (!use.Body || PartBone(bone, skeleton) is not { } part || use.BodyRoots.Contains(part) || PhysicsAreas.KindOf(part) is not { } kind ||
            !body.Pose.TryGetValue(bone, out var frame) || !body.Pose.TryGetValue(part, out var root))
            return false;
        var region = Region(body, skeleton, [part]);
        // A breast's bone carries most of it; a butt's, belly's or thigh's jiggle bone a third at most, as for the skirt's butt balls.
        var weight = kind.Part is PhysicsPart.Chest or PhysicsPart.Pecs ? LimbWeight : PartWeight;
        if (Front(body, region, root.Position, weight) is not { } front || Middle(body, region, root.Position, front) is not { } middle)
        {
            notes.Add($"{name}: none of your skin moves with {bone}, so it stays as it is.");
            return true;
        }
        var offset = Local(frame, middle.Centre);
        var noun = PartNoun(kind);
        var sided = bone.EndsWith("_l", StringComparison.Ordinal) ? "left " : bone.EndsWith("_r", StringComparison.Ordinal) ? "right " : "";
        var distance = Vector3.Distance(centre, middle.Centre);
        item = new PhysicsFitItem(target, name, -1, -1, label,
            $"Moves it {Centimetres(distance)} cm to the middle of your {sided}{noun} and sizes it to the {noun}.", current, Clamp(middle.Radius),
            Shape: new PhysicsShapeMove(offset, offset, distance));
        return true;
    }

    /// <summary>
    /// The body area bone a shape's bone stands for: the bone itself when it is one (j_mune_l), else the first one up its
    /// parents while they aren't joints (iv_c_mune_l under j_mune_l, the <see cref="Region"/> rule). Null for any other bone.
    /// </summary>
    internal static string? PartBone(string bone, PhysicsSkeleton skeleton)
    {
        var current = bone;
        for (var depth = 0; current.Length > 0 && depth < 512; depth++)
        {
            if (PhysicsAreas.KindOf(current) is { Body: true })
                return current;
            if (current.StartsWith("j_", StringComparison.Ordinal))
                return null;
            current = skeleton.Get(current)?.Parent ?? string.Empty;
        }
        return null;
    }

    /// <summary> What a body area's part is called in a sentence: "breast", "pec", "butt cheek", "thigh", "penis", "testicle", or its label. </summary>
    internal static string PartNoun(PhysicsAreaKind kind) => kind.Part switch
    {
        PhysicsPart.Chest => "breast",
        PhysicsPart.Pecs => "pec",
        PhysicsPart.Butt => "butt cheek",
        PhysicsPart.Thighs => "thigh",
        _ when kind.Key.StartsWith("penis", StringComparison.Ordinal) => "penis",
        _ when kind.Key.StartsWith("testicles", StringComparison.Ordinal) => "testicle",
        _ => kind.Label.ToLowerInvariant(),
    };

    /// <summary> Where an area's tip goes: a breast's where you choose, a member's to its end, a testicle's to its middle; others stay. </summary>
    private enum TipRule
    {
        None,
        Breast,
        End,
        Middle,
    }

    private static TipRule RuleOf(PhysicsAreaKind kind) => kind.Key switch
    {
        "chest" or "pecs-ivcs" => TipRule.Breast,
        "penis-ivcs" or "penis-soft-ivcs" => TipRule.End,
        "testicles-ivcs" or "testicles-jiggle-ivcs" => TipRule.Middle,
        _ => TipRule.None,
    };

    /// <summary>
    /// The thickness around each of a chain's points between its bone and its tip (<paramref name="places"/> 1 to the second
    /// last; the last is the tip): the middle distance from the chain's line of the skin its bones carry a tenth or more of,
    /// beside the half segments either side of the point. Null for a point without such skin, or whose place is missing.
    /// </summary>
    private static float?[] PointSizes(PhysicsBody body, IReadOnlySet<int> bones, IReadOnlyList<Vector3?> places)
    {
        var count = Math.Max(0, places.Count - 2);
        var values = Enumerable.Range(0, count).Select(_ => new List<(float Value, float Area)>()).ToArray();
        // How far along a segment a point lies (0 at its start, 1 at its end), and how far from its line.
        static (float Along, float Away)? Beside(Vector3? from, Vector3? to, Vector3 point)
        {
            if (from is not { } a || to is not { } b || (b - a).LengthSquared() < 1e-10f)
                return null;
            var t = Vector3.Dot(point - a, b - a) / (b - a).LengthSquared();
            return (t, Vector3.Distance(point, a + t * (b - a)));
        }
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (body.WeightOn(i, bones, accessories: true) < PartWeight)
                continue;
            for (var n = 1; n <= count; n++)
            {
                // The second half of the segment before the point, or the first half of the one after it, the nearer line first.
                float? away = null;
                if (Beside(places[n - 1], places[n], body.Points[i]) is { Along: >= 0.5f and <= 1 } before)
                    away = before.Away;
                if (Beside(places[n], places[n + 1], body.Points[i]) is { Along: >= 0 and <= 0.5f } after && !(away <= after.Away))
                    away = after.Away;
                if (away is { } distance)
                    values[n - 1].Add((distance, body.Areas[i]));
            }
        }
        return values.Select(list => Percentile(list, 0.5f)).ToArray();
    }

    /// <summary> A model-space point as an offset from a bone, the way physics files give them: turned with the bone, never scaled. </summary>
    private static Vector3 Local(PhysicsFrame frame, Vector3 point) => Vector3.Transform(point - frame.Position, Quaternion.Conjugate(frame.Rotation));

    /// <summary> Every skirt chain with moving points, by simulator and chain. </summary>
    private static List<(int Simulator, int Chain)> SkirtChains(PhybFile file)
    {
        var chains = new List<(int, int)>();
        for (var s = 0; s < file.Simulators.Count; s++)
            if (!file.Simulators[s].Excluded)
                for (var c = 0; c < file.Simulators[s].Chains.Count; c++)
                    if (file.Simulators[s].Chains[c] is { Nodes.Count: >= 2 } chain && PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text)?.Part == PhysicsPart.Skirt)
                        chains.Add((s, c));
        return chains;
    }

    /// <summary>
    /// A ball for each butt cheek that the skirt bumps into, on the hips (which physics doesn't move):
    /// in the middle of the cheek's skin, as big as the cheek less the skirt points' size, as thighs are.
    /// Added only when no other shape the skirt bumps into sits on the butt or hips, and unticked, since
    /// it adds to the file; balls added before are measured again.
    /// </summary>
    private static void Butts(PhybFile file, PhysicsBody body, PhysicsSkeleton skeleton, IReadOnlyDictionary<string, PhysicsFrame> rest,
        Dictionary<string, ShapeUse> users, List<(int Simulator, int Chain)> skirt, List<PhysicsFitItem> items, List<string> notes)
    {
        const string hips = "j_kosi";
        var butt = PhysicsAreas.Kinds.Where(kind => kind.Part == PhysicsPart.Butt).SelectMany(kind => kind.Bones).ToHashSet(StringComparer.Ordinal);
        bool OnButt(string bone) => bone == hips || butt.Contains(bone);
        var covered = users.Where(pair => pair.Value.SkirtSize >= 0 && !pair.Key.StartsWith(SkirtButtPrefix, StringComparison.Ordinal)).Any(pair =>
            file.Collision.Spheres.Any(sphere => sphere.Name.Text == pair.Key && OnButt(sphere.Bone.Text)) ||
            file.Collision.Capsules.Any(capsule => capsule.Name.Text == pair.Key && (OnButt(capsule.StartBone.Text) || OnButt(capsule.EndBone.Text))));
        if (covered || !rest.TryGetValue(hips, out var hipsAtRest))
            return;
        var skirtSize = skirt.Max(target => file.Simulators[target.Simulator].Chains[target.Chain].Nodes.Skip(1).Max(node => node.Radius));
        PhybFile? planned = null;
        foreach (var (side, label) in new[] { ("l", "Left"), ("r", "Right") })
        {
            var name = SkirtButtPrefix + side;
            var bones = butt.Where(bone => bone.EndsWith("_" + side, StringComparison.Ordinal) && body.Pose.ContainsKey(bone)).ToList();
            var region = Region(body, skeleton, bones);
            if (bones.Count == 0 || region.Count == 0)
            {
                notes.Add("Butt for the skirt: your skeleton has no butt bones with skin, so the skirt gets no butt collision.");
                return;
            }
            var root = bones.Aggregate(Vector3.Zero, (sum, bone) => sum + body.Pose[bone].Position) / bones.Count;
            if (Front(body, region, root, PartWeight) is not { } front || Middle(body, region, root, front) is not { } middle)
            {
                notes.Add($"{label} butt for the skirt: none of your skin moves with the butt.");
                continue;
            }
            var existing = file.Collision.Spheres.FirstOrDefault(sphere => sphere.Name.Text == name);
            // Added to a copy, so the second ball counts the bit the first one takes.
            if (existing is null && !PhysicsCollisions.Add(planned ??= file.Clone(), skirt, name))
            {
                notes.Add($"{label} butt for the skirt: the skirt already bumps into {PhysicsCollisions.MaxShapes} shapes.");
                continue;
            }
            var offset = Local(hipsAtRest, middle.Centre);
            var distance = existing is null ? 0 : Vector3.Distance(rest.TryGetValue(existing.Bone.Text, out var on) ? on.Place(existing.Offset) : middle.Centre, middle.Centre);
            items.Add(new PhysicsFitItem(PhysicsFitTarget.Sphere, name, -1, -1, $"{label} butt cheek, for the skirt",
                $"{(existing is null ? "Adds a ball inside your butt cheek that the skirt bumps into" : "The ball the skirt bumps into on your butt cheek")}, so the " +
                $"back of the skirt stays off your skin. Leaves {skirtSize * 100:0.#} cm for the skirt's own size.",
                existing?.Thickness ?? 0, Clamp(middle.Radius - skirtSize), Shape: new PhysicsShapeMove(offset, offset, distance, hips),
                Optional: existing is null));
        }
    }

    /// <summary> How far either side of a skirt bone's height the hips are measured. </summary>
    private const float WaistBand = 0.02f;

    /// <summary> How far, in degrees, around a skirt bone's direction from the hips the hips are measured. </summary>
    private const float WaistSpread = 20f;

    /// <summary>
    /// Customize+ moves for the bones skirts start from, which physics never moves: each goes out from
    /// the hips as far as the hips' skin there grew (the outer tenth of it, at the bone's height, toward
    /// it) from <paramref name="unshaped"/> to <paramref name="body"/>, less what Customize+ already moves it.
    /// </summary>
    private static void Waist(PhybFile file, PhysicsBody body, PhysicsBody unshaped, PhysicsSkeleton skeleton, List<(int Simulator, int Chain)> skirt,
        List<PhysicsWaistMove> moves)
    {
        if (!unshaped.Pose.TryGetValue("j_kosi", out var hips) || !body.Pose.TryGetValue("j_kosi", out var shapedHips))
            return;
        string[] torso = ["j_kosi", "j_sebo_a", "j_asi_a_l", "j_asi_a_r"];
        foreach (var bone in skirt.Select(target => file.Simulators[target.Simulator].Chains[target.Chain].Nodes[0].Bone.Text).Distinct())
        {
            if (!unshaped.Pose.TryGetValue(bone, out var atRest) || !body.Pose.TryGetValue(bone, out var shaped))
                continue;
            var direction = atRest.Position - hips.Position;
            direction.Y = 0;
            if (direction.Length() < 0.01f)
                continue;
            direction = Vector3.Normalize(direction);
            // Each body is measured around its own hips, at the bone's own height, so a pelvis Customize+ raises is measured at the same slice.
            if (Outer(body, skeleton, torso, shapedHips.Position, shaped.Position.Y, direction) is not { } grown ||
                Outer(unshaped, skeleton, torso, hips.Position, atRest.Position.Y, direction) is not { } was || grown - was < 0.005f)
                continue;
            // Out from where the hips are now, so a pelvis Customize+ moves takes the skirt bone with it.
            var extra = Vector3.Dot(atRest.Position + shapedHips.Position - hips.Position + direction * (grown - was) - shaped.Position, direction);
            if (extra >= 0.005f)
                moves.Add(new PhysicsWaistMove(bone, Vector3.Transform(direction * extra, Quaternion.Conjugate(shaped.Rotation)), extra));
        }
    }

    /// <summary> How far out from the hips' middle the torso's skin reaches toward <paramref name="direction"/>, at one height. </summary>
    private static float? Outer(PhysicsBody body, PhysicsSkeleton skeleton, string[] torso, Vector3 middle, float height, Vector3 direction)
    {
        var region = Region(body, skeleton, torso);
        var minCos = MathF.Cos(WaistSpread * MathF.PI / 180);
        var values = new List<(float Value, float Area)>();
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (MathF.Abs(body.Points[i].Y - height) > WaistBand || body.WeightOn(i, region) < LimbWeight)
                continue;
            var flat = body.Points[i] - middle;
            flat.Y = 0;
            var reach = Vector3.Dot(flat, direction);
            if (reach > 0 && reach >= minCos * flat.Length())
                values.Add((reach, body.Areas[i]));
        }
        return Percentile(values, 0.9f);
    }

    /// <summary> The accessory slots whose models these bones carry a tenth or more of somewhere, so an area measures them as its own part. </summary>
    private static IEnumerable<string> Accessories(PhysicsBody body, IReadOnlySet<int> bones)
        => Enumerable.Range(0, body.Points.Length)
            .Where(i => body.PointAccessory[i] != null && body.WeightOn(i, bones, accessories: true) >= PartWeight)
            .Select(i => body.PointAccessory[i]!)
            .Distinct();

    private static string Centimetres(float metres) => (metres * 100).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a new tip length does to the jiggle. The solver moves the tip by distances, which turn the bone
    /// less the further out the tip sits, so the same settings swing a shorter tip more, by the lengths' ratio.
    /// </summary>
    internal static string LeverText(float from, float to, string part = "breast")
    {
        if (!(from > 1e-4f) || !(to > 1e-4f) || MathF.Abs(from / to - 1) < 0.1f)
            return "Tip length barely changes, so the jiggle stays the same.";
        return $"Tip length {Centimetres(from)} to {Centimetres(to)} cm, so the same settings swing the {part} about " +
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
                    {
                        capsule.Radius = item.Fitted;
                        if (item.Shape is { } move)
                            (capsule.StartOffset, capsule.EndOffset) = (move.Start, move.End);
                    }
                    break;
                case PhysicsFitTarget.Sphere:
                    if (item.Shape is { NewOn: { } bone } && file.Collision.Spheres.All(shape => shape.Name.Text != item.Name))
                    {
                        // Without a bit left in the skirt, nothing would bump into the ball.
                        if (!PhysicsCollisions.CanAdd(file, SkirtChains(file), item.Name))
                            break;
                        file.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From(item.Name), Bone = PhybName.From(bone) });
                    }
                    foreach (var sphere in file.Collision.Spheres.Where(shape => shape.Name.Text == item.Name))
                    {
                        sphere.Thickness = item.Fitted;
                        if (item.Shape is { } move)
                            sphere.Offset = move.Start;
                    }
                    // The skirt bumps into the balls the fit makes for it, whether new or measured again.
                    if (item.Shape is { NewOn: not null })
                        PhysicsCollisions.Add(file, SkirtChains(file), item.Name);
                    break;
                case PhysicsFitTarget.Chain or PhysicsFitTarget.Swing or PhysicsFitTarget.Place or PhysicsFitTarget.Points:
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
                    if (item.Target == PhysicsFitTarget.Points)
                    {
                        // Each point its own size; the tip's follows its place, as a tip size does.
                        var tip = PhysicsKnobs.TipIndex(chain);
                        if (item.Points is not { } sizes || sizes.Count != tip)
                            break;
                        for (var n = 1; n < tip; n++)
                            chain.Nodes[n].Radius = sizes[n - 1];
                        chain.Nodes[tip].Radius = item.FittedWith(MovesPlace(chosen, item));
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
        var tip = PhysicsKnobs.TipIndex(chain);
        var bone = chain.Nodes[tip - 1];
        if (Degrees(bone.ConeAxisOffset, from) <= PhysicsKnobs.SpringAimLimit)
            bone.ConeAxisOffset = Vector3.Transform(bone.ConeAxisOffset, turn);
        foreach (var spring in simulator.Attracts.Where(attract => attract.Chain == index && attract.Node == tip && attract.Bone.Text == move.Parent))
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
        /// <summary> The bones of the body area chains that bump into it. </summary>
        public readonly HashSet<string> BodyRoots = new(StringComparer.Ordinal);
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
                        {
                            use.Body = true;
                            use.BodyRoots.Add(chain.Nodes[0].Bone.Text);
                        }
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

    private static PhysicsFitItem Shape(PhysicsFitTarget target, string name, string label, float current, float measured, ShapeUse use,
        PhysicsShapeMove? move)
    {
        if (use.Body || use.SkirtSize < 0)
            return new PhysicsFitItem(target, name, -1, -1, label, "Your skin's thickness around it.", current, Clamp(measured));
        // Skirt points sit on the cloth: the shape plus their size reaches the skin. Thinner legs keep the room the file gives.
        var fitted = Math.Max(current, Clamp(measured - use.SkirtSize));
        var moved = move is null ? "" : $" Moves it {Centimetres(move.Distance)} cm to where Customize+ puts your body, since physics runs before it.";
        return new PhysicsFitItem(target, name, -1, -1, label, $"Leaves {use.SkirtSize * 100:0.#} cm for the skirt's own size.{moved}", current, fitted,
            Shape: move);
    }

    private static float Clamp(float size) => MathF.Round(Math.Clamp(size, Smallest, Largest), 3);

    /// <summary> Where a chain's tip (<see cref="PhysicsKnobs.TipIndex"/>) is: its bone, or the place the chain gives it from the bone before it. </summary>
    private static Vector3? TipPlace(PhysicsBody body, PhybChain chain)
    {
        var tip = chain.Nodes[PhysicsKnobs.TipIndex(chain)];
        if (tip.Bone.Text.Length > 0 && body.Pose.TryGetValue(tip.Bone.Text, out var own))
            return own.Position;
        return TipPlace(body, chain, chain.LastBoneOffset);
    }

    /// <summary> Where a chain's tip would be at <paramref name="offset"/> from the bone before it. </summary>
    private static Vector3? TipPlace(PhysicsBody body, PhybChain chain, Vector3 offset)
        => body.Pose.TryGetValue(chain.Nodes[PhysicsKnobs.TipIndex(chain) - 1].Bone.Text, out var frame) ? frame.Place(offset) : null;

    /// <summary> The share of a breast's skin, furthest out along it, whose middle is its front. </summary>
    private const float FrontShare = 0.03f;

    /// <summary>
    /// A breast's front: the middle of the skin furthest out (the outer <see cref="FrontShare"/> by area)
    /// along the breast's centre line, which runs from the bone through the middle of the skin it carries
    /// most (at least <paramref name="weight"/>; a butt's bones carry a third of it at most). Null without that skin.
    /// </summary>
    private static Vector3? Front(PhysicsBody body, IReadOnlySet<int> bones, Vector3 root, float weight = LimbWeight)
    {
        var points = new List<int>();
        var middle = Vector3.Zero;
        var total = 0f;
        for (var i = 0; i < body.Points.Length; i++)
        {
            if (body.WeightOn(i, bones, accessories: true) < weight)
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
            if (body.WeightOn(i, bones, accessories: true) is var weight and >= PartWeight)
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
        var at = PhysicsKnobs.TipIndex(chain);
        if (!chain.Nodes[at].Bone.IsEmpty)
            return null;
        var before = chain.Nodes[at - 1].Bone.Text;
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
            if (body.WeightOn(i, bones, accessories: true) < PartWeight)
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
