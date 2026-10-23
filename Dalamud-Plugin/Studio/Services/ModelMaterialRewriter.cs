using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Reads, and rewires, the material references of a .mdl file.
///
/// A model's mesh parts name their materials through the file's string block, so switching a part
/// to a different .mtrl means giving it a different string. The block cannot be appended to: both
/// the game and Penumbra read it by walking exactly StringCount null-terminated entries from its
/// start and then matching each attribute/material/bone/shape reference against those entries' own
/// start offsets — an offset that is not one of them reads as nothing at all (Penumbra's model
/// editor shows such a material as blank, and the game loads none). The trailing padding of an
/// existing block would be walked as empty strings, so anything written after it is unreachable.
///
/// So the block is rebuilt whole, in the order the game's own files and Penumbra's writer use —
/// attributes, bones, materials, shape names, contiguous and padded — and every offset table that
/// points into it is repointed. Everything the block grew or shrank by shifts the rest of the file,
/// so the file header's and each LOD's absolute vertex/index/edge-geometry offsets, and RuntimeSize
/// (which covers the string block), move with it. The shift is kept a multiple of 16 so the
/// alignment the file already had — including the 8-byte alignment the bounding boxes rely on —
/// survives untouched.
///
/// Everything else, from the bone tables to the vertex buffers, is copied through byte for byte.
/// </summary>
public static class ModelMaterialRewriter
{
    private const int FileHeaderSize = 68;
    private const int LodStructSize = 60;
    private const int ExtraLodStructSize = 40;
    private const int ElementIdStructSize = 32;
    private const int MeshStructSize = 36;
    private const int TerrainShadowMeshStructSize = 20;
    private const int SubmeshStructSize = 16;
    private const int TerrainShadowSubmeshStructSize = 12;
    private const int ShapeStructSize = 16;
    private const int BoneTableV5Size = 132;
    private const int BoneTableV6HeaderSize = 4;

    // Byte positions inside one LOD struct of the offsets that point elsewhere in the file.
    private const int LodEdgeGeometryDataOffset = 32;
    private const int LodVertexDataOffset = 52;
    private const int LodIndexDataOffset = 56;

    // The material count is kept twice: in the file header and in the model header.
    private const int FileHeaderMaterialCount = 14;
    private const int ModelHeaderMaterialCount = 10;

    // Byte position inside one mesh struct of the material slot it uses.
    private const int MeshMaterialIndex = 8;

    /// <summary>The material each mesh part references, in the file's own slot order.</summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static IReadOnlyList<string> ReadMaterials(byte[] data) => Parse(data).Materials;

    /// <summary>
    /// The same, resolved the way the game and Penumbra resolve it: by walking the string block for
    /// exactly StringCount entries and matching each reference against those entries' own start
    /// offsets. A name that comes back empty is one neither of them can load, however readable it
    /// looks in the raw bytes — so the build checks its own output with this before shipping it.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static IReadOnlyList<string> ReadMaterialsStrict(byte[] data)
    {
        var mdl = Parse(data);
        var starts = WalkStrings(mdl.StringData, mdl.StringCount);
        var names = new string[mdl.MaterialOffsets.Length];
        for (int i = 0; i < names.Length; i++)
            names[i] = starts.TryGetValue(mdl.MaterialOffsets[i], out var name) ? name : string.Empty;
        return names;
    }

    /// <summary>
    /// The material each mesh of the first LOD uses, in mesh order. Meshes that share one material
    /// slot each get their own entry, so they can be given different materials by
    /// <see cref="RewriteMeshes"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static IReadOnlyList<string> ReadMeshMaterials(byte[] data)
    {
        var mdl = Parse(data);
        return MainMeshes(mdl).Select(m => NameAt(mdl.Materials, mdl.MeshMaterials[m])).ToArray();
    }

