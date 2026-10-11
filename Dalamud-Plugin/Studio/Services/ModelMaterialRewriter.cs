using System;
using System.Collections.Generic;
using System.IO;
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

        if (!changed)
            return data;

        // ── The string block, laid out the way the game's own files are ──────
        var blob = new List<byte>();
        var attributeOffsets = Append(blob, mdl.Attributes);
        var boneOffsets      = Append(blob, mdl.Bones);
        var materialOffsets  = Append(blob, materials);
        var shapeOffsets     = Append(blob, mdl.ShapeNames);
        int stringCount = mdl.Attributes.Length + mdl.Bones.Length + materials.Length + mdl.ShapeNames.Length;

        // Pad to the block's own 4 bytes, then on until the whole file after it has moved by a
        // multiple of 16 — every alignment further down is measured from the start of the file.
        int size = blob.Count;
        while (size % 4 != 0 || (size - (int)mdl.StringSize) % 16 != 0)
            size++;
        int delta = size - (int)mdl.StringSize;

        var result = new byte[data.Length + delta];
        Array.Copy(data, 0, result, 0, mdl.StringDataPos);
        blob.CopyTo(result, (int)mdl.StringDataPos);          // the padding stays zero
        Array.Copy(data, mdl.ModelDataPos, result, mdl.ModelDataPos + delta, data.Length - mdl.ModelDataPos);

        // RuntimeSize covers the string block onwards, so it moves with it; StackSize does not.
        WriteU32(result, 8, (uint)(mdl.RuntimeSize + delta));
        WriteU16(result, mdl.StringHeaderPos, (ushort)stringCount);
        WriteU32(result, mdl.StringHeaderPos + 4, (uint)size);

        // Everything past the string block moved: the file header's vertex/index offsets…
        for (int i = 0; i < 3; i++)
        {
            Shift(result, 16 + i * 4, delta);
            Shift(result, 28 + i * 4, delta);
        }

        // …and the same offsets repeated per LOD, plus its edge geometry.
        for (int lod = 0; lod < 3; lod++)
        {
            long at = mdl.LodsPos + delta + lod * LodStructSize;
            Shift(result, at + LodEdgeGeometryDataOffset, delta);
            Shift(result, at + LodVertexDataOffset, delta);
            Shift(result, at + LodIndexDataOffset, delta);
        }

        WriteOffsets(result, mdl.AttributeOffsetsPos + delta, attributeOffsets);
        WriteOffsets(result, mdl.MaterialOffsetsPos + delta, materialOffsets);
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
            br.ReadByte();                      // LodCount
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
            ms.Position += 3L * LodStructSize;
            if ((flags2 & 0x10) != 0)
                ms.Position += 3L * ExtraLodStructSize;
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
