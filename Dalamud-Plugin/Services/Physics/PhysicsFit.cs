using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary> What a fit sizes: a collision shape, or the moving points of one chain. </summary>
internal enum PhysicsFitTarget
{
    Capsule,
    Sphere,
    Chain,
}

/// <summary>
/// A size measured on the body: <see cref="Name"/> is the shape's name, or for a chain the simulator and
/// chain in <see cref="Simulator"/> and <see cref="Chain"/>. Sizes are radii in model units (metres).
/// </summary>
internal sealed record PhysicsFitItem(PhysicsFitTarget Target, string Name, int Simulator, int Chain, string Label, string Detail, float Current,
    float Fitted)
{
    /// <summary> The fit differs from the file by at least half a millimetre. </summary>
    public bool Changes => MathF.Abs(Fitted - Current) >= 0.0005f;
}

/// <summary> The sizes a fit measured, and what it couldn't measure, in plain words. </summary>
internal sealed record PhysicsFitResult(IReadOnlyList<PhysicsFitItem> Items, IReadOnlyList<string> Notes);

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
/// </summary>
internal static class PhysicsFit
{
    /// <summary> How much of a point a shape's bones must carry for it to count as that limb. </summary>
    private const float LimbWeight = 0.5f;

    /// <summary> How much of a point an area's bones must carry for it to move with the area. </summary>
    private const float PartWeight = 0.1f;

    private const float Smallest = 0.005f;
    private const float Largest = 0.5f;

    public static PhysicsFitResult Measure(PhybFile file, PhysicsBody body, PhysicsSkeleton skeleton)
    {
        var items = new List<PhysicsFitItem>();
        var notes = new List<string>();
        var users = ShapeUsers(file);

        foreach (var capsule in file.Collision.Capsules)
        {
            if (!users.TryGetValue(capsule.Name.Text, out var use) || !use.Fits)
                continue;
            var label = $"{capsule.Name.Text}: {PartLabel(capsule.StartBone.Text, capsule.EndBone.Text, skeleton)}";
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
            items.Add(Shape(PhysicsFitTarget.Sphere, sphere.Name.Text, $"{sphere.Name.Text}: {PartLabel(sphere.Bone.Text, sphere.Bone.Text, skeleton)}",
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
                if (chain.Nodes.Count < 2 || PhysicsAreas.KindOf(chain.Nodes[0].Bone.Text) is not { Body: true } kind ||
                    !CollidesOutside(simulator, chain))
                    continue;
                var root = chain.Nodes[0].Bone.Text;
                var name = $"{kind.Label}{Side(root)}";
                if (!body.Pose.TryGetValue(root, out var rootFrame) || TipPlace(body, chain) is not { } tip)
                {
                    notes.Add($"{name}: your skeleton lacks {root}.");
                    continue;
                }
                var bones = Region(body, skeleton, chain.Nodes.Select(node => node.Bone.Text).Where(bone => bone.Length > 0));
                var measured = AroundTip(body, bones, rootFrame.Position, tip, AheadOfSkin(kind.Part));
                if (measured is null)
                    notes.Add($"{name}: none of your skin moves with {root}.");
                else if (float.IsPositiveInfinity(measured.Value))
                    notes.Add($"{name}: its tip sits outside your skin, so its size stays.");
                else
                    items.Add(new PhysicsFitItem(PhysicsFitTarget.Chain, root, s, c, $"{name}: tip size", "Fills the part around the tip.",
                        chain.Nodes[^1].Radius, Clamp(measured.Value)));
            }
        }
        return new PhysicsFitResult(items, notes);
    }

    /// <summary> Sets the sizes of <paramref name="items"/> in the file; chains keep their middle points' share of the tip's size. </summary>
    public static void Apply(PhybFile file, IEnumerable<PhysicsFitItem> items)
    {
        foreach (var item in items)
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
                case PhysicsFitTarget.Chain:
                    if (item.Simulator < 0 || item.Simulator >= file.Simulators.Count ||
                        item.Chain < 0 || item.Chain >= file.Simulators[item.Simulator].Chains.Count)
                        break;
                    var chain = file.Simulators[item.Simulator].Chains[item.Chain];
                    if (chain.Nodes.Count < 2 || chain.Nodes[0].Bone.Text != item.Name)
                        break;
                    var old = chain.Nodes[^1].Radius;
                    for (var n = 1; n < chain.Nodes.Count - 1; n++)
                        chain.Nodes[n].Radius = MathF.Abs(old) > 1e-9f ? chain.Nodes[n].Radius * item.Fitted / old : item.Fitted;
                    chain.Nodes[^1].Radius = item.Fitted;
                    break;
            }
        }
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

