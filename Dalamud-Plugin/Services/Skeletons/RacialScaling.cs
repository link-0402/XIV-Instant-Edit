using System.Buffers.Binary;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.GameFiles;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// A model of one race reshaped for a character of another, as the game shows it: most female gear
/// is a c0201 model that a c0801 character wears through the racial deformer. For preview only:
/// nothing exported from a scaled import is written back.
/// </summary>
internal sealed record RacialScaling(ushort ModelRace, ushort CharacterRace, RacialDeformer Deformer)
{
    /// <summary> "Female Miqo'te", the race the model was reshaped for. </summary>
    public string CharacterLabel => GameRaces.Label(CharacterRace);

    /// <summary> "c0201 to Female Miqo'te". </summary>
    public string Description => $"c{ModelRace:D4} to {CharacterLabel}";

    public RacialScalingRecord ToRecord() => new() { ModelRace = ModelRace, CharacterRace = CharacterRace };

    /// <summary> Whether a saved scaling names two different known races. </summary>
    public static bool IsSafe(RacialScalingRecord? record)
        => record is null ||
           (GameRaces.Find(record.ModelRace) is not null && GameRaces.Find(record.CharacterRace) is not null &&
            record.ModelRace != record.CharacterRace);
}

/// <summary> The race a character is and the <c>human.pbd</c> it is drawn with, to reshape models of other races for it. </summary>
internal sealed record RacialScalingSource(ushort CharacterRace, byte[] Deformers)
{
    /// <summary>
    /// The scaling for the model at a game path or file name: null for models that aren't a human
    /// race's, or are the character's own race. Throws <see cref="InvalidDataException"/> when the
    /// deformer file can't reshape the model's race for the character.
    /// </summary>
    public RacialScaling? For(string modelPath)
    {
        if (ModelSkeletonPaths.Parse(modelPath) is not { Human: true } key || key.GenderRace == CharacterRace)
            return null;
        return new RacialScaling(key.GenderRace, CharacterRace, RacialDeformer.Create(Deformers, CharacterRace, key.GenderRace));
    }
}

/// <summary>
/// Moves every vertex of a Dawntrail (V6) model through a racial deformer, in place: positions,
/// normals and tangents of all LODs, shape vertices included, each blended by its skin weights as
/// the game's vertex shader blends the deformer's bone matrices. The model, mesh and bone bounding
/// boxes are measured again. Layout follows the add-on's xivpy reader (file.py, headers.py, lod.py, mesh.py).
/// </summary>
internal static class RacialScalingModel
{
    private const int FileHeaderSize = 68;
    private const int DeclarationSize = 17 * 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int BoxSize = 32;

    private const byte UsagePosition = 0, UsageWeights = 1, UsageIndices = 2, UsageNormal = 3, UsageTangent = 6;
    private const byte TypeSingle3 = 2, TypeSingle4 = 3, TypeUByte4 = 5, TypeNByte4 = 8, TypeHalf4 = 14, TypeUShort4 = 17;

    private readonly record struct Element(byte Stream, byte Offset, byte Type);

