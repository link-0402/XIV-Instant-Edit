using System.Buffers.Binary;

namespace InstantEdit.Services.Materials;

/// <summary>
/// What a model gives the scroll shader for one material: how many of its meshes use the material, how
/// many of those have no second UV map, and why a second one can't be added (null when it can).
/// </summary>
internal sealed record Uv2Check(int Meshes, int WithoutUv2, string? Blocked)
{
    /// <summary>Some meshes lack the second UV map and one can be added.</summary>
    public bool CanAdd => WithoutUv2 > 0 && Blocked == null;
}

/// <summary>
/// The scroll shader (characterscroll.shpk) draws its pattern along the mesh's second UV map: its pixel
/// shader samples the catchlight texture at TEXCOORD0.zw times the layer's scale plus the time offset. A
/// mesh whose UV element holds only two values gives 0, 0 there, so the whole part shows one texel and
/// flashes as the pattern moves past. <see cref="Add"/> widens those meshes' UV element to four values
/// with the second map a copy of the first, and moves the geometry after it to make room.
/// </summary>
internal static class ModelUv2
{
    private const uint V6 = 0x01000006;
    private const int FileHeaderSize = 68;
    private const int DeclarationSize = 17 * 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const byte UsageUv = 4;
    private const byte TypeSingle2 = 1, TypeSingle4 = 3, TypeHalf2 = 13, TypeHalf4 = 14;

    private sealed record Layout(int LodCount, int LodTable, int MeshTable, int MeshCount, int DeclarationCount, IReadOnlyList<string> Materials,
        int TablesEnd);

    /// <summary>The meshes of <paramref name="bytes"/> that use <paramref name="materialFileName"/> (such as mt_c0201e0079_top_a.mtrl), and their second UV maps.</summary>
    public static Uv2Check Check(byte[] bytes, string materialFileName)
    {
        Layout layout;
        List<int> meshes;
        int without;
        try
        {
            layout = Read(bytes);
            meshes = Targets(bytes, layout, materialFileName).ToList();
            without = meshes.Count(m => UvElement(bytes, m) is { Type: TypeHalf2 or TypeSingle2 });
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return new Uv2Check(0, 0, e.Message); }
        // A damaged table that points past the file.
        catch (Exception e) when (e is ArgumentException or OverflowException) { return new Uv2Check(0, 0, "The model's tables are damaged."); }
        if (without == 0)
            return new Uv2Check(meshes.Count, 0, null);
        if (WhyNot(bytes, layout, meshes) is { } blocked)
            return new Uv2Check(meshes.Count, without, blocked);
        // Some layouts only show while moving the geometry (shared vertex data), so a trial run decides.
        try { Add(bytes, materialFileName); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or OverflowException) { return new Uv2Check(meshes.Count, without, e.Message); }
        catch (ArgumentException) { return new Uv2Check(meshes.Count, without, "The model's tables are damaged."); }
        return new Uv2Check(meshes.Count, without, null);
    }

