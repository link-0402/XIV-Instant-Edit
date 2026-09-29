using System.Buffers.Binary;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services.Heels;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The heels offset card's pure parts: Simple Heels' model offset attributes, the measurement of a feet
/// model (parts hidden by attributes, shapes turned on, racial scaling), finding the drawn feet model in
/// an On Screen tree, and how Simple Heels names models and reports its offset.
/// </summary>
internal static class HeelsOffsetScenarios
{
    public static void Run()
    {
        CheckModelOffsets();
        CheckShapesAreRead();
        CheckMeasurement();
        CheckRacialScaling();
        CheckFeetNode();
        CheckSimpleHeels();
    }

    private static void CheckModelOffsets()
    {
        Require(HeelsModelOffset.Find(["atr_sv_a", "heels_offset=0.0701"]) is { Value: 0.0701f, Attribute: "heels_offset=0.0701" } &&
                HeelsModelOffset.Find(["heels_offset=-0,03"])?.Value == -0.03f &&
                HeelsModelOffset.Find(["HEELS_OFFSET_a_abf"])?.Value == 0.015f,
            "heels: offsets written as a number are read, with a comma or a capitalised prefix too");
        Require(HeelsModelOffset.Find(["heels_offset_n_a_ac"])?.Value == -0.02f && HeelsModelOffset.Find(["heels_offset_b_abf"])?.Value == 1.015f &&
                HeelsModelOffset.Find(["heels_offset_a_bcdefghij"])?.Value == 0.123456789f,
            "heels: letter offsets are read as Simple Heels' mod guide spells them");
        Require(HeelsModelOffset.Find(["heels_offset=high", "heels_offset_a_ae"]) is { Value: 0.04f, Attribute: "heels_offset_a_ae" } &&
                HeelsModelOffset.Find(["atr_heels", "heels_offset"]) is null,
            "heels: the first offset Simple Heels can read wins, and other attributes are skipped");
        Require(HeelsModelOffset.AttributeFor(0.015f) == "heels_offset_a_abf" && HeelsModelOffset.AttributeFor(-0.02f) == "heels_offset_n_a_ac" &&
                HeelsModelOffset.AttributeFor(0.05234f) == "heels_offset_a_afcd" && HeelsModelOffset.AttributeFor(0.00001f) == "heels_offset_a_a",
            "heels: attributes are written in the TexTools-safe form, to a tenth of a millimetre");
        Require(new[] { 0.0523f, -0.0701f, 0.12f, 1.2345f }.All(value => HeelsModelOffset.Find([HeelsModelOffset.AttributeFor(value)])?.Value == value),
            "heels: written attributes read back as the same offset");
        Require(HeelsModelOffset.Format(0.05234f) == "0.0523" && HeelsModelOffset.Format(-0.00001f) == "0.0000" && HeelsModelOffset.Format(-0.1f) == "-0.1000",
            "heels: offsets are shown with four decimals and no minus sign on zero");
    }

    // ---- Synthetic feet model ---------------------------------------------------------------------

    private const float Sole = 0f, Heel = -0.05f, Raised = -0.09f;

    /// <summary> A mesh's vertex heights (vertex i sits at x = i cm), its indices, and its submeshes counted from its first index. </summary>
    private sealed record FeetMesh(float[] Heights, ushort[] Indices, (int Start, int Count, uint Mask)[] Submeshes);

    /// <summary>
    /// Mesh 0 is the foot: two submeshes without attributes on the sole, and a fourth vertex stored after
    /// the three it draws, which the shape "shpx_heel" swaps in for the second submesh's middle index.
    /// Mesh 1 is the heel, drawn only while atr_sv_a is on. With <paramref name="gateFoot"/> the foot
    /// needs atr_sv_b, so turning both off leaves nothing drawn.
    /// </summary>
    private static byte[] FeetModel(bool gateFoot = false) => BuildModel(
        [
            new FeetMesh([Sole, 0.01f, 0.02f, Raised], [0, 1, 2, 1, 2, 0], [(0, 3, gateFoot ? 0b100u : 0), (3, 3, gateFoot ? 0b100u : 0)]),
            new FeetMesh([Heel, -0.04f, 0.03f], [0, 1, 2], [(0, 3, 0b001)]),
        ],
        ["atr_sv_a", "heels_offset_a_afa", "atr_sv_b"],
        [("shpx_heel", 0, [(4, 3)])]);

