using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Racial scaling on synthetic data: the deformer chain through human.pbd's race tree, reshaping a
/// two-LOD model and its bounding boxes, and the import context that marks a scaled import as a
/// preview whose exports are refused.
/// </summary>
internal static class RacialScalingScenarios
{
    private const string TopPath = "chara/equipment/e6001/model/c0201e6001_top.mdl";
    private static readonly string[] Bones = ["j_kubi", "j_sebo_c", "j_te_l"];

    public static void Run(string testRoot)
    {
        CheckChain();
        CheckPaths();
        CheckModel();
        CheckContexts(testRoot);
    }

    private static RacialDeformer.Affine Matrix(float scale, Vector3 translation)
        => new(new Vector4(scale, 0, 0, translation.X), new Vector4(0, scale, 0, translation.Y), new Vector4(0, 0, scale, translation.Z));

    // ---- The race tree ------------------------------------------------------------------------------

    /// <summary> A human.pbd with c0101 (root, no bones), c0201 under it, and c0801 and c1101 under c0201 and c0101. </summary>
    private static byte[] Pbd() => Pbd(
    [
        (101, -1, []),
        (201, 0, [("j_kubi", Matrix(2, Vector3.Zero)), ("j_sebo_c", Matrix(1, new Vector3(0, 0.5f, 0)))]),
        (801, 1, [("j_kubi", Matrix(1, new Vector3(0, 1, 0))), ("j_sebo_c", Matrix(1.5f, Vector3.Zero))]),
        (1101, 0, [("j_kubi", Matrix(0.5f, Vector3.Zero))]),
    ]);

    /// <summary> A human.pbd of these entries; each names its parent entry, -1 for the root. </summary>
    private static byte[] Pbd((ushort Race, int Parent, (string Bone, RacialDeformer.Affine Matrix)[] Bones)[] entries)
    {
        var header = 4 + entries.Length * 12 + entries.Length * 8;
        var body = new MemoryStream();
        var offsets = new int[entries.Length];
        for (var e = 0; e < entries.Length; e++)
        {
            var bones = entries[e].Bones;
            if (bones.Length == 0)
                continue;
            offsets[e] = header + (int)body.Length;
            var w = new BinaryWriter(body);
            var names = bones.Select(b => Encoding.UTF8.GetBytes(b.Bone + "\0")).ToArray();
            var matrices = 4 + bones.Length * 2 + ((bones.Length & 1) != 0 ? 2 : 0);
            var nameStart = matrices + bones.Length * 48;
            w.Write(bones.Length);
            for (int i = 0, at = nameStart; i < bones.Length; at += names[i].Length, i++)
                w.Write((ushort)at);
            if ((bones.Length & 1) != 0)
                w.Write((ushort)0);
            foreach (var (_, matrix) in bones)
                foreach (var row in new[] { matrix.X, matrix.Y, matrix.Z })
                {
                    w.Write(row.X);
                    w.Write(row.Y);
                    w.Write(row.Z);
                    w.Write(row.W);
                }
            foreach (var name in names)
                w.Write(name);
            while (body.Length % 4 != 0)
                w.Write((byte)0);
        }
        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        writer.Write(entries.Length);
        for (var e = 0; e < entries.Length; e++)
        {
            writer.Write(entries[e].Race);
            writer.Write((short)e);
            writer.Write(offsets[e]);
            writer.Write(1f);
        }
        // Tree nodes: parent node, two unused links, and the node's entry.
        for (var e = 0; e < entries.Length; e++)
        {
            writer.Write((short)entries[e].Parent);
            writer.Write((short)-1);
            writer.Write((short)-1);
            writer.Write((short)e);
        }
        writer.Write(body.ToArray());
        return file.ToArray();
    }

