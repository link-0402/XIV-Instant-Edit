using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.Previews;

/// <summary> LOD-0 geometry of a model with normals, the first UV set and submesh attributes. </summary>
public sealed record ModelMesh(IReadOnlyList<ModelMeshPart> Meshes, IReadOnlyList<string> Materials, IReadOnlyList<string> Attributes)
{
    /// <summary> The model's shapes in its shape order, when read with them; empty otherwise. </summary>
    public IReadOnlyList<ModelShape> Shapes { get; init; } = [];
}

/// <summary> One mesh: its vertices and the triangles of each submesh, indexed into those vertices. </summary>
public sealed record ModelMeshPart(int MeshIndex, int MaterialIndex, Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs,
    IReadOnlyList<ModelSubmesh> Submeshes);

public sealed record ModelSubmesh(int SubmeshIndex, int[] Indices, uint AttributeMask)
{
    /// <summary> Where the submesh's indices start among its mesh's indices, which shape values count in. </summary>
    public int MeshIndexStart { get; init; }
}

/// <summary> A shape and the LOD-0 meshes it changes. </summary>
public sealed record ModelShape(string Name, IReadOnlyList<ModelShapeMesh> Meshes);

/// <summary>
/// One mesh's part of a shape: while the shape is on, the mesh's index at <see cref="Indices"/>[i]
/// (counted from the mesh's first index) draws vertex <see cref="Vertices"/>[i] instead. Shape
/// vertices are stored after the mesh's own, so they are in <see cref="ModelMeshPart.Positions"/> too.
/// </summary>
public sealed record ModelShapeMesh(int MeshIndex, int[] Indices, int[] Vertices);

/// <summary>
/// Reads the full LOD-0 mesh of a Dawntrail (V6) model, without the decimation the thumbnail reader
/// applies. Ported from the add-on's xivpy parser (file.py, mesh.py, headers.py, vertex.py): material
/// and attribute names come from their offset tables, and UVs keep the game's top-left origin.
/// </summary>
public static class ModelMeshReader
{
    public const int MaxVertexBufferBytes = ModelGeometryReader.MaxVertexBufferBytes;
    private const int FileHeaderSize = 68;
    private const int VertexDeclarationSize = 17 * 8;
    private const int StringHeaderSize = 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int TerrainShadowMeshSize = 20;
    private const int SubmeshSize = 16;
    private const int TerrainShadowSubmeshSize = 12;
    private const int ShapeSize = 16;
    private const int ShapeMeshSize = 12;

    private const byte UsagePosition = 0;
    private const byte UsageNormal = 3;
    private const byte UsageUv = 4;

    private const byte TypeSingle2 = 1;
    private const byte TypeSingle3 = 2;
    private const byte TypeSingle4 = 3;
    private const byte TypeNByte4 = 8;
    private const byte TypeHalf2 = 13;
    private const byte TypeHalf4 = 14;

    private readonly record struct Element(byte Stream, byte Offset, byte Type);

