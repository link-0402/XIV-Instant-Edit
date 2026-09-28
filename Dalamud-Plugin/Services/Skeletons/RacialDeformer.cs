using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// The per-bone matrices that move a model made for one race (a shared body such as c0201) onto
/// another race's skeleton, read from <c>chara/xls/boneDeformer/human.pbd</c>. Each race's entry
/// moves its parent race's shape onto its own, so a chain applies the oldest ancestor's first: a
/// c0101 model on c0801 goes through c0201's matrices, then c0801's (checked against the game's
/// base skeletons: 4 mm mean bone error that way round, 8 mm the other). Only the bones the
/// skeleton race's entry lists move, as in Penumbra's <c>PbdFile.GetRacialDeformer</c>.
/// </summary>
internal sealed class RacialDeformer
{
    public const string GamePath = "chara/xls/boneDeformer/human.pbd";

    /// <summary> A 3×4 row-major affine transform: rows X, Y, Z with the translation in W. </summary>
    internal readonly record struct Affine(Vector4 X, Vector4 Y, Vector4 Z)
    {
        public static readonly Affine Identity = new(Vector4.UnitX, Vector4.UnitY, Vector4.UnitZ);

        public Vector3 Point(Vector3 p) => new(Vector4.Dot(X, new Vector4(p, 1)), Vector4.Dot(Y, new Vector4(p, 1)), Vector4.Dot(Z, new Vector4(p, 1)));
        public Vector3 Direction(Vector3 d) => new(Vector4.Dot(X, new Vector4(d, 0)), Vector4.Dot(Y, new Vector4(d, 0)), Vector4.Dot(Z, new Vector4(d, 0)));

        /// <summary> <paramref name="after"/> applied after this transform (Penumbra's Append). </summary>
        public Affine Then(Affine after)
            => new(Row(after.X, this), Row(after.Y, this), Row(after.Z, this));

        private static Vector4 Row(Vector4 row, Affine m) => new(
            row.X * m.X.X + row.Y * m.Y.X + row.Z * m.Z.X,
            row.X * m.X.Y + row.Y * m.Y.Y + row.Z * m.Z.Y,
            row.X * m.X.Z + row.Y * m.Y.Z + row.Z * m.Z.Z,
            row.X * m.X.W + row.Y * m.Y.W + row.Z * m.Z.W + row.W);
    }

    private readonly IReadOnlyDictionary<string, Affine> _matrices;

    private RacialDeformer(IReadOnlyDictionary<string, Affine> matrices) => _matrices = matrices;

    public static readonly RacialDeformer None = new(new Dictionary<string, Affine>());

    public int BoneCount => _matrices.Count;

    public Affine For(string bone) => _matrices.TryGetValue(bone, out var matrix) ? matrix : Affine.Identity;

    /// <summary> The deformer for a model of race <paramref name="modelRace"/> worn by <paramref name="skeletonRace"/> (codes like 801). </summary>
    public static RacialDeformer Create(byte[] pbd, int skeletonRace, int modelRace)
    {
        if (skeletonRace == modelRace)
            return None;
        if (pbd.Length < 4)
            throw new InvalidDataException("The racial deformer file is truncated.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(pbd);
        if (count is < 1 or > 4096 || 4 + count * 20L > pbd.Length)
            throw new InvalidDataException("The racial deformer file has an invalid entry count.");
        var entries = new (int Race, int Tree, int Offset)[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * 12;
            entries[i] = (BinaryPrimitives.ReadUInt16LittleEndian(pbd.AsSpan(at)), BinaryPrimitives.ReadInt16LittleEndian(pbd.AsSpan(at + 2)),
                BinaryPrimitives.ReadInt32LittleEndian(pbd.AsSpan(at + 4)));
        }
        var tree = new (short Parent, short Deformer)[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + count * 12 + i * 8;
            tree[i] = (BinaryPrimitives.ReadInt16LittleEndian(pbd.AsSpan(at)), BinaryPrimitives.ReadInt16LittleEndian(pbd.AsSpan(at + 6)));
        }

        int Index(int race)
        {
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Race == race)
                    return i;
            throw new InvalidDataException($"The racial deformer file has no entry for race {race:D4}.");
        }

        int? Parent(int entry)
        {
            var node = entries[entry].Tree;
            if (node < 0 || node >= tree.Length || tree[node].Parent < 0 || tree[node].Parent >= tree.Length)
                return null;
            var deformer = tree[tree[node].Parent].Deformer;
            return deformer >= 0 && deformer < entries.Length ? deformer : null;
        }

        var current = Index(skeletonRace);
        var result = Read(pbd, entries[current].Offset);
        for (var guard = 0; guard < count; guard++)
        {
            if (Parent(current) is not { } parent)
                throw new InvalidDataException($"Race {modelRace:D4} is not an ancestor of {skeletonRace:D4} in the racial deformer file.");
            if (entries[parent].Race == modelRace)
                return new RacialDeformer(result);
            // The parent's matrices bring the model to the parent race first; the matrices so far follow.
            foreach (var (bone, matrix) in Read(pbd, entries[parent].Offset))
                if (result.TryGetValue(bone, out var existing))
                    result[bone] = matrix.Then(existing);
            current = parent;
        }
        throw new InvalidDataException("The racial deformer tree has a cycle.");
    }