    private static void CheckChain()
    {
        var pbd = Pbd();
        var direct = RacialDeformer.Create(pbd, 801, 201);
        Require(direct.For("j_kubi").Point(Vector3.One) == new Vector3(1, 2, 1) && direct.BoneCount == 2,
            "racial scaling: a race's own entry reshapes its parent race's models");
        var chained = RacialDeformer.Create(pbd, 801, 101);
        // c0201's matrix first (×2), then c0801's (+1 up): (1, 1, 1) → (2, 2, 2) → (2, 3, 2).
        Require(chained.For("j_kubi").Point(Vector3.One) == new Vector3(2, 3, 2) &&
                chained.For("j_sebo_c").Point(Vector3.One) == new Vector3(1.5f, 2.25f, 1.5f),
            "racial scaling: a chain applies the oldest ancestor's matrices first");
        var source = new RacialScalingSource(801, pbd);
        Require(source.For("chara/equipment/e6001/model/c0801e6001_top.mdl") is null &&
                source.For("chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl") is null &&
                source.For(TopPath) is { ModelRace: 201, CharacterRace: 801, Description: "c0201 to Female Miqo'te" } &&
                source.For("c0101e0001_glv.mdl")?.Deformer.For("j_kubi").Point(Vector3.One) == new Vector3(2, 3, 2),
            "racial scaling: only other human races' models are scaled, through the whole chain");
        Reject(() => source.For("chara/equipment/e6001/model/c1101e6001_top.mdl"),
            "racial scaling: a race the character's race doesn't descend from can't be scaled for it");

        var blended = RacialDeformer.Blend([Matrix(2, Vector3.Zero), Matrix(1, Vector3.UnitY)], [0, 1], [0.25f, 0.75f]);
        Require(blended.Point(Vector3.One) == new Vector3(1.25f, 2, 1.25f) &&
                RacialDeformer.Blend([Matrix(2, Vector3.Zero)], [0], [0f]) == RacialDeformer.Affine.Identity,
            "racial scaling: bone matrices blend by skin weight, and a vertex without weight stays put");
    }

    private static void CheckPaths()
    {
        Require(ModelSkeletonPaths.WithRace(TopPath, 801) == "chara/equipment/e6001/model/c0801e6001_top.mdl" &&
                ModelSkeletonPaths.WithRace("c0201e6001_top.mdl", 1401) == "c1401e6001_top.mdl" &&
                ModelSkeletonPaths.WithRace("chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl", 801) ==
                "chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl",
            "racial scaling: the skeleton is looked up under the character's race");
    }

    // ---- The model ------------------------------------------------------------------------------------

    private sealed record Vertex(Vector3 Position, Vector3 Normal, byte[] Bones, byte[] Weights);

