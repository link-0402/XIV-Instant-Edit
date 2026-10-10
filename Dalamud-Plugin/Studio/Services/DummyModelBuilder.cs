using System;
using System.Collections.Generic;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Builds a placeholder .mdl for editing in Blender (e.g. via InstantEdit): one
/// empty mesh group per configured material, each with a single empty part
/// already assigned to that material, so the mesh/material wiring is correct
/// before any real geometry exists. The skeleton (bones, bone tables, element
/// IDs, bounding boxes) is carried over unchanged from the vanilla model so the
/// result still binds to the right rig.
///
/// File layout is [FileHeader(68)][VertexDeclarations][StringBlock][ModelHeader
/// onward][vertex/index buffers]. The vertex-declarations block's size is what
/// the file header calls "StackSize"; "RuntimeSize" covers everything after it up
/// to the end of the bounding boxes, the string block included.
///
/// Two version-dependent details matter, both checked against Penumbra's own MdlFile
/// reader/writer, which is what validates a built mod:
/// bone tables are a fixed 64-entry array plus a count in V5, but a header pointing at a
/// packed array in V6 (what Dawntrail ships), with the total array length repeated in the
/// model header; and a vertex declaration must start with a real element, since the reader
/// takes its first entry unconditionally and only then looks for the 255 terminator.
/// </summary>
public static class DummyModelBuilder
{
    private const int FileHeaderSize = 68;
    private const int ModelHeaderSize = 56;
    private const int LodStructSize = 60;
    private const int ElementIdStructSize = 32;
    private const int MeshStructSize = 36;
    private const int SubmeshStructSize = 16;
    private const int BoneTableStructSize = 132;
    private const int BoundingBoxStructSize = 32;

