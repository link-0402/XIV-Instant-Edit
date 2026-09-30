using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Skeletons;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The neck seam fix's pure parts on synthetic data: a face and a body built as two open tubes that
/// meet in a ten-vertex neck ring, skin.shpk materials and small uncompressed textures. Covers the
/// model reader and neck morph insertion, the material constant writer, the racial deformer chain,
/// capturing inputs from a resource tree, the analysis and the texture blend.
/// </summary>
internal static class NeckSeamScenarios
{
    private const string FaceModelPath = "chara/human/c0801/obj/face/f0002/model/c0801f0002_fac.mdl";
    private const string FaceMaterialPath = "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl";
    private const string BodyModelPath = "chara/equipment/e0000/model/c0801e0000_top.mdl";
    private const string BodyMaterialPath = "chara/human/c0801/obj/body/b0001/material/v0001/mt_c0801b0001_a.mtrl";
    private const int RingSize = 10;
    private const float Radius = 0.035f;

    public static void Run(string testRoot)
    {
        CheckMaterial();
        var face = Tube("/mt_c0801f0002_fac_a.mtrl", [1.40f, 1.425f, 1.45f], vStart: 0.2f, vEnd: 0.8f);
        var body = Tube("/mt_c0801b0001_a.mtrl", [1.35f, 1.375f, 1.40f], vStart: 0.2f, vEnd: 0.8f);
        CheckModel(face);
        CheckDeformer();
        CheckAnalysis(testRoot, face, body);
        CheckNames(testRoot);
    }

    // ---- Materials ------------------------------------------------------------------------------------