    private static readonly Vertex[] Vertices =
    [
        new(new Vector3(0.1f, 1.4f, 0.05f), Vector3.UnitZ, [0, 0, 0, 0, 0, 0, 0, 0], [255, 0, 0, 0, 0, 0, 0, 0]),
        new(new Vector3(-0.1f, 1.3f, 0.02f), Vector3.Normalize(new Vector3(1, 1, 0)), [0, 1, 0, 0, 0, 0, 0, 0], [128, 127, 0, 0, 0, 0, 0, 0]),
        new(new Vector3(0.2f, 1.2f, -0.03f), Vector3.UnitX, [0, 1, 2, 0, 1, 2, 0, 1], [32, 32, 32, 32, 32, 32, 32, 31]),
        new(new Vector3(0.3f, 1.1f, 0.1f), Vector3.UnitY, [2, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
    ];

    /// <summary>
    /// A two-LOD model of four vertices weighted to <see cref="Bones"/>: LOD 0 with float positions
    /// and eight influences, LOD 1 with half-precision positions and normals and four influences.
    /// Its boxes follow the add-on: the model box, the first box with its bottom at the origin, and
    /// boxes for j_kubi and j_sebo_c; j_te_l's is left unset, as by a writer that doesn't measure it.
    /// </summary>
    private static byte[] Model()
    {
        (byte Stream, byte Offset, byte Type, byte Usage)[][] declarations =
        [
            [(0, 0, 2, 0), (0, 12, 17, 1), (0, 20, 17, 2), (1, 0, 2, 3), (1, 12, 8, 6), (1, 16, 1, 4)],
            [(0, 0, 14, 0), (0, 8, 5, 1), (0, 12, 5, 2), (1, 0, 14, 3), (1, 8, 8, 6), (1, 12, 1, 4)],
        ];
        int[] stride0 = [28, 16], stride1 = [24, 20];
        var lodVertices = new byte[2][];
        for (var lod = 0; lod < 2; lod++)
        {
            var s0 = new byte[Vertices.Length * stride0[lod]];
            var s1 = new byte[Vertices.Length * stride1[lod]];
            for (var v = 0; v < Vertices.Length; v++)
            {
                var vertex = Vertices[v];
                var a = v * stride0[lod];
                var b = v * stride1[lod];
                if (lod == 0)
                {
                    Floats(s0, a, vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                    vertex.Weights.CopyTo(s0, a + 12);
                    vertex.Bones.CopyTo(s0, a + 20);
                    Floats(s1, b, vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
                }
                else
                {
                    Halves(s0, a, vertex.Position.X, vertex.Position.Y, vertex.Position.Z, 1);
                    // Four influences: the eight-way vertex keeps its first four at their share of 255.
                    var weights = vertex.Weights[..4].ToArray();
                    if (vertex.Weights[4] > 0)
                        weights = [64, 64, 64, 63];
                    weights.CopyTo(s0, a + 8);
                    vertex.Bones[..4].CopyTo(s0, a + 12);
                    Halves(s1, b, vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z, 0);
                }
                var tangent = lod == 0 ? 12 : 8;
                s1[b + tangent] = 255;
                s1[b + tangent + 1] = 128;
                s1[b + tangent + 2] = 128;
                s1[b + tangent + 3] = (byte)(v % 2 == 0 ? 255 : 0);
                Floats(s1, b + tangent + 4, 0.25f * v, 0.5f);
            }
            lodVertices[lod] = [.. s0, .. s1];
        }
        ushort[] triangles = [0, 1, 2, 0, 2, 3];
        var indexBuffer = new byte[16];
        for (var i = 0; i < triangles.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(indexBuffer.AsSpan(i * 2), triangles[i]);

        var strings = Encoding.UTF8.GetBytes("j_kubi\0j_sebo_c\0j_te_l\0/mt_c0201e6001_top_a.mtrl\0");
        var paddedStrings = new byte[(strings.Length + 3) / 4 * 4];
        strings.CopyTo(paddedStrings, 0);
        var block = new MemoryStream();
        var w = new BinaryWriter(block);
        w.Write((ushort)4);
        w.Write((ushort)0);
        w.Write(paddedStrings.Length);
        w.Write(paddedStrings);
        var meshHeader = (int)block.Position;
        // Mesh header: radius (set with the boxes), 2 meshes, no attributes, 2 submeshes, 1 material, 3 bones, 1 bone table.
        w.Write(0f);
        w.Write((ushort)2); w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1); w.Write((ushort)3); w.Write((ushort)1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)2); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)4); w.Write((ushort)0); w.Write(0u); w.Write(0u);
        var lodTable = (int)block.Position;
        w.Write(new byte[3 * 60]);
        for (var mesh = 0; mesh < 2; mesh++)
        {
            w.Write((ushort)Vertices.Length); w.Write((ushort)0); w.Write((uint)triangles.Length);
            w.Write((ushort)0); w.Write((ushort)mesh); w.Write((ushort)1); w.Write((ushort)0);
            w.Write(0u);
            w.Write(0u); w.Write((uint)(Vertices.Length * stride0[mesh])); w.Write(0u);
            w.Write((byte)stride0[mesh]); w.Write((byte)stride1[mesh]); w.Write((byte)0); w.Write((byte)2);
        }
        for (var mesh = 0; mesh < 2; mesh++)
        {
            w.Write(0u); w.Write((uint)triangles.Length); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
        }
        w.Write(23u);
        w.Write(0u); w.Write(7u); w.Write(16u);
        // One bone table of three bones, padded to four entries.
        w.Write((ushort)1); w.Write((ushort)3);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)0);
        w.Write(0u);
        var headerSize = 68 + 2 * 136;
        var padding = (int)(8 - (headerSize + block.Position + 1) % 8) % 8;
        w.Write((byte)padding);
        w.Write(new byte[padding]);
        var boxes = (int)block.Position;
        w.Write(new byte[(4 + 3) * 32]);
        var runtime = (int)block.Position;
        var blockBytes = block.ToArray();

