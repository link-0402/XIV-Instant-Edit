using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>A parsed GEOM mesh: per-vertex channels plus the triangle index list.</summary>
public sealed class SimsGeomMesh
{
    public uint Version;

    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals   = Array.Empty<Vector3>();

    /// <summary>UV channels in declaration order; channel 0 is the one that maps to the diffuse texture.</summary>
    public List<Vector2[]> UvChannels = new();

    /// <summary>Per-vertex bone indices into the bone list, four per vertex.</summary>
    public byte[]? BoneIndices;

    /// <summary>Per-vertex bone weights, four per vertex, normalised to 0..1.</summary>
    public float[]? BoneWeights;

    /// <summary>Per-vertex RGBA colour, or null when the mesh carries none.</summary>
    public byte[]? Colors;

    /// <summary>Triangle list — three indices per face.</summary>
    public int[] Indices = Array.Empty<int>();

    /// <summary>
    /// FNV-32 hashes of the bone names, when the trailing bone block could be located
    /// and validated. Empty otherwise — see <see cref="BoneCount"/>.
    /// </summary>
    public uint[] BoneHashes = Array.Empty<uint>();

    /// <summary>
    /// How many distinct bones the vertex data actually references. This is derived
    /// from the weights themselves, so it holds even when <see cref="BoneHashes"/>
    /// could not be read.
    /// </summary>
    public int BoneCount;

    public int VertexCount => Positions.Length;
    public int FaceCount   => Indices.Length / 3;
    public bool HasSkin    => BoneIndices != null && BoneWeights != null && BoneCount > 0;
}

/// <summary>
/// Parser for the Sims 4 GEOM resource (type 0x015A1849).
///
/// In a package the GEOM chunk is wrapped in an RCOL container, so the resource does
/// not start with the GEOM tag — the container has to be unwrapped first.
///
/// The chunk itself has no fixed vertex layout. It declares a table of vertex elements,
/// each one a usage, a sub-type and a byte size, and the vertex data is that table
/// repeated per vertex. So the reader walks the declared table for every vertex and
/// uses each element's declared byte size as the stride for anything it does not
/// recognise, which keeps unknown or future usages from throwing the parse off.
///
/// Everything up to and including the faces is self-checking: the strides and counts
/// have to land exactly on the face block for the parse to make sense. What follows —
/// two variable blocks and the bone name hashes — is only documented for version 0x0C,
/// and version 0x0F (which current content uses) clearly lays it out differently. The
/// hashes are cosmetic, used only to name vertex groups, so the tail is parsed on a
/// best-effort basis and validated against the resource's own TGI offset; when it does
/// not line up the hashes are dropped rather than guessed at, and the exporter falls
/// back to numbering the bones.
/// </summary>
public static class SimsGeom
{
    private const uint GeomMagic = 0x4D4F4547;   // "GEOM"

    // Vertex element usages.
    private const uint UsagePosition   = 1;
    private const uint UsageNormal     = 2;
    private const uint UsageUv         = 3;
    private const uint UsageBoneAssign = 4;
    private const uint UsageWeights    = 5;
    private const uint UsageTangent    = 6;
    private const uint UsageColor      = 7;

    /// <summary>
    /// Most UV channels accepted. Game meshes carry one or two; each channel is a full
    /// per-vertex array, so an uncapped count lets a tiny forged GEOM allocate gigabytes.
    /// </summary>
    internal const int MaxUvChannels = 4;

    public static SimsGeomMesh Parse(byte[] resource)
    {
        int chunkStart = FindGeomChunk(resource);

        using var ms = new MemoryStream(resource, writable: false);
        using var br = new BinaryReader(ms);
        ms.Position = chunkStart;

        if (br.ReadUInt32() != GeomMagic)
            throw new InvalidDataException("Not a GEOM resource.");

        // Versions 0x0C through 0x0F all share the same header, vertex and face layout
        // and differ only in the trailing blocks, which are read defensively below.
        var mesh = new SimsGeomMesh { Version = br.ReadUInt32() };
        if (mesh.Version != 0x05 && mesh.Version is < 0x0C or > 0x0F)
            throw new InvalidDataException($"Unsupported GEOM version 0x{mesh.Version:X2}.");

        uint tgiOffset = br.ReadUInt32();
        // The offset is measured from the end of the field itself.
        long tgiPosition = ms.Position + tgiOffset;
        br.ReadUInt32();                                  // TGI list size

        uint embeddedId = br.ReadUInt32();
        if (embeddedId != 0)
        {
            // An embedded MTNF material block — skipped wholesale via its own size field.
            uint chunkSize = br.ReadUInt32();
            ms.Position += chunkSize;
        }

        br.ReadUInt32();                                  // merge group
        br.ReadUInt32();                                  // sort order

        int vertexCount  = br.ReadInt32();
        int elementCount = br.ReadInt32();
        if (vertexCount < 0 || vertexCount > 4_000_000 || elementCount < 0 || elementCount > 64)
            throw new InvalidDataException("GEOM declares an implausible vertex or element count.");

        var elements = new (uint Usage, uint SubType, byte Size)[elementCount];
        for (int i = 0; i < elementCount; i++)
            elements[i] = (br.ReadUInt32(), br.ReadUInt32(), br.ReadByte());

        CheckVertexLayout(elements, vertexCount, ms.Length - ms.Position);
        ReadVertexData(br, mesh, elements, vertexCount);
        ReadFaces(br, mesh);
        mesh.BoneHashes = TryReadBoneHashes(br, ms, mesh.Version, tgiPosition);

        return mesh;
    }