    internal static byte[] Material(string[] textures, (uint Key, uint Value)[] keys, (uint Id, float[] Values)[] constants)
    {
        var strings = new MemoryStream();
        strings.Write(Encoding.UTF8.GetBytes("skin.shpk\0"));
        var offsets = new List<ushort>();
        foreach (var texture in textures)
        {
            offsets.Add((ushort)strings.Length);
            strings.Write(Encoding.UTF8.GetBytes(texture + "\0"));
        }
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);
        var values = constants.SelectMany(c => c.Values.SelectMany(BitConverter.GetBytes)).ToArray();
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(0x01030000u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)strings.Length);
        writer.Write((ushort)0);
        writer.Write((byte)textures.Length);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        foreach (var offset in offsets)
        {
            writer.Write(offset);
            writer.Write((ushort)0);
        }
        writer.Write(strings.ToArray());
        writer.Write((ushort)values.Length);
        writer.Write((ushort)keys.Length);
        writer.Write((ushort)constants.Length);
        writer.Write((ushort)textures.Length);
        writer.Write(0xDu);
        foreach (var (key, value) in keys)
        {
            writer.Write(key);
            writer.Write(value);
        }
        var cursor = 0;
        foreach (var (id, constantValues) in constants)
        {
            writer.Write(id);
            writer.Write((ushort)cursor);
            writer.Write((ushort)(constantValues.Length * 4));
            cursor += constantValues.Length * 4;
        }
        uint[] samplers = [SkinMaterial.DiffuseSampler, SkinMaterial.NormalSampler, SkinMaterial.MaskSampler];
        for (var i = 0; i < textures.Length; i++)
        {
            writer.Write(samplers[i]);
            writer.Write(0x000F8340u);
            writer.Write((byte)i);
            writer.Write(new byte[3]);
        }
        writer.Write(values);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
        return bytes;
    }

    private static readonly string[] FaceTextures =
    [
        "chara/human/c0801/obj/face/f0001/texture/c0801f0001_fac_base.tex",
        "chara/human/c0801/obj/face/f0001/texture/c0801f0001_fac_norm.tex",
        "chara/human/c0801/obj/face/f0001/texture/c0801f0001_fac_mask.tex",
    ];

    private static readonly string[] BodyTextures = ["chara/body_base.tex", "chara/body_norm.tex", "chara/body_mask.tex"];

    private static byte[] FaceMaterial()
        => Material(FaceTextures, [], [(SkinMaterial.TileScale, [80, 80]), (SkinMaterial.TileAlpha, [0.4f]), (SkinMaterial.TileIndex, [60])]);

    private static byte[] BodyMaterial()
        => Material(BodyTextures, [(SkinMaterial.SkinTypeKey, SkinMaterial.SkinTypeBody)],
            [(SkinMaterial.TileScale, [150, 150]), (SkinMaterial.TileAlpha, [0.6f]), (SkinMaterial.TileIndex, [60])]);

    private static void CheckMaterial()
    {
        var face = SkinMaterial.Read(FaceMaterial());
        Require(face.IsSkin && face.IsFaceSkin && !face.IsBodySkin && face.TextureFor(SkinMaterial.NormalSampler) == FaceTextures[1],
            "neck seam: a skin.shpk material without a skin type key is a face, and its samplers name its textures");
        Require(SkinMaterial.Read(BodyMaterial()).IsBodySkin, "neck seam: the Body skin type key marks a body skin material");
        Require(face.Constant(SkinMaterial.NormalScale).SequenceEqual([1f]) && face.Constant(SkinMaterial.TileScale).SequenceEqual([80f, 80f]),
            "neck seam: constants the material leaves out read as skin.shpk's defaults");

        var changed = SkinMaterial.Read(FaceMaterial()).WithConstants(new Dictionary<uint, float[]>
        {
            [SkinMaterial.TileScale] = [25.5f, 25.5f],
            [SkinMaterial.NormalScale] = [0.75f],
        });
        var reread = SkinMaterial.Read(changed);
        Require(reread.Constant(SkinMaterial.TileScale).SequenceEqual([25.5f, 25.5f]) && reread.Constant(SkinMaterial.NormalScale).SequenceEqual([0.75f]) &&
                reread.Constant(SkinMaterial.TileAlpha).SequenceEqual([0.4f]) && reread.HasConstant(SkinMaterial.NormalScale),
            "neck seam: constants are overwritten in place or appended, and the others keep their values");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(4)) == changed.Length && reread.Textures.SequenceEqual(FaceTextures),
            "neck seam: the material's size field and texture table stay consistent after a constant change");
        var rewritten = PenumbraService.RewriteMaterialTexturePaths(changed,
            new Dictionary<string, string> { [FaceTextures[2]] = NeckSeamService.PreviewTexturePath(FaceTextures[2], "1a2b3c4d") });
        Require(SkinMaterial.Read(rewritten).TextureFor(SkinMaterial.MaskSampler) == "chara/human/c0801/obj/face/f0001/texture/c0801f0001_fac_mask_ns1a2b3c4d.tex" &&
                SkinMaterial.Read(rewritten).Constant(SkinMaterial.TileScale).SequenceEqual([25.5f, 25.5f]),
            "neck seam: a preview's texture path rewrite keeps the changed constants");
    }

    // ---- Models ---------------------------------------------------------------------------------------

    /// <summary> An open tube of ten-vertex rings at <paramref name="heights"/>, weighted to j_kubi/j_sebo_c. </summary>
    private static byte[] Tube(string material, float[] heights, float vStart, float vEnd)
    {
        const int stride = 56;
        var vertices = heights.Length * RingSize;
        var vertexBuffer = new byte[vertices * stride];
        for (var ring = 0; ring < heights.Length; ring++)
            for (var i = 0; i < RingSize; i++)
            {
                var angle = i * MathF.Tau / RingSize;
                var at = (ring * RingSize + i) * stride;
                var normal = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
                var position = normal * Radius + new Vector3(0, heights[ring], 0);
                WriteFloats(vertexBuffer, at, position.X, position.Y, position.Z);
                vertexBuffer[at + 12] = 153;
                vertexBuffer[at + 13] = 102;
                vertexBuffer[at + 16] = 0;
                vertexBuffer[at + 17] = 1;
                WriteFloats(vertexBuffer, at + 20, normal.X, normal.Y, normal.Z);
                vertexBuffer[at + 32] = 128;
                vertexBuffer[at + 33] = 255;
                vertexBuffer[at + 34] = 128;
                vertexBuffer[at + 35] = 255;
                vertexBuffer[at + 36] = 0;
                vertexBuffer[at + 37] = 255;
                vertexBuffer[at + 38] = 255;
                vertexBuffer[at + 39] = 255;
                var u = 0.1f + 0.8f * i / RingSize;
                var v = vStart + (vEnd - vStart) * ring / (heights.Length - 1);
                WriteFloats(vertexBuffer, at + 40, u, v, u, v);
            }
        var indices = new List<ushort>();
        for (var ring = 0; ring + 1 < heights.Length; ring++)
            for (var i = 0; i < RingSize; i++)
            {
                ushort a = (ushort)(ring * RingSize + i), b = (ushort)(ring * RingSize + (i + 1) % RingSize);
                ushort c = (ushort)(a + RingSize), d = (ushort)(b + RingSize);
                indices.AddRange([a, b, c, b, d, c]);
            }
        var indexBuffer = indices.SelectMany(BitConverter.GetBytes).ToArray();

        var declaration = new byte[136];
        byte[][] elements = [[0, 0, 2, 0, 0], [0, 12, 8, 1, 0], [0, 16, 5, 2, 0], [0, 20, 2, 3, 0], [0, 32, 8, 6, 0], [0, 36, 8, 7, 0], [0, 40, 3, 4, 0]];
        for (var e = 0; e < elements.Length; e++)
            elements[e].CopyTo(declaration, e * 8);
        declaration[elements.Length * 8] = 0xFF;

        var strings = Encoding.UTF8.GetBytes("j_kubi\0j_sebo_c\0" + material + "\0");
        var paddedStrings = new byte[(strings.Length + 3) / 4 * 4];
        strings.CopyTo(paddedStrings, 0);

        var block = new MemoryStream();
        var w = new BinaryWriter(block);
        w.Write((ushort)3);
        w.Write((ushort)0);
        w.Write(paddedStrings.Length);
        w.Write(paddedStrings);
        // Mesh header.
        w.Write(0.1f);
        w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)1); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)2); w.Write((ushort)0); w.Write(0u); w.Write(0u);
        var lodPosition = (int)block.Position;
        w.Write(new byte[3 * 60]);
        // Mesh.
        w.Write((ushort)vertices); w.Write((ushort)0); w.Write((uint)indices.Count);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)0);
        w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
        w.Write((byte)stride); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
        // Submesh.
        w.Write(0u); w.Write((uint)indices.Count); w.Write(0u); w.Write((ushort)0); w.Write((ushort)2);
        // Material and bone name offsets.
        w.Write(16u);
        w.Write(0u); w.Write(7u);
        // One bone table: its indices start right after its header.
        w.Write((ushort)1); w.Write((ushort)2);
        w.Write((ushort)0); w.Write((ushort)1);
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

    private static void CheckModel(byte[] bytes)
    {
        var model = SkinModel.Read(bytes);
        var mesh = model.Meshes.Single();
        Require(mesh.VertexCount == 30 && mesh.Triangles.Length == 2 * 2 * RingSize * 3 && mesh.Material == "/mt_c0801f0002_fac_a.mtrl" &&
                mesh.Bones.SequenceEqual(["j_kubi", "j_sebo_c"]) && mesh.HasUv2 && mesh.Influences == 4,
            "neck seam: the model reader returns the LOD 0 mesh with its material, bone table and both UV sets");
        Require(MathF.Abs(mesh.BlendWeights[0] - 0.6f) < 0.01f && MathF.Abs(mesh.Binormals[0].Y - 1f) < 0.01f && mesh.BinormalSigns[0] > 0 &&
                MathF.Abs(mesh.Normals[0].Z - 1f) < 1e-4f && mesh.Colors[0].X == 0,
            "neck seam: weights, binormals with their sign, normals and vertex colour decode");

        NeckMorph[] morphs = [new(new Vector3(0, 1.4f, 0.035f), Vector3.UnitZ, 0, 1), new(new Vector3(0.035f, 1.4f, 0), Vector3.UnitX, 0, 1)];
        var withMorphs = model.WithNeckMorphs(morphs);
        var reread = SkinModel.Read(withMorphs);
        Require(withMorphs.Length == bytes.Length + 64 && reread.NeckMorphs.SequenceEqual(morphs) &&
                reread.Meshes.Single().Positions.SequenceEqual(mesh.Positions) && reread.Meshes.Single().Triangles.SequenceEqual(mesh.Triangles),
            "neck seam: neck morphs are inserted where the table belongs and the geometry still reads the same");
        var meshHeader = 68 + 136 + 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68 + 136 + 4));
        var lod = meshHeader + 56;
        Require(withMorphs[meshHeader + 43] == 2 && withMorphs[lod + 40] == 0 && withMorphs[lod + 41] == 2 &&
                BinaryPrimitives.ReadUInt32LittleEndian(withMorphs.AsSpan(8)) == BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) + 64 &&
                BinaryPrimitives.ReadUInt32LittleEndian(withMorphs.AsSpan(lod + 52)) == BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lod + 52)) + 64,
            "neck seam: the mesh header and LOD 0 count the morphs, and the geometry offsets move past them");
        var threw = false;
        try { reread.WithNeckMorphs(morphs); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "neck seam: a model that has a neck morph table keeps it");
    }

    // ---- Racial deformer ------------------------------------------------------------------------------

    private static void CheckDeformer()
    {
        // Two entries: 0201 (root) and 0801 (its child) with a deformer moving j_kubi up by 1 cm.
        var pbd = new MemoryStream();
        var w = new BinaryWriter(pbd);
        w.Write(2);
        var deformerOffset = 4 + 2 * 12 + 2 * 8;
        w.Write((ushort)201); w.Write((short)0); w.Write(0); w.Write(1f);
        w.Write((ushort)801); w.Write((short)1); w.Write(deformerOffset); w.Write(1f);
        w.Write((short)-1); w.Write((short)1); w.Write((short)-1); w.Write((short)0);
        w.Write((short)0); w.Write((short)-1); w.Write((short)-1); w.Write((short)1);
        w.Write(1);
        w.Write((ushort)(4 + 2 + 2 + 48));
        w.Write((ushort)0);
        w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
        w.Write(0f); w.Write(1f); w.Write(0f); w.Write(0.01f);
        w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0f);
        w.Write(Encoding.UTF8.GetBytes("j_kubi\0"));
        var deformer = RacialDeformer.Create(pbd.ToArray(), 801, 201);
        Require(deformer.BoneCount == 1 && MathF.Abs(deformer.For("j_kubi").Point(Vector3.Zero).Y - 0.01f) < 1e-6f &&
                deformer.For("j_sebo_c").Point(Vector3.One) == Vector3.One,
            "neck seam: the racial deformer reads the skeleton race's bone matrices");
        var mesh = SkinModel.Read(Tube("/mt.mtrl", [1.40f, 1.45f], 0, 1)).Meshes.Single();
        var (positions, _, _) = deformer.Deform(mesh);
        Require(MathF.Abs(positions[0].Y - (mesh.Positions[0].Y + 0.006f)) < 1e-4f,
            "neck seam: deforming blends the bone matrices by the skin weights (60% j_kubi)");
    }

    // ---- Analysis and fix ----------------------------------------------------------------------------

    internal static byte[] Texture(byte r, byte g, byte b, byte a, int size = 32)
    {
        var bytes = new byte[80 + size * size * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = 1;
        bytes[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 80);
        for (var i = 80; i < bytes.Length; i += 4)
        {
            bytes[i] = b;
            bytes[i + 1] = g;
            bytes[i + 2] = r;
            bytes[i + 3] = a;
        }
        return bytes;
    }

    internal static ResourceNode Node(string gamePath, string actualPath, string mod, IReadOnlyList<ResourceNode>? children = null) => new()
    {
        Type = "Resource", Icon = "", Name = Path.GetFileName(gamePath), GamePath = gamePath, ActualPath = actualPath,
        Children = children ?? [], SourceState = ResourceSourceState.LoadedMod, SourceLabel = mod, SourceModName = mod,
        SourceModDirectory = mod, SourceModRootPath = Path.GetDirectoryName(actualPath) ?? "", SourceRelativePath = Path.GetFileName(actualPath),
        SlotLabel = "", ResourceSection = ResourceSection.CharacterFeatures, SortOrder = 0,
    };

    private static void CheckAnalysis(string testRoot, byte[] faceModel, byte[] bodyModel)
    {
        var folder = Path.Combine(testRoot, "NeckSeam");
        Directory.CreateDirectory(folder);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(folder, "face.mdl")] = faceModel,
            [Path.Combine(folder, "face.mtrl")] = FaceMaterial(),
            [Path.Combine(folder, "face_base.tex")] = Texture(170, 140, 120, 255),
            [Path.Combine(folder, "face_norm.tex")] = Texture(128, 128, 255, 0),
            [Path.Combine(folder, "face_mask.tex")] = Texture(160, 130, 150, 255),
            [Path.Combine(folder, "body.mdl")] = bodyModel,
            [Path.Combine(folder, "body.mtrl")] = BodyMaterial(),
            [Path.Combine(folder, "body_base.tex")] = Texture(180, 150, 130, 255),
            [Path.Combine(folder, "body_norm.tex")] = Texture(128, 128, 255, 255),
            [Path.Combine(folder, "body_mask.tex")] = Texture(160, 116, 150, 255),
        };
        string F(string name) => Path.Combine(folder, name);
        ResourceNode[] roots =
        [
            Node(FaceModelPath, F("face.mdl"), "Face Mod",
                [Node(FaceMaterialPath, F("face.mtrl"), "Skin Mod",
                [
                    Node(FaceTextures[0], F("face_base.tex"), "Makeup Mod"), Node(FaceTextures[1], F("face_norm.tex"), "Makeup Mod"),
                    Node(FaceTextures[2], F("face_mask.tex"), "Makeup Mod"),
                ])]),
            Node(BodyModelPath, F("body.mdl"), "Body Mod",
                [Node(BodyMaterialPath, F("body.mtrl"), "Skin Mod",
                [
                    Node(BodyTextures[0], F("body_base.tex"), "Skin Mod"), Node(BodyTextures[1], F("body_norm.tex"), "Skin Mod"),
                    Node(BodyTextures[2], F("body_mask.tex"), "Skin Mod"),
                ])]),
        ];
        var captured = NeckSeamCapture.Capture(roots, path => files.GetValueOrDefault(path), null);
        Require(captured.Input.Bodies.Count == 1 && captured.Source(FaceMaterialPath)?.ModName == "Skin Mod" &&
                captured.Source(FaceTextures[2])?.Sha256 == NeckSeamCapture.Hash(files[F("face_mask.tex")]) &&
                captured.Source(FaceModelPath)?.IsModFile == true && captured.Source(BodyMaterialPath)?.ModName == "Skin Mod" &&
                captured.Source(BodyTextures[0])?.ModName == "Skin Mod" && captured.Source(BodyModelPath)?.ModName == "Body Mod",
            "neck seam: capture finds the face and the body skin model and records their models, materials and textures with their mods and hashes");

        var report = NeckSeamAnalyzer.Analyze(captured.Input);
        NeckSeamFinding Finding(string title) => report.Findings.Single(f => f.Title == title);
        var proxy = captured.Input.Bodies[0] with { GamePath = "chara/human/c0801/obj/body/b0003/model/c0801b0003_top.mdl" };
        Require(NeckSeamAnalyzer.Analyze(captured.Input with { Bodies = [proxy, .. captured.Input.Bodies] }).BodyModelPath == BodyModelPath,
            "neck seam: a human body model (a seam connector, the low-poly body) is never the neck's body, even listed first with its ring on the face's");
        Require(report.RingVertices == RingSize && report.BodyModelPath == BodyModelPath && Finding("Neck ring fit").Severity == NeckSeamSeverity.Ok,
            "neck seam: the face's lowest open edge is the neck ring and the body's top edge lies on it");
        Require(Finding("Neck connection data").Fix == NeckSeamFixKind.NeckMorph && report.NeckMorphs.Count == RingSize &&
                report.NeckMorphs.All(m => m.BoneA == 0 && m.BoneB == 1),
            "neck seam: a face without neck morphs gets one connection vertex per ring vertex, bound to j_kubi and j_sebo_c");
        Require(Finding("Roughness").Severity == NeckSeamSeverity.Problem && Finding("Skin colour at the seam").Severity == NeckSeamSeverity.Problem &&
                Finding("Specular strength").Severity == NeckSeamSeverity.Ok && Finding("Surface normal at the seam").Severity == NeckSeamSeverity.Ok,
            "neck seam: texture channels that differ at the seam are problems, matching ones are fine");
        Require(report.TexturesToBlend.SetEquals([SkinMaterial.DiffuseSampler, SkinMaterial.MaskSampler]),
            "neck seam: only the textures that differ are marked for blending");
        var match = report.Material;
        Require(match.TileScaleOff && match.TileAlphaOff && !match.TileIndexOff && match.Other.Count == 0 && MathF.Abs(match.BodyNormalAlpha - 1) < 0.01f,
            "neck seam: the tile size and strength differ between the materials, the pattern doesn't");
        float Tiles(float[] scale, float density) => scale.Average() * density;
        var (faceOnly, bodyKept) = match.Plan(1f);
        Require(bodyKept.Count == 0 && faceOnly[SkinMaterial.TileAlpha].SequenceEqual([0.6f]) &&
                MathF.Abs(Tiles(faceOnly[SkinMaterial.TileScale], match.FaceDensity) / match.BodyTiles - 1) < 0.01f,
            "neck seam: meeting at the body's side changes only the face, to the body's tiles per metre and strength");
        var (faceKept, bodyOnly) = match.Plan(0f);
        Require(faceKept.Count == 0 && bodyOnly[SkinMaterial.TileAlpha].SequenceEqual([0.4f]) &&
                MathF.Abs(Tiles(bodyOnly[SkinMaterial.TileScale], match.BodyDensity) / match.FaceTiles - 1) < 0.01f,
            "neck seam: meeting at the face's side changes only the body, to the face's tiles per metre and strength");
        var (faceHalf, bodyHalf) = match.Plan(0.5f);
        var middle = MathF.Sqrt(match.FaceTiles * match.BodyTiles);
        Require(MathF.Abs(Tiles(faceHalf[SkinMaterial.TileScale], match.FaceDensity) / middle - 1) < 0.01f &&
                MathF.Abs(Tiles(bodyHalf[SkinMaterial.TileScale], match.BodyDensity) / middle - 1) < 0.01f &&
                faceHalf[SkinMaterial.TileAlpha].SequenceEqual([0.5f]) && bodyHalf[SkinMaterial.TileAlpha].SequenceEqual([0.5f]),
            "neck seam: in the middle both move, to the geometric mean of their tile sizes and the average strength");
        Require(NeckSeamViews.MeetLabel(0.5f) == "Both meet in the middle" && NeckSeamViews.MeetLabel(0.3f) == "Face moves 30%% · body 70%%" &&
                NeckSeamViews.MaterialPlan(match, 1f).Any(l => l.StartsWith("Pore tile size", StringComparison.Ordinal) && l.Contains("body 150, 150 (unchanged)")),
            "neck seam: the meeting point slider names how far each side moves and lists the resulting values");
        Require(NeckSeamViews.Summary(report).Contains("seam", StringComparison.Ordinal),
            "neck seam: the dialog summary names the differences");

        var fix = NeckSeamFixer.Build(report, new NeckSeamFixOptions(true, true, true, Meet: 1f));
        Require(fix.Model is not null && SkinModel.Read(fix.Model).NeckMorphs.Count == RingSize,
            "neck seam: the fixed face model carries the connection vertices");
        var material = SkinMaterial.Read(fix.Material!);
        Require(material.Constant(SkinMaterial.TileScale).SequenceEqual(faceOnly[SkinMaterial.TileScale]) &&
                material.Textures.SequenceEqual(FaceTextures) && fix.BodyMaterial is null,
            "neck seam: the fixed face material has the new constants and keeps its texture paths, and the body material is left alone");
        var both = NeckSeamFixer.Build(report, new NeckSeamFixOptions(false, true, false, Meet: 0.5f));
        Require(both.Model is null && both.Textures.Count == 0 && both.BodyMaterial is not null &&
                SkinMaterial.Read(both.BodyMaterial).Constant(SkinMaterial.TileScale).SequenceEqual(bodyHalf[SkinMaterial.TileScale]) &&
                SkinMaterial.Read(both.Material!).Constant(SkinMaterial.TileScale).SequenceEqual(faceHalf[SkinMaterial.TileScale]) &&
                both.Changes.Any(c => c.StartsWith("Body material", StringComparison.Ordinal)),
            "neck seam: meeting in the middle writes both materials");
        var mask = fix.Textures.Single(t => t.Sampler == SkinMaterial.MaskSampler).Image;
        // Rows of the 32-texel mask: the seam is at v = 0.2 (row 6.4), the top of the face at v = 0.8.
        var nearSeam = mask.Texel(8, 7).Y * 255;
        var farAway = mask.Texel(8, 24).Y * 255;
        Require(MathF.Abs(nearSeam - 116) < 3 && MathF.Abs(farAway - 130) < 0.5f && fix.Textures.All(t => t.Sampler != SkinMaterial.NormalSampler),
            "neck seam: the blend takes the face's roughness to the body's at the seam and leaves it alone higher up");
        Require(fix.After.Roughness is { } after && MathF.Abs(after.Face - after.Body) < 0.01f && NeckSeamViews.Expected(fix).Any(l => l.StartsWith("Roughness")),
            "neck seam: measured again, the roughness matches and the dialog lists the change");
    }

    // ---- Names, store and backups ---------------------------------------------------------------------

    private static void CheckNames(string testRoot)
    {
        Require(PenumbraService.UniqueModName("Neck Seam Preview - Luci Xiv", name => name == "Neck Seam Preview - Luci Xiv") == "Neck Seam Preview - Luci Xiv (2)" &&
                PenumbraService.UniqueModName("a/b:c", _ => false) == "abc",
            "neck seam: mod names are made unique and stripped of characters a folder can't hold");
        Require(NeckSeamService.PreviewTexturePath("chara/x/--y.tex", "0011aabb") == "chara/x/--y_ns0011aabb.tex",
            "neck seam: preview textures sit next to the original with the preview's tag");

        var store = new NeckSeamPreviewStore(Path.Combine(testRoot, "NeckSeamStore"));
        var preview = new NeckSeamPreview
        {
            Id = Guid.NewGuid(), ModDirectory = "Neck Seam Preview - A", ModIdentifier = Guid.NewGuid(), CollectionId = Guid.NewGuid(),
            CollectionName = "Default", ActorName = "A", ObjectIndex = 0, Created = DateTimeOffset.UtcNow,
            Files =
            [
                new NeckSeamPreviewFile
                {
                    Kind = "material", GamePath = FaceMaterialPath, PreviewGamePath = FaceMaterialPath, PreviewRelativePath = "Files/" + FaceMaterialPath,
                    PreviewSha256 = "00", TextureRewrites = new(StringComparer.OrdinalIgnoreCase) { ["a_ns1.tex"] = "a.tex" },
                    Source = new NeckSeamSource
                    {
                        GamePath = FaceMaterialPath, ActualPath = @"C:\Mods\Skin\x.mtrl", State = ResourceSourceState.LoadedMod, ModName = "Skin",
                        ModDirectory = "Skin", RelativePath = "x.mtrl", Sha256 = "AB",
                    },
                },
            ],
        };
        store.Add(preview);
        var reloaded = new NeckSeamPreviewStore(Path.Combine(testRoot, "NeckSeamStore"));
        reloaded.Load();
        var file = reloaded.Previews.Single().Files.Single();
        Require(reloaded.LoadError.Length == 0 && file.Source.State == ResourceSourceState.LoadedMod && file.Source.IsModFile &&
                file.TextureRewrites["a_ns1.tex"] == "a.tex" && NeckSeamViews.ApplyLines(reloaded.Previews.Single()).Single().StartsWith("Overwrite Skin: x.mtrl"),
            "neck seam: previews survive a reload with their sources and texture path rewrites");
        reloaded.Remove(preview.Id);
        Require(reloaded.Previews.Count == 0, "neck seam: an applied or discarded preview is forgotten");

        var modFolder = Path.Combine(testRoot, "NeckSeamMod");
        Directory.CreateDirectory(modFolder);
        var target = Path.Combine(modFolder, "face.mtrl");
        File.WriteAllBytes(target, [1, 2, 3]);
        var backup = new ModelBackupStore(Path.Combine(testRoot, "NeckSeamBackups")).Create(target, "Skin Mod", "material/face.mtrl");
        Require(File.Exists(backup) && backup.EndsWith(".bak", StringComparison.Ordinal), "neck seam: materials can be backed up before they are overwritten");
    }
}