    /// <param name="shapes">Also read the shapes and their LOD-0 index replacements.</param>
    public static ModelMesh Read(byte[] bytes, bool shapes = false)
    {
        Require(bytes.Length >= FileHeaderSize + StringHeaderSize, "file header");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (version != ModelInfo.V6)
            throw new NotSupportedException(version == ModelInfo.V5
                ? "Painter export needs a Dawntrail (V6) model."
                : $"Unsupported MDL version 0x{version:X8}.");

        var declarationCount = U16(bytes, 12);
        var vertexOffset = U32(bytes, 16);
        var indexOffset = U32(bytes, 28);
        var vertexBufferSize = U32(bytes, 40);
        var indexBufferSize = U32(bytes, 52);
        if (bytes[64] == 0)
            throw new InvalidDataException("The model has no LOD.");
        if (vertexBufferSize > MaxVertexBufferBytes)
            throw new NotSupportedException("The LOD-0 vertex buffer is larger than the game allows.");
        if (declarationCount > 256)
            throw new InvalidDataException("The model declares too many vertex layouts.");
        Require((long)vertexOffset + vertexBufferSize <= bytes.Length, "vertex buffer");
        Require((long)indexOffset + indexBufferSize <= bytes.Length, "index buffer");

        var positions = new Element?[declarationCount];
        var normals = new Element?[declarationCount];
        var uvs = new Element?[declarationCount];
        for (var d = 0; d < declarationCount; d++)
        {
            var declaration = FileHeaderSize + d * VertexDeclarationSize;
            Require(declaration + VertexDeclarationSize <= bytes.Length, "vertex declaration");
            for (var e = 0; e < 17; e++)
            {
                var at = declaration + e * 8;
                var stream = bytes[at];
                if (stream == 0xFF)
                    break;
                var element = new Element(stream, bytes[at + 1], bytes[at + 2]);
                var usage = bytes[at + 3];
                var usageIndex = bytes[at + 4];
                if (usage == UsagePosition) positions[d] = element;
                else if (usage == UsageNormal) normals[d] = element;
                else if (usage == UsageUv && usageIndex == 0) uvs[d] = element;
            }
        }

        var stringHeader = checked(FileHeaderSize + declarationCount * VertexDeclarationSize);
        Require(stringHeader + StringHeaderSize <= bytes.Length, "string table header");
        var stringSize = U32(bytes, stringHeader + 4);
        var stringBlock = stringHeader + StringHeaderSize;
        Require((long)stringBlock + stringSize <= bytes.Length, "string table");
        var meshHeader = checked(stringBlock + (int)stringSize);
        Require(meshHeader + MeshHeaderSize <= bytes.Length, "mesh header");
        var meshCount = U16(bytes, meshHeader + 4);
        var attributeCount = U16(bytes, meshHeader + 6);
        var submeshCount = U16(bytes, meshHeader + 8);
        var materialCount = U16(bytes, meshHeader + 10);
        var elementIdCount = U16(bytes, meshHeader + 24);
        var terrainShadowMeshCount = bytes[meshHeader + 26];
        var flags2 = bytes[meshHeader + 27];
        var terrainShadowSubmeshCount = U16(bytes, meshHeader + 38);

        var lodTable = checked(meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize);
        var meshTable = checked(lodTable + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0));
        var attributeTable = checked(meshTable + meshCount * MeshSize);
        var submeshTable = checked(attributeTable + attributeCount * 4 + terrainShadowMeshCount * TerrainShadowMeshSize);
        var materialTable = checked(submeshTable + submeshCount * SubmeshSize + terrainShadowSubmeshCount * TerrainShadowSubmeshSize);
        Require(materialTable + materialCount * 4 <= bytes.Length, "material table");

        var attributes = ReadNames(bytes, attributeTable, attributeCount, stringBlock, (int)stringSize);
        var materials = ReadNames(bytes, materialTable, materialCount, stringBlock, (int)stringSize);

        var lod0MeshIndex = U16(bytes, lodTable);
        var lod0MeshCount = U16(bytes, lodTable + 2);
        if (lod0MeshIndex + lod0MeshCount > meshCount)
            throw new InvalidDataException("LOD 0 references meshes outside the mesh table.");
        var indexTotal = (int)(indexBufferSize / 2);

