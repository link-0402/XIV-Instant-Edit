using System.Buffers.Binary;
using System.Text;

namespace InstantEdit.Services.Previews;

/// <summary> What the model card shows; <see cref="Partial"/> means a later section could not be read. </summary>
public sealed record ModelInfo(
    uint Version,
    int LodCount,
    int MeshCount,
    int Lod0Vertices,
    int Lod0Indices,
    IReadOnlyList<string> Materials,
    IReadOnlyList<string> Attributes,
    int BoneCount,
    int ShapeCount,
    float Radius,
    bool Partial,
    string? Note)
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;

    public bool IsV6 => Version == V6;
    public int Lod0Triangles => Lod0Indices / 3;

    public string VersionLabel => Version switch
    {
        V6 => "V6",
        V5 => "V5",
        _ => $"0x{Version:X8}",
    };
}

/// <summary>
/// Reads the version-stable front of an MDL (file header, string table, mesh header, LOD and
/// mesh tables, attribute names) by hand, the same way the material preview does, because the
/// bundled Lumina misreads Dawntrail models. Never throws: whatever could be read is returned.
/// </summary>
public static class ModelInfoReader
{
    private const int FileHeaderSize = 68;
    private const int VertexDeclarationSize = 17 * 8;
    private const int StringHeaderSize = 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int MaxMeshes = 4096;
    private const int MaxAttributes = 256;
    private const int MaxDeclarations = 256;
    private const int MaxStrings = 16_384;
    private const int MaxStringBytes = 16 * 1024 * 1024;

    public static ModelInfo Read(byte[] bytes)
    {
        var version = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0u;
        var materials = Array.Empty<string>();
        var attributes = new List<string>();
        int lodCount = 0, meshCount = 0, vertices = 0, indices = 0, bones = 0, shapes = 0;
        var radius = 0f;
        string? note = null;
        var partial = true;
        try
        {
            if (bytes.Length < FileHeaderSize + StringHeaderSize)
                throw new InvalidDataException("The file is too short to be a model.");
            if (version is not (ModelInfo.V5 or ModelInfo.V6))
                throw new InvalidDataException($"Unsupported MDL version 0x{version:X8}.");
            lodCount = bytes[64];

            try
            {
                materials = MaterialPreviewBundleBuilder.ReadModelMaterials(bytes).ToArray();
            }
            catch (InvalidDataException)
            {
                // Fall back to the raw .mtrl strings below.
            }

            var declarationCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
            if (declarationCount > MaxDeclarations)
                throw new InvalidDataException("The model declares too many vertex layouts.");
            var stringHeader = checked(FileHeaderSize + declarationCount * VertexDeclarationSize);
            if (stringHeader > bytes.Length - StringHeaderSize)
                throw new InvalidDataException("The string table is outside the file.");
            var stringCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(stringHeader, 2));
            var stringSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringHeader + 4, 4));
            if (stringCount > MaxStrings || stringSize > MaxStringBytes || stringSize > bytes.Length - stringHeader - StringHeaderSize)
                throw new InvalidDataException("The string table exceeds the supported bounds.");
            var stringStart = stringHeader + StringHeaderSize;
            var stringEnd = checked(stringStart + (int)stringSize);
            var names = new Dictionary<int, string>(stringCount);
            var cursor = stringStart;
            var utf8 = new UTF8Encoding(false, false);
            for (var i = 0; i < stringCount && cursor < stringEnd; i++)
            {
                var terminator = Array.IndexOf(bytes, (byte)0, cursor, stringEnd - cursor);
                if (terminator < 0)
                    break;
                names[cursor - stringStart] = utf8.GetString(bytes, cursor, terminator - cursor);
                cursor = terminator + 1;
            }
            if (materials.Length == 0)
                materials = names.Values.Where(value => value.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)).ToArray();

            var meshHeader = stringEnd;
            if (meshHeader > bytes.Length - MeshHeaderSize)
                throw new InvalidDataException("The mesh header is outside the file.");
            radius = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(meshHeader, 4));
            meshCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 4, 2));
            var attributeCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 6, 2));
            bones = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 12, 2));
            shapes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 16, 2));
            if (lodCount == 0)
                lodCount = bytes[meshHeader + 22];
            var elementIdCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(meshHeader + 24, 2));
            var flags2 = bytes[meshHeader + 27];
            if (meshCount > MaxMeshes || attributeCount > MaxAttributes)
                throw new InvalidDataException("The model declares too many meshes or attributes.");

            var lodTable = checked(meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize);
            if (lodTable > bytes.Length - 3 * LodSize)
                throw new InvalidDataException("The LOD table is outside the file.");
            var lod0MeshIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lodTable, 2));
            var lod0MeshCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lodTable + 2, 2));

            var meshTable = checked(lodTable + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0));
            if (meshTable > bytes.Length - meshCount * MeshSize)
                throw new InvalidDataException("The mesh table is outside the file.");
            for (var i = lod0MeshIndex; i < Math.Min(meshCount, lod0MeshIndex + lod0MeshCount); i++)
            {
                var mesh = meshTable + i * MeshSize;
                vertices = checked(vertices + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(mesh, 2)));
                indices = checked(indices + (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mesh + 4, 4))));
            }

            var attributeTable = checked(meshTable + meshCount * MeshSize);
            if (attributeTable > bytes.Length - attributeCount * 4)
                throw new InvalidDataException("The attribute table is outside the file.");
            for (var i = 0; i < attributeCount; i++)
            {
                var offset = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(attributeTable + i * 4, 4)));
                attributes.Add(names.TryGetValue(offset, out var name) ? name : $"#{offset}");
            }
            partial = false;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException or DecoderFallbackException)
        {
            note = e.Message;
        }

        return new ModelInfo(version, lodCount, meshCount, vertices, indices, materials, attributes, bones, shapes, radius, partial, note);
    }
}
