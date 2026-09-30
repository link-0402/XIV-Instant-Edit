using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using Lumina.Data.Files;
using static InstantEdit.TestSupport.Assertions;

/// <summary> The Substance Painter round trip's pure parts: mesh export, TGA handling, coverage, channel map, job store. </summary>
internal static class PainterScenarios
{
    public static void Run(string testRoot)
    {
        MeshReaderDecodesLodZero();
        ObjWriterFlipsAndScales();
        TgaRoundTrips();
        CoverageKeepsUncoveredTexels();
        CharacterChannelLayout();
        SkinAndHairLayouts();
        CollisionsAndExhaustion();
        NormalSeedRebuildsZ();
        RulesExplainUneditableTextures();
        JobStoreRoundTrips(Path.Combine(testRoot, "PainterJobs"));
        NonSquareTexturesGetSquareSets(Path.Combine(testRoot, "PainterExports"));
        BackFaceCopiesAreDropped();
        VisibilityFollowsAttributes();
        DisplayFollowsMaterialFlags();
        PreviewsColorHairAndColorsets();
        SkinMaterialsAreTold();
        SmallclothesFollowTheGamesRules();
        SkinProjectsNeedOneBodyMaterial();
        SkinLookupFollowsTheCollection(Path.Combine(testRoot, "PainterSkin"));
        OptionalSetsStayOutUntilTicked();
        PluginShipsWithTheAssembly();
    }