        var parts = new List<ModelMeshPart>();
        var meshStarts = new Dictionary<uint, (int Mesh, uint IndexCount, int VertexCount)>();
        for (var m = lod0MeshIndex; m < lod0MeshIndex + lod0MeshCount; m++)
        {
            var mesh = meshTable + m * MeshSize;
            int vertexCount = U16(bytes, mesh);
            var indexCount = U32(bytes, mesh + 4);
            int materialIndex = U16(bytes, mesh + 8);
            int firstSubmesh = U16(bytes, mesh + 10);
            int meshSubmeshCount = U16(bytes, mesh + 12);
            var startIndex = U32(bytes, mesh + 16);
            if (vertexCount == 0 || indexCount < 3)
                continue;
            if (materialIndex >= materials.Count)
                throw new InvalidDataException("A mesh uses a material outside the material table.");
            if (m >= declarationCount || positions[m] is not { } position)
                throw new NotSupportedException("A mesh has no position stream.");
            meshStarts.TryAdd(startIndex, (m, indexCount, vertexCount));

            var meshPositions = new Vector3[vertexCount];
            var meshNormals = new Vector3[vertexCount];
            var meshUvs = new Vector2[vertexCount];
            DecodeStream(bytes, vertexOffset, mesh, position, vertexCount, (v, at, type) =>
                meshPositions[v] = Finite(ReadVector3(bytes, at, type, "position")));
            if (normals[m] is { } normal)
                DecodeStream(bytes, vertexOffset, mesh, normal, vertexCount, (v, at, type) =>
                    meshNormals[v] = SafeNormalize(ReadVector3(bytes, at, type, "normal")));
            if (uvs[m] is { } uv)
                DecodeStream(bytes, vertexOffset, mesh, uv, vertexCount, (v, at, type) =>
                    meshUvs[v] = FiniteUv(ReadVector2(bytes, at, type)));

            var ranges = new List<(int Index, long Start, long Count, uint Mask)>();
            if (meshSubmeshCount == 0)
                ranges.Add((-1, startIndex, indexCount, 0));
            else
            {
                Require(firstSubmesh + meshSubmeshCount <= submeshCount, "submesh range");
                for (var s = firstSubmesh; s < firstSubmesh + meshSubmeshCount; s++)
                {
                    var submesh = submeshTable + s * SubmeshSize;
                    ranges.Add((s, U32(bytes, submesh), U32(bytes, submesh + 4), U32(bytes, submesh + 8)));
                }
            }

            var submeshes = new List<ModelSubmesh>();
            foreach (var (index, start, count, mask) in ranges)
            {
                if (count < 3)
                    continue;
                if (start + count > indexTotal)
                    throw new InvalidDataException("A submesh reads past the index buffer.");
                var triangleIndices = new int[count - count % 3];
                for (var i = 0; i < triangleIndices.Length; i++)
                {
                    int vertex = U16(bytes, (int)(indexOffset + (start + i) * 2));
                    if (vertex >= vertexCount)
                        throw new InvalidDataException("An index points outside its mesh.");
                    triangleIndices[i] = vertex;
                }
                submeshes.Add(new ModelSubmesh(index, triangleIndices, mask) { MeshIndexStart = (int)(start - startIndex) });
            }
            if (submeshes.Count > 0)
                parts.Add(new ModelMeshPart(m, materialIndex, meshPositions, meshNormals, meshUvs, submeshes));
        }

