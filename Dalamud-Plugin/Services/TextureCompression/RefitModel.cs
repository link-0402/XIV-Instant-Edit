using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// The first UV set of every mesh in every LOD of a Dawntrail (V6) model, read from the vertex
/// buffers, and a copy whose chosen meshes have their UVs moved into a crop window. Only those UV
/// bytes change; the rest of the file stays byte for byte. Shape keys need nothing of their own:
/// their replacement vertices sit in the same buffers and are moved with the rest. Layout as in
/// <see cref="NeckSeam.SkinModel"/>, which reads LOD 0 only. Dalamud-free.
/// </summary>
internal sealed class RefitModel
{
    private const uint V5 = 0x01000005, V6 = 0x01000006;
    private const int FileHeaderSize = 68;
    private const int DeclarationSize = 17 * 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int MeshSize = 36;
    private const byte UsageUv = 4;
    private const byte TypeSingle2 = 1, TypeSingle3 = 2, TypeSingle4 = 3, TypeHalf2 = 13, TypeHalf4 = 14;

    /// <summary> One mesh's first UV set: the element's type and where each vertex keeps it. </summary>
    /// <param name="Index">The mesh's index in the model's mesh table.</param>
    /// <param name="Uvs">Each vertex's UV, xy then zw; zw is zero when the element has two components.</param>
    public sealed record Mesh(int Index, int Lod, int MaterialIndex, byte Type, long First, int Stride, Vector4[] Uvs)
    {
        public bool FourComponents => Type is TypeSingle4 or TypeHalf4;
        public bool Half => Type is TypeHalf2 or TypeHalf4;
    }

    private readonly byte[] _bytes;

    private RefitModel(byte[] bytes, IReadOnlyList<string> materials, IReadOnlyList<Mesh> meshes, IReadOnlyList<int> withoutUvs)
    {
        _bytes = bytes;
        Materials = materials;
        Meshes = meshes;
        MeshesWithoutUvs = withoutUvs;
    }

    public IReadOnlyList<string> Materials { get; }

    /// <summary> Meshes with a first UV set, once per LOD that draws them. </summary>
    public IReadOnlyList<Mesh> Meshes { get; }

    /// <summary> Material indices of drawn meshes that have no first UV set, which can't be moved. </summary>
    public IReadOnlyList<int> MeshesWithoutUvs { get; }

    /// <summary>
    /// The model's material names, from its headers only (no geometry), so every model of a mod can
    /// be looked at cheaply. Null when the file isn't a V5 or V6 model.
    /// </summary>
    public static IReadOnlyList<string>? ReadMaterialNames(Stream model)
    {
        Span<byte> head = stackalloc byte[FileHeaderSize];
        model.Position = 0;
        if (model.ReadAtLeast(head, head.Length, false) < head.Length || BinaryPrimitives.ReadUInt32LittleEndian(head) is not (V5 or V6))
            return null;
        var headers = checked(FileHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + BinaryPrimitives.ReadUInt32LittleEndian(head[8..]));
        if (headers > model.Length || headers > 64L * 1024 * 1024)
            throw new InvalidDataException("The model's headers are outside the file.");
        var bytes = new byte[headers];
        model.Position = 0;
        model.ReadExactly(bytes);
        return Parse(bytes, geometry: false).Materials;
    }

    /// <summary> The model with every mesh's UVs. Throws for anything but a V6 model drawn from its main meshes. </summary>
    public static RefitModel Read(byte[] bytes) => Parse(bytes, geometry: true);