    /// <summary>
    /// Locates the GEOM chunk inside its RCOL container. The container lists its
    /// chunks with an explicit position and size, so the tag is never searched for
    /// blindly; a resource that already starts with the tag is used as-is.
    /// </summary>
    private static int FindGeomChunk(byte[] resource)
    {
        if (resource.Length < 4)
            throw new InvalidDataException("Resource is too small to hold a mesh.");

        if (BitConverter.ToUInt32(resource, 0) == GeomMagic)
            return 0;

        using var ms = new MemoryStream(resource, writable: false);
        using var br = new BinaryReader(ms);

        uint version = br.ReadUInt32();
        if (version != 3)
            throw new InvalidDataException("Not a GEOM resource.");

        br.ReadUInt32();                                  // public chunk count
        br.ReadUInt32();                                  // unused
        int externalCount = br.ReadInt32();
        int internalCount = br.ReadInt32();
        if (externalCount < 0 || internalCount <= 0 || externalCount > 4096 || internalCount > 4096)
            throw new InvalidDataException("RCOL container declares an implausible chunk count.");

        ms.Position += (long)(internalCount + externalCount) * 16;   // resource key lists

        for (int i = 0; i < internalCount; i++)
        {
            int position = br.ReadInt32();
            br.ReadInt32();                               // size
            if (position >= 0 && position + 4 <= resource.Length &&
                BitConverter.ToUInt32(resource, position) == GeomMagic)
                return position;
        }

        throw new InvalidDataException("RCOL container holds no GEOM chunk.");
    }

    /// <summary>
    /// Checks the declared vertex table against the bytes that are actually there, before
    /// any per-vertex array is allocated: every element needs a non-zero size at least as
    /// big as what is read from it, the UV channels are capped, and the vertex count times
    /// the stride has to fit in what is left of the resource.
    /// </summary>
    private static void CheckVertexLayout((uint Usage, uint SubType, byte Size)[] elements, int vertexCount, long remaining)
    {
        long stride = 0;
        int uvChannels = 0;
        foreach (var e in elements)
        {
            if (e.Size == 0)
                throw new InvalidDataException($"GEOM declares a zero-size vertex element (usage {e.Usage}).");
            if (e.Size < BytesRead(e))
                throw new InvalidDataException($"GEOM vertex element (usage {e.Usage}) is {e.Size} bytes, too small for its data.");
            if (e.Usage == UsageUv && ++uvChannels > MaxUvChannels)
                throw new InvalidDataException($"GEOM declares more than {MaxUvChannels} UV channels.");
            stride += e.Size;
        }

        if (vertexCount > 0 && stride == 0)
            throw new InvalidDataException("GEOM declares vertices but no vertex elements.");
        if ((long)vertexCount * stride > remaining)
            throw new InvalidDataException("GEOM vertex data runs past the end of the resource.");
    }

    /// <summary>Bytes <see cref="ReadVertexData"/> reads from one element of one vertex.</summary>
    private static int BytesRead((uint Usage, uint SubType, byte Size) e) => e.Usage switch
    {
        UsagePosition or UsageNormal => 12,
        UsageUv                      => 8,
        UsageBoneAssign              => 4,
        UsageWeights                 => e.SubType == 1 && e.Size >= 16 ? 16 : 4,
        UsageColor                   => 4,
        _                            => 0,
    };

