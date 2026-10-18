using System.Text;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's Build mode assigns materials per mesh: meshes that share one material in the
/// file each get a row, and giving them different materials splits it in the written copy.
/// </summary>
internal static class MeshMaterialScenarios
{
    public static void Run()
    {
        SharedMaterialIsReadPerMesh();
        DifferentMaterialsSplitTheSlot();
        SameMaterialRenamesInPlace();
        KeptMeshStaysOnItsSlot();
        AutomaticMatchFollowsDistinctNames();
        SavedSlotLinksBecomeMeshLinks();
    }

    private static void SharedMaterialIsReadPerMesh()
    {
        var model = TwoMeshModel();
        Require(ModelMaterialRewriter.ReadMeshMaterials(model).SequenceEqual(new[] { "/old.mtrl", "/old.mtrl" }),
            "two meshes sharing one material are listed one row each");
        Require(ModelMaterialRewriter.ReadMeshMaterialSlots(model).SequenceEqual(new[] { 0, 0 }),
            "both meshes read as using slot 0");
    }

    private static void DifferentMaterialsSplitTheSlot()
    {
        var source = TwoMeshModel();
        var result = ModelMaterialRewriter.RewriteMeshes(source, new[] { "/a.mtrl", "/b.mtrl" });

        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(result).SequenceEqual(new[] { "/a.mtrl", "/b.mtrl" }),
            "each mesh loads its own material once the shared slot is split");
        Require(ModelMaterialRewriter.ReadMaterialsStrict(result).SequenceEqual(new[] { "/a.mtrl", "/b.mtrl" }),
            "the split adds a second material slot");
        Require(BitConverter.ToUInt16(result, 14) == 2, "the file header counts the new slot");

        // Everything the rewrite does not own comes through byte for byte, moved by one alignment-safe shift.
        uint oldVertex = BitConverter.ToUInt32(source, 16), newVertex = BitConverter.ToUInt32(result, 16);
        int delta = (int)(newVertex - oldVertex);
        Require(delta % 16 == 0 && delta > 0, $"the data after the material table moves by a multiple of 16 ({delta})");
        Require(BitConverter.ToUInt32(result, 28) - BitConverter.ToUInt32(source, 28) == delta
                && BitConverter.ToUInt32(result, 8) - BitConverter.ToUInt32(source, 8) == delta,
            "index offset and runtime size move with it");
        Require(result.AsSpan((int)newVertex).SequenceEqual(source.AsSpan((int)oldVertex)),
            "vertex and index data come through unchanged");
        Require(result.AsSpan((int)newVertex - TailSize, TailSize).SequenceEqual(source.AsSpan((int)oldVertex - TailSize, TailSize)),
            "the tables after the material list (bounding boxes and so on) come through unchanged");

        long lod0 = LodPos(result);
        Require(BitConverter.ToUInt32(result, (int)lod0 + 52) == newVertex && BitConverter.ToUInt32(result, (int)lod0 + 56) == BitConverter.ToUInt32(result, 28),
            "the first LOD's vertex and index offsets move too");