    private static byte[] BuildModel(FeetMesh[] meshes, string[] attributes, (string Name, int Mesh, (ushort Index, ushort Vertex)[] Values)[] shapes)
    {
        const string material = "/mt_c0201e6001_sho_a.mtrl";
        var strings = new List<byte>();
        var offsets = new Dictionary<string, int>();
        foreach (var text in attributes.Append(material).Concat(shapes.Select(shape => shape.Name)))
        {
            offsets[text] = strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(text));
            strings.Add(0);
        }
        while (strings.Count % 4 != 0)
            strings.Add(0);

        var vertexData = new MemoryStream();
        var vertexWriter = new BinaryWriter(vertexData);
        var streamOffsets = new List<int>();
        foreach (var mesh in meshes)
        {
            streamOffsets.Add((int)vertexData.Length);
            for (var v = 0; v < mesh.Heights.Length; v++)
            {
                vertexWriter.Write(v * 0.01f);
                vertexWriter.Write(mesh.Heights[v]);
                vertexWriter.Write(0f);
            }
        }
        var indexData = new MemoryStream();
        var indexWriter = new BinaryWriter(indexData);
        var starts = new List<int>();
        foreach (var mesh in meshes)
        {
            starts.Add((int)indexData.Length / 2);
            foreach (var index in mesh.Indices)
                indexWriter.Write(index);
        }
        var submeshes = meshes.SelectMany((mesh, m) => mesh.Submeshes.Select(sub => (Start: starts[m] + sub.Start, sub.Count, sub.Mask))).ToList();

        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        foreach (var _ in meshes)
        {
            // Float3 positions in stream 0, then the end marker.
            w.Write([0, 0, 2, 0, 0, 0, 0, 0]);
            w.Write((byte)0xFF);
            w.Write(new byte[17 * 8 - 9]);
        }
        w.Write((ushort)offsets.Count);
        w.Write((ushort)0);
        w.Write(strings.Count);
        w.Write(strings.ToArray());
        var meshHeader = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(4), (ushort)meshes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(6), (ushort)attributes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(8), (ushort)submeshes.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(16), (ushort)shapes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(18), (ushort)shapes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(meshHeader.AsSpan(20), (ushort)shapes.Sum(shape => shape.Values.Length));
        meshHeader[22] = 1;
        w.Write(meshHeader);
        var lods = new byte[3 * 60];
        BinaryPrimitives.WriteUInt16LittleEndian(lods.AsSpan(2), (ushort)meshes.Length);
        w.Write(lods);
        var submeshIndex = 0;
        for (var m = 0; m < meshes.Length; m++)
        {
            w.Write((ushort)meshes[m].Heights.Length);
            w.Write((ushort)0);
            w.Write((uint)meshes[m].Indices.Length);
            w.Write((ushort)0);
            w.Write((ushort)submeshIndex);
            w.Write((ushort)meshes[m].Submeshes.Length);
            w.Write((ushort)0);
            w.Write((uint)starts[m]);
            w.Write((uint)streamOffsets[m]);
            w.Write(0u);
            w.Write(0u);
            w.Write([12, 0, 0, 1]);
            submeshIndex += meshes[m].Submeshes.Length;
        }
        foreach (var attribute in attributes)
            w.Write((uint)offsets[attribute]);
        foreach (var (start, count, mask) in submeshes)
        {
            w.Write((uint)start);
            w.Write((uint)count);
            w.Write(mask);
            w.Write(0u);
        }
        w.Write((uint)offsets[material]);
        // No bones: the shape table follows the material table. Each shape has one LOD-0 shape mesh.
        for (var s = 0; s < shapes.Length; s++)
        {
            w.Write((uint)offsets[shapes[s].Name]);
            w.Write((ushort)s);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)0);
            w.Write((ushort)0);
        }
        var valueOffset = 0;
        foreach (var shape in shapes)
        {
            w.Write((uint)starts[shape.Mesh]);
            w.Write((uint)shape.Values.Length);
            w.Write((uint)valueOffset);
            valueOffset += shape.Values.Length;
        }
        foreach (var (index, vertex) in shapes.SelectMany(shape => shape.Values))
        {
            w.Write(index);
            w.Write(vertex);
        }

        var header = new byte[68];
        var vertexOffset = 68 + (int)body.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x01000006);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)meshes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)vertexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(vertexOffset + vertexData.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)vertexData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)indexData.Length);
        header[64] = 1;
        return [.. header, .. body.ToArray(), .. vertexData.ToArray(), .. indexData.ToArray()];
    }

    private static void CheckShapesAreRead()
    {
        var model = FeetModel();
        Require(ModelMeshReader.Read(model).Shapes.Count == 0, "heels: models are read without shapes unless asked");
        var mesh = ModelMeshReader.Read(model, shapes: true);
        Require(mesh.Shapes is [{ Name: "shpx_heel", Meshes: [{ MeshIndex: 0, Indices: [4], Vertices: [3] }] }],
            "heels: a shape is read with its name, mesh and index replacements");
        Require(mesh.Meshes[0].Submeshes.Select(submesh => submesh.MeshIndexStart).SequenceEqual([0, 3]) &&
                mesh.Meshes[1].Submeshes[0].MeshIndexStart == 0 && mesh.Meshes[0].Positions.Length == 4,
            "heels: submeshes know where their indices start in their mesh, and shape vertices are read with the mesh's own");
    }

    private static void CheckMeasurement()
    {
        var model = FeetModel();
        var all = HeelsMeasure.Measure(model, null, null, 0);
        Require(all.Lowest == Heel && all.Offset == -Heel && all.DrawnParts == 3 && all.HiddenParts == 0,
            "heels: with every part drawn, the heel's lowest point sets the offset");
        Require(all.ModelOffset is { Attribute: "heels_offset_a_afa", Value: 0.05f }, "heels: the offset the model stores for Simple Heels is found");
        var noHeel = HeelsMeasure.Measure(model, null, 0b010, 0);
        Require(noHeel.Lowest == Sole && noHeel.HiddenParts == 1 && noHeel.DrawnParts == 2,
            "heels: parts whose attributes are off are left out");
        Require(HeelsMeasure.Measure(model, null, 0b001, 0b1).Lowest == Raised && HeelsMeasure.Measure(model, null, 0b000, 0b1).Lowest == Raised,
            "heels: a shape that is on swaps its vertex in, in the submesh that holds the index");
        Require(HeelsMeasure.Measure(model, null, null, 0b10).Lowest == Heel,
            "heels: bits for shapes the model doesn't have change nothing");
        Reject(() => HeelsMeasure.Measure(FeetModel(gateFoot: true), null, 0b010, 0), "heels: a model with no part drawn is refused");
        var version5 = FeetModel();
        version5[0] = 5;
        try
        {
            HeelsMeasure.Measure(version5, null, null, 0);
            Require(false, "heels: a pre-Dawntrail model is refused");
        }
        catch (NotSupportedException)
        {
            Require(true, "heels: a pre-Dawntrail model is refused");
        }
    }

    private static void CheckRacialScaling()
    {
        var model = RacialScalingScenarios.Model();
        var deformer = RacialScalingScenarios.Deformer();
        var expected = RacialScalingScenarios.Vertices.Min(vertex => RacialScalingScenarios.Expected(deformer, vertex, 8).Y);
        var measured = HeelsMeasure.Measure(model, deformer, null, 0);
        Require(MathF.Abs(measured.Lowest - expected) < 1e-5f && MathF.Abs(HeelsMeasure.Measure(model, null, null, 0).Lowest - 1.1f) < 1e-6f,
            "heels: another race's model is measured as the racial deformer reshapes it");
    }

    private static ResourceNode Node(string gamePath, string actualPath, IReadOnlyList<ResourceNode>? children = null) => new()
    {
        Type = "Mdl", Icon = "", Name = Path.GetFileName(gamePath), GamePath = gamePath, ActualPath = actualPath, Children = children ?? [],
        SourceState = ResourceSourceState.LoadedMod, SourceLabel = "Heels", SourceModName = "Heels", SlotLabel = "Feet",
        ResourceSection = ResourceSection.Gear, SortOrder = 4,
    };

    private static void CheckFeetNode()
    {
        const string modFile = @"G:\Penumbra\Heels\chara\equipment\e6012\model\c0201e6012_sho.mdl";
        var drawn = InstantEdit.Services.Painter.PainterVisibility.NormalizePath("|abc_def|" + modFile);
        var withoutPath = Node("", modFile);
        var feet = Node("chara/equipment/e6012/model/c0201e6012_sho.mdl", modFile);
        var legs = Node("chara/equipment/e6012/model/c0201e6012_dwn.mdl", @"G:\Penumbra\Heels\legs.mdl", [Node("chara/equipment/e6012/texture/x.tex", @"G:\Penumbra\Heels\x.tex")]);
        Require(HeelsMeasure.FeetNode([legs, withoutPath, feet], drawn) == feet && HeelsMeasure.FeetNode([Node("", modFile)], drawn)?.GamePath == "",
            "heels: the drawn feet model is found by its file, preferring the node with the feet slot's game path");
        Require(HeelsMeasure.FeetNode([legs, feet], "chara/equipment/e0100/model/c0201e0100_sho.mdl") is null,
            "heels: a list that predates the current shoes has no match");
        Require(HeelsMeasure.RacePath(feet) == feet.GamePath && HeelsMeasure.RacePath(withoutPath) == "c0201e6012_sho.mdl",
            "heels: the model's race comes from its game path, else from its file name");
        var vanilla = Node("chara/equipment/e0000/model/c0201e0000_sho.mdl", "chara/equipment/e0000/model/c0201e0000_sho.mdl");
        Require(HeelsMeasure.FeetNode([Node("chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl", "x.mdl", [vanilla])],
                    "chara/equipment/e0000/model/c0201e0000_sho.mdl") == vanilla,
            "heels: game-data feet models are found below other nodes too");
    }

    private static void CheckSimpleHeels()
    {
        Require(SimpleHeels.FeetName(0, []) == "Smallclothes (Barefoot)" && SimpleHeels.FeetName(6012, []) == "Unknown#6012" &&
                SimpleHeels.FeetName(6012, ["Heels"]) == "Heels" && SimpleHeels.FeetName(6012, ["A", "B"]) == "A and B" &&
                SimpleHeels.FeetName(6012, ["A", "B", "C"]) == "A, B, C" && SimpleHeels.FeetName(6012, ["A", "B", "C", "D"]) == "A & 3 others.",
            "heels: models are named as Simple Heels' Equipment Offsets list names them");
        Require(SimpleHeels.CurrentOffset("{\"DefaultOffset\":0.0523,\"EmoteConfigs\":[]}") == 0.0523f &&
                SimpleHeels.CurrentOffset("{\"PluginVersion\":\"0.11.1.12\"}") == 0f,
            "heels: Simple Heels' offset is read from its message, and a message without one means none");
        Require(SimpleHeels.CurrentOffset("") is null && SimpleHeels.CurrentOffset("not json") is null && SimpleHeels.CurrentOffset("[1]") is null,
            "heels: an empty or unreadable message means Simple Heels didn't answer");
    }
}
