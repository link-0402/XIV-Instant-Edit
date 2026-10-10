using System;
using System.IO;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>Bone/skeleton metadata read directly from a vanilla .mdl's raw bytes.</summary>
public sealed class VanillaModelInfo
{
    public uint Version;
    public float Radius;
    public float ModelClipOutDistance;
    public float ShadowClipOutDistance;
    public VanillaElementId[] ElementIds = Array.Empty<VanillaElementId>();
    public VanillaBoneTable[] BoneTables = Array.Empty<VanillaBoneTable>();
    public string[] BoneNames = Array.Empty<string>();

    /// <summary>The material each mesh uses, as the model references it (e.g. "/mt_c0101h0005_hir_a.mtrl").</summary>
    public string[] MaterialNames = Array.Empty<string>();

    public VanillaBoundingBox BoundingBoxes;
    public VanillaBoundingBox ModelBoundingBoxes;
    public VanillaBoundingBox WaterBoundingBoxes;
    public VanillaBoundingBox VerticalFogBoundingBoxes;
    public VanillaBoundingBox[] BoneBoundingBoxes = Array.Empty<VanillaBoundingBox>();
}

public readonly struct VanillaElementId
{
    public readonly uint ElementId;
    public readonly uint ParentBoneName;
    public readonly float[] Translate;
    public readonly float[] Rotate;
    public VanillaElementId(uint elementId, uint parentBoneName, float[] translate, float[] rotate)
    { ElementId = elementId; ParentBoneName = parentBoneName; Translate = translate; Rotate = rotate; }
}

/// <summary>
/// One bone table: the bones a mesh may reference, as indices into the model's bone list.
/// V5 stores a fixed 64-entry array per table, V6 a header pointing at a packed array, so only
/// the used entries are kept here and the writer lays them out to suit the version.
/// </summary>
public readonly struct VanillaBoneTable
{
    public readonly ushort[] BoneIndex;
    public ushort BoneCount => (ushort)BoneIndex.Length;
    public VanillaBoneTable(ushort[] boneIndex) { BoneIndex = boneIndex; }
}

public readonly struct VanillaBoundingBox
{
    public readonly float[] Min;
    public readonly float[] Max;
    public VanillaBoundingBox(float[] min, float[] max) { Min = min; Max = max; }
}

/// <summary>
/// Reads just enough of a vanilla .mdl's header to rebuild a dummy model from it —
/// bypassing Lumina's typed <c>MdlFile</c> reader entirely.
///
/// That reader throws <c>EndOfStreamException</c> on every single retail model
/// tested against the currently installed Dalamud/Lumina build, in
/// <c>VertexDeclarationStruct.Read</c>: it always adds the first structure it reads
/// to the element list before checking whether it's the Stream==255 terminator, so
/// a declaration whose very first slot is already the terminator (any declaration
/// with zero real vertex streams) makes it read one structure too many and drifts
/// out of alignment for the rest of the file. This reader never touches vertex
/// declarations at all — it jumps straight past them using the file header's own
/// declared size — so it isn't affected.
///
/// It also does not parse Shapes/ShapeMeshes/ShapeValues/SubmeshBoneMap: those sit
/// between the bone tables and the bounding boxes with a byte layout this reader
/// could not reproduce from the documented struct sizes alone (something between
/// them still doesn't add up against real files — possibly another one-off
/// somewhere in that stretch, possibly a genuine per-patch format change). Since
/// none of that is needed to build a placeholder model, this reader skips directly
/// from the bone tables to the bounding boxes using the file's own declared
/// model-data block size instead of parsing through the gap.
/// </summary>
public static class VanillaModelReader
{
    /// <summary>Model file versions: V6 is what Dawntrail ships, V5 the older layout.</summary>
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;

    private const int FileHeaderSize = 68;
    private const int ModelHeaderSize = 56;
    private const int LodStructSize = 60;
    private const int ExtraLodStructSize = 40;   // 20 ushorts, per Lumina's ExtraLodStruct
    private const int ElementIdStructSize = 32;
    private const int MeshStructSize = 36;
    private const int TerrainShadowMeshStructSize = 20;
    private const int SubmeshStructSize = 16;
    private const int TerrainShadowSubmeshStructSize = 12;
    private const int BoneTableStructSize = 132;
    private const int BoundingBoxStructSize = 32;

    public static VanillaModelInfo Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        // ── File header ──────────────────────────────────────────────────
        uint version = br.ReadUInt32();
        uint stackSize = br.ReadUInt32();   // size of the vertex-declarations block only.
        uint runtimeSize = br.ReadUInt32(); // size of the ModelHeader-onward model-data block.

        // ── String block: sits between vertex declarations and ModelHeader,
        //    and (unlike vertex declarations) is NOT counted in stackSize. ──
        long stringHeaderOffset = FileHeaderSize + stackSize;
        ms.Position = stringHeaderOffset;
        br.ReadUInt16(); // StringCount
        br.ReadUInt16(); // padding
        uint stringSize = br.ReadUInt32();
        var strings = br.ReadBytes((int)stringSize);
        long modelHeaderOffset = stringHeaderOffset + 8 + stringSize;