    private static RefitModel Parse(byte[] bytes, bool geometry)
    {
        Require(bytes.Length >= FileHeaderSize, "file header");
        // Older (V5) models lay out their headers the same way, enough to read their material names.
        if (U32(bytes, 0) != V6 && !(U32(bytes, 0) == V5 && !geometry))
            throw new NotSupportedException("Only Dawntrail (V6) models can be refit.");
        var stackSize = U32(bytes, 4);
        var runtimeSize = U32(bytes, 8);
        int declarationCount = U16(bytes, 12);
        int lodCount = bytes[64];
        if (lodCount is < 1 or > 3)
            throw new InvalidDataException("The model has no valid LOD count.");
        var dataOffset = checked(FileHeaderSize + (long)stackSize + runtimeSize);
        Require(dataOffset <= bytes.Length, "header");

        var uvElements = new (byte Stream, byte Offset, byte Type)?[declarationCount];
        for (var d = 0; d < declarationCount; d++)
        {
            var at = FileHeaderSize + d * DeclarationSize;
            Require(at + DeclarationSize <= bytes.Length, "vertex declaration");
            for (var e = 0; e < 17; e++)
            {
                var element = at + e * 8;
                if (bytes[element] == 0xFF)
                    break;
                if (bytes[element + 3] == UsageUv && bytes[element + 4] == 0)
                    uvElements[d] = (bytes[element], bytes[element + 1], bytes[element + 2]);
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
        int materialCount = U16(bytes, meshHeader + 10);
        int elementIdCount = U16(bytes, meshHeader + 24);
        int terrainShadowMeshCount = bytes[meshHeader + 26];
        var flags2 = bytes[meshHeader + 27];
        int submeshCount = U16(bytes, meshHeader + 8);
        int terrainShadowSubmeshCount = U16(bytes, meshHeader + 38);
        if ((flags2 & 0x10) != 0)
            throw new NotSupportedException("The model has extra LODs.");

        var lodTable = meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize;
        var meshTable = lodTable + 3 * LodSize;
        var materialTable = meshTable + meshCount * MeshSize + attributeCount * 4 + terrainShadowMeshCount * 20 + submeshCount * 16 + terrainShadowSubmeshCount * 12;
        Require(materialTable + materialCount * 4L <= bytes.Length, "material table");
        var materials = new string[materialCount];
        for (var i = 0; i < materialCount; i++)
        {
            var offset = (int)U32(bytes, materialTable + i * 4);
            if (offset >= stringSize)
                throw new InvalidDataException("A material name points outside the string table.");
            var end = Array.IndexOf(bytes, (byte)0, stringBlock + offset, stringSize - offset);
            if (end < 0)
                throw new InvalidDataException("A material name is not terminated.");
            materials[i] = Encoding.UTF8.GetString(bytes, stringBlock + offset, end - stringBlock - offset);
        }
        if (!geometry)
            return new RefitModel(bytes, materials, [], []);

        var meshes = new List<Mesh>();
        var withoutUvs = new List<int>();
        for (var lod = 0; lod < lodCount; lod++)
        {
            var at = lodTable + lod * LodSize;
            int first = U16(bytes, at), count = U16(bytes, at + 2);
            // Water, shadow, terrain shadow and fog meshes: characters have none, and they aren't moved.
            if (U16(bytes, at + 14) != 0 || U16(bytes, at + 18) != 0 || U16(bytes, at + 22) != 0 || U16(bytes, at + 26) != 0)
                throw new NotSupportedException("The model has meshes other than its main ones.");
            if (first + count > meshCount)
                throw new InvalidDataException("A LOD names meshes outside the mesh table.");
            var vertexData = U32(bytes, at + 52);
            for (var m = first; m < first + count; m++)
            {
                var mesh = meshTable + m * MeshSize;
                int vertexCount = U16(bytes, mesh);
                int materialIndex = U16(bytes, mesh + 8);
                if (vertexCount == 0)
                    continue;
                if (materialIndex >= materialCount)
                    throw new InvalidDataException("A mesh uses a material outside the material table.");
                if (m >= declarationCount || uvElements[m] is not { } uv)
                {
                    withoutUvs.Add(materialIndex);
                    continue;
                }
                var size = uv.Type switch
                {
                    TypeSingle2 => 8, TypeSingle3 => 12, TypeSingle4 => 16, TypeHalf2 => 4, TypeHalf4 => 8,
                    _ => throw new NotSupportedException($"Unsupported UV layout (type {uv.Type})."),
                };
                if (uv.Stream > 2)
                    throw new InvalidDataException("A vertex element names an invalid stream.");
                int stride = bytes[mesh + 32 + uv.Stream];
                if (stride < size || uv.Offset + size > stride)
                    throw new InvalidDataException("A vertex stream stride is inconsistent.");
                var start = checked((long)vertexData + U32(bytes, mesh + 20 + uv.Stream * 4) + uv.Offset);
                Require(start + (long)(vertexCount - 1) * stride + size <= bytes.Length, "vertex buffer");
                var uvs = new Vector4[vertexCount];
                for (var v = 0; v < vertexCount; v++)
                    uvs[v] = ReadUv(bytes, (int)(start + (long)v * stride), uv.Type);
                meshes.Add(new Mesh(m, lod, materialIndex, uv.Type, start, stride, uvs));
            }
        }
        return new RefitModel(bytes, materials, meshes, withoutUvs);
    }

    /// <summary>
    /// A copy with the UVs of the meshes using <paramref name="materialIndices"/> moved into
    /// <paramref name="window"/>: both UV sets, as the hair shader reads both. A vertex buffer that
    /// two LODs share is moved once. Every value must come out exact, which an aligned power-of-two
    /// window guarantees; anything else throws.
    /// </summary>
    public byte[] WithWindow(IReadOnlySet<int> materialIndices, UvWindow window)
    {
        var result = (byte[])_bytes.Clone();
        var moved = new HashSet<long>();
        foreach (var mesh in Meshes.Where(mesh => materialIndices.Contains(mesh.MaterialIndex)))
        {
            if (!moved.Add(mesh.First))
                continue;
            for (var v = 0; v < mesh.Uvs.Length; v++)
            {
                var uv = mesh.Uvs[v];
                var at = (int)(mesh.First + (long)v * mesh.Stride);
                var moved4 = mesh.FourComponents
                    ? new Vector4(window.MoveU(uv.X), window.MoveV(uv.Y), window.MoveU(uv.Z), window.MoveV(uv.W))
                    : new Vector4(window.MoveU(uv.X), window.MoveV(uv.Y), 0, 0);
                switch (mesh.Type)
                {
                    case TypeHalf2 or TypeHalf4:
                        WriteHalf(result, at, moved4.X);
                        WriteHalf(result, at + 2, moved4.Y);
                        if (mesh.Type == TypeHalf4)
                        {
                            WriteHalf(result, at + 4, moved4.Z);
                            WriteHalf(result, at + 6, moved4.W);
                        }
                        break;
                    default:
                        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), moved4.X);
                        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at + 4), moved4.Y);
                        if (mesh.Type == TypeSingle4)
                        {
                            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at + 8), moved4.Z);
                            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at + 12), moved4.W);
                        }
                        break;
                }
            }
        }
        return result;
    }

    private static void WriteHalf(byte[] bytes, int at, float value)
    {
        var half = (Half)value;
        if ((float)half != value)
            throw new InvalidDataException("A UV can't be moved exactly in half precision.");
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(at), half);
    }

    private static Vector4 ReadUv(byte[] bytes, int at, byte type) => type switch
    {
        TypeSingle2 => new Vector4(F32(bytes, at), F32(bytes, at + 4), 0, 0),
        TypeSingle3 => new Vector4(F32(bytes, at), F32(bytes, at + 4), 0, 0),
        TypeSingle4 => new Vector4(F32(bytes, at), F32(bytes, at + 4), F32(bytes, at + 8), F32(bytes, at + 12)),
        TypeHalf2 => new Vector4(F16(bytes, at), F16(bytes, at + 2), 0, 0),
        _ => new Vector4(F16(bytes, at), F16(bytes, at + 2), F16(bytes, at + 4), F16(bytes, at + 6)),
    };

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
    private static float F16(byte[] bytes, int at) => (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(at, 2));

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The {what} is outside the file.");
    }
}
