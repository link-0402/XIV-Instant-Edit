using System.Buffers.Binary;
using InstantEdit.Services;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using InstantEdit.Ui;
using Penumbra.Api.Enums;
using static InstantEdit.TestSupport.Assertions;
using StudioModels = InstantEdit.Studio.Models;

/// <summary>Resizing and compressing a mod's textures: what is offered, what is ticked, and that nothing the check rejects is written.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunTextures(string testRoot)
    {
        RestrictionUsesRepeatedHalving();
        HeadersSayWhatCanBeEncoded();
        ScanTicksOnlyLargeOrdinaryTextures();
        ScanChecksTexturesWithoutAMaterialAsUnknown();
        AutoPicksBc5ForIndexMapsAndBc7Otherwise(testRoot);
        AManualBc5PickIsComparedOnAllFourChannels(testRoot);
        AnEncodeTheCheckRejectsIsNotWritten(testRoot);
        ResizingHalvesAndChecksAgainstTheHalvedOriginal(testRoot);
        ACancelledCleanUpKeepsWhatItFinished(testRoot);
        TextureCleanUpStopsWhenTheBackupCantBeStored(testRoot);
        ATextureChangedSinceTheListIsLeftAlone(testRoot);
        KeepFormatIsRefusedForFormatsThatCantBeWritten();
        AutoCompressionKeepsWhatTheCheckRejectsUncompressed(testRoot);
        AutoCompressionReadsUsesFromTheMaterial();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A BGRA32 texture with a pattern no compressor shrinks to one colour.</summary>
    private static byte[] Pattern(int width, int height, int seed = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)(x * 3 + seed);
            pixels[i + 1] = (byte)(y * 5 + seed);
            pixels[i + 2] = (byte)((x ^ y) + seed);
            pixels[i + 3] = 255;
        }
        return TextureFiles.BgraTex(pixels, width, height);
    }

    private static byte[] Header(uint format, int width, int height)
    {
        var bytes = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = 1;
        bytes[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 80);
        return bytes;
    }

    /// <summary>
    /// Penumbra's conversion, offline: "compressed" files carry the right header and size, with their
    /// pixels kept aside so decoding them gives back what went in (BC5 without blue and alpha, or,
    /// corrupted, something else).
    /// </summary>
    private sealed class FakeConverter
    {
        private readonly Dictionary<string, byte[]> _pixels = new(StringComparer.OrdinalIgnoreCase);
        public bool Corrupt { get; init; }
        public List<TextureType> Requested { get; } = [];
        public Func<int, CancellationToken, Task>? BeforeEncode { get; init; }
        private int _encodes;

        public async Task Convert(string input, string output, TextureType type, bool mipMaps)
        {
            var source = File.ReadAllBytes(input);
            var width = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(8));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(10));
            var pixels = _pixels.TryGetValue(input, out var kept) ? kept : source[80..(80 + width * height * 4)];
            if (type == TextureType.RgbaTex)
            {
                File.WriteAllBytes(output, TextureFiles.BgraTex(pixels, width, height));
                return;
            }
            if (BeforeEncode is { } before)
                await before(_encodes, CancellationToken.None);
            _encodes++;
            Requested.Add(type);
            var format = type switch
            {
                TextureType.Bc1Tex => TextureCost.Bc1, TextureType.Bc3Tex => TextureCost.Bc3, TextureType.Bc4Tex => TextureCost.Bc4,
                TextureType.Bc5Tex => TextureCost.Bc5, _ => TextureCost.Bc7,
            };
            var blockBytes = format is TextureCost.Bc1 or TextureCost.Bc4 ? 8 : 16;
            var data = new byte[(width + 3) / 4 * ((height + 3) / 4) * blockBytes];
            File.WriteAllBytes(output, Header(format, width, height).Concat(data).ToArray());
            var stored = pixels.ToArray();
            // BC5 keeps red and green only: it decodes with blue 0 and alpha full, as Penumbra's does.
            if (format == TextureCost.Bc5)
                for (var i = 0; i < stored.Length; i += 4)
                {
                    stored[i] = 0;
                    stored[i + 3] = 255;
                }
            if (Corrupt)
                for (var i = 0; i < stored.Length; i += 4)
                    stored[i] = (byte)(255 - stored[i]);
            _pixels[output] = stored;
        }
    }

    private static TextureCandidate Candidate(TestModFolder mod, string relPath, IReadOnlyList<TextureUse> uses)
    {
        var info = TextureCost.Read(File.ReadAllBytes(Path.Combine(mod.Path, relPath)))!.Value;
        return new TextureCandidate(relPath, Path.Combine(mod.Path, relPath), info, uses, Selectable: true, Preselected: true, Note: null, GamePaths: []);
    }

    private static readonly TextureUse BaseUse = new() { Role = TextureRole.Base, SamplerId = TextureUse.DiffuseSampler, ShaderPackage = "character.shpk" };
    private static readonly TextureUse IndexUse = new() { Role = TextureRole.Index, SamplerId = TextureUse.IndexSampler, ShaderPackage = "character.shpk" };

    private static (ModBackupStore Store, string Cache) TextureBackups(string testRoot)
    {
        var config = Path.Combine(testRoot, "TexConfig", Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(testRoot, "TexCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(cache);
        var store = new ModBackupStore(config);
        store.Load();
        return (store, cache);
    }

    private static CleanUpResult Clean(TestModFolder mod, IReadOnlyList<TextureJob> jobs, FakeConverter convert, string testRoot,
        ModBackupStore? store = null, string? cache = null, CancellationToken token = default)
    {
        if (store is null)
            (store, cache) = TextureBackups(testRoot);
        return TextureCleanUp.ApplyAsync(ModProject.Load(mod.Path), jobs, convert.Convert, store, cache!, new FakeReloader(), new StudioSettings(), null, token)
            .GetAwaiter().GetResult();
    }

    private static uint FormatOf(TestModFolder mod, string relPath) => BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Path.Combine(mod.Path, relPath)).AsSpan(4));

    private static TestModFolder TextureMod(params string[] relPaths)
    {
        var mod = new TestModFolder();
        var files = new Newtonsoft.Json.Linq.JObject();
        for (var i = 0; i < relPaths.Length; i++)
        {
            mod.File(relPaths[i], Pattern(256, 256, i * 17));
            files[$"chara/equipment/e0005/texture/t{i}.tex"] = relPaths[i];
        }
        mod.Meta(new Newtonsoft.Json.Linq.JObject { ["FileVersion"] = 4, ["Name"] = "Textures", ["DefaultData"] = new Newtonsoft.Json.Linq.JObject { ["Files"] = files } }.ToString());
        return mod;
    }

    // ── Scenarios ────────────────────────────────────────────────────────────

    private static void RestrictionUsesRepeatedHalving()
        => Require(VerifiedEncode.Restricted(4096, 2048, 2048) == (2048, 1024) && VerifiedEncode.Restricted(3000, 1000, 2048) == (1500, 500) &&
                   VerifiedEncode.Restricted(32, 16, 16) == (16, 8),
            "a size cap halves both sides until the larger fits");

    private static void HeadersSayWhatCanBeEncoded()
    {
        var bc7 = TextureCost.Read(Header(TextureCost.Bc7, 4096, 2048))!.Value;
        var bc6 = TextureCost.Read(Header(TextureCost.Bc6H, 1024, 1024))!.Value;
        Require(bc7.Is2D && bc7.IsBlockCompressed && TextureCost.FormatName(bc7.Format) == "BC7" && TextureCleanUp.WhyNotSelectable(bc7) is null,
            "a BC7 texture can be resized and encoded again");
        Require(TextureCost.FormatName(bc6.Format) == "BC6H" && TextureCleanUp.WhyNotSelectable(bc6) is { Length: > 0 },
            "an HDR BC6H texture can't be picked");
    }

    private static void ScanTicksOnlyLargeOrdinaryTextures()
    {
        const string normal = "chara/equipment/e0005/texture/mt_c0201e0005_top_a_norm.tex";
        const string compressed = "chara/equipment/e0005/texture/mt_c0201e0005_top_a_mask.tex";
        using var mod = new TestModFolder()
            .File("files/model.mdl", ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/material.mtrl", ModCheckFixtures.Mtrl([normal, compressed, "chara/equipment/e0005/texture/hdr.tex", "chara/equipment/e0005/texture/base.tex"]))
            .File("files/normal.tex", Pattern(4096, 16))
            .File("files/mask.tex", Header(TextureCost.Bc7, 4096, 16).Concat(new byte[4096 * 16]).ToArray())
            .File("files/hdr.tex", Header(TextureCost.Bc6H, 4096, 16).Concat(new byte[4096 * 16]).ToArray())
            .File("files/base.tex", Pattern(4096, 16, 3));
        mod.Meta(new Newtonsoft.Json.Linq.JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Scan",
            ["DefaultData"] = new Newtonsoft.Json.Linq.JObject
            {
                ["Files"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["chara/equipment/e0005/model/c0201e0005_top.mdl"] = "files/model.mdl",
                    ["chara/equipment/e0005/material/v0001/mt_c0201e0005_top_a.mtrl"] = "files/material.mtrl",
                    [normal] = "files/normal.tex",
                    [compressed] = "files/mask.tex",
                    ["chara/equipment/e0005/texture/hdr.tex"] = "files/hdr.tex",
                    ["chara/equipment/e0005/texture/base.tex"] = "files/base.tex",
                },
            },
        }.ToString());
        var meta = PenumbraModMeta.Load(mod.Path);
        var game = new FakeVanillaFiles().Add("chara/equipment/e0005/e0005.imc", ModCheckFixtures.Imc([[(1, 0x3FF)], [(1, 0x3FF)]]));
        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod.Path, Files = meta.AllFiles().ToList(), Swaps = meta.AllSwaps().ToList() },
            new ModFileFactsCache(), game, CancellationToken.None);

        var candidates = TextureCleanUp.Scan(graph, node => File.Exists(node.DiskPath) ? File.ReadAllBytes(node.DiskPath) : null);
        TextureCandidate Of(string rel) => candidates.Single(c => c.RelPath == rel);
        Require(candidates.Count == 4 && Of("files/base.tex") is { Preselected: true, Selectable: true } &&
                Of("files/base.tex").Uses.Any(u => u.Role == TextureRole.Base),
            "a large uncompressed base texture is ticked, with the use its material gives it");
        Require(Of("files/normal.tex") is { Preselected: false, Selectable: true, Note.Length: > 0 } && Of("files/normal.tex").Uses.Any(u => u.Role == TextureRole.Normal),
            "a normal map is offered but left unticked, with a note");
        Require(Of("files/mask.tex") is { Preselected: false, Selectable: true } && Of("files/hdr.tex") is { Selectable: false },
            "an already-compressed texture is left unticked, an HDR one can't be picked");
    }

    private static void ScanChecksTexturesWithoutAMaterialAsUnknown()
    {
        const string gear = "chara/equipment/e0005/texture/v01_c0201e0005_top_norm.tex";
        const string hair = "chara/human/c0101/obj/hair/h0001/texture/c0101h0001_hir_norm.tex";
        using var mod = new TestModFolder()
            .File("files/top_norm.tex", Pattern(256, 256))
            .File("files/hir_norm.tex", Pattern(256, 256, 5));
        mod.Meta(new Newtonsoft.Json.Linq.JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Loose",
            ["DefaultData"] = new Newtonsoft.Json.Linq.JObject
            {
                ["Files"] = new Newtonsoft.Json.Linq.JObject { [gear] = "files/top_norm.tex", [hair] = "files/hir_norm.tex" },
            },
        }.ToString());
        var meta = PenumbraModMeta.Load(mod.Path);
        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod.Path, Files = meta.AllFiles().ToList(), Swaps = meta.AllSwaps().ToList() },
            new ModFileFactsCache(), new FakeVanillaFiles(), CancellationToken.None);
        var candidates = TextureCleanUp.Scan(graph, node => File.Exists(node.DiskPath) ? File.ReadAllBytes(node.DiskPath) : null);
        var top = candidates.Single(c => c.RelPath == "files/top_norm.tex").Uses;
        var hir = candidates.Single(c => c.RelPath == "files/hir_norm.tex").Uses;
        Require(top.Count == 2 && top.All(u => u is { Role: TextureRole.Normal, ShaderPackage: "character.shpk", OpacityChannel: 2, AlphaThreshold: 0.5f }) &&
                top.Any(u => u.CutsOut) && top.Any(u => u.Translucent),
            "a texture no material of the mod reads is checked with its opacity both cut out and blended at half");
        Require(hir.Count == 2 && hir.All(u => u is { ShaderPackage: "hair.shpk", OpacityChannel: 3 }),
            "a loose hair normal map is checked as hair.shpk reads it");
    }

    private static void AutoPicksBc5ForIndexMapsAndBc7Otherwise(string testRoot)
    {
        using var mod = TextureMod("t/base.tex", "t/id.tex");
        var convert = new FakeConverter();
        var result = Clean(mod,
        [
            new TextureJob(Candidate(mod, "t/base.tex", [BaseUse]), TextureFormatChoice.Auto, 4096),
            new TextureJob(Candidate(mod, "t/id.tex", [IndexUse]), TextureFormatChoice.Auto, 4096),
        ], convert, testRoot);
        Require(result.Changed == 2 && FormatOf(mod, "t/base.tex") == TextureCost.Bc7 && FormatOf(mod, "t/id.tex") == TextureCost.Bc5,
            "Auto compresses an index map to BC5 and other textures to BC7");
    }

    private static void AManualBc5PickIsComparedOnAllFourChannels(string testRoot)
    {
        using var mod = TextureMod("t/norm.tex", "t/base.tex", "t/id.tex");
        var normalUse = new TextureUse { Role = TextureRole.Normal, SamplerId = TextureUse.NormalSampler, ShaderPackage = "character.shpk", AlphaThreshold = 0.5f };
        var before = File.ReadAllBytes(Path.Combine(mod.Path, "t", "norm.tex"));
        var result = Clean(mod,
        [
            new TextureJob(Candidate(mod, "t/norm.tex", [normalUse]), TextureFormatChoice.Bc5, 4096),
            new TextureJob(Candidate(mod, "t/base.tex", [BaseUse]), TextureFormatChoice.Bc5, 4096),
            new TextureJob(Candidate(mod, "t/id.tex", [IndexUse]), TextureFormatChoice.Bc5, 4096),
        ], new FakeConverter(), testRoot);
        Require(result.Changed == 1 && result.Kept == 2 && FormatOf(mod, "t/id.tex") == TextureCost.Bc5 &&
                File.ReadAllBytes(Path.Combine(mod.Path, "t", "norm.tex")).SequenceEqual(before) && FormatOf(mod, "t/base.tex") == TextureCost.Bgra8,
            "BC5 picked by hand passes only for an index map: on a normal map it would drop the blue cut-out");
    }

    private static void AnEncodeTheCheckRejectsIsNotWritten(string testRoot)
    {
        using var mod = TextureMod("t/base.tex");
        var before = File.ReadAllBytes(Path.Combine(mod.Path, "t", "base.tex"));
        var result = Clean(mod, [new TextureJob(Candidate(mod, "t/base.tex", [BaseUse]), TextureFormatChoice.Bc7, 4096)], new FakeConverter { Corrupt = true }, testRoot);
        Require(result.Changed == 0 && result.Kept == 1 && result.Problems.Count == 1 &&
                File.ReadAllBytes(Path.Combine(mod.Path, "t", "base.tex")).SequenceEqual(before),
            "a texture whose encode the check rejects stays byte for byte as it was");
    }

    private static void ResizingHalvesAndChecksAgainstTheHalvedOriginal(string testRoot)
    {
        using var mod = TextureMod("t/base.tex");
        var result = Clean(mod, [new TextureJob(Candidate(mod, "t/base.tex", [BaseUse]), TextureFormatChoice.Bc7, 128)], new FakeConverter(), testRoot);
        var header = TextureCost.Read(File.ReadAllBytes(Path.Combine(mod.Path, "t", "base.tex")))!.Value;
        Require(result.Changed == 1 && header.Width == 128 && header.Height == 128 && header.Format == TextureCost.Bc7,
            "a 256 texture capped at 128 is halved and encoded");
    }

    private static void ACancelledCleanUpKeepsWhatItFinished(string testRoot)
    {
        using var mod = TextureMod("t/0.tex", "t/1.tex", "t/2.tex", "t/3.tex");
        var originals = Enumerable.Range(0, 4).Select(i => File.ReadAllBytes(Path.Combine(mod.Path, "t", $"{i}.tex"))).ToArray();
        var cts = new CancellationTokenSource();
        var convert = new FakeConverter { BeforeEncode = (n, _) => { if (n == 2) cts.Cancel(); return Task.CompletedTask; } };
        var (store, cache) = TextureBackups(testRoot);
        var jobs = Enumerable.Range(0, 4).Select(i => new TextureJob(Candidate(mod, $"t/{i}.tex", [BaseUse]), TextureFormatChoice.Bc7, 4096)).ToList();

        var cancelled = false;
        try { Clean(mod, jobs, convert, testRoot, store, cache, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }

        bool Unchanged(int i) => File.ReadAllBytes(Path.Combine(mod.Path, "t", $"{i}.tex")).SequenceEqual(originals[i]);
        Require(cancelled && !Unchanged(0) && !Unchanged(1) && Unchanged(2) && Unchanged(3) &&
                store.For(mod.Path).Single().Files.Count == 2 && !Directory.EnumerateFiles(mod.Path, "*.instant-edit-tmp", SearchOption.AllDirectories).Any(),
            "a cancelled clean-up keeps what it finished, journaled, and leaves the rest and no half-written file");
    }

    private static void TextureCleanUpStopsWhenTheBackupCantBeStored(string testRoot)
    {
        using var mod = TextureMod("t/base.tex");
        var before = File.ReadAllBytes(Path.Combine(mod.Path, "t", "base.tex"));
        var (store, _) = TextureBackups(testRoot);
        var blocked = Path.Combine(testRoot, $"tex-cache-is-a-file-{Guid.NewGuid():N}");
        File.WriteAllText(blocked, "not a folder");

        var result = Clean(mod, [new TextureJob(Candidate(mod, "t/base.tex", [BaseUse]), TextureFormatChoice.Bc7, 4096)], new FakeConverter(), testRoot, store, blocked);
        Require(result.Changed == 0 && result.Problems.Count == 1 && File.ReadAllBytes(Path.Combine(mod.Path, "t", "base.tex")).SequenceEqual(before),
            "a texture whose original can't be backed up is not touched");
    }

    private static void ATextureChangedSinceTheListIsLeftAlone(string testRoot)
    {
        using var mod = TextureMod("t/base.tex");
        var candidate = Candidate(mod, "t/base.tex", [BaseUse]);
        mod.File("t/base.tex", Pattern(128, 128, 5));
        var result = Clean(mod, [new TextureJob(candidate, TextureFormatChoice.Bc7, 4096)], new FakeConverter(), testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && result.Problems[0].Contains("changed since") && FormatOf(mod, "t/base.tex") == TextureCost.Bgra8,
            "a texture that changed since the list was made is left alone, with the reason");
    }

    private static void KeepFormatIsRefusedForFormatsThatCantBeWritten()
    {
        var bc2 = TextureCost.Read(Header(TextureCost.Bc2, 256, 256))!.Value;
        var bc7 = TextureCost.Read(Header(TextureCost.Bc7, 256, 256))!.Value;
        Require(TextureCleanUp.WhyNotFormat(bc2, TextureFormatChoice.Keep) is { Length: > 0 }, "a format the encoder can't write can't be kept");
        Require(TextureCleanUp.WhyNotFormat(bc2, TextureFormatChoice.Bc7) is null && TextureCleanUp.WhyNotFormat(bc7, TextureFormatChoice.Keep) is null,
            "it can be encoded into another format, and a writable format can be kept");
    }

    private static void AutoCompressionKeepsWhatTheCheckRejectsUncompressed(string testRoot)
    {
        var folder = Path.Combine(testRoot, "AutoCompression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string Image(string name, int size)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, Pattern(size, size, name.Length));
            return path;
        }

        var image = Image("base.tex", 256);
        var good = AutoCompression.Encode(image, [BaseUse], new FakeConverter().Convert, CancellationToken.None);
        var index = AutoCompression.Encode(image, [IndexUse], new FakeConverter().Convert, CancellationToken.None);
        Require(good is { Format: StudioModels.TextureCompression.Bc7, Kept.Length: 0 } && TextureCost.Read(good.Tex)!.Value.Format == TextureCost.Bc7 &&
                index.Format == StudioModels.TextureCompression.Bc5 && AutoCompression.FormatOf(index.Tex) == StudioModels.TextureCompression.Bc5,
            "Auto writes BC7, or BC5 for an index map, when the check passes");

        var bad = AutoCompression.Encode(image, [BaseUse], new FakeConverter { Corrupt = true }.Convert, CancellationToken.None);
        Require(bad is { Format: StudioModels.TextureCompression.None, Kept.Length: > 0 } && TextureCost.Read(bad.Tex)!.Value.Format == TextureCost.Bgra8 &&
                AutoCompression.Describe("base.png", bad.Format, bad.Kept, string.Empty).Contains(bad.Kept, StringComparison.Ordinal),
            "an encode the check rejects leaves the texture uncompressed, with the reason");

        var small = AutoCompression.Encode(Image("small.tex", 64), [BaseUse], new FakeConverter().Convert, CancellationToken.None);
        Require(small is { Format: StudioModels.TextureCompression.None, Kept.Length: > 0 }, "a texture too small to compress stays uncompressed");
    }

    private static void AutoCompressionReadsUsesFromTheMaterial()
    {
        var material = new MtrlInfo
        {
            ShaderPackageName = "character.shpk",
            ShaderDataParsed = true,
            MaterialFlags1 = (ushort)TextureUse.TranslucencyFlag,
            Constants = { new MtrlConstant { Id = TextureUse.AlphaThresholdConstant, Offset = 4, Size = 4 } },
            ShaderValues = new byte[4].Concat(BitConverter.GetBytes(0.3f)).ToArray(),
        };
        var normal = AutoCompression.UsesFor(StudioModels.TextureType.Normal, StudioModels.ShaderType.Character, material).Single();
        Require(normal is { Role: TextureRole.Normal, ShaderPackage: "character.shpk", AlphaThreshold: 0.3f, Translucent: true, OpacityChannel: 2 },
            "a slot's use takes the shader, threshold and translucency of its material");

        var unknown = AutoCompression.UsesFor(StudioModels.TextureType.Normal, StudioModels.ShaderType.Hair, null);
        Require(unknown.Count == 2 && unknown.All(use => use is { ShaderPackage: "hair.shpk", OpacityChannel: 3, AlphaThreshold: 0.5f })
                && unknown.Any(use => use.CutsOut) && unknown.Any(use => use.Translucent),
            "with no material yet, an opacity channel is checked both as a cut-out and as blended");
        Require(AutoCompression.UsesFor(StudioModels.TextureType.Index, StudioModels.ShaderType.Character, null).All(use => use.Role == TextureRole.Index),
            "an index slot is checked as an index map");

        var hair = AutoCompression.UsesFor("chara/human/c0101/obj/hair/h0001/texture/c0101h0001_hir_norm.tex", "hair.png")[0];
        var gear = AutoCompression.UsesFor("chara/equipment/e0005/texture/v01_c0201e0005_top_id.tex", "id.png")[0];
        Require(hair is { Role: TextureRole.Normal, ShaderPackage: "hair.shpk" } && gear is { Role: TextureRole.Index, ShaderPackage: "character.shpk" },
            "a replaced texture is checked as its path and kind of model say");
        var lashes = AutoCompression.UsesFor("chara/human/c0101/obj/face/f0001/texture/c0101f0001_etc_norm.tex", "etc.png")[0];
        var face = AutoCompression.UsesFor("chara/human/c0101/obj/face/f0001/texture/c0101f0001_fac_norm.tex", "fac.png")[0];
        Require(lashes.ShaderPackage == "hair.shpk" && face.ShaderPackage == "skin.shpk",
            "a face's _etc_ textures are checked as hair.shpk reads them, the rest as skin");
        Require(AutoCompression.KeyOf([BaseUse]) != AutoCompression.KeyOf([IndexUse]), "different uses are cached apart");
    }
}