    /// <summary>The same, resolved the way the game does — see <see cref="ReadMaterialsStrict"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static IReadOnlyList<string> ReadMeshMaterialsStrict(byte[] data)
    {
        var mdl = Parse(data);
        var starts = WalkStrings(mdl.StringData, mdl.StringCount);
        return MainMeshes(mdl).Select(m =>
        {
            int slot = mdl.MeshMaterials[m];
            return slot < mdl.MaterialOffsets.Length && starts.TryGetValue(mdl.MaterialOffsets[slot], out var name) ? name : string.Empty;
        }).ToArray();
    }

    /// <summary>The material slot each mesh of the first LOD uses, in mesh order.</summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static IReadOnlyList<int> ReadMeshMaterialSlots(byte[] data)
    {
        var mdl = Parse(data);
        return MainMeshes(mdl).Select(m => (int)mdl.MeshMaterials[m]).ToArray();
    }

    /// <summary>
    /// Returns the file with each mesh of the first LOD pointed at <paramref name="newNames"/> (one
    /// entry per mesh, as <see cref="ReadMeshMaterials"/> lists them), where a null entry leaves that
    /// mesh on its own material. A material slot whose meshes all end up on one name is renamed in
    /// place; when they end up on different names, the first mesh's name takes the slot and the others
    /// get slots of their own, appended (or an existing slot already carrying that name). A later LOD
    /// with as many meshes as the first mirrors it mesh by mesh, so its meshes follow theirs onto a new
    /// slot; other LODs' meshes stay on their slot. Returns the input unchanged when nothing needs rewriting.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static byte[] RewriteMeshes(byte[] data, IReadOnlyList<string?> newNames)
    {
        var mdl = Parse(data);
        var meshes = MainMeshes(mdl).ToArray();

        var wanted = new string?[meshes.Length];
        for (int i = 0; i < meshes.Length; i++)
        {
            var name = i < newNames.Count ? newNames[i] : null;
            int slot = mdl.MeshMaterials[meshes[i]];
            wanted[i] = !string.IsNullOrEmpty(name) ? name : slot < mdl.Materials.Length ? mdl.Materials[slot] : null;
        }

        // First pass: each slot takes the name of the first mesh that uses it.
        var materials = mdl.Materials.ToList();
        var renamed = new bool[materials.Count];
        for (int i = 0; i < meshes.Length; i++)
        {
            int slot = mdl.MeshMaterials[meshes[i]];
            if (wanted[i] == null || slot >= materials.Count || renamed[slot])
                continue;
            materials[slot] = wanted[i]!;
            renamed[slot] = true;
        }

        // Second pass: meshes whose slot went to another name move to a slot of their own.
        var meshMaterials = (ushort[])mdl.MeshMaterials.Clone();
        for (int i = 0; i < meshes.Length; i++)
        {
            int slot = mdl.MeshMaterials[meshes[i]];
            if (wanted[i] == null || slot >= materials.Count || string.Equals(materials[slot], wanted[i], StringComparison.Ordinal))
                continue;
            int other = materials.FindIndex(n => string.Equals(n, wanted[i], StringComparison.Ordinal));
            if (other < 0)
            {
                other = materials.Count;
                materials.Add(wanted[i]!);
            }
            meshMaterials[meshes[i]] = checked((ushort)other);
        }

        // Later LODs: a mesh on the same slot as the first LOD's mesh it mirrors goes where that one went.
        foreach (var (index, count) in mdl.LowerLods)
        {
            if (count == 0 || count != meshes.Length || index < 0 || index + count > meshMaterials.Length || index == meshes[0])
                continue;
            for (int i = 0; i < count; i++)
                if (mdl.MeshMaterials[index + i] == mdl.MeshMaterials[meshes[i]])
                    meshMaterials[index + i] = meshMaterials[meshes[i]];
        }

        bool changed = materials.Count != mdl.Materials.Length
                    || !materials.SequenceEqual(mdl.Materials, StringComparer.Ordinal)
                    || !meshMaterials.SequenceEqual(mdl.MeshMaterials);
        return changed ? Write(data, mdl, materials.ToArray(), meshMaterials) : data;
    }

