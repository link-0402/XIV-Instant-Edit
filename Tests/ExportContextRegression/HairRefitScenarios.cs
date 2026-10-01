using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Hair UV refitting's pure parts on synthetic files: a mod's redirections across options, crop
/// windows and exact UV moves, cutting textures without re-encoding, moving a model's UVs in every
/// LOD, the planner's grouping and the cases it leaves alone, the journal and the card's texts.
/// </summary>
internal static class HairRefitScenarios
{
    private const uint Bgra = TextureCost.Bgra8;
    private const uint Bc7 = TextureCost.Bc7;
    private const uint NormalSampler = 0x0C5EC1F1;
    private const uint MaskSampler = 0x8A4E82B6;
    private const string Hair = "chara/human/c0201/obj/hair/h0154/";
    private const string Miqote = "chara/human/c1801/obj/hair/h0154/";

    public static void Run(string testRoot)
    {
        CheckFileMap();
        CheckWindows();
        CheckCrop();
        CheckModel();
        CheckPlanner();
        CheckJournal(testRoot);
        CheckViews();
    }

    // ---- The mod's redirections -----------------------------------------------------------------------

    private static void CheckFileMap()
    {
        const string meta = """
            {
              "FileVersion": 4,
              "DefaultData": { "Files": { "Chara/A.tex": "default group\\a.tex" } },
              "Groups": [
                { "Name": "Race", "Options": [ { "Name": "Miqo'te", "Files": { "chara/b.mdl": "race\\b.mdl", "chara/a2.tex": "default group\\a.tex" } } ] },
                { "Name": "Both", "Options": [ { "Name": "x" }, { "Name": "y" } ], "Containers": [ { "Files": {} }, { "Files": { "chara/c.mtrl": "both/c.mtrl" } } ] }
              ]
            }
            """;
        var map = ModFileMap.Read(meta);
        Require(map.ForFile("Default Group/A.TEX").Select(m => m.GamePath).Order().SequenceEqual(["chara/a.tex", "chara/a2.tex"]) &&
                map.ForGamePath("CHARA/b.mdl").Single() is { File: "race/b.mdl", Container: "group 0 option 0" } &&
                map.ForGamePath("chara/c.mtrl").Single().Container == "group 1 container 1",
            "hair refit: a mod's redirections come from its default files, its options and its combining containers, without regard to case");
        Require(map.Files(".tex").Single() == "default group/a.tex" && map.Files(".mdl").Single() == "race/b.mdl",
            "hair refit: each of a mod's files is listed once, however many paths it serves");
        var refused = false;
        try { ModFileMap.Read("""{ "FileVersion": 3 }"""); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "hair refit: meta.json from before Penumbra 1.7 is refused");
    }

    // ---- Windows and UVs ------------------------------------------------------------------------------

