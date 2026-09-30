using System.Buffers.Binary;
using System.Text;
using InstantEdit.Services.Heels;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The heels offset card's pure parts: Simple Heels' model offset attributes, the measurement of a model
/// holding the feet (parts hidden by attributes, shapes turned on, racial scaling), what Fix offset does
/// with a measurement, writing the offset into a model, finding the drawn model among Penumbra's resolved
/// paths, and how Simple Heels names models and reports its offset.
/// </summary>
internal static class HeelsOffsetScenarios
{
    public static void Run()
    {
        CheckModelOffsets();
        CheckShapesAreRead();
        CheckMeasurement();
        CheckRacialScaling();
        CheckPlan();
        CheckWriteNew();
        CheckWriteReplace();
        CheckWriteDuplicates();
        CheckWriteLimits();
        CheckWriteSkinnedModel();
        CheckResolve();
        CheckSlots();
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
        Require(HeelsModelOffset.IsOffset("heels_offset=0.1") && HeelsModelOffset.IsOffset("HEELS_OFFSET_a_b") && HeelsModelOffset.IsOffset("heels_offset=high") &&
                !HeelsModelOffset.IsOffset("atr_heels") && !HeelsModelOffset.IsOffset("heel_offset=0.1"),
            "heels: offset attributes are recognised in any spelling, readable or not");
        Require(HeelsModelOffset.AttributeFor(0.119f) == "heels_offset=0.119" && HeelsModelOffset.AttributeFor(0.1f) == "heels_offset=0.10" &&
                HeelsModelOffset.AttributeFor(-0.02f) == "heels_offset=-0.02" && HeelsModelOffset.AttributeFor(0.05234f) == "heels_offset=0.0523" &&
                HeelsModelOffset.AttributeFor(-0.00001f) == "heels_offset=0.00",
            "heels: attributes are written as heels_offset=<number> with 2 to 4 decimals, as the Blender add-on writes them");
        Require(new[] { 0.0523f, -0.0701f, 0.12f, 1.2345f }.All(value => HeelsModelOffset.Find([HeelsModelOffset.AttributeFor(value)])?.Value == value),
            "heels: written attributes read back as the same offset");
        Require(HeelsModelOffset.Format(0.05234f) == "0.0523" && HeelsModelOffset.Format(-0.00001f) == "0.0000" && HeelsModelOffset.Format(-0.1f) == "-0.1000",
            "heels: offsets are shown with four decimals and no minus sign on zero");
    }

    // ---- Synthetic feet model ---------------------------------------------------------------------

    private const float Sole = 0f, Heel = -0.05f, Raised = -0.09f;
    private const string Material = "/mt_c0201e6001_sho_a.mtrl";

    /// <summary> A mesh's vertex heights (vertex i sits at x = i cm), its indices, and its submeshes counted from its first index. </summary>
    private sealed record FeetMesh(float[] Heights, ushort[] Indices, (int Start, int Count, uint Mask)[] Submeshes);

    /// <summary>
    /// Mesh 0 is the foot: two submeshes without attributes on the sole, and a fourth vertex stored after
    /// the three it draws, which the shape "shpx_heel" swaps in for the second submesh's middle index.
    /// Mesh 1 is the heel, drawn only while atr_sv_a is on. With <paramref name="gateFoot"/> the foot
    /// needs atr_sv_b, so turning both off leaves nothing drawn.
    /// </summary>
    private static byte[] FeetModel(bool gateFoot = false, string[]? attributes = null) => BuildModel(
        [
            new FeetMesh([Sole, 0.01f, 0.02f, Raised], [0, 1, 2, 1, 2, 0], [(0, 3, gateFoot ? 0b100u : 0), (3, 3, gateFoot ? 0b100u : 0)]),
            new FeetMesh([Heel, -0.04f, 0.03f], [0, 1, 2], [(0, 3, 0b001)]),
        ],
        attributes ?? ["atr_sv_a", "heels_offset_a_afa", "atr_sv_b"],
        [("shpx_heel", 0, [(4, 3)])]);