    /// <summary>The meshes of the first LOD — every mesh when the LOD table does not say.</summary>
    private static IEnumerable<int> MainMeshes(ModelLayout mdl)
    {
        bool valid = mdl.Lod0MeshCount > 0 && mdl.Lod0MeshIndex + mdl.Lod0MeshCount <= mdl.MeshMaterials.Length;
        return valid
            ? Enumerable.Range(mdl.Lod0MeshIndex, mdl.Lod0MeshCount)
            : Enumerable.Range(0, mdl.MeshMaterials.Length);
    }

    private static string NameAt(string[] names, int index) => index < names.Length ? names[index] : string.Empty;

    /// <summary>Every string the block's own count reaches, by the offset it starts at.</summary>
    private static Dictionary<uint, string> WalkStrings(byte[] strings, int count)
    {
        var starts = new Dictionary<uint, string>();
        int at = 0;
        for (int i = 0; i < count && at < strings.Length; i++)
        {
            int end = at;
            while (end < strings.Length && strings[end] != 0) end++;
            starts[(uint)at] = Encoding.UTF8.GetString(strings, at, end - at);
            at = end + 1;
        }
        return starts;
    }

    /// <summary>
    /// Returns the file with each material slot pointed at <paramref name="newNames"/>, where a null
    /// entry (or one already equal to the file's own) leaves that slot alone. Returns the input
    /// unchanged when nothing needs rewriting.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a model this can read.</exception>
    public static byte[] Rewrite(byte[] data, IReadOnlyList<string?> newNames)
    {
        var mdl = Parse(data);

        var materials = (string[])mdl.Materials.Clone();
        bool changed = false;
        for (int i = 0; i < materials.Length; i++)
        {
            var name = i < newNames.Count ? newNames[i] : null;
            if (string.IsNullOrEmpty(name) || string.Equals(name, materials[i], StringComparison.Ordinal))
                continue;
            materials[i] = name;
            changed = true;
        }

        return changed ? Write(data, mdl, materials, mdl.MeshMaterials) : data;
    }