    public static byte[] Build(VanillaModelInfo vanilla, IReadOnlyList<string> materialNames)
    {
        if (materialNames.Count == 0)
            throw new ArgumentException("At least one material is required.", nameof(materialNames));

        int meshCount = materialNames.Count;

        // ── Strings: our new material names, then the original bone names ──────
        // Order follows the game's own files: attributes (none here), bones, then materials.
        var stringsBlob = new List<byte>();
        var boneOffsets = new uint[vanilla.BoneNames.Length];
        for (int i = 0; i < vanilla.BoneNames.Length; i++)
        {
            boneOffsets[i] = (uint)stringsBlob.Count;
            AppendCString(stringsBlob, vanilla.BoneNames[i]);
        }

        var materialOffsets = new uint[meshCount];
        for (int i = 0; i < meshCount; i++)
        {
            materialOffsets[i] = (uint)stringsBlob.Count;
            AppendCString(stringsBlob, Reference(materialNames[i]));
        }

        while (stringsBlob.Count % 4 != 0)
            stringsBlob.Add(0);

        int stringCount = meshCount + vanilla.BoneNames.Length;

        // ── Vertex declarations: one minimal block per mesh. This is the file
        //    header's "StackSize" section; the strings that follow are not part
        //    of it (they count towards RuntimeSize instead). ────────────────
        var vertexInfo = new List<byte>();
        for (int i = 0; i < meshCount; i++)
            AppendMinimalVertexDeclaration(vertexInfo);

        var stringSection = new List<byte>();
        WriteU16(stringSection, (ushort)stringCount);
        WriteU16(stringSection, 0); // padding
        WriteU32(stringSection, (uint)stringsBlob.Count);
        stringSection.AddRange(stringsBlob);

        // ── Model data block ─────────────────────────────────────────────────
        var model = new List<byte>();

        // ModelHeader
        WriteF32(model, vanilla.Radius);
        WriteU16(model, (ushort)meshCount);                 // MeshCount
        WriteU16(model, 0);                                 // AttributeCount
        WriteU16(model, (ushort)meshCount);                 // SubmeshCount
        WriteU16(model, (ushort)meshCount);                 // MaterialCount
        WriteU16(model, (ushort)vanilla.BoneNames.Length);  // BoneCount
        WriteU16(model, (ushort)vanilla.BoneTables.Length); // BoneTableCount
        WriteU16(model, 0);                                 // ShapeCount
        WriteU16(model, 0);                                 // ShapeMeshCount
        WriteU16(model, 0);                                 // ShapeValueCount
        model.Add(1);                                       // LodCount
        model.Add(0);                                       // Flags1
        WriteU16(model, (ushort)vanilla.ElementIds.Length); // ElementIdCount
        model.Add(0);                                       // TerrainShadowMeshCount
        model.Add(0);                                       // Flags2 (ExtraLodEnabled off, etc.)
        WriteF32(model, vanilla.ModelClipOutDistance);
        WriteF32(model, vanilla.ShadowClipOutDistance);
        WriteU16(model, 0);                                 // Unknown4
        WriteU16(model, 0);                                 // TerrainShadowSubmeshCount
        model.Add(0);                                       // Unknown5
        model.Add(0);                                       // BGChangeMaterialIndex
        model.Add(0);                                       // BGCrestChangeMaterialIndex
        model.Add(0);                                       // Unknown6
        WriteU16(model, BoneTableArrayTotal(vanilla));      // BoneTableArrayCountTotal (V6; 0 in V5)
        WriteU16(model, 0);                                 // Unknown8
        WriteU16(model, 0);                                 // Unknown9
        model.AddRange(new byte[6]);                        // Padding

        // ElementIds — unchanged from vanilla (attach points aren't mesh-dependent).
        foreach (var e in vanilla.ElementIds)
        {
            WriteU32(model, e.ElementId);
            WriteU32(model, e.ParentBoneName);
            foreach (var v in e.Translate) WriteF32(model, v);
            foreach (var v in e.Rotate) WriteF32(model, v);
        }

        // The offset every "no geometry here" pointer converges on: right after
        // this whole model-data block, since the vertex/index buffers are empty.
        uint emptyBufferOffset = (uint)(FileHeaderSize + vertexInfo.Count + stringSection.Count
                                      + ModelDataBlockLength(vanilla, meshCount, FileHeaderSize + vertexInfo.Count + stringSection.Count));

        // Lods (always exactly 3 slots; only the first is populated).
        WriteLod(model, meshIndex: 0, meshCount: (ushort)meshCount, dataOffset: emptyBufferOffset);
        WriteLod(model, meshIndex: 0, meshCount: 0, dataOffset: emptyBufferOffset);
        WriteLod(model, meshIndex: 0, meshCount: 0, dataOffset: emptyBufferOffset);

        // Meshes: one empty part per material, in order.
        for (int i = 0; i < meshCount; i++)
        {
            WriteU16(model, 0);           // VertexCount
            WriteU16(model, 0);           // Padding
            WriteU32(model, 0);           // IndexCount
            WriteU16(model, (ushort)i);   // MaterialIndex
            WriteU16(model, (ushort)i);   // SubMeshIndex
            WriteU16(model, 1);           // SubMeshCount
            WriteU16(model, 0);           // BoneTableIndex (table 0 always exists if there are any bones)
            WriteU32(model, 0);           // StartIndex
            WriteU32(model, 0); WriteU32(model, 0); WriteU32(model, 0); // VertexBufferOffset x3
            model.Add(0); model.Add(0); model.Add(0);                  // VertexBufferStride x3
            model.Add(0);                                              // VertexStreamCount
        }

        // No attributes, no terrain shadow meshes.

        // Submeshes: one per mesh, all empty.
        for (int i = 0; i < meshCount; i++)
        {
            WriteU32(model, 0); // IndexOffset
            WriteU32(model, 0); // IndexCount
            WriteU32(model, 0); // AttributeIndexMask
            WriteU16(model, 0); // BoneStartIndex
            WriteU16(model, 0); // BoneCount
        }

        // No terrain shadow submeshes.

        foreach (var o in materialOffsets) WriteU32(model, o);
        foreach (var o in boneOffsets) WriteU32(model, o);

        // Bone tables — the same bones as vanilla, in the layout this version uses.
        WriteBoneTables(model, vanilla);

        // No shapes.

        // SubmeshBoneMap: empty (byte-length prefix of 0, no data), then the padding byte
        // that aligns the bounding boxes to 8, with the game's filler pattern.
        WriteU32(model, 0);
        byte padding = Padding(FileHeaderSize + vertexInfo.Count + stringSection.Count + model.Count + 1);
        model.Add(padding);
        for (int i = 0; i < padding; i++)
            model.Add((byte)(0xDEADBEEFF00DCAFEul >> (8 * (7 - i))));

        // Bounding boxes — unchanged from vanilla; harmless with no geometry and
        // keeps anything that eyeballs the model's extents from seeing a degenerate box.
        WriteBoundingBox(model, vanilla.BoundingBoxes);
        WriteBoundingBox(model, vanilla.ModelBoundingBoxes);
        WriteBoundingBox(model, vanilla.WaterBoundingBoxes);
        WriteBoundingBox(model, vanilla.VerticalFogBoundingBoxes);
        // One per bone: that is the count a reader derives from the header, whatever the source
        // model happened to carry.
        for (int i = 0; i < vanilla.BoneNames.Length; i++)
            WriteBoundingBox(model, i < vanilla.BoneBoundingBoxes.Length ? vanilla.BoneBoundingBoxes[i] : default);

        // ── File header ──────────────────────────────────────────────────────
        var header = new List<byte>();
        WriteU32(header, vanilla.Version);
        WriteU32(header, (uint)vertexInfo.Count);   // StackSize: vertex declarations only.
        // RuntimeSize covers the strings and the model-data block: everything between the
        // vertex declarations and the (empty) vertex/index buffers.
        WriteU32(header, (uint)(stringSection.Count + model.Count));
        WriteU16(header, (ushort)meshCount);        // VertexDeclarationCount
        WriteU16(header, (ushort)meshCount);        // MaterialCount
        for (int i = 0; i < 3; i++) WriteU32(header, emptyBufferOffset); // VertexOffset
        for (int i = 0; i < 3; i++) WriteU32(header, emptyBufferOffset); // IndexOffset
        for (int i = 0; i < 3; i++) WriteU32(header, 0);                // VertexBufferSize
        for (int i = 0; i < 3; i++) WriteU32(header, 0);                // IndexBufferSize
        header.Add(1); // LodCount
        header.Add(1); // EnableIndexBufferStreaming
        header.Add(0); // EnableEdgeGeometry
        header.Add(0); // Padding

        var result = new byte[header.Count + vertexInfo.Count + stringSection.Count + model.Count];
        header.CopyTo(result, 0);
        vertexInfo.CopyTo(result, header.Count);
        stringSection.CopyTo(result, header.Count + vertexInfo.Count);
        model.CopyTo(result, header.Count + vertexInfo.Count + stringSection.Count);
        return result;
    }