    private static Dictionary<string, Affine> Read(byte[] pbd, int offset)
    {
        var result = new Dictionary<string, Affine>(StringComparer.Ordinal);
        if (offset == 0)
            return result;
        if (offset < 0 || offset + 4 > pbd.Length)
            throw new InvalidDataException("A racial deformer entry is outside the file.");
        var bones = BinaryPrimitives.ReadInt32LittleEndian(pbd.AsSpan(offset));
        var matrices = offset + 4 + bones * 2 + ((bones & 1) != 0 ? 2 : 0);
        if (bones is < 0 or > 4096 || matrices + bones * 48L > pbd.Length)
            throw new InvalidDataException("A racial deformer entry is truncated.");
        for (var i = 0; i < bones; i++)
        {
            var nameOffset = offset + BinaryPrimitives.ReadUInt16LittleEndian(pbd.AsSpan(offset + 4 + i * 2));
            var end = Array.IndexOf(pbd, (byte)0, nameOffset);
            if (nameOffset >= pbd.Length || end < 0)
                throw new InvalidDataException("A racial deformer bone name is invalid.");
            var name = Encoding.UTF8.GetString(pbd, nameOffset, end - nameOffset);
            var at = matrices + i * 48;
            Vector4 Row(int row) => new(
                BinaryPrimitives.ReadSingleLittleEndian(pbd.AsSpan(at + row * 16)), BinaryPrimitives.ReadSingleLittleEndian(pbd.AsSpan(at + row * 16 + 4)),
                BinaryPrimitives.ReadSingleLittleEndian(pbd.AsSpan(at + row * 16 + 8)), BinaryPrimitives.ReadSingleLittleEndian(pbd.AsSpan(at + row * 16 + 12)));
            result[name] = new Affine(Row(0), Row(1), Row(2));
        }
        return result;
    }

    /// <summary>
    /// The matrices of one vertex's bones blended by its skin weights, as the game's vertex shader
    /// blends its joint matrices: <paramref name="table"/> holds the matrix of each bone the blend
    /// indices name. Identity for a vertex without weight.
    /// </summary>
    public static Affine Blend(IReadOnlyList<Affine> table, ReadOnlySpan<byte> indices, ReadOnlySpan<float> weights)
    {
        Vector4 x = Vector4.Zero, y = Vector4.Zero, z = Vector4.Zero;
        var total = 0f;
        for (var i = 0; i < weights.Length; i++)
        {
            var weight = weights[i];
            if (weight <= 0)
                continue;
            var matrix = indices[i] < table.Count ? table[indices[i]] : Affine.Identity;
            x += weight * matrix.X;
            y += weight * matrix.Y;
            z += weight * matrix.Z;
            total += weight;
        }
        return total > 1e-6f ? new Affine(x / total, y / total, z / total) : Affine.Identity;
    }
}