    /// <summary>
    /// Writes the file with <paramref name="materials"/> as its material slots (the same count, or
    /// more — new slots go at the end of the table) and <paramref name="meshMaterials"/> as each
    /// mesh's slot.
    /// </summary>
    private static byte[] Write(byte[] data, ModelLayout mdl, string[] materials, ushort[] meshMaterials)
    {
        if (materials.Length < mdl.Materials.Length || materials.Length > ushort.MaxValue)
            throw new InvalidDataException("the material slots cannot be rewritten to that count");

        // ── The string block, laid out the way the game's own files are ──────
        var blob = new List<byte>();
        var attributeOffsets = Append(blob, mdl.Attributes);
        var boneOffsets      = Append(blob, mdl.Bones);
        var materialOffsets  = Append(blob, materials);
        var shapeOffsets     = Append(blob, mdl.ShapeNames);
        int stringCount = mdl.Attributes.Length + mdl.Bones.Length + materials.Length + mdl.ShapeNames.Length;

        // New slots each add an entry to the material offset table, which sits inside the model data.
        int grown = (materials.Length - mdl.Materials.Length) * 4;

        // Pad to the block's own 4 bytes, then on until the whole file after the material table has
        // moved by a multiple of 16 — every alignment further down is measured from the start of the
        // file. What sits between the block and the table is 4-byte data, which stays 4-aligned.
        int size = blob.Count;
        while (size % 4 != 0 || (size - (int)mdl.StringSize + grown) % 16 != 0)
            size++;
        int head  = size - (int)mdl.StringSize;      // how far the model data up to the material table moves
        int delta = head + grown;                     // how far everything after it moves

        long tableEnd = mdl.MaterialOffsetsPos + mdl.Materials.Length * 4L;
        var result = new byte[data.Length + delta];
        Array.Copy(data, 0, result, 0, mdl.StringDataPos);
        blob.CopyTo(result, (int)mdl.StringDataPos);          // the padding stays zero
        Array.Copy(data, mdl.ModelDataPos, result, mdl.ModelDataPos + head, tableEnd - mdl.ModelDataPos);
        Array.Copy(data, tableEnd, result, tableEnd + delta, data.Length - tableEnd);

        // RuntimeSize covers the string block onwards, so it moves with it; StackSize does not.
        WriteU32(result, 8, (uint)(mdl.RuntimeSize + delta));
        WriteU16(result, FileHeaderMaterialCount, (ushort)materials.Length);
        WriteU16(result, mdl.StringHeaderPos, (ushort)stringCount);
        WriteU32(result, mdl.StringHeaderPos + 4, (uint)size);
        WriteU16(result, mdl.ModelDataPos + head + ModelHeaderMaterialCount, (ushort)materials.Length);

        // Everything past the material table moved: the file header's vertex/index offsets…
        for (int i = 0; i < 3; i++)
        {
            Shift(result, 16 + i * 4, delta);
            Shift(result, 28 + i * 4, delta);
        }

        // …and the same offsets repeated per LOD, plus its edge geometry.
        for (int lod = 0; lod < 3; lod++)
        {
            long at = mdl.LodsPos + head + lod * LodStructSize;
            Shift(result, at + LodEdgeGeometryDataOffset, delta);
            Shift(result, at + LodVertexDataOffset, delta);
            Shift(result, at + LodIndexDataOffset, delta);
        }

        for (int m = 0; m < meshMaterials.Length; m++)
            WriteU16(result, mdl.MeshesPos + head + m * MeshStructSize + MeshMaterialIndex, meshMaterials[m]);

        WriteOffsets(result, mdl.AttributeOffsetsPos + head, attributeOffsets);
        WriteOffsets(result, mdl.MaterialOffsetsPos + head, materialOffsets);
        WriteOffsets(result, mdl.BoneOffsetsPos + delta, boneOffsets);
        for (int i = 0; i < shapeOffsets.Length; i++)
            WriteU32(result, mdl.ShapesPos + delta + i * ShapeStructSize, shapeOffsets[i]);

        return result;
    }

