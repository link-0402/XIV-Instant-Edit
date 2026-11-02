using System.Text;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Resources;
using static InstantEdit.TestSupport.Assertions;
using static InstantEdit.TestSupport.TestModels;

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
        UnalignedStringBlockStillRewrites();
        SavedSlotLinksBecomeMeshLinks();
        EditModeReadsMaterialsPerMesh();
        EditModeSlotNamesBecomeMeshNames();
    }

    private static void EditModeReadsMaterialsPerMesh()
    {
        var facts = ModFileFactsCache.FromBytes(TwoMeshModel(), ResourceKind.Model);
        Require(facts.MeshSlots.SequenceEqual(new[] { 0, 0 }) && facts.MaterialSlots.SequenceEqual(new[] { "/old.mtrl" }),
            "the mod editor reads which slot each mesh uses, next to the slots themselves");

        var node = new ResourceNode
        {
            Id = "m", GamePath = "chara/accessory/a0044/model/c0201a0044_nek.mdl", Kind = ResourceKind.Model, Origin = OriginKind.ModFile,
            MaterialSlots = facts.MaterialSlots, MeshSlots = facts.MeshSlots,
            Replacement = new ResourceReplacement { MaterialNames = new List<string> { string.Empty, "/b.mtrl" }, MaterialNamesPerMesh = true },
        };
        Require(node.OwnMaterial(1) == "/old.mtrl" && node.EffectiveMaterial(0) == "/old.mtrl" && node.EffectiveMaterial(1) == "/b.mtrl",
            "each mesh shows its own material, or the one it was pointed at");

        var unknown = new ResourceNode
        {
            Id = "u", GamePath = "x.mdl", Kind = ResourceKind.Model, Origin = OriginKind.ModFile,
            MaterialSlots = new[] { "/a.mtrl", "/b.mtrl" },
        };
        Require(unknown.MeshSlots.SequenceEqual(new[] { 0, 1 }) && unknown.EffectiveMaterial(1) == "/b.mtrl",
            "a model whose meshes are not known reads as one mesh per slot");
    }

    private static void EditModeSlotNamesBecomeMeshNames()
    {
        var saved = new ResourceReplacement { MaterialNames = new List<string> { "/a.mtrl" } };
        Require(saved.MeshMaterialNames(new[] { 0, 0, 1 }).SequenceEqual(new[] { "/a.mtrl", "/a.mtrl", string.Empty }),
            "a mod-editor set-up saved by slot is read through each mesh's slot");

        saved.UseMeshMaterialNames(new[] { 0, 0, 1 });
        Require(saved.MaterialNamesPerMesh && saved.MaterialNames.SequenceEqual(new[] { "/a.mtrl", "/a.mtrl" }),
            "and converted to one name per mesh the first time it is changed");

        saved.UseMeshMaterialNames(new[] { 1, 1 });
        Require(saved.MaterialNames.SequenceEqual(new[] { "/a.mtrl", "/a.mtrl" }), "a set-up already by mesh is left as it is");
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

    private static void UnalignedStringBlockStillRewrites()
    {
        // A model whose string block some other exporter did not pad to 4 bytes. The rewrite used to
        // wait for a size that was a multiple of 4 and of 16 apart from the block's own size, which
        // cannot exist, and never returned: the build could not be cancelled and the edit never finished.
        var source = TwoMeshModel(alignStrings: false);
        Require(BitConverter.ToUInt32(source, 68 + 2 * 136 + 4) % 4 != 0, "the fixture's string block is not a multiple of 4");

        var rewrite = Task.Run(() => ModelMaterialRewriter.RewriteMeshes(source, new[] { "/a.mtrl", "/b.mtrl" }));
        var finished = rewrite.Wait(TimeSpan.FromSeconds(10));
        Require(finished, "a model with an unaligned string block is rewritten instead of hanging");
        if (!finished)
            return;   // reading the result would wait for the loop that never ends
        var result = rewrite.Result;

        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(result).SequenceEqual(new[] { "/a.mtrl", "/b.mtrl" })
                && ModelMaterialRewriter.ReadMaterialsStrict(result).SequenceEqual(new[] { "/a.mtrl", "/b.mtrl" }),
            "its meshes load the materials they were given");
        uint oldVertex = BitConverter.ToUInt32(source, 16), newVertex = BitConverter.ToUInt32(result, 16);
        Require((newVertex - oldVertex) % 16 == 0 && newVertex > oldVertex,
            "the data after the material table still moves by a multiple of 16");
        Require(result.AsSpan((int)newVertex).SequenceEqual(source.AsSpan((int)oldVertex)),
            "vertex and index data come through unchanged");
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

        var lods = ModelMaterialRewriter.RewriteMeshes(TwoMeshModel(lodCount: 2), new string?[] { "/b.mtrl", null });
        Require(MeshSlotsOfEveryLod(lods).SequenceEqual(new[] { 0, 1, 0, 1 }) &&
                ModelMaterialRewriter.ReadMaterialsStrict(lods).SequenceEqual(new[] { "/b.mtrl", "/old.mtrl" }),
            "a later LOD's meshes follow the first LOD's onto their new slots, so a kept mesh keeps its material at a distance too");

        // The second LOD's first mesh gets two submeshes: its meshes are in another order than the first LOD's.
        var reordered = TwoMeshModel(lodCount: 2);
        BitConverter.TryWriteBytes(reordered.AsSpan((int)(LodPos(reordered) + 3 * 60 + 2 * 36 + 12), 2), (ushort)2);
        Require(MeshSlotsOfEveryLod(ModelMaterialRewriter.RewriteMeshes(reordered, new string?[] { "/b.mtrl", null })).SequenceEqual(new[] { 0, 1, 0, 0 }),
            "a later LOD whose meshes differ in submesh count from the first's keeps its slots");
    }

    /// <summary>The material slot of every mesh of the file, all LODs, from the mesh table.</summary>
    private static int[] MeshSlotsOfEveryLod(byte[] model)
    {
        // The LOD table follows the 56-byte model header; three 60-byte LODs, then 36-byte meshes with the slot at 8.
        long lods = LodPos(model);
        int meshCount = BitConverter.ToUInt16(model, (int)(lods - 56 + 4));
        long meshes = lods + 3 * 60;
        return Enumerable.Range(0, meshCount).Select(m => (int)BitConverter.ToUInt16(model, (int)(meshes + m * 36 + 8))).ToArray();
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
        Require(model.SlotLinkFiles is ["unreadable.mdl"], "a file that cannot be read is noted for later");

        Require(!ModelMaterialLinks.ConvertPending(model, Slots) && model.SlotLinkFiles is ["unreadable.mdl"],
            "a noted file that still cannot be read stays noted, and nothing changes");
        IReadOnlyList<int>? Back(string path) => path == "unreadable.mdl" ? new[] { 1, 0, 1 } : Slots(path);
        Require(ModelMaterialLinks.ConvertPending(model, Back) && model.VariantMaterialLinks[1].SequenceEqual(new[] { 0, 1, 0 }) &&
                model.VariantMaterialLinks[0].SequenceEqual(new[] { 1, 1 }) && model.SlotLinkFiles == null,
            "a noted file is converted once it can be read, and only that file");
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
}
