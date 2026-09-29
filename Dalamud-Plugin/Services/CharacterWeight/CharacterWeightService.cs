using System.Buffers;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;
using Lumina.Data;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.CharacterWeight;

/// <summary> A measured character and the actor it was measured on. </summary>
internal sealed record CharacterWeightAnalysis(CharacterWeightReport Report, string ActorName, ushort ObjectIndex, DateTimeOffset Measured)
{
    /// <summary> For mod textures, by texture key: the options of the mod that map the file. </summary>
    public IReadOnlyDictionary<string, ModFileUsage> Usage { get; init; } = new Dictionary<string, ModFileUsage>();
}

/// <summary> What creating, applying or discarding a preview did, with follow-up warnings. </summary>
internal sealed record CharacterWeightOutcome(string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// The character weight workflow: measure what a character costs in texture memory and triangles,
/// put smaller versions of the chosen textures in a preview mod, then write them over the source
/// mods' files (game files go into a new mod) or delete the preview. Textures are encoded by
/// Penumbra, as Mod Optimizer does. Previews are remembered across plugin reloads.
/// </summary>
internal sealed class CharacterWeightService
{
    private const string FixModFallback = "Character Weight Fix";
    private const string Recheck = "Check the weight again";
    private readonly PenumbraService _penumbra;
    private readonly PreviewModService _previews;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly IReadOnlyList<IPreviewModRegistry> _otherPreviews;

    /// <param name="otherPreviews">Other Quick Actions' previews, whose files the weight check leaves alone.</param>
    public CharacterWeightService(PenumbraService penumbra, IDataManager data, IPluginLog log, string configDirectory,
        IReadOnlyList<IPreviewModRegistry> otherPreviews)
    {
        _penumbra = penumbra;
        _previews = new PreviewModService(penumbra);
        _data = data;
        _log = log;
        _otherPreviews = otherPreviews;
        Store = new PreviewModStore<PreviewMod>(configDirectory, "CharacterWeightPreviews.json", "character weight previews");
        Store.Load();
        if (Store.LoadError.Length > 0)
            log.Warning(Store.LoadError);
    }

    public PreviewModStore<PreviewMod> Store { get; }

    public PreviewMod? PreviewFor(string actorName) => Store.PreviewFor(actorName);

    private bool IsPreviewMod(string modDirectory)
        => modDirectory.Length > 0 && (Store.HoldsMod(modDirectory) || _otherPreviews.Any(store => store.HoldsMod(modDirectory)));

    /// <param name="progress">Called with the files read so far and the total.</param>
    public Task<CharacterWeightAnalysis> AnalyzeAsync(OnScreenObject actor, Action<int, int>? progress, CancellationToken token)
        => Task.Run(() =>
        {
            var report = CharacterWeightCapture.Capture(actor.ResourceRoots, SampleTexture, ReadModel, IsPreviewMod, token, progress);
            var usage = new Dictionary<string, ModFileUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in report.Textures.Where(t => t.Source.IsModFile && t.Source.ModRootPath.Length > 0)
                         .GroupBy(t => t.Source.ModRootPath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var found = PenumbraService.DescribeModFileUsages(mod.Key, mod.Select(t => t.Source.RelativePath));
                foreach (var texture in mod)
                    if (found.TryGetValue(texture.Source.RelativePath, out var used))
                        usage[texture.Key] = used;
            }
            return new CharacterWeightAnalysis(report, actor.Name, actor.ObjectIndex, DateTimeOffset.UtcNow) { Usage = usage };
        }, token);

    /// <summary> The first bytes, length and hash of a texture, streamed so large mod files aren't held in memory. </summary>
    private WeightFileSample? SampleTexture(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
                return ReadGameFile(path) is { } game ? new WeightFileSample(game[..Math.Min(game.Length, TextureCost.HeaderSize)], game.Length, PreviewSource.Hash(game)) : null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var head = new byte[(int)Math.Min(stream.Length, TextureCost.HeaderSize)];
            stream.ReadExactly(head);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(head);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return new WeightFileSample(head, stream.Length, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not read {Path} for the weight check.", path);
            return null;
        }
    }

    private byte[]? ReadModel(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
                return ReadGameFile(path);
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= CharacterWeightCapture.MaxModelBytes ? File.ReadAllBytes(info.FullName) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not read {Path} for the weight check.", path);
            return null;
        }
    }

    private byte[]? ReadGameFile(string gamePath)
        => _data.GetFileAsync<FileResource>(PathRules.NormalizeGamePath(gamePath), CancellationToken.None).GetAwaiter().GetResult()?.Data;

    /// <summary>
    /// Encodes the chosen textures through Penumbra and puts them in a new mod enabled for the
    /// character, at the game paths it loads them from. Each source is read again and must still hash
    /// as it did when measured.
    /// </summary>
    public async Task<CharacterWeightOutcome> CreatePreviewAsync(CharacterWeightAnalysis analysis, IReadOnlyDictionary<string, ShrinkChoice> choices,
        Action<string> progress, CancellationToken token)
    {
        if (Store.Previews.Any(p => string.Equals(p.ActorName, analysis.ActorName, StringComparison.Ordinal)))
            throw new InvalidOperationException("A weight preview is active for this character. Apply or discard it first, then check again.");
        var chosen = analysis.Report.Textures
            .Select(texture => (Texture: texture, Choice: choices.TryGetValue(texture.Key, out var choice) ? choice : ShrinkChoice.None))
            .Where(item => !item.Choice.IsNone && ShrinkRules.IsValid(item.Texture, item.Choice))
            .ToList();
        if (chosen.Count == 0)
            throw new InvalidOperationException("No texture is ticked.");

        var id = Guid.NewGuid();
        var entries = new List<PreviewModEntry>();
        var records = new List<PreviewModFile>();
        var changes = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "character-weight", id.ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (texture, choice) = chosen[i];
                progress($"Converting texture {i + 1} of {chosen.Count}: {texture.FileName}");
                var bytes = await EncodeAsync(texture, choice, Path.Combine(work, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), token)
                    .ConfigureAwait(false);
                var relative = "Files/" + texture.GamePaths[0];
                entries.Add(new PreviewModEntry(relative, bytes, texture.GamePaths));
                records.Add(new PreviewModFile
                {
                    Kind = "texture", GamePath = texture.GamePaths[0], GamePaths = texture.GamePaths.ToList(), PreviewRelativePath = relative,
                    PreviewSha256 = PreviewSource.Hash(bytes), Source = texture.Source,
                    Note = analysis.Usage.TryGetValue(texture.Key, out var usage) ? CharacterWeightViews.Usage(usage) : string.Empty,
                });
                changes.Add(CharacterWeightViews.Change(texture, choice));
            }
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e, "Could not remove the weight check's work folder."); }
        }

        progress("Creating the preview mod");
        var description = "Smaller textures made by XIV Instant Edit for " + analysis.ActorName + ":\n" + string.Join("\n", changes) +
                          "\n\nApply or discard them from XIV Instant Edit's Quick Actions tab (Character weight).";
        var mod = await _previews.CreateAsync($"Character Weight Preview - {analysis.ActorName}", description, entries, analysis.ObjectIndex,
            FixModFallback, token).ConfigureAwait(false);
        Store.Add(new PreviewMod
        {
            Id = id, ModDirectory = mod.ModDirectory, ModIdentifier = mod.Identifier, CollectionId = mod.CollectionId, CollectionName = mod.CollectionName,
            ActorName = analysis.ActorName, ObjectIndex = analysis.ObjectIndex, Created = DateTimeOffset.UtcNow, Files = records, Changes = changes,
        });
        return new CharacterWeightOutcome($"Created the preview mod {mod.ModDirectory} in {mod.CollectionName}.", mod.Warnings);
    }

    /// <summary>
    /// One texture's new file: Penumbra decodes it, the size is halved here if asked, and Penumbra
    /// encodes the result with a full mip chain. The source is copied into the work folder first, so
    /// what gets converted is exactly what was hashed, with miswritten mip offsets corrected.
    /// </summary>
    private async Task<byte[]> EncodeAsync(WeightTexture texture, ShrinkChoice choice, string work, CancellationToken token)
    {
        var source = await ReadSourceAsync(texture, token).ConfigureAwait(false);
        var info = texture.Info!.Value;
        var target = ShrinkRules.TargetFormat(texture, choice.Format);
        var (width, height) = ShrinkRules.SizeAfter(info, choice.Halvings);
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "source.tex");
        var output = Path.Combine(work, "output.tex");
        await File.WriteAllBytesAsync(input, TextureFiles.NormalizeMipOffsets(source), token).ConfigureAwait(false);
        var backend = (ITextureEditBackend)_penumbra;
        if (choice.Halvings > 0)
        {
            var decoded = Path.Combine(work, "decoded.tex");
            await backend.ConvertAsync(input, decoded, TextureType.RgbaTex, false).ConfigureAwait(false);
            var (pixels, w, h) = TextureHalving.ReadBgra(await File.ReadAllBytesAsync(decoded, token).ConfigureAwait(false));
            for (var step = 0; step < choice.Halvings; step++)
            {
                token.ThrowIfCancellationRequested();
                pixels = TextureHalving.Halve(pixels, w, h);
                w /= 2;
                h /= 2;
            }
            var tga = Path.Combine(work, "halved.tga");
            await File.WriteAllBytesAsync(tga, TextureHalving.Tga(pixels, w, h), token).ConfigureAwait(false);
            input = tga;
        }
        await backend.ConvertAsync(input, output, TextureFiles.OutputType(target), true).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        TextureFiles.ValidateOutput(bytes, target, width, height, true);
        return bytes;
    }

    private async Task<byte[]> ReadSourceAsync(WeightTexture texture, CancellationToken token)
    {
        var source = texture.Source;
        byte[]? bytes;
        if (source.IsModFile)
        {
            var info = new FileInfo(source.ActualPath);
            bytes = info.Exists && info.Length <= TextureFiles.MaxBytes ? await File.ReadAllBytesAsync(info.FullName, token).ConfigureAwait(false) : null;
        }
        else
            bytes = (await _data.GetFileAsync<FileResource>(PathRules.NormalizeGamePath(source.ActualPath), token).ConfigureAwait(false))?.Data;
        if (bytes is null)
            throw new IOException($"{source.Label} could not be read. {Recheck}.");
        if (!string.Equals(PreviewSource.Hash(bytes), source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{source.Label} changed since the weight check. {Recheck}.");
        return bytes;
    }

    /// <summary> Writes the preview's textures over their sources (backed up first); game files go into a new mod. Then the preview is deleted. </summary>
    public async Task<CharacterWeightOutcome> ApplyAsync(PreviewMod preview)
    {
        var result = await _previews.ApplyAsync(preview, $"Character Weight Fix - {preview.ActorName}",
            "Smaller game textures made by XIV Instant Edit for " + preview.ActorName + ".", FixModFallback, Recheck).ConfigureAwait(false);
        Store.Remove(preview.Id);
        return new CharacterWeightOutcome(result.Describe("Smaller textures applied"), result.Warnings);
    }

    public async Task<CharacterWeightOutcome> DiscardAsync(PreviewMod preview)
    {
        var result = await _previews.DiscardAsync(preview).ConfigureAwait(false);
        if (result.Removed)
            Store.Remove(preview.Id);
        return new CharacterWeightOutcome(result.Message, result.Warnings);
    }
}
