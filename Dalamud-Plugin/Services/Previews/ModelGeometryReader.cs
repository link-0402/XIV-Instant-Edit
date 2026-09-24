using System.Buffers.Binary;
using System.Numerics;

namespace InstantEdit.Services.Previews;

/// <summary> LOD-0 positions and triangles of a model, with the mesh ranges over the index list. </summary>
public sealed record ModelGeometry(Vector3[] Positions, int[] Indices, (int Start, int Count, int MaterialIndex)[] Meshes, Vector3 Min, Vector3 Max)
{
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Reads LOD-0 geometry from a Dawntrail (V6) model: vertex declarations, LOD buffer offsets,
/// mesh table and the position stream. Ported from the add-on's Python reader; every read is
/// bounds-checked and anything unexpected throws instead of producing garbage.
/// </summary>
public static class ModelGeometryReader
{
    public const int MaxTriangles = 60_000;
    public const int HardTriangleLimit = 200_000;
    public const int MaxVertexBufferBytes = 8 << 20;
    private const int FileHeaderSize = 68;
    private const int VertexDeclarationSize = 17 * 8;
    private const int StringHeaderSize = 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const byte UsagePosition = 0;
    private const byte TypeSingle3 = 2;
    private const byte TypeSingle4 = 3;
    private const byte TypeHalf4 = 14;

    private readonly record struct Element(byte Stream, byte Offset, byte Type);

    public static ModelGeometry Read(byte[] bytes)
    {
        if (bytes.Length < FileHeaderSize + StringHeaderSize)
            throw new InvalidDataException("The file is too short to be a model.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (version != ModelInfo.V6)
            throw new NotSupportedException(version == ModelInfo.V5
                ? "Thumbnails need a Dawntrail (V6) model."
                : $"Unsupported MDL version 0x{version:X8}.");

        var declarationCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        var vertexOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        var indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
        var vertexBufferSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));
        var lodCount = bytes[64];
        if (lodCount == 0)
            throw new InvalidDataException("The model has no LOD.");
        if (vertexBufferSize > MaxVertexBufferBytes)
            throw new NotSupportedException("The LOD-0 vertex buffer is larger than the game allows.");
        if (declarationCount > 256)
            throw new InvalidDataException("The model declares too many vertex layouts.");

        var positions = new Element?[declarationCount];
        for (var d = 0; d < declarationCount; d++)
        {
            var declaration = FileHeaderSize + d * VertexDeclarationSize;
            Require(declaration + VertexDeclarationSize <= bytes.Length, "vertex declaration");
            for (var e = 0; e < 17; e++)
            {
                var element = declaration + e * 8;
                var stream = bytes[element];
                if (stream == 0xFF)
                    break;
                if (bytes[element + 3] == UsagePosition)
                    positions[d] = new Element(stream, bytes[element + 1], bytes[element + 2]);
            }
        }

        var stringHeader = checked(FileHeaderSize + declarationCount * VertexDeclarationSize);
        Require(stringHeader + StringHeaderSize <= bytes.Length, "string table header");
        var stringSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringHeader + 4, 4));
        var meshHeader = checked(stringHeader + StringHeaderSize + (int)Math.Min(int.MaxValue, stringSize));
        Require(meshHeader + MeshHeaderSize <= bytes.Length, "mesh header");
        var meshCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 4, 2));
        var elementIdCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 24, 2));
        var flags2 = bytes[meshHeader + 27];
        var lodTable = checked(meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize);
        Require(lodTable + 3 * LodSize <= bytes.Length, "LOD table");
        var lod0MeshIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lodTable, 2));
        var lod0MeshCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lodTable + 2, 2));
        var meshTable = checked(lodTable + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0));
        Require(meshTable + meshCount * MeshSize <= bytes.Length, "mesh table");
        if (lod0MeshIndex + lod0MeshCount > meshCount)
            throw new InvalidDataException("LOD 0 references meshes outside the mesh table.");

        long totalTriangles = 0;
        for (var m = lod0MeshIndex; m < lod0MeshIndex + lod0MeshCount; m++)
            totalTriangles += BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(meshTable + m * MeshSize + 4, 4)) / 3;
        if (totalTriangles > HardTriangleLimit)
            throw new NotSupportedException($"The model has {totalTriangles:N0} triangles; thumbnails stop at {HardTriangleLimit:N0}.");
        var step = totalTriangles > MaxTriangles ? (int)Math.Ceiling(totalTriangles / (double)MaxTriangles) : 1;

        var allPositions = new List<Vector3>();
        var allIndices = new List<int>();
        var meshes = new List<(int Start, int Count, int MaterialIndex)>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var m = lod0MeshIndex; m < lod0MeshIndex + lod0MeshCount; m++)
        {
            var mesh = meshTable + m * MeshSize;
            int vertexCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(mesh, 2));
            var indexCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mesh + 4, 4));
            int materialIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(mesh + 8, 2));
            var startIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mesh + 16, 4));
            if (vertexCount == 0 || indexCount < 3)
                continue;
            if (m >= positions.Length || positions[m] is not { } element)
                throw new NotSupportedException("A mesh has no position stream.");
            var streamOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mesh + 20 + element.Stream * 4, 4));
            int stride = bytes[mesh + 32 + element.Stream];
            var elementSize = element.Type switch
            {
                TypeSingle3 => 12,
                TypeSingle4 => 16,
                TypeHalf4 => 8,
                _ => throw new NotSupportedException($"Unsupported position layout (type {element.Type})."),
            };
            if (stride < elementSize || element.Offset + elementSize > stride)
                throw new InvalidDataException("The position stream stride is inconsistent.");

            var vertexBase = allPositions.Count;
            var firstVertex = checked((long)vertexOffset + streamOffset);
            var lastVertexEnd = checked(firstVertex + (long)(vertexCount - 1) * stride + element.Offset + elementSize);
            if (firstVertex < 0 || lastVertexEnd > bytes.Length)
                throw new InvalidDataException("The vertex buffer is truncated.");
            for (var v = 0; v < vertexCount; v++)
            {
                var at = (int)(firstVertex + (long)v * stride + element.Offset);
                var position = element.Type switch
                {
                    TypeHalf4 => new Vector3(
                        (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at, 2))),
                        (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 2, 2))),
                        (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 4, 2)))),
                    _ => new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at, 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 4, 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8, 4))),
                };
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                    position = Vector3.Zero;
                allPositions.Add(position);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            var firstIndex = checked((long)indexOffset + startIndex * 2L);
            var indexEnd = checked(firstIndex + indexCount * 2L);
            if (firstIndex < 0 || indexEnd > bytes.Length)
                throw new InvalidDataException("The index buffer is truncated.");
            var meshStart = allIndices.Count;
            var triangles = (int)(indexCount / 3);
            for (var t = 0; t < triangles; t += step)
            {
                var at = (int)(firstIndex + t * 6L);
                int a = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
                int b = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 2, 2));
                int c = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 4, 2));
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    throw new InvalidDataException("An index points outside its mesh.");
                allIndices.Add(vertexBase + a);
                allIndices.Add(vertexBase + b);
                allIndices.Add(vertexBase + c);
            }
            meshes.Add((meshStart, allIndices.Count - meshStart, materialIndex));
        }

        if (allIndices.Count == 0)
            throw new NotSupportedException("LOD 0 has no renderable triangles.");
        return new ModelGeometry(allPositions.ToArray(), allIndices.ToArray(), meshes.ToArray(), min, max);
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The {what} is outside the file.");
    }
}