    /// <summary>
    /// Computes the model-data block length up front, so Lod offsets can point past it.
    /// <paramref name="blockStart"/> is the file offset the block begins at, which the alignment
    /// padding before the bounding boxes depends on.
    /// </summary>
    private static int ModelDataBlockLength(VanillaModelInfo vanilla, int meshCount, int blockStart)
    {
        int beforePadding = ModelHeaderSize
         + vanilla.ElementIds.Length * ElementIdStructSize
         + 3 * LodStructSize
         + meshCount * MeshStructSize
         + meshCount * SubmeshStructSize
         + meshCount * 4                          // material name offsets
         + vanilla.BoneNames.Length * 4            // bone name offsets
         + BoneTablesLength(vanilla)
         + 4;                                      // empty submesh bone map length prefix

        int padding = Padding(blockStart + beforePadding + 1);
        return beforePadding
         + 1 + padding                             // padding-amount byte and its filler
         + 4 * BoundingBoxStructSize
         + vanilla.BoneNames.Length * BoundingBoxStructSize;
    }

    /// <summary>Bytes the bone tables take: fixed-size entries in V5, headers plus packed arrays in V6.</summary>
    private static int BoneTablesLength(VanillaModelInfo vanilla)
        => vanilla.Version >= VanillaModelReader.V6
            ? vanilla.BoneTables.Length * 4 + BoneTableArrayTotal(vanilla) * 2
            : vanilla.BoneTables.Length * BoneTableStructSize;

    /// <summary>Total length of the V6 bone index arrays, in entries, each table padded to an even count.</summary>
    private static ushort BoneTableArrayTotal(VanillaModelInfo vanilla)
    {
        if (vanilla.Version < VanillaModelReader.V6)
            return 0;
        int total = 0;
        foreach (var table in vanilla.BoneTables)
            total += (table.BoneCount + 1) / 2 * 2;
        return (ushort)total;
    }