        var again = ModelMaterialRewriter.RewriteMeshes(result, new[] { "/c.mtrl", "/c.mtrl" });
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(again).SequenceEqual(new[] { "/c.mtrl", "/c.mtrl" }),
            "a split file can be rewired again");
    }

    private static void SameMaterialRenamesInPlace()
    {
        var result = ModelMaterialRewriter.RewriteMeshes(TwoMeshModel(), new[] { "/a.mtrl", "/a.mtrl" });
        Require(ModelMaterialRewriter.ReadMaterialsStrict(result).SequenceEqual(new[] { "/a.mtrl" })
                && ModelMaterialRewriter.ReadMeshMaterialSlots(result).SequenceEqual(new[] { 0, 0 }),
            "meshes given the same material keep sharing one slot");

        var source = TwoMeshModel();
        Require(ReferenceEquals(ModelMaterialRewriter.RewriteMeshes(source, new string?[] { null, "/old.mtrl" }), source),
            "nothing to change returns the file as it is");
    }

    private static void KeptMeshStaysOnItsSlot()
    {
        var result = ModelMaterialRewriter.RewriteMeshes(TwoMeshModel(), new string?[] { null, "/b.mtrl" });
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(result).SequenceEqual(new[] { "/old.mtrl", "/b.mtrl" })
                && ModelMaterialRewriter.ReadMeshMaterialSlots(result).SequenceEqual(new[] { 0, 1 }),
            "a kept mesh stays on the file's own material while the other moves to a new slot");

        var first = ModelMaterialRewriter.RewriteMeshes(TwoMeshModel(), new string?[] { "/b.mtrl", null });
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(first).SequenceEqual(new[] { "/b.mtrl", "/old.mtrl" }),
            "the kept mesh keeps its name when it is not the first user of the slot");
    }

    private static void AutomaticMatchFollowsDistinctNames()
    {
        var written = new[] { "/mt_a.mtrl", "/mt_b.mtrl" };
        var plan = ModelMaterialLinks.Plan(new[] { "/x.mtrl", "/x.mtrl", "/y.mtrl" }, null, written);
        Require(plan.SequenceEqual(new[] { "/mt_a.mtrl", "/mt_a.mtrl", "/mt_b.mtrl" }),
            $"meshes sharing a material land on the same one automatically ({string.Join(", ", plan)})");

        var picked = ModelMaterialLinks.Plan(new[] { "/x.mtrl", "/x.mtrl" }, new[] { ModelMaterialLinks.Auto, 1 }, written);
        Require(picked.SequenceEqual(new[] { "/mt_a.mtrl", "/mt_b.mtrl" }), "one mesh of a shared material can be picked on its own");
    }

    private static void SavedSlotLinksBecomeMeshLinks()
    {
        var item = Guid.NewGuid();
        var model = new RaceModelEntry
        {
            SourcePath = "main.mdl",
            MaterialLinks = new List<int> { ModelMaterialLinks.Shared, 0 },
            UseVariants = true,
            VariantPaths = new List<string> { "variant.mdl", "unreadable.mdl" },
            VariantMaterialLinks = new List<List<int>> { new() { 1 }, new() { 1, 0 } },
        };
        model.SharedLinks.Add(new SharedMaterialLink { Slot = 0, Item = 7, Material = item });

        // main.mdl: three meshes on slots 1, 0, 0. variant.mdl: two meshes on slot 0.
        IReadOnlyList<int>? Slots(string path) => path switch
        {
            "main.mdl" => new[] { 1, 0, 0 },
            "variant.mdl" => new[] { 0, 0 },
            _ => null,
        };
        ModelMaterialLinks.ConvertSlotsToMeshes(model, Slots);

        Require(model.MaterialLinks.SequenceEqual(new[] { 0, ModelMaterialLinks.Shared, ModelMaterialLinks.Shared }),
            $"each mesh takes its slot's saved pick ({string.Join(", ", model.MaterialLinks)})");
        Require(model.SharedLinks.Count == 2 && model.SharedLink(-1, 1)?.Material == item && model.SharedLink(-1, 2)?.Material == item,
            "a shared pick follows every mesh of its slot");
        Require(model.VariantMaterialLinks[0].SequenceEqual(new[] { 1, 1 }), "variant files are converted too");
        Require(model.VariantMaterialLinks[1].SequenceEqual(new[] { 1, 0 }), "a file that cannot be read keeps its list");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private const int TailSize = 4 + 1 + 3 + 128;   // bone-map size, padding, bounding boxes

    private static long LodPos(byte[] model)
    {
        uint stack = BitConverter.ToUInt32(model, 4);
        long strings = 68 + stack;
        uint size = BitConverter.ToUInt32(model, (int)strings + 4);
        return strings + 8 + size + 56;
    }

    /// <summary>A V6 model with two meshes in the first LOD, both on the one material "/old.mtrl".</summary>
    private static byte[] TwoMeshModel()
    {
        var strings = new MemoryStream();
        int Name(string value)
        {
            int offset = (int)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(value + "\0"));
            return offset;
        }
        int bone = Name("j_kosi");
        int material = Name("/old.mtrl");
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);

        var vertices = Enumerable.Range(0, 96).Select(i => (byte)(i * 7)).ToArray();
        var indices = Enumerable.Range(0, 24).Select(i => (byte)(200 - i)).ToArray();

        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        w.Write((ushort)2); w.Write((ushort)0); w.Write((uint)strings.Length);
        w.Write(strings.ToArray());
        // Model header.
        w.Write(0.5f);
        w.Write((ushort)2); w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)1); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u);
        w.Write((ushort)2); w.Write(0u); w.Write(new byte[6]);
        long lods = data.Position;
        w.Write(new byte[3 * 60]);
        // Meshes: both on material 0.
        for (int m = 0; m < 2; m++)
        {
            w.Write((ushort)4); w.Write((ushort)0); w.Write(6u);
            w.Write((ushort)0); w.Write((ushort)m); w.Write((ushort)1); w.Write((ushort)0);
            w.Write((uint)(m * 6)); w.Write((uint)(m * 48)); w.Write(0u); w.Write(0u);
            w.Write((byte)12); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
        }
        for (int s = 0; s < 2; s++)
        {
            w.Write((uint)(s * 6)); w.Write(6u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)1);
        }
        w.Write((uint)material);
        w.Write((uint)bone);
        w.Write((ushort)0); w.Write((ushort)1);   // bone table header
        w.Write((ushort)0); w.Write((ushort)0);   // its two-entry array
        // What the rewriter copies through: bone-map size, padding, bounding boxes.
        w.Write(0u);
        w.Write((byte)3); w.Write(new byte[3]);
        w.Write(Enumerable.Range(0, 128).Select(i => (byte)(i + 1)).ToArray());

        long runtime = data.Length;
        const int stack = 2 * 136;
        long vertexAt = 68 + stack + runtime;
        long indexAt = vertexAt + vertices.Length;

        var lod = new byte[60];
        BitConverter.TryWriteBytes(lod.AsSpan(0, 2), (ushort)0);
        BitConverter.TryWriteBytes(lod.AsSpan(2, 2), (ushort)2);
        BitConverter.TryWriteBytes(lod.AsSpan(52, 4), (uint)vertexAt);
        BitConverter.TryWriteBytes(lod.AsSpan(56, 4), (uint)indexAt);
        var body = data.ToArray();
        lod.CopyTo(body, lods);

        var file = new MemoryStream();
        var h = new BinaryWriter(file);
        h.Write(0x01000006u); h.Write((uint)stack); h.Write((uint)runtime);
        h.Write((ushort)2); h.Write((ushort)1);
        h.Write((uint)vertexAt); h.Write(0u); h.Write(0u);
        h.Write((uint)indexAt); h.Write(0u); h.Write(0u);
        h.Write((uint)vertices.Length); h.Write(0u); h.Write(0u);
        h.Write((uint)indices.Length); h.Write(0u); h.Write(0u);
        h.Write((byte)1); h.Write((byte)0); h.Write((byte)0); h.Write((byte)0);
        var declarations = new byte[stack];
        declarations[0] = declarations[136] = 0xFF;
        h.Write(declarations);
        h.Write(body);
        h.Write(vertices);
        h.Write(indices);
        return file.ToArray();
    }
}
