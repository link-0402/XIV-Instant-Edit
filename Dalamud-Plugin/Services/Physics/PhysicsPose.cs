using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace InstantEdit.Services.Physics;

/// <summary> One bone of a Customize+ profile: how it moves, turns and scales the bone, and what it carries to the bones under it. </summary>
internal sealed record PhysicsPlusBone(Vector3 Translation, Vector3 Rotation, Vector3 Scaling, Vector3 ChildScaling, bool ChildScalingIndependent,
    bool PropagateTranslation, bool PropagateRotation, bool PropagateScale)
{
    public bool Propagates => PropagateTranslation || PropagateRotation || PropagateScale;
}

/// <summary>
/// The pose a body is measured in: the skeleton standing in its rest pose, shaped by the character's
/// Customize+ profile and bust size, and nothing else. The idle animation, physics, posing tools and
/// any scaling an animation does don't count, so everyone's body is measured the same way and
/// measuring again gives the same result.
/// </summary>
internal static class PhysicsPose
{
    /// <summary> The game's character-making parameters, whose racial scaling holds each clan's bust range. </summary>
    public const string CharaMakeFile = "chara/xls/charamake/human.cmp";

    // human.cmp: two blocks of 9,216 bytes of colours, 32 of 5,120 bytes of skin and hair colours, then
    // 8 races × 10 entries of 14 floats, a race's two clans first: height and tail ranges for men and
    // women, then the bust's smallest and largest scale on X, Y and Z (Penumbra.GameData's CmpData).
    private const int ScalingStart = 2 * 9216 + 32 * 5120;
    private const int ScaleEntry = 14 * 4;
    private const int EntriesPerRace = 10;
    private const int BustMin = 8;

    private static readonly string[] Breasts = ["j_mune_l", "j_mune_r"];

    /// <summary>
    /// The scale the game gives breast bones for a bust size from 0 to 100: between the clan's smallest
    /// and largest bust in <see cref="CharaMakeFile"/>, as the game reads them. One for men and for body
    /// types the range doesn't cover.
    /// </summary>
    public static Vector3 Bust(ReadOnlySpan<byte> cmp, int clan, bool female, int bodyType, int bustSize)
    {
        if (!female || bodyType >= 2 || clan is < 1 or > 16)
            return Vector3.One;
        var index = clan - 1;
        var at = ScalingStart + (index >> 1) * EntriesPerRace * ScaleEntry + (index & 1) * ScaleEntry + BustMin * 4;
        if (at + 6 * 4 > cmp.Length)
            return Vector3.One;
        // The smallest bust on X, Y and Z, then the largest.
        var range = new float[6];
        for (var i = 0; i < range.Length; i++)
            range[i] = BinaryPrimitives.ReadSingleLittleEndian(cmp[(at + i * 4)..]);
        var size = Math.Clamp(bustSize, 0, 100) / 100f;
        var scale = new Vector3(range[0] + (range[3] - range[0]) * size, range[1] + (range[4] - range[1]) * size, range[2] + (range[5] - range[2]) * size);
        return float.IsFinite(scale.X + scale.Y + scale.Z) && scale.X > 0 && scale.Y > 0 && scale.Z > 0 ? scale : Vector3.One;
    }

    /// <summary> The bones of a Customize+ profile as its IPC writes it (JSON with a "Bones" object). Missing values take Customize+'s defaults. </summary>
    public static Dictionary<string, PhysicsPlusBone> ParsePlus(string json)
    {
        var bones = new Dictionary<string, PhysicsPlusBone>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Bones", out var all) || all.ValueKind != JsonValueKind.Object)
            return bones;
        foreach (var bone in all.EnumerateObject())
        {
            var value = bone.Value;
            if (value.ValueKind != JsonValueKind.Object)
                continue;
            // The IPC leaves out values equal to C#'s defaults, so a missing scale is a scale of zero.
            bones[bone.Name] = new PhysicsPlusBone(Vector(value, "Translation"), Vector(value, "Rotation"), Vector(value, "Scaling"),
                value.TryGetProperty("ChildScaling", out _) ? Vector(value, "ChildScaling") : Vector3.One, Flag(value, "ChildScaleIndependent"),
                Flag(value, "PropagateTranslation"), Flag(value, "PropagateRotation"), Flag(value, "PropagateScale"));
        }
        return bones;
    }

