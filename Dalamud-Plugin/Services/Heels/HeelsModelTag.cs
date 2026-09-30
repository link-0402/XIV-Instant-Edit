using System.Buffers.Binary;
using System.Text;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Heels;

/// <summary>
/// Writes a Simple Heels offset into a Dawntrail model as its only heels offset attribute. A model that
/// has one gets it renamed and loses any others; a model without one gets a new attribute on its first
/// part (LOD 0's first submesh), as the Blender add-on puts a calculated offset there. Everything else
/// keeps its bytes: the string table is rebuilt in its own order, every name that points into it is
/// pointed at its new place, and the tables after it move by a multiple of 8 bytes, so their alignment
/// holds. The geometry offsets in the file header and the LODs move with them.
/// </summary>
internal static class HeelsModelTag
{
    /// <summary> A part's attributes are a 32-bit mask, so a model holds at most 32. </summary>
    public const int MaxAttributes = 32;

    private const int FileHeaderSize = 68;
    private const int DeclarationSize = 17 * 8;
    private const int ModelHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int TerrainShadowMeshSize = 20;
    private const int SubmeshSize = 16;
    private const int TerrainShadowSubmeshSize = 12;
    private const int ShapeSize = 16;

    /// <summary> The model with <paramref name="attribute"/> as its only heels offset attribute. </summary>
    public static byte[] Apply(byte[] bytes, string attribute)
    {
        if (!HeelsModelOffset.IsOffset(attribute) || attribute.Contains('\0'))
            throw new ArgumentException("This is not a heels offset attribute.", nameof(attribute));
        Require(bytes.Length >= FileHeaderSize, "file header");
        if (U32(bytes, 0) != ModelInfo.V6)
            throw new NotSupportedException("Only Dawntrail models can hold the offset.");
        var stackSize = U32(bytes, 4);
        var runtimeSize = U32(bytes, 8);
        int declarationCount = U16(bytes, 12);
        int lodCount = bytes[64];
        if (bytes[66] != 0)
            throw new NotSupportedException("The model uses edge geometry, which Instant Edit can't move.");
        if (stackSize != declarationCount * DeclarationSize)
            throw new InvalidDataException("The model's vertex declarations don't match its header.");
        var dataStart = checked(FileHeaderSize + (long)stackSize + runtimeSize);
        Require(dataStart <= bytes.Length, "geometry");

        var stringHeader = FileHeaderSize + declarationCount * DeclarationSize;
        Require(stringHeader + 8L <= dataStart, "string table");
        int stringCount = U16(bytes, stringHeader);
        var stringSize = (int)Math.Min(U32(bytes, stringHeader + 4), int.MaxValue);
        var stringBlock = stringHeader + 8;
        Require(stringBlock + (long)stringSize + ModelHeaderSize <= dataStart, "string table");
        var modelHeader = stringBlock + stringSize;

        int meshCount = U16(bytes, modelHeader + 4);
        int attributeCount = U16(bytes, modelHeader + 6);
        int submeshCount = U16(bytes, modelHeader + 8);
        int materialCount = U16(bytes, modelHeader + 10);
        int boneCount = U16(bytes, modelHeader + 12);
        int boneTableCount = U16(bytes, modelHeader + 14);
        int shapeCount = U16(bytes, modelHeader + 16);
        int elementIdCount = U16(bytes, modelHeader + 24);
        int terrainShadowMeshCount = bytes[modelHeader + 26];
        var flags2 = bytes[modelHeader + 27];
        int terrainShadowSubmeshCount = U16(bytes, modelHeader + 38);
        int neckMorphCount = bytes[modelHeader + 43];
        int boneTableArrayCount = U16(bytes, modelHeader + 44);
        if (neckMorphCount > 0 || U32(bytes, modelHeader + 48) > 0)
            throw new NotSupportedException("Face models can't hold the offset.");
        if (attributeCount > MaxAttributes)
            throw new InvalidDataException($"The model lists {attributeCount} attributes, more than a model can use.");

        var elementIds = modelHeader + ModelHeaderSize;
        var lodTable = elementIds + elementIdCount * ElementIdSize;
        var meshTable = lodTable + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0);
        var attributeTable = meshTable + meshCount * MeshSize;
        var afterAttributes = attributeTable + attributeCount * 4;
        var submeshTable = afterAttributes + terrainShadowMeshCount * TerrainShadowMeshSize;
        var materialTable = submeshTable + submeshCount * SubmeshSize + terrainShadowSubmeshCount * TerrainShadowSubmeshSize;
        var boneNameTable = materialTable + materialCount * 4;
        var shapeTable = boneNameTable + boneCount * 4 + boneTableCount * 4 + boneTableArrayCount * 2;
        Require(shapeTable + (long)shapeCount * ShapeSize <= dataStart, "model tables");

