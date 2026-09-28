using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.NeckSeam;

/// <summary> One LOD-0 mesh with the vertex data the neck seam analysis reads. </summary>
internal sealed class SkinMesh
{
    public required int MeshIndex { get; init; }
    public required string Material { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    /// <summary> The stored binormal (the "tangent" usage) and its handedness sign (+1 or -1). </summary>
    public required Vector3[] Binormals { get; init; }
    public required float[] BinormalSigns { get; init; }
    /// <summary> UV set 1 (uv0.xy) and UV set 2 (uv0.zw); the second is zero when the mesh has none. </summary>
    public required Vector2[] Uv1 { get; init; }
    public required Vector2[] Uv2 { get; init; }
    public required bool HasUv2 { get; init; }
    /// <summary> Vertex colour 0 in 0..1, white when the mesh stores none. </summary>
    public required Vector4[] Colors { get; init; }
    /// <summary> <see cref="Influences"/> bone-table indices and weights (0..1) per vertex. </summary>
    public required byte[] BlendIndices { get; init; }
    public required float[] BlendWeights { get; init; }
    public required int Influences { get; init; }
    /// <summary> Bone names of the mesh's bone table, which the blend indices index. </summary>
    public required string[] Bones { get; init; }
    /// <summary> Every triangle of the mesh (all submeshes), three vertex indices each. </summary>
    public required int[] Triangles { get; init; }
    public int VertexCount => Positions.Length;
}

/// <summary> One neck morph (connection vertex) entry of a face model. </summary>
internal readonly record struct NeckMorph(Vector3 Position, Vector3 Normal, byte BoneA, byte BoneB);

/// <summary>
/// The parts of a Dawntrail (V6) model the neck seam work needs: LOD-0 skin meshes with positions,
/// normals, binormals, both UV sets, vertex colour and weights, and the neck morph table. Layout
/// follows the add-on's xivpy reader (file.py, headers.py, lod.py, mesh.py).
/// </summary>
internal sealed class SkinModel
{
    public const uint V6 = 0x01000006;
    public const uint NeckMorphFlag = 0x00006699;
    private const int FileHeaderSize = 68;
    private const int DeclarationSize = 17 * 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int NeckMorphSize = 32;

    private const byte UsagePosition = 0, UsageWeights = 1, UsageIndices = 2, UsageNormal = 3, UsageUv = 4, UsageBinormal = 6, UsageColor = 7;
    private const byte TypeSingle2 = 1, TypeSingle3 = 2, TypeSingle4 = 3, TypeUByte4 = 5, TypeNByte4 = 8, TypeHalf2 = 13, TypeHalf4 = 14, TypeUShort4 = 17;

    private readonly byte[] _bytes;
    private readonly int _meshHeader;
    private readonly int _lodTable;
    private readonly int _neckMorphTable;
    private readonly int _lodCount;

    public IReadOnlyList<SkinMesh> Meshes { get; }
    public IReadOnlyList<string> Materials { get; }
    public IReadOnlyList<string> Bones { get; }
    /// <summary> Bone tables as indices into <see cref="Bones"/>. </summary>
    public IReadOnlyList<ushort[]> BoneTables { get; }
    public IReadOnlyList<NeckMorph> NeckMorphs { get; }

    private SkinModel(byte[] bytes, int meshHeader, int lodTable, int neckMorphTable, int lodCount, IReadOnlyList<SkinMesh> meshes,
        IReadOnlyList<string> materials, IReadOnlyList<string> bones, IReadOnlyList<ushort[]> boneTables, IReadOnlyList<NeckMorph> neckMorphs)
    {
        _bytes = bytes;
        _meshHeader = meshHeader;
        _lodTable = lodTable;
        _neckMorphTable = neckMorphTable;
        _lodCount = lodCount;
        Meshes = meshes;
        Materials = materials;
        Bones = bones;
        BoneTables = boneTables;
        NeckMorphs = neckMorphs;
    }

    private readonly record struct Element(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex);

    public static SkinModel Read(byte[] bytes)
    {
        Require(bytes.Length >= FileHeaderSize + 8, "file header");
        if (U32(bytes, 0) != V6)
            throw new NotSupportedException("Only Dawntrail (V6) models are supported.");
        var stackSize = U32(bytes, 4);
        var runtimeSize = U32(bytes, 8);
        int declarationCount = U16(bytes, 12);
        var vertexOffset = U32(bytes, 16);
        var indexOffset = U32(bytes, 28);
        var indexBufferSize = U32(bytes, 52);
        int lodCount = bytes[64];
        if (lodCount is < 1 or > 3)
            throw new InvalidDataException("The model has no valid LOD count.");
        var dataOffset = checked(FileHeaderSize + (long)stackSize + runtimeSize);
        Require(dataOffset <= bytes.Length && indexOffset + (long)indexBufferSize <= bytes.Length, "geometry");

        var declarations = new List<Element>[declarationCount];
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
                declarations[d].Add(new Element(bytes[element], bytes[element + 1], bytes[element + 2], bytes[element + 3], bytes[element + 4]));
            }
        }