    private static void ReadVertexData(BinaryReader br, SimsGeomMesh mesh,
        (uint Usage, uint SubType, byte Size)[] elements, int vertexCount)
    {
        int uvChannelCount = 0;
        foreach (var e in elements)
            if (e.Usage == UsageUv)
                uvChannelCount++;

        mesh.Positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        bool hasNormals = false;

        var uvs = new List<Vector2[]>(uvChannelCount);
        for (int i = 0; i < uvChannelCount; i++)
            uvs.Add(new Vector2[vertexCount]);

        byte[]?  boneIndices = null;
        float[]? boneWeights = null;
        byte[]?  colors      = null;

        for (int v = 0; v < vertexCount; v++)
        {
            int uvChannel = 0;

            foreach (var element in elements)
            {
                long next = br.BaseStream.Position + element.Size;

                switch (element.Usage)
                {
                    case UsagePosition:
                        mesh.Positions[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        break;

                    case UsageNormal:
                        normals[v] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        hasNormals = true;
                        break;

                    case UsageUv:
                        if (uvChannel < uvs.Count)
                            uvs[uvChannel][v] = new Vector2(br.ReadSingle(), br.ReadSingle());
                        uvChannel++;
                        break;

                    case UsageBoneAssign:
                        boneIndices ??= new byte[vertexCount * 4];
                        for (int i = 0; i < 4; i++)
                            boneIndices[v * 4 + i] = br.ReadByte();
                        break;

                    case UsageWeights:
                        boneWeights ??= new float[vertexCount * 4];
                        // Sub-type 1 stores floats; everything else stores normalised bytes.
                        if (element.SubType == 1 && element.Size >= 16)
                        {
                            for (int i = 0; i < 4; i++)
                                boneWeights[v * 4 + i] = br.ReadSingle();
                        }
                        else
                        {
                            for (int i = 0; i < 4; i++)
                                boneWeights[v * 4 + i] = br.ReadByte() / 255f;
                        }
                        break;

                    case UsageColor:
                        colors ??= new byte[vertexCount * 4];
                        for (int i = 0; i < 4; i++)
                            colors[v * 4 + i] = br.ReadByte();
                        break;

                    case UsageTangent:
                    default:
                        break;   // not needed downstream — skipped by the stride below
                }

                br.BaseStream.Position = next;
            }
        }

        if (hasNormals)
            mesh.Normals = normals;
        mesh.UvChannels  = uvs;
        mesh.BoneIndices = boneIndices;
        mesh.BoneWeights = boneWeights;
        mesh.Colors      = colors;
        mesh.BoneCount   = CountBones(boneIndices, boneWeights);
    }

    /// <summary>
    /// Derives the bone count from the vertex data, counting only bones that carry
    /// weight — a zero-weighted slot keeps whatever index happened to be there.
    /// </summary>
    private static int CountBones(byte[]? indices, float[]? weights)
    {
        if (indices == null || weights == null)
            return 0;

        int highest = -1;
        for (int i = 0; i < indices.Length && i < weights.Length; i++)
            if (weights[i] > 0f && indices[i] > highest)
                highest = indices[i];

        return highest + 1;
    }

    private static void ReadFaces(BinaryReader br, SimsGeomMesh mesh)
    {
        int itemCount = br.ReadInt32();
        if (itemCount < 0 || itemCount > 1024)
            throw new InvalidDataException("GEOM declares an implausible face-block count.");

        var indices = new List<int>();

        for (int i = 0; i < itemCount; i++)
        {
            byte bytesPerIndex = br.ReadByte();
            int  indexCount    = br.ReadInt32();
            if (indexCount < 0)
                throw new InvalidDataException("GEOM declares a negative face-point count.");
            if (bytesPerIndex is not (1 or 2 or 4))
                throw new InvalidDataException($"GEOM uses an unsupported index size of {bytesPerIndex} bytes.");
            if ((long)indexCount * bytesPerIndex > br.BaseStream.Length - br.BaseStream.Position)
                throw new InvalidDataException("GEOM face data runs past the end of the resource.");

            for (int f = 0; f < indexCount; f++)
            {
                // A 32-bit index past int.MaxValue wraps negative here; the exporter skips
                // any face whose index is outside the vertex range, negative ones included.
                indices.Add(bytesPerIndex switch
                {
                    1 => br.ReadByte(),
                    2 => br.ReadUInt16(),
                    _ => (int)br.ReadUInt32(),
                });
            }
        }

        mesh.Indices = indices.ToArray();
    }

    /// <summary>
    /// Best-effort read of the bone name hashes that follow the faces.
    ///
    /// Two variable-length blocks sit in between, and their layout is only documented
    /// for version 0x0C. Rather than trusting the walk, the result is checked against
    /// where the resource says its TGI list begins: the bone list ends exactly there,
    /// so a walk that lands anywhere else got the layout wrong. Returns an empty array
    /// in that case, and the caller names the bones by index instead.
    /// </summary>
    private static uint[] TryReadBoneHashes(BinaryReader br, MemoryStream ms, uint version, long tgiPosition)
    {
        try
        {
            if (version == 0x05)
            {
                br.ReadUInt32();                    // skin controller index
            }
            else
            {
                int count1 = br.ReadInt32();
                if (count1 < 0 || count1 > 1_000_000) return Array.Empty<uint>();
                for (int i = 0; i < count1; i++)
                {
                    br.ReadUInt32();
                    int pairCount = br.ReadInt32();
                    if (pairCount < 0 || pairCount > 1_000_000) return Array.Empty<uint>();
                    ms.Position += pairCount * 8L;
                }

                int count2 = br.ReadInt32();
                if (count2 < 0 || count2 > 1_000_000) return Array.Empty<uint>();
                ms.Position += count2 * 6L;
            }

            int boneCount = br.ReadInt32();
            if (boneCount < 0 || boneCount > 4096)
                return Array.Empty<uint>();

            var hashes = new uint[boneCount];
            for (int i = 0; i < boneCount; i++)
                hashes[i] = br.ReadUInt32();

            // The bone list is the last thing before the TGI list; anything else means
            // the walk drifted and these hashes are not hashes.
            return ms.Position == tgiPosition ? hashes : Array.Empty<uint>();
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return Array.Empty<uint>();
        }
    }
}