    /// <summary> The shapes a moving point stays out of: its simulator's, by the point's bits, and its chain's own. </summary>
    private static IEnumerable<string> Shapes(PhybSimulator simulator, PhybChain chain, PhybNode node)
    {
        for (var bit = 0; bit < simulator.Collisions.Count && bit < 32; bit++)
            if ((node.CollisionFlags & (1u << bit)) != 0 && simulator.Collisions[bit].Type != PhybCollisionType.Inside)
                yield return simulator.Collisions[bit].Name.Text;
        foreach (var reference in chain.Collisions)
            if (reference.Type != PhybCollisionType.Inside)
                yield return reference.Name.Text;
    }

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
        var before = chain.Nodes[^2].Bone.Text;
        return body.Pose.TryGetValue(before, out var frame) ? frame.Place(chain.LastBoneOffset) : null;
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
    /// part's skin, and null without any such skin.
    /// </summary>
    private static float? AroundTip(PhysicsBody body, IReadOnlySet<int> bones, Vector3 root, Vector3 tip, float ahead)
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
        return depth < -0.005f ? float.PositiveInfinity : Math.Min(width, Math.Max(depth, 0) + ahead);
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

    /// <summary> "Left forearm" for a shape between two bones: the part of the bone nearer the body. </summary>
    internal static string PartLabel(string from, string to, PhysicsSkeleton skeleton)
    {
        var path = PathBones(from, to, skeleton);
        var bone = path.Count > 0 ? path[^1] : from;
        // A shape that crosses the body names both ends.
        if (path.Count > 2 && Side(from) != Side(to))
            return $"{Sided(from)} to {Sided(to)}";
        return Sided(bone);
    }

    /// <summary> "left forearm" for j_ude_b_l. </summary>
    private static string Sided(string bone)
        => (bone.EndsWith("_l", StringComparison.Ordinal) ? "left " : bone.EndsWith("_r", StringComparison.Ordinal) ? "right " : string.Empty) + Part(bone);

    private static string Part(string bone)
    {
        var name = bone.EndsWith("_l", StringComparison.Ordinal) || bone.EndsWith("_r", StringComparison.Ordinal) ? bone[..^2] : bone;
        return name switch
        {
            "j_sako" => "shoulder",
            "j_ude_a" or "n_hkata" => "upper arm",
            "j_ude_b" or "n_hte" => "forearm",
            "n_hhiji" => "elbow",
            "j_te" => "hand",
            "j_asi_a" => "thigh",
            "j_asi_b" => "knee",
            "j_asi_c" => "calf",
            "j_asi_d" or "j_asi_e" => "foot",
            "j_mune" or "iv_c_mune" => "breast",
            "iv_shiri" or "ya_shiri_phys" => "butt cheek",
            "j_kosi" => "hips",
            "j_sebo_a" => "waist",
            "j_sebo_b" => "chest",
            "j_sebo_c" => "upper back",
            "j_kubi" => "neck",
            _ when name.StartsWith("j_oya", StringComparison.Ordinal) || name.StartsWith("j_hito", StringComparison.Ordinal) ||
                   name.StartsWith("j_naka", StringComparison.Ordinal) || name.StartsWith("j_kusu", StringComparison.Ordinal) ||
                   name.StartsWith("j_ko_", StringComparison.Ordinal) => "fingers",
            _ => bone,
        };
    }
}