        // ── ModelHeader (56 bytes) ───────────────────────────────────────
        ms.Position = modelHeaderOffset;
        float radius = br.ReadSingle();
        ushort meshCount = br.ReadUInt16();
        ushort attributeCount = br.ReadUInt16();
        ushort submeshCount = br.ReadUInt16();
        ushort materialCount = br.ReadUInt16();
        ushort boneCount = br.ReadUInt16();
        ushort boneTableCount = br.ReadUInt16();
        br.ReadUInt16();                    // ShapeCount
        br.ReadUInt16();                    // ShapeMeshCount
        br.ReadUInt16();                    // ShapeValueCount
        br.ReadByte();                      // LodCount
        br.ReadByte();                      // Flags1 (unused)
        ushort elementIdCount = br.ReadUInt16();
        byte terrainShadowMeshCount = br.ReadByte();
        byte flags2 = br.ReadByte();
        float modelClipOutDistance = br.ReadSingle();
        float shadowClipOutDistance = br.ReadSingle();
        br.ReadUInt16();                    // Unknown4
        ushort terrainShadowSubmeshCount = br.ReadUInt16();
        br.ReadBytes(4);                    // Unknown5, BGChangeMaterialIndex, BGCrestChangeMaterialIndex, Unknown6
        br.ReadBytes(6);                    // Unknown7/8/9
        br.ReadBytes(6);                    // Padding

        bool extraLodEnabled = (flags2 & 0x10) != 0;

        // ── ElementIds ────────────────────────────────────────────────────
        var elementIds = new VanillaElementId[elementIdCount];
        for (int i = 0; i < elementIdCount; i++)
        {
            uint elementId = br.ReadUInt32();
            uint parentBoneName = br.ReadUInt32();
            var translate = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            var rotate = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            elementIds[i] = new VanillaElementId(elementId, parentBoneName, translate, rotate);
        }

        // ── Skip everything mesh-related we don't need, up to bone data. ─
        br.ReadBytes(3 * LodStructSize);
        if (extraLodEnabled)
            br.ReadBytes(3 * ExtraLodStructSize);
        br.ReadBytes(meshCount * MeshStructSize);
        br.ReadBytes(attributeCount * 4);
        br.ReadBytes(terrainShadowMeshCount * TerrainShadowMeshStructSize);
        br.ReadBytes(submeshCount * SubmeshStructSize);
        br.ReadBytes(terrainShadowSubmeshCount * TerrainShadowSubmeshStructSize);
        var materialNameOffsets = new uint[materialCount];
        for (int i = 0; i < materialCount; i++)
            materialNameOffsets[i] = br.ReadUInt32();

        // ── BoneNameOffsets + BoneTables ─────────────────────────────────
        var boneNameOffsets = new uint[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneNameOffsets[i] = br.ReadUInt32();

        var boneTables = new VanillaBoneTable[boneTableCount];
        for (int i = 0; i < boneTableCount; i++)
        {
            if (version >= V6)
            {
                // V6: a 4-byte header per table, with the index array stored after all headers.
                // The offset counts 4-byte units from this header's own position.
                long headerPos = ms.Position;
                ushort offset = br.ReadUInt16();
                ushort size   = br.ReadUInt16();
                long after    = ms.Position;

                ms.Position = headerPos + offset * 4;
                var indices = new ushort[size];
                for (int k = 0; k < size; k++) indices[k] = br.ReadUInt16();
                boneTables[i] = new VanillaBoneTable(indices);

                ms.Position = after;
            }
            else
            {
                // V5: 64 indices then the count, used or not.
                var indices = new ushort[64];
                for (int k = 0; k < 64; k++) indices[k] = br.ReadUInt16();
                uint tableBoneCount = br.ReadUInt32();
                boneTables[i] = new VanillaBoneTable(indices[..(int)Math.Min(tableBoneCount, 64u)]);
            }
        }

        // ── Jump straight to the bounding boxes, skipping Shapes/ShapeMeshes/
        //    ShapeValues/SubmeshBoneMap — not needed, and not reliably skippable
        //    from here (see class doc). RuntimeSize covers everything from the string
        //    block to the last bounding box, so the boxes sit at its very end.
        long modelDataEnd = FileHeaderSize + stackSize + runtimeSize;
        long boundingBoxStart = modelDataEnd - (4 + boneCount) * (long)BoundingBoxStructSize;
        ms.Position = boundingBoxStart;

        VanillaBoundingBox ReadBox()
        {
            var min = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            var max = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            return new VanillaBoundingBox(min, max);
        }

        var boundingBoxes = ReadBox();
        var modelBoundingBoxes = ReadBox();
        var waterBoundingBoxes = ReadBox();
        var verticalFogBoundingBoxes = ReadBox();
        var boneBoundingBoxes = new VanillaBoundingBox[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneBoundingBoxes[i] = ReadBox();

        // ── Resolve bone name strings ────────────────────────────────────
        var boneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneNames[i] = ReadCString(strings, boneNameOffsets[i]);

        var materialNames = new string[materialCount];
        for (int i = 0; i < materialCount; i++)
            materialNames[i] = ReadCString(strings, materialNameOffsets[i]);

        return new VanillaModelInfo
        {
            Version = version,
            Radius = radius,
            ModelClipOutDistance = modelClipOutDistance,
            ShadowClipOutDistance = shadowClipOutDistance,
            ElementIds = elementIds,
            BoneTables = boneTables,
            BoneNames = boneNames,
            MaterialNames = materialNames,
            BoundingBoxes = boundingBoxes,
            ModelBoundingBoxes = modelBoundingBoxes,
            WaterBoundingBoxes = waterBoundingBoxes,
            VerticalFogBoundingBoxes = verticalFogBoundingBoxes,
            BoneBoundingBoxes = boneBoundingBoxes,
        };
    }

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
}