        var stringHeader = FileHeaderSize + declarationCount * DeclarationSize;
        var stringSize = (int)U32(bytes, stringHeader + 4);
        var stringBlock = stringHeader + 8;
        Require(stringBlock + (long)stringSize <= bytes.Length, "string table");
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

        var cursor = meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize;
        var lodTable = cursor;
        cursor += 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0);
        var meshTable = cursor;
        cursor += meshCount * MeshSize;
        cursor += attributeCount * 4;
        cursor += terrainShadowMeshCount * 20;
        cursor += submeshCount * 16;
        cursor += terrainShadowSubmeshCount * 12;
        var materialTable = cursor;
        cursor += materialCount * 4;
        var boneNameTable = cursor;
        cursor += boneCount * 4;
        var boneTables = cursor;
        cursor += boneTableCount * 4 + boneTableArrayCount * 2;
        cursor += shapeCount * 16 + shapeMeshCount * 12 + shapeValueCount * 4;
        Require(cursor + 4 <= bytes.Length, "submesh bone map");
        cursor = checked(cursor + 4 + (int)U32(bytes, cursor));
        var neckMorphTable = cursor;
        cursor += neckMorphCount * NeckMorphSize;
        cursor = checked(cursor + (int)faceDataCount * 16);
        Require(cursor < bytes.Length, "padding");
        cursor += 1 + bytes[cursor];
        cursor += (4 + boneCount) * 32;
        if (cursor > dataOffset)
            throw new InvalidDataException("The model's tables run into its geometry.");

        var materials = ReadNames(bytes, materialTable, materialCount, stringBlock, stringSize);
        var bones = ReadNames(bytes, boneNameTable, boneCount, stringBlock, stringSize);
        var tables = new ushort[boneTableCount][];
        for (var t = 0; t < boneTableCount; t++)
        {
            var header = boneTables + t * 4;
            int offset = U16(bytes, header);
            int count = U16(bytes, header + 2);
            var start = header + offset * 4;
            Require(start + count * 2 <= bytes.Length, "bone table");
            tables[t] = new ushort[count];
            for (var i = 0; i < count; i++)
            {
                tables[t][i] = U16(bytes, start + i * 2);
                if (tables[t][i] >= boneCount)
                    throw new InvalidDataException("A bone table names a bone outside the bone list.");
            }
        }

        var morphs = new NeckMorph[neckMorphCount];
        for (var i = 0; i < neckMorphCount; i++)
        {
            var at = neckMorphTable + i * NeckMorphSize;
            morphs[i] = new NeckMorph(new Vector3(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8)),
                new Vector3(F32(bytes, at + 16), F32(bytes, at + 20), F32(bytes, at + 24)), bytes[at + 28], bytes[at + 29]);
        }

        int lod0Mesh = U16(bytes, lodTable);
        int lod0Count = U16(bytes, lodTable + 2);
        if (lod0Mesh + lod0Count > meshCount)
            throw new InvalidDataException("LOD 0 references meshes outside the mesh table.");
        var indexTotal = indexBufferSize / 2;
        var meshes = new List<SkinMesh>();
        for (var m = lod0Mesh; m < lod0Mesh + lod0Count; m++)
        {
            var mesh = meshTable + m * MeshSize;
            int vertexCount = U16(bytes, mesh);
            var indexCount = U32(bytes, mesh + 4);
            int materialIndex = U16(bytes, mesh + 8);
            int boneTableIndex = U16(bytes, mesh + 14);
            var startIndex = U32(bytes, mesh + 16);
            if (vertexCount == 0 || indexCount < 3 || m >= declarationCount)
                continue;
            if (materialIndex >= materials.Count)
                throw new InvalidDataException("A mesh uses a material outside the material table.");
            if (startIndex + (long)indexCount > indexTotal)
                throw new InvalidDataException("A mesh reads past the index buffer.");
            meshes.Add(ReadMesh(bytes, vertexOffset, indexOffset, mesh, m, vertexCount, indexCount, startIndex, declarations[m],
                materials[materialIndex], boneTableIndex < tables.Length ? tables[boneTableIndex].Select(b => bones[b]).ToArray() : []));
        }

        return new SkinModel(bytes, meshHeader, lodTable, neckMorphTable, lodCount, meshes, materials, bones, tables, morphs);
    }

    private static SkinMesh ReadMesh(byte[] bytes, uint vertexOffset, uint indexOffset, int mesh, int meshIndex, int count, uint indexCount,
        uint startIndex, List<Element> elements, string material, string[] bones)
    {
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var binormals = new Vector3[count];
        var signs = new float[count];
        var uv1 = new Vector2[count];
        var uv2 = new Vector2[count];
        var colors = new Vector4[count];
        Array.Fill(colors, Vector4.One);
        Array.Fill(signs, 1f);
        var influences = 4;
        foreach (var element in elements)
            if (element.Usage is UsageWeights or UsageIndices && element.Type == TypeUShort4)
                influences = 8;
        var indices = new byte[count * influences];
        var weights = new float[count * influences];
        bool hasPosition = false, hasUv2 = false;

        foreach (var element in elements)
        {
            switch (element.Usage)
            {
                case UsagePosition:
                    hasPosition = true;
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) => positions[v] = ReadVector3(bytes, at, element.Type));
                    break;
                case UsageNormal:
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) => normals[v] = SafeNormalize(ReadVector3(bytes, at, element.Type)));
                    break;
                case UsageBinormal when element.Type == TypeNByte4:
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) =>
                    {
                        binormals[v] = SafeNormalize(new Vector3(bytes[at] / 127.5f - 1f, bytes[at + 1] / 127.5f - 1f, bytes[at + 2] / 127.5f - 1f));
                        signs[v] = bytes[at + 3] >= 128 ? 1f : -1f;
                    });
                    break;
                case UsageUv when element.UsageIndex == 0:
                    var four = element.Type is TypeSingle4 or TypeHalf4;
                    hasUv2 = four;
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) =>
                    {
                        var uv = ReadVector4(bytes, at, element.Type);
                        uv1[v] = new Vector2(uv.X, uv.Y);
                        if (four)
                            uv2[v] = new Vector2(uv.Z, uv.W);
                    });
                    break;
                case UsageColor when element.UsageIndex == 0 && element.Type == TypeNByte4:
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) =>
                        colors[v] = new Vector4(bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]) / 255f);
                    break;
                case UsageWeights:
                case UsageIndices:
                    var width = element.Type == TypeUShort4 ? 8 : element.Type is TypeUByte4 or TypeNByte4 ? 4 : 0;
                    if (width == 0)
                        throw new NotSupportedException($"Unsupported skin weight layout (type {element.Type}).");
                    var isWeights = element.Usage == UsageWeights;
                    Decode(bytes, vertexOffset, mesh, element, count, (v, at) =>
                    {
                        for (var i = 0; i < width; i++)
                        {
                            if (isWeights) weights[v * influences + i] = bytes[at + i] / 255f;
                            else indices[v * influences + i] = bytes[at + i];
                        }
                    });
                    break;
            }
        }
        if (!hasPosition)
            throw new NotSupportedException("A mesh has no position stream.");

        var triangles = new int[indexCount - indexCount % 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            int vertex = U16(bytes, (int)(indexOffset + (startIndex + i) * 2));
            if (vertex >= count)
                throw new InvalidDataException("An index points outside its mesh.");
            triangles[i] = vertex;
        }

        return new SkinMesh
        {
            MeshIndex = meshIndex, Material = material, Positions = positions, Normals = normals, Binormals = binormals, BinormalSigns = signs,
            Uv1 = uv1, Uv2 = uv2, HasUv2 = hasUv2, Colors = colors, BlendIndices = indices, BlendWeights = weights, Influences = influences,
            Bones = bones, Triangles = triangles,
        };
    }

    /// <summary>
    /// A copy of this model with <paramref name="morphs"/> as its neck morph table. Only a model
    /// without one can take a table: the entries are inserted where the table belongs, LOD 0 is
    /// pointed at them (later LODs start after them with none, like vanilla faces), and every
    /// absolute offset past the insertion moves by the inserted size. Entries are 32 bytes, so the
    /// 8-byte alignment of the geometry is kept.
    /// </summary>
    public byte[] WithNeckMorphs(IReadOnlyList<NeckMorph> morphs)
    {
        if (NeckMorphs.Count != 0)
            throw new InvalidOperationException("The model already has a neck morph table.");
        if (morphs.Count is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(morphs), "A neck morph table holds 1 to 255 entries.");
        var inserted = morphs.Count * NeckMorphSize;
        var result = new byte[_bytes.Length + inserted];
        _bytes.AsSpan(0, _neckMorphTable).CopyTo(result);
        _bytes.AsSpan(_neckMorphTable).CopyTo(result.AsSpan(_neckMorphTable + inserted));
        for (var i = 0; i < morphs.Count; i++)
        {
            var at = _neckMorphTable + i * NeckMorphSize;
            var morph = morphs[i];
            WriteF32(result, at, morph.Position.X);
            WriteF32(result, at + 4, morph.Position.Y);
            WriteF32(result, at + 8, morph.Position.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at + 12), NeckMorphFlag);
            WriteF32(result, at + 16, morph.Normal.X);
            WriteF32(result, at + 20, morph.Normal.Y);
            WriteF32(result, at + 24, morph.Normal.Z);
            result[at + 28] = morph.BoneA;
            result[at + 29] = morph.BoneB;
        }

        // Runtime size, then the LODs' absolute vertex and index buffer offsets in the file header.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked(U32(result, 8) + (uint)inserted));
        for (var lod = 0; lod < _lodCount; lod++)
        {
            Shift(result, 16 + lod * 4, inserted);
            Shift(result, 28 + lod * 4, inserted);
        }
        // LOD layout (lod.py): edge geometry offset at 32, neck morph offset and count at 40 and 41,
        // vertex and index data offsets at 52 and 56.
        result[_meshHeader + 43] = (byte)morphs.Count;
        for (var lod = 0; lod < _lodCount; lod++)
        {
            var at = _lodTable + lod * LodSize;
            result[at + 40] = (byte)(lod == 0 ? 0 : morphs.Count);
            result[at + 41] = (byte)(lod == 0 ? morphs.Count : 0);
            Shift(result, at + 32, inserted);
            Shift(result, at + 52, inserted);
            Shift(result, at + 56, inserted);
        }
        return result;
    }

    private static void Shift(byte[] bytes, int at, int delta)
    {
        var value = U32(bytes, at);
        if (value != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), checked(value + (uint)delta));
    }

    private static void Decode(byte[] bytes, uint vertexOffset, int mesh, Element element, int count, Action<int, int> read)
    {
        var size = element.Type switch
        {
            TypeSingle2 => 8, TypeSingle3 => 12, TypeSingle4 => 16, TypeUByte4 or TypeNByte4 => 4,
            TypeHalf2 => 4, TypeHalf4 => 8, TypeUShort4 => 8,
            _ => throw new NotSupportedException($"Unsupported vertex element type {element.Type}."),
        };
        if (element.Stream > 2)
            throw new InvalidDataException("A vertex element names an invalid stream.");
        var streamOffset = U32(bytes, mesh + 20 + element.Stream * 4);
        int stride = bytes[mesh + 32 + element.Stream];
        if (stride < size || element.Offset + size > stride)
            throw new InvalidDataException("A vertex stream stride is inconsistent.");
        var first = checked((long)vertexOffset + streamOffset);
        var end = checked(first + (long)(count - 1) * stride + element.Offset + size);
        if (end > bytes.Length)
            throw new InvalidDataException("The vertex buffer is truncated.");
        for (var v = 0; v < count; v++)
            read(v, (int)(first + (long)v * stride + element.Offset));
    }

    private static Vector3 ReadVector3(byte[] bytes, int at, byte type) => type switch
    {
        TypeSingle3 or TypeSingle4 => new Vector3(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8)),
        TypeHalf4 => new Vector3(F16(bytes, at), F16(bytes, at + 2), F16(bytes, at + 4)),
        TypeNByte4 => new Vector3(bytes[at] / 127.5f - 1f, bytes[at + 1] / 127.5f - 1f, bytes[at + 2] / 127.5f - 1f),
        _ => throw new NotSupportedException($"Unsupported vector layout (type {type})."),
    };

    private static Vector4 ReadVector4(byte[] bytes, int at, byte type) => type switch
    {
        TypeSingle2 => new Vector4(F32(bytes, at), F32(bytes, at + 4), 0, 0),
        TypeSingle3 => new Vector4(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8), 0),
        TypeSingle4 => new Vector4(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8), F32(bytes, at + 12)),
        TypeHalf2 => new Vector4(F16(bytes, at), F16(bytes, at + 2), 0, 0),
        TypeHalf4 => new Vector4(F16(bytes, at), F16(bytes, at + 2), F16(bytes, at + 4), F16(bytes, at + 6)),
        _ => throw new NotSupportedException($"Unsupported UV layout (type {type})."),
    };

    private static IReadOnlyList<string> ReadNames(byte[] bytes, int table, int count, int block, int blockSize)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = (int)U32(bytes, table + i * 4);
            if (offset >= blockSize)
                throw new InvalidDataException("A name points outside the string table.");
            var start = block + offset;
            var end = Array.IndexOf(bytes, (byte)0, start, blockSize - offset);
            if (end < 0)
                throw new InvalidDataException("A name in the string table is not terminated.");
            names[i] = Encoding.UTF8.GetString(bytes, start, end - start);
        }
        return names;
    }

    private static Vector3 SafeNormalize(Vector3 value)
    {
        var length = value.Length();
        return float.IsFinite(length) && length > 1e-6f ? value / length : Vector3.UnitY;
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
    private static void WriteF32(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at, 4), value);

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The {what} is outside the file.");
    }
}
