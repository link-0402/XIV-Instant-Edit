using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// How far a body area can swing before its skin sinks into the torso. The part's skin turns about
/// the bone toward every side of the swing, each point by the share of it the area's bones carry; a
/// swing is clear while less than 3% of the part's skin sinks 1.5 cm deeper past the torso's skin than
/// it does at rest, about where clipping starts to show. A centimetre would cut breasts to the game's
/// own small swing, since a breast swinging toward the middle soon presses its inner side into the
/// chest a little. The torso is the skin the hips and spine carry most that no
/// physics area moves: arms and legs move with the animation, where collision shapes keep parts out,
/// and the other breast or cheek moves itself.
/// </summary>
internal static class PhysicsClearance
{
    private const float PartWeight = 0.1f;
    private const float Sink = 0.015f;
    private const float SinkingShare = 0.03f;
    private const float Cell = 0.01f;
    private const float Reach = 0.03f;
    private const int Sides = 16;
    private const int Step = 5;
    private static readonly string[] TorsoBones = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c"];

    /// <summary>
    /// The torso's skin in a grid of centimetre cells, each knowing the nearest torso point within three
    /// centimetres, so how deep a point sits past the skin is one lookup.
    /// </summary>
    internal sealed class Torso
    {
        private readonly PhysicsBody body;
        private readonly Vector3 origin;
        private readonly int sizeX, sizeY, sizeZ;
        private readonly int[] nearest;

        internal Torso(PhysicsBody body, IReadOnlyList<int> points)
        {
            this.body = body;
            Count = points.Count;
            if (points.Count == 0)
            {
                nearest = [];
                return;
            }
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var i in points)
            {
                min = Vector3.Min(min, body.Points[i]);
                max = Vector3.Max(max, body.Points[i]);
            }
            origin = min - new Vector3(Reach);
            var size = (max - min + new Vector3(2 * Reach)) / Cell;
            (sizeX, sizeY, sizeZ) = ((int)size.X + 1, (int)size.Y + 1, (int)size.Z + 1);
            nearest = new int[sizeX * sizeY * sizeZ];
            Array.Fill(nearest, -1);
            var best = new float[nearest.Length];
            Array.Fill(best, float.MaxValue);
            var cells = (int)MathF.Ceiling(Reach / Cell);
            foreach (var i in points)
            {
                var point = body.Points[i];
                var (cx, cy, cz) = CellOf(point);
                for (var x = Math.Max(0, cx - cells); x <= Math.Min(sizeX - 1, cx + cells); x++)
                for (var y = Math.Max(0, cy - cells); y <= Math.Min(sizeY - 1, cy + cells); y++)
                for (var z = Math.Max(0, cz - cells); z <= Math.Min(sizeZ - 1, cz + cells); z++)
                {
                    var centre = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * Cell;
                    var distance = Vector3.DistanceSquared(centre, point);
                    if (distance > Reach * Reach)
                        continue;
                    var index = (x * sizeY + y) * sizeZ + z;
                    if (distance < best[index])
                    {
                        best[index] = distance;
                        nearest[index] = i;
                    }
                }
            }
        }

        /// <summary> How many torso points there are. </summary>
        public int Count { get; }

        /// <summary> How far a place lies past the torso's skin (negative in front of it), or null far from it. </summary>
        public float? Depth(Vector3 place)
        {
            if (nearest.Length == 0)
                return null;
            var (x, y, z) = CellOf(place);
            if (x < 0 || y < 0 || z < 0 || x >= sizeX || y >= sizeY || z >= sizeZ)
                return null;
            var point = nearest[(x * sizeY + y) * sizeZ + z];
            return point < 0 ? null : Vector3.Dot(body.Points[point] - place, body.Normals[point]);
        }

