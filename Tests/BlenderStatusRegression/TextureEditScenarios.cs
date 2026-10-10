using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            await VariantsAsync(Path.Combine(root, "variants"));
            await VanillaVariantAsync(Path.Combine(root, "vanilla-variant"));
            await WatcherAsync(Path.Combine(root, "watcher"));
            await UnchangedFilesAsync(Path.Combine(root, "unchanged"));
            await RestartAsync(Path.Combine(root, "restart"));
            await MetadataRewritesAsync(Path.Combine(root, "metadata"));
            await WaitingAsync(Path.Combine(root, "waiting"));
            await WaitingRetriesAsync(Path.Combine(root, "waiting-retries"));
            await UnlistedModPauseAsync(Path.Combine(root, "unlisted-mod"));
            await CacheCleanupAsync(Path.Combine(root, "cache-cleanup"));
            await DiscardAllAsync(Path.Combine(root, "discard-all"));
            await SharedTexturesAsync(Path.Combine(root, "shared"));
            AtomicReplacement(Path.Combine(root, "atomic"));
            await PainterBatchAsync(Path.Combine(root, "painter"));
            PainterJobMod(Path.Combine(root, "painter-mod"));
            await MisplacedMipsAsync(Path.Combine(root, "misplaced-mips"));
            await DroppedImageAsync(Path.Combine(root, "dropped"));
            await DroppedVanillaImageAsync(Path.Combine(root, "dropped-vanilla"));
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
        Check(restored.Sessions.Single().Paused && restored.Sessions.Single().WorkingFile == s.WorkingFile, "a session paused by its restore stays paused after a restart");
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
        var v4Fingerprint = PenumbraService.TextureMappingFingerprint(saved.ModRoot, saved.RelativePath);
        File.WriteAllText(Path.Combine(saved.ModRoot, "default_mod.json"), "{\"ignored\":true}");
        Check(PenumbraService.TextureMappingFingerprint(saved.ModRoot, saved.RelativePath) == v4Fingerprint,
            "texture mapping fingerprints ignore stray legacy JSON files");
        Check(await f.Service.StartAsync(f.Request, false) == id, "vanilla reopening reuses the session");
    }

    private static async Task VariantsAsync(string root)
    {
        var bc7 = (uint)TexFile.TextureFormat.BC7;
        using var f = new Fixture(root, bc7, withMeta: true);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        Check(TextureEditService.IsVariantFileName(s, "Red.tga") && !TextureEditService.IsVariantFileName(s, "test.tga") &&
              !TextureEditService.IsVariantFileName(s, "snapshot.tga") && !TextureEditService.IsVariantFileName(s, "Red.png"),
            "only differently named TGAs beside the working image are variants");

        File.WriteAllBytes(Path.Combine(s.Directory, "Red.tga"), Tga(8, 8, 70));
        await f.Service.ProcessPendingAsync(true);
        s = f.Service.Sessions.Single();
        var red = s.Variants.Single();
        var redTarget = s.VariantTargetFile(red);
        Check(red is { Name: "Red", RelativePath: "Files/chara/test_Red.tex" } && File.Exists(redTarget) &&
              TextureFiles.ReadTex(File.ReadAllBytes(redTarget)).Format == bc7 &&
              File.ReadAllBytes(s.TargetFile).SequenceEqual(f.Backend.Original),
            "a variant TGA is encoded to its own TEX beside the original, which stays untouched");
        var group = VariantGroup(s);
        var options = group.GetProperty("Options");
        Check(group.GetProperty("Type").GetString() == "Single" && group.GetProperty("Name").GetString() == "test variants" &&
              group.GetProperty("DefaultSettings").GetInt32() == 0 && options.GetArrayLength() == 2 &&
              options[0].GetProperty("Name").GetString() == "Original" && !options[0].TryGetProperty("Files", out _) &&
              options[1].GetProperty("Name").GetString() == "Red" &&
              options[1].GetProperty("Files").GetProperty("chara/test.tex").GetString() == red.RelativePath,
            "the variant becomes an option mapping the original game path, beside an empty Original option");
        Check(f.Backend.LastShown == red.OptionId && s.OriginalOptionId is not null,
            "saving a variant selects its option");

        File.WriteAllBytes(Path.Combine(s.Directory, "Red.tga"), Tga(8, 8, 71));
        await f.Service.ProcessPendingAsync(true);
        s = f.Service.Sessions.Single();
        Check(f.Backend.VariantCommits == 2 && s.Variants.Single().RelativePath == red.RelativePath &&
              VariantGroup(s).GetProperty("Options").GetArrayLength() == 2 && File.ReadAllBytes(redTarget)[80] == 71,
            "saving a variant again replaces its TEX without adding options");

        File.WriteAllBytes(Path.Combine(s.Directory, "Blue.tga"), Tga(8, 8, 72));
        File.WriteAllBytes(Path.Combine(s.Directory, "Original.tga"), Tga(8, 8, 73));
        await f.Service.ProcessPendingAsync(true);
        s = f.Service.Sessions.Single();
        using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(s.ModRoot, "meta.json"))))
            Check(metadata.RootElement.GetProperty("Groups").GetArrayLength() == 1 &&
                  VariantGroup(s).GetProperty("Options").GetArrayLength() == 3 &&
                  s.Variants.Select(v => v.Name).Order().SequenceEqual(["Blue", "Red"]),
                "further variants join the same group; the reserved Original name is refused");

        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 74));
        await f.Service.ProcessPendingAsync(true);
        s = f.Service.Sessions.Single();
        Check(f.Backend.Commits == 1 && !s.Conflict && f.Backend.LastShown == s.OriginalOptionId,
            "the main image still saves after its variant group changed meta.json, and shows Original");
        Check(TextureEditService.IsReusableFor(s, f.Request with { ActualPath = redTarget }),
            "a texture showing a variant reopens its session");

        f.Service.Dispose();
        await f.Service.Completion;
        using var restored = new TextureEditService(f.Backend, f.Config, f.ConfigDir, f.Backups, (_, _) => { }, false);
        Check(restored.Sessions.Single().Variants.Count == 2 && restored.Sessions.Single().VariantGroupId == s.VariantGroupId,
            "variants survive a restart");
        await restored.DiscardAsync(id);
        Check(File.Exists(redTarget), "discard keeps committed variant files");
    }

    private static async Task VanillaVariantAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, vanilla: true);
        await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        File.WriteAllBytes(Path.Combine(s.Directory, "Red.tga"), Tga(8, 8, 80));
        await f.Service.ProcessPendingAsync(true);
        s = f.Service.Sessions.Single();
        Check(!s.NeedsMod && File.ReadAllBytes(s.TargetFile).SequenceEqual(f.Backend.Original) &&
              s.Variants.Single().RelativePath == "Files/chara/test_Red.tex" && VariantGroup(s).GetProperty("Options").GetArrayLength() == 2,
            "a variant of a vanilla texture creates the mod with the unchanged original, then adds the variant");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 81));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 2 && File.ReadAllBytes(f.Service.Sessions.Single().TargetFile)[80] == 81,
            "the vanilla session's main image then saves into that mod");
    }

    private static JsonElement VariantGroup(TextureEditSession s)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(s.ModRoot, "meta.json")));
        return metadata.RootElement.GetProperty("Groups").EnumerateArray()
            .Single(group => group.GetProperty("Id").GetGuid() == s.VariantGroupId).Clone();
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

    /// <summary>
    /// A scan reads a working or variant TGA only when its size or write time changed, so idle sessions
    /// cost nothing. Saves the watcher missed still change them, and a resumed session reads again.
    /// </summary>
    private static async Task UnchangedFilesAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, withMeta: true);
        await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        var red = Path.Combine(s.Directory, "Red.tga");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 56));
        File.WriteAllBytes(red, Tga(8, 8, 75));
        await f.Service.ProcessPendingAsync(true);
        // Keeps the size and write time, so only reading the file notices the change.
        void Rewrite(string path, byte value)
        {
            var written = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, Tga(8, 8, value));
            File.SetLastWriteTimeUtc(path, written);
        }
        Rewrite(s.WorkingFile, 57);
        Rewrite(red, 76);
        // Each session is scanned at most every 2 seconds.
        await Task.Delay(2100);
        await f.Service.ProcessPendingAsync();
        Check(f.Backend.Commits == 1 && f.Backend.VariantCommits == 1,
            "a scan doesn't read a working or variant TGA whose size and write time haven't changed");
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 2 && f.Backend.VariantCommits == 2 && File.ReadAllBytes(f.Backend.Target)[80] == 57,
            "a forced scan reads them anyway");

        // This fixture has no watcher, as if it missed these saves.
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 58));
        File.WriteAllBytes(red, Tga(8, 8, 77));
        await Task.Delay(2100);
        await f.Service.ProcessPendingAsync();
        Check(f.Backend.Commits == 3 && f.Backend.VariantCommits == 3 && File.ReadAllBytes(f.Backend.Target)[80] == 58,
            "saves the watcher missed go through, since they change the write time");

        // With the catalog locked, the failed save can't be stored either, which pauses the session.
        f.Backend.FailConversion = true;
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 59));
        using (new FileStream(Path.Combine(f.ConfigDir, "TextureSessions.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            await f.Service.ProcessPendingAsync(true);
        f.Backend.FailConversion = false;
        Check(f.Service.Sessions.Single().Paused, "a save that fails while session storage fails too pauses the session");
        await f.Service.StartAsync(f.Request, false);
        await Task.Delay(2100);
        await f.Service.ProcessPendingAsync();
        Check(f.Backend.Commits == 4 && File.ReadAllBytes(f.Backend.Target)[80] == 59 && !f.Service.Sessions.Single().Paused,
            "a resumed session reads its working TGA again, so a save that failed before the pause goes through");
    }

    /// <summary>
    /// Sessions carry on across plugin reloads and game restarts: a watching session keeps watching, a
    /// paused one stays paused, and one left alone for a day is paused so cache cleanup can remove it.
    /// </summary>
    private static async Task RestartAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        TextureEditRequest Other(string name) => f.Request with
        {
            GamePath = $"chara/{name}.tex", ActualPath = Path.Combine(f.Backend.ModRoot, "Files", "chara", name + ".tex"), RelativePath = $"Files/chara/{name}.tex",
        };
        var watching = await f.Service.StartAsync(f.Request, false);
        var paused = await f.Service.StartAsync(Other("paused"), false);
        var idle = await f.Service.StartAsync(Other("idle"), false);
        await f.Service.SetPausedAsync(paused, true);
        SetStale(f.Service.Sessions.Single(s => s.Id == idle).Directory);
        // Saving any session rewrites every session.json; that doesn't make the idle one active.
        await f.Service.SetPausedAsync(watching, false);
        f.Service.Dispose();
        await f.Service.Completion;

        using var restarted = new TextureEditService(f.Backend, f.Config, f.ConfigDir, f.Backups, (_, _) => { }, false);
        TextureEditSession Find(Guid id) => restarted.Sessions.Single(s => s.Id == id);
        Check(Find(watching) is { Paused: false, Status: "Watching for saves" } && Find(paused) is { Paused: true, Status: "Paused" } &&
              Find(idle) is { Paused: true } i && i.Status.StartsWith("Paused after a day", StringComparison.Ordinal),
            "after a restart a watching session keeps watching, a paused one stays paused, and one idle for a day is paused");
        File.WriteAllBytes(Find(watching).WorkingFile, Tga(8, 8, 55));
        await restarted.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && File.ReadAllBytes(f.Backend.Target)[80] == 55, "a save after a restart goes through without resuming the session");
    }

    /// <summary>
    /// Penumbra, Blender exports and other sessions rewrite meta.json all the time. A session only stops
    /// when the game paths mapped to its own file change, and reconnecting takes it back.
    /// </summary>
    private static async Task MetadataRewritesAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, withMeta: true);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        var metaPath = Path.Combine(f.Backend.ModRoot, "meta.json");
        void EditMeta(Action<JsonObject> edit)
        {
            var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
            edit(meta);
            File.WriteAllText(metaPath, meta.ToJsonString());
        }

        // As Penumbra saves it, after a Blender export added an attribute group.
        EditMeta(meta =>
        {
            meta["LastWrite"] = DateTime.UtcNow.AddMinutes(5).ToString("O");
            meta["Description"] = "Edited in Penumbra";
            meta["DefaultData"]!["Files"]!["chara/test.tex"] = "files\\chara\\test.tex";
            meta["Groups"]!.AsArray().Add(new JsonObject
            {
                ["Type"] = "Imc", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "XIV Instant Edit test Parts (IMC)",
                ["Options"] = new JsonArray { new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "A" } },
            });
        });
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 60));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && !f.Service.Sessions.Single().Conflict,
            "rewrites of meta.json that keep the texture's mapping, with paths as Penumbra writes them, don't stop a save");

        var secondTarget = Path.Combine(f.Backend.ModRoot, "Files", "chara", "second.tex");
        PenumbraService.AddGameTextureToMod(f.Backend.ModRoot, "chara/second.tex", f.Backend.Original);
        var second = await f.Service.StartAsync(f.Request with { GamePath = "chara/second.tex", ActualPath = secondTarget, RelativePath = "Files/chara/second.tex" }, false);
        File.WriteAllBytes(Path.Combine(f.Service.Sessions.Single(x => x.Id == second).Directory, "Red.tga"), Tga(8, 8, 61));
        await f.Service.ProcessPendingAsync(true);
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 62));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.VariantCommits == 1 && f.Backend.Commits == 2 && f.Service.Sessions.All(x => !x.Conflict),
            "another session's variant group in the same mod doesn't stop this session");

        var encodes = f.Backend.Encodes;
        EditMeta(meta => meta["DefaultData"]!["Files"]!["chara/test.tex"] = "Files/chara/elsewhere.tex");
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 63));
        await f.Service.ProcessPendingAsync(true);
        var conflicted = f.Service.Sessions.Single(x => x.Id == id);
        Check(f.Backend.Commits == 2 && f.Backend.Encodes == encodes && conflicted is { Conflict: true, Paused: true } &&
              conflicted.Status.Contains("no longer uses", StringComparison.Ordinal),
            "once the mod maps the texture to another file, a save stops with a conflict before encoding");
        await RejectAsync(() => f.Service.ReconnectAsync(id), "reconnecting is refused while the mod doesn't use the file");
        EditMeta(meta => meta["DefaultData"]!["Files"]!["chara/test.tex"] = "Files/chara/test.tex");
        await f.Service.ReconnectAsync(id);
        Check(f.Service.Sessions.Single(x => x.Id == id) is { Conflict: false, Paused: false }, "reconnecting takes the session back");
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 3 && File.ReadAllBytes(f.Backend.Target)[80] == 63, "the save the conflict stopped goes through after reconnecting");

        var external = Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 99);
        File.WriteAllBytes(f.Backend.Target, external);
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 64));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Service.Sessions.Single(x => x.Id == id).Conflict && File.ReadAllBytes(f.Backend.Target).SequenceEqual(external),
            "a texture file another program changed stops the save with a conflict");
        await f.Service.ReconnectAsync(id);
        await f.Service.ProcessPendingAsync(true);
        var reconnected = f.Service.Sessions.Single(x => x.Id == id);
        Check(!reconnected.Conflict && File.ReadAllBytes(f.Backend.Target)[80] == 64 && File.ReadAllBytes(reconnected.LastBackup).SequenceEqual(external),
            "after reconnecting, the save goes over the other program's file and backs it up first");

        // Earlier versions kept a hash of all of meta.json.
        f.Service.Dispose();
        await f.Service.Completion;
        var catalogPath = Path.Combine(f.ConfigDir, "TextureSessions.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsArray();
        foreach (var entry in catalog)
            entry!["MappingFingerprint"] = TextureFiles.Hash(File.ReadAllBytes(metaPath));
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        EditMeta(meta => meta["LastWrite"] = DateTime.UtcNow.AddMinutes(10).ToString("O"));
        using var restarted = new TextureEditService(f.Backend, f.Config, f.ConfigDir, f.Backups, (_, _) => { }, false);
        File.WriteAllBytes(restarted.Sessions.Single(x => x.Id == id).WorkingFile, Tga(8, 8, 65));
        await restarted.ProcessPendingAsync(true);
        var legacy = restarted.Sessions.Single(x => x.Id == id);
        Check(!legacy.Conflict && File.ReadAllBytes(f.Backend.Target)[80] == 65 && legacy.MappingFingerprint.StartsWith("paths:", StringComparison.Ordinal),
            "a session from an earlier version still saves after meta.json changed, and takes the new fingerprint");
    }

    /// <summary>
    /// A save that can't go through right now, while Penumbra loads or the character shows another file,
    /// waits without encoding and goes through later. It never turns into a conflict.
    /// </summary>
    private static async Task WaitingAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        f.Backend.Wait = "Penumbra isn't available right now.";
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 66));
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 0 && f.Backend.Encodes == 0 && f.Service.Sessions.Single() is { Waiting: true, Conflict: false, Paused: false } w &&
              w.Status == "Save waiting: Penumbra isn't available right now.",
            "a save that has to wait encodes nothing and says why, without a conflict");
        f.Backend.Wait = null;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && f.Service.Sessions.Single() is { Waiting: false, Conflict: false }, "the waiting save goes through once it can");

        // Painter sends aren't tried again: the send says why, and the session carries on.
        f.Backend.Wait = "Your character shows another file for this texture right now.";
        var results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 67))], CancellationToken.None);
        Check(results.Single() is { Outcome: ExternalTextureOutcome.Failed } r && r.Message == f.Backend.Wait &&
              f.Service.Sessions.Single() is { Waiting: false, Conflict: false, Paused: false } p && p.Status.StartsWith("Painter texture not applied:", StringComparison.Ordinal),
            "a Painter texture that can't be applied now fails that send without stopping the session");
        f.Backend.Wait = null;
        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 67))], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Applied && f.Backend.Commits == 2, "sending it again applies it");

        File.WriteAllBytes(f.Backend.Target, Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 98));
        await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 68))], CancellationToken.None);
        var status = f.Service.Sessions.Single().Status;
        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 68))], CancellationToken.None);
        Check(f.Service.Sessions.Single() is { Conflict: true } c && c.Status == status && results.Single().Message == status,
            "sending to a conflicted session again reports the conflict as it was");
        await f.Service.ReconnectAsync(id);
        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 68))], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Applied && File.ReadAllBytes(f.Backend.Target)[80] == 68,
            "after reconnecting, Painter's texture is applied");
    }

    /// <summary>
    /// A waiting save is checked again on each retry, but its TGA is only read and converted again once
    /// the save can go through. The same goes for a variant.
    /// </summary>
    private static async Task WaitingRetriesAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, withMeta: true);
        await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        var red = Path.Combine(s.Directory, "Red.tga");
        f.Backend.Wait = "Penumbra isn't available right now.";
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 66));
        File.WriteAllBytes(red, Tga(8, 8, 79));
        await f.Service.ProcessPendingAsync(true);
        var conversions = f.Backend.Conversions;
        var checks = f.Backend.Checks;
        // Reading either file would fail while it's locked.
        using (new FileStream(s.WorkingFile, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(red, FileMode.Open, FileAccess.Read, FileShare.None))
            await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Checks == checks + 2 && f.Backend.Conversions == conversions && f.Service.Sessions.Single().Waiting,
            "retrying a waiting save checks again without reading or converting the working or variant TGA");
        f.Backend.Wait = null;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && f.Backend.VariantCommits == 1 && !f.Service.Sessions.Single().Waiting,
            "once the check passes, both waiting saves go through");
    }

    /// <summary>
    /// Saves waiting for Penumbra to list the session's mod again pause the session after a while, so it
    /// doesn't try again forever. Waiting for Penumbra itself never pauses it.
    /// </summary>
    private static async Task UnlistedModPauseAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var id = await f.Service.StartAsync(f.Request, false);
        var s = f.Service.Sessions.Single();
        f.Service.UnlistedModPause = TimeSpan.Zero;
        f.Backend.Wait = "Penumbra isn't available right now.";
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 66));
        await f.Service.ProcessPendingAsync(true);
        await f.Service.ProcessPendingAsync(true);
        Check(f.Service.Sessions.Single() is { Waiting: true, Paused: false }, "waiting for Penumbra itself never pauses the session");

        f.Service.UnlistedModPause = TimeSpan.FromMinutes(5);
        f.Backend.Wait = "Penumbra doesn't list the texture's mod right now.";
        f.Backend.ModUnlisted = true;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Service.Sessions.Single() is { Waiting: true, Paused: false }, "a session whose mod Penumbra doesn't list waits at first");
        f.Service.UnlistedModPause = TimeSpan.Zero;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 0 && f.Service.Sessions.Single() is { Paused: true, Waiting: false, Conflict: false } p &&
              p.Status.StartsWith("Paused: Penumbra doesn't list the texture's mod.", StringComparison.Ordinal),
            "after a while of that, the session pauses and says why");
        var checks = f.Backend.Checks;
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Checks == checks, "a paused session isn't tried again");

        f.Backend.Wait = null;
        f.Backend.ModUnlisted = false;
        await f.Service.SetPausedAsync(id, false);
        await f.Service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 1 && f.Service.Sessions.Single() is { Paused: false, Waiting: false },
            "Resume saves once Penumbra lists the mod again");
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

    /// <summary> Clean up all sessions removes every working folder; a session whose files are in use stays listed. </summary>
    private static async Task DiscardAllAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var firstId = await f.Service.StartAsync(f.Request, false);
        var secondTarget = Path.Combine(f.Backend.ModRoot, "Files", "chara", "second.tex");
        var secondId = await f.Service.StartAsync(new TextureEditRequest("chara/second.tex", secondTarget, "Mod", f.Backend.ModRoot,
            "Files/chara/second.tex", null, 0), false);
        await f.Service.SetPausedAsync(secondId, true);
        var first = f.Service.Sessions.Single(s => s.Id == firstId);
        var second = f.Service.Sessions.Single(s => s.Id == secondId);
        File.WriteAllBytes(Path.Combine(second.Directory, "artist-source.psd"), [1, 2, 3]);

        var locked = Path.Combine(first.Directory, "open-in-editor.psd");
        IReadOnlyList<string> kept;
        using (new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            kept = await f.Service.DiscardAllAsync();
        Check(kept.Count == 1 && kept[0].StartsWith("test.tex: ", StringComparison.Ordinal) &&
              f.Service.Sessions.Select(s => s.Id).SequenceEqual([firstId]),
            "clean up all reports a session whose files are in use and keeps it listed");
        Check(!Directory.Exists(second.Directory), "clean up all deletes the other working folders, artist documents included");
        using (var restored = new TextureEditService(f.Backend, f.Config, f.ConfigDir, f.Backups, (_, _) => { }, false))
            Check(restored.Sessions.Select(s => s.Id).SequenceEqual([firstId]), "the catalog keeps only the session that stayed");

        kept = await f.Service.DiscardAllAsync();
        Check(kept.Count == 0 && f.Service.Sessions.Count == 0 && !Directory.Exists(first.Directory),
            "cleaning up again once the files are closed removes the rest");
        Check(File.Exists(f.Backend.Target) && File.Exists(secondTarget), "clean up all leaves the mod files alone");
    }

    /// <summary> Textures Substance Painter sends back are applied as one batch through their sessions. </summary>
    private static async Task PainterBatchAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var job = Guid.NewGuid();
        var firstId = await f.Service.StartAsync(f.Request with { JobId = job }, false);
        var secondTarget = Path.Combine(f.Backend.ModRoot, "Files", "chara", "second.tex");
        var secondId = await f.Service.StartAsync(new TextureEditRequest("chara/second.tex", secondTarget, "Mod", f.Backend.ModRoot,
            "Files/chara/second.tex", null, 0, JobId: job), false);
        Check(firstId != secondId && f.Service.Sessions.Count == 2, "a Painter project opens one session per texture");

        var red = Tga(8, 8, 77);
        var blue = Tga(8, 8, 88);
        var refreshes = f.Backend.Refreshes;
        var results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(firstId, red), new ExternalTextureSave(secondId, blue)], CancellationToken.None);
        Check(results.All(r => r.Outcome == ExternalTextureOutcome.Applied) && f.Backend.Commits == 2, "both exported textures are committed");
        Check(f.Backend.Refreshes == refreshes + 1 && f.Backend.RefreshedMods[^1] == 1, "the batch reloads its mod once and redraws once");
        Check(File.ReadAllBytes(f.Backend.Target)[80] == 77 && File.ReadAllBytes(secondTarget)[80] == 88, "each session wrote its own file");
        var first = f.Service.Sessions.Single(s => s.Id == firstId);
        Check(File.ReadAllBytes(first.WorkingFile).SequenceEqual(red) && first.WorkingHash == TextureFiles.Hash(red),
            "the applied image replaces the working TGA");

        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(firstId, red), new ExternalTextureSave(secondId, blue)], CancellationToken.None);
        Check(results.All(r => r.Outcome == ExternalTextureOutcome.Unchanged) && f.Backend.Commits == 2 && f.Backend.Refreshes == refreshes + 1,
            "sending the same pixels again commits and redraws nothing");

        await f.Service.ProcessPendingAsync(force: true);
        Check(f.Backend.Commits == 2, "the watcher treats the mirrored working TGA as already saved");

        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(firstId, null)], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Restored && File.ReadAllBytes(f.Backend.Target).SequenceEqual(f.Backend.Original),
            "matching Painter's untouched export restores the captured original exactly");
        Check(File.ReadAllBytes(secondTarget)[80] == 88, "a restore leaves the project's other textures alone");
        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(firstId, null)], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Unchanged, "restoring an untouched texture does nothing");

        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(Guid.NewGuid(), red)], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Failed, "a missing session is reported, not thrown");
        results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(secondId, Tga(6, 8, 1))], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Failed && File.ReadAllBytes(secondTarget)[80] == 88,
            "an image the texture's compression can't hold is refused and the file stays");

        // A Painter project keeps its sessions even when they look stale.
        f.Service.KeepSession = id => id == firstId;
        await f.Service.SetPausedAsync(firstId, true);
        await f.Service.SetPausedAsync(secondId, true);
        SetStale(first.Directory);
        SetStale(f.Service.Sessions.Single(s => s.Id == secondId).Directory);
        await f.Service.CleanupStaleSessionsAsync();
        Check(f.Service.Sessions.Any(s => s.Id == firstId), "cleanup keeps sessions a Painter project links");
        Check(TextureEditService.IsReusableFor(first with { PixelHash = "x", NewModName = "M", CollectionId = Guid.NewGuid(), ObjectIndex = null }, f.Request with { ModDirectory = "", JobId = job }) &&
              !TextureEditService.IsReusableFor(first with { PixelHash = "x", NewModName = "M", CollectionId = Guid.NewGuid(), ObjectIndex = null }, f.Request with { ModDirectory = "" }),
            "a vanilla session is only reused inside its own Painter project");
        // Game Files edits vanilla textures without an actor: the session keeps none, only the collection.
        var gameFilesSession = first with { PixelHash = "x", NewModName = "M", CollectionId = Guid.NewGuid(), ObjectIndex = null, ActorAddress = 0, JobId = null };
        var gameFilesRequest = f.Request with { ModDirectory = "", ObjectIndex = null, ActorAddress = 0, JobId = null, CollectionObjectIndex = 0 };
        Check(TextureEditService.IsReusableFor(gameFilesSession, gameFilesRequest) &&
              !TextureEditService.IsReusableFor(gameFilesSession, gameFilesRequest with { ObjectIndex = 0, ActorAddress = 1234 }) &&
              !TextureEditService.IsReusableFor(gameFilesSession with { CollectionId = null }, gameFilesRequest),
            "a vanilla texture opened from Game Files reuses its actor-less session, not an On Screen one");
    }

    /// <summary>
    /// Materials point at blank game textures such as white.tex without shipping them. None opens
    /// for editing, and a session opened before that rule never writes one.
    /// </summary>
    private static async Task SharedTexturesAsync(string root)
    {
        Check(SharedTextures.IsBlank("chara/common/texture/white.tex") && SharedTextures.IsBlank("--chara/common/texture/White.tex") &&
              SharedTextures.IsBlank("common/graphics/texture/dummy.tex") && SharedTextures.IsBlank("bgcommon/texture/dummy_n.tex") &&
              !SharedTextures.IsBlank("chara/human/c0801/obj/face/f0002/common/texture/white.tex"),
            "the game's blank textures are recognized by game path, also with a leading --");
        Check(SharedTextures.EditBlock("chara/common/texture/white.tex", vanilla: false).Length > 0 &&
              SharedTextures.EditBlock("chara/common/texture/eye/eye01_base.tex", vanilla: true).Length > 0 &&
              SharedTextures.EditBlock("chara/common/texture/eye/eye01_base.tex", vanilla: false).Length == 0 &&
              SharedTextures.EditBlock("chara/equipment/e0001/texture/v01_c0101e0001_top_base.tex", vanilla: true).Length == 0,
            "blank textures are never editable, other shared game textures only when a mod ships its own file");

        using (var f = new Fixture(Path.Combine(root, "open"), (uint)TexFile.TextureFormat.BC1))
        {
            var white = f.Request with { GamePath = "chara/common/texture/white.tex", RelativePath = "Files/chara/common/texture/white.tex" };
            await RejectAsync(() => f.Service.StartAsync(white, false), "a mod's copy of white.tex doesn't open for editing");
            await RejectAsync(() => f.Service.StartAsync(white with { ModDirectory = "", NewModName = "White" }, false),
                "the game's white.tex doesn't open into a new mod");
            await RejectAsync(() => f.Service.StartAsync(f.Request with { GamePath = "chara/common/texture/eye/eye01_base.tex", ModDirectory = "", NewModName = "Eye" }, false),
                "a shared game texture doesn't open into a new mod");
            Check(f.Service.Sessions.Count == 0 && f.Backend.Commits == 0, "a refused texture leaves no session behind");
            await f.Service.StartAsync(f.Request with { GamePath = "chara/common/texture/eye/eye01_base.tex", RelativePath = "Files/chara/common/texture/eye/eye01_base.tex" }, false);
            Check(f.Service.Sessions.Count == 1, "an eye mod's own texture under chara/common still opens");
        }

        using var old = new Fixture(Path.Combine(root, "old"), (uint)TexFile.TextureFormat.BC1);
        var painted = await old.Service.StartAsync(old.Request, false);
        TextureEditRequest Other(string name) => old.Request with
        {
            GamePath = $"chara/{name}.tex", ActualPath = Path.Combine(old.Backend.ModRoot, "Files", "chara", name + ".tex"), RelativePath = $"Files/chara/{name}.tex",
        };
        var saved = await old.Service.StartAsync(Other("second"), false);
        var variant = await old.Service.StartAsync(Other("third"), false);
        old.Service.Dispose();
        await old.Service.Completion;
        // As if these sessions were opened on blank textures by an earlier version.
        var catalogPath = Path.Combine(old.ConfigDir, "TextureSessions.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsArray();
        var blanks = new Dictionary<Guid, string>
        {
            [painted] = "chara/common/texture/white.tex", [saved] = "chara/common/texture/null_normal.tex", [variant] = "bgcommon/texture/dummy_d.tex",
        };
        var targets = new Dictionary<Guid, (string File, byte[] Bytes)>();
        foreach (var entry in catalog)
        {
            var id = entry!["Id"]!.GetValue<Guid>();
            var before = JsonSerializer.Deserialize<TextureEditSession>(entry.ToJsonString())!;
            entry["GamePath"] = blanks[id];
            var after = JsonSerializer.Deserialize<TextureEditSession>(entry.ToJsonString())!;
            // The working TGA is named after the game path; a leftover one would count as a variant.
            File.Move(before.WorkingFile, after.WorkingFile);
            targets[id] = (after.TargetFile, File.ReadAllBytes(after.TargetFile));
        }
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        using var service = new TextureEditService(old.Backend, old.Config, old.ConfigDir, old.Backups, (_, _) => { }, false);
        Check(service.StartupError.Length == 0 && service.Sessions.Count == 3, "sessions on blank textures still load, so their files can be discarded");

        var results = await service.ApplyExternalAsync([new ExternalTextureSave(painted, Tga(8, 8, 71))], CancellationToken.None);
        Check(results.Single().Outcome == ExternalTextureOutcome.Failed && service.Sessions.Single(s => s.Id == painted) is { Conflict: true } p && p.Status.Contains("white.tex"),
            "a Painter send never writes white.tex; its session conflicts");

        await service.SetPausedAsync(saved, false);
        await service.SetPausedAsync(variant, false);
        var savedSession = service.Sessions.Single(s => s.Id == saved);
        File.WriteAllBytes(savedSession.WorkingFile, Tga(8, 8, 72));
        File.WriteAllBytes(Path.Combine(service.Sessions.Single(s => s.Id == variant).Directory, "Red.tga"), Tga(8, 8, 73));
        await service.ProcessPendingAsync(true);
        Check(service.Sessions.Single(s => s.Id == saved) is { Conflict: true } n && n.Status.Contains("null_normal.tex") &&
              service.Sessions.Single(s => s.Id == variant) is { Conflict: true, Variants.Count: 0 } d && d.Status.Contains("dummy_d.tex"),
            "an editor save or a variant of a blank texture conflicts");
        Check(old.Backend.Commits == 0 && old.Backend.VariantCommits == 0 && targets.Values.All(t => File.ReadAllBytes(t.File).SequenceEqual(t.Bytes)),
            "no blank texture's file is written");
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (IOException) { Check(true, message); return; }
        Check(false, message);
    }

    /// <summary> A Painter project's vanilla textures share one mod, found again by its identifier. </summary>
    private static void PainterJobMod(string root)
    {
        var job = Guid.NewGuid();
        var modRoot = Path.Combine(root, "Painter Top");
        Directory.CreateDirectory(modRoot);
        var first = Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 5);
        PenumbraService.StageGameTextureMod(modRoot, "Painter Top", "chara/equipment/e0001/texture/a_base.tex", first, job);
        Check(PenumbraService.ReadModStableIdentifierForRegression(modRoot) == job, "the project's mod carries the job id as its identifier");
        const string baseFile = "Files/chara/equipment/e0001/texture/a_base.tex", normFile = "Files/chara/equipment/e0001/texture/a_norm.tex";
        var before = PenumbraService.TextureMappingFingerprint(modRoot, baseFile);
        var unmapped = PenumbraService.TextureMappingFingerprint(modRoot, normFile);
        PenumbraService.AddGameTextureToMod(modRoot, "chara/equipment/e0001/texture/a_norm.tex", Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 4, 6));
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, "meta.json"))).RootElement;
        var files = meta.GetProperty("DefaultData").GetProperty("Files");
        Check(files.GetProperty("chara/equipment/e0001/texture/a_base.tex").GetString() == "Files/chara/equipment/e0001/texture/a_base.tex" &&
              files.GetProperty("chara/equipment/e0001/texture/a_norm.tex").GetString() == "Files/chara/equipment/e0001/texture/a_norm.tex",
            "a later vanilla texture joins the mod's default option");
        Check(File.Exists(Path.Combine(modRoot, "Files", "chara", "equipment", "e0001", "texture", "a_norm.tex")) &&
              PenumbraService.TextureMappingFingerprint(modRoot, normFile) != unmapped &&
              PenumbraService.TextureMappingFingerprint(modRoot, baseFile) == before,
            "the file is written; the new file's mapping fingerprint changes and the first texture's stays");
        Check(PenumbraService.ReadModStableIdentifierForRegression(modRoot) == job, "adding a file keeps the identifier");
    }

    /// <summary>
    /// Old mods ship textures whose mip offsets were written for uncompressed data (Bibo+'s
    /// bibo_viera_base.tex). Sessions open them, and every write of such an original, from a
    /// restore or a Painter send, carries a header that describes its data.
    /// </summary>
    private static async Task MisplacedMipsAsync(string root)
    {
        var bc7 = (uint)TexFile.TextureFormat.BC7;
        var intact = Tex(bc7, 8, 8, 3, 12);
        var broken = UncompressedOffsets(intact);
        Reject(() => TextureFiles.ReadTex(broken), "the strict TEX check refuses BC7 mip offsets written for uncompressed data");
        using (var f = new Fixture(Path.Combine(root, "mod"), bc7, original: broken))
        {
            var id = await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            Check(!s.Paused && s.Format == bc7 && s.MipMaps && File.ReadAllBytes(Path.Combine(s.Directory, "original.tex")).SequenceEqual(intact) &&
                  f.Backend.TargaInputs.Single().SequenceEqual(intact) && File.ReadAllBytes(s.TargetFile).SequenceEqual(broken),
                "a session opens such a texture and keeps and converts it with offsets that describe its data; the mod file stays");

            var results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, null)], CancellationToken.None);
            Check(results.Single().Outcome == ExternalTextureOutcome.Unchanged && f.Backend.Commits == 0 &&
                  File.ReadAllBytes(s.TargetFile).SequenceEqual(broken),
                "an untouched Painter texture leaves the mod file with its own offsets alone");

            File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 90));
            await f.Service.ProcessPendingAsync(true);
            s = f.Service.Sessions.Single();
            Check(f.Backend.Commits == 1 && !s.Conflict && TextureFiles.ReadTex(File.ReadAllBytes(s.TargetFile)).Mips == 4 &&
                  File.ReadAllBytes(s.LastBackup).SequenceEqual(broken),
                "the first save replaces the mod file it captured and backs it up unchanged");

            await f.Service.RestoreAsync(id);
            Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(intact) && f.Service.Sessions.Single() is { Paused: true, Width: 8, Height: 8 },
                "restoring that backup writes the texture back with offsets that describe its data");

            results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, null)], CancellationToken.None);
            Check(results.Single().Outcome == ExternalTextureOutcome.Unchanged && f.Backend.Commits == 2,
                "the restored texture counts as the captured original");
            results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, Tga(8, 8, 91))], CancellationToken.None);
            var applied = results.Single().Outcome;
            results = await f.Service.ApplyExternalAsync([new ExternalTextureSave(id, null)], CancellationToken.None);
            Check(applied == ExternalTextureOutcome.Applied && results.Single().Outcome == ExternalTextureOutcome.Restored &&
                  f.Backend.Commits == 4 && File.ReadAllBytes(s.TargetFile).SequenceEqual(intact),
                "a Painter send back to the untouched image restores the original with offsets that describe its data");
        }

        using (var f = new Fixture(Path.Combine(root, "vanilla"), bc7, vanilla: true, original: broken))
        {
            await f.Service.StartAsync(f.Request, false);
            var s = f.Service.Sessions.Single();
            File.WriteAllBytes(Path.Combine(s.Directory, "Red.tga"), Tga(8, 8, 92));
            await f.Service.ProcessPendingAsync(true);
            s = f.Service.Sessions.Single();
            Check(!s.NeedsMod && s.Variants.Single().RelativePath.Length > 0 && File.ReadAllBytes(s.TargetFile).SequenceEqual(intact),
                "a variant of such a texture creates its mod with the original's offsets normalized");
        }
    }

    /// <summary>
    /// An image dropped on a texture in On Screen replaces it through the texture's session, and Undo
    /// puts back what it replaced while the texture still holds the drop.
    /// </summary>
    private static async Task DroppedImageAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7);
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        var png = Png(Path.Combine(images, "Red Top.png"), 8, 8, 30);

        var result = await f.Service.ReplaceAsync(f.Request, png);
        var s = f.Service.Sessions.Single();
        Check(result.Outcome == ExternalTextureOutcome.Applied && f.Backend.Commits == 1 && File.ReadAllBytes(s.TargetFile)[80] == 30 &&
              TextureFiles.ReadTex(File.ReadAllBytes(s.TargetFile)).Format == (uint)TexFile.TextureFormat.BC7,
            "a dropped PNG is decoded and committed in the texture's own compression");
        Check(s is { Paused: false, Replacement: { File: "Red Top.png" } drop } && drop.Hash == s.LastCommittedHash &&
              File.ReadAllBytes(drop.Backup).SequenceEqual(f.Backend.Original) && File.ReadAllBytes(s.WorkingFile)[18] == 30,
            "the session records the drop with the backup of what it replaced, and the working TGA takes the image");
        Check(TextureEditService.CanUndoReplace(s) && f.Service.FindUndoableReplace(f.Request)?.Id == s.Id &&
              f.Service.FindUndoableReplace(f.Request with { ActualPath = Path.Combine(root, "other.tex") }) is null,
            "the drop is found again from the texture's row, and only from it");

        f.Service.Dispose();
        await f.Service.Completion;
        using var service = new TextureEditService(f.Backend, f.Config, f.ConfigDir, f.Backups, (_, _) => { }, false);
        Check(service.FindUndoableReplace(f.Request)?.Replacement?.File == "Red Top.png", "a drop can still be undone after a restart");

        await service.UndoReplaceAsync(s.Id);
        s = service.Sessions.Single();
        Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(f.Backend.Original) && f.Backend.Commits == 2 &&
              s is { Replacement: null, Paused: false } && !TextureEditService.CanUndoReplace(s) && service.FindUndoableReplace(f.Request) is null,
            "undo puts back exactly the texture the drop replaced and ends the drop");
        Check(File.ReadAllBytes(s.WorkingFile)[18] == 12 && f.Backend.TargaInputs[^1].SequenceEqual(f.Backend.Original),
            "the working TGA follows the texture that was put back");
        await RejectAsync(() => service.UndoReplaceAsync(s.Id), "a drop is undone only once");

        var tex = Path.Combine(images, "Same.tex");
        File.WriteAllBytes(tex, UncompressedOffsets(Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 3, 12)));
        result = await service.ReplaceAsync(f.Request, tex);
        Check(f.Backend.TargaInputs[^1].SequenceEqual(Tex((uint)TexFile.TextureFormat.BC7, 8, 8, 3, 12)),
            "a dropped TEX goes through the converter with its mip offsets normalized");
        Check(result.Outcome == ExternalTextureOutcome.Unchanged && f.Backend.Commits == 2 && service.FindUndoableReplace(f.Request) is null,
            "an image the same as the texture commits nothing and leaves nothing to undo");

        await service.ReplaceAsync(f.Request, png);
        s = service.Sessions.Single();
        File.WriteAllBytes(s.WorkingFile, Tga(8, 8, 40));
        await service.ProcessPendingAsync(true);
        Check(f.Backend.Commits == 4 && File.ReadAllBytes(s.TargetFile)[80] == 40 && service.FindUndoableReplace(f.Request) is null,
            "a later editor save ends the drop's undo");
        await RejectAsync(() => service.UndoReplaceAsync(s.Id), "undo refuses once the texture changed after the drop");

        await RejectAsync(() => service.ReplaceAsync(f.Request, Path.Combine(images, "notes.txt")), "a file that isn't an image is refused");
        result = await service.ReplaceAsync(f.Request, Png(Path.Combine(images, "Odd.png"), 6, 8, 50));
        Check(result.Outcome == ExternalTextureOutcome.Failed && result.Message.Contains("divisible by 4"),
            "an image the texture's block compression can't hold is refused with why");
        Check(f.Backend.Commits == 4 && File.ReadAllBytes(s.TargetFile)[80] == 40, "refused drops leave the texture alone");
    }

    /// <summary> An image dropped on a vanilla texture creates its mod; undo puts the game's texture back in that mod. </summary>
    private static async Task DroppedVanillaImageAsync(string root)
    {
        using var f = new Fixture(root, (uint)TexFile.TextureFormat.BC7, vanilla: true);
        var png = Png(Path.Combine(root, "Blue.png"), 8, 8, 60);
        var result = await f.Service.ReplaceAsync(f.Request, png);
        var s = f.Service.Sessions.Single();
        Check(result.Outcome == ExternalTextureOutcome.Applied && !s.NeedsMod && File.ReadAllBytes(s.TargetFile)[80] == 60 &&
              s.Replacement is { Backup: "" } && f.Service.FindUndoableReplace(f.Request)?.Id == s.Id,
            "a drop on a vanilla texture creates its mod and can be undone");
        await f.Service.UndoReplaceAsync(s.Id);
        Check(File.ReadAllBytes(s.TargetFile).SequenceEqual(f.Backend.Original) && f.Service.Sessions.Single().Replacement is null,
            "undo writes the captured game texture back into the new mod");
    }

    /// <summary> An 8-bit RGBA PNG of one colour whose blue channel is <paramref name="blue"/>; the fake converter keeps a TGA's first byte (blue). </summary>
    private static string Png(string path, int width, int height, byte blue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height,
            new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 100, blue, 255));
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, path);
        return path;
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
        public Fixture(string root, uint format, bool vanilla = false, bool watch = false, TimeSpan? cleanupInterval = null, bool withMeta = false,
            byte[]? original = null)
        {
            ConfigDir = Path.Combine(root, "config"); Directory.CreateDirectory(ConfigDir);
            var cacheDirectory = Path.Combine(root, "cache");
            MakeCache(cacheDirectory);
            Config = new Configuration { TextureCacheDirectory = cacheDirectory };
            Backups = new ModelBackupStore(ConfigDir);
            Backend = new FakeBackend(root, format, Backups, vanilla, withMeta, original);
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
        public int Commits, VariantCommits, Encodes, Conversions, Checks;
        public Guid? LastShown;
        public TextureType LastFormat;
        public bool FailConversion, RefreshWarning;
        public Action? OnEncode;
        /// <summary>Set to make saves wait, as when Penumbra is unavailable or the character shows another file.</summary>
        public string? Wait;
        /// <summary>The wait is for Penumbra to list the mod again.</summary>
        public bool ModUnlisted;
        private string Meta => Path.Combine(ModRoot, "meta.json");
        /// <summary>The TEX files converted to TGA, as the converter received them.</summary>
        public readonly List<byte[]> TargaInputs = [];
        public FakeBackend(string root, uint format, ModelBackupStore backups, bool vanilla, bool withMeta = false, byte[]? original = null)
        {
            _backups = backups; _vanilla = vanilla;
            ModRoot = Path.Combine(root, "Mod"); Target = Path.Combine(ModRoot, "Files", "chara", "test.tex");
            Original = original ?? Tex(format, 8, 8, 4, 12);
            if (vanilla) return;
            if (withMeta)
            {
                // A registered v4 mod, which variant groups need.
                Directory.CreateDirectory(ModRoot);
                PenumbraService.StageGameTextureMod(ModRoot, "Mod", "chara/test.tex", Original);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllBytes(Target, Original);
        }
        public Task<TextureSource> CaptureAsync(TextureEditRequest request, CancellationToken token)
        {
            // As PenumbraService captures: the header of the normalized file, the bytes and hash as stored.
            var h = TextureFiles.ReadOriginal(Original).Header;
            // Each game path gets its own file in the mod, so a batch can hold several textures.
            var relative = "Files/" + request.GamePath;
            var target = Path.Combine(ModRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!_vanilla && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, Original);
            }
            return Task.FromResult(new TextureSource(Original, new TextureEditSession
            {
                GamePath = request.GamePath, ModDirectory = "Mod", ModRoot = ModRoot, RelativePath = relative, JobId = request.JobId,
                NewModName = request.NewModName, NeedsMod = _vanilla, CollectionId = Guid.NewGuid(),
                Format = h.Format, Width = 8, Height = 8, MipMaps = true, LastCommittedHash = _vanilla ? "" : TextureFiles.Hash(Original),
                MappingFingerprint = File.Exists(Meta) ? PenumbraService.TextureMappingFingerprint(ModRoot, relative) : "",
            }));
        }
        // As PenumbraService checks before encoding, minus Penumbra and the live actor.
        public Task CheckAsync(TextureEditSession s, CancellationToken token)
        {
            Checks++;
            if (Wait is { } wait) throw new TextureWaitException(wait, ModUnlisted);
            if (!s.NeedsMod && File.Exists(Meta)) PenumbraService.EnsureTextureMappingForRegression(s);
            return Task.CompletedTask;
        }
        public Task<TextureRebase> RebaseAsync(TextureEditSession s, CancellationToken token)
        {
            if (Wait is { } wait) throw new TextureWaitException(wait);
            if (File.Exists(Meta) && PenumbraService.TextureGamePaths(ModRoot, s.RelativePath).Count == 0)
                throw new IOException("The mod no longer uses this texture's file.");
            var bytes = File.ReadAllBytes(s.TargetFile);
            var h = TextureFiles.ReadOriginal(bytes).Header;
            var variants = s.Variants.Where(v => v.RelativePath.Length > 0 && File.Exists(s.VariantTargetFile(v)))
                .ToDictionary(v => v.Name, v => TextureFiles.Hash(File.ReadAllBytes(s.VariantTargetFile(v))), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new TextureRebase(TextureFiles.Hash(bytes),
                File.Exists(Meta) ? PenumbraService.TextureMappingFingerprint(ModRoot, s.RelativePath) : "", h.Format, h.Width, h.Height, variants));
        }
        public Task ConvertAsync(string input, string output, TextureType format, bool mipMaps)
        {
            Conversions++;
            if (FailConversion) throw new IOException("Simulated converter failure");
            if (format == TextureType.Targa)
            {
                TargaInputs.Add(File.ReadAllBytes(input));
                File.WriteAllBytes(output, Tga(8, 8, 12));
            }
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
                if (mipMaps) { LastFormat = format; Encodes++; OnEncode?.Invoke(); }
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
                s.MappingFingerprint = PenumbraService.TextureMappingFingerprint(ModRoot, s.RelativePath);
            }
            else
            {
                if (File.Exists(Meta)) PenumbraService.EnsureTextureMappingForRegression(s);
                if (TextureFiles.Hash(File.ReadAllBytes(s.TargetFile)) != s.LastCommittedHash) throw new TextureConflictException("Destination changed");
                backup = TextureFiles.Replace(s.TargetFile, ModRoot, s.RelativePath, "Mod", tex, s.LastCommittedHash, _backups, current, token);
            }
            Interlocked.Increment(ref Commits);
            return Task.FromResult(new TextureCommit(TextureFiles.Hash(tex), backup, "Saved"));
        }
        public Task<TextureVariantCommit> CommitVariantAsync(TextureEditSession s, TextureVariant variant, byte[] tex, Func<bool> current, CancellationToken token)
        {
            var result = PenumbraService.CommitTextureVariantFiles(s, variant, tex, _backups, current, token);
            Interlocked.Increment(ref VariantCommits);
            return Task.FromResult(result);
        }
        public Task<string> RefreshAsync(IReadOnlyList<TextureRefresh> items, CancellationToken token)
        {
            LastShown = items[^1].ShowOption;
            Interlocked.Increment(ref Refreshes);
            RefreshedMods.Add(items.Select(item => item.Session.ModDirectory).Distinct().Count());
            return Task.FromResult(RefreshWarning ? "Texture saved; refresh needs attention" : "Saved and redrawn");
        }
        public int Refreshes;
        public readonly List<int> RefreshedMods = [];
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
    internal static byte[] Tex(uint format, int width, int height, int mips, byte value)
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

    /// <summary> The texture as old mod tools wrote it: offsets for uncompressed mips, and one more mip claimed than stored. </summary>
    private static byte[] UncompressedOffsets(byte[] tex)
    {
        var bytes = (byte[])tex.Clone();
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)), height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        var mips = ++bytes[14];
        var offset = 80;
        for (var m = 0; m < mips; m++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + m * 4), (uint)offset);
            offset += Math.Max(1, width >> m) * Math.Max(1, height >> m) * 4;
        }
        return bytes;
    }
}