        // The string table in its own order: where each string starts and its bytes.
        var strings = new List<(int Offset, byte[] Bytes)>(stringCount);
        var stringAt = new Dictionary<int, int>(stringCount);
        for (int i = 0, at = 0; i < stringCount; i++)
        {
            var end = at < stringSize ? Array.IndexOf(bytes, (byte)0, stringBlock + at, stringSize - at) : -1;
            if (end < 0)
                throw new InvalidDataException("A name in the model's string table is not terminated.");
            stringAt[at] = strings.Count;
            strings.Add((at, bytes[(stringBlock + at)..end]));
            at = end - stringBlock + 1;
        }
        int StringIndex(int position)
        {
            var offset = U32(bytes, position);
            return offset <= int.MaxValue && stringAt.TryGetValue((int)offset, out var index)
                ? index
                : throw new InvalidDataException("The model names a string where its string table has none, so its names can't be rewritten.");
        }

        // Every other name in the header: element parents, materials, bones and shapes, by where their offsets sit.
        var references = new List<int>(elementIdCount + materialCount + boneCount + shapeCount);
        for (var i = 0; i < elementIdCount; i++)
            references.Add(elementIds + i * ElementIdSize + 4);
        for (var i = 0; i < materialCount; i++)
            references.Add(materialTable + i * 4);
        for (var i = 0; i < boneCount; i++)
            references.Add(boneNameTable + i * 4);
        for (var i = 0; i < shapeCount; i++)
            references.Add(shapeTable + i * ShapeSize);
        var referenced = new int[strings.Count];
        foreach (var position in references)
            referenced[StringIndex(position)]++;

        // The attributes that stay, in order: the first offset attribute stays in its place, the others go.
        var attributes = new int[attributeCount];
        for (var i = 0; i < attributeCount; i++)
            attributes[i] = StringIndex(attributeTable + i * 4);
        var tags = Enumerable.Range(0, attributeCount)
            .Where(i => HeelsModelOffset.IsOffset(Encoding.UTF8.GetString(strings[attributes[i]].Bytes)))
            .ToList();
        var kept = new List<int>(attributeCount + 1);
        var remap = new int[attributeCount];
        for (var i = 0; i < attributeCount; i++)
        {
            remap[i] = tags.Count > 1 && tags.IndexOf(i) > 0 ? -1 : kept.Count;
            if (remap[i] >= 0)
                kept.Add(i);
        }
        var tag = tags.Count > 0 ? remap[tags[0]] : kept.Count;
        if (tags.Count == 0)
        {
            if (attributeCount >= MaxAttributes)
                throw new InvalidOperationException($"The model already has {MaxAttributes} attributes, the most a model can hold, so it has no room for the offset.");
            kept.Add(-1);
        }
        foreach (var index in kept)
            if (index >= 0 && index != (tags.Count > 0 ? tags[0] : -1))
                referenced[attributes[index]]++;

        // The new string table: the tag's own string takes the new text when nothing else names it,
        // otherwise the text goes in after it, or after the last attribute for a new tag.
        var text = Encoding.UTF8.GetBytes(attribute);
        var removed = tags.Skip(1).Select(i => attributes[i]).ToHashSet();
        var rename = tags.Count > 0 && referenced[attributes[tags[0]]] == 0 ? attributes[tags[0]] : -1;
        var insertAfter = tags.Count > 0
            ? attributes[tags[0]]
            : kept.Where(index => index >= 0).Select(index => attributes[index]).DefaultIfEmpty(-1).Max();
        if (tags.Count > 0)
            removed.Remove(attributes[tags[0]]);
        var block = new MemoryStream(stringSize + text.Length + 16);
        var offsets = new int[strings.Count];
        var newCount = 0;
        var tagOffset = -1;
        if (rename < 0 && insertAfter < 0)
            tagOffset = Append(block, text, ref newCount);
        for (var s = 0; s < strings.Count; s++)
        {
            if (removed.Contains(s) && referenced[s] == 0)
            {
                offsets[s] = -1;
                continue;
            }
            offsets[s] = Append(block, s == rename ? text : strings[s].Bytes, ref newCount);
            if (s == rename)
                tagOffset = offsets[s];
            else if (s == insertAfter && rename < 0)
                tagOffset = Append(block, text, ref newCount);
        }
        if (newCount > ushort.MaxValue)
            throw new InvalidOperationException("The model's string table is full.");