        if (parts.Count == 0)
            throw new NotSupportedException("LOD 0 has no renderable triangles.");
        return new ModelMesh(parts, materials, attributes)
        {
            Shapes = shapes ? ReadShapes(bytes, meshHeader, materialTable + materialCount * 4, stringBlock, (int)stringSize, meshStarts) : [],
        };
    }

    /// <summary>
    /// The shape table and each shape's LOD-0 meshes, which follow the bone names and bone tables.
    /// A shape mesh names its mesh by the mesh's first index; its values count indices from there.
    /// </summary>
    private static List<ModelShape> ReadShapes(byte[] bytes, int meshHeader, int boneNameTable, int stringBlock, int stringSize,
        Dictionary<uint, (int Mesh, uint IndexCount, int VertexCount)> meshStarts)
    {
        int boneCount = U16(bytes, meshHeader + 12);
        int boneTableCount = U16(bytes, meshHeader + 14);
        int shapeCount = U16(bytes, meshHeader + 16);
        int shapeMeshCount = U16(bytes, meshHeader + 18);
        int shapeValueCount = U16(bytes, meshHeader + 20);
        int boneTableArrayCount = U16(bytes, meshHeader + 44);
        var shapeTable = checked(boneNameTable + boneCount * 4 + boneTableCount * 4 + boneTableArrayCount * 2);
        var shapeMeshTable = checked(shapeTable + shapeCount * ShapeSize);
        var shapeValueTable = checked(shapeMeshTable + shapeMeshCount * ShapeMeshSize);
        Require(shapeValueTable + shapeValueCount * 4 <= bytes.Length, "shape table");

        var shapes = new List<ModelShape>(shapeCount);
        for (var s = 0; s < shapeCount; s++)
        {
            var shape = shapeTable + s * ShapeSize;
            var nameOffset = U32(bytes, shape);
            if (nameOffset >= stringSize)
                throw new InvalidDataException("A shape name points outside the string table.");
            var nameEnd = Array.IndexOf(bytes, (byte)0, stringBlock + (int)nameOffset, stringSize - (int)nameOffset);
            if (nameEnd < 0)
                throw new InvalidDataException("A shape name in the string table is not terminated.");
            var name = Encoding.UTF8.GetString(bytes, stringBlock + (int)nameOffset, nameEnd - stringBlock - (int)nameOffset);
            // LOD 0's range of shape meshes; the other LODs' follow in the same table.
            int first = U16(bytes, shape + 4);
            int count = U16(bytes, shape + 10);
            Require(first + count <= shapeMeshCount, "shape mesh range");
            var meshes = new List<ModelShapeMesh>();
            for (var sm = first; sm < first + count; sm++)
            {
                var shapeMesh = shapeMeshTable + sm * ShapeMeshSize;
                var meshStart = U32(bytes, shapeMesh);
                var valueCount = U32(bytes, shapeMesh + 4);
                var valueOffset = U32(bytes, shapeMesh + 8);
                if (valueOffset + (long)valueCount > shapeValueCount)
                    throw new InvalidDataException("A shape reads past the shape values.");
                if (!meshStarts.TryGetValue(meshStart, out var mesh))
                    continue;
                var indices = new int[valueCount];
                var vertices = new int[valueCount];
                for (var v = 0; v < valueCount; v++)
                {
                    var value = shapeValueTable + (int)(valueOffset + v) * 4;
                    indices[v] = U16(bytes, value);
                    vertices[v] = U16(bytes, value + 2);
                    if (indices[v] >= mesh.IndexCount || vertices[v] >= mesh.VertexCount)
                        throw new InvalidDataException("A shape points outside its mesh.");
                }
                meshes.Add(new ModelShapeMesh(mesh.Mesh, indices, vertices));
            }
            shapes.Add(new ModelShape(name, meshes));
        }
        return shapes;
    }

    private static void DecodeStream(byte[] bytes, uint vertexOffset, int mesh, Element element, int vertexCount,
        Action<int, int, byte> read)
    {
        var size = element.Type switch
        {
            TypeSingle2 => 8,
            TypeSingle3 => 12,
            TypeSingle4 => 16,
            TypeNByte4 => 4,
            TypeHalf2 => 4,
            TypeHalf4 => 8,
            _ => throw new NotSupportedException($"Unsupported vertex element type {element.Type}."),
        };
        if (element.Stream > 2)
            throw new InvalidDataException("A vertex element names an invalid stream.");
        var streamOffset = U32(bytes, mesh + 20 + element.Stream * 4);
        int stride = bytes[mesh + 32 + element.Stream];
        if (stride < size || element.Offset + size > stride)
            throw new InvalidDataException("A vertex stream stride is inconsistent.");
        var first = checked((long)vertexOffset + streamOffset);
        var end = checked(first + (long)(vertexCount - 1) * stride + element.Offset + size);
        if (first < 0 || end > bytes.Length)
            throw new InvalidDataException("The vertex buffer is truncated.");
        for (var v = 0; v < vertexCount; v++)
            read(v, (int)(first + (long)v * stride + element.Offset), element.Type);
    }

    private static Vector3 ReadVector3(byte[] bytes, int at, byte type, string what) => type switch
    {
        TypeSingle3 or TypeSingle4 => new Vector3(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8)),
        TypeHalf4 => new Vector3(F16(bytes, at), F16(bytes, at + 2), F16(bytes, at + 4)),
        TypeNByte4 => new Vector3(bytes[at] / 255f * 2f - 1f, bytes[at + 1] / 255f * 2f - 1f, bytes[at + 2] / 255f * 2f - 1f),
        _ => throw new NotSupportedException($"Unsupported {what} layout (type {type})."),
    };

    private static Vector2 ReadVector2(byte[] bytes, int at, byte type) => type switch
    {
        TypeSingle2 or TypeSingle3 or TypeSingle4 => new Vector2(F32(bytes, at), F32(bytes, at + 4)),
        TypeHalf2 or TypeHalf4 => new Vector2(F16(bytes, at), F16(bytes, at + 2)),
        _ => throw new NotSupportedException($"Unsupported UV layout (type {type})."),
    };

    private static IReadOnlyList<string> ReadNames(byte[] bytes, int table, int count, int block, int blockSize)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = U32(bytes, table + i * 4);
            if (offset >= blockSize)
                throw new InvalidDataException("A name points outside the string table.");
            var start = block + (int)offset;
            var end = Array.IndexOf(bytes, (byte)0, start, blockSize - (int)offset);
            if (end < 0)
                throw new InvalidDataException("A name in the string table is not terminated.");
            names[i] = Encoding.UTF8.GetString(bytes, start, end - start);
        }
        return names;
    }

    private static Vector3 Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) ? value : Vector3.Zero;

    private static Vector2 FiniteUv(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) ? value : Vector2.Zero;

    private static Vector3 SafeNormalize(Vector3 value)
    {
        value = Finite(value);
        var length = value.Length();
        return length > 1e-6f ? value / length : Vector3.UnitY;
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

    private static float F32(byte[] bytes, int at) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at, 4));
    private static float F16(byte[] bytes, int at) => (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2)));

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The {what} is outside the file.");
    }
}
