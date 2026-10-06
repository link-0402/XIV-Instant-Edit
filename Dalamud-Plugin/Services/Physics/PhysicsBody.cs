using System.Numerics;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Physics;

/// <summary> A bone's place in model space, in Havok's order: scaled, then turned, then moved. </summary>
internal readonly record struct PhysicsFrame(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static PhysicsFrame Rest(Vector3 position, Quaternion rotation) => new(position, rotation, Vector3.One);

    /// <summary> A point in the bone's space, its scale included, in model space. </summary>
    public Vector3 ToModel(Vector3 local) => Position + Vector3.Transform(local * Scale, Rotation);

    /// <summary> A model-space point in the bone's space, its scale included. </summary>
    public Vector3 ToLocal(Vector3 model)
    {
        var turned = Vector3.Transform(model - Position, Quaternion.Conjugate(Rotation));
        return new Vector3(Divide(turned.X, Scale.X), Divide(turned.Y, Scale.Y), Divide(turned.Z, Scale.Z));
    }

    /// <summary>
    /// Where an offset a physics file gives from this bone is in model space. Physics runs before
    /// Customize+ and the bust slider scale bones, so offsets turn and move with the bone but never scale.
    /// </summary>
    public Vector3 Place(Vector3 offset) => Position + Vector3.Transform(offset, Rotation);

    /// <summary> A direction in the bone's space, in model space, without scale. </summary>
    public Vector3 Turn(Vector3 direction) => Vector3.Transform(direction, Rotation);

    private static float Divide(float value, float by) => MathF.Abs(by) > 1e-6f ? value / by : value;
}

/// <summary>
/// A model the character draws, to measure: its file, the racial deformer that reshapes it for the
/// character (null for the character's own race), and the parts and shapes it turns on (null parts
/// counts every part).
/// </summary>
internal sealed record PhysicsBodyModel(string Name, byte[] Bytes, RacialDeformer? Deformer, uint? EnabledAttributes, uint EnabledShapes);

/// <summary>
/// The body as the game draws it this frame: every drawn part of the body models (skin, and the
/// underwear of the game's own smallclothes), posed through the live skeleton the way the game's
/// vertex shader poses it (racial deformer, rest pose, live pose), so Customize+, the bust slider and
/// the animation are all in. Each point keeps its heaviest bones and their weights, which tell the arm
/// from the chest it rests against, and the breast flesh that a body mod's spine bone partly carries
/// from the rest of the chest. Pure data, testable without the game.
/// </summary>
internal sealed class PhysicsBody
{
    /// <summary> How many bones each point keeps, heaviest first. </summary>
    public const int Influences = 4;

    private readonly Dictionary<string, int> boneIndex;