    /// <summary>Writes each name into the block, returning where each one landed.</summary>
    private static uint[] Append(List<byte> blob, IReadOnlyList<string> names)
    {
        var offsets = new uint[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            offsets[i] = (uint)blob.Count;
            blob.AddRange(Encoding.UTF8.GetBytes(names[i]));
            blob.Add(0);
        }
        return offsets;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Where the pieces this touches live in one .mdl, and the names they point at.</summary>
    private sealed class ModelLayout
    {
        public uint RuntimeSize;
        public long StringHeaderPos;
        public long StringDataPos;
        public uint StringSize;
        public int  StringCount;
        public byte[] StringData = Array.Empty<byte>();
        public long ModelDataPos;
        public long LodsPos;
        public long AttributeOffsetsPos;
        public long MaterialOffsetsPos;
        public long BoneOffsetsPos;
        public long ShapesPos;
        public long MeshesPos;

        /// <summary>The first LOD's range of meshes, as its LOD struct gives it.</summary>
        public int Lod0MeshIndex;
        public int Lod0MeshCount;

        /// <summary>The ranges of meshes of the LODs after the first that the file has (LodCount, at most 3).</summary>
        public (int Index, int Count)[] LowerLods = Array.Empty<(int, int)>();

        /// <summary>The material slot each mesh uses, for every mesh of the file.</summary>
        public ushort[] MeshMaterials = Array.Empty<ushort>();

        public string[] Attributes = Array.Empty<string>();
        public string[] Materials  = Array.Empty<string>();

        /// <summary>The raw offsets the material table holds, for resolving them as the game does.</summary>
        public uint[] MaterialOffsets = Array.Empty<uint>();

        public string[] Bones      = Array.Empty<string>();
        public string[] ShapeNames = Array.Empty<string>();
    }

    private static ModelLayout Parse(byte[] data)
    {
        if (data.Length < FileHeaderSize)
            throw new InvalidDataException("the file is too small to be a model");

        try
        {
            using var ms = new MemoryStream(data, writable: false);
            using var br = new BinaryReader(ms);

            uint version     = br.ReadUInt32();
            uint stackSize   = br.ReadUInt32();
            uint runtimeSize = br.ReadUInt32();
            if (version != VanillaModelReader.V5 && version != VanillaModelReader.V6)
                throw new InvalidDataException($"model version {version:X8} is not one this can rewrite");

            long stringHeaderPos = FileHeaderSize + stackSize;
            ms.Position = stringHeaderPos;
            ushort stringCount = br.ReadUInt16();
            br.ReadUInt16();                    // padding
            uint stringSize = br.ReadUInt32();
            long stringDataPos = ms.Position;
            // Checked before reading: ReadBytes allocates the whole size it is asked for up front.
            if (stringSize > ms.Length - stringDataPos)
                throw new InvalidDataException("the string block runs past the end of the file");
            var strings = br.ReadBytes((int)stringSize);
            if (strings.Length != stringSize)
                throw new InvalidDataException("the string block runs past the end of the file");

            long modelDataPos = ms.Position;

            // ── ModelHeader ──────────────────────────────────────────────────
            br.ReadSingle();                    // Radius
            ushort meshCount      = br.ReadUInt16();
            ushort attributeCount = br.ReadUInt16();
            ushort submeshCount   = br.ReadUInt16();
            ushort materialCount  = br.ReadUInt16();
            ushort boneCount      = br.ReadUInt16();
            ushort boneTableCount = br.ReadUInt16();
            ushort shapeCount     = br.ReadUInt16();
            br.ReadUInt16();                    // ShapeMeshCount
            br.ReadUInt16();                    // ShapeValueCount
            byte lodCount = br.ReadByte();
            br.ReadByte();                      // Flags1
            ushort elementIdCount = br.ReadUInt16();
            byte terrainShadowMeshCount = br.ReadByte();
            byte flags2 = br.ReadByte();
            br.ReadSingle();                    // ModelClipOutDistance
            br.ReadSingle();                    // ShadowClipOutDistance
            br.ReadUInt16();                    // CullingGridCount
            ushort terrainShadowSubmeshCount = br.ReadUInt16();
            br.ReadBytes(4);                    // Flags3, BG material indices, NeckMorphCount
            ushort boneTableArrayTotal = br.ReadUInt16();
            br.ReadBytes(4);                    // Unknown8/9
            br.ReadBytes(6);                    // Padding

            ms.Position += elementIdCount * (long)ElementIdStructSize;
            long lodsPos = ms.Position;
            ushort lod0MeshIndex = br.ReadUInt16();
            ushort lod0MeshCount = br.ReadUInt16();
            var lowerLods = new List<(int Index, int Count)>();
            for (int lod = 1; lod < Math.Min((int)lodCount, 3); lod++)
            {
                ms.Position = lodsPos + lod * (long)LodStructSize;
                lowerLods.Add((br.ReadUInt16(), br.ReadUInt16()));
            }
            ms.Position = lodsPos + 3L * LodStructSize;
            if ((flags2 & 0x10) != 0)
                ms.Position += 3L * ExtraLodStructSize;

            long meshesPos = ms.Position;
            if (meshesPos + meshCount * (long)MeshStructSize > ms.Length)
                throw new InvalidDataException("the mesh table runs past the end of the file");
            var meshMaterials = new ushort[meshCount];
            for (int m = 0; m < meshCount; m++)
                meshMaterials[m] = BitConverter.ToUInt16(data, (int)(meshesPos + m * MeshStructSize + MeshMaterialIndex));
            ms.Position += meshCount * (long)MeshStructSize;

            long attributeOffsetsPos = ms.Position;
            var attributes = ReadNames(br, ms, strings, attributeCount);

            ms.Position += terrainShadowMeshCount * (long)TerrainShadowMeshStructSize;
            ms.Position += submeshCount * (long)SubmeshStructSize;
            ms.Position += terrainShadowSubmeshCount * (long)TerrainShadowSubmeshStructSize;

            long materialOffsetsPos = ms.Position;
            var materialOffsets = new uint[materialCount];
            var materials = ReadNames(br, ms, strings, materialCount, materialOffsets);
            long boneOffsetsPos = ms.Position;
            var bones = ReadNames(br, ms, strings, boneCount);

            // Bone tables sit between the bone names and the shapes, and are sized by version:
            // V5 keeps a fixed 64-entry array per table, V6 a header per table plus one packed
            // array block whose total length the model header carries.
            ms.Position += version >= VanillaModelReader.V6
                ? boneTableCount * (long)BoneTableV6HeaderSize + boneTableArrayTotal * 2L
                : boneTableCount * (long)BoneTableV5Size;

            long shapesPos = ms.Position;
            var shapeNames = new string[shapeCount];
            for (int i = 0; i < shapeCount; i++)
            {
                shapeNames[i] = ReadCString(strings, br.ReadUInt32());
                ms.Position += ShapeStructSize - 4;
            }

            if (ms.Position > data.Length)
                throw new InvalidDataException("the model's tables run past the end of the file");

            return new ModelLayout
            {
                RuntimeSize         = runtimeSize,
                StringHeaderPos     = stringHeaderPos,
                StringDataPos       = stringDataPos,
                StringSize          = stringSize,
                StringCount         = stringCount,
                StringData          = strings,
                ModelDataPos        = modelDataPos,
                LodsPos             = lodsPos,
                AttributeOffsetsPos = attributeOffsetsPos,
                MaterialOffsetsPos  = materialOffsetsPos,
                BoneOffsetsPos      = boneOffsetsPos,
                ShapesPos           = shapesPos,
                MeshesPos           = meshesPos,
                Lod0MeshIndex       = lod0MeshIndex,
                Lod0MeshCount       = lod0MeshCount,
                LowerLods           = lowerLods.ToArray(),
                MeshMaterials       = meshMaterials,
                Attributes          = attributes,
                Materials           = materials,
                MaterialOffsets     = materialOffsets,
                Bones               = bones,
                ShapeNames          = shapeNames,
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("the file does not read as a model", ex);
        }
    }

    /// <summary>One offset table, resolved against the string block.</summary>
    private static string[] ReadNames(BinaryReader br, Stream ms, byte[] strings, int count, uint[]? rawOffsets = null)
    {
        if (ms.Position + count * 4L > ms.Length)
            throw new InvalidDataException("a name table is not where the header says it is");

        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            uint offset = br.ReadUInt32();
            if (rawOffsets != null) rawOffsets[i] = offset;
            names[i] = ReadCString(strings, offset);
        }
        return names;
    }

    private static void WriteOffsets(byte[] buffer, long position, uint[] offsets)
    {
        for (int i = 0; i < offsets.Length; i++)
            WriteU32(buffer, position + i * 4, offsets[i]);
    }

    /// <summary>Moves one absolute file offset along by <paramref name="delta"/>; 0 means "none" and stays.</summary>
    private static void Shift(byte[] buffer, long position, int delta)
    {
        uint value = BitConverter.ToUInt32(buffer, (int)position);
        if (value != 0)
            WriteU32(buffer, position, (uint)(value + delta));
    }

    private static void WriteU32(byte[] buffer, long position, uint value)
        => BitConverter.TryWriteBytes(buffer.AsSpan((int)position, 4), value);

    private static void WriteU16(byte[] buffer, long position, ushort value)
        => BitConverter.TryWriteBytes(buffer.AsSpan((int)position, 2), value);

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
}
