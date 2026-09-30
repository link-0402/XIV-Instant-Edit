using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Services.Painter;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The body seams on synthetic parts: an arm tube (the top) and a hand tube (the gloves) meeting in a
/// ten-vertex wrist ring, skin.shpk materials and small uncompressed textures. Covers texture
/// addressing (vanilla's body UVs run from 1 to 2), submesh attributes and shape keys, the vertex
/// writer, the seam search and its findings, the weld, normal, material and texture fixes measured
/// again from the written files, and the preview plan.
/// </summary>
internal static class BodySeamScenarios
{
    private const string TopPath = "chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string GlovePath = "chara/equipment/e0000/model/c0201e0000_glv.mdl";
    private const string SkinPath = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
    private const string OtherSkinPath = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_b.mtrl";
    private static readonly string[] SkinTextures = ["chara/skin_base.tex", "chara/skin_norm.tex", "chara/skin_mask.tex"];
    private const int Ring = 10;
    private const float Radius = 0.03f;
    private const float WristX = 0.45f;
    private const float WristY = 1.0f;

    public static void Run(string testRoot)
    {
        CheckAddressing();
        CheckModelMasks();
        CheckVertexWriter();
        CheckSeams();
        CheckMisses();
        CheckOverlaps();
        CheckSurroundings();
        CheckKeepUnchanged();
        CheckBackups(testRoot);
        CheckCapture(testRoot);
        CheckViews();
    }

    // ---- Textures ------------------------------------------------------------------------------------

    private static void CheckAddressing()
    {
        var rgba = new byte[4 * 4 * 4];
        for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
                rgba[(y * 4 + x) * 4] = (byte)(x * 80);
        var wrap = new SeamImage(4, 4, rgba);
        var mirror = new SeamImage(4, 4, rgba) { AddressU = SeamAddress.Mirror, AddressV = SeamAddress.Mirror };
        var clamp = new SeamImage(4, 4, rgba) { AddressU = SeamAddress.Clamp, AddressV = SeamAddress.Clamp };
        Require(MathF.Abs(wrap.Sample(new Vector2(1.375f, 0.375f)).X - wrap.Sample(new Vector2(0.375f, 0.375f)).X) < 1e-6f &&
                MathF.Abs(mirror.Sample(new Vector2(1.125f, 0.375f)).X - mirror.Sample(new Vector2(0.875f, 0.375f)).X) < 1e-6f &&
                MathF.Abs(clamp.Sample(new Vector2(1.6f, 0.375f)).X - 240 / 255f) < 1e-6f,
            "body seam: textures are read outside 0..1 the way the sampler addresses them (wrap, mirror, clamp)");
        var (a, _, _) = wrap.IntoTile(new Vector2(1.2f, 0.3f), new Vector2(1.3f, 0.3f), new Vector2(1.25f, 0.4f));
        var (m, _, _) = mirror.IntoTile(new Vector2(1.2f, 0.3f), new Vector2(1.3f, 0.3f), new Vector2(1.25f, 0.4f));
        Require(Vector2.Distance(a, new Vector2(0.2f, 0.3f)) < 1e-5f && Vector2.Distance(m, new Vector2(0.8f, 0.3f)) < 1e-5f,
            "body seam: a UV triangle outside 0..1 is moved into the texture's tile the way the sampler reads it");
        var material = SkinMaterial.Read(Mtrl());
        Require(material.FlagsFor(SkinMaterial.DiffuseSampler) == 0x000F8340 &&
                SeamTextures.Decode(TwoTone((10, 20, 30), (40, 50, 60)), material.FlagsFor(SkinMaterial.DiffuseSampler)).AddressU == SeamAddress.Wrap,
            "body seam: sampler flags are read from the material and decoded textures address like their sampler");
    }

    /// <summary> A 32-texel texture whose top half has one colour and bottom half another, alpha 255. </summary>
    private static byte[] TwoTone((byte R, byte G, byte B) top, (byte R, byte G, byte B) bottom, byte alpha = 255)
    {
        const int size = 32;
        var bytes = NeckSeamScenarios.Texture(0, 0, 0, alpha, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var at = 80 + (y * size + x) * 4;
                var colour = y < size / 2 ? top : bottom;
                bytes[at] = colour.B;
                bytes[at + 1] = colour.G;
                bytes[at + 2] = colour.R;
            }
        return bytes;
    }