        private (int X, int Y, int Z) CellOf(Vector3 place)
        {
            var cell = (place - origin) / Cell;
            return ((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z));
        }
    }

    /// <summary> The torso of a body: skin the hips and spine carry most, facing somewhere, that no body area moves. </summary>
    public static Torso TorsoOf(PhysicsBody body, PhysicsSkeleton skeleton)
    {
        var torso = TorsoBones.Select(body.BoneIndex).Where(index => index >= 0).ToHashSet();
        var areas = PhysicsFit.Region(body, skeleton, PhysicsAreas.Kinds.Where(kind => kind.Body).SelectMany(kind => kind.Bones)
            .Concat(skeleton.Body.Select(bone => bone.Name).Where(name => PhysicsAreas.KindOf(name) is { Body: true })).Distinct());
        var points = new List<int>();
        for (var i = 0; i < body.Points.Length; i++)
            if (torso.Contains(body.Owner(i)) && body.WeightOn(i, areas) < PartWeight && body.Normals[i] != Vector3.Zero)
                points.Add(i);
        return new Torso(body, points);
    }

    /// <summary>
    /// The largest swing in whole degrees a chain's bone is clear to make, up to 180 (no limit needed),
    /// and never less than its rest place needs. Null when the chain's part has no skin, the body no
    /// torso, or the skeleton lacks its bones.
    /// </summary>
    public static float? Degrees(PhysicsBody body, PhysicsSkeleton skeleton, Torso torso, PhybSimulator simulator, int chain)
    {
        if (torso.Count == 0 || chain < 0 || chain >= simulator.Chains.Count || simulator.Chains[chain].Nodes.Count < 2)
            return null;
        var own = simulator.Chains[chain];
        var root = own.Nodes[0].Bone.Text;
        if (!body.Pose.TryGetValue(root, out var frame))
            return null;
        var pivot = frame.Position;
        var next = own.Nodes[1].Bone.Text is { Length: > 0 } bone && body.Pose.TryGetValue(bone, out var after)
            ? after.Position
            : frame.Place(own.LastBoneOffset);
        if ((next - pivot).LengthSquared() < 1e-8f)
            return null;
        var rest = Vector3.Normalize(next - pivot);
        var axis = rest;
        if (skeleton.Get(root)?.Parent is { Length: > 0 } parent && body.Pose.TryGetValue(parent, out var parentFrame) &&
            own.Nodes[0].ConeAxisOffset.LengthSquared() > 1e-12f)
            axis = Vector3.Normalize(parentFrame.Turn(own.Nodes[0].ConeAxisOffset));

        // The part's skin, how much of each point the part carries, and how deep it sits at rest.
        var region = PhysicsFit.Region(body, skeleton, own.Nodes.Select(node => node.Bone.Text).Where(name => name.Length > 0));
        var part = new List<(int Point, float Share, float AtRest)>();
        var total = 0f;
        for (var i = 0; i < body.Points.Length; i++)
        {
            var share = body.WeightOn(i, region);
            if (share < PartWeight)
                continue;
            part.Add((i, share, Math.Max(torso.Depth(body.Points[i]) ?? 0, 0)));
            total += body.Areas[i];
        }
        if (part.Count == 0 || !(total > 0))
            return null;

        var restAngle = Angle(rest, axis);
        var sides = Perpendiculars(axis);
        bool Clear(float degrees)
        {
            var radians = degrees * MathF.PI / 180;
            for (var side = 0; side < Sides; side++)
            {
                var around = side * 2 * MathF.PI / Sides;
                var toward = MathF.Cos(radians) * axis + MathF.Sin(radians) * (MathF.Cos(around) * sides.A + MathF.Sin(around) * sides.B);
                var turn = Between(rest, toward);
                var sinking = 0f;
                foreach (var (point, share, depth) in part)
                {
                    var offset = body.Points[point] - pivot;
                    var moved = body.Points[point] + share * (Vector3.Transform(offset, turn) - offset);
                    if (torso.Depth(moved) is { } now && now > depth + Sink)
                        sinking += body.Areas[point];
                }
                if (sinking > SinkingShare * total)
                    return false;
            }
            return true;
        }

        var clear = 180;
        for (var degrees = Step; degrees <= 180; degrees += Step)
        {
            if (Clear(degrees))
                continue;
            clear = degrees - Step;
            for (var finer = degrees - 1; finer > degrees - Step; finer--)
                if (Clear(finer))
                {
                    clear = finer;
                    break;
                }
            break;
        }
        // The cone has to hold the bone's rest place, or the game pushes it away from rest.
        return Math.Min(180, Math.Max(clear, MathF.Ceiling(restAngle) + 2));
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI;

    /// <summary> Two directions at right angles to an axis and each other. </summary>
    private static (Vector3 A, Vector3 B) Perpendiculars(Vector3 axis)
    {
        var helper = MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var a = Vector3.Normalize(Vector3.Cross(axis, helper));
        return (a, Vector3.Cross(axis, a));
    }

    /// <summary> The shortest turn from one direction to another. </summary>
    internal static Quaternion Between(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.999999f)
            return Quaternion.Identity;
        if (dot < -0.999999f)
        {
            var (perpendicular, _) = Perpendiculars(from);
            return Quaternion.CreateFromAxisAngle(perpendicular, MathF.PI);
        }
        var cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1 + dot));
    }
}