    /// <summary>
    /// V5 writes 64 indices and a count per table. V6 writes a (offset, count) header per table,
    /// the offset counting 4-byte units from that header, with the arrays packed after all headers.
    /// </summary>
    private static void WriteBoneTables(List<byte> model, VanillaModelInfo vanilla)
    {
        if (vanilla.Version < VanillaModelReader.V6)
        {
            foreach (var table in vanilla.BoneTables)
            {
                for (int i = 0; i < 64; i++)
                    WriteU16(model, i < table.BoneIndex.Length ? table.BoneIndex[i] : (ushort)0);
                WriteU32(model, table.BoneCount);
            }
            return;
        }

        int start = model.Count;
        int headerBytes = vanilla.BoneTables.Length * 4;
        var arrays = new List<byte>();
        for (int i = 0; i < vanilla.BoneTables.Length; i++)
        {
            var table = vanilla.BoneTables[i];
            // Distance from this header to where its array starts, in 4-byte units.
            int fromHeader = headerBytes - i * 4 + arrays.Count;
            WriteU16(model, (ushort)(fromHeader / 4));
            WriteU16(model, table.BoneCount);

            foreach (var index in table.BoneIndex)
                WriteU16(arrays, index);
            if ((table.BoneCount & 1) == 1)
                WriteU16(arrays, 0);
        }
        model.AddRange(arrays);
        System.Diagnostics.Debug.Assert(model.Count - start == BoneTablesLength(vanilla));
    }

    /// <summary>The game aligns the bounding boxes to 8 bytes; this is the filler that gets it there.</summary>
    private static byte Padding(long positionAfterPaddingByte)
    {
        var padding = (byte)(positionAfterPaddingByte & 0b111);
        return padding > 0 ? (byte)(8 - padding) : (byte)0;
    }

    private static void WriteLod(List<byte> b, ushort meshIndex, ushort meshCount, uint dataOffset)
    {
        WriteU16(b, meshIndex);
        WriteU16(b, meshCount);
        WriteF32(b, 0f);   // ModelLodRange
        WriteF32(b, 100f); // TextureLodRange
        WriteU16(b, 0); WriteU16(b, 0); // WaterMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // ShadowMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // TerrainShadowMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // VerticalFogMeshIndex/Count
        WriteU32(b, 0); // EdgeGeometrySize
        WriteU32(b, 0); // EdgeGeometryDataOffset
        WriteU32(b, 0); // PolygonCount
        WriteU32(b, 0); // Unknown1
        WriteU32(b, 0); // VertexBufferSize
        WriteU32(b, 0); // IndexBufferSize
        WriteU32(b, dataOffset); // VertexDataOffset
        WriteU32(b, dataOffset); // IndexDataOffset
    }

    private static void WriteBoundingBox(List<byte> b, VanillaBoundingBox box)
    {
        foreach (var v in box.Min) WriteF32(b, v);
        foreach (var v in box.Max) WriteF32(b, v);
    }

    /// <summary>
    /// The smallest declaration a reader accepts: one position element, then the terminator,
    /// then empty slots up to the fixed 17. A declaration whose first slot is already the
    /// terminator desyncs Lumina's reader (it always takes the first entry), which is what makes
    /// Penumbra fail to parse the file.
    /// </summary>
    private static void AppendMinimalVertexDeclaration(List<byte> b)
    {
        // Stream 0, offset 0, type Single3, usage Position, usage index 0, 3 bytes padding.
        b.Add(0); b.Add(0); b.Add(2); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0);
        // Terminator.
        b.Add(255); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0);
        for (int i = 2; i < 17; i++)
            for (int k = 0; k < 8; k++)
                b.Add(0);
    }

    /// <summary>
    /// A material name as a model references it. The game's own files store "/mt_name.mtrl", where the
    /// leading slash means "in this model's own material folder"; without it the game has nowhere to
    /// resolve the name from.
    /// </summary>
    private static string Reference(string name)
        => name.StartsWith('/') ? name : "/" + name;

    private static void AppendCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.UTF8.GetBytes(s ?? string.Empty));
        b.Add(0);
    }

    private static void WriteU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteF32(List<byte> b, float v) => b.AddRange(BitConverter.GetBytes(v));
}
