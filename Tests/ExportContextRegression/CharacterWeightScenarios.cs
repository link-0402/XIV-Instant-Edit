using System.Buffers.Binary;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.CharacterWeight;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The character weight check's pure parts on synthetic files: texture memory from TEX headers,
/// counting a character's files the way sync plugins do, the default shrink plan and its totals,
/// halving, the text the dialog shows, and the preview records and store the neck seam now shares.
/// </summary>
internal static class CharacterWeightScenarios
{
    private const uint Bgra = TextureCost.Bgra8;
    private const uint Bc7 = TextureCost.Bc7;
    private const uint Bc5 = TextureCost.Bc5;
    private const uint Bc1 = TextureCost.Bc1;
    private const uint Type2D = 0x00800000;

    public static void Run(string testRoot)
    {
        CheckTextureCost();
        var report = CheckCapture();
        CheckPlan(report);
        CheckHalving();
        CheckViews();
        CheckPreviewRecords(testRoot);
    }

    /// <summary> A TEX file: the header, then as many bytes as its mip chain takes, filled with <paramref name="fill"/>. </summary>
    private static byte[] Tex(uint format, int width, int height, int mips, byte fill = 0, uint attributes = Type2D, int arraySize = 0)
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

    // ---- Texture memory -----------------------------------------------------------------------------

    private static void CheckTextureCost()
    {
        // Real files measured on 2026-09-29: vanilla textures are exactly their header plus these mip chains.
        Require(TextureCost.Vram(Bc7, 2048, 2048, 12) == 5_592_432 && TextureCost.Vram(Bc1, 256, 512, 10) == 87_400 &&
                TextureCost.Vram(Bc5, 1024, 1024, 11) == 1_398_128 && TextureCost.Vram(Bgra, 1024, 1024, 1) == 4L * 1024 * 1024,
            "character weight: texture memory follows the format's bits per pixel and 4 × 4 blocks through the whole mip chain");
        Require(TextureCost.BitsPerPixel(0x1131) == 8 && TextureCost.BitsPerPixel(0x2460) == 64 && TextureCost.BitsPerPixel(Bc1) == 4 &&
                TextureCost.BitsPerPixel(0) is null && TextureCost.IsBlockCompressed(Bc5) && !TextureCost.IsBlockCompressed(Bgra),
            "character weight: the format code carries its size, and unknown formats have none");

        var file = Tex(Bc7, 2048, 2048, 12);
        var info = TextureCost.Read(file)!.Value;
        Require(info is { Width: 2048, Height: 2048, Mips: 12, Format: Bc7, Is2D: true } && info.Vram == file.Length - TextureCost.HeaderSize,
            "character weight: the header gives the size, and the file holds exactly the mip chain after it");
        var cube = TextureCost.Read(Tex(Bc1, 64, 64, 1, attributes: 0x02000000 | Type2D))!.Value;
        var array = TextureCost.Read(Tex(Bc1, 64, 64, 1, arraySize: 3, attributes: 0x10000000 | Type2D))!.Value;
        Require(!cube.Is2D && cube.Vram == 6 * 2048 && !array.Is2D && array.Vram == 3 * 2048 && TextureCost.Read(new byte[40]) is null,
            "character weight: cube maps count six faces and arrays each layer; they are not treated as ordinary textures");
        Require(TextureCost.FormatName(Bgra) == "BGRA32" && TextureCost.FormatName(Bc5) == "BC5" && TextureCost.FormatName(0x1234) == "0x1234",
            "character weight: formats have short names");
    }

    // ---- Measuring a character -----------------------------------------------------------------------

