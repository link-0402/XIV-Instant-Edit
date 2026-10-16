using System.IO.Compression;
using System.Text;
using InstantEdit.Studio.Services.Sims;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Sims 4 importer against hostile .package input: forged sizes, oversized textures,
/// zero-stride meshes, RefPack header flags, bad face indices, names from the package,
/// re-extracting into the same folder, and cancelling. Every byte buffer here is built by hand.
/// </summary>
internal static class SimsImportScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "SimsImport");
        Directory.CreateDirectory(root);

        ForgedUnpackedSizesAreRefusedBeforeAllocating(root);   // sims-1
        ZlibOutputStopsAtTheCap();
        RefPackFlagsReadTheRightWayRound();                    // sims-5
        TruncatedRefPackIsAnError();
        TextureDimensionsAreCappedAndCheckedAgainstTheData();  // sims-2
        GeomLayoutIsCheckedBeforeAllocating();                 // sims-3
        BadFaceIndexDropsOnlyThatFace(root);                   // sims-7
        NamesFromThePackageStayInsideTheOutputFolder(root);    // sims-6
        ReExtractingReplacesOnlyItsOwnFiles(root);             // sims-4
        SimsWorkStopsWhenCancelled(root);                      // sims-8
    }

    private const long Megabyte = 1024 * 1024;

    private static void ForgedUnpackedSizesAreRefusedBeforeAllocating(string root)
    {
        var bomb = Zlib(new byte[1024 * 1024]);
        var noise = new byte[64 * 1024];
        new Random(7).NextBytes(noise);
        var packedNoise = Zlib(noise);
        var cutNoise = packedNoise[..(packedNoise.Length / 2)];
        var path = Path.Combine(root, "forged-sizes.package");
        WritePackage(path,
            new Res(SimsResourceType.Geom, 1, bomb, MemSize: 0x80000000, Compression: 0x5A42),
            new Res(SimsResourceType.Geom, 2, new byte[100], MemSize: 0x7FFFFFC0, Compression: 0x5A42),
            new Res(SimsResourceType.Geom, 3, new byte[100], MemSize: 200 * 1024 * 1024, Compression: 0x5A42),
            new Res(SimsResourceType.Geom, 4, new byte[100], MemSize: 0x7FFFFFC0, Compression: 0xFFFF),
            new Res(SimsResourceType.Geom, 5, bomb, MemSize: 1024 * 1024, Compression: 0x5A42),
            new Res(SimsResourceType.Geom, 6, cutNoise, MemSize: (uint)noise.Length, Compression: 0x5A42));

        using var package = new DbpfPackage(path);
        string[] names =
        {
            "a zlib entry whose size has bit 31 set",
            "a 100-byte zlib entry claiming 2 GB",
            "a 100-byte zlib entry claiming 200 MB (under the cap, past any ratio)",
            "a 100-byte RefPack entry claiming 2 GB",
        };
        for (int i = 0; i < names.Length; i++)
        {
            string error = string.Empty;
            byte[]? data = null;
            long allocated = Allocated(() => data = package.TryRead(package.Entries[i], out error));
            Require(data == null && error.Length > 0 && allocated < 16 * Megabyte,
                $"{names[i]} is refused without a big allocation ({allocated / Megabyte} MB, '{error}')");
        }

        var good = package.TryRead(package.Entries[4], out var goodError);
        Require(good != null && good.Length == 1024 * 1024, $"a zlib entry with a true size still reads ({goodError})");

        var shortRead = package.TryRead(package.Entries[5], out var shortError);
        Require(shortRead == null && shortError.Length > 0,
            $"a zlib entry cut off before its stated size is an error ('{shortError}')");
    }

    private static void ZlibOutputStopsAtTheCap()
    {
        var packed = Zlib(new byte[1024 * 1024]);
        Reject(() => DbpfPackage.Inflate(packed, 0, 64 * 1024), "zlib with no stated size stops at the cap");
        Require(DbpfPackage.Inflate(packed, 0, 4 * 1024 * 1024).Length == 1024 * 1024,
            "zlib with no stated size under the cap still inflates");
    }

    private static readonly byte[] RefPackBody = { 0xE0, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0x14, 0x03, 0xFC };

    private const string RefPackText = "ABCDABCDABCD";

    private static void RefPackFlagsReadTheRightWayRound()
    {
        (string Name, byte[] Header)[] headers =
        {
            ("10 FB, 3-byte size", new byte[] { 0x10, 0xFB, 0, 0, 12 }),
            ("80 FB, 4-byte size", new byte[] { 0x80, 0xFB, 0, 0, 0, 12 }),
            ("01 FB, 3-byte packed size then 3-byte size", new byte[] { 0x01, 0xFB, 0, 0, 13, 0, 0, 12 }),
            ("81 FB, 4-byte packed size then 4-byte size", new byte[] { 0x81, 0xFB, 0, 0, 0, 16, 0, 0, 0, 12 }),
        };

        foreach (var (name, header) in headers)
        {
            var stream = header.Concat(RefPackBody).ToArray();
            var fromHeader = Encoding.ASCII.GetString(RefPack.Decompress(stream, 0));
            var fromEntry  = Encoding.ASCII.GetString(RefPack.Decompress(stream, 12));
            Require(fromHeader == RefPackText && fromEntry == RefPackText,
                $"RefPack header {name} decodes (got '{fromHeader}' / '{fromEntry}')");
        }
    }

    private static void TruncatedRefPackIsAnError()
    {
        Reject(() => RefPack.Decompress(new byte[] { 0x10, 0xFB, 0, 0, 20 }.Concat(RefPackBody).ToArray(), 0),
            "a RefPack stream that stops short of its declared size is an error");
        Reject(() => RefPack.Decompress(new byte[] { 0x10, 0xFB, 0, 0, 12, 0xE0, (byte)'A' }, 0),
            "a RefPack stream whose literals run out is an error");
        Reject(() => RefPack.Decompress(new byte[] { 0x10, 0xFB, 0, 0, 12, 0xE0, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 0x14 }, 0),
            "a RefPack command missing its operand byte is an error");
        Reject(() => RefPack.Decompress(new byte[] { 0x10, 0xFB, 0 }, 0), "a RefPack header cut short is an error");

        long allocated = Allocated(() => RefPack.Decompress(new byte[] { 0x80, 0xFB, 0x80, 0, 0, 0 }.Concat(RefPackBody).ToArray(), 0));
        Require(allocated < Megabyte, "a RefPack header size with bit 31 set is refused without allocating");
        Reject(() => RefPack.Decompress(new byte[] { 0x80, 0xFB, 0x80, 0, 0, 0 }.Concat(RefPackBody).ToArray(), 0),
            "a RefPack header size with bit 31 set is a clean error");

        allocated = Allocated(() => RefPack.Decompress(new byte[] { 0x80, 0xFB, 0x7F, 0xFF, 0xFF, 0xFF }.Concat(RefPackBody).ToArray(), 0));
        Require(allocated < Megabyte, "a 2 GB RefPack header size from 14 bytes is refused without allocating");
    }

    private static void TextureDimensionsAreCappedAndCheckedAgainstTheData()
    {
        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.Rle2, Rle2(16384, 16384)),
            "a 36-byte RLE2 claiming 16384x16384");
        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.Rle2, Rle2(8192, 8192)),
            "an RLE2 at the size cap with no command stream");
        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.Rle2, Rle2(8, 8, 2 << 2)),
            "an RLE2 whose commands cover 2 of its 4 blocks");

        var transparent = SimsTextureDecoder.Decode(SimsResourceType.Rle2, Rle2(8, 8, 4 << 2));
        Require(transparent.Width == 8 && transparent.Height == 8 && transparent.Rgba.Length == 256,
            "an RLE2 whose commands cover every block still decodes");

        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.ImgDds, UncompressedDds(4096, 4096, pixels: 0)),
            "a 128-byte uncompressed DDS claiming 4096x4096");
        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.ImgDds, UncompressedDds(23170, 23170, pixels: 0)),
            "an uncompressed DDS claiming 23170x23170");
        RequireCheapRejection(() => SimsTextureDecoder.Decode(SimsResourceType.ImgDds, UncompressedDds(2, 2, pixels: 3)),
            "an uncompressed DDS one pixel short");

        var small = SimsTextureDecoder.Decode(SimsResourceType.ImgDds, UncompressedDds(2, 2, pixels: 4));
        Require(small.Width == 2 && small.Rgba[0] == 0x30 && small.Rgba[3] == 0x40, "a complete uncompressed DDS still decodes");
    }

    private static readonly (uint, uint, byte) Position = (1, 1, 12);

    private static readonly (uint, uint, byte) Normal   = (2, 1, 12);

    private static readonly (uint, uint, byte) Uv       = (3, 1, 8);

    private static void GeomLayoutIsCheckedBeforeAllocating()
    {
        var zeroUvs = Enumerable.Repeat(((uint)3, (uint)1, (byte)0), 64).ToArray();
        RequireCheapRejection(() => SimsGeom.Parse(Geom(4_000_000, zeroUvs, Array.Empty<byte>(), 2, Array.Empty<uint>())),
            "a GEOM of 4 M vertices with 64 zero-size UV elements");
        RequireCheapRejection(() => SimsGeom.Parse(Geom(1_000_000, new[] { Position }, new byte[12], 2, Array.Empty<uint>())),
            "a GEOM of 1 M vertices holding one vertex of data");
        RequireCheapRejection(() => SimsGeom.Parse(Geom(1, new[] { ((uint)1, (uint)1, (byte)4) }, new byte[12], 2, Array.Empty<uint>())),
            "a GEOM position element smaller than a position");
        RequireCheapRejection(() => SimsGeom.Parse(Geom(1, Enumerable.Repeat(Uv, 5).ToArray(), new byte[40], 2, Array.Empty<uint>())),
            "a GEOM with five UV channels");
        RequireCheapRejection(() => SimsGeom.Parse(Geom(3, new[] { Position }, new byte[36], 2, Array.Empty<uint>(), indexCount: 1_000_000)),
            "a GEOM claiming a million face points it does not hold");

        var mesh = SimsGeom.Parse(Geom(3, new[] { Position, Uv }, new byte[60], 2, new uint[] { 0, 1, 2 }));
        Require(mesh.VertexCount == 3 && mesh.FaceCount == 1 && mesh.UvChannels.Count == 1, "a well-formed GEOM still parses");
    }

    private static void BadFaceIndexDropsOnlyThatFace(string root)
    {
        var vertices = new byte[4 * 24];
        var positions = new[] { (0f, 0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f), (1f, 1f, 0f) };
        for (int v = 0; v < 4; v++)
        {
            BitConverter.TryWriteBytes(vertices.AsSpan(v * 24, 4), positions[v].Item1);
            BitConverter.TryWriteBytes(vertices.AsSpan(v * 24 + 4, 4), positions[v].Item2);
            BitConverter.TryWriteBytes(vertices.AsSpan(v * 24 + 20, 4), 1f);   // normal +Z
        }

        var mesh = SimsGeom.Parse(Geom(4, new[] { Position, Normal }, vertices, 4,
            new uint[] { 0, 1, 2, 0x80000000, 1, 2, 1, 3, 2 }));
        Require(mesh.FaceCount == 3, "a GEOM with a 32-bit index past int.MaxValue still parses");

        var glb = Path.Combine(root, "bad-index.glb");
        int skipped = SimsMeshExporter.ExportGlb(mesh, glb, "bad-index");
        Require(skipped == 1 && File.Exists(glb), $"the export drops the one bad face and writes the rest (skipped {skipped})");

        var allBad = SimsGeom.Parse(Geom(4, new[] { Position, Normal }, vertices, 4, new uint[] { 0xFFFFFFFF, 1, 2 }));
        Reject(() => SimsMeshExporter.ExportGlb(allBad, Path.Combine(root, "all-bad.glb"), "all-bad"),
            "a mesh whose every face is bad fails cleanly");
    }

    private static void NamesFromThePackageStayInsideTheOutputFolder(string root)
    {
        Require(SimsPackageInspector.MakeSafeName("..") == "sims_part", "'..' is never used as a name");
        Require(SimsPackageInspector.MakeSafeName(".", "sims_package") == "sims_package", "'.' falls back too");
        Require(SimsPackageInspector.MakeSafeName("  .hidden. ") == "hidden", "leading and trailing dots and spaces go");
        Require(SimsPackageInspector.MakeSafeName("CON") == "_CON" && SimsPackageInspector.MakeSafeName("lpt1.txt") == "_lpt1.txt",
            "device names get a prefix");
        Require(SimsPackageInspector.MakeSafeName("a/b:c") == "a_b_c", "invalid characters become underscores");
        var longName = SimsPackageInspector.MakeSafeName(new string('x', 300));
        Require(longName.Length == SimsPackageInspector.MaxNameLength, $"a 300-character CAS name is capped (got {longName.Length})");

        var source = Path.Combine(root, "dots");
        Directory.CreateDirectory(source);
        var package = Path.Combine(source, "...package");
        WritePackage(package, new Res(SimsResourceType.ImgOverlay, 1, UncompressedDds(4, 4, pixels: 16)));
        var output = Path.Combine(root, "dots-out");
        var scan = SimsPackageInspector.Scan(package);
        var result = SimsPackageInspector.Extract(scan, output);
        Require(string.Equals(Path.GetDirectoryName(result.RootFolder), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(result.RootFolder) == "sims_package" && result.Extracted == 1,
            $"a package named '...package' extracts inside the output folder ({result.RootFolder})");
    }

    private static void ReExtractingReplacesOnlyItsOwnFiles(string root)
    {
        var sourceA = Path.Combine(root, "downloads-a");
        var sourceB = Path.Combine(root, "downloads-b");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        var packageA = Path.Combine(sourceA, "hair.package");
        var packageB = Path.Combine(sourceB, "hair.package");
        WritePackage(packageA,
            new Res(SimsResourceType.ImgOverlay, 1, UncompressedDds(4, 4, pixels: 16)),
            new Res(SimsResourceType.ImgOverlay, 2, UncompressedDds(4, 4, pixels: 16)),
            new Res(SimsResourceType.ImgOverlay, 3, UncompressedDds(4, 4, pixels: 16)));
        WritePackage(packageB, new Res(SimsResourceType.ImgOverlay, 9, UncompressedDds(4, 4, pixels: 16)));

        var output = Path.Combine(root, "extract-out");
        var scan = SimsPackageInspector.Scan(packageA);
        Require(scan.Textures.Count(t => t.Role == SimsTextureRole.Diffuse && t.Selected) == 3, "the fixture package scans as three diffuse swatches");

        var first = SimsPackageInspector.Extract(scan, output);
        Require(Directory.GetFiles(first.DiffuseFolder).Length == 3 && first.WrittenFiles.Count == 5 &&
                first.WrittenFiles.Any(f => Path.GetFileName(f) == "stash.json"),
            "the first extraction writes three swatches, its report and its stash.json");

        scan.Textures.Skip(1).ToList().ForEach(t => t.Selected = false);
        var second = SimsPackageInspector.Extract(scan, output);
        Require(second.RootFolder == first.RootFolder && Directory.GetFiles(second.DiffuseFolder).Length == 1 && second.DiffuseCount == 1,
            "re-extracting fewer swatches leaves only this run's swatch in the diffuse folder");

        var mine = Path.Combine(second.DiffuseFolder, "mine.png");
        File.WriteAllBytes(mine, new byte[] { 1, 2, 3 });
        var third = SimsPackageInspector.Extract(scan, output);
        Require(third.RootFolder == first.RootFolder + "_2" && File.Exists(mine) && Directory.GetFiles(second.DiffuseFolder).Length == 2
                && Directory.GetFiles(third.DiffuseFolder).Length == 1,
            "a file the importer didn't write is kept and the run moves to a fresh folder");

        var other = SimsPackageInspector.Extract(SimsPackageInspector.Scan(packageB), output);
        Require(other.RootFolder == first.RootFolder + "_3" && Directory.GetFiles(third.DiffuseFolder).Length == 1,
            "another package with the same file name never clears the first one's output");

        var legacyOut = Path.Combine(root, "legacy-out");
        var legacyFile = Path.Combine(legacyOut, "hair", "textures", "diffuse", "old.png");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyFile)!);
        File.WriteAllBytes(legacyFile, new byte[] { 1 });
        var legacy = SimsPackageInspector.Extract(scan, legacyOut);
        Require(File.Exists(legacyFile) && legacy.RootFolder.EndsWith("hair_2", StringComparison.Ordinal),
            "output from before manifests existed is left alone");
    }

    private static void SimsWorkStopsWhenCancelled(string root)
    {
        var source = Path.Combine(root, "sims");
        Directory.CreateDirectory(source);
        var package = Path.Combine(source, "swatches.package");
        WritePackage(package,
            new Res(SimsResourceType.ImgOverlay, 1, UncompressedDds(4, 4, pixels: 16)),
            new Res(SimsResourceType.ImgOverlay, 2, UncompressedDds(4, 4, pixels: 16)),
            new Res(SimsResourceType.ImgOverlay, 3, UncompressedDds(4, 4, pixels: 16)));

        using (var cts = new CancellationTokenSource())
        {
            Exception? error = null;
            try { SimsPackageInspector.Scan(package, line => { if (line.StartsWith("Decoding", StringComparison.Ordinal)) cts.Cancel(); }, cts.Token); }
            catch (Exception ex) { error = ex; }
            Require(error is OperationCanceledException, $"a scan stops when cancelled ({error?.GetType().Name})");
        }

        var scan = SimsPackageInspector.Scan(package);
        var output = Path.Combine(root, "sims-out");
        using (var cts = new CancellationTokenSource())
        {
            int calls = 0;
            Exception? error = null;
            try { SimsPackageInspector.Extract(scan, output, progress: _ => { if (++calls == 1) cts.Cancel(); }, cancel: cts.Token); }
            catch (Exception ex) { error = ex; }
            var diffuse = Directory.GetFiles(output, "*.png", SearchOption.AllDirectories);
            Require(error is OperationCanceledException && diffuse.Length < 3,
                $"an extraction stops between files when cancelled ({error?.GetType().Name}, {diffuse.Length} written)");
            Require(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
                "what it wrote is still recorded as its own");
        }
    }

    internal sealed record Res(uint Type, ulong Instance, byte[] Data, uint? MemSize = null, ushort Compression = 0);

    /// <summary>A DBPF 2.1 package with no constant key parts; entries with a compression type get the compressed flag.</summary>
    internal static void WritePackage(string path, params Res[] resources)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[96]);

        var offsets = new long[resources.Length];
        for (int i = 0; i < resources.Length; i++)
        {
            offsets[i] = ms.Position;
            bw.Write(resources[i].Data);
        }

        long indexOffset = ms.Position;
        bw.Write(0u);
        for (int i = 0; i < resources.Length; i++)
        {
            var r = resources[i];
            bw.Write(r.Type);
            bw.Write(0u);
            bw.Write((uint)(r.Instance >> 32));
            bw.Write((uint)r.Instance);
            bw.Write((uint)offsets[i]);
            bw.Write((uint)r.Data.Length | (r.Compression != 0 ? 0x80000000u : 0u));
            bw.Write(r.MemSize ?? (uint)r.Data.Length);
            bw.Write(r.Compression);
            bw.Write((ushort)1);
        }

        var bytes = ms.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x46504244u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 2u);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(36, 4), (uint)resources.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(64, 4), (uint)indexOffset);
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    /// <summary>An RLE2 header with one mip whose command stream follows it; each op is one ushort command.</summary>
    private static byte[] Rle2(ushort width, ushort height, params ushort[] ops)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x35545844u);          // DXT5
        bw.Write(0x32454C52u);          // RLE2
        bw.Write(width);
        bw.Write(height);
        bw.Write((ushort)1);
        bw.Write((ushort)0);
        int streams = 16 + 20;
        bw.Write(streams);              // command stream
        for (int i = 0; i < 4; i++)
            bw.Write(streams + ops.Length * 2);
        foreach (var op in ops)
            bw.Write(op);
        return ms.ToArray();
    }

    /// <summary>A 32-bit BGRA DDS of <paramref name="pixels"/> pixels, each 0x40302010 (B 10, G 20, R 30, A 40).</summary>
    internal static byte[] UncompressedDds(int width, int height, int pixels)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);
        bw.Write(124);
        bw.Write(0x100Fu);
        bw.Write(height);
        bw.Write(width);
        bw.Write(width * 4);
        bw.Write(0);
        bw.Write(1);
        bw.Write(new byte[44]);
        bw.Write(32);
        bw.Write(0x41u);                // RGB | alpha pixels
        bw.Write(0u);
        bw.Write(32);
        bw.Write(0x00FF0000u);
        bw.Write(0x0000FF00u);
        bw.Write(0x000000FFu);
        bw.Write(0xFF000000u);
        bw.Write(0x1000u);
        bw.Write(new byte[16]);
        for (int i = 0; i < pixels; i++)
            bw.Write(0x40302010u);
        return ms.ToArray();
    }

    /// <summary>A bare GEOM chunk (version 0x0C) with one face block and an empty bone tail.</summary>
    private static byte[] Geom(int vertexCount, (uint Usage, uint SubType, byte Size)[] elements, byte[] vertexData,
        byte bytesPerIndex, uint[] indices, int? indexCount = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x4D4F4547u);          // GEOM
        bw.Write(0x0Cu);
        bw.Write(0u);                   // TGI offset
        bw.Write(0u);                   // TGI size
        bw.Write(0u);                   // no embedded material
        bw.Write(0u);                   // merge group
        bw.Write(0u);                   // sort order
        bw.Write(vertexCount);
        bw.Write(elements.Length);
        foreach (var (usage, subType, size) in elements)
        {
            bw.Write(usage);
            bw.Write(subType);
            bw.Write(size);
        }
        bw.Write(vertexData);
        bw.Write(1);                    // one face block
        bw.Write(bytesPerIndex);
        bw.Write(indexCount ?? indices.Length);
        foreach (var index in indices)
        {
            if (bytesPerIndex == 4) bw.Write(index);
            else bw.Write((ushort)index);
        }
        bw.Write(0);                    // tail block 1
        bw.Write(0);                    // tail block 2
        bw.Write(0);                    // bone count
        return ms.ToArray();
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { action(); }
        catch (Exception) { }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>The input is refused with InvalidDataException and the attempt allocates under 16 MB.</summary>
    private static void RequireCheapRejection(Action action, string name)
    {
        Exception? error = null;
        long allocated = Allocated(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        Require(error is InvalidDataException && allocated < 16 * Megabyte,
            $"{name} is refused cheaply ({allocated / Megabyte} MB, {error?.GetType().Name}: {error?.Message})");
    }
}
