using InstantEdit.Services.Previews;
using Lumina.Data.Files;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The preview cache policy (LRU, byte budget, invalidation, failure caching, disposal with loads
/// in flight, concurrency cap) and the CPU texture decoder, without a GPU or Dalamud.
/// </summary>
internal static class PreviewScenarios
{
    private sealed class Fake(long size) : IDisposable
    {
        public long Size { get; } = size;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static PreviewKey Key(string path, long size = 0) => new(path, false, size, 0);

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    public static async Task RunAsync()
    {
        await CachePolicyAsync();
        DecoderChecks();
        ModelChecks();
    }

    /// <summary>
    /// A minimal Dawntrail model: one mesh of a unit cube with a float3 position stream, one
    /// material, two attributes and one bone. Layout: file header | vertex declaration | string
    /// table | mesh header | 3 LODs | mesh | attribute offsets | material offset | bone offset |
    /// vertex buffer | index buffer.
    /// </summary>
    private static byte[] SyntheticModel(out int meshTableOffset)
    {
        var strings = new[] { "atr_a", "atr_b", "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl", "j_kosi" };
        var stringBytes = strings.SelectMany(s => System.Text.Encoding.UTF8.GetBytes(s + "\0")).ToArray();
        var stringOffsets = new int[strings.Length];
        for (int i = 0, at = 0; i < strings.Length; i++)
        {
            stringOffsets[i] = at;
            at += System.Text.Encoding.UTF8.GetByteCount(strings[i]) + 1;
        }

        const int header = 68, declaration = 136, stringHeader = 8, meshHeader = 56, lod = 60, mesh = 36;
        var stringTable = header + declaration;
        var meshHeaderAt = stringTable + stringHeader + stringBytes.Length;
        var lods = meshHeaderAt + meshHeader;
        var meshes = lods + 3 * lod;
        var attributes = meshes + mesh;
        var materials = attributes + 2 * 4;
        var bones = materials + 4;
        var vertexData = bones + 4;
        var indexData = vertexData + 8 * 12;
        var bytes = new byte[indexData + 36 * 2];
        meshTableOffset = meshes;

        void U16(int at, int value) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 2), (ushort)value);
        void U32(int at, uint value) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), value);
        void F32(int at, float value) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), value);

        U32(0, 0x01000006u);
        U16(12, 1);
        U16(14, 1);
        U32(16, (uint)vertexData);
        U32(28, (uint)indexData);
        U32(40, 8 * 12);
        U32(52, 36 * 2);
        bytes[64] = 1;
        // Position element: stream 0, offset 0, type 2 (float3), usage 0 (position); then the terminator.
        bytes[header + 2] = 2;
        bytes[header + 8] = 0xFF;
        U16(stringTable, strings.Length);
        U32(stringTable + 4, (uint)stringBytes.Length);
        stringBytes.CopyTo(bytes, stringTable + stringHeader);
        F32(meshHeaderAt, 1.5f);
        U16(meshHeaderAt + 4, 1);
        U16(meshHeaderAt + 6, 2);
        U16(meshHeaderAt + 10, 1);
        U16(meshHeaderAt + 12, 1);
        bytes[meshHeaderAt + 22] = 1;
        U16(lods, 0);
        U16(lods + 2, 1);
        U16(meshes, 8);
        U32(meshes + 4, 36);
        U16(meshes + 8, 0);
        U32(meshes + 16, 0);
        U32(meshes + 20, 0);
        bytes[meshes + 32] = 12;
        bytes[meshes + 35] = 1;
        U32(attributes, (uint)stringOffsets[0]);
        U32(attributes + 4, (uint)stringOffsets[1]);
        U32(materials, (uint)stringOffsets[2]);
        U32(bones, (uint)stringOffsets[3]);
        for (var v = 0; v < 8; v++)
        {
            F32(vertexData + v * 12, v & 1);
            F32(vertexData + v * 12 + 4, (v >> 1) & 1);
            F32(vertexData + v * 12 + 8, (v >> 2) & 1);
        }
        int[] cube =
        [
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
            2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        ];
        for (var i = 0; i < cube.Length; i++)
            U16(indexData + i * 2, cube[i]);
        return bytes;
    }

    private static void ModelChecks()
    {
        var model = SyntheticModel(out var meshTable);
        var info = ModelInfoReader.Read(model);
        Require(info.IsV6 && info.VersionLabel == "V6" && info.LodCount == 1 && info.MeshCount == 1 && info.Lod0Vertices == 8 && info.Lod0Indices == 36
                && info.Lod0Triangles == 12 && info.Materials.Count == 1 && info.Materials[0].EndsWith("top_a.mtrl", StringComparison.Ordinal)
                && info.Attributes.SequenceEqual(["atr_a", "atr_b"]) && info.BoneCount == 1 && info.ShapeCount == 0
                && Math.Abs(info.Radius - 1.5f) < 1e-6 && !info.Partial && info.Note is null,
            "the model info reader reads the header, string table, LOD 0 totals and attribute names");
        var truncated = ModelInfoReader.Read(model[..(meshTable + 10)]);
        Require(truncated.Partial && truncated.Note is not null && truncated.MeshCount == 1 && truncated.IsV6,
            "a truncated model yields a partial read instead of an exception");
        var ancient = new byte[80];
        BitConverter.TryWriteBytes(ancient.AsSpan(0, 4), 0x01000005u);
        Require(ModelInfoReader.Read(ancient).VersionLabel == "V5" && ModelInfoReader.Read([1, 2, 3]).Partial,
            "unsupported versions and junk are reported, never thrown");

        var geometry = ModelGeometryReader.Read(model);
        Require(geometry.Positions.Length == 8 && geometry.Indices.Length == 36 && geometry.TriangleCount == 12 && geometry.Meshes.Length == 1
                && geometry.Meshes[0] == (0, 36, 0) && geometry.Min == System.Numerics.Vector3.Zero && geometry.Max == System.Numerics.Vector3.One,
            "the geometry reader returns LOD 0 positions, triangles and bounds");
        Reject(() => ModelGeometryReader.Read(model[..^4]), "a truncated index buffer is rejected");
        var v5 = (byte[])model.Clone();
        BitConverter.TryWriteBytes(v5.AsSpan(0, 4), 0x01000005u);
        var refused = false;
        try { ModelGeometryReader.Read(v5); } catch (NotSupportedException) { refused = true; }
        Require(refused, "pre-Dawntrail models get no thumbnail");

        var pixels = ModelThumbnailRenderer.Render(geometry, 64, supersample: false);
        int minX = 64, maxX = -1, minY = 64, maxY = -1, opaque = 0;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                if (pixels[(y * 64 + x) * 4 + 3] == 0) continue;
                opaque++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        Require(opaque > 200 && minX >= 3 && minY >= 3 && maxX <= 60 && maxY <= 60 && maxX - minX >= 30,
            "the rasteriser draws the cube fitted inside the margins");
        var smooth = ModelThumbnailRenderer.Render(geometry, 32);
        Require(smooth.Length == 32 * 32 * 4 && smooth.Where((_, i) => i % 4 == 3).Any(a => a == 255) && smooth.Where((_, i) => i % 4 == 3).Any(a => a is > 0 and < 255),
            "supersampling produces soft edges");
        var empty = new ModelGeometry([], [], [], System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero);
        Require(ModelThumbnailRenderer.Render(empty, 16, false).All(b => b == 0), "empty geometry renders fully transparent");
    }

    private static async Task CachePolicyAsync()
    {
        var loads = new Dictionary<string, TaskCompletionSource<Fake>>(StringComparer.Ordinal);
        var loadCalls = 0;
        var disposed = new List<Fake>();
        Task<Fake> Load(PreviewKey key, CancellationToken token)
        {
            lock (loads)
            {
                loadCalls++;
                // Deliberately ignores cancellation, so a value can still arrive after invalidation or disposal.
                var source = new TaskCompletionSource<Fake>(TaskCreationOptions.RunContinuationsAsynchronously);
                loads[key.Path + "#" + key.Size] = source;
                return source.Task;
            }
        }
        void Complete(string path, long size, Fake value)
        {
            TaskCompletionSource<Fake> source;
            lock (loads) source = loads[path + "#" + size];
            source.TrySetResult(value);
        }

        var cache = new AsyncPreviewCache<Fake>(Load, fake => fake.Size, fake => { fake.Dispose(); lock (disposed) disposed.Add(fake); },
            maxEntries: 2, byteBudget: 10, maxConcurrent: 1);

        var a = cache.Get(Key("a", 1));
        Require(a.State == PreviewState.Loading && ReferenceEquals(a, cache.Get(Key("a", 1))) && loadCalls == 1,
            "a missing key starts one load and returns the same loading entry afterwards");
        Complete("a", 1, new Fake(4));
        await WaitFor(() => a.State == PreviewState.Ready, "a to load");
        Require(a.Value is { Size: 4 } && a.Bytes == 4 && cache.Bytes == 4, "a completed load becomes ready with its byte size");

        var b = cache.Get(Key("b", 1));
        Require(loadCalls == 2, "a second key starts a second load");
        var c = cache.Get(Key("c", 1));
        Require(cache.Count == 2 && !cache.TryPeek(Key("a", 1), out _) && a.State == PreviewState.Failed,
            "exceeding the entry limit evicts the least recently used entry");
        cache.Pump();
        Require(disposed.Count == 1 && disposed[0].Size == 4, "an evicted value is disposed on the next pump");
        Require(loadCalls == 2, "the concurrency cap holds the third load until a slot frees up");
        Complete("b", 1, new Fake(6));
        await WaitFor(() => b.State == PreviewState.Ready, "b to load");
        await WaitFor(() => { lock (loads) return loads.ContainsKey("c#1"); }, "the queued load to start");
        Require(loadCalls == 3, "the queued load starts once the slot frees up");
        Complete("c", 1, new Fake(6));
        await WaitFor(() => c.State == PreviewState.Ready, "c to load");
        await WaitFor(() => cache.Bytes <= 10, "the byte budget to apply");
        Require(cache.Count == 1 && c.State == PreviewState.Ready && b.State == PreviewState.Failed,
            "exceeding the byte budget evicts older entries but never the newest");

        var d = cache.Get(Key("d", 1));
        var invalidated = cache.Invalidate("d");
        Require(invalidated == 1 && d.State == PreviewState.Failed && !cache.TryPeek(Key("d", 1), out _),
            "invalidating a path removes its entries, loading ones included");
        Complete("d", 1, new Fake(1));
        await WaitFor(() => { cache.Pump(); lock (disposed) return disposed.Any(f => f.Size == 1); }, "the late value to be disposed");
        Require(true, "a load that completes after invalidation is disposed instead of cached");

        var failing = new AsyncPreviewCache<Fake>((_, _) => throw new IOException("boom"), f => f.Size, f => f.Dispose());
        var e = failing.Get(Key("e"));
        await WaitFor(() => e.State == PreviewState.Failed, "the failing load");
        Require(e.Error == "boom" && ReferenceEquals(e, failing.Get(Key("e"))), "a failed load is cached and not retried for the same key");
        Require(!ReferenceEquals(e, failing.Get(Key("e", 2))), "a changed key (size or time) loads again");
        failing.Invalidate("e");
        Require(!ReferenceEquals(e, failing.Get(Key("e"))), "invalidation allows a failed key to load again");
        failing.Dispose();

        var late = cache.Get(Key("late", 1));
        cache.Dispose();
        Require(late.State == PreviewState.Failed && cache.Get(Key("x")).State == PreviewState.Failed, "a disposed cache fails every entry");
        Complete("late", 1, new Fake(2));
        await WaitFor(() => { cache.Pump(); lock (disposed) return disposed.Any(f => f.Size == 2); }, "the in-flight value to be disposed after dispose");
        Require(true, "a load finishing after Dispose releases its value");
    }

    private static void DecoderChecks()
    {
        var bgra = (uint)TexFile.TextureFormat.B8G8R8A8;
        var flatSource = TextureEditScenarios.Tex(bgra, 8, 8, 1, 42);
        var flat = TextureDecoder.Decode(flatSource);
        Require(flat.Width == 8 && flat.Height == 8 && flat.SourceWidth == 8 && flat.Bgra.Length == 256 && flat.Format == bgra && flat.Mips == 1
                && !flat.Downscaled && flat.Bgra.SequenceEqual(flatSource[80..(80 + 256)]),
            "an uncompressed texture decodes to its own pixels");
        var scaledSource = TextureEditScenarios.Tex(bgra, 8, 4, 1, 7);
        var scaled = TextureDecoder.Decode(scaledSource, maxEdge: 4);
        var expectedScaled = TextureDecoder.Downscale(scaledSource[80..(80 + 128)], 8, 4, 4).Pixels;
        Require(scaled.Width == 4 && scaled.Height == 2 && scaled.SourceWidth == 8 && scaled.SourceHeight == 4 && scaled.Downscaled
                && scaled.Bgra.SequenceEqual(expectedScaled),
            "previews larger than the edge limit are halved until they fit");
        var compressed = TextureDecoder.Decode(TextureEditScenarios.Tex((uint)TexFile.TextureFormat.BC7, 16, 16, 3, 0));
        Require(compressed.Width == 16 && compressed.Height == 16 && compressed.Mips == 3 && compressed.Bgra.Length == 16 * 16 * 4,
            "block-compressed textures decode through Lumina");
        Reject(() => TextureDecoder.Decode(TextureEditScenarios.Tex(bgra, 8, 8, 1, 1)[..^1]), "truncated textures are rejected");
        var cube = TextureEditScenarios.Tex(bgra, 8, 8, 1, 1);
        cube[3] = 0x02;
        var rejected = false;
        try { TextureDecoder.Decode(cube); } catch (NotSupportedException) { rejected = true; }
        Require(rejected, "cube and volume textures are not previewed");

        var checker = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var v = (byte)((x + y) % 2 == 0 ? 0 : 100);
                var i = (y * 4 + x) * 4;
                checker[i] = v; checker[i + 1] = v; checker[i + 2] = v; checker[i + 3] = 200;
            }
        var (pixels, width, height) = TextureDecoder.Downscale(checker, 4, 4, 2);
        Require(width == 2 && height == 2 && pixels.Length == 16 && pixels[0] == 50 && pixels[3] == 200,
            "box downscaling averages colour and keeps alpha");
        var same = TextureDecoder.Downscale(checker, 4, 4, 4);
        Require(ReferenceEquals(same.Pixels, checker), "images within the limit are returned untouched");
        var alpha = TextureDecoder.AlphaOnly([1, 2, 3, 200, 9, 9, 9, 0]);
        Require(alpha.SequenceEqual(new byte[] { 200, 200, 200, 255, 0, 0, 0, 255 }), "the alpha view is opaque greyscale");
        Require(TextureDecoder.FormatLabel(bgra) == "BGRA32" && TextureDecoder.FormatLabel(0x1234) == "0x1234",
            "format labels fall back to the raw code");
    }
}