    private PhysicsBody(string[] bones, Vector3[] points, int[] pointBones, float[] pointWeights, float[] areas,
        IReadOnlyDictionary<string, PhysicsFrame> pose, IReadOnlyList<string> models, int missingBones)
    {
        Bones = bones;
        Points = points;
        PointBones = pointBones;
        PointWeights = pointWeights;
        Areas = areas;
        Pose = pose;
        Models = models;
        MissingBones = missingBones;
        boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < bones.Length; i++)
            boneIndex.TryAdd(bones[i], i);
    }

    /// <summary> The bones that move some point, which <see cref="PointBones"/> index. </summary>
    public IReadOnlyList<string> Bones { get; }

    /// <summary> The drawn skin's points in model space. </summary>
    public Vector3[] Points { get; }

    /// <summary> Point p's bones from p × <see cref="Influences"/> on, heaviest first: indices into <see cref="Bones"/>, or -1. </summary>
    public int[] PointBones { get; }

    /// <summary> The weights of <see cref="PointBones"/>, adding up to 1 for each point. </summary>
    public float[] PointWeights { get; }

    /// <summary> How much skin each point stands for: a third of its drawn triangles' area. </summary>
    public float[] Areas { get; }

    /// <summary> The live skeleton, by bone name, as the game drew it. </summary>
    public IReadOnlyDictionary<string, PhysicsFrame> Pose { get; }

    /// <summary> The models measured, by name. </summary>
    public IReadOnlyList<string> Models { get; }

    /// <summary> How many model bones the skeleton lacks: their points stay where the model has them. </summary>
    public int MissingBones { get; }

    public int BoneIndex(string bone) => boneIndex.GetValueOrDefault(bone, -1);

    /// <summary> A body from points already posed, such as a measurement kept from before or a test's. </summary>
    internal static PhysicsBody Of(IReadOnlyList<string> bones, Vector3[] points, int[] pointBones, float[] pointWeights, float[] areas,
        IReadOnlyDictionary<string, PhysicsFrame> pose)
    {
        if (pointBones.Length != points.Length * Influences || pointWeights.Length != pointBones.Length || areas.Length != points.Length)
            throw new ArgumentException("Each point needs its bones, weights and area.");
        return new PhysicsBody(bones.ToArray(), points, pointBones, pointWeights, areas, pose, [], 0);
    }

    /// <summary> The bone that moves point <paramref name="point"/> most. </summary>
    public int Owner(int point) => PointBones[point * Influences];

    /// <summary> How much of point <paramref name="point"/> these bones move, from 0 to 1. </summary>
    public float WeightOn(int point, IReadOnlySet<int> bones)
    {
        var total = 0f;
        for (var i = 0; i < Influences; i++)
        {
            var bone = PointBones[point * Influences + i];
            if (bone >= 0 && bones.Contains(bone))
                total += PointWeights[point * Influences + i];
        }
        return total;
    }

    /// <summary>
    /// Poses the drawn skin of <paramref name="models"/>: each point moves with each of its bones by the
    /// bone's racial deformer, then from the bone's <paramref name="rest"/> place to its <paramref name="pose"/>
    /// place, blended by weight. Throws <see cref="InvalidDataException"/> when nothing is drawn.
    /// </summary>
    public static PhysicsBody Measure(IEnumerable<PhysicsBodyModel> models, IReadOnlyDictionary<string, PhysicsFrame> rest,
        IReadOnlyDictionary<string, PhysicsFrame> pose)
    {
        var bones = new List<string>();
        var boneIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var points = new List<Vector3>();
        var pointBones = new List<int>();
        var pointWeights = new List<float>();
        var areas = new List<float>();
        var names = new List<string>();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var heaviest = new int[Influences];
        var heaviestWeights = new float[Influences];
        foreach (var model in models)
        {
            var mesh = ModelMeshReader.Read(model.Bytes, shapes: model.EnabledShapes != 0, skin: true);
            if (mesh.Bones.Count == 0)
                continue;
            names.Add(model.Name);
            // Per model bone: its racial deformer, and its rest and live places (null when the skeleton lacks it).
            var deformers = mesh.Bones.Select(bone => model.Deformer?.For(bone) ?? RacialDeformer.Affine.Identity).ToArray();
            var places = mesh.Bones.Select(bone => rest.TryGetValue(bone, out var from) && pose.TryGetValue(bone, out var to)
                ? ((PhysicsFrame From, PhysicsFrame To)?)(from, to)
                : null).ToArray();
            var ids = new int[mesh.Bones.Count];
            for (var b = 0; b < ids.Length; b++)
            {
                if (places[b] is null)
                    missing.Add(mesh.Bones[b]);
                if (!boneIds.TryGetValue(mesh.Bones[b], out ids[b]))
                {
                    ids[b] = boneIds[mesh.Bones[b]] = bones.Count;
                    bones.Add(mesh.Bones[b]);
                }
            }

            var swaps = ShapeSwaps(mesh, model.EnabledShapes);
            foreach (var part in mesh.Meshes)
            {
                if (part.Skin is not { } skin)
                    continue;
                swaps.TryGetValue(part.MeshIndex, out var swap);
                // The drawn triangles, through the shapes the model turns on.
                var triangles = new List<int>();
                foreach (var submesh in part.Submeshes)
                {
                    // The game draws a part only while all of its attributes are enabled.
                    if (model.EnabledAttributes is { } enabled && (submesh.AttributeMask & ~enabled) != 0)
                        continue;
                    for (var i = 0; i < submesh.Indices.Length; i++)
                        triangles.Add(swap is not null && swap.TryGetValue(submesh.MeshIndexStart + i, out var swapped) ? swapped : submesh.Indices[i]);
                }
                var pointOf = new Dictionary<int, int>();
                foreach (var vertex in triangles)
                {
                    if (pointOf.ContainsKey(vertex))
                        continue;
                    if (Skinned(part.Positions[vertex], skin, vertex, deformers, places, heaviest, heaviestWeights) is not { } posed)
                    {
                        pointOf[vertex] = -1;
                        continue;
                    }
                    pointOf[vertex] = points.Count;
                    points.Add(posed);
                    areas.Add(0);
                    for (var i = 0; i < Influences; i++)
                    {
                        pointBones.Add(heaviest[i] >= 0 ? ids[heaviest[i]] : -1);
                        pointWeights.Add(heaviestWeights[i]);
                    }
                }
                // Each point stands for a third of the skin of its triangles, so dense and sparse meshes count alike.
                for (var t = 0; t + 2 < triangles.Count; t += 3)
                {
                    int a = pointOf[triangles[t]], b = pointOf[triangles[t + 1]], c = pointOf[triangles[t + 2]];
                    if (a < 0 || b < 0 || c < 0)
                        continue;
                    var third = Vector3.Cross(points[b] - points[a], points[c] - points[a]).Length() / 6;
                    if (!float.IsFinite(third))
                        continue;
                    areas[a] += third;
                    areas[b] += third;
                    areas[c] += third;
                }
            }
        }
        if (points.Count == 0)
            throw new InvalidDataException("None of your body's skin is drawn, so there is nothing to measure.");
        return new PhysicsBody(bones.ToArray(), points.ToArray(), pointBones.ToArray(), pointWeights.ToArray(), areas.ToArray(), pose, names,
            missing.Count);
    }

    /// <summary>
    /// A vertex posed by its skin; <paramref name="heaviest"/> gets its heaviest model bones (-1 for none),
    /// heaviest first, and <paramref name="heaviestWeights"/> their share of its weight. Null without any weight.
    /// </summary>
    private static Vector3? Skinned(Vector3 vertex, ModelSkin skin, int index, RacialDeformer.Affine[] deformers,
        (PhysicsFrame From, PhysicsFrame To)?[] places, int[] heaviest, float[] heaviestWeights)
    {
        Array.Fill(heaviest, -1);
        Array.Clear(heaviestWeights);
        var sum = Vector3.Zero;
        var total = 0f;
        for (var i = 0; i < skin.Influences; i++)
        {
            var slot = index * skin.Influences + i;
            var bone = skin.Bones[slot];
            var weight = skin.Weights[slot];
            if (bone < 0 || bone >= deformers.Length || weight <= 0)
                continue;
            var point = deformers[bone].Point(vertex);
            if (places[bone] is { } place)
                point = place.To.ToModel(place.From.ToLocal(point));
            sum += weight * point;
            total += weight;
            Keep(bone, weight, heaviest, heaviestWeights);
        }
        if (total <= 1e-6f)
            return null;
        for (var i = 0; i < heaviestWeights.Length; i++)
            heaviestWeights[i] /= total;
        var posed = sum / total;
        return float.IsFinite(posed.X) && float.IsFinite(posed.Y) && float.IsFinite(posed.Z) ? posed : null;
    }

    // Keeps the heaviest bones, heaviest first; a bone named twice adds up.
    private static void Keep(int bone, float weight, int[] bones, float[] weights)
    {
        var at = Array.IndexOf(bones, bone);
        if (at >= 0)
        {
            weight += weights[at];
            for (var j = at; j < bones.Length - 1; j++)
            {
                bones[j] = bones[j + 1];
                weights[j] = weights[j + 1];
            }
            bones[^1] = -1;
            weights[^1] = 0;
        }
        for (var i = 0; i < bones.Length; i++)
        {
            if (bones[i] >= 0 && weights[i] >= weight)
                continue;
            for (var j = bones.Length - 1; j > i; j--)
            {
                bones[j] = bones[j - 1];
                weights[j] = weights[j - 1];
            }
            bones[i] = bone;
            weights[i] = weight;
            return;
        }
    }

    // Shapes swap some of a mesh's indices for their own vertices; later shapes win, as the game applies them in order.
    private static Dictionary<int, Dictionary<int, int>> ShapeSwaps(ModelMesh mesh, uint enabledShapes)
    {
        var swaps = new Dictionary<int, Dictionary<int, int>>();
        for (var s = 0; s < mesh.Shapes.Count && s < 32; s++)
        {
            if ((enabledShapes & (1u << s)) == 0)
                continue;
            foreach (var shapeMesh in mesh.Shapes[s].Meshes)
            {
                if (!swaps.TryGetValue(shapeMesh.MeshIndex, out var swap))
                    swaps[shapeMesh.MeshIndex] = swap = [];
                for (var i = 0; i < shapeMesh.Indices.Length; i++)
                    swap[shapeMesh.Indices[i]] = shapeMesh.Vertices[i];
            }
        }
        return swaps;
    }

    /// <summary> The skeleton's rest places in model space, from each bone's rest place relative to its parent. </summary>
    public static Dictionary<string, PhysicsFrame> RestPose(PhysicsSkeleton skeleton)
    {
        var frames = new Dictionary<string, PhysicsFrame>(StringComparer.Ordinal);
        foreach (var bone in skeleton.Body)
            Resolve(bone, 0);
        return frames;

        PhysicsFrame Resolve(PhysicsBone bone, int depth)
        {
            if (frames.TryGetValue(bone.Name, out var known))
                return known;
            var rotation = Quaternion.Normalize(bone.Rotation);
            // Havok's composition: the parent turns and moves the child; rest poses have no scale.
            var frame = depth < 512 && skeleton.Get(bone.Parent) is { } parent && parent.Name != bone.Name
                ? Resolve(parent, depth + 1) is var up
                    ? PhysicsFrame.Rest(up.Position + Vector3.Transform(bone.Position, up.Rotation), Quaternion.Normalize(up.Rotation * rotation))
                    : default
                : PhysicsFrame.Rest(bone.Position, rotation);
            frames[bone.Name] = frame;
            return frame;
        }
    }
}
