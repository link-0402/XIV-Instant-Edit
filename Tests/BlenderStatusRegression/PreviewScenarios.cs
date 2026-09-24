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
