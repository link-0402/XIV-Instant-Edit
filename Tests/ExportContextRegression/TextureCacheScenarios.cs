using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The converted-texture cache hands its entries straight to the mod being written, so an entry that
/// is not whole must never be found.
/// </summary>
internal static class TextureCacheScenarios
{
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "texture-cache-integrity");
        Directory.CreateDirectory(folder);
        var cache = new TextureCache(folder);

        var whole = Texture(4, 4);
        var key = TextureCache.KeyForGenerated("whole");
        Require(cache.Store(key, whole) is { } stored && File.ReadAllBytes(stored).SequenceEqual(whole) &&
                cache.Get(key)!.SequenceEqual(whole) && cache.PathOf(key) == stored,
            "a whole texture is stored, read back and found by path");
        Require(Directory.GetFiles(folder, "*.part").Length == 0, "storing leaves no part file behind");

        var cutKey = TextureCache.KeyForGenerated("cut");
        cache.Store(cutKey, whole[..100]);   // a write that stopped part-way through the mip data
        var cutPath = Path.Combine(folder, $"{cutKey}.tex");
        Require(cache.Get(cutKey) == null && !File.Exists(cutPath),
            "a texture shorter than its header says is a miss and is removed");
        cache.Store(cutKey, whole[..100]);
        Require(cache.PathOf(cutKey) == null && !File.Exists(cutPath),
            "and the same by path, so it is never copied into a mod");

        var stubKey = TextureCache.KeyForGenerated("stub");
        cache.Store(stubKey, new byte[20]);
        Require(cache.Get(stubKey) == null, "a file shorter than a header is a miss");

        var padded = whole.Concat(new byte[16]).ToArray();
        var paddedKey = TextureCache.KeyForGenerated("padded");
        cache.Store(paddedKey, padded);
        Require(cache.Get(paddedKey)!.Length == padded.Length, "a texture with trailing padding is kept");

        var source = Path.Combine(root, "texture-cache-adopt.tex");
        File.WriteAllBytes(source, whole);
        var adoptKey = TextureCache.KeyForGenerated("adopted");
        Require(cache.Adopt(adoptKey, source) is { } adopted && File.ReadAllBytes(adopted).SequenceEqual(whole) && !File.Exists(source) &&
                Directory.GetFiles(folder, "*.part").Length == 0,
            "an adopted file arrives whole, its source goes, and no part file is left");
        Require(cache.Adopt(adoptKey, Path.Combine(root, "missing.tex")) == null,
            "adopting a file that is not there reports failure and leaves the earlier entry alone");
        Require(cache.Get(adoptKey)!.SequenceEqual(whole), "the earlier entry survives a failed adopt");

        var oldPart = Path.Combine(folder, "old.tex.0123.part");
        var freshPart = Path.Combine(folder, "fresh.tex.4567.part");
        File.WriteAllBytes(oldPart, new byte[10]);
        File.WriteAllBytes(freshPart, new byte[10]);
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddDays(-3));
        cache.Prune(long.MaxValue);
        Require(!File.Exists(oldPart) && File.Exists(freshPart),
            "pruning removes part files from a write that did not survive, but not one that may still be running");
    }

    /// <summary>An 80-byte TEX header for an uncompressed 2D BGRA8 texture with one mip, and its pixels.</summary>
    private static byte[] Texture(int width, int height)
    {
        var data = new byte[80 + width * height * 4];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), 0x00800000u);   // 2D
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), 0x1450u);       // BGRA8
        BitConverter.TryWriteBytes(data.AsSpan(8, 2), (ushort)width);
        BitConverter.TryWriteBytes(data.AsSpan(10, 2), (ushort)height);
        BitConverter.TryWriteBytes(data.AsSpan(12, 2), (ushort)1);
        data[14] = 1;
        for (int i = 80; i < data.Length; i++)
            data[i] = (byte)(i * 7);
        return data;
    }
}