    /// <summary>A copy of <paramref name="bytes"/> whose meshes using <paramref name="materialFileName"/> have a second UV map, copied from the first.</summary>
    public static byte[] Add(byte[] bytes, string materialFileName)
    {
        var layout = Read(bytes);
        var targets = Targets(bytes, layout, materialFileName).Where(m => UvElement(bytes, m) is { Type: TypeHalf2 or TypeSingle2 }).ToHashSet();
        if (targets.Count == 0)
            throw new InvalidOperationException("Every mesh with this material already has a second UV map.");
        if (WhyNot(bytes, layout, targets) is { } blocked)
            throw new NotSupportedException(blocked);

        var lodVertex = new byte[layout.LodCount][];
        var newStreamOffsets = new Dictionary<(int Mesh, int Stream), uint>();
        for (var l = 0; l < layout.LodCount; l++)
            lodVertex[l] = RebuildVertexBuffer(bytes, layout, l, targets, newStreamOffsets);

        // The data region: each LOD's vertex then index buffer, in file order; the gaps between them stay as they were.
        var regions = new List<(long Offset, long Size, int Lod, bool Vertex)>();
        for (var l = 0; l < layout.LodCount; l++)
        {
            regions.Add((U32(bytes, 16 + l * 4), U32(bytes, 40 + l * 4), l, true));
            regions.Add((U32(bytes, 28 + l * 4), U32(bytes, 52 + l * 4), l, false));
        }
        // Empty buffers first at a shared offset, so they start there rather than inside the buffer after them.
        regions.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset)
            : (a.Size == 0) != (b.Size == 0) ? (a.Size == 0 ? -1 : 1)
            : a.Vertex == b.Vertex ? a.Lod.CompareTo(b.Lod) : a.Vertex ? -1 : 1);
        var start = regions[0].Offset;
        if (start < layout.TablesEnd)
            throw new InvalidDataException("The model's geometry starts inside its tables.");
        if (start > bytes.Length)
            throw new InvalidDataException("A buffer runs past the end of the model.");
        var output = new List<byte>(bytes.Length + targets.Count * 1024);
        output.AddRange(bytes.AsSpan(0, (int)start).ToArray());
        var cursor = start;
        var newOffsets = new Dictionary<(int Lod, bool Vertex), uint>();
        foreach (var region in regions)
        {
            if (region.Offset < cursor)
            {
                // Two buffers at one offset only happens for empty ones.
                if (region.Size != 0 && region.Offset + region.Size > cursor)
                    throw new InvalidDataException("The model's buffers overlap.");
                newOffsets[(region.Lod, region.Vertex)] = checked((uint)(output.Count - (cursor - region.Offset)));
                continue;
            }
            if (region.Offset + region.Size > bytes.Length)
                throw new InvalidDataException("A buffer runs past the end of the model.");
            output.AddRange(bytes.AsSpan((int)cursor, (int)(region.Offset - cursor)).ToArray());
            newOffsets[(region.Lod, region.Vertex)] = checked((uint)output.Count);
            output.AddRange(region.Vertex ? lodVertex[region.Lod] : bytes.AsSpan((int)region.Offset, (int)region.Size).ToArray());
            cursor = region.Offset + region.Size;
        }
        output.AddRange(bytes.AsSpan((int)cursor).ToArray());
        var result = output.ToArray();

        for (var l = 0; l < layout.LodCount; l++)
        {
            var vertexAt = newOffsets[(l, true)];
            var indexAt = newOffsets[(l, false)];
            var vertexSize = (uint)lodVertex[l].Length;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16 + l * 4), vertexAt);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28 + l * 4), indexAt);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40 + l * 4), vertexSize);
            var lod = layout.LodTable + l * LodSize;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(lod + 44), vertexSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(lod + 52), vertexAt);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(lod + 56), indexAt);
        }
        foreach (var ((mesh, stream), offset) in newStreamOffsets)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(layout.MeshTable + mesh * MeshSize + 20 + stream * 4), offset);
        foreach (var mesh in targets)
        {
            var uv = UvElement(bytes, mesh)!.Value;
            var width = Width(uv.Type);
            result[layout.MeshTable + mesh * MeshSize + 32 + uv.Stream] = checked((byte)(bytes[layout.MeshTable + mesh * MeshSize + 32 + uv.Stream] + width));
            var declaration = FileHeaderSize + mesh * DeclarationSize;
            for (var e = 0; e < 17; e++)
            {
                var at = declaration + e * 8;
                if (result[at] == 0xFF)
                    break;
                if (e == uv.Index)
                    result[at + 2] = uv.Type == TypeHalf2 ? TypeHalf4 : TypeSingle4;
                else if (result[at] == uv.Stream && result[at + 1] > uv.Offset)
                    result[at + 1] = checked((byte)(result[at + 1] + width));
            }
        }
        return result;
    }

    /// <summary>One LOD's vertex buffer with the target meshes' UV streams widened; records where each mesh stream now starts.</summary>
    private static byte[] RebuildVertexBuffer(byte[] bytes, Layout layout, int lod, HashSet<int> targets, Dictionary<(int Mesh, int Stream), uint> offsets)
    {
        var bufferStart = U32(bytes, 16 + lod * 4);
        var bufferSize = U32(bytes, 40 + lod * 4);
        if (bufferStart + (long)bufferSize > bytes.Length)
            throw new InvalidDataException("A vertex buffer runs past the end of the model.");
        var at = layout.LodTable + lod * LodSize;
        int first = U16(bytes, at), count = U16(bytes, at + 2);
        var chunks = new List<(uint Offset, uint Size, int Mesh, int Stream)>();
        for (var m = first; m < first + count; m++)
        {
            var mesh = layout.MeshTable + m * MeshSize;
            int vertices = U16(bytes, mesh);
            // The streams the declaration reads; some tools write a wrong stream count byte.
            foreach (var s in Streams(bytes, m, layout.DeclarationCount))
                if (vertices > 0 && bytes[mesh + 32 + s] > 0)
                    chunks.Add((U32(bytes, mesh + 20 + s * 4), (uint)(vertices * bytes[mesh + 32 + s]), m, s));
        }
        chunks.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var output = new List<byte>((int)bufferSize + targets.Count * 4096);
        var cursor = 0u;
        var placed = new Dictionary<uint, (uint Size, bool Widened, uint At)>();
        foreach (var chunk in chunks)
        {
            var widen = targets.Contains(chunk.Mesh) && UvElement(bytes, chunk.Mesh)!.Value.Stream == chunk.Stream;
            if (chunk.Offset + (long)chunk.Size > bufferSize)
                throw new InvalidDataException("A mesh's vertices run past its LOD's vertex buffer.");
            if (placed.TryGetValue(chunk.Offset, out var shared))
            {
                // Two meshes reading the same vertices: fine while neither is widened.
                if (shared.Size != chunk.Size || shared.Widened || widen)
                    throw new NotSupportedException("Two meshes share vertex data, so one of them can't get a second UV map alone.");
                offsets[(chunk.Mesh, chunk.Stream)] = shared.At;
                continue;
            }
            if (chunk.Offset < cursor)
                throw new InvalidDataException("Two meshes' vertices overlap.");
            output.AddRange(bytes.AsSpan((int)(bufferStart + cursor), (int)(chunk.Offset - cursor)).ToArray());
            var newAt = checked((uint)output.Count);
            offsets[(chunk.Mesh, chunk.Stream)] = newAt;
            placed[chunk.Offset] = (chunk.Size, widen, newAt);
            var source = bytes.AsSpan((int)(bufferStart + chunk.Offset), (int)chunk.Size);
            if (widen)
                output.AddRange(Widen(source, bytes, layout, chunk.Mesh));
            else
                output.AddRange(source.ToArray());
            cursor = chunk.Offset + chunk.Size;
        }
        output.AddRange(bytes.AsSpan((int)(bufferStart + cursor), (int)(bufferSize - cursor)).ToArray());
        // The buffer grows by a multiple of 16 bytes (zeros at its end, inside its size), so the buffers
        // after it keep the 16-byte alignment the game's files have, as ModelMaterialRewriter keeps it.
        while ((output.Count - bufferSize) % 16 != 0)
            output.Add(0);
        // Streams with no data (empty meshes, unused slots) move with the bytes before them.
        var growth = placed.Where(p => p.Value.Widened).Select(p => (Offset: p.Key, Grown: Widened(bytes, layout, chunks, p.Key) - p.Value.Size)).ToList();
        for (var m = first; m < first + count; m++)
            for (var s = 0; s < 3; s++)
                if (!offsets.ContainsKey((m, s)))
                {
                    var old = U32(bytes, layout.MeshTable + m * MeshSize + 20 + s * 4);
                    offsets[(m, s)] = checked((uint)(old + growth.Where(g => g.Offset < old).Sum(g => (long)g.Grown)));
                }
        return output.ToArray();
    }

    /// <summary>The size a widened chunk at <paramref name="offset"/> has now.</summary>
    private static uint Widened(byte[] bytes, Layout layout, List<(uint Offset, uint Size, int Mesh, int Stream)> chunks, uint offset)
    {
        var chunk = chunks.First(c => c.Offset == offset && c.Size > 0);
        var uv = UvElement(bytes, chunk.Mesh)!.Value;
        int stride = bytes[layout.MeshTable + chunk.Mesh * MeshSize + 32 + uv.Stream];
        return (uint)(chunk.Size / stride * (stride + Width(uv.Type)));
    }

    /// <summary>A mesh stream with each vertex's UV values written twice: first map, then the copy as the second.</summary>
    private static byte[] Widen(ReadOnlySpan<byte> stream, byte[] bytes, Layout layout, int mesh)
    {
        var uv = UvElement(bytes, mesh)!.Value;
        var width = Width(uv.Type);
        int stride = bytes[layout.MeshTable + mesh * MeshSize + 32 + uv.Stream];
        if (stride + width > byte.MaxValue)
            throw new NotSupportedException("The mesh's vertices are too wide for a second UV map.");
        if (uv.Offset + width > stride)
            throw new InvalidDataException("The mesh's UV values run past its vertex size.");
        var vertices = stream.Length / stride;
        var output = new byte[vertices * (stride + width)];
        var head = uv.Offset + width;
        for (var v = 0; v < vertices; v++)
        {
            var from = stream.Slice(v * stride, stride);
            var to = output.AsSpan(v * (stride + width), stride + width);
            from[..head].CopyTo(to);
            from.Slice(uv.Offset, width).CopyTo(to[head..]);
            from[head..].CopyTo(to[(head + width)..]);
        }
        return output;
    }

    /// <summary>Why a second UV map can't be added to <paramref name="meshes"/>, or null when it can.</summary>
    private static string? WhyNot(byte[] bytes, Layout layout, IEnumerable<int> meshes)
    {
        for (var l = 0; l < layout.LodCount; l++)
        {
            var lod = layout.LodTable + l * LodSize;
            for (var field = 12; field < 28; field += 4)
                if (U16(bytes, lod + field + 2) != 0)
                    return "The model has water, shadow or fog meshes, which this can't move yet.";
            if (U32(bytes, lod + 28) != 0)
                return "The model has edge geometry, which this can't move yet.";
        }
        foreach (var mesh in meshes)
        {
            if (LodOf(bytes, layout, mesh) < 0)
                return "A mesh with this material is outside the model's LODs.";
            if (UvElement(bytes, mesh) is not { } uv)
                return "A mesh with this material has no UV map.";
            if (uv.Type is not (TypeHalf2 or TypeSingle2 or TypeHalf4 or TypeSingle4))
                return "A mesh with this material stores its UV map in a form this can't widen.";
        }
        return null;
    }

    /// <summary>The vertex streams (0 to 2) the mesh's declaration reads.</summary>
    private static IEnumerable<int> Streams(byte[] bytes, int mesh, int declarationCount)
    {
        if (mesh >= declarationCount)
            yield break;
        var seen = new bool[3];
        var declaration = FileHeaderSize + mesh * DeclarationSize;
        for (var e = 0; e < 17; e++)
        {
            var at = declaration + e * 8;
            if (bytes[at] == 0xFF)
                break;
            if (bytes[at] > 2)
                throw new InvalidDataException("A mesh reads a vertex stream past the third.");
            seen[bytes[at]] = true;
        }
        for (var s = 0; s < 3; s++)
            if (seen[s])
                yield return s;
    }

    private static int LodOf(byte[] bytes, Layout layout, int mesh)
    {
        for (var l = 0; l < layout.LodCount; l++)
        {
            var lod = layout.LodTable + l * LodSize;
            int first = U16(bytes, lod), count = U16(bytes, lod + 2);
            if (mesh >= first && mesh < first + count)
                return l;
        }
        return -1;
    }

    private readonly record struct UvInfo(int Index, byte Stream, byte Offset, byte Type);

    /// <summary>The mesh's first UV element (usage index 0): where it is in the declaration and its stream, offset and type.</summary>
    private static UvInfo? UvElement(byte[] bytes, int mesh)
    {
        var declaration = FileHeaderSize + mesh * DeclarationSize;
        for (var e = 0; e < 17; e++)
        {
            var at = declaration + e * 8;
            if (bytes[at] == 0xFF)
                break;
            if (bytes[at + 3] == UsageUv && bytes[at + 4] == 0)
                return new UvInfo(e, bytes[at], bytes[at + 1], bytes[at + 2]);
        }
        return null;
    }

    private static int Width(byte type) => type == TypeHalf2 ? 4 : 8;

    private static IEnumerable<int> Targets(byte[] bytes, Layout layout, string materialFileName)
    {
        for (var m = 0; m < Math.Min(layout.MeshCount, layout.DeclarationCount); m++)
        {
            int material = U16(bytes, layout.MeshTable + m * MeshSize + 8);
            if (U16(bytes, layout.MeshTable + m * MeshSize) == 0 || material >= layout.Materials.Count)
                continue;
            var name = layout.Materials[material].Replace('\\', '/');
            if (string.Equals(name[(name.LastIndexOf('/') + 1)..], materialFileName, StringComparison.OrdinalIgnoreCase))
                yield return m;
        }
    }

    private static Layout Read(byte[] bytes)
    {
        Require(bytes.Length >= FileHeaderSize, "file header");
        if (U32(bytes, 0) != V6)
            throw new NotSupportedException("Only Dawntrail (V6) models are supported.");
        int declarationCount = U16(bytes, 12);
        int lodCount = bytes[64];
        if (lodCount is < 1 or > 3)
            throw new InvalidDataException("The model has no valid LOD count.");
        var stringHeader = FileHeaderSize + declarationCount * DeclarationSize;
        Require(stringHeader + 8 <= bytes.Length, "vertex declarations");
        var stringBlock = stringHeader + 8;
        Require(stringBlock + (long)U32(bytes, stringHeader + 4) <= bytes.Length, "string table");
        var stringSize = (int)U32(bytes, stringHeader + 4);
        var meshHeader = stringBlock + stringSize;
        Require(meshHeader + MeshHeaderSize <= bytes.Length, "mesh header");
        int meshCount = U16(bytes, meshHeader + 4);
        int attributeCount = U16(bytes, meshHeader + 6);
        int submeshCount = U16(bytes, meshHeader + 8);
        int materialCount = U16(bytes, meshHeader + 10);
        int elementIdCount = U16(bytes, meshHeader + 24);
        int terrainShadowMeshCount = bytes[meshHeader + 26];
        var flags2 = bytes[meshHeader + 27];
        int terrainShadowSubmeshCount = U16(bytes, meshHeader + 38);
        if (terrainShadowMeshCount > 0)
            throw new NotSupportedException("The model has terrain shadow meshes, which this can't move yet.");
        var cursor = meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize;
        var lodTable = cursor;
        cursor += 3 * LodSize;
        Require(cursor <= bytes.Length, "LOD table");
        if ((flags2 & 0x10) != 0)
        {
            Require(cursor + 3 * ExtraLodSize <= bytes.Length, "extra LOD table");
            // Extra LODs list light shaft, glass, material change and crest change meshes.
            for (var l = 0; l < 3; l++)
                for (var field = 0; field < 8; field += 2)
                    if (U16(bytes, cursor + l * ExtraLodSize + field * 2 + 2) != 0)
                        throw new NotSupportedException("The model has extra meshes (glass, light shafts or crest changes), which this can't move yet.");
            cursor += 3 * ExtraLodSize;
        }
        var meshTable = cursor;
        cursor += meshCount * MeshSize;
        cursor += attributeCount * 4;
        cursor += submeshCount * 16;
        cursor += terrainShadowSubmeshCount * 12;
        var materialTable = cursor;
        cursor += materialCount * 4;
        Require(cursor <= bytes.Length && declarationCount * DeclarationSize + FileHeaderSize <= bytes.Length, "mesh tables");
        var materials = new List<string>(materialCount);
        for (var i = 0; i < materialCount; i++)
        {
            var raw = U32(bytes, materialTable + i * 4);
            Require(raw < (uint)stringSize, "material name");
            var offset = (int)raw;
            var end = Array.IndexOf(bytes, (byte)0, stringBlock + offset, stringSize - offset);
            materials.Add(System.Text.Encoding.UTF8.GetString(bytes, stringBlock + offset, (end < 0 ? stringBlock + stringSize : end) - stringBlock - offset));
        }
        return new Layout(lodCount, lodTable, meshTable, meshCount, declarationCount, materials, cursor);
    }

    private static void Require(bool condition, string part)
    {
        if (!condition)
            throw new InvalidDataException($"The model's {part} is cut short.");
    }

    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
}