    /// <summary> A copy of the model reshaped by <paramref name="deformer"/>. </summary>
    public static byte[] Apply(byte[] source, RacialDeformer deformer)
    {
        var bytes = (byte[])source.Clone();
        if (deformer.BoneCount == 0)
            return bytes;
        Require(bytes.Length >= FileHeaderSize, "file header");
        if (U32(bytes, 0) != SkinModelVersion)
            throw new NotSupportedException("Only Dawntrail (V6) models can be scaled.");
        var stackSize = U32(bytes, 4);
        var runtimeSize = U32(bytes, 8);
        int declarationCount = U16(bytes, 12);
        int lodCount = bytes[64];
        if (lodCount is < 1 or > 3)
            throw new InvalidDataException("The model has no valid LOD count.");
        var dataOffset = checked(FileHeaderSize + (long)stackSize + runtimeSize);
        Require(dataOffset <= bytes.Length, "geometry");

        var declarations = new Dictionary<byte, Element>[declarationCount];
        for (var d = 0; d < declarationCount; d++)
        {
            declarations[d] = [];
            var at = FileHeaderSize + d * DeclarationSize;
            Require(at + DeclarationSize <= bytes.Length, "vertex declaration");
            for (var e = 0; e < 17; e++)
            {
                var element = at + e * 8;
                if (bytes[element] == 0xFF)
                    break;
                // The first element of a usage is the one the game skins (a second UV or colour set has index 1).
                declarations[d].TryAdd(bytes[element + 3], new Element(bytes[element], bytes[element + 1], bytes[element + 2]));
            }
        }

        var stringHeader = FileHeaderSize + declarationCount * DeclarationSize;
        Require(stringHeader + 8 <= bytes.Length, "string table");
        var stringSize = (int)U32(bytes, stringHeader + 4);
        var stringBlock = stringHeader + 8;
        Require(stringSize >= 0 && stringBlock + (long)stringSize <= bytes.Length, "string table");
        var meshHeader = stringBlock + stringSize;
        Require(meshHeader + MeshHeaderSize <= bytes.Length, "mesh header");
        int meshCount = U16(bytes, meshHeader + 4);
        int attributeCount = U16(bytes, meshHeader + 6);
        int submeshCount = U16(bytes, meshHeader + 8);
        int materialCount = U16(bytes, meshHeader + 10);
        int boneCount = U16(bytes, meshHeader + 12);
        int boneTableCount = U16(bytes, meshHeader + 14);
        int shapeCount = U16(bytes, meshHeader + 16);
        int shapeMeshCount = U16(bytes, meshHeader + 18);
        int shapeValueCount = U16(bytes, meshHeader + 20);
        int elementIdCount = U16(bytes, meshHeader + 24);
        int terrainShadowMeshCount = bytes[meshHeader + 26];
        var flags2 = bytes[meshHeader + 27];
        int terrainShadowSubmeshCount = U16(bytes, meshHeader + 38);
        int neckMorphCount = bytes[meshHeader + 43];
        int boneTableArrayCount = U16(bytes, meshHeader + 44);
        var faceDataCount = U32(bytes, meshHeader + 48);
        // Face models keep neck morph positions and face data outside the vertex buffers.
        if (neckMorphCount > 0 || faceDataCount > 0)
            throw new NotSupportedException("Face models (with neck morphs or face data) can't be scaled.");

        long cursor = meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize;
        var lodTable = (int)cursor;
        cursor += 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0);
        var meshTable = (int)cursor;
        cursor += meshCount * MeshSize + attributeCount * 4 + terrainShadowMeshCount * 20 + submeshCount * 16 + terrainShadowSubmeshCount * 12 +
                  materialCount * 4;
        var boneNameTable = (int)cursor;
        cursor += boneCount * 4;
        var boneTables = (int)cursor;
        cursor += boneTableCount * 4 + boneTableArrayCount * 2 + shapeCount * 16 + shapeMeshCount * 12 + shapeValueCount * 4;
        Require(cursor + 4 <= bytes.Length, "submesh bone map");
        cursor += 4 + U32(bytes, (int)cursor);
        Require(cursor < bytes.Length, "padding");
        cursor += 1 + bytes[cursor];
        var boxes = cursor;
        cursor += (4 + boneCount) * BoxSize;
        if (cursor > dataOffset)
            throw new InvalidDataException("The model's tables run into its geometry.");

        var bones = ReadNames(bytes, boneNameTable, boneCount, stringBlock, stringSize);
        var tables = new int[boneTableCount][];
        for (var t = 0; t < boneTableCount; t++)
        {
            var header = boneTables + t * 4;
            int offset = U16(bytes, header), count = U16(bytes, header + 2);
            var start = header + offset * 4;
            Require(start + count * 2 <= bytes.Length, "bone table");
            tables[t] = new int[count];
            for (var i = 0; i < count; i++)
                if ((tables[t][i] = U16(bytes, start + i * 2)) >= boneCount)
                    throw new InvalidDataException("A bone table names a bone outside the bone list.");
        }
        var boneMatrices = bones.Select(deformer.For).ToArray();