    private static byte[] Tex(SeamImage image)
    {
        var bytes = NeckSeamScenarios.Texture(0, 0, 0, 0, image.Width);
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            bytes[80 + i] = image.Rgba[i + 2];
            bytes[80 + i + 1] = image.Rgba[i + 1];
            bytes[80 + i + 2] = image.Rgba[i];
            bytes[80 + i + 3] = image.Rgba[i + 3];
        }
        return bytes;
    }

    private static byte[] Mtrl(float tileScale = 100, float tileAlpha = 1, float? normalScale = null)
        => NeckSeamScenarios.Material(SkinTextures, [(SkinMaterial.SkinTypeKey, SkinMaterial.SkinTypeBody)],
            normalScale is { } scale
                ? [(SkinMaterial.TileScale, [tileScale, tileScale]), (SkinMaterial.TileAlpha, [tileAlpha]), (SkinMaterial.NormalScale, [scale])]
                : [(SkinMaterial.TileScale, [tileScale, tileScale]), (SkinMaterial.TileAlpha, [tileAlpha])]);

    // ---- Models --------------------------------------------------------------------------------------

    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

    private sealed record Shape(string Name, (ushort At, ushort Vertex)[] Values);

    /// <summary>
    /// A V6 model with one mesh: vertices (float or half positions, all weight on its first bone),
    /// triangles split into submeshes with attribute masks, attribute names and shape keys.
    /// </summary>
    private static byte[] Model(string material, IReadOnlyList<Vertex> vertices, IReadOnlyList<int> indices,
        IReadOnlyList<(int Start, int Count, uint Attributes)> submeshes, IReadOnlyList<string> attributes, IReadOnlyList<Shape> shapes, bool half = false)
    {
        var stride = half ? 52 : 56;
        var p = half ? 8 : 12;
        int weights = p, blend = p + 4, normal = p + 8, binormal = p + 20, color = p + 24, uv = p + 28;
        var vertexBuffer = new byte[vertices.Count * stride];
        for (var v = 0; v < vertices.Count; v++)
        {
            var at = v * stride;
            var (position, n, texture) = vertices[v];
            if (half)
            {
                BinaryPrimitives.WriteHalfLittleEndian(vertexBuffer.AsSpan(at), (Half)position.X);
                BinaryPrimitives.WriteHalfLittleEndian(vertexBuffer.AsSpan(at + 2), (Half)position.Y);
                BinaryPrimitives.WriteHalfLittleEndian(vertexBuffer.AsSpan(at + 4), (Half)position.Z);
                BinaryPrimitives.WriteHalfLittleEndian(vertexBuffer.AsSpan(at + 6), (Half)1f);
            }
            else
                WriteFloats(vertexBuffer, at, position.X, position.Y, position.Z);
            vertexBuffer[at + weights] = 255;
            WriteFloats(vertexBuffer, at + normal, n.X, n.Y, n.Z);
            vertexBuffer[at + binormal] = 128;
            vertexBuffer[at + binormal + 1] = 255;
            vertexBuffer[at + binormal + 2] = 128;
            vertexBuffer[at + binormal + 3] = 255;
            vertexBuffer[at + color + 1] = 255;
            vertexBuffer[at + color + 2] = 255;
            vertexBuffer[at + color + 3] = 255;
            WriteFloats(vertexBuffer, at + uv, texture.X, texture.Y, texture.X, texture.Y);
        }
        var indexBuffer = indices.SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray();

        var declaration = new byte[136];
        byte[][] elements =
        [
            [0, 0, (byte)(half ? 14 : 2), 0, 0], [0, (byte)weights, 8, 1, 0], [0, (byte)blend, 5, 2, 0], [0, (byte)normal, 2, 3, 0],
            [0, (byte)binormal, 8, 6, 0], [0, (byte)color, 8, 7, 0], [0, (byte)uv, 3, 4, 0],
        ];
        for (var e = 0; e < elements.Length; e++)
            elements[e].CopyTo(declaration, e * 8);
        declaration[elements.Length * 8] = 0xFF;

        var strings = new MemoryStream();
        int Name(string value)
        {
            var offset = (int)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(value + "\0"));
            return offset;
        }
        var attributeOffsets = attributes.Select(Name).ToList();
        int[] boneOffsets = [Name("j_ude_b_l"), Name("j_te_l")];
        var materialOffset = Name(material);
        var shapeOffsets = shapes.Select(s => Name(s.Name)).ToList();
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);

        var block = new MemoryStream();
        var w = new BinaryWriter(block);
        w.Write((ushort)(attributes.Count + 3 + shapes.Count));
        w.Write((ushort)0);
        w.Write((int)strings.Length);
        w.Write(strings.ToArray());
        // Mesh header.
        w.Write(0.1f);
        w.Write((ushort)1); w.Write((ushort)attributes.Count); w.Write((ushort)submeshes.Count); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)1);
        w.Write((ushort)shapes.Count); w.Write((ushort)shapes.Count); w.Write((ushort)shapes.Sum(s => s.Values.Length));
        w.Write((byte)1); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)2); w.Write((ushort)0); w.Write(0u); w.Write(0u);
        var lodPosition = (int)block.Position;
        w.Write(new byte[3 * 60]);
        // Mesh.
        w.Write((ushort)vertices.Count); w.Write((ushort)0); w.Write((uint)indices.Count);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)submeshes.Count); w.Write((ushort)0);
        w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
        w.Write((byte)stride); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
        foreach (var offset in attributeOffsets)
            w.Write((uint)offset);
        foreach (var (start, count, mask) in submeshes)
        {
            w.Write((uint)start); w.Write((uint)count); w.Write(mask); w.Write((ushort)0); w.Write((ushort)2);
        }
        w.Write((uint)materialOffset);
        foreach (var offset in boneOffsets)
            w.Write((uint)offset);
        // One bone table: its indices start right after its header.
        w.Write((ushort)1); w.Write((ushort)2);
        w.Write((ushort)0); w.Write((ushort)1);
        for (var s = 0; s < shapes.Count; s++)
        {
            w.Write((uint)shapeOffsets[s]);
            w.Write((ushort)s); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0);
        }
        var valueOffset = 0;
        foreach (var shape in shapes)
        {
            w.Write(0u); w.Write((uint)shape.Values.Length); w.Write((uint)valueOffset);
            valueOffset += shape.Values.Length;
        }
        foreach (var shape in shapes)
            foreach (var (at, vertex) in shape.Values)
            {
                w.Write(at); w.Write(vertex);
            }
        // Submesh bone map (empty), no neck morphs, no face data.
        w.Write(0u);
        var padding = (int)(8 - (68 + 136 + block.Position + 1) % 8) % 8;
        w.Write((byte)padding);
        w.Write(new byte[padding]);
        w.Write(new byte[(4 + 2) * 32]);
        var runtime = (int)block.Position;
        var dataOffset = 68 + 136 + runtime;

        var lod = new byte[60];
        BinaryPrimitives.WriteUInt16LittleEndian(lod, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(lod.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(lod.AsSpan(44), (uint)vertexBuffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(lod.AsSpan(48), (uint)indexBuffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(lod.AsSpan(52), (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(lod.AsSpan(56), (uint)(dataOffset + vertexBuffer.Length));
        var blockBytes = block.ToArray();
        lod.CopyTo(blockBytes, lodPosition);

        var header = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SkinModel.V6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 136);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)runtime);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(dataOffset + vertexBuffer.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)vertexBuffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)indexBuffer.Length);
        header[64] = 1;
        return [.. header, .. declaration, .. blockBytes, .. vertexBuffer, .. indexBuffer];
    }

    private static void WriteFloats(byte[] bytes, int at, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }

    /// <summary> Vertices of an open tube around the wrist axis: one ten-vertex ring per height, UVs laid out row by row. </summary>
    private static List<Vertex> Tube(float[] heights, float vStart, float vEnd, float uOffset = 0, Func<int, int, float>? radius = null,
        Func<int, int, Vector3>? tilt = null)
    {
        var vertices = new List<Vertex>();
        for (var ring = 0; ring < heights.Length; ring++)
            for (var i = 0; i < Ring; i++)
            {
                var angle = i * MathF.Tau / Ring;
                var radial = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
                var normal = Vector3.Normalize(radial + (tilt?.Invoke(ring, i) ?? Vector3.Zero));
                var position = new Vector3(WristX, heights[ring], 0) + radial * (radius?.Invoke(ring, i) ?? Radius);
                vertices.Add(new Vertex(position, normal, new Vector2(uOffset + 0.1f + 0.8f * i / Ring, vStart + (vEnd - vStart) * ring / (heights.Length - 1))));
            }
        return vertices;
    }

    /// <summary> Two triangles per quad between neighbouring rings, band by band. </summary>
    private static List<int> Quads(int rings, int offset = 0)
    {
        var indices = new List<int>();
        for (var ring = 0; ring + 1 < rings; ring++)
            for (var i = 0; i < Ring; i++)
            {
                int a = offset + ring * Ring + i, b = offset + ring * Ring + (i + 1) % Ring;
                indices.AddRange([a, b, a + Ring, b, b + Ring, a + Ring]);
            }
        return indices;
    }

    private const int Band = Ring * 6;

    /// <summary> The arm: rings from the wrist up, the lowest band behind the atr_hij attribute. </summary>
    private static byte[] Top(float gap = 0.0008f, float uOffset = 0, bool half = false, float y0 = -1, Func<int, int, float>? radius = null)
    {
        var bottom = y0 >= 0 ? y0 : WristY + gap;
        var vertices = Tube([bottom, bottom + 0.04f, bottom + 0.08f], 0.1f, 0.4f, uOffset, radius);
        return Model("/mt_c0201b0001_a.mtrl", vertices, Quads(3), [(0, Band, 1u), (Band, Band, 0u)], ["atr_hij"], [], half);
    }

    /// <summary>
    /// The hand: rings up to the wrist, its wrist-ring normals tilted and, optionally, the wrist ring
    /// moved by a shape key (replacement vertices after the mesh's own). With <paramref name="reach"/>,
    /// one more ring that far up the arm and 3 mm inside it, tucked under the top's skin.
    /// </summary>
    private static byte[] Glove(string material = "/mt_c0201b0001_a.mtrl", Func<int, int, float>? radius = null, float lift = 0, bool tilt = true,
        float reach = 0)
    {
        var heights = reach > 0 ? new[] { WristY - 0.08f, WristY - 0.04f, WristY, WristY + reach } : new[] { WristY - 0.08f, WristY - 0.04f, WristY };
        var vertices = Tube(heights, 0.6f, 0.9f, radius: reach > 0 ? (ring, i) => ring == 3 ? Radius - 0.003f : radius?.Invoke(ring, i) ?? Radius : radius,
            tilt: tilt ? (ring, _) => ring == 2 ? new Vector3(0, 0.1f, 0) : Vector3.Zero : null);
        var indices = Quads(heights.Length);
        var shapes = new List<Shape>();
        if (lift > 0)
        {
            var first = vertices.Count;
            for (var i = 0; i < Ring; i++)
            {
                var original = vertices[2 * Ring + i];
                vertices.Add(original with { Position = original.Position + new Vector3(0, lift, 0) });
            }
            var values = new List<(ushort, ushort)>();
            for (var at = 0; at < indices.Count; at++)
                if (indices[at] >= 2 * Ring)
                    values.Add(((ushort)at, (ushort)(first + indices[at] - 2 * Ring)));
            shapes.Add(new Shape("shpx_wr_test", values.ToArray()));
        }
        return Model(material, vertices, indices, [(0, indices.Count, 0u)], [], shapes);
    }

    private static void CheckModelMasks()
    {
        var top = Top();
        var all = SkinModel.Read(top);
        var hidden = SkinModel.Read(top, attributes: 0);
        Require(all.Attributes.SequenceEqual(["atr_hij"]) && all.Meshes.Single().Triangles.Length == 2 * Band &&
                SkinModel.Read(top, attributes: 1).Meshes.Single().Triangles.Length == 2 * Band &&
                hidden.Meshes.Single().Triangles.Length == Band && hidden.Meshes.Single().Triangles.All(v => v >= Ring),
            "body seam: a submesh is drawn only while all its attributes are enabled, and every submesh counts when the masks are unknown");
        var glove = Glove(lift: 0.001f);
        var plain = SkinModel.Read(glove);
        var shaped = SkinModel.Read(glove, shapes: 1);
        Require(plain.Shapes.SequenceEqual(["shpx_wr_test"]) && plain.Meshes.Single().Triangles.Max() < 3 * Ring &&
                shaped.Meshes.Single().Triangles.Count(v => v >= 3 * Ring) == plain.Meshes.Single().Triangles.Count(v => v >= 2 * Ring),
            "body seam: an enabled shape key swaps its index entries for the replacement vertices");
    }

    private static void CheckVertexWriter()
    {
        foreach (var half in new[] { false, true })
        {
            var bytes = Top(half: half);
            var model = SkinModel.Read(bytes);
            var mesh = model.Meshes.Single();
            var position = new Vector3(0.5f, 1.25f, -0.03f);
            var normal = Vector3.Normalize(new Vector3(0.2f, 0.3f, 0.9f));
            var binormal = Vector3.Normalize(new Vector3(0, 0.9f, -0.3f));
            var written = model.WithVertexChanges([new SkinVertexChange(mesh.MeshIndex, 3, position, normal, binormal)]);
            var reread = SkinModel.Read(written).Meshes.Single();
            Require(written.Length == bytes.Length && mesh.HalfPositions == half &&
                    Vector3.Distance(reread.Positions[3], position) < (half ? 1e-3f : 1e-6f) && Vector3.Distance(reread.Normals[3], normal) < 1e-3f &&
                    Vector3.Distance(reread.Binormals[3], binormal) < 0.02f && reread.BinormalSigns[3] > 0 &&
                    reread.Positions[4] == mesh.Positions[4] && reread.Uv1[3] == mesh.Uv1[3],
                $"body seam: the vertex writer stores positions ({(half ? "half" : "float")}), normals and binormals in their own formats and leaves the rest");
        }
    }

    // ---- Seams ---------------------------------------------------------------------------------------

    private static NeckSeamMaterialInput Skin(string path = SkinPath, byte[]? material = null, byte[]? diffuse = null, byte[]? normal = null)
        => new(path, material ?? Mtrl(), new Dictionary<string, byte[]>
        {
            [SkinTextures[0]] = diffuse ?? TwoTone((180, 150, 130), (150, 120, 100)),
            [SkinTextures[1]] = normal ?? NeckSeamScenarios.Texture(128, 128, 255, 255),
            [SkinTextures[2]] = NeckSeamScenarios.Texture(160, 116, 150, 255),
        });

    private static NeckSeamInput Input(byte[] top, byte[] glove, NeckSeamMaterialInput? topSkin = null, NeckSeamMaterialInput? gloveSkin = null,
        uint? topAttributes = null, uint? gloveShapes = null)
        => new(null,
        [
            new NeckSeamModelInput(TopPath, top, [topSkin ?? Skin()]) { Attributes = topAttributes },
            new NeckSeamModelInput(GlovePath, glove, [gloveSkin ?? Skin()]) { Shapes = gloveShapes },
        ], null);

    private static BodySeamReport Analyze(byte[] top, byte[] glove, NeckSeamMaterialInput? topSkin = null, NeckSeamMaterialInput? gloveSkin = null,
        uint? topAttributes = null, uint? gloveShapes = null)
        => BodySeamAnalyzer.Analyze(Input(top, glove, topSkin, gloveSkin, topAttributes, gloveShapes));

    private static NeckSeamFinding Finding(BodySeam seam, string title) => seam.Findings.Single(f => f.Title == title);

    private static void CheckSeams()
    {
        var input = Input(Top(), Glove());
        var report = BodySeamAnalyzer.Analyze(input);
        var wrists = report.Seam(BodySeamKind.Wrists)!;
        Require(wrists is { Chains.Count: 1 } && wrists.Chains[0] is { Side: "left", Closed: true, A.Length: Ring } && wrists.A.Slot == "top" &&
                wrists.B.Slot == "glv" && report.Seam(BodySeamKind.Waist) is null && report.Notes.ContainsKey(BodySeamKind.Waist) &&
                report.Notes.ContainsKey(BodySeamKind.Ankles),
            "body seam: the top's and the gloves' open skin edges meet in a closed ring at the left wrist; the other seams say why they weren't measured");
        Require(Finding(wrists, "Edge fit") is { Severity: NeckSeamSeverity.Problem, Fix: NeckSeamFixKind.Weld } && wrists.CanWeld &&
                MathF.Abs(wrists.GapMax - 0.0008f) < 1e-4f,
            "body seam: a gap under the weld limit is a problem the weld fixes");
        Require(Finding(wrists, "Vertex normals at the edge") is { Severity: NeckSeamSeverity.Problem, Fix: NeckSeamFixKind.Normals } && wrists.NormalsDiffer &&
                Finding(wrists, "Skin colour at the seam") is { Severity: NeckSeamSeverity.Problem, Fix: NeckSeamFixKind.Textures } &&
                Finding(wrists, "Surface normal at the seam").Severity == NeckSeamSeverity.Ok &&
                wrists.TexturesToBlend.SetEquals([SkinMaterial.DiffuseSampler]) && wrists.Material is null &&
                Finding(wrists, "Skin material").Severity == NeckSeamSeverity.Ok,
            "body seam: tilted edge normals and a colour step between the two UV islands show, the shared material and flat normal maps don't");

        // Vanilla body UVs run from 1 to 2 and the body sampler wraps, so they must read the same texels.
        var vanilla = Analyze(Top(uOffset: 1), Glove()).Seam(BodySeamKind.Wrists)!;
        Require(Finding(vanilla, "Skin colour at the seam").Face == Finding(wrists, "Skin colour at the seam").Face,
            "body seam: UVs from 1 to 2 read the texture as the game's wrapping sampler does");

        // Fix everything halfway, then measure again from the written files.
        var fix = BodySeamFixer.Build(report, new Dictionary<BodySeamKind, BodySeamFixOptions> { [BodySeamKind.Wrists] = new(true, true, true, true) });
        Require(fix.Models.Select(m => m.GamePath).Order().SequenceEqual([GlovePath, TopPath]) && fix.Materials.Count == 0 &&
                fix.Textures.Single() is { Sampler: SkinMaterial.DiffuseSampler, GamePath: "chara/skin_base.tex" } texture &&
                texture.Materials.SequenceEqual([SkinPath]) && fix.Expected[BodySeamKind.Wrists].Any(l => l.StartsWith("Edge gap", StringComparison.Ordinal)),
            "body seam: meeting halfway changes both models and the shared skin texture, and says what it expects");
        var fixedSkin = Skin(diffuse: Tex(fix.Textures.Single().Image));
        var after = Analyze(fix.Models.Single(m => m.GamePath == TopPath).Bytes, fix.Models.Single(m => m.GamePath == GlovePath).Bytes, fixedSkin, fixedSkin)
            .Seam(BodySeamKind.Wrists)!;
        Require(after.GapMax < BodySeamAnalyzer.GapFloor && after.NormalMax < BodySeamAnalyzer.NormalFloor &&
                Finding(after, "Skin colour at the seam").Severity == NeckSeamSeverity.Ok && after.Worst == NeckSeamSeverity.Ok,
            "body seam: measured again from the written files, the edges meet, share their normals and the colour runs on");

        var keepTop = BodySeamFixer.Build(report, new Dictionary<BodySeamKind, BodySeamFixOptions> { [BodySeamKind.Wrists] = new(true, true, false, false, Meet: 0) });
        var onlyGlove = keepTop.Models.Single();
        var measured = Analyze(Top(), onlyGlove.Bytes).Seam(BodySeamKind.Wrists)!;
        Require(onlyGlove.GamePath == GlovePath && measured.GapMax < BodySeamAnalyzer.GapFloor && measured.NormalMax < BodySeamAnalyzer.NormalFloor,
            "body seam: meeting at the first part's side moves only the second part, onto the first part's edge");

        // Half-float positions round to about 1 mm here; the weld puts both edges on a position they can hold.
        var halfTop = Top(half: true, y0: WristY + 1f / 1024);
        var halfReport = Analyze(halfTop, Glove(tilt: false));
        var halfSeam = halfReport.Seam(BodySeamKind.Wrists)!;
        Require(halfSeam.CanWeld && Finding(halfSeam, "Edge fit").Detail.Contains("half floats", StringComparison.Ordinal),
            "body seam: a gap from half-float positions says so");
        var halfFix = BodySeamFixer.Build(halfReport, new Dictionary<BodySeamKind, BodySeamFixOptions> { [BodySeamKind.Wrists] = new(true, false, false, false) });
        var halfAfter = Analyze(halfFix.Models.SingleOrDefault(m => m.GamePath == TopPath)?.Bytes ?? halfTop, halfFix.Models.Single(m => m.GamePath == GlovePath).Bytes)
            .Seam(BodySeamKind.Wrists)!;
        Require(halfAfter.GapMax < BodySeamAnalyzer.GapFloor,
            "body seam: after the weld the half-float edge and the full-precision edge meet exactly");

        // Different skin materials: their settings meet like the neck's.
        var other = Skin(OtherSkinPath, Mtrl(tileScale: 250, tileAlpha: 0.5f));
        var mixed = Analyze(Top(), Glove(material: "/mt_c0201b0001_b.mtrl"), gloveSkin: other).Seam(BodySeamKind.Wrists)!;
        Require(mixed.Material is { Any: true, TileScaleOff: true, TileAlphaOff: true } && mixed.MaterialDiffers &&
                Finding(mixed, "Skin material").Severity == NeckSeamSeverity.Info && mixed.MaterialPathB == OtherSkinPath,
            "body seam: parts with different skin materials compare their detail tile and settings");
        var materialFix = BodySeamFixer.Build(new BodySeamReport { Seams = [mixed], Notes = new Dictionary<BodySeamKind, string>() },
            new Dictionary<BodySeamKind, BodySeamFixOptions> { [BodySeamKind.Wrists] = new(false, false, true, false) },
            new Dictionary<string, byte[]> { [OtherSkinPath] = Mtrl(tileScale: 250, tileAlpha: 0.5f, normalScale: 0.7f) });
        var gloveMaterial = SkinMaterial.Read(materialFix.Materials.Single(m => m.GamePath == OtherSkinPath).Bytes);
        var topMaterial = SkinMaterial.Read(materialFix.Materials.Single(m => m.GamePath == SkinPath).Bytes);
        Require(materialFix.Models.Count == 0 && gloveMaterial.Constant(SkinMaterial.TileScale)[0] < 250 &&
                topMaterial.Constant(SkinMaterial.TileScale)[0] > 100 &&
                MathF.Abs(topMaterial.Constant(SkinMaterial.TileAlpha)[0] - gloveMaterial.Constant(SkinMaterial.TileAlpha)[0]) < 0.01f &&
                gloveMaterial.Constant(SkinMaterial.NormalScale)[0] == 0.7f && topMaterial.Constant(SkinMaterial.NormalScale)[0] == 1f,
            "body seam: meeting halfway changes both materials, building on a given base, until their tile strength matches");

        // The preview plan: both models, the texture and the material that reads it.
        var analysis = new NeckSeamAnalysis(null, new NeckSeamCaptured(input, new Dictionary<string, NeckSeamSource>()), "A", 0, 0)
        {
            Body = report,
        };
        var plan = SkinSeamPreviewPlan.Build(analysis, new SkinSeamFix(null, fix));
        var material = plan.Materials.Single();
        var rewrites = SkinSeamPreviewPlan.Rewrites(material.Bytes, plan.Textures.Select(t => t.GamePath).ToHashSet(), "1a2b3c4d");
        Require(plan.Models.Count == 2 && material.GamePath == SkinPath && material.Kind == "skin material" &&
                plan.PreviewPath(plan.Textures.Single(), "1a2b3c4d") == "chara/skin_base_ns1a2b3c4d.tex" &&
                rewrites.Single() is { Key: "chara/skin_base.tex", Value: "chara/skin_base_ns1a2b3c4d.tex" },
            "body seam: the preview holds both models, the blended texture at a new path and the material pointed at it");
    }

    private static void CheckMisses()
    {
        var apart = Analyze(Top(gap: 0), Glove(radius: (_, _) => 0.036f)).Seam(BodySeamKind.Wrists);
        Require(apart is { Chains.Count: 0, AnyFix: false } && Finding(apart, "Edges meet").Severity == NeckSeamSeverity.Problem,
            "body seam: edges of similar size 6 mm apart don't meet and get no fix");
        var partly = Analyze(Top(gap: 0), Glove(radius: (ring, i) => ring == 2 && i >= 5 ? 0.045f : Radius, tilt: false)).Seam(BodySeamKind.Wrists);
        Require(partly is { Chains.Count: 0, CanWeld: false } && Finding(partly, "Edges meet").Detail.Contains("only meet in places", StringComparison.Ordinal),
            "body seam: edges that meet in places but run apart elsewhere were made for different bodies, so they aren't welded");
        var hidden = Analyze(Top(), Glove(), topAttributes: 0);
        Require(hidden.Seam(BodySeamKind.Wrists) is null && hidden.Notes[BodySeamKind.Wrists].Contains("cover", StringComparison.Ordinal),
            "body seam: skin the game doesn't draw (a hidden attribute) doesn't count");
        var shaped = Analyze(Top(), Glove(lift: 0.0008f, tilt: false), gloveShapes: 1).Seam(BodySeamKind.Wrists)!;
        Require(Finding(shaped, "Edge fit").Severity == NeckSeamSeverity.Ok && !shaped.CanWeld,
            "body seam: an enabled connector shape key moves the edge the way the game draws it");
    }

    // ---- Overlaps, clothing and seam connectors --------------------------------------------------------

    private static void CheckOverlaps()
    {
        // The gloves reach 1 cm up the arm and dive 3 mm under it; the top's edge lies on the gloves' skin.
        var report = Analyze(Top(gap: 0), Glove(reach: 0.01f));
        var wrists = report.Seam(BodySeamKind.Wrists)!;
        Require(wrists is { Chains.Count: 1 } && wrists.Chains[0] is { Overlap: true, Closed: true, A.Length: Ring } chain && chain.Outer == wrists.A &&
                wrists.GapMax < BodySeamAnalyzer.GapFloor && Finding(wrists, "Edge fit") is { Severity: NeckSeamSeverity.Ok } fit &&
                fit.Detail.Contains("rests on the gloves' skin", StringComparison.Ordinal),
            "body seam: an edge lying on the other part's skin is an overlap, not edges 1 cm apart, and no gap can show");
        Require(Finding(wrists, "Vertex normals at the edge").Severity == NeckSeamSeverity.Problem && wrists.NormalsDiffer,
            "body seam: at an overlap the outer edge's normals are compared with the skin under it");

        // The top's edge stands 0.8 mm off the gloves' skin: a step the weld lays flat, moving only the top.
        var raised = Analyze(Top(gap: 0, radius: (ring, _) => ring == 0 ? Radius + 0.0008f : Radius), Glove(reach: 0.01f));
        var step = raised.Seam(BodySeamKind.Wrists)!;
        Require(step.Chains.Single().Overlap && Finding(step, "Edge fit") is { Severity: NeckSeamSeverity.Problem, Fix: NeckSeamFixKind.Weld } && step.CanWeld &&
                MathF.Abs(step.GapMax - 0.0008f) < 1e-4f,
            "body seam: an overlapping edge standing off the skin under it is a step the weld fixes");
        var fix = BodySeamFixer.Build(raised, new Dictionary<BodySeamKind, BodySeamFixOptions> { [BodySeamKind.Wrists] = new(true, true, false, false, Meet: 0.5f) });
        var top = fix.Models.Single();
        var after = Analyze(top.Bytes, Glove(reach: 0.01f)).Seam(BodySeamKind.Wrists)!;
        Require(top.GamePath == TopPath && after.Chains.Single().Overlap && after.GapMax < BodySeamAnalyzer.GapFloor && after.NormalMax < BodySeamAnalyzer.NormalFloor,
            "body seam: the weld lays the outer edge onto the skin under it with that skin's normals, and the part under it stays as it is");
    }

    private const string ConnectorPath = "chara/human/c0201/obj/body/b0002/model/c0201b0002_top.mdl";
    private const string ClothPath = "chara/equipment/e0001/model/c0201e0001_glv.mdl";

    /// <summary> A seam connector: a band of skin behind atr_cn_wrist, rings 1 cm either side of the wrist with the given radii. </summary>
    private static byte[] Connector(float inner, float middle, string material = "/mt_c0201b0001_a.mtrl")
    {
        var vertices = Tube([WristY - 0.01f, WristY, WristY + 0.01f], 0.4f, 0.6f, radius: (ring, _) => ring == 1 ? middle : inner);
        var indices = Quads(3);
        return Model(material, vertices, indices, [(0, indices.Count, 1u)], ["atr_cn_wrist"], []);
    }

    /// <summary> A sleeve: a clothing tube 1 cm outside the skin, from 5 cm below the wrist to 5 cm above it. </summary>
    private static byte[] Sleeve()
    {
        var vertices = Tube([WristY - 0.05f, WristY, WristY + 0.05f], 0.1f, 0.9f, radius: (_, _) => Radius + 0.01f);
        var indices = Quads(3);
        return Model("/mt_c0201e0001_glv_a.mtrl", vertices, indices, [(0, indices.Count, 0u)], [], []);
    }

    private static void CheckSurroundings()
    {
        Require(BodySeamAnalyzer.SlotOf(ConnectorPath) is null && BodySeamAnalyzer.SlotOf(TopPath) == "top" && SeamSurroundings.IsHumanBodyModel(ConnectorPath) &&
                !SeamSurroundings.IsHumanBodyModel(TopPath),
            "body seam: a human body model named like a top (the game's seam connectors, its low-poly body) is never a part");
        var band = SkinModel.Read(Connector(Radius - 0.003f, Radius)).Meshes.Single();
        Require(band.Triangles.Length == 0 && band.Connectors["wrist"].Length == 2 * Band,
            "body seam: a connector's submesh isn't drawn skin; it is listed by the seam it joins");

        NeckSeamInput With(params NeckSeamModelInput[] more)
        {
            var input = Input(Top(), Glove());
            return input with { Bodies = [.. more, .. input.Bodies] };
        }
        NeckSeamModelInput ConnectorInput(byte[] bytes, uint? attributes = null, string skin = SkinPath)
            => new(ConnectorPath, bytes, [Skin(skin)]) { Attributes = attributes };

        // Listed first, so a pick by order would take it for the top.
        var filled = BodySeamAnalyzer.Analyze(With(ConnectorInput(Connector(Radius - 0.003f, Radius)))).Seam(BodySeamKind.Wrists)!;
        Require(filled.A.ModelPath == TopPath && Finding(filled, "Seam connector") is { Severity: NeckSeamSeverity.Info, Face: "Loaded" } connector &&
                connector.Detail.Contains("fills the gap", StringComparison.Ordinal) && Finding(filled, "Edge fit").Severity == NeckSeamSeverity.Warning && filled.CanWeld,
            "body seam: a connector under a 0.8 mm gap fills it with skin, so the gap is a warning instead of a problem");
        var notDrawn = BodySeamAnalyzer.Analyze(With(ConnectorInput(Connector(Radius - 0.003f, Radius), attributes: 0))).Seam(BodySeamKind.Wrists)!;
        Require(Finding(notDrawn, "Seam connector") is { Face: "Not drawn", Severity: NeckSeamSeverity.Info } && Finding(notDrawn, "Edge fit").Severity == NeckSeamSeverity.Problem,
            "body seam: a connector the game doesn't draw fills nothing");
        var otherSkin = BodySeamAnalyzer.Analyze(With(ConnectorInput(Connector(Radius - 0.003f, Radius, "/mt_c0201b0001_b.mtrl"), skin: OtherSkinPath))).Seam(BodySeamKind.Wrists)!;
        Require(Finding(otherSkin, "Seam connector") is { Severity: NeckSeamSeverity.Warning } other && other.Detail.Contains("differently coloured", StringComparison.Ordinal),
            "body seam: a connector with another skin material shows differently coloured skin in the gap");
        var poking = BodySeamAnalyzer.Analyze(With(ConnectorInput(Connector(Radius + 0.002f, Radius + 0.002f)))).Seam(BodySeamKind.Wrists)!;
        Require(Finding(poking, "Seam connector") is { Severity: NeckSeamSeverity.Problem } poke && poke.Detail.Contains("sticks out", StringComparison.Ordinal),
            "body seam: a connector sticking 2 mm out of the skin shows as a band of skin");

        var sleeved = BodySeamAnalyzer.Analyze(Input(Top(), Glove()) with { Clothing = [new NeckSeamModelInput(ClothPath, Sleeve(), [])] })
            .Seam(BodySeamKind.Wrists)!;
        Require(sleeved.Findings[0] is { Title: "Covered by clothing", Severity: NeckSeamSeverity.Info } && sleeved.Worst == NeckSeamSeverity.Info &&
                sleeved.Chains[0].Covered >= BodySeamAnalyzer.HiddenShare && sleeved.CanWeld &&
                NeckSeamViews.BodySummary(sleeved).StartsWith("Clothing covers the wrists", StringComparison.Ordinal) &&
                NeckSeamViews.TabLabel("Wrists", sleeved.Findings) == "Wrists###skin-seam-Wrists",
            "body seam: a seam a sleeve closes in around doesn't show, so its findings are notes, and the fixes stay available");
        var hiddenPoke = BodySeamAnalyzer.Analyze(With(ConnectorInput(Connector(Radius + 0.002f, Radius + 0.002f))) with { Clothing = [new NeckSeamModelInput(ClothPath, Sleeve(), [])] })
            .Seam(BodySeamKind.Wrists)!;
        Require(Finding(hiddenPoke, "Seam connector").Severity <= NeckSeamSeverity.Info,
            "body seam: a connector sticking out under clothing doesn't show");
    }

    /// <summary> A TEX with its mip chain: uncompressed pixels of one value, or BC7 blocks of one byte. </summary>
    private static byte[] Tex(uint format, int size, int mips, byte fill)
    {
        var bc = format != 0x1450;
        var sizes = Enumerable.Range(0, mips).Select(m => Math.Max(1, size >> m))
            .Select(s => bc ? Math.Max(1, (s + 3) / 4) * Math.Max(1, (s + 3) / 4) * 16 : s * s * 4).ToList();
        var bytes = new byte[80 + sizes.Sum()];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = (byte)mips;
        bytes[15] = 1;
        var offset = 80;
        for (var m = 0; m < mips; m++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + m * 4), (uint)offset);
            offset += sizes[m];
        }
        bytes.AsSpan(80).Fill(fill);
        return bytes;
    }

    private static void CheckKeepUnchanged()
    {
        // The re-encode changed every pixel by one step; the edit changed only the 2 x 2 corner.
        var changed = new bool[16 * 16];
        changed[0] = changed[1] = changed[16] = changed[17] = true;
        var kept = TextureFiles.KeepUnchanged(Tex(0x1450, 16, 5, 100), Tex(0x1450, 16, 5, 101), changed);
        int Pixel(int mip, int x, int y)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(kept.AsSpan(28 + mip * 4));
            return kept[offset + (y * Math.Max(1, 16 >> mip) + x) * 4];
        }
        Require(Pixel(0, 0, 0) == 101 && Pixel(0, 2, 2) == 101 && Pixel(0, 3, 3) == 100 && Pixel(0, 8, 8) == 100 && Pixel(1, 0, 0) == 101 &&
                Pixel(1, 4, 4) == 100 && Pixel(4, 0, 0) == 101,
            "texture: outside the edit (with a pixel of margin at every mip level) the original's pixels go back into the re-encoded file");
        const uint bc7 = (uint)Lumina.Data.Files.TexFile.TextureFormat.BC7;
        var single = new bool[16 * 16];
        single[0] = true;
        var blocks = TextureFiles.KeepUnchanged(Tex(bc7, 16, 3, 0xAA), Tex(bc7, 16, 3, 0xBB), single);
        Require(blocks[80] == 0xBB && blocks[80 + 16] == 0xAA && blocks[80 + 10 * 16] == 0xAA && blocks[80 + 256] == 0xBB && blocks[80 + 256 + 3 * 16] == 0xAA,
            "texture: a block-compressed texture keeps the original's whole blocks where the edit didn't reach");
        var other = Tex(bc7, 16, 3, 0xBB);
        Require(TextureFiles.KeepUnchanged(Tex(0x1450, 16, 5, 100), other, single).SequenceEqual(other),
            "texture: a re-encode into another format keeps nothing of the original");
    }

    private static void CheckBackups(string testRoot)
    {
        var folder = Path.Combine(testRoot, "SkinSeamBackups");
        Directory.CreateDirectory(folder);
        var store = new ModelBackupStore(folder);
        var target = Path.Combine(folder, "strwn.tex");
        File.WriteAllBytes(target, [1, 2, 3]);
        var first = store.Create(target, "Makeup Mod", "normal/strwn.tex");
        File.WriteAllBytes(target, [4]);
        store.Create(target, "Makeup Mod", "normal/strwn.tex");
        var listed = store.List("Makeup Mod", "normal/strwn.tex");
        Require(listed.Count == 2 && listed[0].Created >= listed[1].Created && store.Resolve(listed[1].TargetId, listed[1].Name) == first &&
                File.ReadAllBytes(first).SequenceEqual(new byte[] { 1, 2, 3 }) && store.List("Other Mod", "normal/strwn.tex").Count == 0,
            "backups: a file's kept backups are listed newest first, each resolving to its copy");

        PreviewSource Source(string mod, string relative) => new()
        {
            GamePath = "chara/" + relative, ActualPath = @"C:\Mods\" + mod + @"\" + relative, State = ResourceSourceState.LoadedMod, ModName = mod,
            ModDirectory = mod, RelativePath = relative, Sha256 = "00",
        };
        var time = new DateTimeOffset(2026, 9, 29, 23, 52, 24, TimeSpan.Zero);
        var groups = PreviewBackups.Group(
        [
            new PreviewBackupFile(Source("Makeup", "normal/strwn.tex"), new ManagedBackup("a", "first", time)),
            new PreviewBackupFile(Source("Skin", "chara/bibo_mid_norm.tex"), new ManagedBackup("b", "body", time.AddSeconds(0.1))),
            new PreviewBackupFile(Source("Face", "face.mdl"), new ManagedBackup("c", "face", time.AddSeconds(0.3))),
            new PreviewBackupFile(Source("Makeup", "normal/strwn.tex"), new ManagedBackup("a", "again", time.AddSeconds(5))),
            new PreviewBackupFile(Source("makeup", "Normal/strwn.tex"), new ManagedBackup("a", "later", time.AddMinutes(2))),
        ]);
        Require(groups.Count == 2 && groups[0].Files.Single().Backup.Name == "later" && groups[1].Files.Count == 3 &&
                groups[1].Files.Single(f => f.Source.ModName == "Makeup").Backup.Name == "first" && groups[1].Created == time,
            "backups: backups made together form one group, newest first, keeping each file's earliest backup in it");
    }

    // ---- Capture and text ------------------------------------------------------------------------------

    private static void CheckCapture(string testRoot)
    {
        var folder = Path.Combine(testRoot, "BodySeam");
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [F("top.mdl")] = Top(), [F("glv.mdl")] = Glove(), [F("skin.mtrl")] = Mtrl(),
            [F("base.tex")] = TwoTone((180, 150, 130), (150, 120, 100)), [F("norm.tex")] = NeckSeamScenarios.Texture(128, 128, 255, 255),
            [F("mask.tex")] = NeckSeamScenarios.Texture(160, 116, 150, 255), [F("sleeve.mdl")] = Sleeve(),
        };
        ResourceNode Skin() => NeckSeamScenarios.Node(SkinPath, F("skin.mtrl"), "Skin Mod",
            [NeckSeamScenarios.Node(SkinTextures[0], F("base.tex"), "Skin Mod"), NeckSeamScenarios.Node(SkinTextures[1], F("norm.tex"), "Skin Mod"),
             NeckSeamScenarios.Node(SkinTextures[2], F("mask.tex"), "Skin Mod")]);
        ResourceNode[] roots =
        [
            NeckSeamScenarios.Node(TopPath, F("top.mdl"), "Top Mod", [Skin()]), NeckSeamScenarios.Node(GlovePath, F("glv.mdl"), "Glove Mod", [Skin()]),
            NeckSeamScenarios.Node(ClothPath, F("sleeve.mdl"), "Sleeve Mod"),
        ];
        var live = new PainterLiveCharacter([new PainterLiveModel(PainterVisibility.NormalizePath(F("top.mdl")), 0, 0, 1)], null) { Race = 801 };
        var captured = NeckSeamCapture.Capture(roots, path => files.GetValueOrDefault(path), null, live);
        var top = captured.Input.Bodies.Single(b => b.GamePath == TopPath);
        Require(captured.Input.Face is null && captured.Input.CharacterRace == 801 && top.Attributes == 0 && top.Shapes == 0 &&
                captured.Input.Bodies.Single(b => b.GamePath == GlovePath).Attributes is null && captured.Source(GlovePath)?.ModName == "Glove Mod" &&
                captured.Source(SkinTextures[0])?.ModName == "Skin Mod" && NeckSeamCapture.HasSkinModels(roots) && !NeckSeamCapture.HasFaceModel(roots) &&
                captured.Input.Clothing.Single().GamePath == ClothPath && captured.Input.Bodies.All(b => b.GamePath != ClothPath),
            "body seam: capture works without a face, takes each model's drawn attributes and shape keys and the race from the game, records the body files, " +
            "and keeps gear without skin as clothing");
        var noFace = false;
        try { NeckSeamAnalyzer.Analyze(captured.Input); }
        catch (InvalidDataException) { noFace = true; }
        Require(noFace && BodySeamAnalyzer.Analyze(captured.Input).Seam(BodySeamKind.Wrists) is null,
            "body seam: without a face the neck says why, and the top's hidden wrist band leaves no wrist seam");
    }

    private static void CheckViews()
    {
        Require(NeckSeamViews.MeetLabel(0.3f, "top", "gloves") == "Top moves 30%% · gloves 70%%" && NeckSeamViews.MeetLabel(0f, "top", "gloves") == "Only the gloves change" &&
                NeckSeamViews.MeetLabel(1f, "top", "gloves") == "Only the top changes" && NeckSeamViews.MeetLabel(0.3f) == "Face moves 30%% · body 70%%",
            "body seam: the meeting point slider names the two parts");
        var seam = Analyze(Top(), Glove()).Seam(BodySeamKind.Wrists)!;
        Require(NeckSeamViews.BodySummary(seam).Contains("show as a seam", StringComparison.Ordinal) &&
                NeckSeamViews.TabLabel("Wrists", seam.Findings) == "Wrists (3)###skin-seam-Wrists" && NeckSeamViews.TabLabel("Waist", null) == "Waist###skin-seam-Waist",
            "body seam: a seam's summary and tab label count what needs a look");
        var preview = new NeckSeamPreview
        {
            Id = Guid.NewGuid(), ModDirectory = "Skin Seam Preview - A", ModIdentifier = Guid.NewGuid(), CollectionId = Guid.NewGuid(), CollectionName = "Default",
            ActorName = "A", ObjectIndex = 0, Created = DateTimeOffset.UtcNow,
            Files =
            [
                new NeckSeamPreviewFile
                {
                    Kind = "gloves model", GamePath = GlovePath, PreviewGamePath = GlovePath, PreviewRelativePath = "Files/" + GlovePath, PreviewSha256 = "00",
                    Source = new NeckSeamSource
                    {
                        GamePath = GlovePath, ActualPath = @"C:\Mods\Gloves\glv.mdl", State = ResourceSourceState.LoadedMod, ModName = "Gloves",
                        ModDirectory = "Gloves", RelativePath = "glv.mdl", Sha256 = "AB",
                    },
                },
            ],
        };
        Require(NeckSeamViews.ModelWarning(preview) is { } warning && warning.Contains("every character", StringComparison.Ordinal) &&
                warning.Contains("Gloves: glv.mdl", StringComparison.Ordinal) && NeckSeamViews.ModelWarning(preview with { Files = [] }) is null,
            "body seam: applying a changed model warns that every character and outfit wearing it changes");
    }
}