    /// <summary> The Painter plugin is embedded for the Settings installer and carries this plugin's version. </summary>
    private static void PluginShipsWithTheAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Tools", "SubstancePainter", "xiv_instant_edit")))
            directory = directory.Parent;
        Require(directory is not null, "the Painter plugin sources are found from the test output");
        var package = Path.Combine(directory!.FullName, "Tools", "SubstancePainter", "xiv_instant_edit");
        var resources = typeof(PainterInstallation).Assembly.GetManifestResourceNames();
        foreach (var file in Directory.EnumerateFiles(package, "*.py"))
            Require(resources.Contains("InstantEdit.PainterPlugin." + Path.GetFileName(file)), $"{Path.GetFileName(file)} is embedded for the installer");
        var init = File.ReadAllText(Path.Combine(package, "__init__.py"));
        var match = System.Text.RegularExpressions.Regex.Match(init, "PLUGIN_VERSION = \"([^\"]+)\"");
        var version = typeof(PainterInstallation).Assembly.GetName().Version!;
        Require(match.Success && match.Groups[1].Value == $"{version.Major}.{version.Minor}.{version.Build}",
            "the Painter plugin's PLUGIN_VERSION matches the plugin version");
    }

    // ---- Synthetic Dawntrail model --------------------------------------------------------------

    private sealed record Element(byte Stream, byte Offset, byte Type, byte Usage, byte UsageIndex = 0);

    private sealed record MeshSpec(Element[] Elements, byte[][] Streams, byte[] Strides, int VertexCount, ushort[] Indices,
        ushort Material, (int Start, int Count, uint Mask)[] Submeshes);

    /// <summary> Mesh 0: float3 positions, half4 normals and half2 UVs over two streams, two masked submeshes.
    /// Mesh 1: one float stream (position4, normal3, uv4) and no submeshes. Materials are listed in the
    /// offset table in reverse string order, so only the table maps mesh material indices correctly. </summary>
    internal static byte[] SyntheticModel()
    {
        var mesh0Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        var mesh0Uvs = new[] { new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f), new Vector2(0.75f, 0.25f), new Vector2(0.25f, 0.25f) };
        var stream0 = new byte[4 * 12];
        var stream1 = new byte[4 * 12];
        for (var v = 0; v < 4; v++)
        {
            WriteF32(stream0, v * 12, mesh0Positions[v].X, mesh0Positions[v].Y, mesh0Positions[v].Z);
            WriteF16(stream1, v * 12, 0, 0, 2, 1); // not unit length: the reader normalizes
            WriteF16(stream1, v * 12 + 8, mesh0Uvs[v].X, mesh0Uvs[v].Y);
        }
        var mesh1 = new byte[3 * 44];
        var mesh1Positions = new[] { new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(2, 1, 0) };
        for (var v = 0; v < 3; v++)
        {
            WriteF32(mesh1, v * 44, mesh1Positions[v].X, mesh1Positions[v].Y, mesh1Positions[v].Z, 1);
            WriteF32(mesh1, v * 44 + 16, 0, 1, 0);
            WriteF32(mesh1, v * 44 + 28, 0.1f * v, 0.2f * v, 0, 0);
        }
        var meshes = new[]
        {
            new MeshSpec(
                [new Element(0, 0, 2, 0), new Element(1, 0, 14, 3), new Element(1, 8, 13, 4)],
                [stream0, stream1], [12, 12], 4, [0, 1, 2, 0, 2, 3], 0, [(0, 3, 0b01), (3, 3, 0b10)]),
            new MeshSpec(
                [new Element(0, 0, 3, 0), new Element(0, 16, 2, 3), new Element(0, 28, 3, 4)],
                [mesh1], [44], 3, [0, 1, 2], 1, []),
        };
        return BuildModel(meshes, ["atr_a", "atr_b"], ["/mt_a.mtrl", "/mt_b.mtrl"], materialTableOrder: [1, 0]);
    }

    private static byte[] BuildModel(MeshSpec[] meshes, string[] attributes, string[] materialStrings, int[] materialTableOrder)
    {
        // String block: attributes then materials.
        var strings = new List<byte>();
        var offsets = new Dictionary<string, int>();
        foreach (var s in attributes.Concat(materialStrings))
        {
            offsets[s] = strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(s));
            strings.Add(0);
        }
        while (strings.Count % 4 != 0) strings.Add(0);

        var vertexData = new List<byte>();
        var streamOffsets = new List<int[]>();
        foreach (var mesh in meshes)
        {
            var offsetsForMesh = new int[3];
            for (var s = 0; s < mesh.Streams.Length; s++)
            {
                offsetsForMesh[s] = vertexData.Count;
                vertexData.AddRange(mesh.Streams[s]);
            }
            streamOffsets.Add(offsetsForMesh);
        }
        var indexData = new List<byte>();
        var starts = new List<int>();
        foreach (var mesh in meshes)
        {
            starts.Add(indexData.Count / 2);
            foreach (var index in mesh.Indices) { indexData.Add((byte)index); indexData.Add((byte)(index >> 8)); }
        }
        var submeshes = meshes.SelectMany((mesh, m) => mesh.Submeshes.Select(sub => (Start: starts[m] + sub.Start, sub.Count, sub.Mask))).ToList();

        var header = new byte[68];
        var declarations = new byte[meshes.Length * 17 * 8];
        for (var m = 0; m < meshes.Length; m++)
        {
            var at = m * 17 * 8;
            foreach (var element in meshes[m].Elements)
            {
                declarations[at] = element.Stream; declarations[at + 1] = element.Offset; declarations[at + 2] = element.Type;
                declarations[at + 3] = element.Usage; declarations[at + 4] = element.UsageIndex;
                at += 8;
            }
            declarations[at] = 0xFF;
        }
        var stringHeader = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(stringHeader, (ushort)(attributes.Length + materialStrings.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(stringHeader.AsSpan(4), (uint)strings.Count);
        var meshHeader = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(4), (ushort)meshes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(6), (ushort)attributes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(8), (ushort)submeshes.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(10), (ushort)materialStrings.Length);
        meshHeader[22] = 1;
        var lods = new byte[3 * 60];
        BinaryPrimitives.WriteUInt16LittleEndian(lods.AsSpan(2), (ushort)meshes.Length);
        var meshTable = new byte[meshes.Length * 36];
        var submeshIndex = 0;
        for (var m = 0; m < meshes.Length; m++)
        {
            var at = m * 36;
            var mesh = meshes[m];
            BinaryPrimitives.WriteUInt16LittleEndian(meshTable.AsSpan(at), (ushort)mesh.VertexCount);
            BinaryPrimitives.WriteUInt32LittleEndian(meshTable.AsSpan(at + 4), (uint)mesh.Indices.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(meshTable.AsSpan(at + 8), mesh.Material);
            BinaryPrimitives.WriteUInt16LittleEndian(meshTable.AsSpan(at + 10), (ushort)submeshIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(meshTable.AsSpan(at + 12), (ushort)mesh.Submeshes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(meshTable.AsSpan(at + 16), (uint)starts[m]);
            for (var s = 0; s < 3; s++)
                BinaryPrimitives.WriteUInt32LittleEndian(meshTable.AsSpan(at + 20 + s * 4), (uint)streamOffsets[m][s]);
            for (var s = 0; s < mesh.Strides.Length; s++)
                meshTable[at + 32 + s] = mesh.Strides[s];
            meshTable[at + 35] = (byte)mesh.Streams.Length;
            submeshIndex += mesh.Submeshes.Length;
        }
        var attributeTable = new byte[attributes.Length * 4];
        for (var a = 0; a < attributes.Length; a++)
            BinaryPrimitives.WriteUInt32LittleEndian(attributeTable.AsSpan(a * 4), (uint)offsets[attributes[a]]);
        var submeshTable = new byte[submeshes.Count * 16];
        for (var s = 0; s < submeshes.Count; s++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(submeshTable.AsSpan(s * 16), (uint)submeshes[s].Start);
            BinaryPrimitives.WriteUInt32LittleEndian(submeshTable.AsSpan(s * 16 + 4), (uint)submeshes[s].Count);
            BinaryPrimitives.WriteUInt32LittleEndian(submeshTable.AsSpan(s * 16 + 8), submeshes[s].Mask);
        }
        var materialTable = new byte[materialStrings.Length * 4];
        for (var i = 0; i < materialTableOrder.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(materialTable.AsSpan(i * 4), (uint)offsets[materialStrings[materialTableOrder[i]]]);

        var body = new List<byte>();
        body.AddRange(declarations);
        body.AddRange(stringHeader);
        body.AddRange(strings);
        body.AddRange(meshHeader);
        body.AddRange(lods);
        body.AddRange(meshTable);
        body.AddRange(attributeTable);
        body.AddRange(submeshTable);
        body.AddRange(materialTable);
        var vertexOffset = 68 + body.Count;
        var indexOffset = vertexOffset + vertexData.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x01000006);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)meshes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)materialStrings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)vertexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)vertexData.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)indexData.Count);
        header[64] = 1;
        return header.Concat(body).Concat(vertexData).Concat(indexData).ToArray();
    }

    private static void WriteF32(byte[] bytes, int at, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + i * 4), values[i]);
    }

    private static void WriteF16(byte[] bytes, int at, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + i * 2), BitConverter.HalfToInt16Bits((Half)values[i]));
    }

    // ---- Mesh reader and OBJ --------------------------------------------------------------------

    private static void MeshReaderDecodesLodZero()
    {
        var mesh = ModelMeshReader.Read(SyntheticModel());
        Require(mesh.Materials.SequenceEqual(["/mt_b.mtrl", "/mt_a.mtrl"]), "material names follow the offset table, not the string order");
        Require(mesh.Attributes.SequenceEqual(["atr_a", "atr_b"]), "attribute names come from their offset table");
        Require(mesh.Meshes.Count == 2 && mesh.Meshes[0].MaterialIndex == 0 && mesh.Meshes[1].MaterialIndex == 1, "both LOD-0 meshes keep their material index");
        var first = mesh.Meshes[0];
        Require(first.Positions[2] == new Vector3(1, 1, 0) && first.Uvs[1] == new Vector2(0.75f, 0.75f), "float positions and half UVs decode across two streams");
        Require(Math.Abs(first.Normals[0].Length() - 1) < 1e-5 && first.Normals[0].Z > 0.99f, "half normals decode and are normalized");
        Require(first.Submeshes.Count == 2 && first.Submeshes[0].Indices.SequenceEqual([0, 1, 2]) && first.Submeshes[1].Indices.SequenceEqual([0, 2, 3]),
            "submesh index ranges come from the submesh table");
        Require(first.Submeshes[0].AttributeMask == 1 && first.Submeshes[1].AttributeMask == 2, "submesh attribute masks are kept");
        var second = mesh.Meshes[1];
        Require(second.Submeshes.Count == 1 && second.Submeshes[0].Indices.SequenceEqual([0, 1, 2]) && second.Submeshes[0].SubmeshIndex == -1,
            "a mesh without submeshes becomes one submesh over its index range");
        Require(second.Positions[1] == new Vector3(3, 0, 0) && Math.Abs(second.Uvs[2].Y - 0.4f) < 1e-6, "a single float4 stream decodes position and UV");
        Reject(() => ModelMeshReader.Read(SyntheticModel()[..200]), "a truncated model is rejected");
    }

    private static void ObjWriterFlipsAndScales()
    {
        var mesh = ModelMeshReader.Read(SyntheticModel());
        var groups = new List<ObjGroup>();
        foreach (var part in mesh.Meshes)
            foreach (var submesh in part.Submeshes)
                groups.Add(new ObjGroup(ObjWriter.GroupName("c0101e0001_top", part, submesh, mesh.Attributes), part, submesh,
                    part.MaterialIndex == 0 ? "mt_b" : "mt_a"));
        var writer = new StringWriter();
        ObjWriter.Write(writer, "model.mtl", groups);
        var lines = writer.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Require(lines.Contains("mtllib model.mtl"), "the OBJ references its material library");
        Require(lines.Contains("v 100 100 0") && lines.Contains("v 300 0 0"), "positions go from metres to centimetres");
        Require(lines.Contains("vt 0.25 0.25") && lines.Contains("vt 0.75 0.75"), "V flips to OBJ's bottom-left origin");
        Require(lines.Contains("o c0101e0001_top 0.0 [atr_a]") && lines.Contains("o c0101e0001_top 0.1 [atr_b]") && lines.Contains("o c0101e0001_top 1.0"),
            "each submesh is an object named after its mesh, index and attributes");
        Require(lines.Count(line => line == "usemtl mt_b") == 2 && lines.Count(line => line == "usemtl mt_a") == 1, "each object uses its texture set");
        Require(lines.Contains("f 1/1/1 3/3/3 4/4/4") && lines.Contains("f 5/5/5 6/6/6 7/7/7"), "faces index each mesh's own vertices, 1-based");
        Require(lines.Count(line => line.StartsWith("v ", StringComparison.Ordinal)) == 7, "a mesh's vertices are written once for all its submeshes");
        var mtl = new StringWriter();
        ObjWriter.WriteMaterialLibrary(mtl, groups.Select(g => g.Material));
        Require(mtl.ToString().Replace("\r", "") == "newmtl mt_b\nnewmtl mt_a\n", "the material library lists each texture set once");
    }

    // ---- TGA and coverage -----------------------------------------------------------------------

    private static void TgaRoundTrips()
    {
        var image = new RgbaImage(3, 2);
        for (var i = 0; i < image.Pixels.Length; i++)
            image.Pixels[i] = (byte)(i * 7);
        var bgra = TgaImage.WriteBgra32(image);
        Require(TextureFiles.ValidateTga(bgra) == (3, 2), "written TGAs pass the session validator");
        Require(TgaImage.Read(bgra).Pixels.SequenceEqual(image.Pixels), "a 32-bit TGA reads back unchanged");

        // Painter writes 24-bit, bottom-left files when a map's alpha is constant white.
        var painter = new byte[18 + 3 * 2 * 3];
        painter[2] = 2; painter[12] = 3; painter[14] = 2; painter[16] = 24; painter[17] = 0;
        for (var i = 0; i < 3; i++) { painter[18 + i * 3] = 10; painter[18 + i * 3 + 1] = 20; painter[18 + i * 3 + 2] = 30; } // bottom row
        for (var i = 3; i < 6; i++) { painter[18 + i * 3] = 40; painter[18 + i * 3 + 1] = 50; painter[18 + i * 3 + 2] = 60; } // top row
        var read = TgaImage.Read(painter);
        Require(read.Pixels[0] == 60 && read.Pixels[1] == 50 && read.Pixels[2] == 40 && read.Pixels[3] == 255,
            "a 24-bit bottom-left TGA reads top-down, as RGB, with opaque alpha");
        Require(read.Pixels[12] == 30 && read.Pixels[14] == 10, "the bottom row lands last");

        var rle = new byte[] { 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 32, 0x28, 0x81, 1, 2, 3, 4 };
        Require(TgaImage.Read(rle).Pixels.SequenceEqual(new byte[] { 3, 2, 1, 4, 3, 2, 1, 4 }), "run-length packets expand");
        var gray = TgaImage.Read(TgaImage.WriteChannel(image, 3));
        Require(gray.Pixels[0] == image.Pixels[3] && gray.Pixels[4] == image.Pixels[7] && gray.Pixels[3] == 255, "a grayscale seed holds one component");
        var rgb = TgaImage.Read(TgaImage.WriteRgb24(image));
        Require(rgb.Pixels[4] == image.Pixels[4] && rgb.Pixels[6] == image.Pixels[6] && rgb.Pixels[7] == 255, "a color seed keeps RGB and drops alpha");
        Require(new RgbaImage(3, 2, (byte[])image.Pixels.Clone()).PixelHash() == image.PixelHash() &&
                new RgbaImage(2, 3, (byte[])image.Pixels.Clone()).PixelHash() != image.PixelHash(), "pixel hashes cover size and pixels");
        Reject(() => TgaImage.Read(rle[..20]), "truncated run-length data is rejected");
    }

    private static void CoverageKeepsUncoveredTexels()
    {
        // One triangle over the left half of an 8 x 8 texture (UV origin top-left).
        var triangles = new[] { (new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(0, 1)) };
        var tight = UvCoverage.Rasterize(8, 8, triangles, 0);
        Require(tight.IsCovered(0, 0) && tight.IsCovered(1, 4) && !tight.IsCovered(6, 1) && !tight.IsCovered(7, 7), "texel centres inside the triangle are covered");
        var margin = UvCoverage.Rasterize(8, 8, triangles, 1);
        Require(margin.CoveredCount > tight.CoveredCount && margin.IsCovered(4, 0) && !margin.IsCovered(7, 7), "the margin grows the coverage by whole texels");
        var wrapped = UvCoverage.Rasterize(8, 8, [(new Vector2(1.0f, 0), new Vector2(1.5f, 0), new Vector2(1.0f, 1))], 0);
        Require(wrapped.IsCovered(0, 0) && wrapped.IsCovered(1, 4), "UVs outside the tile wrap");
        var painter = new RgbaImage(8, 8);
        Array.Fill(painter.Pixels, (byte)200);
        var current = new RgbaImage(8, 8);
        Array.Fill(current.Pixels, (byte)10);
        var merged = tight.Merge(painter, current);
        Require(merged.Pixels[0] == 200 && merged.Pixels[(1 * 8 + 7) * 4] == 10, "covered texels come from Painter, the rest stays");
        Require(UvCoverage.MarginFor(2048, 1024) == 8 && UvCoverage.MarginFor(256, 256) == 2, "the seam margin scales with the texture");
    }

    // ---- Channel map ----------------------------------------------------------------------------

    private const uint Bc7 = (uint)TexFile.TextureFormat.BC7;
    private const uint Bc5 = (uint)TexFile.TextureFormat.BC5;
    private const uint Bc1 = (uint)TexFile.TextureFormat.BC1;

    private static PainterTextureLayout LayoutOf(PainterSetLayout set, string usage)
        => set.Textures.First(t => t.Texture.Usage == usage);

    private static string ChannelOf(PainterTextureLayout layout, int component)
        => layout.Components.FirstOrDefault(c => c.Component == component)?.Channel ?? "";

    private static void CharacterChannelLayout()
    {
        var set = PainterChannelMap.Layout(new PainterTextureSetInput("mt_top", "character.shpk",
        [
            new PainterTextureInput("top_id", "index", Bc5, 1024, 1024, "t3"),
            new PainterTextureInput("", "specular", Bc7, 1024, 1024, "t4"),
            new PainterTextureInput("top_base", "diffuse", Bc7, 2048, 2048, "t0"),
            new PainterTextureInput("top_norm", "normal", Bc5, 2048, 1024, "t1"),
            new PainterTextureInput("top_mask", "mask", Bc7, 1024, 1024, "t2"),
        ]));
        var diffuse = LayoutOf(set, "diffuse");
        var normal = LayoutOf(set, "normal");
        var mask = LayoutOf(set, "mask");
        var index = LayoutOf(set, "index");
        Require(ChannelOf(diffuse, 0) == "BaseColor" && ChannelOf(diffuse, 2) == "BaseColor" && ChannelOf(diffuse, 3) == "User0",
            "gear diffuse RGB goes to base color and its alpha to a user channel");
        Require(ChannelOf(normal, 0) == "Normal" && ChannelOf(normal, 1) == "Normal" && normal.Components.Count == 2,
            "a BC5 normal map only carries RG, which go to the normal channel");
        Require(ChannelOf(mask, 0) == "Specularlevel" && ChannelOf(mask, 1) == "Roughness" && ChannelOf(mask, 2) == "AO" && ChannelOf(mask, 3) == "User1",
            "Dawntrail mask channels map to specular level, roughness, AO and a user channel");
        Require(ChannelOf(index, 0) == "User2" && ChannelOf(index, 1) == "User3", "an index map lives in user channels");
        Require(set.Textures.First(t => t.Texture.Usage == "specular").Components.All(c => c.Channel == "Specular"),
            "a reference-only texture still fills a free semantic channel");
        Require(set.Width == 2048 && set.Height == 2048, "the texture set is as large as its largest texture");
        Require(set.RemoveChannels.SequenceEqual(["Metallic"]), "Metallic is removed while roughness is used");
        Require(set.AddChannels.Any(c => c.Type == "User0" && c.Format == "L8" && c.Label == "diffuse.a") &&
                set.AddChannels.Any(c => c.Type == "BaseColor" && c.Format == "sRGB8"), "added channels carry formats and labels");

        var seeds = PainterChannelMap.Seeds(set);
        Require(seeds.Count(s => s.Texture.Key == "top_base") == 2 && seeds.Any(s => s.FileName == "t0.rgb.tga" && s.ColorSpace == "color") &&
                seeds.Any(s => s.FileName == "t0.a.tga" && s.ColorSpace == "data" && s.Channel == "User0"),
            "diffuse seeds are one color image plus its alpha");
        Require(seeds.Single(s => s.Texture.Key == "top_norm").Kind == PainterSeedKind.Normal, "normal maps seed the normal channel");

        var export = PainterChannelMap.ExportConfig([set]);
        var presets = export["exportPresets"]!.AsArray();
        Require(presets.All(p => p!["maps"]!.AsArray().Count == 1),
            "each texture gets a preset of its own, since Painter applies a preset's first map parameters to all of its maps");
        var maps = presets.Select(p => p!["maps"]![0]!).ToList();
        Require(maps.Count == 4 && maps.All(m => m["parameters"]!["fileFormat"]!.GetValue<string>() == "tga"),
            "every exported texture gets one TGA map; reference-only textures are not exported");
        Require(export["exportList"]!.AsArray().All(e => e!["rootPath"]!.GetValue<string>() == "mt_top") && export["exportList"]!.AsArray().Count == 4,
            "each preset is listed against its texture set");
        var normalMap = maps.Single(m => m!["fileName"]!.GetValue<string>() == "top_norm")!;
        var channels = normalMap["channels"]!.AsArray();
        Require(channels[0]!["srcMapName"]!.GetValue<string>() == "Normal_OpenGL" && channels[0]!["srcMapType"]!.GetValue<string>() == "virtualMap" &&
                channels[2]!["srcMapName"]!.GetValue<string>() == "black" && channels[3]!["srcMapName"]!.GetValue<string>() == "white",
            "normal RG come from Normal_OpenGL; components BC5 doesn't store are constants");
        var size = normalMap["parameters"]!["sizeLog2"]!.AsArray();
        Require(size[0]!.GetValue<int>() == 11 && size[1]!.GetValue<int>() == 10, "each map exports at its own texture's size");
        var maskMap = maps.Single(m => m!["fileName"]!.GetValue<string>() == "top_mask")!;
        Require(maskMap["channels"]!.AsArray().Select(c => c!["srcMapName"]!.GetValue<string>()).SequenceEqual(["specularlevel", "roughness", "ambientOcclusion", "user1"]),
            "mask components export from their homes");
        Require(maskMap["parameters"]!["paddingAlgorithm"]!.GetValue<string>() == "infinite" && maskMap["parameters"]!["bitDepth"]!.GetValue<string>() == "8",
            "maps export as 8-bit with infinite padding");
        var config = PainterChannelMap.ExportConfig([set, PainterChannelMap.Layout(new PainterTextureSetInput("mt_ref", "character.shpk",
            [new PainterTextureInput("", "diffuse", Bc7, 512, 512, "r0")]))]);
        Require(config["exportList"]!.AsArray().Count == 4 && config["exportList"]!.AsArray().All(e => e!["rootPath"]!.GetValue<string>() != "mt_ref") &&
                !config.ContainsKey("exportPath"), "sets without exported textures are left out, and the export path is Painter's to set");
    }

    private static void SkinAndHairLayouts()
    {
        var skin = PainterChannelMap.Layout(new PainterTextureSetInput("skin", "skin.shpk",
        [
            new PainterTextureInput("body_d", "diffuse", Bc7, 1024, 1024, "a"),
            new PainterTextureInput("body_n", "normal", Bc7, 1024, 1024, "b"),
            new PainterTextureInput("body_m", "mask", Bc7, 1024, 1024, "c"),
        ]));
        Require(ChannelOf(LayoutOf(skin, "diffuse"), 3) == "Opacity" && ChannelOf(LayoutOf(skin, "normal"), 2) == "User0" &&
                ChannelOf(LayoutOf(skin, "mask"), 2) == "Scattering", "skin keeps opacity in diffuse alpha and SSS in mask blue");
        var hair = PainterChannelMap.Layout(new PainterTextureSetInput("hair", "hair.shpk",
        [
            new PainterTextureInput("hair_n", "normal", Bc7, 1024, 1024, "a"),
            new PainterTextureInput("hair_m", "mask", Bc7, 1024, 1024, "b"),
        ]));
        Require(ChannelOf(LayoutOf(hair, "normal"), 3) == "Opacity" && ChannelOf(LayoutOf(hair, "mask"), 3) == "AO" &&
                ChannelOf(LayoutOf(hair, "normal"), 2) == "User0", "hair opacity is normal alpha and its occlusion is mask alpha");
        var legacy = PainterChannelMap.Layout(new PainterTextureSetInput("legacy", "characterlegacy.shpk",
            [new PainterTextureInput("old_m", "mask", Bc7, 512, 512, "a")]));
        Require(ChannelOf(LayoutOf(legacy, "mask"), 1) == "Glossiness" && legacy.RemoveChannels.Contains("Roughness"),
            "legacy masks hold gloss, so the unused roughness channel goes");
    }

    private static void CollisionsAndExhaustion()
    {
        var set = PainterChannelMap.Layout(new PainterTextureSetInput("two", "character.shpk",
        [
            new PainterTextureInput("n1", "normal", Bc7, 512, 512, "a"),
            new PainterTextureInput("n2", "normal", Bc7, 512, 512, "b"),
        ]));
        var second = set.Textures.First(t => t.Texture.Key == "n2");
        Require(second.Components.All(c => c.Channel.StartsWith("User", StringComparison.Ordinal)) && second.Components.Count == 4,
            "a second normal map falls back to user channels");
        var full = PainterChannelMap.Layout(new PainterTextureSetInput("full", "unknown.shpk",
        [
            new PainterTextureInput("m1", "mask", Bc7, 512, 512, "a"),
            new PainterTextureInput("m2", "mask", Bc7, 512, 512, "b"),
            new PainterTextureInput("m3", "mask", Bc7, 512, 512, "c"),
        ]));
        Require(full.Textures.Count(t => t.Exported) == 2 && full.Textures.Single(t => !t.Exported).Problem.Length > 0,
            "a texture that finds no free channel is not exported, with a reason");
        Require(PainterChannelMap.StoredComponents(Bc1).Length == 4 && PainterChannelMap.StoredComponents((uint)TexFile.TextureFormat.BC4).SequenceEqual([0]),
            "BC1 keeps its 1-bit alpha; BC4 stores red only");
    }

    private static void NormalSeedRebuildsZ()
    {
        var image = new RgbaImage(2, 1, [128, 128, 7, 9, 255, 128, 0, 0]);
        var seed = PainterChannelMap.NormalSeed(image);
        Require(seed.Pixels[2] == 255 && seed.Pixels[3] == 255, "a flat normal gets Z = 1 and opaque alpha");
        Require(seed.Pixels[6] == 128 && seed.Pixels[4] == 255, "a normal along +X gets Z = 0 and keeps its RG");
    }

    // ---- Rules and store ------------------------------------------------------------------------

    private static TexturePlanTexture Planned(string usage, string gamePath, string kind = "mod", uint format = Bc7, int size = 1024, int uvSet = 0)
        => new(0, usage, uvSet, gamePath, "", kind.Length == 0 ? null : new SourceResourceLocator { Kind = kind, GamePath = gamePath, Sha256 = "" },
            format, size, size, "");

    private static void RulesExplainUneditableTextures()
    {
        Require(PainterRules.EditReason(Planned("diffuse", "chara/equipment/e0001/texture/a_d.tex")).Length == 0, "a modded gear texture can be sent back");
        Require(PainterRules.EditReason(Planned("normal", "chara/common/texture/tile_norm.tex")).Length > 0 &&
                PainterRules.EditReason(Planned("normal", "--common/graphics/texture/x.tex")).Length > 0, "shared game textures stay in Painter");
        Require(PainterRules.EditReason(Planned("decal", "chara/x.tex")).Length > 0, "decals and other shared samplers are not offered");
        Require(PainterRules.EditReason(Planned("diffuse", "chara/x.tex", uvSet: 1)).Length > 0, "second-UV-set textures are not offered");
        Require(PainterRules.EditReason(Planned("diffuse", "chara/x.tex", kind: "")).Length > 0, "external files are not offered");
        Require(PainterRules.EditReason(Planned("diffuse", "chara/x.tex", format: (uint)TexFile.TextureFormat.BC2)).Length > 0, "formats sessions can't keep are not offered");
        Require(PainterRules.EditReason(Planned("diffuse", "chara/x.tex", size: 1026)).Length > 0, "compressed sizes must divide by 4");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Require(PainterRules.UniqueName("--c0101e0001_top_norm", used, "t") == "--c0101e0001_top_norm" &&
                PainterRules.UniqueName("--C0101E0001_TOP_NORM", used, "t") == "--C0101E0001_TOP_NORM_2" &&
                PainterRules.UniqueName("my tex (1)", used, "t") == "my_tex__1" && PainterRules.UniqueName("¤¤", used, "t") == "t",
            "names are made safe and unique, case-insensitively");
    }

    private static void JobStoreRoundTrips(string root)
    {
        Directory.CreateDirectory(root);
        var store = new PainterJobStore(root);
        var session = Guid.NewGuid();
        var job = new PainterJob
        {
            Id = Guid.NewGuid(),
            Capability = PainterJobStore.NewCapability(),
            DisplayName = "Player – top",
            Created = DateTimeOffset.UtcNow,
            Targets =
            [
                new PainterTarget
                {
                    Key = "top_base", TextureSet = "mt_top", GamePath = "chara/a.tex", SessionId = session, Width = 1024, Height = 1024,
                    ExportWidth = 2048, ExportHeight = 1024,
                    Protect = true, Coverage = [new PainterCoverageModel("chara/a.mdl", true, [0, 2], [0b101])],
                },
            ],
        };
        store.Save(job);
        store.Update(() => job.Targets[0].BaselineHash = "ABC");
        var reloaded = new PainterJobStore(root);
        reloaded.Load();
        var copy = reloaded.Find(job.Id);
        Require(copy is not null && copy.Targets[0].BaselineHash == "ABC" && copy.Targets[0].Coverage[0].Meshes.SequenceEqual([0, 2]) &&
                copy.Targets[0].Coverage[0].Vanilla, "jobs survive a reload with their targets and coverage");
        Require(copy!.Targets[0].ExportWidth == 2048 && copy.Targets[0].Coverage[0].AttributeMasks!.SequenceEqual([0b101u]) &&
                !copy.Targets[0].Coverage[0].Draws(new ModelSubmesh(0, [], 0b010)) && copy.Targets[0].Coverage[0].Draws(new ModelSubmesh(0, [], 0b100)),
            "export sizes and the character's attribute masks survive a reload");

        // Projects written before square sets and attribute masks load with neither.
        var legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "PainterJobs.json"), $$"""
            {"schema":"instant-edit.painter-jobs","version":1,"jobs":[{"Id":"{{Guid.NewGuid()}}","Capability":"c","Targets":[{"Key":"k","Width":512,"Height":512,
             "Protect":true,"Coverage":[{"Source":"chara/b.mdl","Vanilla":true,"Meshes":[1]}]}]}]}
            """);
        var old = new PainterJobStore(legacy);
        old.Load();
        var oldTarget = old.Jobs.Single().Targets[0];
        Require(old.LoadError.Length == 0 && oldTarget.ExportWidth == 0 && oldTarget.Coverage[0].AttributeMasks is null &&
                oldTarget.Coverage[0].Draws(new ModelSubmesh(0, [], 0b1)), "older projects load, uncropped and with every part drawn");
        Require(reloaded.Authorize(job.Id.ToString("N"), job.Capability) is not null, "the job's capability authorizes it");
        Require(reloaded.Authorize(job.Id.ToString("N"), job.Capability + "x") is null && reloaded.Authorize(Guid.NewGuid().ToString("N"), job.Capability) is null &&
                reloaded.Authorize("not-a-guid", job.Capability) is null, "a wrong capability or job id is refused");
        Require(reloaded.LinksSession(session) && !reloaded.LinksSession(Guid.NewGuid()), "stores know which sessions a project links");
        Require(reloaded.Remove(job.Id) && reloaded.Jobs.Count == 0, "a discarded job is removed");
        File.WriteAllText(Path.Combine(root, "PainterJobs.json"), "{ not json");
        var broken = new PainterJobStore(root);
        broken.Load();
        Require(broken.LoadError.Length > 0, "a damaged job file is reported");
        Reject(() => broken.Save(job), "a damaged job file is not overwritten");
    }

    // ---- What Painter shows ---------------------------------------------------------------------

    private static void NonSquareTexturesGetSquareSets(string root)
    {
        var tall = PainterChannelMap.Layout(new PainterTextureSetInput("face", "skin.shpk",
        [
            new PainterTextureInput("face_d", "diffuse", Bc7, 1024, 2048, "a"),
            new PainterTextureInput("face_m", "mask", Bc7, 512, 1024, "b"),
        ]));
        Require(tall.Width == 1024 && tall.Height == 2048 && tall.Size == 2048 && tall.ScaleU == 2 && tall.ScaleV == 1,
            "a 1:2 texture gets a square set twice its width, which it fills across half");
        var maps = PainterChannelMap.ExportConfig([tall])["exportPresets"]!.AsArray().Select(p => p!["maps"]![0]!).ToList();
        int[] SizeOf(string key) => maps.Single(m => m["fileName"]!.GetValue<string>() == key)["parameters"]!["sizeLog2"]!.AsArray()
            .Select(v => v!.GetValue<int>()).ToArray();
        Require(SizeOf("face_d").SequenceEqual([11, 11]) && SizeOf("face_m").SequenceEqual([10, 10]),
            "each texture exports the whole square set at the size whose top-left corner is the texture");
        var wide = PainterChannelMap.Layout(new PainterTextureSetInput("wide", "character.shpk",
            [new PainterTextureInput("w", "diffuse", Bc7, 2048, 512, "a")]));
        Require(wide.Size == 2048 && wide.ScaleU == 1 && wide.ScaleV == 4, "a 4:1 texture fills a quarter of its set's height");

        var mesh = ModelMeshReader.Read(SyntheticModel());
        var groups = mesh.Meshes.SelectMany(part => part.Submeshes.Select(submesh =>
            new ObjGroup("g", part, submesh, part.MaterialIndex == 0 ? "tall" : "wide"))).ToList();
        var writer = new StringWriter();
        ObjWriter.Write(writer, "m.mtl", groups, new Dictionary<string, (int, int)> { ["tall"] = (2, 1), ["wide"] = (1, 4) });
        var lines = writer.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Require(lines.Contains("vt 0.125 0.25") && lines.Contains("vt 0.375 0.75"), "U shrinks into the set's left part");
        Require(lines.Contains("vt 0 1") && lines.Contains("vt 0.1 0.95") && lines.Contains("vt 0.2 0.9"),
            "V shrinks into the set's top part, measured from OBJ's top");

        var export = new RgbaImage(4, 2);
        for (var i = 0; i < export.Pixels.Length; i++)
            export.Pixels[i] = (byte)i;
        var corner = export.Crop(2, 2);
        Require(corner.Width == 2 && corner.Pixels.Take(8).SequenceEqual(export.Pixels.Take(8)) && corner.Pixels.Skip(8).SequenceEqual(export.Pixels.Skip(16).Take(8)),
            "cropping keeps the top-left corner row by row");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "k.tga");
        File.WriteAllBytes(file, TgaImage.WriteBgra32(export));
        var job = new PainterJob { Targets = [new PainterTarget { Key = "k", Width = 2, Height = 2, ExportWidth = 4, ExportHeight = 2 }] };
        Require(PainterJobService.ReadExport(job, "k", file).PixelHash() == corner.PixelHash(), "a square set's export is cropped to its texture");
        var older = new PainterJob { Targets = [new PainterTarget { Key = "k", Width = 4, Height = 2 }] };
        Require(PainterJobService.ReadExport(older, "k", file).Width == 4, "older projects' exports are taken whole");
        var wrong = new PainterJob { Targets = [new PainterTarget { Key = "k", Width = 2, Height = 2, ExportWidth = 8, ExportHeight = 4 }] };
        Reject(() => PainterJobService.ReadExport(wrong, "k", file), "an export of another size is refused rather than cropped wrongly");
    }

    private static void BackFaceCopiesAreDropped()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(5, 5, 5) };
        var part = new ModelMeshPart(0, 0, positions, new Vector3[positions.Length], new Vector2[positions.Length],
        [
            // A card, its reversed back copy on separate vertices, the card again, and an unrelated triangle.
            new ModelSubmesh(0, [0, 1, 2, 3, 5, 4, 1, 2, 0, 0, 1, 6], 0),
        ]);
        var other = new ModelMeshPart(1, 1, positions, new Vector3[positions.Length], new Vector2[positions.Length], [new ModelSubmesh(0, [3, 5, 4], 0)]);
        var twoSided = new HashSet<string>();
        var result = ObjWriter.WithoutBackFaceCopies(
            [new ObjGroup("a", part, part.Submeshes[0], "cards"), new ObjGroup("b", other, other.Submeshes[0], "other")], twoSided);
        Require(result[0].Submesh.Indices.SequenceEqual([0, 1, 2, 0, 1, 6]), "reversed copies and repeats of a triangle are dropped");
        Require(result[1].Submesh.Indices.SequenceEqual([3, 5, 4]), "another material's triangles are compared separately");
        Require(twoSided.SetEquals(["cards"]), "a material that lost back copies is drawn double-sided");
        var untouched = ObjWriter.WithoutBackFaceCopies([new ObjGroup("b", other, other.Submeshes[0], "other")], twoSided);
        Require(ReferenceEquals(untouched[0].Submesh, other.Submeshes[0]), "groups without copies are kept as they are");
    }

    private static void VisibilityFollowsAttributes()
    {
        Require(PainterVisibility.Draws(null, new ModelSubmesh(0, [], 0b11)) && PainterVisibility.Draws([], new ModelSubmesh(0, [], 0b11)),
            "with nothing known, every part is drawn");
        Require(PainterVisibility.Draws([0b000], new ModelSubmesh(0, [], 0)), "parts without attributes are always drawn");
        Require(PainterVisibility.Draws([0b011], new ModelSubmesh(0, [], 0b011)) && !PainterVisibility.Draws([0b001], new ModelSubmesh(0, [], 0b011)),
            "a part is drawn only while all of its attributes are enabled");
        Require(PainterVisibility.Draws([0b001, 0b010], new ModelSubmesh(0, [], 0b010)), "a part drawn by any loaded copy of the model counts");
        Require(PainterVisibility.NormalizePath(@"|1_2_3|G:\Penumbra\My Mod\A.mdl") == "g:/penumbra/my mod/a.mdl" &&
                PainterVisibility.NormalizePath("chara/x.mdl") == "chara/x.mdl", "Penumbra's path prefix and separators are normalized");
        var live = new PainterLiveCharacter([new PainterLiveModel("g:/penumbra/my mod/a.mdl", 0b1), new PainterLiveModel("chara/v.mdl", 0b10),
            new PainterLiveModel("g:/penumbra/my mod/a.mdl", 0b100)], null);
        Require(live.MasksFor(new PainterModelRef("chara/a.mdl", @"G:\Penumbra\My Mod\A.mdl", false)).SequenceEqual([0b1u, 0b100u]) &&
                live.MasksFor(new PainterModelRef("chara/v.mdl", "chara/v.mdl", true)).SequenceEqual([0b10u]) &&
                live.MasksFor(new PainterModelRef("chara/w.mdl", "chara/w.mdl", true)).Count == 0,
            "loaded models match by their file, or by game path for vanilla ones");
    }

    private static void DisplayFollowsMaterialFlags()
    {
        Require(PainterDisplay.For(0x1D, 1f, true, false) == new PainterDisplay("blend", 0.5f, false), "translucent materials blend by opacity");
        Require(PainterDisplay.For(0x1D, 1f, true, true).DoubleSided, "a material whose back copies were dropped shows both sides");
        Require(PainterDisplay.For(0x0D, 0.5f, true, false) == new PainterDisplay("test", 0.5f, false), "the others cut off below the alpha threshold");
        Require(PainterDisplay.For(0x0C, 0.25f, true, false) == new PainterDisplay("test", 0.25f, true), "without the hide flag back faces show");
        Require(PainterDisplay.For(0x0D, 0f, true, false) == PainterDisplay.Default && PainterDisplay.For(0x0D, null, true, false) == PainterDisplay.Default,
            "a threshold of 0 draws opaque");
        Require(PainterDisplay.For(0x1D, 1f, false, false) == PainterDisplay.Default, "sets without an opacity channel draw opaque");
        Require(PainterDisplay.For(null, 0.5f, true, false) == PainterDisplay.Default, "unknown flags draw opaque and single-sided");
    }

    private static void PreviewsColorHairAndColorsets()
    {
        var colors = new PainterCharacterColors(new Vector3(1, 0, 0), new Vector3(0, 0, 1));
        var hair = PainterPreviews.Hair(new RgbaImage(2, 1, [128, 128, 0, 255, 128, 128, 255, 0]), colors);
        Require(hair.Pixels.SequenceEqual(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }), "hair color blends to the highlight color by normal blue");

        var values = new float[32 * 32];
        void Row(int row, float r, float g, float b) { values[row * 32] = r; values[row * 32 + 1] = g; values[row * 32 + 2] = b; }
        Row(0, 1, 0, 0);
        Row(1, 0, 1, 0);
        Row(2, 0, 0, 1);
        Row(3, 0.214f, 0.214f, 0.214f);
        var index = new RgbaImage(3, 1, [0, 255, 0, 255, 0, 0, 0, 255, 17, 255, 0, 255]);
        var preview = PainterPreviews.ColorSet(index, new TexturePlanColorSet(32, values));
        Require(preview.Pixels.Take(4).SequenceEqual(new byte[] { 255, 0, 0, 255 }) && preview.Pixels.Skip(4).Take(4).SequenceEqual(new byte[] { 0, 255, 0, 255 }),
            "index red picks a row pair and inverted index green blends toward its second row");
        Require(preview.Pixels[8] == 0 && preview.Pixels[10] == 255, "index red 17 picks the second pair");
        Require(Math.Abs(PainterPreviews.ColorSet(new RgbaImage(1, 1, [17, 0, 0, 255]), new TexturePlanColorSet(32, values)).Pixels[0] - 128) <= 1,
            "colorset colors are linear and preview in sRGB");
    }

    // ---- Skin projects --------------------------------------------------------------------------

    private const string BodySkinPath = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
    private const string FaceSkinPath = "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_fac_a.mtrl";

    private static TexturePlanTexture Sourced(string usage, string gamePath)
        => new(0, usage, 0, gamePath, gamePath, new SourceResourceLocator { Kind = "game", GamePath = gamePath, Sha256 = "" }, Bc7, 1024, 1024, "");

    /// <summary> A read material whose first texture is its diffuse and the others normal maps. </summary>
    private static TexturePlanMaterial Material(string name, string gamePath, string shader, uint? skinType, params string[] textures)
        => new(name, gamePath, gamePath, shader, textures.Select((texture, i) => Sourced(i == 0 ? "diffuse" : "normal", texture)).ToList(), "")
        {
            ShaderKeys = skinType is { } type ? new Dictionary<uint, uint> { [SkinMaterial.SkinTypeKey] = type } : new Dictionary<uint, uint>(),
        };

    /// <summary> A model with one mesh per material; each submesh is one triangle with the given attribute mask. </summary>
    private static ModelMesh Parts(params (string Material, uint[] Submeshes)[] parts)
        => new(parts.Select((part, index) => new ModelMeshPart(index, index, new Vector3[3], new Vector3[3], new Vector2[3],
                part.Submeshes.Select((mask, s) => new ModelSubmesh(s, [0, 1, 2], mask)).ToList())).ToList(),
            parts.Select(part => part.Material).ToList(), ["atr_a", "atr_b"]);

    private static PainterSkinCandidate Candidate(string part, string path, ModelMesh mesh, IReadOnlyList<uint> masks, params TexturePlanMaterial[] materials)
        => new(part, new PainterModelRef(path, path, true), mesh, new ModelTexturePlan(path, materials, []), masks);

    private static void SkinMaterialsAreTold()
    {
        var body = Material("/b.mtrl", "b", "skin.shpk", SkinMaterial.SkinTypeBody);
        var hrothgar = Material("/h.mtrl", "h", "skin.shpk", SkinMaterial.SkinTypeBodyHrothgar);
        var face = Material("/f.mtrl", "f", "skin.shpk", null);
        var emissive = Material("/e.mtrl", "e", "Skin.shpk", SkinMaterial.SkinTypeFaceEmissive);
        var gear = Material("/g.mtrl", "g", "character.shpk", SkinMaterial.SkinTypeBody);
        Require(PainterSkin.IsBodySkin(body) && PainterSkin.IsBodySkin(hrothgar) && !PainterSkin.IsFaceSkin(body), "skin.shpk's body types are body skin");
        Require(PainterSkin.IsFaceSkin(face) && PainterSkin.IsFaceSkin(emissive) && !PainterSkin.IsBodySkin(face),
            "skin.shpk without the skin type key, or with a face type, is face skin");
        Require(!PainterSkin.IsBodySkin(gear) && !PainterSkin.IsFaceSkin(gear), "other shaders are no skin, whatever their keys");
        var triangles = PainterSkin.DrawnTriangles(Parts(("/b.mtrl", [0, 1, 2]), ("/g.mtrl", [0])), [0b01]);
        Require(triangles["/B.MTRL"] == 2 && triangles["/g.mtrl"] == 1, "drawn triangles leave out parts whose attributes are off, by material name");
        Require(PainterRules.TextureSources([Material("/x.mtrl", "x", "skin.shpk", null, "a.tex", "b.tex"),
                new TexturePlanMaterial("/y.mtrl", "", "", "", [Planned("diffuse", "c.tex")], "")]).SequenceEqual(["a.tex", "b.tex"]),
            "texture sources are the files read, leaving out textures that weren't");
    }

    private static void SmallclothesFollowTheGamesRules()
    {
        Require(PainterSmallclothes.Fallback(801) == 201 && PainterSmallclothes.Fallback(1401) == 201 && PainterSmallclothes.Fallback(701) == 101 &&
                PainterSmallclothes.Fallback(201) == 101 && PainterSmallclothes.Fallback(1501) == 901 && PainterSmallclothes.Fallback(1201) == 1101 &&
                PainterSmallclothes.Fallback(1304) == 1301 && PainterSmallclothes.Fallback(1804) == 104,
            "races fall back like Penumbra's GenderRace.Fallback");
        // Miqo'te women have hands of their own, Midlander women's feet, and only the legs' material flag.
        ushort Entry(int race) => race switch
        {
            801 => (ushort)(1 << (PainterBodySlot.Hands.EqdpShift + 1) | 1 << PainterBodySlot.Legs.EqdpShift),
            201 => (ushort)(1 << (PainterBodySlot.Feet.EqdpShift + 1)),
            _ => (ushort)0,
        };
        Require(PainterSmallclothes.ModelRace(801, PainterBodySlot.Hands, Entry) == 801 && PainterSmallclothes.ModelRace(801, PainterBodySlot.Feet, Entry) == 201 &&
                PainterSmallclothes.ModelRace(801, PainterBodySlot.Legs, Entry) == 101 && PainterSmallclothes.ModelRace(101, PainterBodySlot.Body, _ => 0) == 101,
            "a slot takes the race's own smallclothes when its EQDP model flag is set, else its fallback's, else Midlander Male's");
        Require(PainterSmallclothes.ApplyEqdp(0b11_1111_1111, PainterBodySlot.Hands, 0) == 0b11_1100_1111 &&
                PainterSmallclothes.ApplyEqdp(0, PainterBodySlot.Hands, 0xFFFF) == 0b11_0000,
            "an EQDP manipulation replaces only its slot's two bits");

        // Three blocks of four entries: the first stored, the second collapsed, the third stored after the first.
        var eqdp = new byte[12 + 8 * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(eqdp.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(eqdp.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(eqdp.AsSpan(8), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(eqdp.AsSpan(10), 4);
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(eqdp.AsSpan(12 + i * 2), (ushort)(100 + i));
        Require(PainterSmallclothes.EqdpEntry(eqdp, 0) == 100 && PainterSmallclothes.EqdpEntry(eqdp, 3) == 103, "EQDP entries of a stored block");
        Require(PainterSmallclothes.EqdpEntry(eqdp, 5) == 0 && PainterSmallclothes.EqdpEntry(eqdp, 12) == 0,
            "a collapsed block's sets, and sets past the last block, have empty entries");
        Require(PainterSmallclothes.EqdpEntry(eqdp, 9) == 105, "EQDP block offsets count entries");

        // Parts 1, 2 and 4, variants 0 and 1.
        var imc = new byte[4 + 2 * 3 * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(imc, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(imc.AsSpan(2), 0b10110);
        BinaryPrimitives.WriteUInt16LittleEndian(imc.AsSpan(4 + 1 * 6 + 2), 0x7C00 | 0x2D3);
        BinaryPrimitives.WriteUInt16LittleEndian(imc.AsSpan(4 + 5 * 6 + 2), 0x155);
        Require(PainterSmallclothes.ImcAttributes(imc, 0, 2) == 0x2D3 && PainterSmallclothes.ImcAttributes(imc, 1, 4) == 0x155,
            "an IMC entry is found by its part's place among the file's parts; its attributes are the low ten bits");
        Reject(() => PainterSmallclothes.ImcAttributes(imc, 0, 3), "an IMC part the file lacks is refused");
        Require(PainterSmallclothes.EnabledAttributes(["atr_gv_a", "atr_gv_b", "atr_ude", "atrx_custom", "atr_tv_b", "atr_gv_k", "atr_gv_c"],
                    PainterBodySlot.Hands, 0b101) == ~(1u << 1),
            "only the slot's own variant attributes whose IMC bit is off are turned off");

        var shapes = PainterSmallclothes.ConnectorShapes([
            ["shpx_wr_a", "shpx_wa_b", "shp_brw", "shpx_an_x", "shpx_wr_"],
            ["shpx_wr_", "shpx_wr_a"],
            ["shpx_wa_b", "shpx_an_c", "shpx_wr_a"],
            ["shpx_an_c", "shpx_wa_b"],
        ]);
        Require(shapes.SequenceEqual([0b11u, 0b10u, 0b11u, 0b1u]),
            "connector shapes turn on where both neighbours have them: wrists on torso and hands, waist on torso and legs, ankles on legs and feet");

        // Two triangles; the first shape moves the second triangle's first corner, the second the first triangle's.
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(2, 0, 0) };
        var shaped = new ModelMesh([new ModelMeshPart(0, 0, positions, new Vector3[5], new Vector2[5],
            [new ModelSubmesh(0, [0, 1, 2], 0), new ModelSubmesh(1, [1, 3, 2], 0) { MeshIndexStart = 3 }])], ["/m.mtrl"], [])
        {
            Shapes = [new ModelShape("shpx_wr_a", [new ModelShapeMesh(0, [3], [4])]), new ModelShape("shpx_wr_b", [new ModelShapeMesh(0, [0], [3])])],
        };
        var applied = PainterSmallclothes.WithShapes(shaped, 0b01);
        Require(applied.Meshes[0].Submeshes[0].Indices.SequenceEqual([0, 1, 2]) && applied.Meshes[0].Submeshes[1].Indices.SequenceEqual([4, 3, 2]),
            "an enabled shape swaps the mesh indices it names for its own vertices");
        Require(PainterSmallclothes.WithShapes(shaped, 0b11).Meshes[0].Submeshes[0].Indices.SequenceEqual([3, 1, 2]) &&
                ReferenceEquals(PainterSmallclothes.WithShapes(shaped, 0), shaped),
            "every enabled shape applies; without any the mesh stays as it is");

        var folder = PainterSmallclothes.SkinFolder([
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl",
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
            "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl",
            "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl",
            "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl",
            "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_fac_a.mtrl",
        ]);
        Require(folder == new PainterSkinFolder("chara/human/c0201/obj/body/b0001/material/v0001", 201, 1),
            "the skin folder is the one most loaded body materials come from; names of another race don't count");
        Require(PainterSmallclothes.SkinMaterialPath("/mt_c0101b0001_bibo.mtrl", folder!) == "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl" &&
                PainterSmallclothes.SkinMaterialPath("mt_c0801b0001_A.mtrl", folder!) == "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl" &&
                PainterSmallclothes.SkinMaterialPath("/mt_c0201e0000_top_a.mtrl", folder!) is null,
            "body material names of any race load from the character's own skin folder; other names aren't looked up");
        Require(PainterSmallclothes.FolderOfName("/mt_c0801b0001_a.mtrl") == new PainterSkinFolder("chara/human/c0801/obj/body/b0001/material/v0001", 801, 1) &&
                PainterSmallclothes.FolderOfName("/mt_c0201e0000_top_a.mtrl") is null,
            "without loaded body skin, a body material name gives its folder");
        Require(PainterBodySlot.Hands.ModelPath(801) == "chara/equipment/e0000/model/c0801e0000_glv.mdl" &&
                PainterSmallclothes.EqdpPath(1401) == "chara/xls/charadb/equipmentdeformerparameter/c1401.eqdp",
            "smallclothes model and EQDP paths");
    }

    private static void SkinProjectsNeedOneBodyMaterial()
    {
        var skin = Material("/mt_c0201b0001_a.mtrl", BodySkinPath, "skin.shpk", SkinMaterial.SkinTypeBody, "body_base.tex", "body_norm.tex");
        // A model of another race names the body skin for its own race; it loads the same file.
        var raceSkin = skin with { ModelMaterial = "/mt_c0101b0001_a.mtrl" };
        var extra = Material("/mt_c0201b0001_b.mtrl", "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_b.mtrl", "skin.shpk",
            SkinMaterial.SkinTypeBody, "extra_base.tex");
        var underwear = Material("/mt_c0201e0000_top_a.mtrl", "chara/equipment/e0000/material/v0001/mt_c0201e0000_top_a.mtrl", "character.shpk", null, "top_norm.tex");
        var torso = Candidate("Torso", "chara/equipment/e0000/model/c0201e0000_top.mdl",
            Parts((skin.ModelMaterial, [0, 0, 0]), (extra.ModelMaterial, [0, 0, 0, 0]), (underwear.ModelMaterial, [0])), [], skin, extra, underwear);
        var hands = Candidate("Hands", "chara/equipment/e0000/model/c0201e0000_glv.mdl", Parts((skin.ModelMaterial, [0])), [], skin);
        // The legs' extra skin is turned off by an attribute.
        var legs = Candidate("Legs", "chara/equipment/e0000/model/c0101e0000_dwn.mdl",
            Parts((raceSkin.ModelMaterial, [0]), (extra.ModelMaterial, [0b10])), [0b01], raceSkin, extra);
        var feet = Candidate("Feet", "chara/equipment/e0000/model/c0201e0000_sho.mdl", Parts((skin.ModelMaterial, [0])), [], skin);
        var shared = PainterSkin.SharedBodyMaterial([torso, hands, legs, feet], out var problem);
        Require(shared == skin && problem.Length == 0, "the body skin every part draws is painted, even where one part draws more of another");

        PainterSkinCandidate Both(string part) => Candidate(part, $"{part}.mdl", Parts((skin.ModelMaterial, [0]), (extra.ModelMaterial, [0])), [], skin, extra);
        Require(PainterSkin.SharedBodyMaterial([torso, Both("Hands"), Both("Legs"), Both("Feet")], out _) == extra,
            "of several shared skins, the one drawing the most triangles is painted");

        var otherHands = Candidate("Hands", "chara/equipment/e0000/model/c0201e0000_glv.mdl", Parts((extra.ModelMaterial, [0])), [], extra);
        Require(PainterSkin.SharedBodyMaterial([torso, otherHands, legs, feet], out problem) is null &&
                problem.Contains("don't share one skin material") && problem.Contains("hands: mt_c0201b0001_b.mtrl"),
            "body parts without one shared skin are refused, naming what each draws");
        var coveredLegs = Candidate("Legs", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", Parts((skin.ModelMaterial, [0b10])), [0b01], skin);
        Require(PainterSkin.SharedBodyMaterial([torso, hands, coveredLegs, feet], out problem) is null &&
                problem.Contains("legs smallclothes model (c0201e0000_dwn.mdl) draws no body skin"),
            "a part drawing no body skin is refused");

        const string faceModel = "chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl";
        var faceA = Material("/mt_c0801f0001_fac_a.mtrl", FaceSkinPath, "skin.shpk", null, "face_base.tex");
        var faceB = Material("/mt_c0801f0001_fac_b.mtrl", "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_fac_b.mtrl", "skin.shpk", null, "teeth.tex");
        var neck = Material("/mt_c0801f0001_fac_e.mtrl", "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_fac_e.mtrl", "skin.shpk",
            SkinMaterial.SkinTypeBody, "neck.tex");
        var iris = Material("/mt_c0801f0001_iri_a.mtrl", "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_iri_a.mtrl", "iris.shpk", null, "eye.tex");
        var face = Candidate("Head", faceModel,
            Parts((neck.ModelMaterial, [0]), (faceB.ModelMaterial, [0]), (faceA.ModelMaterial, [0]), (iris.ModelMaterial, [0])), [], neck, faceB, faceA, iris);
        Require(PainterSkin.HeadMaterial(face) == faceA, "the head is the face's _fac_a skin, wherever it comes");
        var hiddenA = Candidate("Head", faceModel,
            Parts((neck.ModelMaterial, [0]), (faceB.ModelMaterial, [0]), (faceA.ModelMaterial, [0b10])), [0b01], neck, faceB, faceA);
        Require(PainterSkin.HeadMaterial(hiddenA) == faceB,
            "without a drawn _fac_a, the head is the first mesh's face skin; body skin on the face model isn't the head");
        Require(PainterSkin.HeadMaterial(Candidate("Head", faceModel, Parts((iris.ModelMaterial, [0])), [], iris)) is null, "a face without face skin has no head");

        var sets = PainterProjectBuilder.BuildSets([skin, faceA], _ => true, out var setByMaterial);
        Require(sets.Count == 2 && sets[0].AlwaysShared && !sets[1].AlwaysShared,
            "body skin sets keep the texels outside the project's UV islands, which models it doesn't know draw too; the face's don't");
        Require(setByMaterial[skin.ModelMaterial] == sets[0].Name && sets[0].Textures.All(t => t.Editable && t.Selected == (t.Texture.Usage != "index")),
            "the skin's own textures are ticked");
    }

    /// <summary> A model with one triangle per material, each in a mesh of its own whose one submesh has the given attribute mask. </summary>
    private static byte[] TriangleModel(string[] attributes, params (string Material, uint Mask)[] parts)
    {
        var vertices = new byte[3 * 44];
        for (var v = 0; v < 3; v++)
        {
            WriteF32(vertices, v * 44, v == 1 ? 1 : 0, v == 2 ? 1 : 0, 0, 1);
            WriteF32(vertices, v * 44 + 16, 0, 0, 1);
            WriteF32(vertices, v * 44 + 28, 0.25f * v, 0.5f, 0, 0);
        }
        var meshes = parts.Select((part, m) => new MeshSpec([new Element(0, 0, 3, 0), new Element(0, 16, 2, 3), new Element(0, 28, 3, 4)],
            [vertices], [44], 3, [0, 1, 2], (ushort)m, [(0, 3, part.Mask)])).ToArray();
        return BuildModel(meshes, attributes, parts.Select(part => part.Material).ToArray(), Enumerable.Range(0, parts.Length).ToArray());
    }

    /// <summary> Penumbra's version 0 metadata encoding: a version byte and the manipulations as JSON, gzipped. </summary>
    private static string Metadata(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
        {
            gzip.WriteByte(0);
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }
        return Convert.ToBase64String(output.ToArray());
    }

    private static void SkinLookupFollowsTheCollection(string root)
    {
        Directory.CreateDirectory(root);
        const string skinFolder = "chara/human/c0801/obj/body/b0001/material/v0001";
        const string extraTexture = "chara/human/c0801/obj/body/b0001/texture/extra_base.tex";
        var torsoFile = Path.Combine(root, "c0201e0000_top.mdl");
        File.WriteAllBytes(torsoFile, TriangleModel([], ("/mt_c0201b0001_a.mtrl", 0), ("/mt_c0201e0000_top_a.mtrl", 0)));
        var extraFile = Path.Combine(root, "mt_c0801b0001_b.mtrl");
        File.WriteAllBytes(extraFile, NeckSeamScenarios.Material([extraTexture], [(SkinMaterial.SkinTypeKey, SkinMaterial.SkinTypeBody)], []));
        var extraTextureFile = Path.Combine(root, "extra_base.tex");

        static byte[] Eqdp(int entry)
        {
            var file = new byte[10];
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), (ushort)entry);
            return file;
        }
        var imc = new byte[4 + 5 * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(imc.AsSpan(2), 0x1F);
        BinaryPrimitives.WriteUInt16LittleEndian(imc.AsSpan(4 + 2 * 6 + 2), 0b01);
        var game = new Dictionary<string, byte[]>
        {
            // Miqo'te women have hands of their own; Midlander women have a torso, legs and feet.
            [PainterSmallclothes.EqdpPath(801)] = Eqdp(1 << 5),
            [PainterSmallclothes.EqdpPath(201)] = Eqdp(1 << 3 | 1 << 7 | 1 << 9),
            [PainterSmallclothes.ImcPath] = imc,
            ["chara/equipment/e0000/model/c0801e0000_glv.mdl"] = TriangleModel(["atr_gv_a", "atr_gv_b"], ("/mt_c0801b0001_a.mtrl", 0b10)),
            ["chara/equipment/e0000/model/c0801e0000_dwn.mdl"] = TriangleModel([], ("/mt_c0201b0001_a.mtrl", 0), ("/mt_c0201b0001_b.mtrl", 0)),
            ["chara/equipment/e0000/model/c0101e0000_sho.mdl"] = TriangleModel(["atr_sv_a"], ("/mt_c0101b0001_a.mtrl", 0)),
        };
        var mods = new Dictionary<string, string>
        {
            ["chara/equipment/e0000/model/c0201e0000_top.mdl"] = torsoFile,
            [$"{skinFolder}/mt_c0801b0001_b.mtrl"] = extraFile,
            [extraTexture] = extraTextureFile,
        };
        // The collection gives Miqo'te women legs of their own, takes Midlander women's feet away and shows the hands' second part.
        var meta = Metadata("""
            [{"Type":"Eqdp","Manipulation":{"Entry":128,"Gender":"Female","Race":"Miqote","SetId":0,"Slot":"Legs"}},
             {"Type":"Eqdp","Manipulation":{"Entry":0,"Gender":"Female","Race":"Midlander","SetId":0,"Slot":"Feet"}},
             {"Type":"Imc","Manipulation":{"ObjectType":"Equipment","PrimaryId":0,"Variant":0,"EquipSlot":"Hands","Entry":{"AttributeMask":2}}}]
            """);
        var collection = Guid.NewGuid();
        var asked = new List<string>();
        var penumbra = new PainterSkinPenumbra(_ => Task.FromResult<Guid?>(collection), _ => Task.FromResult<string?>(meta), (id, paths) =>
        {
            asked.AddRange(paths);
            return Task.FromResult(paths.Select(path => id == collection ? mods.GetValueOrDefault(path) ?? path : null).ToArray());
        });
        var resolver = new PainterSkinResolver(penumbra, (path, _) => Task.FromResult(game.GetValueOrDefault(path)),
            (error, message) => throw new InvalidOperationException(message, error));

        var face = new PainterModelRef("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", "chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", true);
        var loaded = new MaterialResourceCandidate($"{skinFolder}/mt_c0801b0001_a.mtrl", Path.Combine(root, "mt_c0801b0001_a.mtrl"));
        var request = new PainterRequest(3, 0, "Test", face, [], [loaded])
        {
            Scope = PainterScope.Skin, Live = new PainterLiveCharacter([new PainterLiveModel(face.GamePath, 0b1, 0b10)], null) { Race = 801 },
        };
        var result = resolver.ResolveAsync(request, default).GetAwaiter().GetResult();
        var skin = result.Skin!;
        Require(skin.Body.Select(part => (part.Part, part.Model.GamePath)).SequenceEqual([
                ("Torso", "chara/equipment/e0000/model/c0201e0000_top.mdl"), ("Hands", "chara/equipment/e0000/model/c0801e0000_glv.mdl"),
                ("Legs", "chara/equipment/e0000/model/c0801e0000_dwn.mdl"), ("Feet", "chara/equipment/e0000/model/c0101e0000_sho.mdl")]),
            "each body slot takes the smallclothes of the race EQDP picks, with the collection's EQDP manipulations applied");
        Require(skin.Body[0].Model == new PainterModelRef("chara/equipment/e0000/model/c0201e0000_top.mdl", torsoFile, false) && skin.Body[1].Model.Vanilla,
            "smallclothes load from the collection's mods, else from game data");
        Require(skin.Body[1].Masks.SequenceEqual([~1u]) && skin.Body[3].Masks.SequenceEqual([~1u]) && skin.Body[0].Masks.SequenceEqual([uint.MaxValue]),
            "variant parts follow the collection's IMC manipulation, else the game's IMC file");
        Require(skin.Body[0].MaterialPaths.Count == 1 && skin.Body[0].MaterialPaths["/mt_c0201b0001_a.mtrl"] == $"{skinFolder}/mt_c0801b0001_a.mtrl" &&
                skin.Body[2].MaterialPaths["/mt_c0201b0001_b.mtrl"] == $"{skinFolder}/mt_c0801b0001_b.mtrl",
            "body material names load from the character's own skin folder; the underwear isn't looked up");
        Require(skin.Head is { Part: "Head" } head && head.Model == face && head.Masks.SequenceEqual([0b1u]) && head.Shapes == 0b10,
            "the head is the face the character draws, as it draws it");
        Require(result.Resources.Contains(loaded) && result.Resources.Any(r => r.GamePath == $"{skinFolder}/mt_c0801b0001_b.mtrl" && r.ActualPath == extraFile) &&
                result.Resources.Any(r => r.GamePath == extraTexture && r.ActualPath == extraTextureFile) &&
                !asked.Contains($"{skinFolder}/mt_c0801b0001_a.mtrl") && !result.Resources.Any(r => r.GamePath.Contains("e0000")),
            "skin materials the character doesn't load now come from the collection with their textures; loaded ones aren't asked again");

        // Wearing nothing, the character draws these smallclothes: their parts and shapes are the game's.
        var undressed = request with
        {
            Live = request.Live! with
            {
                Models = [.. request.Live!.Models, new PainterLiveModel(PainterVisibility.NormalizePath(torsoFile), 0b101, 0b1, 1),
                    new PainterLiveModel("chara/equipment/e0000/model/c0801e0000_glv.mdl", 0b10, 0, 2),
                    new PainterLiveModel("chara/equipment/e0000/model/c0801e0000_dwn.mdl", 0b1, 0b100, 3),
                    new PainterLiveModel("chara/equipment/e0000/model/c0101e0000_sho.mdl", 0, 0, 4)],
            },
        };
        var drawn = resolver.ResolveAsync(undressed, default).GetAwaiter().GetResult().Skin!;
        Require(drawn.AsDrawn && !skin.AsDrawn &&
                drawn.Body.Select(part => (part.Masks.Single(), part.Shapes)).SequenceEqual([(0b101u, 0b1u), (0b10u, 0u), (0b1u, 0b100u), (0u, 0u)]),
            "with nothing worn in any body slot, the smallclothes' parts and shapes are the ones the game draws");
        var dressed = request with { Live = request.Live! with { Models = [.. undressed.Live!.Models.Where(m => m.Slot != 3)] } };
        Require(resolver.ResolveAsync(dressed, default).GetAwaiter().GetResult().Skin! is { AsDrawn: false } rules && rules.Body[1].Masks.SequenceEqual([~1u]),
            "gear in any body slot changes what the others show, so then every part follows the rules");
    }

    private static void OptionalSetsStayOutUntilTicked()
    {
        var body = Material("/mt_c0201b0001_a.mtrl", BodySkinPath, "skin.shpk", SkinMaterial.SkinTypeBody, "body_base.tex");
        var face = Material("/mt_c0801f0001_fac_a.mtrl", FaceSkinPath, "skin.shpk", null, "face_base.tex", "face_norm.tex");
        var sets = PainterProjectBuilder.BuildSets([body, face], _ => true, out _);
        var main = new PainterDraftModel
        {
            Model = new PainterModelRef("chara/equipment/e0000/model/c0201e0000_top.mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", true),
            Mesh = Parts((body.ModelMaterial, [0])), Selected = true, Part = "Torso",
            SetByMaterial = new Dictionary<string, string> { [body.ModelMaterial] = sets[0].Name },
        };
        var faceModel = new PainterDraftModel
        {
            Model = new PainterModelRef("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", "chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", true),
            Mesh = Parts((face.ModelMaterial, [0])), Part = "Head",
            SetByMaterial = new Dictionary<string, string> { [face.ModelMaterial] = sets[1].Name },
        };
        var draft = new PainterDraft
        {
            Request = new PainterRequest(0, 0, "Test", main.Model, [faceModel.Model], []) { Scope = PainterScope.Skin },
            Main = main, Siblings = [faceModel], Sets = sets, Warnings = [], Title = "Test's skin",
        };
        Require(draft.InProject(sets[0]) && !draft.InProject(sets[1]), "a set only an unticked model draws stays out of the project");
        Require(draft.SelectedTextures.Count() == 1 && sets[0].Textures.Contains(draft.SelectedTextures.Single()) && draft.NeedsModName,
            "its ticked textures aren't sent; the body's vanilla texture needs a new mod");
        faceModel.Selected = true;
        Require(draft.InProject(sets[1]) && draft.SelectedTextures.Count() == 3, "ticking the model brings its set and its ticked textures in");
        Require(sets[1].Textures.All(t => t.Selected), "the face's textures were ticked all along, so they come back as they were");
    }
}