        var bounds = new Bounds();
        var boneBounds = new Bounds[boneCount];
        var done = new HashSet<int>();
        for (var lod = 0; lod < lodCount; lod++)
        {
            var vertexOffset = U32(bytes, 16 + lod * 4);
            var entry = lodTable + lod * LodSize;
            // The LOD's mesh ranges: its meshes, then its water, shadow and vertical fog meshes.
            foreach (var range in new[] { 0, 12, 16, 24 })
            {
                int first = U16(bytes, entry + range), count = U16(bytes, entry + range + 2);
                for (var mesh = first; mesh < first + count; mesh++)
                {
                    if (mesh >= meshCount)
                        throw new InvalidDataException("A LOD references meshes outside the mesh table.");
                    if (!done.Add(mesh))
                        continue;
                    if (mesh >= declarationCount)
                        throw new InvalidDataException("A mesh has no vertex declaration.");
                    var at = meshTable + mesh * MeshSize;
                    int boneTable = U16(bytes, at + 14);
                    var table = boneTable < tables.Length ? tables[boneTable] : [];
                    TransformMesh(bytes, vertexOffset, at, declarations[mesh], table, boneMatrices, bounds, boneBounds);
                }
            }
        }

        WriteBounds(bytes, (int)boxes, meshHeader, bounds, boneBounds);
        return bytes;
    }

    private const uint SkinModelVersion = 0x01000006;

    private static void TransformMesh(byte[] bytes, uint vertexOffset, int mesh, Dictionary<byte, Element> elements, int[] table,
        RacialDeformer.Affine[] boneMatrices, Bounds bounds, Bounds[] boneBounds)
    {
        int count = U16(bytes, mesh);
        if (count == 0)
            return;
        if (!elements.TryGetValue(UsagePosition, out var position))
            throw new NotSupportedException("A mesh has no position stream.");
        var positionAt = Locate(bytes, vertexOffset, mesh, position, count, position.Type switch
        {
            TypeSingle3 => 12, TypeSingle4 => 16, TypeHalf4 => 8,
            _ => throw new NotSupportedException($"Unsupported position layout (type {position.Type})."),
        });
        var normalAt = elements.TryGetValue(UsageNormal, out var normal) ? Locate(bytes, vertexOffset, mesh, normal, count, DirectionSize(normal.Type)) : null;
        var tangentAt = elements.TryGetValue(UsageTangent, out var tangent) ? Locate(bytes, vertexOffset, mesh, tangent, count, DirectionSize(tangent.Type)) : null;
        var hasWeights = elements.TryGetValue(UsageWeights, out var weights);
        var hasIndices = elements.TryGetValue(UsageIndices, out var indices);
        if (hasWeights != hasIndices)
            throw new InvalidDataException("A mesh has skin weights without bone indices, or indices without weights.");
        Func<int, int>? weightsAt = null, indicesAt = null;
        var influences = 0;
        if (hasWeights)
        {
            var weightWidth = SkinWidth(weights.Type);
            var indexWidth = SkinWidth(indices.Type);
            influences = Math.Min(weightWidth, indexWidth);
            weightsAt = Locate(bytes, vertexOffset, mesh, weights, count, weightWidth);
            indicesAt = Locate(bytes, vertexOffset, mesh, indices, count, indexWidth);
        }

        var matrices = table.Select(bone => boneMatrices[bone]).ToArray();
        var moves = matrices.Any(matrix => matrix != RacialDeformer.Affine.Identity);
        Span<float> vertexWeights = stackalloc float[8];
        Span<byte> vertexBones = stackalloc byte[8];
        for (var v = 0; v < count; v++)
        {
            var p = ReadVector3(bytes, positionAt(v), position.Type);
            var used = 0;
            if (weightsAt is not null && indicesAt is not null)
            {
                var w = weightsAt(v);
                var b = indicesAt(v);
                for (var i = 0; i < influences; i++)
                {
                    vertexWeights[i] = bytes[w + i] / 255f;
                    vertexBones[i] = bytes[b + i];
                }
                used = influences;
            }
            if (moves && used > 0)
            {
                var matrix = RacialDeformer.Blend(matrices, vertexBones[..used], vertexWeights[..used]);
                if (matrix != RacialDeformer.Affine.Identity)
                {
                    WritePosition(bytes, positionAt(v), position.Type, matrix.Point(p));
                    // The boxes measure the position as stored, after half precision rounds it.
                    p = ReadVector3(bytes, positionAt(v), position.Type);
                    if (normalAt is not null)
                        WriteDirection(bytes, normalAt(v), normal.Type, matrix);
                    if (tangentAt is not null)
                        WriteDirection(bytes, tangentAt(v), tangent.Type, matrix);
                }
            }
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                continue;
            bounds.Add(p);
            for (var i = 0; i < used; i++)
                if (vertexWeights[i] > 0 && vertexBones[i] < table.Length)
                    (boneBounds[table[vertexBones[i]]] ??= new Bounds()).Add(p);
        }
    }

    private static int DirectionSize(byte type) => type switch
    {
        TypeSingle3 => 12, TypeSingle4 => 16, TypeHalf4 => 8, TypeNByte4 => 4,
        _ => throw new NotSupportedException($"Unsupported normal or tangent layout (type {type})."),
    };

    // Weights and indices take a byte per influence: four, or eight in a UShort4 element.
    private static int SkinWidth(byte type) => type switch
    {
        TypeUByte4 or TypeNByte4 => 4, TypeUShort4 => 8,
        _ => throw new NotSupportedException($"Unsupported skin weight layout (type {type})."),
    };

    /// <summary> The byte offset of each vertex's element, after checking the whole stream is inside the file. </summary>
    private static Func<int, int> Locate(byte[] bytes, uint vertexOffset, int mesh, Element element, int count, int size)
    {
        if (element.Stream > 2)
            throw new InvalidDataException("A vertex element names an invalid stream.");
        var streamOffset = U32(bytes, mesh + 20 + element.Stream * 4);
        int stride = bytes[mesh + 32 + element.Stream];
        if (stride < size || element.Offset + size > stride)
            throw new InvalidDataException("A vertex stream stride is inconsistent.");
        var first = checked((long)vertexOffset + streamOffset + element.Offset);
        if (checked(first + (long)(count - 1) * stride + size) > bytes.Length)
            throw new InvalidDataException("The vertex buffer is truncated.");
        return v => (int)(first + (long)v * stride);
    }

    private static Vector3 ReadVector3(byte[] bytes, int at, byte type) => type switch
    {
        TypeSingle3 or TypeSingle4 => new Vector3(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8)),
        TypeHalf4 => new Vector3(F16(bytes, at), F16(bytes, at + 2), F16(bytes, at + 4)),
        TypeNByte4 => new Vector3(bytes[at] / 127.5f - 1f, bytes[at + 1] / 127.5f - 1f, bytes[at + 2] / 127.5f - 1f),
        _ => throw new NotSupportedException($"Unsupported vector layout (type {type})."),
    };

    // The fourth component (padding, or a tangent's handedness) is kept.
    private static void WritePosition(byte[] bytes, int at, byte type, Vector3 value)
    {
        if (type == TypeHalf4)
        {
            WriteF16(bytes, at, value.X);
            WriteF16(bytes, at + 2, value.Y);
            WriteF16(bytes, at + 4, value.Z);
            return;
        }
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 8), value.Z);
    }

    private static void WriteDirection(byte[] bytes, int at, byte type, RacialDeformer.Affine matrix)
    {
        var direction = matrix.Direction(ReadVector3(bytes, at, type));
        var length = direction.Length();
        if (!float.IsFinite(length) || length < 1e-6f)
            return;
        direction /= length;
        if (type != TypeNByte4)
        {
            WritePosition(bytes, at, type, direction);
            return;
        }
        // The add-on's vector_to_bytes: [-1, 1] to 0..255, rounded.
        bytes[at] = (byte)Math.Clamp(MathF.Round((direction.X + 1) * 127.5f), 0, 255);
        bytes[at + 1] = (byte)Math.Clamp(MathF.Round((direction.Y + 1) * 127.5f), 0, 255);
        bytes[at + 2] = (byte)Math.Clamp(MathF.Round((direction.Z + 1) * 127.5f), 0, 255);
    }

    private sealed class Bounds
    {
        public Vector3 Min = new(float.MaxValue);
        public Vector3 Max = new(float.MinValue);
        public bool Empty => Min.X > Max.X;

        public void Add(Vector3 p)
        {
            Min = Vector3.Min(Min, p);
            Max = Vector3.Max(Max, p);
        }
    }

    /// <summary>
    /// The model box (the second of the four) and each bone box that was set become the new extents.
    /// The first box follows the model box where it matched it: the add-on stores the model box with
    /// its bottom at the origin there. The radius follows the first box when it was measured from it.
    /// </summary>
    private static void WriteBounds(byte[] bytes, int boxes, int meshHeader, Bounds bounds, Bounds[] boneBounds)
    {
        if (bounds.Empty)
            return;
        var oldFirst = ReadBox(bytes, boxes);
        var oldModel = ReadBox(bytes, boxes + BoxSize);
        var oldRadius = F32(bytes, meshHeader);
        var first = (Min: Follow(oldFirst.Min, oldModel.Min, bounds.Min), Max: Follow(oldFirst.Max, oldModel.Max, bounds.Max));
        WriteBox(bytes, boxes + BoxSize, bounds.Min, bounds.Max);
        WriteBox(bytes, boxes, first.Min, first.Max);
        if (MathF.Abs(oldRadius - Radius(oldFirst.Min, oldFirst.Max)) <= 1e-4f * MathF.Max(1, oldRadius))
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(meshHeader), Radius(first.Min, first.Max));
        for (var bone = 0; bone < boneBounds.Length; bone++)
        {
            var at = boxes + (4 + bone) * BoxSize;
            var old = ReadBox(bytes, at);
            if (boneBounds[bone] is { Empty: false } box && (old.Min != Vector3.Zero || old.Max != Vector3.Zero))
                WriteBox(bytes, at, box.Min, box.Max);
        }

        static Vector3 Follow(Vector3 old, Vector3 oldModel, Vector3 model)
            => new(old.X == oldModel.X ? model.X : old.X, old.Y == oldModel.Y ? model.Y : old.Y, old.Z == oldModel.Z ? model.Z : old.Z);
    }

    // The add-on's BoundingBox.radius: the length of the largest extent along each axis.
    private static float Radius(Vector3 min, Vector3 max) => Vector3.Max(Vector3.Abs(min), Vector3.Abs(max)).Length();

    private static (Vector3 Min, Vector3 Max) ReadBox(byte[] bytes, int at)
        => (new Vector3(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8)), new Vector3(F32(bytes, at + 16), F32(bytes, at + 20), F32(bytes, at + 24)));

    // The fourth components hold 1, as the add-on writes them.
    private static void WriteBox(byte[] bytes, int at, Vector3 min, Vector3 max)
    {
        ReadOnlySpan<float> values = [min.X, min.Y, min.Z, 1, max.X, max.Y, max.Z, 1];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }

    private static string[] ReadNames(byte[] bytes, int table, int count, int block, int blockSize)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = (int)U32(bytes, table + i * 4);
            if (offset < 0 || offset >= blockSize)
                throw new InvalidDataException("A bone name points outside the string table.");
            var end = Array.IndexOf(bytes, (byte)0, block + offset, blockSize - offset);
            if (end < 0)
                throw new InvalidDataException("A bone name in the string table is not terminated.");
            names[i] = System.Text.Encoding.UTF8.GetString(bytes, block + offset, end - block - offset);
        }
        return names;
    }

    private static ushort U16(byte[] bytes, int at)
    {
        Require(at >= 0 && at + 2 <= bytes.Length, "model data");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    }

    private static uint U32(byte[] bytes, int at)
    {
        Require(at >= 0 && at + 4 <= bytes.Length, "model data");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    }

    private static float F32(byte[] bytes, int at)
    {
        Require(at >= 0 && at + 4 <= bytes.Length, "model data");
        return BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at, 4));
    }

    private static float F16(byte[] bytes, int at) => (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2)));
    private static void WriteF16(byte[] bytes, int at, float value) => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at), BitConverter.HalfToInt16Bits((Half)value));

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The {what} is outside the file.");
    }
}