    private static void CheckWindows()
    {
        // The Sims 4 layout: strands in the top-left 1024 × 1024 of a 2048 × 4096 texture.
        var sims = TextureCrop.Fit(new Vector2(0.01f, 0.002f), new Vector2(0.5f, 0.25f), TextureCrop.MaxShift(2048, true), TextureCrop.MaxShift(4096, true));
        Require(sims == new UvWindow(1, 2, 0, 0) && sims.Value.Fraction == 1.0 / 8,
            "hair refit: hair in the top-left eighth of a 1:2 texture fits a window of half the width and a quarter of the height, its edges included");
        Require(TextureCrop.Fit(new Vector2(0.55f, 0.3f), new Vector2(0.7f, 0.45f), 12, 12) == new UvWindow(2, 2, 2, 1) &&
                TextureCrop.Fit(new Vector2(0.4f, 0.1f), new Vector2(0.6f, 0.2f), 12, 12) == new UvWindow(0, 2, 0, 0),
            "hair refit: windows are aligned to their own size, so UVs across the middle keep the whole width");
        Require(TextureCrop.Fit(new Vector2(-0.01f, 0), new Vector2(0.2f, 0.2f), 12, 12) is null && TextureCrop.Fit(new Vector2(0, 0), new Vector2(0.2f, 1.5f), 12, 12) is null &&
                TextureCrop.Fit(new Vector2(0, 0), new Vector2(0.01f, 0.01f), 1, 0) == new UvWindow(1, 0, 0, 0),
            "hair refit: UVs outside 0..1 get no window, and a window halves each side only as often as the textures allow");
        Require(TextureCrop.MaxShift(2048, true) == 7 && TextureCrop.MaxShift(64, false) == 2 && TextureCrop.MaxShift(1000, true) == 1 && TextureCrop.MaxShift(24, false) == 0,
            "hair refit: a side halves while it divides evenly and stays 16 pixels or more, in whole blocks when compressed");

        var window = new UvWindow(1, 2, 1, 3);
        var random = new Random(5);
        var exact = true;
        for (var i = 0; i < 20000 && exact; i++)
        {
            float u = 0.5f + (float)random.NextDouble() * 0.5f, v = 0.75f + (float)random.NextDouble() * 0.25f;
            float half = (float)(Half)u, halfV = (float)(Half)v;
            float movedU = window.MoveU(u), movedV = window.MoveV(v), movedHalf = window.MoveU(half), movedHalfV = window.MoveV(halfV);
            exact = movedU == (u - 0.5f) * 2 && movedV == (v - 0.75f) * 4 && (float)(Half)movedHalf == movedHalf && (float)(Half)movedHalfV == movedHalfV &&
                    movedU is >= 0 and <= 1 && movedV is >= 0 and <= 1;
        }
        Require(exact, "hair refit: UVs inside a window move into 0..1 exactly, in single and in half precision");
    }

    // ---- Cutting textures -----------------------------------------------------------------------------

