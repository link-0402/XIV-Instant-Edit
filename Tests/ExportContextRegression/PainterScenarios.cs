using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
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
                    Protect = true, Coverage = [new PainterCoverageModel("chara/a.mdl", true, [0, 2])],
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
}