        // The tables before the attributes move by the string table's growth, the ones after it by that and
        // the attribute table's: both stay aligned as they were, the later ones (bounding boxes, geometry) to 8 bytes.
        var delta = kept.Count - attributeCount;
        var newSize = (int)block.Length;
        while (Mod(newSize - stringSize, 4) != 0 || Mod(newSize - stringSize + 4 * delta, 8) != 0)
            newSize++;
        var headerShift = newSize - stringSize;
        var shift = headerShift + 4 * delta;
        var output = new byte[checked(bytes.Length + shift)];

        bytes.AsSpan(0, stringHeader + 4).CopyTo(output);
        W16(output, stringHeader, (ushort)newCount);
        W32(output, stringHeader + 4, (uint)newSize);
        block.GetBuffer().AsSpan(0, (int)block.Length).CopyTo(output.AsSpan(stringBlock));
        W32(output, 8, checked((uint)(runtimeSize + shift)));
        for (var lod = 0; lod < 3; lod++)
        {
            MoveGeometryOffset(output, 16 + lod * 4, lod < lodCount);
            MoveGeometryOffset(output, 28 + lod * 4, lod < lodCount);
        }

        var newModelHeader = modelHeader + headerShift;
        bytes.AsSpan(modelHeader, attributeTable - modelHeader).CopyTo(output.AsSpan(newModelHeader));
        W16(output, newModelHeader + 6, (ushort)kept.Count);
        for (var lod = 0; lod < 3; lod++)
        {
            MoveGeometryOffset(output, lodTable + headerShift + lod * LodSize + 52, lod < lodCount);
            MoveGeometryOffset(output, lodTable + headerShift + lod * LodSize + 56, lod < lodCount);
        }
        for (var i = 0; i < kept.Count; i++)
            W32(output, attributeTable + headerShift + i * 4, (uint)(kept[i] < 0 || i == tag ? tagOffset : offsets[attributes[kept[i]]]));

        bytes.AsSpan(afterAttributes, (int)(dataStart - afterAttributes)).CopyTo(output.AsSpan(afterAttributes + shift));
        bytes.AsSpan((int)dataStart).CopyTo(output.AsSpan((int)dataStart + shift));
        foreach (var position in references)
            W32(output, position + (position < attributeTable ? headerShift : shift), (uint)offsets[StringIndex(position)]);

        // Parts keep their attributes under their new numbers; the parts of a removed offset get the one that stays.
        var first = tags.Count == 0 ? FirstPart(bytes, lodTable, meshTable, meshCount, submeshCount) : -1;
        var known = attributeCount >= 32 ? uint.MaxValue : (1u << attributeCount) - 1;
        for (var s = 0; s < submeshCount; s++)
        {
            var mask = U32(bytes, submeshTable + s * SubmeshSize + 8);
            var result = mask & ~known;
            for (var b = 0; b < attributeCount; b++)
                if ((mask & (1u << b)) != 0)
                    result |= remap[b] >= 0 ? 1u << remap[b] : 1u << tag;
            if (s == first)
                result |= 1u << tag;
            W32(output, submeshTable + shift + s * SubmeshSize + 8, result);
        }
        return output;

        // A used LOD's offsets count from the geometry, as the game and Penumbra read them, even an empty LOD's that point
        // before it. An unused LOD's that point into the geometry or past it (often at its end) move with it; others stay.
        void MoveGeometryOffset(byte[] target, int position, bool used)
        {
            var value = U32(target, position);
            if (used || value >= dataStart)
                W32(target, position, unchecked((uint)(value + shift)));
        }
    }

    /// <summary> LOD 0's first part: the first submesh of its first mesh that has any. -1 when it has none. </summary>
    private static int FirstPart(byte[] bytes, int lodTable, int meshTable, int meshCount, int submeshCount)
    {
        int first = U16(bytes, lodTable), count = U16(bytes, lodTable + 2);
        for (var mesh = first; mesh < first + count && mesh < meshCount; mesh++)
        {
            int submesh = U16(bytes, meshTable + mesh * MeshSize + 10), submeshes = U16(bytes, meshTable + mesh * MeshSize + 12);
            if (submeshes > 0 && submesh < submeshCount)
                return submesh;
        }
        return -1;
    }

    private static int Append(MemoryStream block, byte[] text, ref int count)
    {
        var offset = (int)block.Length;
        block.Write(text);
        block.WriteByte(0);
        count++;
        return offset;
    }

    private static int Mod(int value, int divisor) => (value % divisor + divisor) % divisor;

    private static ushort U16(byte[] bytes, int at)
    {
        Require(at >= 0 && at + 2L <= bytes.Length, "model header");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    }

    private static uint U32(byte[] bytes, int at)
    {
        Require(at >= 0 && at + 4L <= bytes.Length, "model header");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    }

    private static void W16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at, 2), value);
    private static void W32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The model's {what} is outside the file.");
    }
}
