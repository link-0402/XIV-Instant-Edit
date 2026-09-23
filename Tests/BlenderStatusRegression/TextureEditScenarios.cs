using System.Buffers.Binary;
using System.Text.Json;
using InstantEdit;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data.Files;
using Penumbra.Api.Enums;

internal static class TextureEditScenarios
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("[PASS] texture: " + message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected rejection: " + message);
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "instant-edit-textures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (format, target) in new[]
            {
                (TexFile.TextureFormat.BC1, TextureType.Bc1Tex), (TexFile.TextureFormat.BC3, TextureType.Bc3Tex),
                (TexFile.TextureFormat.BC4, TextureType.Bc4Tex), (TexFile.TextureFormat.BC5, TextureType.Bc5Tex),
                (TexFile.TextureFormat.BC7, TextureType.Bc7Tex), (TexFile.TextureFormat.B8G8R8A8, TextureType.RgbaTex),
            })
            {
                var bytes = Tex((uint)format, 8, 8, 4, 42);
                Check(TextureFiles.ReadTex(bytes).Mips == 4 && TextureFiles.OutputType((uint)format) == target, $"{format} maps to an explicit encoder");
                Reject(() => TextureFiles.ReadTex(bytes[..^1]), $"truncated {format} mip chain rejected");
                await RoundTripAsync(Path.Combine(root, format.ToString()), (uint)format, target);
            }
            Reject(() => TextureFiles.ReadTex(Tex((uint)TexFile.TextureFormat.BC2, 8, 8, 1, 0)), "BC2 is rejected instead of changing compression");
            var invalid = Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 1, 0);
            invalid[3] = 0x02;
            Reject(() => TextureFiles.ReadTex(invalid), "cube/volume layouts rejected");
            var tga = Tga(8, 8, 24);
            TextureFiles.ValidateTga(tga, 8, 8);
            var rle = new byte[23];
            tga.AsSpan(0, 18).CopyTo(rle);
            rle[2] = 10; rle[18] = 0xbf;
            TextureFiles.ValidateTga(rle, 8, 8);
            Reject(() => TextureFiles.ValidateTga(rle[..^1], 8, 8), "truncated RLE rejected");
            rle[18] = 0xff;
            Reject(() => TextureFiles.ValidateTga(rle, 8, 8), "RLE overrun rejected");
            tga[17] = 0x20;
            Reject(() => TextureFiles.ValidateTga(tga, 8, 8), "missing alpha descriptor rejected");
            tga[17] = 0x28; tga[16] = 24;
            Reject(() => TextureFiles.ValidateTga(tga, 8, 8), "24-bit TGA rejected");
            Reject(() => TextureFiles.ValidateTga(Tga(4, 8, 1), 8, 8), "initial conversion must match the captured size");
            Check(TextureFiles.ValidateTga(Tga(4, 12, 1)) == (4, 12), "resized TGA saves report their dimensions");
            Reject(() => TextureFiles.ValidateTga(Tga(0, 8, 1)), "zero-width TGA rejected");
            Reject(() => TextureFiles.ValidateEncodable((uint)TexFile.TextureFormat.BC7, 6, 8), "block-compressed saves need whole 4 × 4 tiles");
            TextureFiles.ValidateEncodable((uint)TexFile.TextureFormat.B8G8R8A8, 6, 7);
            Check(true, "uncompressed saves accept any supported size");
            var rgba = Tex((uint)TexFile.TextureFormat.B8G8R8A8, 8, 8, 1, 0);
            var hash = TextureFiles.PixelHash(rgba);
            rgba[80] = 123; // RGB under alpha zero must participate in no-op detection.
            Check(hash != TextureFiles.PixelHash(rgba), "hidden RGB participates in pixel hashing");
            Check(TextureFiles.PixelHash(Tex((uint)TexFile.TextureFormat.B8G8R8A8, 4, 16, 1, 0)) != hash,
                "same-area resizes with identical bytes still count as changed pixels");
            var namedSession = new TextureEditSession
            {
                CacheRoot = root,
                GamePath = "chara/equipment/e0001/texture/c0101e0001_top_d.tex",
            };
            Check(Path.GetFileName(namedSession.WorkingFile) == "c0101e0001_top_d.tga",
                "working TGA keeps the original texture filename");
            await SessionFailuresAsync(Path.Combine(root, "failures"));
            await ResizeAndEncodingAsync(Path.Combine(root, "resize"));
            await IncompleteSessionAsync(Path.Combine(root, "incomplete"));
            await VanillaAsync(Path.Combine(root, "vanilla"));
            await WatcherAsync(Path.Combine(root, "watcher"));
            await CacheCleanupAsync(Path.Combine(root, "cache-cleanup"));
            AtomicReplacement(Path.Combine(root, "atomic"));
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test root.");
            Directory.Delete(root, true);
        }
    }

    private static void AtomicReplacement(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var target = f.Backend.Target;
        var original = f.Backend.Original;
        var candidate = Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 22);
        var external = Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 33);
        var calls = 0;
        Reject(() => TextureFiles.Replace(target, f.Backend.ModRoot, "Files/chara/test.tex", "Mod", candidate,
            TextureFiles.Hash(original), f.Backups, () =>
            {
                if (++calls == 2) File.WriteAllBytes(target, external);
                return true;
            }, CancellationToken.None), "external edit during final save checks is protected");
        Check(File.ReadAllBytes(target).SequenceEqual(external), "failed atomic commit leaves external file intact");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.tmp").Any(), "failed commits remove staged temporary files");
        File.WriteAllBytes(target, original);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject(() => TextureFiles.Replace(target, f.Backend.ModRoot, "Files/chara/test.tex", "Mod", candidate,
                TextureFiles.Hash(original), f.Backups, () => true, CancellationToken.None), "locked destination rejects atomic replacement");
        }
        Check(File.ReadAllBytes(target).SequenceEqual(original), "replacement failure retains original bytes");
        var bc7 = (uint)TexFile.TextureFormat.BC7;
        var bgra = (uint)TexFile.TextureFormat.B8G8R8A8;
        Reject(() => TextureFiles.ValidateOutput(Tex(bc7, 8, 8, 1, 1), bc7, 8, 8, true), "missing generated mipmaps rejected");
        Reject(() => TextureFiles.ValidateOutput(Tex(bgra, 8, 8, 4, 1), bc7, 8, 8, true), "converted output must use the requested encoding");
        Reject(() => TextureFiles.ValidateOutput(Tex(bc7, 8, 8, 4, 1), bc7, 16, 8, true), "converted output must match the saved size");
        TextureFiles.ValidateOutput(Tex(bc7, 8, 8, 1, 1), bc7, 8, 8, false);
        Check(true, "non-mipmapped originals retain a single surface");
        var s = new TextureEditSession { Format = bc7, Width = 8, Height = 8, MipMaps = true };
        TextureFiles.ValidateCommit(Tex(bgra, 16, 4, 1, 1), s);
        Check(true, "an uncompressed save of any supported size can replace a compressed original");
        Reject(() => TextureFiles.ValidateCommit(Tex((uint)TexFile.TextureFormat.BC1, 8, 8, 4, 1), s), "commits cannot switch to another compressed format");
    }

    private static async Task ResizeAndEncodingAsync(string root)
    {
        var bc7 = (uint)TexFile.TextureFormat.BC7;
        var bgra = (uint)TexFile.TextureFormat.B8G8R8A8;
        using var f = new Fixture(root, bc7);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        Check(s.SavedFormat == bc7, "new sessions record the captured format as saved");

        File.WriteAllBytes(s.WorkingFile, Tga(16, 4, 50));
        await f.Service.ProcessPendingAsync(true);
        var header = TextureFiles.ReadTex(File.ReadAllBytes(s.TargetFile));
        var resized = TextureFiles.Hash(File.ReadAllBytes(s.TargetFile));
        Check(f.Backend.Commits == 1 && header is { Width: 16, Height: 4 } && header.Format == bc7 &&
              header.Mips == TextureFiles.FullMipCount(16, 4) && f.Service.Sessions.Single() is { Width: 16, Height: 4 },
            "a resized save commits at its new resolution with a regenerated mip chain");

        File.WriteAllBytes(s.WorkingFile, Tga(6, 4, 51));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && TextureFiles.Hash(File.ReadAllBytes(s.TargetFile)) == resized,
            "a BC save that is not whole 4 × 4 tiles leaves the destination unchanged");

        f.Config.RecompressTextures = false;
        await f.Service.ProcessPendingAsync(true);
        header = TextureFiles.ReadTex(File.ReadAllBytes(s.TargetFile));
        Check(f.Backend.Commits == 2 && f.Backend.LastFormat == TextureType.RgbaTex && header.Format == bgra &&
              header is { Width: 6, Height: 4 } && f.Service.Sessions.Single().SavedFormat == bgra,
            "with recompression off, saves are written uncompressed at any size");

        await f.Service.RestoreAsync(id);
        Check(TextureFiles.Hash(File.ReadAllBytes(s.TargetFile)) == resized &&
              f.Service.Sessions.Single() is { Paused: true, Width: 16, Height: 4 } restored && restored.SavedFormat == bc7,
            "restoring a backup with a different size and encoding updates the session");
        Check(await f.Service.StartAsync(f.Request, false) == id && !f.Service.Sessions.Single().Paused,
            "opening a paused texture again resumes its session");

        f.Config.RecompressTextures = true;
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 52));
        await f.Service.ProcessPendingAsync(true);
        f.Config.RecompressTextures = false;
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 52).Concat(new byte[] { 1, 2, 3 }).ToArray());
        await f.Service.ProcessPendingAsync(true);
        // Commits: two saves, the restore, then the recompressed and the uncompressed save.
        Check(f.Backend.Commits == 5 && f.Service.Sessions.Single().SavedFormat == bgra,
            "a recompression change applies on the next save even when pixels are unchanged");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 52).Concat(new byte[] { 4, 5, 6 }).ToArray());
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 5, "unchanged pixels and encoding skip recompression");
    }

    private static async Task IncompleteSessionAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        f.Backend.FailConversion = true;
        try { await f.Service.StartAsync(f.Request, false); }
        catch (IOException) { }
        f.Backend.FailConversion = false;
        var broken = f.Service.Sessions.Single().Id;
        var id = await f.Service.StartAsync(f.Request, false);
        Check(id != broken && !f.Service.Sessions.Single(item => item.Id == id).Paused,
            "a session that failed to open is not reused when the texture is opened again");
    }

    private static async Task RoundTripAsync(string root, uint format, TextureType target)
    {
        using var fixture = new Fixture(root, format);
        var id = await fixture.Service.StartAsync(fixture.Request, false);
        var s = fixture.Service.Sessions.Single();
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 77));
        await fixture.Service.ProcessPendingAsync(true);
        Check(fixture.Backend.Commits == 1 && fixture.Backend.LastFormat == target, $"{target} session preserves encoding after an uncompressed TGA save");
        Check(TextureFiles.ReadTex(File.ReadAllBytes(s.TargetFile)).Format == format, "committed format equals captured source format");
        Check(File.Exists(fixture.Service.Sessions.Single().LastBackup), "replacement creates a TEX backup");
        // Metadata-only save: valid TGA footer bytes change, pixel content does not.
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 77).Concat(new byte[] { 1, 2, 3 }).ToArray());
        await fixture.Service.ProcessPendingAsync(true);
        Check(fixture.Backend.Commits == 1, "metadata-only save skips recompression and redraw");
        Check(await fixture.Service.StartAsync(fixture.Request, false) == id, "reopening uses the existing working file");
        await fixture.Service.RestoreAsync(id);
        Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(fixture.Backend.Original), "restore retains the backup bytes exactly");
        Check(fixture.Service.Sessions.Single().Paused && File.ReadAllBytes(s.WorkingFile)[18] == 77, "restore pauses and retains working pixels");
        fixture.Service.Dispose();
        await fixture.Service.Completion;
        using var restored = new TextureEditService(fixture.Backend, fixture.Config, fixture.ConfigDir, fixture.Backups, (_, _) => { }, false);
        Check(restored.Sessions.Single().Paused && restored.Sessions.Single().WorkingFile == s.WorkingFile, "session is restored paused after restart");
        Check(await restored.StartAsync(fixture.Request, false) == id && !restored.Sessions.Single().Paused,
            "reopening a texture after restart resumes its session");
        await restored.DiscardAsync(id);
        Check(!Directory.Exists(s.Directory) && File.Exists(s.TargetFile), "discard removes only working files");
    }

    private static async Task SessionFailuresAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        var original = File.ReadAllBytes(s.TargetFile);
        File.WriteAllBytes(s.WorkingFile, [1, 2, 3]);
        await f.Service.ProcessPendingAsync(true);
        Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(original), "partial saves do not touch destination");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 31));
        using (var locked = new FileStream(s.WorkingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 0, "locked save is retried without committing");
        f.Backend.FailConversion = true;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 0 && File.ReadAllBytes(s.TargetFile).SequenceEqual(original), "conversion failure retains original");
        f.Backend.FailConversion = false;
        f.Backend.OnEncode = () => File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 32));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 0, "new save during conversion supersedes old result");
        f.Backend.OnEncode = null;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1, "latest stable save eventually commits");
        f.Backend.OnEncode = () => { _ = f.Service.SetPausedAsync(id, true); };
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 33));
        await f.Service.ProcessPendingAsync(true);
        await f.Service.SetPausedAsync(id, true);
        Check(f.Backend.Commits == 1, "pause cancels an in-flight conversion");
        f.Backend.OnEncode = null;
        await f.Service.SetPausedAsync(id, false);
        f.Backend.RefreshWarning = true;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 2 && f.Service.Sessions.Single().Status.Contains("attention"), "redraw failures are separate from successful commits");
        // A destination change outside the session must not be overwritten.
        File.WriteAllBytes(s.TargetFile, Tex(s.Format, 8, 8, 4, 99));
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 34));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 2 && f.Service.Sessions.Single().Conflict && f.Service.Sessions.Single().Paused, "external edits pause with a conflict");
        var cache2 = Path.Combine(root, "cache2");
        MakeCache(cache2);
        f.Config.TextureCacheDirectory = cache2;
        Check(f.Service.EnsureConfiguredCache() == Path.Combine(cache2, TextureFiles.CacheFolder) &&
              f.Service.Sessions.Single().CacheRoot != Path.Combine(cache2, TextureFiles.CacheFolder),
            "cache changes leave existing sessions in place");
        Check(f.Config.TextureCacheDirectory == cache2, "the plugin retains its central cache location while Blender is offline");
    }

    private static async Task VanillaAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, vanilla: true);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        Check(!Directory.Exists(s.ModRoot), "vanilla session does not create a mod before the first save");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 41));
        await f.Service.ProcessPendingAsync(true);
        var saved = f.Service.Sessions.Single();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.ModRoot, "meta.json")));
        var rootMetadata = metadata.RootElement;
        Check(!saved.NeedsMod && rootMetadata.GetProperty("FileVersion").GetInt32() == 4 &&
              rootMetadata.GetProperty("Identifier").TryGetGuid(out _) &&
              rootMetadata.GetProperty("LastWrite").TryGetDateTimeOffset(out _) &&
              rootMetadata.GetProperty("DefaultData").GetProperty("Files").GetProperty(s.GamePath).GetString() == s.RelativePath &&
              rootMetadata.GetProperty("Groups").GetArrayLength() == 0 &&
              !File.Exists(Path.Combine(saved.ModRoot, "default_mod.json")) &&
              Directory.GetFiles(saved.ModRoot, "group_*.json").Length == 0,
            "first vanilla save creates one v4 meta.json with the captured texture mapping");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 42));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 2, "subsequent vanilla saves reuse the same mod");
        var v4Fingerprint = PenumbraService.TextureMappingFingerprint(saved.ModRoot);
        File.WriteAllText(Path.Combine(saved.ModRoot, "default_mod.json"), "{\"ignored\":true}");
        Check(PenumbraService.TextureMappingFingerprint(saved.ModRoot) == v4Fingerprint,
            "texture mapping fingerprints ignore stray legacy JSON files");
        Check(await f.Service.StartAsync(f.Request, false) == id, "vanilla reopening reuses the session");
    }

    private static async Task WatcherAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, watch: true);
        await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        var temp = Path.Combine(s.Directory, "editor-save.tmp");
        File.WriteAllBytes(temp, Tga(8, 8, 61));
        File.Move(temp, s.WorkingFile, true);
        for (var i = 0; i < 60 && f.Backend.Commits == 0; i++) await Task.Delay(100);
        Check(f.Backend.Commits == 1, "directory watcher catches rename-based editor saves");
        f.Service.Dispose();
        await f.Service.Completion;
    }

    private static async Task CacheCleanupAsync(string root)
    {
        using (var f = new Fixture(Path.Combine(root, "stale"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 1 && !Directory.Exists(s.Directory),
                "automatic cleanup removes stale paused texture sessions");
        }

        using (var f = new Fixture(Path.Combine(root, "unsaved"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 99));
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 0 && Directory.Exists(s.Directory),
                "automatic cleanup retains stale sessions with unsaved TGA changes");
            await f.Service.DiscardAsync(s.Id);
        }

        using (var f = new Fixture(Path.Combine(root, "artist-source"), (uint)TexFile.TextureFormat.BC7))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            File.WriteAllBytes(Path.Combine(s.Directory, "artist-source.psd"), [1, 2, 3]);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 0 && File.Exists(Path.Combine(s.Directory, "artist-source.psd")),
                "automatic cleanup retains stale sessions with artist source documents");
            await f.Service.DiscardAsync(s.Id);
        }

        using (var f = new Fixture(Path.Combine(root, "disabled"), (uint)TexFile.TextureFormat.BC7))
        {
            f.Config.AutomaticCacheCleanup = false;
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            SetStale(s.Directory);
            Check(await f.Service.CleanupStaleSessionsAsync() == 0 && Directory.Exists(s.Directory),
                "disabled automatic cleanup retains stale texture sessions");
            await f.Service.DiscardAsync(s.Id);
        }

        using (var f = new Fixture(Path.Combine(root, "background"), (uint)TexFile.TextureFormat.BC7,
            watch: true, cleanupInterval: TimeSpan.FromMilliseconds(100)))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            await f.Service.SetPausedAsync(s.Id, true);
            SetStale(s.Directory);
            for (var i = 0; i < 20 && Directory.Exists(s.Directory); i++) await Task.Delay(100);
            Check(!Directory.Exists(s.Directory), "background cleanup removes stale paused texture sessions");
        }
    }

    private static void SetStale(string directory)
    {
        var stale = DateTime.UtcNow - TimeSpan.FromDays(2);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(path, stale);
        Directory.SetLastWriteTimeUtc(directory, stale);
    }

    private static string MakeCache(string root)
    {
        Directory.CreateDirectory(root);
        return TextureFiles.EnsureCacheRoot(root);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TextureEditService Service;
        public readonly FakeBackend Backend;
        public readonly Configuration Config;
        public readonly ModelBackupStore Backups;
        public readonly string ConfigDir;
        public readonly TextureEditRequest Request;
        public Fixture(string root, uint format, bool vanilla = false, bool watch = false, TimeSpan? cleanupInterval = null)
        {
            ConfigDir = Path.Combine(root, "config"); Directory.CreateDirectory(ConfigDir);
            var cacheDirectory = Path.Combine(root, "cache");
            MakeCache(cacheDirectory);
            Config = new Configuration { TextureCacheDirectory = cacheDirectory };
            Backups = new ModelBackupStore(ConfigDir);
            Backend = new FakeBackend(root, format, Backups, vanilla);
            Request = new TextureEditRequest("chara/test.tex", vanilla ? "chara/test.tex" : Backend.Target,
                vanilla ? "" : "Mod", Backend.ModRoot, "Files/chara/test.tex", null, 0, vanilla ? "Mod" : "");
            Service = new TextureEditService(Backend, Config, ConfigDir, Backups, (_, _) => { }, watch, cleanupInterval);
        }
        public void Dispose() => Service.Dispose();
    }

    // This fixture tests orchestration and validation, not the real Penumbra/Photoshop codecs.
    private sealed class FakeBackend : ITextureEditBackend
    {
        public readonly string ModRoot;
        public readonly string Target;
        public readonly byte[] Original;
        private readonly ModelBackupStore _backups;
        private readonly bool _vanilla;
        public int Commits;
        public TextureType LastFormat;
        public bool FailConversion, RefreshWarning;
        public Action? OnEncode;
        public FakeBackend(string root, uint format, ModelBackupStore backups, bool vanilla)
        {
            _backups = backups; _vanilla = vanilla;
            ModRoot = Path.Combine(root, "Mod"); Target = Path.Combine(ModRoot, "Files", "chara", "test.tex");
            Original = Tex(format, 8, 8, 4, 12);
            if (!vanilla)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
                File.WriteAllBytes(Target, Original);
            }
        }
        public Task<TextureSource> CaptureAsync(TextureEditRequest request, CancellationToken token)
        {
            var h = TextureFiles.ReadTex(Original);
            return Task.FromResult(new TextureSource(Original, new TextureEditSession
            {
                GamePath = request.GamePath, ModDirectory = "Mod", ModRoot = ModRoot, RelativePath = "Files/chara/test.tex",
                NewModName = request.NewModName, NeedsMod = _vanilla, CollectionId = Guid.NewGuid(),
                Format = h.Format, Width = 8, Height = 8, MipMaps = true, LastCommittedHash = _vanilla ? "" : TextureFiles.Hash(Original),
            }));
        }
        public Task ConvertAsync(string input, string output, TextureType format, bool mipMaps)
        {
            if (FailConversion) throw new IOException("Simulated converter failure");
            if (format == TextureType.Targa) File.WriteAllBytes(output, Tga(8, 8, 12));
            else
            {
                var tga = File.ReadAllBytes(input);
                var code = format switch
                {
                    TextureType.Bc1Tex => TexFile.TextureFormat.BC1, TextureType.Bc3Tex => TexFile.TextureFormat.BC3,
                    TextureType.Bc4Tex => TexFile.TextureFormat.BC4, TextureType.Bc5Tex => TexFile.TextureFormat.BC5,
                    TextureType.Bc7Tex => TexFile.TextureFormat.BC7, _ => TexFile.TextureFormat.B8G8R8A8,
                };
                var (width, height) = TextureFiles.ValidateTga(tga);
                File.WriteAllBytes(output, Tex((uint)code, width, height, mipMaps ? TextureFiles.FullMipCount(width, height) : 1, tga[18]));
                if (mipMaps) { LastFormat = format; OnEncode?.Invoke(); }
            }
            return Task.CompletedTask;
        }
        public Task<TextureCommit> CommitAsync(TextureEditSession s, byte[] tex, Func<bool> current, CancellationToken token)
        {
            var h = TextureFiles.ReadTex(tex);
            if (!TextureFiles.IsSessionFormat(h.Format, s)) throw new IOException("Incorrect format");
            if (!current()) throw new OperationCanceledException();
            string backup = "";
            if (s.NeedsMod)
            {
                Directory.CreateDirectory(ModRoot);
                PenumbraService.StageGameTextureMod(ModRoot, "Mod", s.GamePath, tex);
                s.NeedsMod = false;
            }
            else
            {
                if (TextureFiles.Hash(File.ReadAllBytes(Target)) != s.LastCommittedHash) throw new TextureConflictException("Destination changed");
                backup = TextureFiles.Replace(Target, ModRoot, s.RelativePath, "Mod", tex, s.LastCommittedHash, _backups, current, token);
            }
            Interlocked.Increment(ref Commits);
            return Task.FromResult(new TextureCommit(TextureFiles.Hash(tex), backup, "Saved"));
        }
        public Task<string> RefreshAsync(TextureEditSession s, CancellationToken token) => Task.FromResult(RefreshWarning ? "Texture saved; refresh needs attention" : "Saved and redrawn");
    }

    private static byte[] Tga(int width, int height, byte value)
    {
        var bytes = new byte[18 + width * height * 4];
        bytes[2] = 2; bytes[16] = 32; bytes[17] = 0x28;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)height);
        bytes.AsSpan(18).Fill(value);
        return bytes;
    }
    private static byte[] Tex(uint format, int width, int height, int mips, byte value)
    {
        var sizes = Enumerable.Range(0, mips).Select(m => format == (uint)TexFile.TextureFormat.B8G8R8A8
            ? Math.Max(1, width >> m) * Math.Max(1, height >> m) * 4
            : ((Math.Max(1, width >> m) + 3) / 4) * ((Math.Max(1, height >> m) + 3) / 4) *
              (format == (uint)TexFile.TextureFormat.BC1 || format == (uint)TexFile.TextureFormat.BC4 ? 8 : 16)).ToArray();
        var bytes = new byte[80 + sizes.Sum()];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)height);
        bytes[12] = 1; bytes[14] = (byte)mips;
        var offset = 80;
        for (var m = 0; m < mips; m++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + m * 4), (uint)offset); offset += sizes[m]; }
        bytes.AsSpan(80).Fill(value);
        return bytes;
    }
}
