using System.Buffers.Binary;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Automatic texture compression's pure parts on synthetic files: TEX headers and sizes, which of a
/// character's textures are considered and how their materials read them, the check that keeps a
/// texture when compression would change what its shaders read, finding and shrinking textures that
/// hold one color, the backup store, and the card's texts.
/// </summary>
internal static class TextureCompressionScenarios
{
    private const uint Bgra = TextureCost.Bgra8;
    private const uint Bc7 = TextureCost.Bc7;
    private const uint Bc5 = TextureCost.Bc5;
    private const uint Bc1 = TextureCost.Bc1;
    private const uint Type2D = 0x00800000;
    private const string Mods = @"C:\Penumbra\";

    public static void Run(string testRoot)
    {
        CheckTextureCost();
        CheckCompressible();
        CheckCapture();
        CheckCutouts();
        CheckOpacity();
        CheckIndexRows();
        CheckNormalsAndColors();
        CheckSingleColor();
        CheckBackupStore(testRoot);
        CheckRestoreSelection();
        CheckBackupExpiry(testRoot);
        CheckTrigger();
        CheckRunSummary();
        CheckViews();
    }

    /// <summary> A TEX file: the header, then as many bytes as its mip chain takes, filled with <paramref name="fill"/>. </summary>
    internal static byte[] Tex(uint format, int width, int height, int mips, byte fill = 0, uint attributes = Type2D, int arraySize = 0)
    {
        var data = TextureCost.Vram(format, width, height, mips) * Math.Max(1, arraySize) * ((attributes & 0x02000000) != 0 ? 6 : 1);
        var bytes = new byte[TextureCost.HeaderSize + data];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = (byte)mips;
        bytes[15] = (byte)arraySize;
        long offset = TextureCost.HeaderSize;
        for (var mip = 0; mip < mips; mip++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + mip * 4), (uint)offset);
            offset += TextureCost.Vram(format, Math.Max(1, width >> mip), Math.Max(1, height >> mip), 1);
        }
        bytes.AsSpan(TextureCost.HeaderSize).Fill(fill);
        return bytes;
    }

    // ---- TEX headers ----------------------------------------------------------------------------------

    private static void CheckTextureCost()
    {
        // Real files measured on 2026-09-29: vanilla textures are exactly their header plus these mip chains.
        Require(TextureCost.Vram(Bc7, 2048, 2048, 12) == 5_592_432 && TextureCost.Vram(Bc1, 256, 512, 10) == 87_400 &&
                TextureCost.Vram(Bc5, 1024, 1024, 11) == 1_398_128 && TextureCost.Vram(Bgra, 1024, 1024, 1) == 4L * 1024 * 1024,
            "texture compression: a texture's size follows its format's bits per pixel and 4 × 4 blocks through the whole mip chain");
        Require(TextureCost.BitsPerPixel(0x1131) == 8 && TextureCost.BitsPerPixel(0x2460) == 64 && TextureCost.BitsPerPixel(Bc1) == 4 &&
                TextureCost.BitsPerPixel(0) is null && TextureCost.IsBlockCompressed(Bc5) && !TextureCost.IsBlockCompressed(Bgra),
            "texture compression: the format code carries its size, and unknown formats have none");
        var file = Tex(Bc7, 2048, 2048, 12);
        var info = TextureCost.Read(file)!.Value;
        Require(info is { Width: 2048, Height: 2048, Mips: 12, Format: Bc7, Is2D: true } && info.Vram == file.Length - TextureCost.HeaderSize,
            "texture compression: the header gives the size, and the file holds exactly the mip chain after it");
        var cube = TextureCost.Read(Tex(Bc1, 64, 64, 1, attributes: 0x02000000 | Type2D))!.Value;
        var array = TextureCost.Read(Tex(Bc1, 64, 64, 1, arraySize: 3, attributes: 0x10000000 | Type2D))!.Value;
        Require(!cube.Is2D && cube.Vram == 6 * 2048 && !array.Is2D && array.Vram == 3 * 2048 && TextureCost.Read(new byte[40]) is null,
            "texture compression: cube maps count six faces and arrays each layer; they are not ordinary textures");
        Require(TextureCost.FormatName(Bgra) == "BGRA32" && TextureCost.FormatName(Bc5) == "BC5" && TextureCost.FormatName(0x1234) == "0x1234",
            "texture compression: formats have short names");

        var decoded = Tex(Bgra, 4, 2, 3);
        for (var i = 0; i < 32; i++)
            decoded[80 + i] = (byte)i;
        var (pixels, width, height) = TextureCost.TopLevelBgra(decoded);
        Require(width == 4 && height == 2 && pixels.Count == 32 && pixels[0] == 0 && pixels[31] == 31 && pixels.Array == decoded,
            "texture compression: a decoded texture's top level is read in place, without its smaller mipmaps");
    }

    private static void CheckCompressible()
    {
        TexInfo Info(uint format, int width, int height, uint attributes = Type2D) => TextureCost.Read(Tex(format, width, height, 1, attributes: attributes))!.Value;
        Require(CompressionCapture.Compressible(Info(Bgra, 2048, 2048)) && CompressionCapture.Compressible(Info(TextureCost.Bgra4, 1024, 512)) &&
                CompressionCapture.Compressible(Info(Bgra, 128, 128)),
            "texture compression: uncompressed color textures of at least 128 × 128 pixels are compressed");
        Require(!CompressionCapture.Compressible(Info(Bc7, 2048, 2048)) && !CompressionCapture.Compressible(Info(Bc1, 1024, 1024)) &&
                !CompressionCapture.Compressible(Info(0x1131, 1024, 1024)),
            "texture compression: compressed and single-channel textures stay as they are");
        Require(!CompressionCapture.Compressible(Info(Bgra, 1022, 1024)) && !CompressionCapture.Compressible(Info(Bgra, 64, 64)) &&
                !CompressionCapture.Compressible(Info(Bgra, 512, 512, 0x02000000 | Type2D)),
            "texture compression: sizes block compression can't tile, tiny textures and cube maps are left alone");
    }

    // ---- The character's textures ---------------------------------------------------------------------

    private static ResourceNode Node(string gamePath, string actualPath, string mod, string name = "", ResourceSourceState state = ResourceSourceState.LoadedMod,
        IReadOnlyList<ResourceNode>? children = null) => new()
    {
        Type = "Resource", Icon = "", Name = name, GamePath = gamePath, ActualPath = actualPath, Children = children ?? [],
        SourceState = state, SourceLabel = mod, SourceModName = state == ResourceSourceState.LoadedMod ? mod : null,
        SourceModDirectory = state == ResourceSourceState.LoadedMod ? mod : null,
        SourceModRootPath = state == ResourceSourceState.LoadedMod ? Mods + mod : null,
        SourceRelativePath = state == ResourceSourceState.LoadedMod ? actualPath[(Mods + mod + @"\").Length..].Replace('\\', '/') : actualPath,
        SlotLabel = "Body", ResourceSection = ResourceSection.Gear, SortOrder = 0,
    };

    private static string ModFile(string mod, string relative) => Mods + mod + @"\" + relative.Replace('/', '\\');

    /// <summary> A material with the given shader package, textures (path and flags), samplers (id and texture index), constants and flags. </summary>
    internal static byte[] Mtrl(string shader, (string Path, ushort Flags)[] textures, (uint Id, int Texture)[] samplers, (uint Id, float[] Values)[] constants,
        uint flags)
    {
        var strings = new MemoryStream();
        var offsets = new List<ushort>();
        foreach (var (path, _) in textures)
        {
            offsets.Add((ushort)strings.Length);
            strings.Write(Encoding.UTF8.GetBytes(path + "\0"));
        }
        var shaderOffset = (ushort)strings.Length;
        strings.Write(Encoding.UTF8.GetBytes(shader + "\0"));
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);
        var values = constants.SelectMany(c => c.Values.SelectMany(BitConverter.GetBytes)).ToArray();
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(0x01030000u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)strings.Length);
        writer.Write(shaderOffset);
        writer.Write((byte)textures.Length);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        for (var i = 0; i < textures.Length; i++)
        {
            writer.Write(offsets[i]);
            writer.Write(textures[i].Flags);
        }
        writer.Write(strings.ToArray());
        writer.Write((ushort)values.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)constants.Length);
        writer.Write((ushort)samplers.Length);
        writer.Write(flags);
        var cursor = 0;
        foreach (var (id, constantValues) in constants)
        {
            writer.Write(id);
            writer.Write((ushort)cursor);
            writer.Write((ushort)(constantValues.Length * 4));
            cursor += constantValues.Length * 4;
        }
        foreach (var (id, texture) in samplers)
        {
            writer.Write(id);
            writer.Write(0x000F8340u);
            writer.Write((byte)texture);
            writer.Write(new byte[3]);
        }
        writer.Write(values);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
        return bytes;
    }

    private static void CheckCapture()
    {
        const string gear = "Gear Mod";
        const string preview = "Skin Seam Preview - A";
        const string top = "chara/equipment/e0001/texture/v01_c0201e0001_top_";
        var cutout = Mtrl("character.shpk", [(top + "norm.tex", 0), (top + "id.tex", 0), (top + "mask.tex", 0)],
            [(TextureUse.NormalSampler, 0), (TextureUse.IndexSampler, 1), (TextureUse.MaskSampler, 2)], [(TextureUse.AlphaThresholdConstant, [0.5f])], 0);
        // A blended material reading the same normal file under another path, whose texture table stores the DX9 name.
        var blended = Mtrl("character.shpk", [("chara/equipment/e0002/texture/v01_c0201e0002_dwn_norm.tex", 0x8000)], [(TextureUse.NormalSampler, 0)],
            [(TextureUse.AlphaThresholdConstant, [0.25f])], TextureUse.TranslucencyFlag);
        var hair = Mtrl("hair.shpk", [("chara/human/c0201/obj/hair/h0001/texture/c0201h0001_hir_norm.tex", 0)], [(TextureUse.NormalSampler, 0)],
            [(TextureUse.AlphaThresholdConstant, [0.5f])], 0);
        var materials = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ModFile(gear, "top.mtrl")] = cutout, [ModFile(gear, "dwn.mtrl")] = blended, [ModFile(gear, "hair.mtrl")] = hair,
        };
        var normal = ModFile(gear, "tex/norm.tex");
        ResourceNode[] roots =
        [
            Node("chara/equipment/e0001/model/c0201e0001_top.mdl", ModFile(gear, "top.mdl"), gear, children:
            [
                Node("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl", ModFile(gear, "top.mtrl"), gear, children:
                [
                    Node(top + "norm.tex", normal, gear, "g_SamplerNormal"),
                    Node(top + "id.tex", ModFile(gear, "tex/id.tex"), gear, "g_SamplerIndex"),
                    Node(top + "mask.tex", ModFile(gear, "tex/mask.tex"), gear, "g_SamplerMask"),
                ]),
                Node("chara/equipment/e0002/material/v0001/mt_c0201e0002_dwn_a.mtrl", ModFile(gear, "dwn.mtrl"), gear, children:
                [
                    // The game requests the DX11 name the material's flag asks for.
                    Node("chara/equipment/e0002/texture/--v01_c0201e0002_dwn_norm.tex", normal, gear, "g_SamplerNormal"),
                ]),
                Node("chara/human/c0201/obj/hair/h0001/material/v0001/mt_c0201h0001_hir_a.mtrl", ModFile(gear, "hair.mtrl"), gear, children:
                [
                    Node("chara/human/c0201/obj/hair/h0001/texture/c0201h0001_hir_norm.tex", ModFile(gear, "tex/hair_norm.tex"), gear, "g_SamplerNormal"),
                ]),
                // A material that can't be read: Penumbra's sampler name gives the role.
                Node("chara/equipment/e0003/material/v0001/mt_c0201e0003_glv_a.mtrl", ModFile(gear, "missing.mtrl"), gear, children:
                [
                    Node("chara/equipment/e0003/texture/v01_c0201e0003_glv_base.tex", ModFile(gear, "tex/glv_base.tex"), gear, "g_SamplerDiffuse"),
                ]),
                Node("chara/equipment/e0004/material/v0001/mt_c0201e0004_sho_a.mtrl", ModFile(preview, "Files/sho.mtrl"), preview, children:
                [
                    Node("chara/equipment/e0004/texture/v01_c0201e0004_sho_base.tex", ModFile(preview, "Files/sho_base.tex"), preview, "g_SamplerDiffuse"),
                ]),
                Node("chara/common/texture/skin_d.tex", "chara/common/texture/skin_d.tex", "Game data", state: ResourceSourceState.GameData),
            ]),
        ];
        var candidates = CompressionCapture.Collect(roots, node => materials.TryGetValue(node.ActualPath, out var bytes) ? SkinMaterial.Read(bytes) : null,
            mod => string.Equals(mod, preview, StringComparison.OrdinalIgnoreCase));
        CompressionCandidate File(string name) => candidates.Single(c => c.FileName == name);

        Require(candidates.Count == 6 && candidates.All(c => c.Source.IsModFile) && !candidates.Any(c => c.FileName == "skin_d.tex"),
            "texture compression: every mod texture the character renders is considered once; game files are not");
        var norm = File("norm.tex");
        Require(norm.GamePaths.Count == 2 && norm.Uses.Count == 2 && norm.Uses.All(u => u.Role == TextureRole.Normal && u.OpacityChannel == 2) &&
                norm.Uses.Count(u => u.CutsOut && u.AlphaThreshold == 0.5f) == 1 && norm.Uses.Count(u => u.Translucent && u.AlphaThreshold == 0.25f) == 1,
            "texture compression: a normal map two materials read knows both game paths, and which one cuts out with blue and which blends it");
        Require(File("id.tex").Uses.Single() is { Role: TextureRole.Index, SamplerId: TextureUse.IndexSampler, OpacityChannel: -1 } &&
                File("mask.tex").Uses.Single().Role == TextureRole.Mask,
            "texture compression: roles come from the material's samplers");
        Require(File("hair_norm.tex").Uses.Single() is { OpacityChannel: 3, CutsOut: true },
            "texture compression: hair normal maps carry opacity in alpha");
        Require(File("glv_base.tex").Uses.Single() is { Role: TextureRole.Base, SamplerId: 0, ShaderPackage: "" },
            "texture compression: when a material can't be read, Penumbra's sampler name still gives the role");
        Require(File("sho_base.tex").InPreview && !File("norm.tex").InPreview,
            "texture compression: files of other Quick Actions' preview mods are marked so they are left alone");
        var loaded = CompressionCapture.ModFiles(roots);
        Require(loaded.Count == 12 && loaded.Contains(ModFile(gear, "top.mdl")) && loaded.Contains(ModFile(gear, "TOP.MTRL")) && loaded.Contains(normal) &&
                !loaded.Contains("chara/common/texture/skin_d.tex"),
            "texture optimization: what the character has loaded from mods is every model, material and texture of the tree, game files left out");
    }

    // ---- The check ------------------------------------------------------------------------------------

    /// <summary> A BGRA image filled with (b, g, r, a). </summary>
    private static byte[] Image(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    private static TextureUse Use(TextureRole role, string shader, float threshold = 0, bool translucent = false)
        => new() { Role = role, ShaderPackage = shader, AlphaThreshold = threshold, Translucent = translucent };

    private static void CheckCutouts()
    {
        const int size = 256;
        var cut = new[] { Use(TextureRole.Normal, "character.shpk", 0.5f) };
        // A flat normal map whose blue channel cuts out the right half.
        var original = Image(size, size, 255, 128, 128, 255);
        for (var y = 0; y < size; y++)
        for (var x = size / 2; x < size; x++)
            original[(y * size + x) * 4] = 0;
        var same = CompressionCheck.Compare(original, original, size, size, cut, false);
        Require(same.Passed && same.CutoutChange == 0 && same.LargeErrorShare == 0 && same.NormalAngleP99 == 0,
            "texture compression check: an unchanged texture passes");

        byte[] Flipped(int count, Func<int, (int X, int Y)> at)
        {
            var copy = (byte[])original.Clone();
            for (var k = 0; k < count; k++)
            {
                var (x, y) = at(k);
                copy[(y * size + x) * 4] = (byte)(original[(y * size + x) * 4] >= 128 ? 120 : 136);
            }
            return copy;
        }
        // 1 % of the drawn pixels spread over the left half: lace breaking up.
        var many = CompressionCheck.Compare(original, Flipped(328, k => (k % 128, k / 128 * 16)), size, size, cut, false);
        Require(!many.Passed && many.CutoutChange > 0.009 && many.Problem.Contains("see-through", StringComparison.Ordinal),
            "texture compression check: a cut-out that loses 1 % of its drawn pixels is kept as it is");
        // A pixel here and there along the whole texture: 0.05 %.
        var few = CompressionCheck.Compare(original, Flipped(16, k => (k * 7 % 128, k * 16)), size, size, cut, false);
        Require(few.Passed && few.CutoutChange > 0 && few.CutoutChange < CompressionTolerances.Default.MaxCutoutChange,
            "texture compression check: a few scattered edge pixels don't keep a texture");

        // In a large texture, damage to one small area stands out in its tile even when the whole barely changes.
        const int large = 512;
        var wide = Image(large, large, 255, 128, 128, 255);
        var local = (byte[])wide.Clone();
        for (var k = 0; k < 120; k++)
            local[((k / 12) * large + k % 12) * 4] = 100;
        var tile = CompressionCheck.Compare(wide, local, large, large, cut, false);
        Require(!tile.Passed && tile.CutoutChange < CompressionTolerances.Default.MaxCutoutChange && tile.CutoutTileChange > CompressionTolerances.Default.MaxCutoutTileChange,
            "texture compression check: holes gathered in one tile keep the texture even when the share overall is tiny");

        // Hair reads opacity from alpha: blue can change freely, alpha can't.
        var hairUse = new[] { Use(TextureRole.Normal, "hair.shpk", 0.5f) };
        var hair = Image(64, 64, 200, 128, 128, 255);
        var blueOnly = Image(64, 64, 20, 128, 128, 255);
        var alphaCut = Image(64, 64, 200, 128, 128, 255);
        for (var i = 3; i < 64 * 16 * 4; i += 4)
            alphaCut[i] = 100;
        Require(CompressionCheck.Compare(hair, blueOnly, 64, 64, hairUse, false).CutoutChange == 0 &&
                !CompressionCheck.Compare(hair, alphaCut, 64, 64, hairUse, false).Passed,
            "texture compression check: hair normal maps are judged on their alpha, gear normal maps on their blue");
    }

    private static void CheckOpacity()
    {
        const int size = 128;
        var blend = new[] { Use(TextureRole.Normal, "character.shpk", 0.5f, translucent: true) };
        // Blue 64 at a threshold of 0.5 draws at half opacity.
        var original = Image(size, size, 64, 128, 128, 255);
        var slight = Image(size, size, 70, 128, 128, 255);
        Require(CompressionCheck.Compare(original, slight, size, size, blend, false) is { Passed: true, OpacityChange: 0 },
            "texture compression check: blended opacity that moves by a few hundredths passes");
        var patchy = (byte[])original.Clone();
        for (var k = 0; k < size * size / 50; k++)
            patchy[k * 50 * 4] = 96;
        var result = CompressionCheck.Compare(original, patchy, size, size, blend, false);
        Require(!result.Passed && result.OpacityChange > CompressionTolerances.Default.MaxOpacityChange && result.Problem.Contains("see-through", StringComparison.Ordinal),
            "texture compression check: blended opacity that jumps by a quarter on 2 % of the pixels keeps the texture");
        var unthresholded = new[] { Use(TextureRole.Normal, "character.shpk", 0, translucent: true) };
        Require(CompressionCheck.Compare(original, Image(size, size, 94, 128, 128, 255), size, size, unthresholded, false).OpacityChange == 0,
            "texture compression check: a blended material without a threshold draws everything, so its blue isn't opacity");
    }

    private static void CheckIndexRows()
    {
        Require(CompressionCheck.Pair(0) == 0 && CompressionCheck.Pair(8) == 0 && CompressionCheck.Pair(9) == 1 && CompressionCheck.Pair(17) == 1 &&
                CompressionCheck.Pair(25) == 1 && CompressionCheck.Pair(26) == 2 && CompressionCheck.Pair(255) == 15,
            "texture compression check: index maps pick colorset row pairs 17 levels apart");
        const int size = 64;
        var index = new[] { Use(TextureRole.Index, "character.shpk") };
        // Pair 0 on the left, pair 2 on the right, a blended border column of pair 1; blue and alpha unused.
        var original = Image(size, size, 77, 128, 0, 200);
        for (var y = 0; y < size; y++)
        for (var x = size / 2; x < size; x++)
            original[(y * size + x) * 4 + 2] = (byte)(x == size / 2 ? 17 : 34);
        var shifted = (byte[])original.Clone();
        for (var y = 0; y < size; y++)
        {
            shifted[(y * size + size / 2) * 4 + 2] = 0;
            shifted[(y * size) * 4] = 0;
            shifted[(y * size) * 4 + 3] = 255;
        }
        var border = CompressionCheck.Compare(original, shifted, size, size, index, true);
        Require(border.Passed && border.RowChange == 0 && border.LargeErrorShare == 0,
            "texture compression check: an index map's border moving by a pixel, and changes to channels BC5 drops, pass");
        var stray = (byte[])original.Clone();
        for (var k = 0; k < 40; k++)
            stray[((k % 8 + 4) * size + k / 8 + 4) * 4 + 2] = 85;
        var strayResult = CompressionCheck.Compare(original, stray, size, size, index, true);
        Require(!strayResult.Passed && strayResult.RowChange > 0 && strayResult.Problem.Contains("dye", StringComparison.Ordinal),
            "texture compression check: row pairs that appear from nowhere keep the index map as it is");
    }

    private static void CheckNormalsAndColors()
    {
        const int size = 64;
        var skin = new[] { Use(TextureRole.Normal, "skin.shpk") };
        var flat = Image(size, size, 255, 128, 128, 255);
        var tilted = (byte[])flat.Clone();
        for (var k = 0; k < size * size / 40; k++)
            tilted[k * 40 * 4 + 2] = 230;
        var normals = CompressionCheck.Compare(flat, tilted, size, size, skin, false);
        Require(!normals.Passed && normals.NormalAngleP99 > CompressionTolerances.Default.MaxNormalAngleP99 && normals.Problem.Contains("light", StringComparison.Ordinal),
            "texture compression check: normals that turn far on more than 1 % of the pixels keep the texture");

        var diffuse = new[] { Use(TextureRole.Base, "character.shpk") };
        var colors = Image(size, size, 40, 80, 120, 255);
        var blotched = (byte[])colors.Clone();
        for (var k = 0; k < size * size / 100; k++)
            blotched[k * 100 * 4 + 2] = 200;
        var shifted = Image(size, size, 70, 110, 150, 255);
        Require(!CompressionCheck.Compare(colors, blotched, size, size, diffuse, false).Passed &&
                CompressionCheck.Compare(colors, shifted, size, size, diffuse, false).Passed,
            "texture compression check: blotches of large errors keep a texture, an even shift under the limit does not");
        var texts = new[]
        {
            CompressionCheck.ProblemOf(new CompressionCheckResult { CutoutChange = 1 }), CompressionCheck.ProblemOf(new CompressionCheckResult { OpacityChange = 1 }),
            CompressionCheck.ProblemOf(new CompressionCheckResult { RowChange = 1 }), CompressionCheck.ProblemOf(new CompressionCheckResult { NormalAngleP99 = 90 }),
            CompressionCheck.ProblemOf(new CompressionCheckResult { LargeErrorShare = 1 }),
        };
        Require(texts.Distinct().Count() == 5 && texts.All(text => text.StartsWith("compressing would", StringComparison.Ordinal) && !text.Contains('…') &&
                                                                 !text.Contains("...", StringComparison.Ordinal)),
            "texture compression check: each reason reads as its own sentence part, without ellipses");
    }

    // ---- Single colors --------------------------------------------------------------------------------

    private static void CheckSingleColor()
    {
        TexInfo Info(uint format, int width, int height, uint attributes = Type2D) => TextureCost.Read(Tex(format, width, height, 1, attributes: attributes))!.Value;
        Require(SingleColor.Shrinkable(Info(Bgra, 4096, 4096)) && SingleColor.Shrinkable(Info(Bc7, 2048, 1024)) && SingleColor.Shrinkable(Info(Bc1, 64, 64)) &&
                SingleColor.Shrinkable(Info(Bc5, 1024, 1024)) && SingleColor.Shrinkable(Info(Bgra, 1022, 1024)),
            "single colors: uncompressed and BC1/3/5/7 textures larger than 32 × 32 can shrink, whatever their size");
        Require(!SingleColor.Shrinkable(Info(Bgra, 32, 32)) && !SingleColor.Shrinkable(Info(Bgra, 16, 64)) && !SingleColor.Shrinkable(Info(TextureCost.Bc4, 1024, 1024)) &&
                !SingleColor.Shrinkable(Info(0x1131, 1024, 1024)) && !SingleColor.Shrinkable(Info(Bgra, 512, 512, 0x02000000 | Type2D)),
            "single colors: textures no larger than the shrunk size, single-channel formats and cube maps stay as they are");

        bool May(byte[] tex) => SingleColor.MayHoldOneColor(new MemoryStream(tex, false), TextureCost.Read(tex)!.Value);
        // Every block of a BC7 texture the same: one color. One block different in the top level: not; in a smaller mipmap only: still, as only the top level is read.
        var blocks = Tex(Bc7, 256, 256, 9, 7);
        var patterned = (byte[])blocks.Clone();
        patterned[80 + 16 * 300 + 5] = 8;
        var lowerMip = (byte[])blocks.Clone();
        lowerMip[80 + 256 * 256 + 3] = 8;
        Require(May(blocks) && !May(patterned) && May(lowerMip),
            "single colors: a compressed texture may hold one color when its top level repeats one block exactly");
        // A large texture is read in chunks; a difference after the first one still counts.
        var late = Tex(Bc7, 2048, 2048, 1, 7);
        late[^1] = 8;
        Require(!May(late) && May(Tex(Bc7, 2048, 2048, 1, 7)),
            "single colors: the whole top level is compared, past the first chunk read");
        // Bogus mip offsets: the top level is read right after the header, as Penumbra does; a file too short for it doesn't count.
        var bogus = Tex(Bc1, 128, 128, 1, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bogus.AsSpan(28), 12);
        Require(May(bogus) && !May(bogus[..^8]),
            "single colors: the top level sits right after the header when the header points elsewhere, and must be all there");

        var noisy = Tex(Bgra, 64, 64, 1, 128);
        for (var i = 80; i < noisy.Length; i += 7)
            noisy[i] = (byte)(128 + i % 5);
        var spotted = (byte[])noisy.Clone();
        spotted[80 + 4 * 1000 + 2] = 140;
        Require(May(noisy) && !May(spotted),
            "single colors: uncompressed pixels may vary by a few levels, like a flat normal map with a level of noise, but no more");

        var flat = Image(64, 64, 255, 127, 127, 255);
        for (var i = 0; i < flat.Length; i += 4 * 3)
            flat[i + 1] = 128;
        Require(SingleColor.ColorOf(flat, 64, 64, false) == (255, 127, 127, 255) && SingleColor.ColorOf(Image(64, 64, 9, 8, 7, 6), 64, 64, false) == (9, 8, 7, 6),
            "single colors: the color is each channel's rounded average");
        var bumpy = (byte[])flat.Clone();
        bumpy[4 * 100 + 1] = 140;
        var index = Image(64, 64, 0, 255, 34, 255);
        for (var i = 0; i < index.Length; i += 8)
        {
            index[i] = 200;
            index[i + 3] = 0;
        }
        Require(SingleColor.ColorOf(bumpy, 64, 64, false) is null && SingleColor.ColorOf(index, 64, 64, false) is null &&
                SingleColor.ColorOf(index, 64, 64, true) is (_, 255, 34, _),
            "single colors: one pixel beyond the tolerance makes it more than one color, unless it is in blue or alpha, which BC5 drops");

        var small = SingleColor.Tex((1, 2, 3, 4));
        var header = InstantEdit.Services.TextureFiles.ReadTex(small);
        var (pixels, width, height) = TextureCost.TopLevelBgra(small);
        Require(header is { Format: Bgra, Width: SingleColor.Size, Height: SingleColor.Size, Mips: 1 } && width == 32 && height == 32 &&
                SingleColor.ColorOf(pixels, width, height, false) == (1, 2, 3, 4) && small.Length == 80 + 32 * 32 * 4,
            "single colors: the shrunk texture is a valid 32 × 32 BGRA32 TEX of the color, for Penumbra to encode");

        var quad = new byte[2 * 2 * 4];
        for (var p = 0; p < 4; p++)
            quad[p * 4] = (byte)(p + 1);
        var stretched = SingleColor.Stretch(quad, 2, 2, 4, 6);
        Require(stretched.Length == 4 * 6 * 4 && stretched[0] == 1 && stretched[(0 * 4 + 3) * 4] == 2 && stretched[(5 * 4 + 0) * 4] == 3 && stretched[(5 * 4 + 3) * 4] == 4 &&
                stretched[(2 * 4 + 1) * 4] == 1 && stretched[(3 * 4 + 2) * 4] == 4,
            "single colors: the decoded small texture is stretched over the original's size, each pixel taking the nearest");

        // The check compares the original with the shrunk color at every pixel: noise within the tolerance passes, a color that drifted doesn't.
        var diffuse = new[] { Use(TextureRole.Base, "character.shpk") };
        var drifted = Image(64, 64, 255, 127, 117, 255);
        Require(CompressionCheck.Compare(flat, Image(64, 64, 255, 127, 127, 255), 64, 64, diffuse, false, SingleColor.CheckTolerances).Passed &&
                !CompressionCheck.Compare(flat, drifted, 64, 64, diffuse, false, SingleColor.CheckTolerances).Passed &&
                CompressionCheck.Compare(flat, drifted, 64, 64, diffuse, false).Passed,
            "single colors: a shrunk texture may only move by the tolerance and the encoder's rounding, less than compression may");
        var cut = new[] { Use(TextureRole.Normal, "character.shpk", 0.5f) };
        var edge = Image(64, 64, 126, 128, 128, 255);
        for (var i = 0; i < edge.Length; i += 8)
            edge[i] = 129;
        Require(SingleColor.ColorOf(edge, 64, 64, false) is (128, _, _, _) &&
                !CompressionCheck.Compare(edge, Image(64, 64, 128, 128, 128, 255), 64, 64, cut, false, SingleColor.CheckTolerances).Passed,
            "single colors: a near-uniform opacity channel straddling the cut-out threshold keeps the texture as it is");
    }

    // ---- Backups --------------------------------------------------------------------------------------

    private static void CheckBackupStore(string testRoot)
    {
        var root = Path.Combine(testRoot, "TextureBackups");
        var config = Path.Combine(root, "config");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(cache);
        var first = Tex(Bgra, 128, 128, 1, 7);
        var second = Tex(Bgra, 128, 128, 1, 9);
        string shaFirst = TextureBackupStore.Hash(first), shaSecond = TextureBackupStore.Hash(second);
        var backup = TextureBackupStore.StoreBackup(cache, first, shaFirst);
        Require(backup == Path.Combine(cache, TextureBackupStore.FolderName, shaFirst + ".tex") && File.ReadAllBytes(backup).SequenceEqual(first) &&
                TextureBackupStore.StoreBackup(cache, first, shaFirst) == backup,
            "texture backups: an original is copied into the cache folder under its hash, once");
        var invalid = false;
        try { TextureBackupStore.StoreBackup(cache, first, "../x"); }
        catch (ArgumentException) { invalid = true; }
        Require(invalid, "texture backups: only hashes name backup files");

        CompressedTexture Entry(string file, string sha, string path, long length) => new()
        {
            File = file, ModDirectory = "Gear Mod", ModName = "Gear Mod", RelativePath = Path.GetFileName(file), Backup = path, OriginalSha256 = sha,
            OriginalLength = length, OriginalFormat = Bgra, CompressedSha256 = "C0", CompressedLength = length / 4, CompressedFormat = Bc7, Width = 128, Height = 128,
            Compressed = DateTimeOffset.UtcNow,
        };
        var store = new TextureBackupStore(config);
        store.Load();
        var a = Entry(ModFile("Gear Mod", "a.tex"), shaFirst, backup, first.Length);
        var b = Entry(ModFile("Gear Mod", "b.tex"), shaFirst, backup, first.Length);
        store.Record(a);
        store.Record(b);
        Require(store.Compressed.Count == 2 && store.BackupTotals() == (1, first.Length),
            "texture backups: two files with the same original share one copy");
        store.Forget([a]);
        var sharedKept = File.Exists(backup);
        store.Forget([b]);
        Require(sharedKept && !File.Exists(backup) && store.Compressed.Count == 0,
            "texture backups: a shared copy is deleted when the last file that uses it is forgotten");

        var older = Entry(ModFile("Gear Mod", "a.tex"), shaFirst, TextureBackupStore.StoreBackup(cache, first, shaFirst), first.Length);
        store.Record(older);
        var newerBackup = TextureBackupStore.StoreBackup(cache, second, shaSecond);
        store.RecordKept(new KeptTexture { File = ModFile("Gear Mod", "a.tex"), Sha256 = shaSecond, Reason = "r", CheckVersion = 1 });
        Require(store.KeptFor(ModFile("Gear Mod", "A.TEX"), shaSecond.ToLowerInvariant(), 1) is not null && store.KeptFor(ModFile("Gear Mod", "a.tex"), shaSecond, 2) is null &&
                store.KeptFor(ModFile("Gear Mod", "a.tex"), shaFirst, 1) is null,
            "texture backups: a kept texture is remembered by file, content and check version");
        store.Record(Entry(ModFile("Gear Mod", "a.tex"), shaSecond, newerBackup, second.Length));
        Require(store.Compressed.Single().OriginalSha256 == shaSecond && !File.Exists(older.Backup) && File.Exists(newerBackup) && store.Kept.Count == 0,
            "texture backups: compressing a file again replaces its entry and its verdict, and the old original goes");

        var refused = false;
        try { store.Record(Entry(ModFile("Gear Mod", "a.tex"), "C0", older.Backup, first.Length)); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused && store.Compressed.Single().OriginalSha256 == shaSecond && File.Exists(newerBackup),
            "texture backups: a file still holding what was written over it isn't recorded again, so its original's copy stays");

        var outside = Path.Combine(root, "outside.tex");
        File.WriteAllBytes(outside, first);
        store.Record(Entry(ModFile("Gear Mod", "c.tex"), shaFirst, outside, first.Length));
        var reloaded = new TextureBackupStore(config);
        reloaded.Load();
        Require(reloaded.LoadError.Length == 0 && reloaded.Compressed.Count == 2 && reloaded.CompressedFor(ModFile("Gear Mod", "a.tex"))?.Backup == newerBackup,
            "texture backups: the list survives a reload");
        reloaded.Forget(reloaded.Compressed.ToList());
        Require(File.Exists(outside) && !File.Exists(newerBackup) && reloaded.Compressed.Count == 0,
            "texture backups: only copies inside the texture-backups folder are ever deleted");

        reloaded.MarkRestored([ModFile("Gear Mod", "a.tex"), ModFile("Gear Mod", "hair.mdl")]);
        var marked = new TextureBackupStore(config);
        marked.Load();
        Require(marked.IsRestored(ModFile("Gear Mod", "A.TEX")) && marked.IsRestored(ModFile("Gear Mod", "hair.mdl")) && !marked.IsRestored(ModFile("Gear Mod", "b.tex")),
            "texture backups: files whose originals were restored are remembered across a reload");
        Require(marked.ClearRestored([ModFile("Gear Mod", "a.tex"), ModFile("Gear Mod", "b.tex")]) == 1 && !marked.IsRestored(ModFile("Gear Mod", "a.tex")) &&
                marked.IsRestored(ModFile("Gear Mod", "hair.mdl")),
            "texture backups: Optimize now lets only the character's restored files be optimized again");

        var journal = Path.Combine(config, TextureBackupStore.JournalName);
        File.WriteAllText(journal, "{ not json");
        var broken = new TextureBackupStore(config);
        broken.Load();
        Require(broken.LoadError.Length > 0 && broken.Compressed.Count == 0 && !File.Exists(journal) &&
                Directory.EnumerateFiles(config, TextureBackupStore.JournalName + ".unreadable-*").Any(),
            "texture backups: an unreadable list is set aside, not overwritten, and the error is reported");
    }

    private static CompressedTexture Compressed(string file, string backup, DateTimeOffset when, string sha = "A0") => new()
    {
        File = file, ModDirectory = "Gear Mod", ModName = "Gear Mod", RelativePath = Path.GetFileName(file), Backup = backup, OriginalSha256 = sha,
        OriginalLength = 4, CompressedSha256 = "C0", CompressedLength = 1, Compressed = when,
    };

    private static RefitGroup Refit(DateTimeOffset when, params (string File, string Backup)[] files) => new()
    {
        ModDirectory = "Hair Mod", ModName = "Hair Mod", Refit = when,
        Files = files.Select(file => new RefitFile
        {
            File = file.File, RelativePath = Path.GetFileName(file.File), Backup = file.Backup, OriginalSha256 = "A1", OriginalLength = 4, NewSha256 = "B1", NewLength = 2,
        }).ToList(),
    };

    private static void CheckRestoreSelection()
    {
        var now = DateTimeOffset.UtcNow;
        var worn = Compressed(ModFile("Gear Mod", "top_norm.tex"), "b1", now);
        var other = Compressed(ModFile("Gear Mod", "dwn_norm.tex"), "b2", now);
        var cut = Compressed(ModFile("Hair Mod", "hair_opt2_norm.tex"), "b3", now);
        var hair = Refit(now, (ModFile("Hair Mod", "hair.mdl"), "m"), (ModFile("Hair Mod", "hair_norm.tex"), "t"));
        // A second material of the same model, refit after the first: restoring one must restore both.
        var sharing = Refit(now, (ModFile("Hair Mod", "hair.mdl"), "m2"), (ModFile("Hair Mod", "hair_opt2_norm.tex"), "t2"));
        var unrelated = Refit(now, (ModFile("Other Hair", "hair.mdl"), "m3"));
        var (entries, groups) = TextureBackupStore.Covering([worn, other, cut], [hair, sharing, unrelated],
            [ModFile("Gear Mod", "TOP_NORM.TEX"), ModFile("Hair Mod", "hair_norm.tex"), ModFile("Gear Mod", "never_optimized.tex")]);
        Require(entries.Count == 2 && entries.Contains(worn) && entries.Contains(cut) && !entries.Contains(other),
            "texture restore: only the optimized textures the character has loaded go back, with those of the hair restored with them");
        Require(groups.Count == 2 && ReferenceEquals(groups[0], hair) && ReferenceEquals(groups[1], sharing),
            "texture restore: refit hair goes back whole, with every refit sharing a file with it, oldest first");
        var (none, noGroups) = TextureBackupStore.Covering([worn], [hair], [ModFile("Gear Mod", "sho.tex")]);
        Require(none.Count == 0 && noGroups.Count == 0, "texture restore: nothing goes back for files the character hasn't loaded");
    }

    private static void CheckBackupExpiry(string testRoot)
    {
        var root = Path.Combine(testRoot, "TextureBackupExpiry");
        var config = Path.Combine(root, "config");
        var cache = Path.Combine(root, "cache");
        var folder = Path.Combine(cache, TextureBackupStore.FolderName);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(folder);
        string Copy(char fill, DateTime written)
        {
            var path = Path.Combine(folder, new string(fill, 64) + ".tex");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(path, written);
            return path;
        }
        var now = DateTimeOffset.UtcNow;
        var old = now - TextureBackupStore.Retention - TimeSpan.FromHours(1);
        var recent = now - TimeSpan.FromDays(1);
        string oldCopy = Copy('A', old.UtcDateTime), recentCopy = Copy('B', recent.UtcDateTime), oldHair = Copy('C', old.UtcDateTime),
            oldStray = Copy('D', old.UtcDateTime), recentStray = Copy('E', recent.UtcDateTime);
        var foreign = Path.Combine(folder, "notes.txt");
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(foreign, old.UtcDateTime);
        // A copy an old and a recent entry share stays for the recent one.
        var shared = Copy('F', old.UtcDateTime);

        var store = new TextureBackupStore(config);
        store.Load();
        store.Record(Compressed(ModFile("Gear Mod", "old.tex"), oldCopy, old));
        store.Record(Compressed(ModFile("Gear Mod", "recent.tex"), recentCopy, recent));
        store.Record(Compressed(ModFile("Gear Mod", "old_shared.tex"), shared, old));
        store.Record(Compressed(ModFile("Gear Mod", "recent_shared.tex"), shared, recent));
        store.RecordRefit(Refit(old, (ModFile("Hair Mod", "hair.mdl"), oldHair)));
        var (forgotten, strays) = store.Expire(cache, now - TextureBackupStore.Retention);
        Require(forgotten == 3 && strays == 1 && store.Refits.Count == 0 &&
                store.Compressed.Select(entry => Path.GetFileName(entry.File)).Order(StringComparer.Ordinal).SequenceEqual(["recent.tex", "recent_shared.tex"]),
            "texture backups: the automatic cleanup forgets what was optimized more than a week ago");
        Require(!File.Exists(oldCopy) && !File.Exists(oldHair) && !File.Exists(oldStray) && File.Exists(recentCopy) && File.Exists(shared) &&
                File.Exists(recentStray) && File.Exists(foreign),
            "texture backups: the cleanup deletes old originals and old copies no entry names, and nothing else");
        Require(store.Expire(cache, now - TextureBackupStore.Retention) == (0, 0),
            "texture backups: a second cleanup finds nothing left to do");
    }

    // ---- Runs -----------------------------------------------------------------------------------------

    private static void CheckTrigger()
    {
        // Penumbra reports the textures a material loads without their character, so a Glamourer
        // outfit change only arrives as the character's model and material loads.
        Require(CompressionTrigger.LoadsTextures("chara/equipment/e6001/material/v0001/mt_c0201e6001_top_a.mtrl") &&
                CompressionTrigger.LoadsTextures("chara/equipment/e6001/model/c0201e6001_top.mdl") &&
                CompressionTrigger.LoadsTextures("chara/common/texture/decal_face/_decal_5.tex") &&
                CompressionTrigger.LoadsTextures("CHARA/ACCESSORY/A0004/MATERIAL/V0001/MT_C0201A0004_WRS_A.MTRL"),
            "texture compression: the character's models and materials schedule a run, not only its textures");
        Require(new[]
            {
                "chara/human/c0201/animation/a0001/bt_common/emote/sit.pap", "chara/action/emote/sit.tmb", "vfx/common/eff/cmat_hand.avfx",
                "vfx/common/texture/cmat_hand.atex", "sound/foot/foot.scd", "chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb",
            }.All(path => !CompressionTrigger.LoadsTextures(path)),
            "texture compression: animations, effects, sounds and skeletons don't schedule a run");
    }

    private static void CheckRunSummary()
    {
        var compressed = new CompressionRun
        {
            Finished = DateTimeOffset.Now.AddMinutes(-1), Compressed = 2, BytesBefore = 8L << 20, BytesAfter = 2L << 20, Kept = ["a.tex: r"], Warnings = ["w"],
        };
        var idle = new CompressionRun { Finished = DateTimeOffset.Now, Kept = ["b.tex: r"] };
        var shown = CompressionRun.After(compressed, idle);
        Require(shown.Compressed == 2 && shown.Finished == compressed.Finished && shown.Warnings.SequenceEqual(["w"]) && shown.Kept.SequenceEqual(["b.tex: r"]),
            "texture compression: a run with nothing to report keeps the last compression and its problems, and updates the kept textures");
        Require(CompressionRun.After(compressed, new CompressionRun()).Kept.Count == 0,
            "texture compression: kept textures the character no longer wears leave the card");
        var failed = new CompressionRun { Failed = ["c.tex: e"] };
        var next = new CompressionRun { Compressed = 1 };
        Require(CompressionRun.After(null, idle) == idle && CompressionRun.After(compressed, failed) == failed &&
                CompressionRun.After(compressed, next) == next,
            "texture compression: the first run, and a run that compressed or ran into problems, replace what the card shows");
    }

    // ---- The card -------------------------------------------------------------------------------------

    private static void CheckViews()
    {
        Require(TextureCompressionViews.Bytes(0) == "0 B" && TextureCompressionViews.Bytes(1536) == "1.50 KiB" &&
                TextureCompressionViews.Bytes(612L * 1024 * 1024 + 358L * 1024) == "612.35 MiB" && TextureCompressionViews.Bytes(3L << 30) == "3.00 GiB",
            "texture compression card: sizes in units of 1024");
        Require(TextureCompressionViews.Textures(1) == "1 texture" && TextureCompressionViews.Textures(1234) == "1,234 textures",
            "texture compression card: counts read naturally");
        CompressedTexture Entry(long before, long after, bool shrunk = false) => new()
        {
            File = @"C:\x.tex", ModDirectory = "M", RelativePath = "x.tex", Backup = @"C:\b.tex", OriginalSha256 = "A", OriginalLength = before,
            CompressedSha256 = "B", CompressedLength = after, Shrunk = shrunk,
        };
        var run = new CompressionRun { Finished = DateTimeOffset.Now, Compressed = 2, BytesBefore = 8L << 20, BytesAfter = 2L << 20, Kept = ["x.tex: r"] };
        var texts = new[]
        {
            TextureCompressionViews.Status(CompressionPhase.Off, ""), TextureCompressionViews.Status(CompressionPhase.Watching, ""),
            TextureCompressionViews.Status(CompressionPhase.Waiting, ""), TextureCompressionViews.Status(CompressionPhase.Checking, ""),
            TextureCompressionViews.Totals([], []), TextureCompressionViews.Totals([Entry(4L << 20, 1L << 20), Entry(4L << 20, 1L << 20)], []),
            TextureCompressionViews.Backups((2, 8L << 20), true), TextureCompressionViews.LastRun(run), TextureCompressionViews.Kept(run),
            TextureCompressionViews.Restored(new CompressionRestore(3, ["M: y.tex"], ["M: z.tex: gone"], [])),
            TextureCompressionViews.Totals([Entry(4L << 20, 1L << 20), Entry(5L << 20, 1L << 20, true)], []),
            TextureCompressionViews.LastRun(run with { Shrunk = 1 }),
            TextureCompressionViews.Backups((2, 8L << 20), false), TextureCompressionViews.LeftRestored(run with { Restored = ["a.tex", "b.tex"] }),
            TextureCompressionViews.Restored(new CompressionRestore(0, [], [], [])),
        };
        Require(TextureCompressionViews.Status(CompressionPhase.Compressing, "Optimizing 1 of 2: a.tex") == "Optimizing 1 of 2: a.tex" &&
                texts[5] == "2 textures optimized so far, 6.00 MiB smaller in total." && texts[4] == "Nothing is optimized yet." &&
                texts[8] == "1 texture you wear stays as it is, because compressing would visibly change it." &&
                texts[9].StartsWith("Restored 3 textures. 1 file changed since", StringComparison.Ordinal) &&
                texts[14] == "Nothing to restore on your character.",
            "texture optimization card: status, totals, kept textures and restoring read as sentences");
        Require(texts[6] == "Backups: 8.00 MiB, kept for 7 days." && texts[12].Contains("automatic cache cleanup is off", StringComparison.Ordinal) &&
                texts[13] == "2 restored files are skipped. Hover for which." &&
                TextureCompressionViews.LeftRestored(run) == string.Empty,
            "texture optimization card: how long originals stay, and the worn files left as restored");
        Require(texts[10] == "2 textures optimized (1 of one color, shrunk to 32 × 32) so far, 7.00 MiB smaller in total." &&
                texts[11].StartsWith("Last run: 2 textures optimized (1 of one color, shrunk to 32 × 32), 6.00 MiB smaller, ", StringComparison.Ordinal) &&
                !texts[7].Contains("one color", StringComparison.Ordinal),
            "texture optimization card: textures shrunk for holding one color are counted in the totals and the last run");
        Require(texts.All(text => !text.Contains('…') && !text.Contains("...", StringComparison.Ordinal)),
            "texture compression card: no ellipses, which the game font draws as dashes");
        var temp = Path.GetTempPath();
        Require(TextureCompressionViews.InTempFolder(Path.Combine(temp, "XIV Instant Edit"), temp) &&
                !TextureCompressionViews.InTempFolder(Path.GetPathRoot(temp) + "Games" + Path.DirectorySeparatorChar + "Cache", temp),
            "texture compression card: a cache folder inside the temp folder is recognised");
    }
}
