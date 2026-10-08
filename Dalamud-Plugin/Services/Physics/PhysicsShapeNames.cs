namespace InstantEdit.Services.Physics;

/// <summary>
/// The body part a collision shape covers, read from the bones it sits on, for lists that would
/// otherwise show only a shape's own name: "Left thigh (asi_l_a)".
/// </summary>
internal static class PhysicsShapeNames
{
    /// <summary> "Left thigh (asi_l_a)", or one name when the shape is already called after its part or the file has no such shape. </summary>
    public static string Label(PhybCollision collision, string name, PhysicsSkeleton? skeleton)
        => Part(collision, name, skeleton) is { } part && !string.Equals(part, name, StringComparison.OrdinalIgnoreCase) ? $"{part} ({name})" : name;

    /// <summary> "Left thigh" for the shape called <paramref name="name"/>, or null when the file has none. </summary>
    public static string? Part(PhybCollision collision, string name, PhysicsSkeleton? skeleton)
    {
        if (collision.Capsules.FirstOrDefault(shape => shape.Name.Text == name) is { } capsule)
            return Capitalized(Between(capsule.StartBone.Text, capsule.EndBone.Text, skeleton));
        var bone = collision.Spheres.FirstOrDefault(shape => shape.Name.Text == name)?.Bone
                   ?? collision.Ellipsoids.FirstOrDefault(shape => shape.Name.Text == name)?.Bone
                   ?? collision.NormalPlanes.FirstOrDefault(shape => shape.Name.Text == name)?.Bone
                   ?? collision.ThreePointPlanes.FirstOrDefault(shape => shape.Name.Text == name)?.Bone;
        return bone is null || bone.IsEmpty ? null : Capitalized(Sided(bone.Text));
    }

    /// <summary>
    /// "left forearm" for a shape between two bones: the part of the bone nearer the body. A shape
    /// that crosses the body names both ends. Without a skeleton, the nearer bone is the one higher up
    /// the arm or leg.
    /// </summary>
    public static string Between(string from, string to, PhysicsSkeleton? skeleton)
    {
        if (from == to || to.Length == 0)
            return Sided(from);
        if (skeleton is null)
            return Side(from) != Side(to) && Side(from).Length > 0 && Side(to).Length > 0
                ? $"{Sided(from)} to {Sided(to)}"
                : Sided(Depth(to) < Depth(from) ? to : from);
        var path = PhysicsFit.PathBones(from, to, skeleton);
        var bone = path.Count > 0 ? path[^1] : from;
        if (path.Count > 2 && Side(from) != Side(to))
            return $"{Sided(from)} to {Sided(to)}";
        return Sided(bone);
    }

    /// <summary> "left forearm" for j_ude_b_l. </summary>
    public static string Sided(string bone)
        => (bone.EndsWith("_l", StringComparison.Ordinal) ? "left " : bone.EndsWith("_r", StringComparison.Ordinal) ? "right " : string.Empty) + Part(bone);

    private static string Side(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) ? "l" : bone.EndsWith("_r", StringComparison.Ordinal) ? "r" : string.Empty;

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Unsided(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) || bone.EndsWith("_r", StringComparison.Ordinal) ? bone[..^2] : bone;

    // How far down a limb a bone is, for shapes read without a skeleton.
    private static int Depth(string bone) => Unsided(bone) switch
    {
        "j_sako" or "j_asi_a" => 1,
        "j_ude_a" or "n_hkata" or "j_asi_b" => 2,
        "j_ude_b" or "n_hhiji" or "j_asi_c" => 3,
        "j_te" or "n_hte" or "j_asi_d" => 4,
        "j_asi_e" => 5,
        var name when IsFinger(name) => 5,
        _ => 0,
    };

    private static bool IsFinger(string name)
        => name.StartsWith("j_oya", StringComparison.Ordinal) || name.StartsWith("j_hito", StringComparison.Ordinal) ||
           name.StartsWith("j_naka", StringComparison.Ordinal) || name.StartsWith("j_kusu", StringComparison.Ordinal) ||
           name.StartsWith("j_ko_", StringComparison.Ordinal) || name.StartsWith("iv_hito", StringComparison.Ordinal) ||
           name.StartsWith("iv_naka", StringComparison.Ordinal) || name.StartsWith("iv_kusu", StringComparison.Ordinal) ||
           name.StartsWith("iv_ko_", StringComparison.Ordinal);

    private static string Part(string bone)
    {
        var name = Unsided(bone);
        return name switch
        {
            "j_sako" => "shoulder",
            "j_ude_a" or "n_hkata" => "upper arm",
            "iv_nitoukin" => "bicep",
            "j_ude_b" or "n_hte" => "forearm",
            "n_hhiji" => "elbow",
            "j_te" => "hand",
            "j_asi_a" or "iv_daitai_phys" or "ya_daitai_phys" => "thigh",
            "j_asi_b" => "knee",
            "j_asi_c" => "calf",
            "j_asi_d" or "j_asi_e" => "foot",
            "j_mune" or "iv_c_mune" => "breast",
            "iv_kyokin_phys" => "pec",
            "iv_shiri" or "ya_shiri_phys" => "butt cheek",
            "iv_fukubu_phys" or "ya_fukubu_phys" => "belly",
            "j_kosi" => "hips",
            "j_sebo_a" => "waist",
            "j_sebo_b" => "chest",
            "j_sebo_c" => "upper back",
            "j_kubi" => "neck",
            "j_kao" => "head",
            "j_kami_a" or "j_kami_b" or "j_kami_f" => "hair",
            "j_mimi" => "ear",
            "n_hara" => "body",
            _ when IsFinger(name) => "fingers",
            _ => Words(name),
        };
    }

    /// <summary> An unknown bone in words without its prefix: "j_sk_f_a" becomes "sk f a". </summary>
    private static string Words(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(word => word is not ("j" or "n" or "iv" or "ya" or "nf" or "phys")).ToList();
        return words.Count == 0 ? name : string.Join(" ", words);
    }
}