    private static ResourceNode Node(string gamePath, string actualPath, string mod, string name = "", string slot = "Body",
        ResourceSourceState state = ResourceSourceState.LoadedMod, IReadOnlyList<ResourceNode>? children = null) => new()
    {
        Type = "Resource", Icon = "", Name = name, GamePath = gamePath, ActualPath = actualPath, Children = children ?? [],
        SourceState = state, SourceLabel = mod, SourceModName = state == ResourceSourceState.LoadedMod ? mod : null,
        SourceModDirectory = state == ResourceSourceState.LoadedMod ? mod : null,
        SourceModRootPath = state == ResourceSourceState.LoadedMod ? @"C:\Penumbra\" + mod : null,
        SourceRelativePath = state == ResourceSourceState.LoadedMod ? actualPath[(@"C:\Penumbra\" + mod + @"\").Length..].Replace('\\', '/') : actualPath,
        SlotLabel = slot, ResourceSection = ResourceSection.Gear, SortOrder = 0,
    };

    private static string ModFile(string mod, string relative) => @"C:\Penumbra\" + mod + @"\" + relative.Replace('/', '\\');

    /// <summary> A model whose most detailed level has meshes with these index counts. </summary>
    private static byte[] Model(params uint[] indexCounts)
    {
        const int header = 68, strings = 8, meshHeader = 56, lods = 3 * 60, mesh = 36;
        var bytes = new byte[header + strings + meshHeader + lods + indexCounts.Length * mesh];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x01000006);
        bytes[64] = 1;
        var at = header + strings;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 4), (ushort)indexCounts.Length);
        bytes[at + 22] = 1;
        var lod = at + meshHeader;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(lod + 2), (ushort)indexCounts.Length);
        var table = lod + lods;
        for (var i = 0; i < indexCounts.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(table + i * mesh), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + i * mesh + 4), indexCounts[i]);
        }
        return bytes;
    }

    private const string PreviewFolder = "Character Weight Preview - A";

    private static CharacterWeightReport CheckCapture()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ModFile("Body Mod", "tex/body_norm.tex")] = Tex(Bgra, 64, 64, 1, 1),
            [ModFile("Body Mod", "tex/body_id.tex")] = Tex(Bgra, 64, 64, 1, 2),
            [ModFile("Body Mod", "tex/body_mask.tex")] = Tex(Bc7, 128, 128, 8, 3),
            // A copy of the body mask in another mod: sync plugins count identical content once.
            [ModFile("Legs Mod", "tex/copy_mask.tex")] = Tex(Bc7, 128, 128, 8, 3),
            ["chara/human/c0201/obj/face/f0001/texture/c0201f0001_fac_id.tex"] = Tex(Bc5, 256, 256, 9, 4),
            [ModFile(PreviewFolder, "Files/chara/preview_d.tex")] = Tex(Bc7, 64, 64, 7, 5),
            [ModFile("Body Mod", "body.mdl")] = Model(30, 60, 90),
            [ModFile("Legs Mod", "legs.mdl")] = Model(120),
            [ModFile("Hands Mod", "hands.mdl")] = Model(30, 60, 90),
            ["chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl"] = Model(300),
        };
        ResourceNode Material(string mod, string name, params ResourceNode[] textures)
            => Node("chara/material/" + name, ModFile(mod, name), mod, children: textures);
        var normal = ModFile("Body Mod", "tex/body_norm.tex");
        ResourceNode[] roots =
        [
            Node("chara/equipment/e0001/model/c0201e0001_top.mdl", ModFile("Body Mod", "body.mdl"), "Body Mod", slot: "Body", children:
            [
                Material("Body Mod", "mt_top_a.mtrl",
                    Node("chara/equipment/e0001/texture/v01_c0201e0001_top_norm.tex", normal, "Body Mod", "g_SamplerNormal"),
                    Node("chara/equipment/e0001/texture/v01_c0201e0001_top_id.tex", ModFile("Body Mod", "tex/body_id.tex"), "Body Mod", "g_SamplerIndex"),
                    Node("chara/equipment/e0001/texture/v01_c0201e0001_top_mask.tex", ModFile("Body Mod", "tex/body_mask.tex"), "Body Mod", "g_SamplerMask")),
            ]),
            Node("chara/equipment/e0002/model/c0201e0002_dwn.mdl", ModFile("Legs Mod", "legs.mdl"), "Legs Mod", slot: "Legs", children:
            [
                Material("Legs Mod", "mt_dwn_a.mtrl",
                    // The body's normal map again, requested under the legs' path: one file, two game paths.
                    Node("chara/equipment/e0002/texture/v01_c0201e0002_dwn_norm.tex", normal, "Body Mod", "g_SamplerNormal", slot: "Legs"),
                    Node("chara/equipment/e0002/texture/v01_c0201e0002_dwn_mask.tex", ModFile("Legs Mod", "tex/copy_mask.tex"), "Legs Mod", "g_SamplerMask"),
                    Node("chara/equipment/e0002/texture/v01_c0201e0002_dwn_d.tex", ModFile(PreviewFolder, "Files/chara/preview_d.tex"), PreviewFolder, "g_SamplerDiffuse")),
            ]),
            // The same legs model in a second slot counts once.
            Node("chara/equipment/e0002/model/c0201e0002_dwn.mdl", ModFile("Legs Mod", "legs.mdl"), "Legs Mod", slot: "Feet"),
            Node("chara/equipment/e0003/model/c0201e0003_glv.mdl", ModFile("Hands Mod", "hands.mdl"), "Hands Mod", slot: "Hands"),
            Node("chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl", "chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl", "Game data",
                slot: "Face", state: ResourceSourceState.GameData, children:
                [
                    Node("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl", "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl",
                        "Game data", state: ResourceSourceState.GameData, children:
                        [
                            Node("chara/human/c0201/obj/face/f0001/texture/c0201f0001_fac_id.tex", "chara/human/c0201/obj/face/f0001/texture/c0201f0001_fac_id.tex",
                                "Game data", "g_SamplerIndex", "Face", ResourceSourceState.GameData),
                        ]),
                ]),
        ];

        WeightFileSample? Sample(string path)
            => files.TryGetValue(path, out var bytes) ? new WeightFileSample(bytes[..Math.Min(bytes.Length, 80)], bytes.Length, PreviewSource.Hash(bytes)) : null;
        var report = CharacterWeightCapture.Capture(roots, Sample, path => files.GetValueOrDefault(path),
            mod => string.Equals(mod, PreviewFolder, StringComparison.OrdinalIgnoreCase));
        WeightTexture Texture(string name) => report.Textures.Single(t => t.FileName == name);

        Require(report.Textures.Count == 6 && Texture("body_norm.tex").GamePaths.Count == 2 &&
                Texture("body_norm.tex").Slots.SequenceEqual(["Body", "Legs"]) && Texture("body_norm.tex").Uses.All(u => u.StartsWith("g_SamplerNormal in", StringComparison.Ordinal)),
            "character weight: a texture file used by two materials is one row that knows both game paths and slots");
        Require(Texture("body_id.tex").IndexOnly && !Texture("body_norm.tex").IndexOnly && Texture("body_norm.tex").Role == TextureRole.Normal &&
                Texture("c0201f0001_fac_id.tex").IndexOnly,
            "character weight: index maps are recognised by the sampler Penumbra names them after");
        Require(Texture("preview_d.tex").InPreview && Texture("c0201f0001_fac_id.tex").Source.State == ResourceSourceState.GameData &&
                !Texture("c0201f0001_fac_id.tex").CountsForSync && Texture("body_mask.tex").CountsForSync,
            "character weight: files in preview mods are marked, and game files don't count for sync plugins");
        Require(Texture("body_norm.tex").Uncompressed && Texture("body_norm.tex").NoMips && !Texture("body_mask.tex").Uncompressed &&
                !Texture("body_mask.tex").NoMips && !Texture("body_mask.tex").Oversized,
            "character weight: uncompressed textures and textures without mipmaps are flagged");

        long Length(string name) => Texture(name).FileLength;
        Require(report.SyncVram == Length("body_norm.tex") + Length("body_id.tex") + Length("body_mask.tex") + Length("preview_d.tex"),
            "character weight: sync plugins' texture memory is the file size of each distinct modded texture, game files left out");
        Require(report.AllVram == report.Textures.Sum(t => t.Vram) && report.GameVram == Texture("c0201f0001_fac_id.tex").Vram &&
                report.Textures.Zip(report.Textures.Skip(1)).All(pair => pair.First.Vram >= pair.Second.Vram),
            "character weight: every rendered texture's memory is totalled too, and textures are ranked by memory");

        Require(report.Models.Count == 4 && report.Models.Single(m => m.FileName == "legs.mdl").Slots.SequenceEqual(["Legs", "Feet"]) &&
                report.Models.Single(m => m.FileName == "body.mdl").Triangles == 60 && report.Models[0].FileName == "c0201f0001_fac.mdl",
            "character weight: each model file is one row with its most detailed level's triangles, ranked by triangles");
        Require(report.SyncTriangles == 60 + 40 && report.AllTriangles == 60 + 40 + 60 + 100 && report.GameTriangles == 100,
            "character weight: sync plugins count each distinct modded model once; game models only in the full total");

        var unreadable = CharacterWeightCapture.Capture([Node("chara/x/missing.tex", ModFile("Body Mod", "missing.tex"), "Body Mod")],
            _ => null, _ => null, _ => false);
        Require(unreadable.Textures.Single().Error.Length > 0 && unreadable.SyncVram == 0 && ShrinkRules.Blocker(unreadable.Textures.Single()) is not null,
            "character weight: a file that can't be read is listed but neither counted nor offered for a change");
        return report;
    }

    // ---- Shrinking -----------------------------------------------------------------------------------

    private static WeightTexture Made(uint format, int width, int height, int mips, bool index = false,
        ResourceSourceState state = ResourceSourceState.LoadedMod, string content = "AA")
    {
        var info = new TexInfo(Type2D, format, width, height, 1, mips, 0);
        var mod = state == ResourceSourceState.LoadedMod;
        return new WeightTexture
        {
            Key = "file:" + content + width, GamePaths = ["chara/equipment/e0001/texture/x.tex"], Uses = [], Slots = [],
            Role = index ? TextureRole.Index : TextureRole.Base, IndexOnly = index, Info = info,
            FileLength = TextureCost.HeaderSize + info.Vram!.Value, Vram = info.Vram!.Value,
            Source = new PreviewSource
            {
                GamePath = "chara/equipment/e0001/texture/x.tex", ActualPath = mod ? @"C:\Penumbra\Mod\x.tex" : "chara/equipment/e0001/texture/x.tex",
                State = state, ModName = mod ? "Mod" : "", ModDirectory = mod ? "Mod" : "", RelativePath = mod ? "x.tex" : "", Sha256 = content,
            },
        };
    }

    private static void CheckPlan(CharacterWeightReport report)
    {
        WeightTexture Texture(string name) => report.Textures.Single(t => t.FileName == name);
        Require(ShrinkRules.Default(Texture("body_norm.tex")) == new ShrinkChoice(ShrinkFormat.Bc7, 0) &&
                ShrinkRules.Default(Texture("body_id.tex")) == new ShrinkChoice(ShrinkFormat.Bc5, 0),
            "character weight: uncompressed textures default to Mod Optimizer's Auto rule, BC5 for index maps and BC7 for the rest");
        Require(ShrinkRules.Default(Texture("body_mask.tex")).IsNone && ShrinkRules.Default(Texture("c0201f0001_fac_id.tex")).IsNone &&
                ShrinkRules.Default(Texture("preview_d.tex")).IsNone && ShrinkRules.Blocker(Texture("preview_d.tex"))!.Contains("preview"),
            "character weight: compressed textures, game files already compressed and preview files are left alone by default");
        Require(!ShrinkRules.Formats(Texture("body_norm.tex")).Contains(ShrinkFormat.Bc5) &&
                ShrinkRules.Formats(Texture("body_id.tex")).SequenceEqual([ShrinkFormat.Keep, ShrinkFormat.Bc7, ShrinkFormat.Bc5]),
            "character weight: two-channel BC5 is offered only where the shader reads two channels");

        var huge = Made(Bgra, 8192, 8192, 14 - 1);
        var hugeBc7 = Made(Bc7, 8192, 4096, 13);
        Require(huge.Oversized && ShrinkRules.Default(huge) == new ShrinkChoice(ShrinkFormat.Bc7, 1) &&
                ShrinkRules.Default(hugeBc7) == new ShrinkChoice(ShrinkFormat.Keep, 1),
            "character weight: textures above 4096 are halved until they fit, compressed if they were uncompressed");
        var odd = Made(Bgra, 1366, 770, 1);
        Require(ShrinkRules.Default(odd).IsNone && ShrinkRules.Halvings(odd, ShrinkFormat.Bc7).Count == 0 &&
                ShrinkRules.Halvings(odd, ShrinkFormat.Keep).SequenceEqual([0, 1]) && ShrinkRules.Suggested(odd) == new ShrinkChoice(ShrinkFormat.Keep, 1),
            "character weight: block compression needs whole 4 × 4 tiles, so odd sizes can only be halved and stay uncompressed");
        var small = Made(Bc7, 128, 128, 8);
        Require(ShrinkRules.Halvings(small, ShrinkFormat.Keep).SequenceEqual([0, 1]) && !ShrinkRules.IsValid(small, new ShrinkChoice(ShrinkFormat.Bc7, 0)) &&
                ShrinkRules.Blocker(Made(0x1131, 256, 256, 1)) is { } a8 && a8.Contains("A8"),
            "character weight: halving stops at 64 pixels, compressed textures keep their format, and A8 textures stay as they are");
        Require(ShrinkRules.WithFormat(huge, new ShrinkChoice(ShrinkFormat.Bc7, 1), ShrinkFormat.Keep) == new ShrinkChoice(ShrinkFormat.Keep, 1) &&
                ShrinkRules.WithFormat(odd, new ShrinkChoice(ShrinkFormat.Keep, 1), ShrinkFormat.Bc7) == new ShrinkChoice(ShrinkFormat.Keep, 1),
            "character weight: switching formats keeps the size where the new format allows it");
        Require(ShrinkRules.VramAfter(huge, new ShrinkChoice(ShrinkFormat.Bc7, 1)) == TextureCost.Vram(Bc7, 4096, 4096, 13) &&
                ShrinkRules.FileAfter(Texture("body_norm.tex"), new ShrinkChoice(ShrinkFormat.Bc7, 0)) == TextureCost.HeaderSize + TextureCost.Vram(Bc7, 64, 64, 7),
            "character weight: the new size counts the full mip chain every conversion writes");

        var defaults = report.Textures.ToDictionary(t => t.Key, ShrinkRules.Default);
        var plan = ShrinkRules.Totals(report, defaults);
        var bc7 = TextureCost.HeaderSize + TextureCost.Vram(Bc7, 64, 64, 7);
        Require(plan.Files == 2 && plan.SyncVram == 2 * bc7 + Texture("body_mask.tex").FileLength + Texture("preview_d.tex").FileLength &&
                plan.AllVram == report.AllVram - Texture("body_norm.tex").Vram - Texture("body_id.tex").Vram + 2 * (bc7 - TextureCost.HeaderSize),
            "character weight: the planned totals replace the changed textures' sizes");
        var mask = Texture("body_mask.tex");
        var copy = Texture("copy_mask.tex");
        var halveMask = new ShrinkChoice(ShrinkFormat.Keep, 1);
        var one = ShrinkRules.Totals(report, new Dictionary<string, ShrinkChoice> { [mask.Key] = halveMask });
        var both = ShrinkRules.Totals(report, new Dictionary<string, ShrinkChoice> { [mask.Key] = halveMask, [copy.Key] = halveMask });
        var smaller = ShrinkRules.FileAfter(mask, halveMask);
        Require(one.SyncVram == report.SyncVram + smaller && both.SyncVram == report.SyncVram - mask.FileLength + smaller,
            "character weight: changing one of two identical files makes sync plugins count both; changing both alike counts once");
        var face = Texture("c0201f0001_fac_id.tex");
        var game = ShrinkRules.Totals(report, new Dictionary<string, ShrinkChoice> { [face.Key] = new(ShrinkFormat.Keep, 1) });
        Require(game.SyncVram == report.SyncVram + ShrinkRules.FileAfter(face, new ShrinkChoice(ShrinkFormat.Keep, 1)),
            "character weight: a changed game file moves into a mod, so sync plugins start counting it");
    }

    private static void CheckHalving()
    {
        byte[] pixels =
        [
            0, 10, 200, 255, 255, 10, 200, 255, 1, 2, 3, 4, 5, 6, 7, 8,
            255, 10, 200, 255, 255, 11, 201, 0, 9, 10, 11, 12, 13, 14, 15, 16,
        ];
        var half = TextureHalving.Halve(pixels, 4, 2);
        Require(half.Length == 8 && half[0] == 191 && half[1] == 10 && half[2] == 200 && half[3] == 191 && half[4] == 7 && half[7] == 10,
            "character weight: halving averages each 2 × 2 block channel by channel, rounding to nearest");
        Reject(() => TextureHalving.Halve(new byte[3 * 2 * 4], 3, 2), "character weight: an odd width can't be halved evenly");

        var tga = TextureHalving.Tga(half, 2, 1);
        var read = TgaImage.Read(tga);
        Require(read.Width == 2 && read.Height == 1 && read.Pixels[0] == half[2] && read.Pixels[2] == half[0] && read.Pixels[3] == half[3],
            "character weight: the halved BGRA pixels are written as a top-down 32-bit TGA for Penumbra");

        var tex = Tex(Bgra, 4, 4, 3, 7);
        var (top, width, height) = TextureHalving.ReadBgra(tex);
        Require(width == 4 && height == 4 && top.Length == 64 && top.All(b => b == 7),
            "character weight: only the top level of Penumbra's decoded texture is read");
    }

    // ---- Text ----------------------------------------------------------------------------------------

    private static void CheckViews()
    {
        Require(CharacterWeightViews.Bytes(0) == "0 B" && CharacterWeightViews.Bytes(1536) == "1.50 KiB" &&
                CharacterWeightViews.Bytes(642_097_971) == "612.35 MiB" && CharacterWeightViews.Count(212_301) == "212,301",
            "character weight: sizes print like the sync plugins print them");
        Require(CharacterWeightViews.VramLevel(SyncThresholds.WarningVram) == WeightLevel.Below &&
                CharacterWeightViews.VramLevel(SyncThresholds.WarningVram + 1) == WeightLevel.Warning &&
                CharacterWeightViews.VramLevel(SyncThresholds.AutoPauseVram + 1) == WeightLevel.AutoPause &&
                CharacterWeightViews.TriangleLevel(165_000) == WeightLevel.Below && CharacterWeightViews.TriangleLevel(250_001) == WeightLevel.AutoPause &&
                SyncThresholds.WarningVram == 375L << 20 && SyncThresholds.AutoPauseVram == 550L << 20,
            "character weight: a total is over a sync plugin limit only when it is greater, with Lightless' and PlayerSync's defaults");
        Require(CharacterWeightViews.VramStatus(600L << 20).Contains("550.00 MiB auto-pause") &&
                CharacterWeightViews.TriangleStatus(170_000).Contains("165,000 warning"),
            "character weight: the status names the limit the total is above");
        Require(CharacterWeightViews.Usage(new ModFileUsage(["Default"], false)) == "used by Default" &&
                CharacterWeightViews.Usage(new ModFileUsage(["Top: Red", "Top: Blue", "Top: Green"], true)) ==
                "used by Top: Red, Top: Blue and Top: Green, mapped to several game paths" &&
                CharacterWeightViews.Usage(new ModFileUsage(["A", "B", "C", "D"], false)) == "used by 4 options",
            "character weight: the confirmation names the options that share a file");

        var texture = Made(Bgra, 2048, 2048, 1);
        var change = CharacterWeightViews.Change(texture, new ShrinkChoice(ShrinkFormat.Bc7, 1));
        Require(change.StartsWith("x.tex: BGRA32 2048 × 2048 → BC7 1024 × 1024 (16.00 MiB → ", StringComparison.Ordinal) &&
                CharacterWeightViews.Notes(texture) == "uncompressed · no mipmaps" &&
                CharacterWeightViews.Notes(Made(Bc7, 1024, 1024, 11, state: ResourceSourceState.GameData)) == "game file",
            "character weight: a change reads format, size and memory before and after, and notes list the flags");
        var texts = new[]
        {
            change, CharacterWeightViews.Notes(texture), CharacterWeightViews.VramStatus(0), CharacterWeightViews.TriangleStatus(1_000_000),
            string.Join(" ", CharacterWeightViews.NoteDetails(texture)), CharacterWeightViews.FormatLabel(texture, ShrinkFormat.Keep),
            CharacterWeightViews.SizeLabel(texture, 0),
        };
        Require(texts.All(text => !text.Contains('…') && !text.Contains("...", StringComparison.Ordinal)),
            "character weight: no text uses an ellipsis, which the game's font can't draw");
    }

    // ---- Shared preview records ----------------------------------------------------------------------

    private static void CheckPreviewRecords(string testRoot)
    {
        PreviewSource source = new NeckSeamSource
        {
            GamePath = "chara/x.mtrl", ActualPath = @"C:\Mods\Skin\x.mtrl", State = ResourceSourceState.LoadedMod, ModName = "Skin",
            ModDirectory = "Skin", RelativePath = "x.mtrl", Sha256 = "AB",
        };
        Require(source.IsModFile && source.Label == "Skin: x.mtrl",
            "preview mods: the neck seam's sources are the shared sources, with the same meaning");

        // A NeckSeamPreviews.json as the neck seam wrote it before the preview-mod service was shared.
        var folder = Path.Combine(testRoot, "PreviewStores");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "NeckSeamPreviews.json"),
            """
            [
              {
                "Id": "0f8fad5b-d9cb-469f-a165-70867728950e",
                "ModDirectory": "Neck Seam Preview - A",
                "ModIdentifier": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                "CollectionId": "550e8400-e29b-41d4-a716-446655440000",
                "CollectionName": "Default",
                "ActorName": "A",
                "ObjectIndex": 0,
                "Created": "2026-09-28T12:00:00+00:00",
                "Files": [
                  {
                    "Kind": "face material",
                    "GamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewGamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewRelativePath": "Files/chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                    "PreviewSha256": "00",
                    "Source": {
                      "GamePath": "chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_a.mtrl",
                      "ActualPath": "C:\\Mods\\Skin\\x.mtrl",
                      "State": "LoadedMod",
                      "ModName": "Skin",
                      "ModDirectory": "Skin",
                      "ModRootPath": "",
                      "RelativePath": "x.mtrl",
                      "ModStableId": null,
                      "Sha256": "AB"
                    },
                    "TextureRewrites": { "a_ns1.tex": "a.tex" }
                  }
                ],
                "Changes": [ "Face material: tile size" ]
              }
            ]
            """);
        var neckSeam = new NeckSeamPreviewStore(folder);
        neckSeam.Load();
        var old = neckSeam.Previews.Single();
        Require(neckSeam.LoadError.Length == 0 && old.Files.Single().Source.IsModFile && old.Files.Single().TextureRewrites["a_ns1.tex"] == "a.tex" &&
                neckSeam.HoldsMod("neck seam preview - a") && !neckSeam.HoldsMod("Skin") && neckSeam.PreviewFor("A") == old,
            "preview mods: neck seam previews saved before the shared store still load, and the store knows their mod folders");

        var store = new PreviewModStore<PreviewMod>(folder, "CharacterWeightPreviews.json", "character weight previews");
        var preview = new PreviewMod
        {
            Id = Guid.NewGuid(), ModDirectory = PreviewFolder, ModIdentifier = Guid.NewGuid(), CollectionId = Guid.NewGuid(), CollectionName = "Default",
            ActorName = "A", ObjectIndex = 0, Created = DateTimeOffset.UtcNow, Changes = ["x.tex: BGRA32 → BC7"],
            Files =
            [
                new PreviewModFile
                {
                    Kind = "texture", GamePath = "chara/a/x.tex", GamePaths = ["chara/a/x.tex", "chara/b/x.tex"], PreviewRelativePath = "Files/chara/a/x.tex",
                    PreviewSha256 = "01", Note = "used by Default", Source = source,
                },
                new PreviewModFile
                {
                    Kind = "texture", GamePath = "chara/c/y.tex", PreviewRelativePath = "Files/chara/c/y.tex", PreviewSha256 = "02",
                    Source = new PreviewSource { GamePath = "chara/c/y.tex", ActualPath = "chara/c/y.tex", State = ResourceSourceState.GameData, Sha256 = "03" },
                },
            ],
        };
        store.Add(preview);
        var reloaded = new PreviewModStore<PreviewMod>(folder, "CharacterWeightPreviews.json", "character weight previews");
        reloaded.Load();
        var files = reloaded.Previews.Single().Files;
        Require(reloaded.LoadError.Length == 0 && files[0].FixGamePaths.SequenceEqual(["chara/a/x.tex", "chara/b/x.tex"]) &&
                files[1].FixGamePaths.SequenceEqual(["chara/c/y.tex"]) && files[0].Note == "used by Default" && !files[1].Source.IsModFile &&
                reloaded.HoldsMod(PreviewFolder),
            "preview mods: weight previews survive a reload with every game path their files replace");
        var lines = CharacterWeightViews.ApplyLines(reloaded.Previews.Single());
        Require(lines[0] == "Overwrite Skin: x.mtrl (used by Default)" && lines[1] == "Put the texture for chara/c/y.tex in a new mod (it is game data)",
            "preview mods: the confirmation lists each file it overwrites, with what else uses it, and the game files that go into a new mod");
        reloaded.Remove(preview.Id);
        Require(reloaded.Previews.Count == 0 && !reloaded.HoldsMod(PreviewFolder), "preview mods: an applied or discarded preview is forgotten");

        var entry = PreviewModEntry.At("/chara\\x\\--y.tex", [1]);
        Require(entry.RelativePath == "Files/chara/x/--y.tex" && entry.GamePaths.SequenceEqual(["chara/x/--y.tex"]),
            "preview mods: a file mapped from one game path sits at Files/<game path>");
        Require(new PreviewApplyResult(["Skin", "Face"], 2, "Neck Seam Fix - A", 1, []).Describe("Neck seam fix applied") ==
                "Neck seam fix applied: wrote 2 files into Skin, Face (backups kept for 7 days); put 1 game file in the mod Neck Seam Fix - A." &&
                new PreviewApplyResult([], 0, null, 0, []).Describe("Neck seam fix applied") == "Nothing needed writing; the preview is removed.",
            "preview mods: applying reports what it wrote the way the neck seam always has");
        Require(PenumbraService.UniqueModName("", _ => false, "Character Weight Fix") == "Character Weight Fix" &&
                PenumbraService.UniqueModName("Character Weight Preview - A", name => name == "Character Weight Preview - A") == "Character Weight Preview - A (2)",
            "preview mods: new mod names fall back to the feature's name and are made unique");
    }
}
