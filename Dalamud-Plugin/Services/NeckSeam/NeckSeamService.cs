using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Services.Skeletons;
using Lumina.Data;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.NeckSeam;

/// <summary> A measured seam and the actor and files it was measured on. </summary>
internal sealed record NeckSeamAnalysis(NeckSeamReport Report, NeckSeamCaptured Captured, string ActorName, ushort ObjectIndex, nint Address)
{
    /// <summary> Mod directories of neck seam previews among the face's files: measured with a preview active. </summary>
    public IReadOnlyList<string> PreviewSources { get; init; } = [];
}

/// <summary> What applying or creating a preview did, with follow-up warnings. </summary>
internal sealed record NeckSeamOutcome(string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// The neck seam workflow: measure the seam of an on-screen character, put the fixed face files in a
/// new preview mod, then either write them over the source mods' files (with managed backups) or
/// delete the preview. Previews are remembered across plugin reloads.
/// </summary>
internal sealed class NeckSeamService
{
    /// <summary> The mod name used when a character's name can't name a folder. </summary>
    private const string FixModFallback = "Neck Seam Fix";
    private const string Recheck = "Measure the seam again";
    private readonly PenumbraService _penumbra;
    private readonly PreviewModService _previews;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Func<bool> _recompress;

    public NeckSeamService(PenumbraService penumbra, IDataManager data, IPluginLog log, string configDirectory, Func<bool> recompress)
    {
        _penumbra = penumbra;
        _previews = new PreviewModService(penumbra);
        _data = data;
        _log = log;
        _recompress = recompress;
        Store = new NeckSeamPreviewStore(configDirectory);
        Store.Load();
        if (Store.LoadError.Length > 0)
            log.Warning(Store.LoadError);
    }

    public NeckSeamPreviewStore Store { get; }

    public NeckSeamPreview? PreviewFor(string actorName)
        => Store.Previews.LastOrDefault(p => string.Equals(p.ActorName, actorName, StringComparison.Ordinal));

    public async Task<NeckSeamAnalysis> AnalyzeAsync(OnScreenObject actor, CancellationToken token)
    {
        byte[]? pbd = null;
        try { pbd = (await _data.GetFileAsync<FileResource>(RacialDeformer.GamePath, token).ConfigureAwait(false))?.Data; }
        catch (Exception e) when (e is not OperationCanceledException) { _log.Debug(e, "Could not read the racial deformer file."); }
        return await Task.Run(() =>
        {
            var captured = NeckSeamCapture.Capture(actor.ResourceRoots, Read, pbd);
            token.ThrowIfCancellationRequested();
            var report = NeckSeamAnalyzer.Analyze(captured.Input);
            var previews = Store.Previews.Select(p => p.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previewSources = captured.Sources.Values.Where(s => previews.Contains(s.ModDirectory)).Select(s => s.ModDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new NeckSeamAnalysis(report, captured, actor.Name, actor.ObjectIndex, actor.Address) { PreviewSources = previewSources };
        }, token).ConfigureAwait(false);
    }

    private byte[]? Read(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length is > 0 and <= NeckSeamCapture.MaxFileBytes ? File.ReadAllBytes(info.FullName) : null;
            }
            return _data.GetFileAsync<FileResource>(PathRules.NormalizeGamePath(path), CancellationToken.None).GetAwaiter().GetResult()?.Data;
        }
        catch (Exception e)
        {
            _log.Debug(e, "Could not read {Path} for the neck seam.", path);
            return null;
        }
    }

    public static Task<NeckSeamFix> BuildFixAsync(NeckSeamAnalysis analysis, NeckSeamFixOptions options, CancellationToken token)
        => Task.Run(() => NeckSeamFixer.Build(analysis.Report, options), token);

    /// <summary>
    /// Puts the fixed face files in a new Penumbra mod enabled for the character. Changed textures get
    /// new game paths (the face material in the mod points at them), so other materials that share
    /// the original textures don't change while previewing.
    /// </summary>
    public async Task<(NeckSeamPreview Preview, NeckSeamOutcome Outcome)> CreatePreviewAsync(NeckSeamAnalysis analysis, NeckSeamFix fix, CancellationToken token)
    {
        if (fix.Empty)
            throw new InvalidOperationException("The selected fixes don't change anything.");
        if (analysis.PreviewSources.Count > 0)
            throw new InvalidOperationException("A neck seam preview is active for this character. Apply or discard it first, then measure again.");
        var report = analysis.Report;
        var id = Guid.NewGuid();
        var tag = id.ToString("N")[..8];
        var files = new List<(string GamePath, byte[] Bytes)>();
        var records = new List<NeckSeamPreviewFile>();
        NeckSeamSource SourceOf(string gamePath) => analysis.Captured.Source(gamePath)
            ?? throw new InvalidDataException($"The source of {gamePath} is unknown; measure the seam again.");

        var material = SkinMaterial.Read(report.FaceMaterialBytes);
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "neck-seam", id.ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var texture in fix.Textures)
            {
                var index = material.Samplers[texture.Sampler];
                var stored = material.Textures[index];
                var previewStored = PreviewTexturePath(stored, tag);
                var previewRequested = PathRules.Dx11TexturePath(previewStored, material.TextureFlags[index]);
                var bytes = await EncodeAsync(texture, work, token).ConfigureAwait(false);
                rewrites[stored] = previewStored;
                files.Add((previewRequested, bytes));
                records.Add(new NeckSeamPreviewFile
                {
                    Kind = "face " + NeckSeamFixer.Label(texture.Sampler) + " texture", GamePath = texture.GamePath, PreviewGamePath = previewRequested,
                    PreviewRelativePath = "Files/" + previewRequested, PreviewSha256 = NeckSeamCapture.Hash(bytes), Source = SourceOf(texture.GamePath),
                });
            }
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e, "Could not remove the neck seam work folder."); }
        }

        var materialBytes = fix.Material ?? (rewrites.Count > 0 ? report.FaceMaterialBytes : null);
        if (materialBytes is not null)
        {
            var previewMaterial = rewrites.Count > 0 ? PenumbraService.RewriteMaterialTexturePaths(materialBytes, rewrites) : materialBytes;
            files.Add((report.FaceMaterialPath, previewMaterial));
            records.Add(new NeckSeamPreviewFile
            {
                Kind = "face material", GamePath = report.FaceMaterialPath, PreviewGamePath = report.FaceMaterialPath,
                PreviewRelativePath = "Files/" + report.FaceMaterialPath, PreviewSha256 = NeckSeamCapture.Hash(previewMaterial),
                Source = SourceOf(report.FaceMaterialPath),
                TextureRewrites = rewrites.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase),
            });
        }
        if (fix.BodyMaterial is not null)
        {
            // The body's skin material is shared by every body part, so its path is kept: the whole body previews the change.
            files.Add((report.BodyMaterialPath, fix.BodyMaterial));
            records.Add(new NeckSeamPreviewFile
            {
                Kind = "body material", GamePath = report.BodyMaterialPath, PreviewGamePath = report.BodyMaterialPath,
                PreviewRelativePath = "Files/" + report.BodyMaterialPath, PreviewSha256 = NeckSeamCapture.Hash(fix.BodyMaterial),
                Source = SourceOf(report.BodyMaterialPath),
            });
        }
        if (fix.Model is not null)
        {
            files.Add((report.FaceModelPath, fix.Model));
            records.Add(new NeckSeamPreviewFile
            {
                Kind = "face model", GamePath = report.FaceModelPath, PreviewGamePath = report.FaceModelPath,
                PreviewRelativePath = "Files/" + report.FaceModelPath, PreviewSha256 = NeckSeamCapture.Hash(fix.Model), Source = SourceOf(report.FaceModelPath),
            });
        }

        var description = "Neck seam preview made by XIV Instant Edit for " + analysis.ActorName + ":\n" + string.Join("\n", fix.Changes) +
                          "\n\nApply or discard it from the neck seam dialog (the face model's ⋯ menu on the On Screen tab).";
        var mod = await _previews.CreateAsync($"Neck Seam Preview - {analysis.ActorName}", description,
            files.Select(file => PreviewModEntry.At(file.GamePath, file.Bytes)).ToList(), analysis.ObjectIndex, FixModFallback, token).ConfigureAwait(false);
        var preview = new NeckSeamPreview
        {
            Id = id, ModDirectory = mod.ModDirectory, ModIdentifier = mod.Identifier, CollectionId = mod.CollectionId, CollectionName = mod.CollectionName,
            ActorName = analysis.ActorName, ObjectIndex = analysis.ObjectIndex, Created = DateTimeOffset.UtcNow, Files = records, Changes = fix.Changes.ToList(),
        };
        Store.Add(preview);
        return (preview, new NeckSeamOutcome($"Created the preview mod {mod.ModDirectory} in {mod.CollectionName}.", mod.Warnings));
    }

    /// <summary> The texture's folder, its name with the preview's tag: …/c0801f0001_fac_norm.tex → …/c0801f0001_fac_norm_ns1a2b3c4d.tex. </summary>
    internal static string PreviewTexturePath(string stored, string tag)
    {
        var normalized = PathRules.NormalizeGamePath(stored);
        var slash = normalized.LastIndexOf('/');
        var name = Path.GetFileNameWithoutExtension(normalized[(slash + 1)..]);
        return (slash < 0 ? "" : normalized[..(slash + 1)]) + name + "_ns" + tag + ".tex";
    }

    private async Task<byte[]> EncodeAsync(NeckSeamTextureOutput texture, string work, CancellationToken token)
    {
        var type = TextureType.RgbaTex;
        var mips = true;
        try
        {
            var header = TextureFiles.ReadTex(texture.Original);
            mips = header.Mips > 1;
            if (_recompress())
                type = TextureFiles.OutputType(header.Format);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            // Formats the texture editor can't preserve are saved uncompressed.
        }
        var label = NeckSeamFixer.Label(texture.Sampler);
        var tga = Path.Combine(work, label + ".tga");
        var output = Path.Combine(work, label + ".tex");
        await File.WriteAllBytesAsync(tga, TgaImage.WriteBgra32(new RgbaImage(texture.Image.Width, texture.Image.Height, texture.Image.Rgba)), token)
            .ConfigureAwait(false);
        await ((ITextureEditBackend)_penumbra).ConvertAsync(tga, output, type, mips).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        _ = TextureFiles.ReadTex(bytes);
        return bytes;
    }

    /// <summary> What <see cref="ApplyAsync"/> would write, for the confirmation. </summary>
    public static IReadOnlyList<(string Kind, NeckSeamSource Source)> ApplyTargets(NeckSeamPreview preview)
        => preview.Files.Select(f => (f.Kind, f.Source)).ToList();

    /// <summary>
    /// Writes the preview's files over their sources: mod files in place (backed up first), game data
    /// into a new "Neck Seam Fix" mod. The face material gets its original texture paths back, since
    /// the textures themselves are replaced. Then the preview mod is deleted.
    /// </summary>
    public async Task<NeckSeamOutcome> ApplyAsync(NeckSeamPreview preview)
    {
        var result = await _previews.ApplyAsync(Shared(preview), $"Neck Seam Fix - {preview.ActorName}",
            "Neck seam fix made by XIV Instant Edit for game files " + preview.ActorName + " uses.", FixModFallback, Recheck).ConfigureAwait(false);
        Store.Remove(preview.Id);
        return new NeckSeamOutcome(result.Describe("Neck seam fix applied"), result.Warnings);
    }

    public async Task<NeckSeamOutcome> DiscardAsync(NeckSeamPreview preview)
    {
        var result = await _previews.DiscardAsync(Shared(preview)).ConfigureAwait(false);
        if (result.Removed)
            Store.Remove(preview.Id);
        return new NeckSeamOutcome(result.Message, result.Warnings);
    }

    /// <summary> The preview as the shared preview-mod workflow sees it. </summary>
    private static PreviewMod Shared(NeckSeamPreview preview) => new()
    {
        Id = preview.Id, ModDirectory = preview.ModDirectory, ModIdentifier = preview.ModIdentifier, CollectionId = preview.CollectionId,
        CollectionName = preview.CollectionName, ActorName = preview.ActorName, ObjectIndex = preview.ObjectIndex, Created = preview.Created,
        Changes = preview.Changes,
        Files = preview.Files.Select(file => new PreviewModFile
        {
            Kind = file.Kind, GamePath = file.GamePath, PreviewRelativePath = file.PreviewRelativePath, PreviewSha256 = file.PreviewSha256,
            Source = file.Source, TextureRewrites = file.TextureRewrites,
        }).ToList(),
    };
}
