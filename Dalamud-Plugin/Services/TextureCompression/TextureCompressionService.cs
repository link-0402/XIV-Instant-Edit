using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;
using Lumina.Data;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services.TextureCompression;

/// <summary> What automatic compression is doing, for the card. </summary>
internal enum CompressionPhase
{
    Off,
    /// <summary> On, waiting for the character to load new textures. </summary>
    Watching,
    /// <summary> New textures loaded; waiting until the character is out of combat, cutscenes, group pose and zone changes. </summary>
    Waiting,
    Checking,
    Compressing,
    Restoring,
}

/// <summary> One run: what it compressed, how much smaller the files got, and what it kept or couldn't do. </summary>
internal sealed record CompressionRun
{
    public DateTimeOffset Finished { get; init; }
    public int Compressed { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
    /// <summary> "file: reason" for each texture of the character the check keeps as it is. </summary>
    public IReadOnlyList<string> Kept { get; init; } = [];
    /// <summary> "file: error" for textures that couldn't be compressed. </summary>
    public IReadOnlyList<string> Failed { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// What the card shows after a run. A run that compressed nothing and ran into no problems, such
    /// as the one after the redraw that ends a run, or one for an outfit compressed before, only
    /// brings the list of kept textures up to date, so the last compression and its problems stay.
    /// </summary>
    public static CompressionRun After(CompressionRun? previous, CompressionRun run)
        => previous is null || run.Compressed > 0 || run.Failed.Count > 0 || run.Warnings.Count > 0 ? run : previous with { Kept = run.Kept };
}

/// <summary> Which of the local player's loads that Penumbra reports can bring new textures. Dalamud-free. </summary>
internal static class CompressionTrigger
{
    /// <summary>
    /// Models, materials and textures. Penumbra reports the textures a material loads without the
    /// character they belong to, so gear textures never arrive as such: the character's materials
    /// (and the models that load them) stand for them. Textures that do come with the character, such
    /// as face paint, count too. Materials count even when no mod replaces them, since a game
    /// material can read a mod's texture.
    /// </summary>
    public static bool LoadsTextures(string gamePath)
        => gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) || gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
           gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
}

/// <summary> What restoring did: originals written back, and files it left alone. </summary>
internal sealed record CompressionRestore(int Restored, IReadOnlyList<string> Changed, IReadOnlyList<string> Failed, IReadOnlyList<string> Warnings);

/// <summary>
/// Compresses the uncompressed mod textures the local player wears, as they are loaded. While it is
/// on, every model and material Penumbra loads for the player schedules a run once loading settles
/// and the player is free (see <see cref="CompressionTrigger"/>). A run reads the player's resource
/// tree, has Penumbra encode each uncompressed 2D texture (BC5 for index maps, BC7 otherwise;
/// mipmaps only when the original has them), decodes the result and keeps it only when
/// <see cref="CompressionCheck"/> finds that what the shaders read stays the same. The original is
/// copied into the cache folder first, then the compressed file replaces it in its mod; the mods are
/// reloaded and the player redrawn once.
/// Restoring writes the originals back over files that still hold what was compressed.
/// </summary>
internal sealed class TextureCompressionService : IDisposable
{
    /// <summary> How long a run waits after the character's last model or material load, so an outfit change is taken in one go. </summary>
    private const long SettleMs = 3000;
    private const long MaxMaterialBytes = 4L * 1024 * 1024;
    private const string Recheck = "It is looked at again the next time it loads";
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(3);

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly PenumbraService _penumbra;
    private readonly OnScreenService _onScreen;
    private readonly TextureEditService _textures;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pi;
    private readonly IReadOnlyList<IPreviewModRegistry> _previews;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sessionLock = new();
    // Textures Penumbra couldn't convert this session, by path and content, so every load doesn't retry them.
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    // Textures compressed this session, by path and original content. One that is put back as it was,
    // as a tool that writes it again after every redraw would, is left alone instead of compressed,
    // reloaded and redrawn over and over.
    private readonly HashSet<string> _compressedOriginals = new(StringComparer.OrdinalIgnoreCase);
    // Each texture's hash as a run last read it, with the file's size and write time then, so the run
    // after every outfit change doesn't read the textures the check kept again.
    private readonly ConcurrentDictionary<string, (FileStamp Stamp, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private EventSubscriber<nint, string, string>? _resolved;
    private CancellationTokenSource? _run;
    private CompressionRun? _lastRun;
    private nint _localPlayer;
    private long _dueAt;
    private int _busy;
    private volatile CompressionPhase _phase;
    private volatile string _progress = string.Empty;

    public TextureCompressionService(Configuration config, Action saveConfig, PenumbraService penumbra, OnScreenService onScreen,
        TextureEditService textures, IFramework framework, IObjectTable objects, IClientState clientState, ICondition condition,
        IDataManager data, IPluginLog log, IDalamudPluginInterface pi, string configDirectory, IReadOnlyList<IPreviewModRegistry> previews)
    {
        _config = config;
        _saveConfig = saveConfig;
        _penumbra = penumbra;
        _onScreen = onScreen;
        _textures = textures;
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _condition = condition;
        _data = data;
        _log = log;
        _pi = pi;
        _previews = previews;
        Backups = new TextureBackupStore(configDirectory);
        Backups.Load();
        if (Backups.LoadError.Length > 0)
            log.Warning(Backups.LoadError);
        _phase = config.AutoCompressTextures ? CompressionPhase.Watching : CompressionPhase.Off;
        framework.Update += OnUpdate;
        clientState.Login += OnLogin;
        if (config.AutoCompressTextures)
        {
            Subscribe();
            Schedule(SettleMs);
        }
    }

    public TextureBackupStore Backups { get; }
    public bool Enabled => _config.AutoCompressTextures;
    public CompressionPhase Phase => _phase;
    /// <summary> "Compressing 2 of 5: top_d.tex" while a run works on a texture; empty otherwise. </summary>
    public string Progress => _progress;
    public CompressionRun? LastRun => Volatile.Read(ref _lastRun);
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary> Turns automatic compression on or off. Turning it on looks at what the character wears now. </summary>
    public void SetEnabled(bool enabled)
    {
        if (_config.AutoCompressTextures != enabled)
        {
            _config.AutoCompressTextures = enabled;
            _saveConfig();
        }
        if (enabled)
        {
            Subscribe();
            if (!Busy)
                _phase = CompressionPhase.Watching;
            Schedule(0);
            return;
        }
        Unsubscribe();
        Volatile.Write(ref _dueAt, 0);
        CancelRun();
        if (!Busy)
            _phase = CompressionPhase.Off;
    }

    private void Subscribe()
    {
        if (_resolved is not null)
            return;
        try
        {
            _resolved = GameObjectResourcePathResolved.Subscriber(_pi, OnResolved);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not subscribe to Penumbra's resolved paths; textures are compressed when compression is turned on or you log in.");
        }
    }

    private void Unsubscribe()
    {
        _resolved?.Dispose();
        _resolved = null;
    }

    private void OnLogin()
    {
        if (_config.AutoCompressTextures)
            Schedule(SettleMs);
    }

    /// <summary>
    /// Penumbra resolved a path for a game object, on whichever thread loads it. A model, material or
    /// texture of the local player schedules a run once loading settles. Must stay cheap.
    /// </summary>
    private void OnResolved(nint gameObject, string gamePath, string resolvedPath)
    {
        if (gameObject == 0 || gameObject != Volatile.Read(ref _localPlayer) || !CompressionTrigger.LoadsTextures(gamePath))
            return;
        Schedule(SettleMs);
    }

    private void Schedule(long delayMs) => Volatile.Write(ref _dueAt, Math.Max(1, Environment.TickCount64 + delayMs));

    /// <summary> Every frame: remembers the local player for <see cref="OnResolved"/> and starts a due run once the player is free. </summary>
    private void OnUpdate(IFramework framework)
    {
        var player = _objects.LocalPlayer;
        Volatile.Write(ref _localPlayer, player?.Address ?? 0);
        var due = Volatile.Read(ref _dueAt);
        if (due == 0 || !_config.AutoCompressTextures || Environment.TickCount64 < due || Busy)
            return;
        if (player is null || !_clientState.IsLoggedIn || Occupied() || !_penumbra.Available)
        {
            if (_phase == CompressionPhase.Watching)
                _phase = CompressionPhase.Waiting;
            return;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        // A load reported since due was read gets a run of its own after this one.
        Interlocked.CompareExchange(ref _dueAt, 0, due);
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Volatile.Write(ref _run, run);
        _phase = CompressionPhase.Checking;
        _ = Task.Run(() => CompressAsync(run.Token));
    }

    /// <summary> Group pose, cutscenes, combat and zone changes: the redraw at the end would reset a pose, flicker in a fight or hit a loading character. </summary>
    private bool Occupied()
        => _clientState.IsGPosing || _condition[ConditionFlag.InCombat] || _condition[ConditionFlag.BetweenAreas] ||
           _condition[ConditionFlag.BetweenAreas51] || _condition[ConditionFlag.OccupiedInCutSceneEvent] ||
           _condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.WatchingCutscene78];

    private void CancelRun()
    {
        try
        {
            Volatile.Read(ref _run)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished.
        }
    }

    /// <summary> What one run did so far. </summary>
    private sealed class RunTally
    {
        public readonly List<string> Kept = [];
        public readonly List<string> Failed = [];
        public readonly List<string> Warnings = [];
        public readonly HashSet<string> Mods = new(StringComparer.OrdinalIgnoreCase);
        public int Compressed;
        public long Before, After;
    }

    private async Task CompressAsync(CancellationToken token)
    {
        var tally = new RunTally();
        try
        {
            try
            {
                await CompressCharacterAsync(tally, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Turned off or unloading; what was written is still reloaded below.
            }
            catch (Exception e)
            {
                _log.Warning(e, "Automatic texture compression failed.");
                tally.Warnings.Add(e.Message);
            }
            await ReloadChangedModsAsync(tally).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastRun, CompressionRun.After(LastRun, new CompressionRun
            {
                Finished = DateTimeOffset.Now, Compressed = tally.Compressed, BytesBefore = tally.Before, BytesAfter = tally.After,
                Kept = tally.Kept, Failed = tally.Failed, Warnings = tally.Warnings,
            }));
            _progress = string.Empty;
            _phase = _config.AutoCompressTextures ? CompressionPhase.Watching : CompressionPhase.Off;
            var run = Interlocked.Exchange(ref _run, null);
            run?.Dispose();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary> Reads the character's textures and compresses the uncompressed ones the check lets through. </summary>
    private async Task CompressCharacterAsync(RunTally tally, CancellationToken token)
    {
        var actor = _onScreen.CaptureLocalPlayer(token);
        if (actor is null)
            return;
        var candidates = CompressionCapture.Collect(actor.ResourceRoots, ReadMaterial,
            directory => directory.Length > 0 && _previews.Any(store => store.HoldsMod(directory)));
        var edited = _textures.Sessions.SelectMany(EditedFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var work = new List<(CompressionCandidate Candidate, TexInfo Info)>();
        foreach (var candidate in candidates)
            if (!candidate.InPreview && candidate.Source.IsModFile && !edited.Contains(candidate.File) && ReadHeader(candidate.File) is { } info &&
                CompressionCapture.Compressible(info))
            {
                if (UnchangedVerdict(candidate.File) is { } reason)
                    tally.Kept.Add($"{candidate.FileName}: {reason}");
                else
                    work.Add((candidate, info));
            }
        if (work.Count == 0)
            return;
        // Nothing is written without a place for the originals.
        string cacheRoot;
        try
        {
            cacheRoot = TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            tally.Warnings.Add($"The cache folder can't hold the backups ({e.Message}), so nothing was compressed. Choose another cache folder in Settings.");
            return;
        }

        for (var i = 0; i < work.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (candidate, info) = work[i];
            _phase = CompressionPhase.Compressing;
            _progress = $"Compressing {i + 1} of {work.Count}: {candidate.FileName}";
            try
            {
                var outcome = await CompressOneAsync(candidate, info, cacheRoot, token).ConfigureAwait(false);
                if (outcome.Kept is { } reason)
                    tally.Kept.Add($"{candidate.FileName}: {reason}");
                else if (outcome.Entry is { } entry)
                {
                    tally.Compressed++;
                    tally.Before += entry.OriginalLength;
                    tally.After += entry.CompressedLength;
                    tally.Mods.Add(entry.ModDirectory);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warning(e, "Could not compress {File}.", candidate.File);
                tally.Failed.Add($"{candidate.FileName}: {e.Message}");
            }
        }
    }

    /// <summary> Reloads the mods whose files were replaced and redraws the character, once it is free again. </summary>
    private async Task ReloadChangedModsAsync(RunTally tally)
    {
        if (tally.Mods.Count == 0 || _lifetime.IsCancellationRequested)
            return;
        try
        {
            // A fight or cutscene may have started while the textures were encoded. Only Penumbra
            // calls follow, which run on the framework thread anyway.
            _progress = "Waiting until your character is free to reload the changed mods";
            while (await _framework.RunOnFrameworkThread(Occupied).ConfigureAwait(false))
                await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false);
            _progress = "Reloading the changed mods";
            tally.Warnings.AddRange(await _penumbra.ReloadModsAndRedrawPlayerAsync(tally.Mods).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Unloading; the files are written, and Penumbra reads them on the next load.
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not reload the mods after compressing textures.");
            tally.Warnings.Add("The changed mods couldn't be reloaded; redraw your character in Penumbra.");
        }
    }

    private sealed record Outcome(CompressedTexture? Entry, string? Kept);

    /// <summary>
    /// One texture: Penumbra encodes it and decodes the result, the check compares what its shaders
    /// read, and a passing texture is backed up, remembered and written over its mod file.
    /// </summary>
    private async Task<Outcome> CompressOneAsync(CompressionCandidate candidate, TexInfo info, string cacheRoot, CancellationToken token)
    {
        // Stamped before reading: a file that changes while it is read is read again by the next run.
        var stamp = FileStamp.Of(candidate.File);
        var original = await File.ReadAllBytesAsync(candidate.File, token).ConfigureAwait(false);
        if (original.LongLength > TextureFiles.MaxBytes)
            return new Outcome(null, "it is too large to convert");
        var sha = TextureBackupStore.Hash(original);
        if (stamp is { } read)
            _hashes[candidate.File] = (read, sha);
        if (Verdict(candidate.File, sha) is { } known)
            return new Outcome(null, known);

        var twoChannel = candidate.Uses.Count > 0 && candidate.Uses.All(use => use.Role == TextureRole.Index);
        var target = twoChannel ? TextureCost.Bc5 : TextureCost.Bc7;
        var source = TextureFiles.NormalizeMipOffsets(original);
        // Mipmaps only when the original has them: a texture drawn without them keeps its look far away.
        var mipMaps = (source[14] & 0x7F) > 1;
        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "texture-compression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "original.tex");
            var output = Path.Combine(work, "compressed.tex");
            var check = Path.Combine(work, "check.tex");
            await File.WriteAllBytesAsync(input, source, token).ConfigureAwait(false);
            byte[] encoded;
            (ArraySegment<byte> Pixels, int Width, int Height) decoded;
            ArraySegment<byte> originalPixels;
            try
            {
                await ConvertAsync(input, output, TextureFiles.OutputType(target), mipMaps).ConfigureAwait(false);
                encoded = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
                TextureFiles.ValidateOutput(encoded, target, info.Width, info.Height, mipMaps);
                await ConvertAsync(output, check, TextureType.RgbaTex, mipMaps).ConfigureAwait(false);
                decoded = TextureCost.TopLevelBgra(await File.ReadAllBytesAsync(check, token).ConfigureAwait(false));
                if (info.Format == TextureCost.Bgra8)
                    originalPixels = TextureCost.TopLevelBgra(source).Pixels;
                else
                {
                    var plain = Path.Combine(work, "plain.tex");
                    await ConvertAsync(input, plain, TextureType.RgbaTex, mipMaps).ConfigureAwait(false);
                    originalPixels = TextureCost.TopLevelBgra(await File.ReadAllBytesAsync(plain, token).ConfigureAwait(false)).Pixels;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_sessionLock)
                    _failed.Add(ContentKey(candidate.File, sha));
                throw;
            }
            if (decoded.Width != info.Width || decoded.Height != info.Height)
                throw new InvalidDataException("Penumbra decoded the compressed texture at another size.");

            var result = CompressionCheck.Compare(originalPixels, decoded.Pixels, info.Width, info.Height, candidate.Uses, twoChannel);
            if (!result.Passed)
            {
                _log.Information($"Kept {candidate.Label} uncompressed: {result.Problem} (cut-outs {result.CutoutChange:P3}, tile {result.CutoutTileChange:P1}; " +
                                 $"opacity {result.OpacityChange:P3}, tile {result.OpacityTileChange:P1}; rows {result.RowChange:P4}, tile {result.RowTileChange:P1}; " +
                                 $"normals p99 {result.NormalAngleP99:0.0} degrees; large errors {result.LargeErrorShare:P3}).");
                Backups.RecordKept(new KeptTexture { File = candidate.File, Sha256 = sha, Reason = result.Problem, CheckVersion = CompressionCheck.Version });
                return new Outcome(null, result.Problem);
            }
            token.ThrowIfCancellationRequested();

            // The original goes into the cache folder and the list first, so no crash can lose it.
            var backup = TextureBackupStore.StoreBackup(cacheRoot, original, sha);
            var entry = new CompressedTexture
            {
                File = candidate.File, ModDirectory = candidate.Source.ModDirectory, ModName = candidate.Source.ModName,
                ModStableId = candidate.Source.ModStableId, RelativePath = candidate.Source.RelativePath, Backup = backup,
                OriginalSha256 = sha, OriginalLength = original.LongLength, OriginalFormat = info.Format,
                CompressedSha256 = TextureBackupStore.Hash(encoded), CompressedLength = encoded.LongLength, CompressedFormat = target,
                Width = info.Width, Height = info.Height, Compressed = DateTimeOffset.UtcNow,
            };
            Backups.Record(entry);
            try
            {
                await _penumbra.ReplaceModFileAsync(candidate.Source with { Sha256 = sha }, sha, encoded, Recheck).ConfigureAwait(false);
            }
            catch
            {
                Backups.Forget([entry]);
                throw;
            }
            lock (_sessionLock)
                _compressedOriginals.Add(ContentKey(candidate.File, sha));
            _log.Information($"Compressed {candidate.Label} to {TextureCost.FormatName(target)}: {original.LongLength:N0} to {encoded.LongLength:N0} bytes.");
            return new Outcome(entry, null);
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e, "Could not remove a texture compression work folder."); }
        }
    }

    /// <summary>
    /// Why a texture stays as it is: the check kept this content, Penumbra couldn't convert it this
    /// session, or it was compressed this session and then put back as it was; null when none of these.
    /// </summary>
    private string? Verdict(string file, string sha)
    {
        if (Backups.KeptFor(file, sha, CompressionCheck.Version) is { } kept)
            return kept.Reason;
        var key = ContentKey(file, sha);
        lock (_sessionLock)
        {
            if (_failed.Contains(key))
                return "Penumbra couldn't convert it earlier this session";
            if (_compressedOriginals.Contains(key))
                return "it was compressed earlier this session, then something put the original back";
        }
        return null;
    }

    private static string ContentKey(string file, string sha) => file + "\n" + sha;

    /// <summary> <see cref="Verdict"/> without reading the file, when its size and write time are as a run last read it; null otherwise. </summary>
    private string? UnchangedVerdict(string file)
        => _hashes.TryGetValue(file, out var seen) && FileStamp.Of(file) == seen.Stamp ? Verdict(file, seen.Sha256) : null;

    /// <summary>
    /// Has Penumbra convert a file, waiting for it to finish even when a run is cancelled, since it
    /// writes into the work folder; only a conversion that hangs is given up on.
    /// </summary>
    private async Task ConvertAsync(string input, string output, TextureType type, bool mipMaps)
    {
        var conversion = ((ITextureEditBackend)_penumbra).ConvertAsync(input, output, type, mipMaps);
        if (await Task.WhenAny(conversion, Task.Delay(ConvertTimeout)).ConfigureAwait(false) != conversion)
            throw new TimeoutException("Penumbra took too long to convert the texture.");
        await conversion.ConfigureAwait(false);
    }

    private TexInfo? ReadHeader(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[TextureCost.HeaderSize];
            return stream.Read(head, 0, head.Length) == head.Length ? TextureCost.Read(head) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.Debug(e, "Could not read the header of {File}.", file);
            return null;
        }
    }

    /// <summary> A material of the tree, from its mod file or the game data, for the check to know how its textures are read. </summary>
    private SkinMaterial? ReadMaterial(ResourceNode node)
    {
        try
        {
            byte[]? bytes = null;
            if (node.SourceState == ResourceSourceState.GameData)
                bytes = _data.GetFileAsync<FileResource>(PathRules.NormalizeGamePath(node.ActualPath), CancellationToken.None).GetAwaiter().GetResult()?.Data;
            else if (node.SourceState == ResourceSourceState.LoadedMod && Path.IsPathRooted(node.ActualPath) &&
                     new FileInfo(node.ActualPath) is { Exists: true, Length: > 0 and <= MaxMaterialBytes } info)
                bytes = File.ReadAllBytes(info.FullName);
            return bytes is null ? null : SkinMaterial.Read(bytes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not read the material {Path} for texture compression.", node.ActualPath);
            return null;
        }
    }

    /// <summary> Files an open texture session writes to, which compression leaves to the session. </summary>
    private static IEnumerable<string> EditedFiles(TextureEditSession session)
    {
        if (session.ModRoot.Length == 0 || session.RelativePath.Length == 0)
            yield break;
        yield return Path.GetFullPath(session.TargetFile);
        foreach (var variant in session.Variants.Where(variant => variant.RelativePath.Length > 0))
            yield return Path.GetFullPath(session.VariantTargetFile(variant));
    }

    /// <summary>
    /// Turns automatic compression off, waits for a run to stop, then writes each original back over
    /// its file when the file still holds what was compressed. Files that changed since are left
    /// alone and keep their backups.
    /// </summary>
    public async Task<CompressionRestore> RestoreAsync()
    {
        SetEnabled(false);
        while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            await Task.Delay(100, _lifetime.Token).ConfigureAwait(false);
        var restored = 0;
        var changed = new List<string>();
        var failed = new List<string>();
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> warnings = [];
        try
        {
            _phase = CompressionPhase.Restoring;
            var entries = Backups.Compressed.ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                _progress = $"Restoring {i + 1} of {entries.Count}: {Path.GetFileName(entry.RelativePath)}";
                try
                {
                    if (!File.Exists(entry.File))
                    {
                        failed.Add($"{entry.Label}: the file is no longer there");
                        continue;
                    }
                    var current = TextureBackupStore.Hash(await File.ReadAllBytesAsync(entry.File).ConfigureAwait(false));
                    if (string.Equals(current, entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        ForgetRestored(entry);
                        restored++;
                        continue;
                    }
                    if (!string.Equals(current, entry.CompressedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        changed.Add(entry.Label);
                        continue;
                    }
                    var original = File.Exists(entry.Backup) ? await File.ReadAllBytesAsync(entry.Backup).ConfigureAwait(false) : null;
                    if (original is null || !string.Equals(TextureBackupStore.Hash(original), entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        failed.Add($"{entry.Label}: its backup is missing or damaged");
                        continue;
                    }
                    await _penumbra.ReplaceModFileAsync(SourceOf(entry), entry.CompressedSha256, original, "It was left as it is").ConfigureAwait(false);
                    ForgetRestored(entry);
                    restored++;
                    mods.Add(entry.ModDirectory);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    _log.Warning(e, "Could not restore {File}.", entry.File);
                    failed.Add($"{entry.Label}: {e.Message}");
                }
            }
            if (mods.Count > 0)
            {
                _progress = "Reloading the changed mods";
                warnings = await _penumbra.ReloadModsAndRedrawPlayerAsync(mods).ConfigureAwait(false);
            }
        }
        finally
        {
            _progress = string.Empty;
            _phase = _config.AutoCompressTextures ? CompressionPhase.Watching : CompressionPhase.Off;
            Interlocked.Exchange(ref _busy, 0);
        }
        return new CompressionRestore(restored, changed, failed, warnings);
    }

    /// <summary> Drops a restored texture's entry; turning compression back on compresses it again. </summary>
    private void ForgetRestored(CompressedTexture entry)
    {
        Backups.Forget([entry]);
        lock (_sessionLock)
            _compressedOriginals.Remove(ContentKey(entry.File, entry.OriginalSha256));
    }

    /// <summary> Forgets every backup and deletes the copies; the compressed files stay as they are. </summary>
    public int DeleteBackups()
    {
        var entries = Backups.Compressed.ToList();
        Backups.Forget(entries);
        return entries.Count;
    }

    private static PreviewSource SourceOf(CompressedTexture entry) => new()
    {
        GamePath = entry.RelativePath,
        ActualPath = entry.File,
        State = ResourceSourceState.LoadedMod,
        ModName = entry.ModName,
        ModDirectory = entry.ModDirectory,
        RelativePath = entry.RelativePath,
        ModStableId = entry.ModStableId,
        Sha256 = entry.CompressedSha256,
    };

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _clientState.Login -= OnLogin;
        Unsubscribe();
        _lifetime.Cancel();
        CancelRun();
    }
}