    /// <summary> A BGRA texture whose every pixel of every level spells out its level and position. </summary>
    private static byte[] Labelled(int width, int height, int mips)
    {
        var tex = TextureCompressionScenarios.Tex(Bgra, width, height, mips);
        for (var mip = 0; mip < mips; mip++)
        {
            int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(tex.AsSpan(28 + mip * 4));
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var at = offset + (y * w + x) * 4;
                tex[at] = (byte)x;
                tex[at + 1] = (byte)y;
                tex[at + 2] = (byte)mip;
                tex[at + 3] = 255;
            }
        }
        return tex;
    }

    private static void CheckCrop()
    {
        var original = Labelled(64, 128, 8);
        var cut = TextureCrop.Crop(original, new UvWindow(1, 2, 1, 2));
        var header = InstantEdit.Services.TextureFiles.ReadTex(cut);
        var pixelsRight = true;
        for (var mip = 0; mip < header.Mips && pixelsRight; mip++)
        {
            int w = Math.Max(1, 32 >> mip), h = Math.Max(1, 32 >> mip);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(cut.AsSpan(28 + mip * 4));
            for (var y = 0; y < h && pixelsRight; y++)
            for (var x = 0; x < w && pixelsRight; x++)
            {
                var at = offset + (y * w + x) * 4;
                pixelsRight = cut[at] == (32 >> mip) + x && cut[at + 1] == (64 >> mip) + y && cut[at + 2] == mip;
            }
        }
        Require(header is { Format: Bgra, Width: 32, Height: 32, Mips: 6 } && pixelsRight && cut.Length == 80 + TextureCost.Vram(Bgra, 32, 32, 6),
            "hair refit: each level of a cut texture keeps exactly the window's pixels of the same level, down to 1 × 1");

        // Compressed: blocks are copied as they are; levels that would cut through a block are left off.
        var blocks = TextureCompressionScenarios.Tex(Bc7, 64, 128, 8);
        for (var mip = 0; mip < 8; mip++)
        {
            int columns = Math.Max(1, (64 >> mip) / 4), rows = Math.Max(1, (128 >> mip) / 4);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(blocks.AsSpan(28 + mip * 4));
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var at = offset + (row * columns + column) * 16;
                blocks[at] = (byte)column;
                blocks[at + 1] = (byte)row;
                blocks[at + 2] = (byte)mip;
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(20), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(blocks.AsSpan(24), 7);
        var corner = TextureCrop.Crop(blocks, new UvWindow(1, 2, 0, 0));
        var inside = TextureCrop.Crop(blocks, new UvWindow(1, 2, 1, 1));
        var cornerHeader = InstantEdit.Services.TextureFiles.ReadTex(corner);
        var insideHeader = InstantEdit.Services.TextureFiles.ReadTex(inside);
        var at1 = (int)BinaryPrimitives.ReadUInt32LittleEndian(inside.AsSpan(28 + 4));
        Require(cornerHeader is { Width: 32, Height: 32, Mips: 6 } && insideHeader is { Width: 32, Height: 32, Mips: 4 } &&
                inside[80] == 8 && inside[81] == 8 && inside[82] == 0 && inside[at1] == 4 && inside[at1 + 1] == 4 && inside[at1 + 2] == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(inside.AsSpan(20)) == 3 && BinaryPrimitives.ReadUInt32LittleEndian(inside.AsSpan(24)) == 3 &&
                BinaryPrimitives.ReadUInt32LittleEndian(corner.AsSpan(20)) == 5,
            "hair refit: a compressed texture is cut along its blocks, without re-encoding; a window off the corner ends the chain where a level would split a block");
        var refused = false;
        try { TextureCrop.Crop(blocks, new UvWindow(3, 0, 0, 0)); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "hair refit: a window that doesn't divide a texture into whole blocks of at least 16 pixels is refused");
    }

    // ---- Models ---------------------------------------------------------------------------------------

    private sealed record MeshSpec(int Material, Vector4[] Uvs, bool Half = false, bool TwoComponents = false);

    /// <summary>
    /// A V6 model whose meshes hold a position and a UV per vertex; each LOD names a range of meshes,
    /// and LODs may share vertex data.
    /// </summary>
    private static byte[] Model(string[] materials, MeshSpec[] meshes, (int First, int Count)[] lods)
    {
        const int declarationSize = 136;
        var strings = new MemoryStream();
        var offsets = materials.Select(name =>
        {
            var offset = (int)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(name + "\0"));
            return offset;
        }).ToList();
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);

        var vertexData = new MemoryStream();
        var meshOffsets = new List<int>();
        var strides = new List<int>();
        foreach (var mesh in meshes)
        {
            meshOffsets.Add((int)vertexData.Length);
            var uvSize = mesh.Half ? (mesh.TwoComponents ? 4 : 8) : mesh.TwoComponents ? 8 : 16;
            strides.Add(12 + uvSize);
            var w = new BinaryWriter(vertexData);
            foreach (var uv in mesh.Uvs)
            {
                w.Write(1f); w.Write(2f); w.Write(3f);
                float[] values = mesh.TwoComponents ? [uv.X, uv.Y] : [uv.X, uv.Y, uv.Z, uv.W];
                foreach (var value in values)
                {
                    if (mesh.Half) w.Write((Half)value);
                    else w.Write(value);
                }
            }
        }

        var head = new MemoryStream();
        var h = new BinaryWriter(head);
        foreach (var mesh in meshes)
        {
            var declaration = new byte[declarationSize];
            byte uvType = mesh.Half ? (byte)(mesh.TwoComponents ? 13 : 14) : (byte)(mesh.TwoComponents ? 1 : 3);
            new byte[] { 0, 0, 2, 0, 0 }.CopyTo(declaration, 0);
            new byte[] { 0, 12, uvType, 4, 0 }.CopyTo(declaration, 8);
            declaration[16] = 0xFF;
            h.Write(declaration);
        }
        h.Write((ushort)materials.Length); h.Write((ushort)0); h.Write((int)strings.Length); h.Write(strings.ToArray());
        // Mesh header: radius, then mesh, attribute, submesh, material, bone and bone table counts, shapes, LOD count, flags.
        h.Write(1f);
        h.Write((ushort)meshes.Length); h.Write((ushort)0); h.Write((ushort)0); h.Write((ushort)materials.Length); h.Write((ushort)0); h.Write((ushort)0);
        h.Write((ushort)0); h.Write((ushort)0); h.Write((ushort)0);
        h.Write((byte)lods.Length); h.Write((byte)0); h.Write((ushort)0); h.Write((byte)0); h.Write((byte)0);
        h.Write(0f); h.Write(0f); h.Write((ushort)0); h.Write((ushort)0);
        h.Write(0u); h.Write((ushort)0); h.Write((ushort)0); h.Write(0u); h.Write(0u);
        var lodAt = (int)head.Length;
        h.Write(new byte[3 * 60]);
        foreach (var (mesh, index) in meshes.Select((mesh, index) => (mesh, index)))
        {
            h.Write((ushort)mesh.Uvs.Length); h.Write((ushort)0); h.Write(0u);
            h.Write((ushort)mesh.Material); h.Write((ushort)0); h.Write((ushort)0); h.Write((ushort)0);
            h.Write(0u);
            h.Write((uint)meshOffsets[index]); h.Write(0u); h.Write(0u);
            h.Write((byte)strides[index]); h.Write((byte)0); h.Write((byte)0); h.Write((byte)1);
        }
        foreach (var offset in offsets)
            h.Write((uint)offset);
        h.Write(new byte[64]);

        var stack = head.ToArray();
        var file = new byte[68 + stack.Length + vertexData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x01000006);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)stack.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)meshes.Length);
        file[64] = (byte)lods.Length;
        var dataStart = 68 + stack.Length;
        for (var lod = 0; lod < 3; lod++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16 + lod * 4), (uint)dataStart);
        for (var lod = 0; lod < lods.Length; lod++)
        {
            var at = lodAt + lod * 60;
            BinaryPrimitives.WriteUInt16LittleEndian(stack.AsSpan(at), (ushort)lods[lod].First);
            BinaryPrimitives.WriteUInt16LittleEndian(stack.AsSpan(at + 2), (ushort)lods[lod].Count);
            BinaryPrimitives.WriteUInt32LittleEndian(stack.AsSpan(at + 52), (uint)dataStart);
        }
        stack.CopyTo(file, 68);
        vertexData.ToArray().CopyTo(file, dataStart);
        return file;
    }

    private static Vector4[] Uvs(params (float U, float V)[] points) => points.Select(p => new Vector4(p.U, p.V, p.U, p.V)).ToArray();

    private static void CheckModel()
    {
        var hair = Uvs((0.01f, 0.02f), (0.49f, 0.24f), (0.25f, 0.125f));
        var other = Uvs((0.75f, 0.9f), (0.9f, 0.95f), (0.8f, 0.92f));
        // LOD 1 draws mesh 0 again from the same vertex data, and its own half-precision mesh 2.
        var bytes = Model(["/mt_c0201h0154_hir_a.mtrl", "/mt_c0201h0154_acc_b.mtrl"],
            [new MeshSpec(0, hair), new MeshSpec(1, other), new MeshSpec(0, Uvs((0.125f, 0.0625f), (0.375f, 0.1875f), (0.5f, 0.25f)), Half: true)],
            [(0, 2), (0, 1), (2, 1)]);
        var model = RefitModel.Read(bytes);
        using (var stream = new MemoryStream(bytes))
            Require(RefitModel.ReadMaterialNames(stream)!.SequenceEqual(["/mt_c0201h0154_hir_a.mtrl", "/mt_c0201h0154_acc_b.mtrl"]),
                "hair refit: a model's material names are read from its headers alone");
        Require(model.Meshes.Count == 4 && model.Meshes.Count(m => m.Index == 0) == 2 && model.Meshes.Single(m => m.Index == 2) is { Half: true, Lod: 2 } &&
                model.Meshes[0].Uvs[1] == new Vector4(0.49f, 0.24f, 0.49f, 0.24f),
            "hair refit: every LOD's meshes are read with both UV sets, in single or half precision");

        var window = new UvWindow(1, 2, 0, 0);
        var moved = RefitModel.Read(model.WithWindow(new HashSet<int> { 0 }, window));
        var first = moved.Meshes.First(m => m.Index == 0);
        var changedBytes = bytes.Zip(model.WithWindow(new HashSet<int> { 0 }, window)).Count(pair => pair.First != pair.Second);
        Require(first.Uvs[1] == new Vector4(0.98f, 0.96f, 0.98f, 0.96f) && moved.Meshes.Single(m => m.Index == 2).Uvs[2] == new Vector4(1, 1, 1, 1) &&
                moved.Meshes.Single(m => m.Index == 1).Uvs.SequenceEqual(other) && changedBytes <= 3 * 16 + 3 * 8,
            "hair refit: the chosen material's meshes move into the window in every LOD, a shared vertex buffer once; other meshes and every other byte stay");
        var inexact = false;
        try { new UvWindow(1, 0, 1, 0).MoveU(1e-8f); }
        catch (InvalidDataException) { inexact = true; }
        Require(inexact, "hair refit: a UV that can't be moved exactly is refused rather than rounded");
    }

    // ---- Planning --------------------------------------------------------------------------------------

    private sealed class FakeMod
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ModFileMap.Mapping> Mappings = [];
        public readonly HashSet<string> Game = new(StringComparer.OrdinalIgnoreCase);

        public void Map(string gamePath, string file, byte[] bytes, string container = ModFileMap.Default)
        {
            Files[file] = bytes;
            Mappings.Add(new ModFileMap.Mapping(gamePath, file, container));
        }

        public RefitResult Plan(params string[] seeds)
        {
            var planner = new HairRefitPlanner(new ModFileMap(Mappings), file => Files.GetValueOrDefault(file),
                file =>
                {
                    if (!Files.TryGetValue(file, out var bytes))
                        return null;
                    try
                    {
                        using var stream = new MemoryStream(bytes);
                        return RefitModel.ReadMaterialNames(stream);
                    }
                    catch (InvalidDataException) { return null; }
                }, Game.Contains);
            return planner.Plan(seeds);
        }
    }

    private static byte[] HairMaterial(string textures, string shader = HairRefitPlanner.HairShader)
        => TextureCompressionScenarios.Mtrl(shader, [(textures + "norm.tex", 0), (textures + "mask.tex", 0)], [(NormalSampler, 0), (MaskSampler, 1)], [], 0);

    /// <summary> A two-race hair mod: the default files for c0201, an option for Miqo'te whose material reads the same textures. </summary>
    private static FakeMod HairMod(Vector4[]? miqoteUvs = null)
    {
        var mod = new FakeMod();
        var textures = Hair + "texture/c0201h0154_hir_";
        mod.Map(Hair + "model/c0201h0154_hir.mdl", "hair/c0201.mdl", Model(["/mt_c0201h0154_hir_a.mtrl", "/mt_c0201h0154_acc_b.mtrl"],
            [new MeshSpec(0, Uvs((0.01f, 0.02f), (0.49f, 0.24f), (0.25f, 0.125f))), new MeshSpec(1, Uvs((0.7f, 0.9f), (0.9f, 0.95f), (0.8f, 0.92f)))],
            [(0, 2)]));
        mod.Map(Hair + "material/v0001/mt_c0201h0154_hir_a.mtrl", "hair/c0201_a.mtrl", HairMaterial(textures));
        mod.Map(textures + "norm.tex", "hair/norm.tex", Labelled(64, 128, 8));
        mod.Map(textures + "mask.tex", "hair/mask.tex", Labelled(64, 128, 8));
        mod.Map(Miqote + "model/c1801h0154_hir.mdl", "hair/c1801.mdl", Model(["/mt_c1801h0154_hir_a.mtrl"],
            [new MeshSpec(0, miqoteUvs ?? Uvs((0.1f, 0.05f), (0.4f, 0.2f), (0.3f, 0.1f)), Half: true)], [(0, 1)]), "group 0 option 0");
        mod.Map(Miqote + "material/v0001/mt_c1801h0154_hir_a.mtrl", "hair/c1801_a.mtrl", HairMaterial(textures), "group 0 option 0");
        return mod;
    }

    private static void CheckPlanner()
    {
        var result = HairMod().Plan("hair/norm.tex", "hair/mask.tex");
        var plan = result.Plans.SingleOrDefault();
        Require(result.Refusals.Count == 0 && plan is not null && plan.Window == new UvWindow(1, 2, 0, 0) &&
                plan.Textures.SequenceEqual(["hair/mask.tex", "hair/norm.tex"]) && plan.Materials.SequenceEqual(["hair/c0201_a.mtrl", "hair/c1801_a.mtrl"]) &&
                plan.Models.Count == 2 && plan.Models["hair/c0201.mdl"].SetEquals([0]) && plan.Models["hair/c1801.mdl"].SetEquals([0]),
            "hair refit: a worn texture brings every material of the mod that reads it, every model of any option or race that uses them, and their other textures");

        // What the game draws stays the same: each vertex reads the same pixel from the cut texture at its moved UV.
        var mod = HairMod();
        var cut = TextureCrop.Crop(mod.Files["hair/norm.tex"], plan!.Window);
        var model = RefitModel.Read(mod.Files["hair/c0201.mdl"]);
        var moved = RefitModel.Read(model.WithWindow(plan.Models["hair/c0201.mdl"].ToHashSet(), plan.Window));
        var same = true;
        foreach (var (before, after) in model.Meshes.Where(m => m.MaterialIndex == 0).Zip(moved.Meshes.Where(m => m.MaterialIndex == 0)))
            for (var v = 0; v < before.Uvs.Length; v++)
                same &= Pixel(mod.Files["hair/norm.tex"], 64, 128, before.Uvs[v]) == Pixel(cut, 32, 32, after.Uvs[v]);
        Require(same, "hair refit: every moved vertex samples the same pixel from the cut texture as before");

        // Texture variants: another option puts another file at the same path. They share one window, so they are cut together.
        var variants = HairMod();
        variants.Map(Hair + "texture/c0201h0154_hir_norm.tex", "variants/norm_b.tex", Labelled(64, 128, 8), "group 1 option 0");
        // A variant that comes with its own model, whose UVs fill the texture, keeps every variant whole, whichever is worn.
        var filling = HairMod();
        filling.Map(Hair + "texture/c0201h0154_hir_norm.tex", "variants/norm_b.tex", Labelled(64, 128, 8), "group 1 option 0");
        filling.Map(Hair + "model/c0201h0154_hir.mdl", "variants/c0201_b.mdl",
            Model(["/mt_c0201h0154_hir_a.mtrl"], [new MeshSpec(0, Uvs((0.02f, 0.01f), (0.98f, 0.97f), (0.5f, 0.5f)))], [(0, 1)]), "group 1 option 0");
        Require(variants.Plan("hair/norm.tex").Plans.Single() is { Window: { ShiftU: 1, ShiftV: 2, TileU: 0, TileV: 0 } } variantPlan &&
                variantPlan.Textures.Contains("variants/norm_b.tex") && variants.Plan("variants/norm_b.tex").Plans.Single().Textures.Contains("hair/norm.tex") &&
                filling.Plan("hair/norm.tex") is { Plans.Count: 0, Refusals.Count: 0 } && filling.Plan("variants/norm_b.tex") is { Plans.Count: 0, Refusals.Count: 0 },
            "hair refit: texture variants in other options are cut with the same window, and one whose model fills the texture keeps them all whole");

        var swapped = HairMod();
        swapped.Map("chara/human/c0101/obj/hair/h0154/texture/c0101h0154_hir_norm.tex", "hair/norm.tex", swapped.Files["hair/norm.tex"], "group 0 option 1");
        swapped.Game.Add("chara/human/c0101/obj/hair/h0154/texture/c0101h0154_hir_norm.tex");
        var custom = HairMod();
        custom.Map("chara/human/c0101/obj/hair/h0154/texture/unused_norm.tex", "hair/norm.tex", custom.Files["hair/norm.tex"], "group 0 option 1");
        Require(swapped.Plan("hair/norm.tex") is { Plans.Count: 0, Refusals: [{ Reason: var swapReason }] } && swapReason.Contains("game's own materials") &&
                custom.Plan("hair/norm.tex").Plans.Count == 1,
            "hair refit: a texture an option puts where the game's own materials read it is left alone; one at a path the game doesn't have is no obstacle");

        var shared = HairMod();
        shared.Map("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl", "gear/top.mtrl", HairMaterial(Hair + "texture/c0201h0154_hir_", "character.shpk"));
        Require(shared.Plan("hair/norm.tex").Refusals.Single().Reason.Contains("character.shpk"),
            "hair refit: a texture another shader reads too is left alone");

        // The c0201 material and textures without a c0201 model: the game's own hair model uses them.
        var vanillaModel = HairMod();
        vanillaModel.Mappings.RemoveAll(m => m.File == "hair/c0201.mdl");
        vanillaModel.Game.Add(Hair + "material/v0001/mt_c0201h0154_hir_a.mtrl");
        // The mod's model only in an option: with the option off the game's model shows the mod's textures anyway.
        var optional = HairMod();
        optional.Mappings.RemoveAll(m => m.File == "hair/c0201.mdl");
        optional.Map(Hair + "model/c0201h0154_hir.mdl", "hair/c0201.mdl", optional.Files["hair/c0201.mdl"], "group 0 option 2");
        optional.Game.Add(Hair + "material/v0001/mt_c0201h0154_hir_a.mtrl");
        // A leftover material none of the mod's models names, in a folder whose model the mod replaces.
        var leftover = HairMod();
        leftover.Map(Hair + "material/v0001/mt_c0201h0154_hir_z.mtrl", "hair/c0201_z.mtrl", HairMaterial(Hair + "texture/c0201h0154_hir_"));
        leftover.Game.Add(Hair + "material/v0001/mt_c0201h0154_hir_z.mtrl");
        Require(vanillaModel.Plan("hair/norm.tex").Refusals.Single().Reason.Contains("game's own models") && optional.Plan("hair/norm.tex").Plans.Count == 1 &&
                leftover.Plan("hair/norm.tex").Plans.Count == 1,
            "hair refit: a material no model of the mod uses is left alone where the game has its own, unless the mod replaces the models of its folder");

        var gameTexture = HairMod();
        gameTexture.Map(Hair + "material/v0001/mt_c0201h0154_hir_a.mtrl", "hair/c0201_a.mtrl",
            TextureCompressionScenarios.Mtrl(HairRefitPlanner.HairShader, [(Hair + "texture/c0201h0154_hir_norm.tex", 0), ("chara/common/texture/vanilla_mask.tex", 0)],
                [(NormalSampler, 0), (MaskSampler, 1)], [], 0));
        gameTexture.Game.Add("chara/common/texture/vanilla_mask.tex");
        Require(gameTexture.Plan("hair/norm.tex").Refusals.Single().Reason.Contains("vanilla_mask.tex"),
            "hair refit: hair whose material also reads a texture from the game, which wouldn't be cut, is left alone");

        // The second UV set inside the window moves along; outside it, the window grows to hold it.
        var differing = HairMod([new Vector4(0.1f, 0.05f, 0.1f, 0.05f), new Vector4(0.4f, 0.2f, 0.3f, 0.2f), new Vector4(0.3f, 0.1f, 0.3f, 0.1f)]);
        var wider = HairMod([new Vector4(0.1f, 0.05f, 0.1f, 0.05f), new Vector4(0.4f, 0.2f, 0.4f, 0.45f), new Vector4(0.3f, 0.1f, 0.3f, 0.1f)]);
        var wrapping = HairMod(Uvs((0.1f, 0.05f), (1.4f, 0.2f), (0.3f, 0.1f)));
        var spread = HairMod(Uvs((-0.1f, 0.05f), (0.9f, 1.6f), (0.3f, 0.1f)));
        Require(differing.Plan("hair/norm.tex").Plans.Single().Window == new UvWindow(1, 2, 0, 0) &&
                wider.Plan("hair/norm.tex").Plans.Single().Window == new UvWindow(1, 1, 0, 0) &&
                wrapping.Plan("hair/norm.tex").Refusals.Single().Reason.Contains("outside") &&
                spread.Plan("hair/norm.tex") is { Plans.Count: 0, Refusals.Count: 0 },
            "hair refit: both UV sets must fit the window, hair that wraps around is left alone, and hair spread over most of its texture has nothing to cut");

        var broken = HairMod();
        broken.Map("chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl", "hair/broken.mdl", [1, 2, 3, 4]);
        Require(broken.Plan("hair/norm.tex").Refusals.Single().Reason.Contains("broken.mdl"),
            "hair refit: when one of the mod's models can't be read, nothing in the mod is refit, since it could use any material");
    }

    /// <summary> The BGRA pixel a UV lands on (nearest, clamped), as an integer. </summary>
    private static int Pixel(byte[] tex, int width, int height, Vector4 uv)
    {
        int x = Math.Clamp((int)(uv.X * width), 0, width - 1), y = Math.Clamp((int)(uv.Y * height), 0, height - 1);
        return BinaryPrimitives.ReadInt32LittleEndian(tex.AsSpan(80 + (y * width + x) * 4));
    }

    // ---- The journal ----------------------------------------------------------------------------------

    private static void CheckJournal(string testRoot)
    {
        var root = Path.Combine(testRoot, "HairRefit");
        var config = Path.Combine(root, "config");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(cache);
        byte[] model = [1, 2, 3], texture = [4, 5, 6];
        string modelSha = TextureBackupStore.Hash(model), textureSha = TextureBackupStore.Hash(texture);
        var modelBackup = TextureBackupStore.StoreBackup(cache, model, modelSha, ".mdl");
        var textureBackup = TextureBackupStore.StoreBackup(cache, texture, textureSha);
        var refused = false;
        try { TextureBackupStore.StoreBackup(cache, model, modelSha, ".exe"); }
        catch (ArgumentException) { refused = true; }
        Require(modelBackup.EndsWith(modelSha + ".mdl", StringComparison.Ordinal) && refused,
            "hair refit: models are backed up next to textures under their hash; nothing else is");

        RefitFile File(string name, string backup, string sha, long length) => new()
        {
            File = Path.Combine(root, "mod", name), RelativePath = name, Backup = backup, OriginalSha256 = sha, OriginalLength = length, NewSha256 = "B" + name,
            NewLength = 1,
        };
        var group = new RefitGroup { ModDirectory = "Hair", ModName = "Hair", Files = [File("a.mdl", modelBackup, modelSha, 3), File("a.tex", textureBackup, textureSha, 3)] };
        var store = new TextureBackupStore(config);
        store.Load();
        store.RecordRefit(group);
        var reloaded = new TextureBackupStore(config);
        reloaded.Load();
        Require(reloaded.Refits.Single().Files.Count == 2 && reloaded.BackupTotals() == (2, 6) && reloaded.Refits.Single().BytesSaved == 4,
            "hair refit: a refit group and its backups survive a reload and count in the totals");
        reloaded.UpdateRefit(reloaded.Refits.Single(), [reloaded.Refits.Single().Files[1]]);
        var kept = System.IO.File.Exists(textureBackup) && !System.IO.File.Exists(modelBackup);
        reloaded.ForgetRefits(reloaded.Refits.ToList());
        Require(kept && reloaded.Refits.Count == 0 && !System.IO.File.Exists(textureBackup),
            "hair refit: restoring part of a group keeps the rest and its backups; forgetting it deletes them");
    }

    // ---- The card -------------------------------------------------------------------------------------

    private static void CheckViews()
    {
        var group = new RefitGroup
        {
            ModDirectory = "M", Files = [new RefitFile { File = @"C:\a.tex", RelativePath = "a.tex", Backup = "b", OriginalSha256 = "A", OriginalLength = 4L << 20, NewSha256 = "B", NewLength = 1L << 20 }],
        };
        var run = new CompressionRun { Finished = DateTimeOffset.Now, Refit = 1, BytesBefore = 4L << 20, BytesAfter = 1L << 20, NotRefit = ["a.tex: r", "b.tex: s"] };
        Require(TextureCompressionViews.Totals([], [group]) == "1 hairstyle refit so far, 3.00 MiB smaller in total." &&
                TextureCompressionViews.LastRun(run).StartsWith("Last run: 1 hairstyle refit, 3.00 MiB smaller, ", StringComparison.Ordinal) &&
                TextureCompressionViews.NotRefit(run).StartsWith("2 hair textures you wear use only part of their space but can't be refit safely.", StringComparison.Ordinal) &&
                TextureCompressionViews.Restored(new CompressionRestore(2, [], [], [], 1)).StartsWith("Restored 2 textures and 1 hairstyle.", StringComparison.Ordinal),
            "hair refit card: refit hairstyles count in the totals, the last run and restoring, and hair left as it is says so");
        Require(CompressionRun.After(run, new CompressionRun { NotRefit = ["c.tex: t"] }) is { Refit: 1, NotRefit: ["c.tex: t"] },
            "hair refit card: a quiet run keeps the last refit and brings the hair left as it is up to date");
    }
}