    private static Vector3 Vector(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var vector) || vector.ValueKind != JsonValueKind.Object)
            return Vector3.Zero;
        float Axis(string axis) => vector.TryGetProperty(axis, out var element) && element.TryGetSingle(out var value) && float.IsFinite(value) ? value : 0;
        return new Vector3(Axis("X"), Axis("Y"), Axis("Z"));
    }

    private static bool Flag(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The skeleton's rest pose with a Customize+ profile applied as Customize+ applies it to a pose
    /// (in model space, parents before children: scale, then turn, then a move along the turned bone;
    /// with any carry on, the bones under it follow the move, and its scale and turn spread about it
    /// when those carry), then the bust's scale on the breast bones, which the game applies after
    /// Customize+.
    /// </summary>
    public static Dictionary<string, PhysicsFrame> Reference(PhysicsSkeleton skeleton, IReadOnlyDictionary<string, PhysicsPlusBone>? plus,
        Vector3 bust)
    {
        var frames = PhysicsBody.RestPose(skeleton);
        if (plus is { Count: > 0 })
        {
            var children = skeleton.Body.ToLookup(bone => bone.Parent, StringComparer.Ordinal);
            foreach (var bone in Ordered(skeleton))
            {
                if (!plus.TryGetValue(bone, out var change) || !frames.TryGetValue(bone, out var initial))
                    continue;
                var rotation = Quaternion.Normalize(initial.Rotation * Turn(change.Rotation));
                var changed = new PhysicsFrame(initial.Position + Vector3.Transform(change.Translation, rotation), rotation, initial.Scale * change.Scaling);
                frames[bone] = changed;
                if (!change.Propagates)
                    continue;
                var childScale = change.ChildScalingIndependent ? initial.Scale * change.ChildScaling : changed.Scale;
                var spreadScale = change.PropagateScale && (change.Scaling != Vector3.One || change.ChildScalingIndependent && change.ChildScaling != Vector3.One);
                var spreadTurn = change.PropagateRotation && change.Rotation != Vector3.Zero;
                var deltaScale = Divide(childScale, initial.Scale);
                var deltaTurn = Quaternion.Normalize(changed.Rotation * Quaternion.Inverse(initial.Rotation));
                var deltaMove = changed.Position - initial.Position;
                foreach (var below in Descendants(bone, children))
                {
                    if (!frames.TryGetValue(below, out var child))
                        continue;
                    var offset = child.Position - changed.Position;
                    var scale = child.Scale;
                    var turn = child.Rotation;
                    if (spreadScale)
                    {
                        scale *= deltaScale;
                        offset *= deltaScale;
                    }
                    if (spreadTurn)
                    {
                        turn = Quaternion.Normalize(deltaTurn * turn);
                        offset = Vector3.Transform(offset, deltaTurn);
                    }
                    frames[below] = new PhysicsFrame(deltaMove + changed.Position + offset, turn, scale);
                }
            }
        }
        foreach (var breast in Breasts)
            if (frames.TryGetValue(breast, out var frame))
                frames[breast] = frame with { Scale = frame.Scale * bust };
        return frames;
    }

    /// <summary> Customize+'s turn for its angles in degrees: X as yaw, Y as pitch, Z as roll. </summary>
    internal static Quaternion Turn(Vector3 degrees)
        => degrees == Vector3.Zero
            ? Quaternion.Identity
            : Quaternion.CreateFromYawPitchRoll(degrees.X * MathF.PI / 180, degrees.Y * MathF.PI / 180, degrees.Z * MathF.PI / 180);

    /// <summary> The skeleton's bones, every parent before its children. </summary>
    private static IEnumerable<string> Ordered(PhysicsSkeleton skeleton)
    {
        var known = skeleton.Body.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var children = skeleton.Body.ToLookup(bone => bone.Parent, StringComparer.Ordinal);
        var roots = skeleton.Body.Where(bone => bone.Parent.Length == 0 || !known.Contains(bone.Parent)).Select(bone => bone.Name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var bone = queue.Dequeue();
            if (!seen.Add(bone))
                continue;
            yield return bone;
            foreach (var child in children[bone])
                queue.Enqueue(child.Name);
        }
    }

    private static IEnumerable<string> Descendants(string bone, ILookup<string, PhysicsBone> children)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { bone };
        var stack = new Stack<string>(children[bone].Select(child => child.Name));
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next))
                continue;
            yield return next;
            foreach (var child in children[next])
                stack.Push(child.Name);
        }
    }

    private static Vector3 Divide(Vector3 value, Vector3 by)
        => new(MathF.Abs(by.X) > 1e-6f ? value.X / by.X : 1, MathF.Abs(by.Y) > 1e-6f ? value.Y / by.Y : 1, MathF.Abs(by.Z) > 1e-6f ? value.Z / by.Z : 1);
}