    private static byte[] BuildModel(FeetMesh[] meshes, string[] attributes, (string Name, int Mesh, (ushort Index, ushort Vertex)[] Values)[] shapes)
    {
        var strings = new List<byte>();
        var offsets = new Dictionary<string, int>();
        foreach (var text in attributes.Append(Material).Concat(shapes.Select(shape => shape.Name)))
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
        w.Write((uint)offsets[Material]);
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
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(meshes.Length * 17 * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(body.Length - meshes.Length * 17 * 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)meshes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)vertexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(vertexOffset + vertexData.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)vertexData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)indexData.Length);
        header[64] = 1;
        return [.. header, .. body.ToArray(), .. vertexData.ToArray(), .. indexData.ToArray()];
    }

    /// <summary> The model's string table in its own order. </summary>
    private static List<string> Strings(byte[] model)
    {
        var header = 68 + BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(12)) * 17 * 8;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(model.AsSpan(header));
        var at = header + 8;
        var strings = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var end = Array.IndexOf(model, (byte)0, at);
            strings.Add(Encoding.UTF8.GetString(model, at, end - at));
            at = end + 1;
        }
        return strings;
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
        Require(all.ModelOffset is { Attribute: "heels_offset_a_afa", Value: 0.05f } && all.OffsetAttributes == 1,
            "heels: the offset the model stores for Simple Heels is found and counted");
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
        Refused<NotSupportedException>(() => HeelsMeasure.Measure(version5, null, null, 0), "heels: a pre-Dawntrail model is refused");
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

    // ---- Fix offset -------------------------------------------------------------------------------

    private static void CheckPlan()
    {
        HeelsMeasurement Measured(float lowest, string[] attributes) => new(lowest, 3, 0, HeelsModelOffset.Find(attributes),
            attributes.Count(HeelsModelOffset.IsOffset));
        Require(HeelsFix.Plan(Measured(-0.0833f, []), HeelsSlot.Feet) is { Action: HeelsFixAction.Write, Attribute: "heels_offset=0.0833" } &&
                HeelsFix.Plan(Measured(-0.0833f, ["heels_offset_a_bd"]), HeelsSlot.Feet) is { Action: HeelsFixAction.Write, Attribute: "heels_offset=0.0833" },
            "heels: a model without an offset, or with a wrong one, gets the measured offset");
        Require(HeelsFix.Plan(Measured(-0.1192f, ["heels_offset_a_bbj"]), HeelsSlot.Feet).Action == HeelsFixAction.None &&
                HeelsFix.Plan(Measured(-0.0004f, ["atr_sv_a"]), HeelsSlot.Feet) is { Action: HeelsFixAction.None, Reason: var flat } && flat.Contains("stands on the ground"),
            "heels: an offset within a millimetre is right, and a model on the ground without one needs none");
        Require(HeelsFix.Plan(Measured(-0.1192f, ["heels_offset=0.1192", "heels_offset=0.14"]), HeelsSlot.Feet).Action == HeelsFixAction.Write &&
                HeelsFix.Plan(Measured(-0.05f, ["heels_offset=high"]), HeelsSlot.Feet).Action == HeelsFixAction.Write,
            "heels: two offsets, or one Simple Heels can't read, are always rewritten as one");
        Require(HeelsFix.Plan(Measured(0f, ["heels_offset=0.05"]), HeelsSlot.Feet) is { Action: HeelsFixAction.Write, Attribute: "heels_offset=0.00" } &&
                HeelsFix.Plan(Measured(0.0183f, []), HeelsSlot.Feet) is { Action: HeelsFixAction.Write, Attribute: "heels_offset=-0.0183" },
            "heels: a wrong offset on flat shoes becomes zero, and floating bare feet are lowered onto the ground");
        Require(HeelsFix.Plan(Measured(0.143f, []), HeelsSlot.Legs) is { Action: HeelsFixAction.Refuse, Reason: var high } &&
                high.Contains("legs model") && high.Contains("14.3 cm above") &&
                HeelsFix.Plan(Measured(-0.35f, []), HeelsSlot.Top) is { Action: HeelsFixAction.Refuse, Reason: var deep } && deep.Contains("35.0 cm below"),
            "heels: a model that doesn't reach the ground, or reaches deeper than any heel, gets no offset");
    }

    private static void CheckWriteNew()
    {
        var model = FeetModel(attributes: ["atr_sv_a", "atr_sv_b"]);
        var written = HeelsModelTag.Apply(model, "heels_offset=0.05");
        var before = ModelMeshReader.Read(model, shapes: true);
        var after = ModelMeshReader.Read(written, shapes: true);
        Require(after.Attributes.SequenceEqual(["atr_sv_a", "atr_sv_b", "heels_offset=0.05"]) && after.Materials.SequenceEqual([Material]) &&
                after.Shapes is [{ Name: "shpx_heel", Meshes: [{ Indices: [4], Vertices: [3] }] }],
            "heels: a new offset is added after the model's attributes, and its material and shape keep their names");
        Require(Strings(written).SequenceEqual(["atr_sv_a", "atr_sv_b", "heels_offset=0.05", Material, "shpx_heel"]),
            "heels: the new attribute's name goes after the other attributes' names in the string table");
        Require(after.Meshes[0].Submeshes[0].AttributeMask == 0b100 && after.Meshes[0].Submeshes[1].AttributeMask == 0 &&
                after.Meshes[1].Submeshes[0].AttributeMask == 0b001,
            "heels: the new offset goes on the first part only, and the other parts keep their attributes");
        Require(after.Meshes.Zip(before.Meshes).All(pair => pair.First.Positions.SequenceEqual(pair.Second.Positions)) &&
                HeelsMeasure.Measure(written, null, null, 0b1).Lowest == HeelsMeasure.Measure(model, null, null, 0b1).Lowest,
            "heels: the geometry stays as it was");
        Require((written.Length - model.Length) % 8 == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(16)) - BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(16)) == written.Length - model.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(8)) - BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(8)) == written.Length - model.Length,
            "heels: the geometry moves by a multiple of 8 bytes, and the header's offsets and size move with it");
        Require(HeelsModelTag.Apply(written, "heels_offset=0.05").AsSpan().SequenceEqual(written),
            "heels: writing the same offset again changes nothing");
    }

    private static void CheckWriteReplace()
    {
        var model = FeetModel();
        var written = HeelsModelTag.Apply(model, "heels_offset=0.0833");
        var after = ModelMeshReader.Read(written);
        Require(after.Attributes.SequenceEqual(["atr_sv_a", "heels_offset=0.0833", "atr_sv_b"]) &&
                Strings(written).SequenceEqual(["atr_sv_a", "heels_offset=0.0833", "atr_sv_b", Material, "shpx_heel"]),
            "heels: an existing offset is renamed in its place");
        Require(after.Meshes.SelectMany(mesh => mesh.Submeshes).Select(submesh => submesh.AttributeMask)
                    .SequenceEqual(ModelMeshReader.Read(model).Meshes.SelectMany(mesh => mesh.Submeshes).Select(submesh => submesh.AttributeMask)),
            "heels: renaming an offset leaves every part's attributes alone");
        Require(HeelsMeasure.Measure(written, null, null, 0) is { ModelOffset: { Value: 0.0833f }, OffsetAttributes: 1, Lowest: Heel },
            "heels: the model then sets the new offset and measures as before");
    }

    private static void CheckWriteDuplicates()
    {
        var model = BuildModel(
            [
                new FeetMesh([Sole, 0.01f, 0.02f], [0, 1, 2, 1, 2, 0], [(0, 3, 0), (3, 3, 0b100)]),
                new FeetMesh([Heel, -0.04f, 0.03f], [0, 1, 2], [(0, 3, 0b010)]),
            ],
            ["heels_offset_a_af", "atr_sv_a", "heels_offset=0.0488"], []);
        var written = HeelsModelTag.Apply(model, "heels_offset=0.05");
        var after = ModelMeshReader.Read(written);
        Require(after.Attributes.SequenceEqual(["heels_offset=0.05", "atr_sv_a"]) && Strings(written).SequenceEqual(["heels_offset=0.05", "atr_sv_a", Material]),
            "heels: a second offset attribute is removed, with its name");
        Require(after.Meshes.SelectMany(mesh => mesh.Submeshes).Select(submesh => submesh.AttributeMask).SequenceEqual([0u, 0b01u, 0b10u]),
            "heels: parts are renumbered past the removed offset, and its parts get the one that stays");
        Require(written.Length < model.Length && (model.Length - written.Length) % 8 == 0 &&
                HeelsMeasure.Measure(written, null, null, 0).Lowest == HeelsMeasure.Measure(model, null, null, 0).Lowest,
            "heels: the model shrinks by a multiple of 8 bytes and measures as before");
    }

    private static void CheckWriteLimits()
    {
        var full = Enumerable.Range(0, 32).Select(i => $"atr_x{i:D2}").ToArray();
        Refused<InvalidOperationException>(() => HeelsModelTag.Apply(FeetModel(attributes: full), "heels_offset=0.05"),
            "heels: a model with 32 attributes has no room for a new offset");
        var fullWithOffset = full[..31].Append("heels_offset=0.02").ToArray();
        Require(ModelMeshReader.Read(HeelsModelTag.Apply(FeetModel(attributes: fullWithOffset), "heels_offset=0.05")).Attributes[31] == "heels_offset=0.05",
            "heels: a model with 32 attributes can still have its offset replaced");
        Reject(() => HeelsModelTag.Apply(FeetModel(), "atr_sv_c"), "heels: only offset attributes are written");
        var version5 = FeetModel();
        version5[0] = 5;
        Refused<NotSupportedException>(() => HeelsModelTag.Apply(version5, "heels_offset=0.05"), "heels: a pre-Dawntrail model isn't written");
    }

    private static void CheckWriteSkinnedModel()
    {
        // Two LODs, bones and bounding boxes: the bone names must still point at the right strings, or the racial deformer would miss them.
        var model = RacialScalingScenarios.Model();
        var deformer = RacialScalingScenarios.Deformer();
        var written = HeelsModelTag.Apply(model, "heels_offset=-0.012");
        Require(HeelsMeasure.Measure(written, deformer, null, 0).Lowest == HeelsMeasure.Measure(model, deformer, null, 0).Lowest &&
                HeelsMeasure.Measure(written, null, null, 0).ModelOffset?.Attribute == "heels_offset=-0.012",
            "heels: a skinned model keeps its bone names and is reshaped as before");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(20)) - BinaryPrimitives.ReadUInt32LittleEndian(model.AsSpan(20)) == written.Length - model.Length,
            "heels: the second LOD's geometry offset moves too");
    }

    private static void CheckResolve()
    {
        const string modFile = @"G:\Penumbra\Heels\chara\equipment\e6012\model\c0201e6012_sho.mdl";
        const string legsFile = @"G:\Penumbra\Heels\legs.mdl";
        var resolved = new Dictionary<string, HashSet<string>>
        {
            [legsFile] = ["chara/equipment/e6012/model/c0201e6012_dwn.mdl"],
            [modFile] = ["chara/equipment/e6012/model/c0201e6012_sho.mdl", "chara/equipment/e6012/model/c0201e6012_top.mdl"],
            ["chara/equipment/e0000/model/c0201e0000_sho.mdl"] = ["chara/equipment/e0000/model/c0201e0000_sho.mdl"],
            [@"G:\Penumbra\Heels\x.tex"] = ["chara/equipment/e6012/texture/x.tex"],
        };
        Require(HeelsMeasure.Resolve(resolved, PainterVisibility.NormalizePath("|abc_def|" + modFile), HeelsSlot.Feet) ==
                (modFile, "chara/equipment/e6012/model/c0201e6012_sho.mdl"),
            "heels: the drawn model is found by its file, with the feet slot's game path");
        Require(HeelsMeasure.Resolve(resolved, PainterVisibility.NormalizePath(modFile), HeelsSlot.Top)?.GamePath == "chara/equipment/e6012/model/c0201e6012_top.mdl" &&
                HeelsMeasure.Resolve(resolved, PainterVisibility.NormalizePath(legsFile), HeelsSlot.Legs)?.GamePath == "chara/equipment/e6012/model/c0201e6012_dwn.mdl",
            "heels: the legs and body slots take their own game paths");
        Require(HeelsMeasure.Resolve(resolved, "chara/equipment/e0000/model/c0201e0000_sho.mdl", HeelsSlot.Feet)?.ActualPath ==
                "chara/equipment/e0000/model/c0201e0000_sho.mdl",
            "heels: a game file is found by its game path");
        Require(HeelsMeasure.Resolve(resolved, PainterVisibility.NormalizePath(legsFile), HeelsSlot.Feet)?.GamePath == "chara/equipment/e6012/model/c0201e6012_dwn.mdl",
            "heels: a file without the slot's game path falls back to one of its own");
        Require(HeelsMeasure.Resolve(resolved, "chara/equipment/e0100/model/c0201e0100_sho.mdl", HeelsSlot.Feet) is null &&
                HeelsMeasure.Resolve(null, PainterVisibility.NormalizePath(modFile), HeelsSlot.Feet) is null,
            "heels: a file Penumbra doesn't list isn't found");
    }

    private static void CheckSlots()
    {
        Require(HeelsSlots.FeetOrder.SequenceEqual([HeelsSlot.Feet, HeelsSlot.Legs, HeelsSlot.Top]) && (int)HeelsSlot.Legs == 3 && (int)HeelsSlot.Top == 1,
            "heels: the feet are looked for in the feet model, then the legs, then the body");
        Require(HeelsSlot.Feet.ReadBefore().SequenceEqual([HeelsSlot.Top, HeelsSlot.Legs]) && HeelsSlot.Top.ReadBefore().SequenceEqual(Array.Empty<HeelsSlot>()),
            "heels: Simple Heels reads the body's and legs' offsets before the feet's");
        var key = new HeelsModelKey(HeelsSlot.Feet, "a.mdl", 0b0111, 0b1);
        Require(key.SameModel(key with { Attributes = 0b1111 }) && !key.SameModel(key with { Shapes = 0 }) && !key.SameModel(key with { Slot = HeelsSlot.Legs }),
            "heels: a written offset's attribute doesn't make the model count as changed, while other shapes or slots do");
    }

    private static void CheckSimpleHeels()
    {
        Require(SimpleHeels.ItemName(HeelsSlot.Feet, 0, []) == "Smallclothes (Barefoot)" && SimpleHeels.ItemName(HeelsSlot.Feet, 6012, []) == "Unknown#6012" &&
                SimpleHeels.ItemName(HeelsSlot.Feet, 6012, ["Heels"]) == "Heels" && SimpleHeels.ItemName(HeelsSlot.Feet, 6012, ["A", "B"]) == "A and B" &&
                SimpleHeels.ItemName(HeelsSlot.Legs, 6012, ["A", "B", "C"]) == "A, B, C" &&
                SimpleHeels.ItemName(HeelsSlot.Top, 6012, ["A", "B", "C", "D"]) == "A & 3 others.",
            "heels: models are named as Simple Heels' Equipment Offsets list names them");
        Require(SimpleHeels.CurrentOffset("{\"DefaultOffset\":0.0523,\"EmoteConfigs\":[]}") == 0.0523f &&
                SimpleHeels.CurrentOffset("{\"PluginVersion\":\"0.11.1.12\"}") == 0f,
            "heels: Simple Heels' offset is read from its message, and a message without one means none");
        Require(SimpleHeels.CurrentOffset("") is null && SimpleHeels.CurrentOffset("not json") is null && SimpleHeels.CurrentOffset("[1]") is null,
            "heels: an empty or unreadable message means Simple Heels didn't answer");
    }

    private static void Refused<TException>(Action action, string name) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            Require(true, name);
            return;
        }
        Require(false, name);
    }
}
