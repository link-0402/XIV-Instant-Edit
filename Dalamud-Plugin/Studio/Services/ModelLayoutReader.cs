using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>One mesh of a model's first LOD: the material slot it draws with, and its run of submeshes.</summary>
internal sealed record LayoutMesh(int Index, int MaterialIndex, int SubmeshStart, int SubmeshCount);

/// <summary>
/// The parts of a model that decide which materials it loads and which submeshes an IMC attribute
/// mask can hide. <see cref="Structured"/> is false when the layout could not be followed; the
/// materials and attributes are then every such name in the string table.
/// </summary>
internal sealed record ModelLayout(
    uint Version,
    IReadOnlyList<string> Materials,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> Bones,
    IReadOnlyList<LayoutMesh> Lod0Meshes,
    IReadOnlyList<uint> SubmeshAttributeMasks,
    IReadOnlyList<int> MeshesWithInvalidMaterial,
    bool Structured,
    string? StructureError);

/// <summary>
/// Reads the metadata in front of a model's vertex and index buffers, MDL v5 and v6 alike: their
/// layout is the same up to the material and bone name offsets (Dawntrail changed only the bone
/// tables behind them). Mod Optimizer's MdlReader, rebuilt here.
/// </summary>
internal static class ModelLayoutReader
{
    public const uint Version5 = 0x01000005;
    public const uint Version6 = 0x01000006;

    private const int HeaderSize = 0x44;
    private const int ModelHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int TerrainShadowMeshSize = 20;
    private const int SubmeshSize = 16;
    private const int TerrainShadowSubmeshSize = 12;

    public static bool LooksLikeModel(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) is Version5 or Version6;

    /// <summary>The model's layout; throws <see cref="InvalidDataException"/> for a file that is not a model.</summary>
    public static ModelLayout Read(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeModel(data))
            throw new InvalidDataException("The file is not a model (unknown MDL version).");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var table = ReadStringTable(data);
        try
        {
            return ReadStructured(data, version, table);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return new ModelLayout(version,
                table.Strings.Where(s => s.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToArray(),
                table.Strings.Where(s => s.StartsWith("atr_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray(),
                Array.Empty<string>(), Array.Empty<LayoutMesh>(), Array.Empty<uint>(), Array.Empty<int>(), false, ex.Message);
        }
    }

    private static ModelLayout ReadStructured(ReadOnlySpan<byte> data, uint version, StringTable table)
    {
        var model = table.DataStart + table.Size;
        Require(data, model, ModelHeaderSize, "model header");
        var meshCount           = U16(data, model + 4);
        var attributeCount      = U16(data, model + 6);
        var submeshCount        = U16(data, model + 8);
        var materialCount       = U16(data, model + 10);
        var boneCount           = U16(data, model + 12);
        var elementIdCount      = U16(data, model + 24);
        var terrainMeshCount    = data[model + 26];
        var flags2              = data[model + 27];
        var terrainSubmeshCount = U16(data, model + 38);

        var lodStart = model + ModelHeaderSize + elementIdCount * ElementIdSize;
        Require(data, lodStart, 3 * LodSize, "LOD table");
        var lod0MeshIndex = U16(data, lodStart);
        var lod0MeshCount = U16(data, lodStart + 2);

        var meshStart      = lodStart + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0);
        var attributeStart = meshStart + meshCount * MeshSize;
        var submeshStart   = attributeStart + attributeCount * 4 + terrainMeshCount * TerrainShadowMeshSize;
        var materialStart  = submeshStart + submeshCount * SubmeshSize + terrainSubmeshCount * TerrainShadowSubmeshSize;
        var boneStart      = materialStart + materialCount * 4;
        Require(data, meshStart, boneStart + boneCount * 4 - meshStart, "model structures");

        var materials = ReadNames(data, table, materialStart, materialCount, "material");
        if (materials.Any(name => !name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The material offset table does not point at material names.");
        var attributes = ReadNames(data, table, attributeStart, attributeCount, "attribute");
        var bones = ReadNames(data, table, boneStart, boneCount, "bone");

        var meshes = new List<LayoutMesh>(lod0MeshCount);
        var invalid = new List<int>();
        for (var i = 0; i < meshCount; ++i)
        {
            var offset = meshStart + i * MeshSize;
            var materialIndex = U16(data, offset + 8);
            var submeshIndex = U16(data, offset + 10);
            var meshSubmeshes = U16(data, offset + 12);
            if (materialIndex >= materialCount) invalid.Add(i);
            if (i >= lod0MeshIndex && i < lod0MeshIndex + lod0MeshCount)
            {
                if (submeshIndex + meshSubmeshes > submeshCount)
                    throw new InvalidDataException($"Mesh {i} references submeshes outside the submesh table.");
                meshes.Add(new LayoutMesh(i, materialIndex, submeshIndex, meshSubmeshes));
            }
        }

        var masks = new uint[submeshCount];
        for (var i = 0; i < submeshCount; ++i)
            masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(submeshStart + i * SubmeshSize + 8)..]);

        return new ModelLayout(version, materials, attributes, bones, meshes, masks, invalid, true, null);
    }

    private static string[] ReadNames(ReadOnlySpan<byte> data, StringTable table, int start, int count, string kind)
    {
        var result = new string[count];
        for (var i = 0; i < count; ++i)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(start + i * 4)..]);
            if (!table.ByOffset.TryGetValue(offset, out var name))
                throw new InvalidDataException($"A {kind} name offset does not point at a string.");
            result[i] = name;
        }
        return result;
    }

    private sealed record StringTable(IReadOnlyList<string> Strings, IReadOnlyDictionary<uint, string> ByOffset, int DataStart, int Size);

    private static StringTable ReadStringTable(ReadOnlySpan<byte> data)
    {
        var tableStart = HeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (tableStart + 8 > data.Length) throw new InvalidDataException("MDL string table is outside the file.");
        var count = U16(data, (int)tableStart);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)tableStart + 4)..]);
        var dataStart = (int)tableStart + 8;
        if (dataStart + (long)size > data.Length) throw new InvalidDataException("MDL string table is truncated.");

        var strings = new List<string>(count);
        var byOffset = new Dictionary<uint, string>(count);
        var block = data.Slice(dataStart, (int)size);
        var position = 0;
        for (var i = 0; i < count && position < block.Length; ++i)
        {
            var terminator = block[position..].IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("MDL string table is unterminated.");
            var value = Encoding.UTF8.GetString(block.Slice(position, terminator));
            strings.Add(value);
            byOffset.TryAdd((uint)position, value);
            position += terminator + 1;
        }
        return new StringTable(strings, byOffset, dataStart, (int)size);
    }

    private static void Require(ReadOnlySpan<byte> data, int start, int length, string what)
    {
        if (start < 0 || length < 0 || start + (long)length > data.Length)
            throw new InvalidDataException($"MDL {what} extends beyond the file.");
    }

    private static int U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