        var dataOffset = headerSize + runtime;
        var lodOffsets = new int[2];
        var lodSize = lodVertices[0].Length + indexBuffer.Length;
        lodOffsets[0] = dataOffset;
        lodOffsets[1] = dataOffset + lodSize;
        for (var lod = 0; lod < 2; lod++)
        {
            var at = lodTable + lod * 60;
            BinaryPrimitives.WriteUInt16LittleEndian(blockBytes.AsSpan(at), (ushort)lod);
            BinaryPrimitives.WriteUInt16LittleEndian(blockBytes.AsSpan(at + 2), 1);
            foreach (var range in new[] { 12, 16, 24 })
                BinaryPrimitives.WriteUInt16LittleEndian(blockBytes.AsSpan(at + range), (ushort)(lod + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 44), (uint)lodVertices[lod].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 48), (uint)indexBuffer.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 52), (uint)lodOffsets[lod]);
            BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(at + 56), (uint)(lodOffsets[lod] + lodVertices[lod].Length));
        }
        // Boxes as the add-on writes them: the model box, the first one from the origin up, and weighted bones'.
        var positions = Vertices.Select(v => v.Position).ToArray();
        var min = positions.Aggregate(Vector3.Min);
        var max = positions.Aggregate(Vector3.Max);
        Box(blockBytes, boxes, min with { Y = 0 }, max);
        Box(blockBytes, boxes + 32, min, max);
        Box(blockBytes, boxes + 4 * 32, Vertices[..3].Select(v => v.Position).Aggregate(Vector3.Min), Vertices[..3].Select(v => v.Position).Aggregate(Vector3.Max));
        Box(blockBytes, boxes + 5 * 32, Vertices[1..3].Select(v => v.Position).Aggregate(Vector3.Min), Vertices[1..3].Select(v => v.Position).Aggregate(Vector3.Max));
        BinaryPrimitives.WriteSingleLittleEndian(blockBytes.AsSpan(meshHeader), Vector3.Max(Vector3.Abs(min with { Y = 0 }), Vector3.Abs(max)).Length());

        var header = new byte[68];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SkinModel.V6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2 * 136);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)runtime);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 1);
        for (var lod = 0; lod < 2; lod++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16 + lod * 4), (uint)lodOffsets[lod]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28 + lod * 4), (uint)(lodOffsets[lod] + lodVertices[lod].Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40 + lod * 4), (uint)lodVertices[lod].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52 + lod * 4), (uint)indexBuffer.Length);
        }
        header[64] = 2;
        var declarationBytes = new byte[2 * 136];
        for (var d = 0; d < 2; d++)
        {
            for (var e = 0; e < declarations[d].Length; e++)
            {
                var (stream, offset, type, usage) = declarations[d][e];
                declarationBytes[d * 136 + e * 8] = stream;
                declarationBytes[d * 136 + e * 8 + 1] = offset;
                declarationBytes[d * 136 + e * 8 + 2] = type;
                declarationBytes[d * 136 + e * 8 + 3] = usage;
            }
            declarationBytes[d * 136 + declarations[d].Length * 8] = 0xFF;
        }
        return [.. header, .. declarationBytes, .. blockBytes, .. lodVertices[0], .. indexBuffer, .. lodVertices[1], .. indexBuffer];
    }

    private static void Floats(byte[] bytes, int at, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }

    private static void Halves(byte[] bytes, int at, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + i * 2), BitConverter.HalfToInt16Bits((Half)values[i]));
    }

    private static void Box(byte[] bytes, int at, Vector3 min, Vector3 max) => Floats(bytes, at, min.X, min.Y, min.Z, 1, max.X, max.Y, max.Z, 1);

    /// <summary> Byte offsets of the synthetic model's parts, as <see cref="Model"/> lays them out. </summary>
    private sealed record Layout(int Boxes, int MeshHeader, int Lod1);

    private static Layout Where(byte[] model)
    {
        var meshHeader = 68 + 2 * 136 + 8 + BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(68 + 2 * 136 + 4));
        var runtimeEnd = 68 + 2 * 136 + BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(8));
        return new Layout(runtimeEnd - 7 * 32, meshHeader, (int)BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(20)));
    }

    private static Vector3 Read3(byte[] bytes, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8)));

    private static Vector3 ReadHalf3(byte[] bytes, int at) => new((float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at))),
        (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 2))),
        (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 4))));

    /// <summary> c0801's matrices for c0201 models: a skewed j_kubi, a shrunk and raised j_sebo_c; j_te_l doesn't move. </summary>
    private static RacialDeformer Deformer() => RacialDeformer.Create(Pbd(
    [
        (201, -1, []),
        (801, 0,
        [
            ("j_kubi", new(new Vector4(1.1f, 0, 0.05f, 0.01f), new Vector4(0, 0.95f, 0, 0.03f), new Vector4(-0.05f, 0, 1.05f, -0.02f))),
            ("j_sebo_c", Matrix(0.9f, new Vector3(0, 0.1f, 0))),
        ]),
    ]), 801, 201);

    private static Vector3 Expected(RacialDeformer deformer, Vertex vertex, int influences)
    {
        var table = Bones.Select(deformer.For).ToArray();
        var weights = vertex.Weights[..influences].Select(w => w / 255f).ToArray();
        if (influences == 4 && vertex.Weights[4] > 0)
            weights = [64 / 255f, 64 / 255f, 64 / 255f, 63 / 255f];
        return RacialDeformer.Blend(table, vertex.Bones[..influences], weights).Point(vertex.Position);
    }

    private static void CheckModel()
    {
        var model = Model();
        var original = SkinModel.Read(model).Meshes.Single();
        Require(original.Positions.SequenceEqual(Vertices.Select(v => v.Position)) && original.Influences == 8 && original.Bones.SequenceEqual(Bones),
            "racial scaling: the synthetic two-LOD model reads back");
        var deformer = Deformer();
        var scaled = RacialScalingModel.Apply(model, deformer);
        var mesh = SkinModel.Read(scaled).Meshes.Single();
        var layout = Where(scaled);
        Require(Enumerable.Range(0, Vertices.Length).All(v => Vector3.Distance(mesh.Positions[v], Expected(deformer, Vertices[v], 8)) < 1e-6f) &&
                mesh.Positions[3] == Vertices[3].Position && mesh.Positions[0] != Vertices[0].Position,
            "racial scaling: LOD 0's float positions move by their eight blended bone matrices; an unweighted vertex stays");
        var lod1 = Enumerable.Range(0, Vertices.Length).Select(v => ReadHalf3(scaled, layout.Lod1 + v * 16)).ToArray();
        Require(Enumerable.Range(0, Vertices.Length).All(v => Vector3.Distance(lod1[v], Expected(deformer, Vertices[v], 4)) < 2e-3f) &&
                BinaryPrimitives.ReadInt16LittleEndian(scaled.AsSpan(layout.Lod1 + 6)) == BitConverter.HalfToInt16Bits((Half)1),
            "racial scaling: LOD 1's half positions move too, keeping their fourth component");
        Require(mesh.Normals.All(n => MathF.Abs(n.Length() - 1) < 1e-5f) && Vector3.Dot(mesh.Normals[0], Vertices[0].Normal) < 0.99999f &&
                mesh.BinormalSigns.SequenceEqual([1f, -1f, 1f, -1f]) && MathF.Abs(mesh.Binormals[0].Length() - 1) < 1e-5f,
            "racial scaling: normals and tangents turn with the vertices and stay unit length; tangent handedness is kept");
        Require(mesh.Uv1.SequenceEqual(original.Uv1) &&
                scaled.AsSpan(0, layout.MeshHeader).SequenceEqual(model.AsSpan(0, layout.MeshHeader)) &&
                scaled.AsSpan(layout.MeshHeader + 4, layout.Boxes - layout.MeshHeader - 4).SequenceEqual(
                    model.AsSpan(layout.MeshHeader + 4, layout.Boxes - layout.MeshHeader - 4)),
            "racial scaling: UVs and every table before the boxes are untouched, but for the radius");

        var all = Enumerable.Range(0, Vertices.Length).Select(v => mesh.Positions[v]).Concat(lod1).ToArray();
        var min = all.Aggregate(Vector3.Min);
        var max = all.Aggregate(Vector3.Max);
        var modelBox = (Read3(scaled, layout.Boxes + 32), Read3(scaled, layout.Boxes + 48));
        var firstBox = (Read3(scaled, layout.Boxes), Read3(scaled, layout.Boxes + 16));
        Require(modelBox == (min, max) && firstBox == (min with { Y = 0 }, max) &&
                MathF.Abs(BinaryPrimitives.ReadSingleLittleEndian(scaled.AsSpan(layout.MeshHeader)) -
                          Vector3.Max(Vector3.Abs(min with { Y = 0 }), Vector3.Abs(max)).Length()) < 1e-6f,
            "racial scaling: the model box is measured again, the first box keeps its bottom at the origin, and the radius follows");
        var kubiBox = (Read3(scaled, layout.Boxes + 4 * 32), Read3(scaled, layout.Boxes + 4 * 32 + 16));
        var kubiVertices = new[] { 0, 1, 2 }.Select(v => mesh.Positions[v]).Concat(new[] { 0, 1, 2 }.Select(v => lod1[v])).ToArray();
        Require(kubiBox == (kubiVertices.Aggregate(Vector3.Min), kubiVertices.Aggregate(Vector3.Max)) &&
                scaled.AsSpan(layout.Boxes + 6 * 32, 32).ToArray().All(b => b == 0),
            "racial scaling: a weighted bone's box covers its vertices in every LOD; an unset bone box stays unset");

        Require(RacialScalingModel.Apply(model, RacialDeformer.None).SequenceEqual(model),
            "racial scaling: a deformer without bones leaves the model as it is");

        var face = (byte[])model.Clone();
        face[layout.MeshHeader + 43] = 1;
        var refused = false;
        try { RacialScalingModel.Apply(face, deformer); }
        catch (NotSupportedException) { refused = true; }
        Require(refused, "racial scaling: face models with neck morphs are refused rather than half scaled");
    }

    // ---- Contexts and exports ---------------------------------------------------------------------------

    private static void CheckContexts(string testRoot)
    {
        var root = Path.Combine(testRoot, "RacialScalingMod");
        var relative = "Files/" + TopPath;
        var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Model());
        var record = new RacialScaling(201, 801, Deformer()).ToRecord();

        IReadOnlyList<PersistedExportContext> persisted = [];
        using var registry = new ExportContextRegistry("scaling-plugin", persist: contexts => persisted = contexts);
        var context = registry.CreateContext(TopPath, 0, "RacialScalingMod", target, "Racial Scaling Mod", 42428, root, relative, racialScaling: record);
        var json = JsonSerializer.Serialize(context);
        Require(record is { ModelRace: 201, CharacterRace: 801 } && context.RacialScaling == record &&
                persisted.Single().RacialScaling == record && !json.Contains("racialScaling", StringComparison.Ordinal),
            "racial scaling: the import context records the races, persisted but not echoed to Blender");
        var saved = JsonSerializer.Deserialize<PersistedExportContext>(JsonSerializer.Serialize(persisted.Single()))!;
        var newtonsoft = Newtonsoft.Json.JsonConvert.DeserializeObject<PersistedExportContext>(Newtonsoft.Json.JsonConvert.SerializeObject(persisted.Single()))!;
        var fromConfiguration = newtonsoft with { ContextId = Guid.NewGuid().ToString("N") };
        using (var reloaded = new ExportContextRegistry("reloaded-scaling-plugin", [saved, fromConfiguration]))
        {
            bool Restored(string contextId)
                => reloaded.TryReattach(contextId, context.ImportId, context.Capability, 42428, out var restored, out _) &&
                   restored?.RacialScaling == record;
            Require(Restored(context.ContextId) && Restored(fromConfiguration.ContextId),
                "racial scaling: a scaled import stays scaled after a plugin restart, also through Dalamud's configuration fallback");
        }
        foreach (var bad in new[] { record with { CharacterRace = 201 }, record with { ModelRace = 9999 } })
        {
            var broken = persisted.Single() with { RacialScaling = bad };
            using var brokenRegistry = new ExportContextRegistry("broken-scaling-plugin", [broken]);
            Require(!brokenRegistry.TryReattach(broken.ContextId, broken.ImportId, broken.Capability, 42428, out _, out var brokenCode) &&
                    brokenCode == "stale_context",
                $"racial scaling: a saved context scaled c{bad.ModelRace:D4} to c{bad.CharacterRace:D4} is unusable rather than unscaled");
        }
        Reject(() => registry.CreateContext(TopPath, 0, "RacialScalingMod", target, "Racial Scaling Mod", 42428, root, relative,
                racialScaling: record with { CharacterRace = 201 }),
            "racial scaling: a context can't be scaled from a race to itself");
        var game = registry.CreateGameContext(TopPath, TopPath, 0, 42428, racialScaling: record);
        Require(game.RacialScaling == record, "racial scaling: a vanilla import records its scaling too");

        // Scaled imports are previews: nothing exported from one, or combined with one, is written.
        var hair = context with { GamePath = "chara/human/c0801/obj/hair/h0101/model/c0801h0101_hir.mdl", RacialScaling = null };
        Require(ExportServer.RacialScalingRefusal([context]) is { Success: false, Code: "racial_scaling_preview" } refusal &&
                refusal.Message.Contains("c0201 to Female Miqo'te", StringComparison.Ordinal) &&
                ExportServer.RacialScalingRefusal([game]) is { Code: "racial_scaling_preview" },
            "racial scaling: every export from a scaled import is refused, naming the scaling");
        Require(ExportServer.RacialScalingRefusal([hair, context]) is { Code: "racial_scaling_preview" } &&
                ExportServer.RacialScalingRefusal([context, hair]) is { Code: "racial_scaling_preview" },
            "racial scaling: a mashup is refused whether a scaled import is its destination or a contributor");
        Require(ExportServer.RacialScalingRefusal([hair]) is null && ExportServer.RacialScalingRefusal([hair, hair]) is null,
            "racial scaling: unscaled imports export as before");
    }
}
