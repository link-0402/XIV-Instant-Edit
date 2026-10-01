using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Services.Skeletons;
using Lumina.Data;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.NeckSeam;

/// <summary> The measured skin seams (the neck, when it could be measured, and the body seams) and the actor and files they were measured on. </summary>
internal sealed record NeckSeamAnalysis(NeckSeamReport? Report, NeckSeamCaptured Captured, string ActorName, ushort ObjectIndex, nint Address)
{
    /// <summary> Mod directories of skin seam previews among the measured files: measured with a preview active. </summary>
    public IReadOnlyList<string> PreviewSources { get; init; } = [];
    /// <summary> Why the neck wasn't measured (no face, the neck covered) when <see cref="Report"/> is null. </summary>
    public string NeckError { get; init; } = string.Empty;
    public BodySeamReport? Body { get; init; }
    public string BodyError { get; init; } = string.Empty;
    /// <summary> The drawn skin materials, for matching one's tone to another's. </summary>
    public SkinToneReport? Tone { get; init; }
    public string ToneError { get; init; } = string.Empty;
}

/// <summary> What applying or creating a preview did, with follow-up warnings. </summary>
internal sealed record NeckSeamOutcome(string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// The skin seam workflow: measure where an on-screen character's models meet (the face and body at
/// the neck, the body parts at the wrists, waist and ankles), put the fixed files in a new preview
/// mod, then either write them over the source mods' files (with managed backups) or delete the
/// preview. Previews are remembered across plugin reloads.
/// </summary>
internal sealed class NeckSeamService
{
    /// <summary> The mod name used when a character's name can't name a folder. </summary>
    private const string FixModFallback = "Skin Seam Fix";
    private const string Recheck = "Measure the seams again";
    private readonly PenumbraService _penumbra;
    private readonly PreviewModService _previews;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Func<bool> _recompress;
    private readonly PainterLiveReader? _live;

    /// <param name="live">Reads which parts and shape keys the game draws, so only drawn skin is compared.</param>
    public NeckSeamService(PenumbraService penumbra, IDataManager data, IPluginLog log, string configDirectory, Func<bool> recompress,
        PainterLiveReader? live = null)
    {
        _penumbra = penumbra;
        _previews = new PreviewModService(penumbra);
        _data = data;
        _log = log;
        _recompress = recompress;
        _live = live;
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
        PainterLiveCharacter? live = null;
        if (_live is not null)
        {
            try { live = await _live.ReadAsync(actor.ObjectIndex, actor.Address).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.Debug(e, "Could not read which parts the character draws."); }
        }
        return await Task.Run(() =>
        {
            var captured = NeckSeamCapture.Capture(actor.ResourceRoots, Read, pbd, live);
            token.ThrowIfCancellationRequested();
            // The clothing and seam connectors around the seams, read once for the neck and the body seams.
            var around = SeamSurroundings.None;
            try { around = SeamSurroundings.Read(captured.Input, captured.Input.CharacterRace); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.Warning(e, "Could not read the clothing and seam connectors around the seams."); }
            token.ThrowIfCancellationRequested();
            NeckSeamReport? report = null;
            var neckError = string.Empty;
            try { report = NeckSeamAnalyzer.Analyze(captured.Input, around); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { neckError = e.Message; }
            token.ThrowIfCancellationRequested();
            BodySeamReport? body = null;
            var bodyError = string.Empty;
            // The body seams are measured apart from the neck, so a model they can't handle leaves the neck's check usable.
            try { body = BodySeamAnalyzer.Analyze(captured.Input, around); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warning(e, "Could not measure the body seams.");
                bodyError = e.Message;
            }
            token.ThrowIfCancellationRequested();
            SkinToneReport? tone = null;
            var toneError = string.Empty;
            try { tone = SkinToneAnalyzer.Analyze(captured); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warning(e, "Could not measure the skin tones.");
                toneError = e.Message;
            }
            var previews = Store.Previews.Select(p => p.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previewSources = captured.Sources.Values.Where(s => previews.Contains(s.ModDirectory)).Select(s => s.ModDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new NeckSeamAnalysis(report, captured, actor.Name, actor.ObjectIndex, actor.Address)
            {
                PreviewSources = previewSources, NeckError = neckError, Body = body, BodyError = bodyError, Tone = tone, ToneError = toneError,
            };
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
            _log.Debug(e, "Could not read {Path} for the skin seams.", path);
            return null;
        }
    }

    /// <summary>
    /// Builds the selected fixes: the neck's (null options leave it out), then the body seams', whose
    /// skin material changes build on the neck's change to the same body material, then the skin tone
    /// match, which reads the base material as those fixes left it. The tone match refuses a target
    /// material the seam fixes also write, since one preview can hold only one version of it.
    /// </summary>
    public static Task<SkinSeamFix> BuildFixAsync(NeckSeamAnalysis analysis, NeckSeamFixOptions? neck,
        IReadOnlyDictionary<BodySeamKind, BodySeamFixOptions> body, SkinToneFixOptions? tone, CancellationToken token)
        => Task.Run(() =>
        {
            var neckFix = analysis.Report is { } report && neck is not null ? NeckSeamFixer.Build(report, neck) : null;
            token.ThrowIfCancellationRequested();
            var materialBase = neckFix?.BodyMaterial is { } bodyMaterial && analysis.Report is { } neckReport
                ? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [neckReport.BodyMaterialPath] = bodyMaterial }
                : null;
            var bodyFix = analysis.Body is { } bodyReport && body.Count > 0 ? BodySeamFixer.Build(bodyReport, body, materialBase) : null;
            token.ThrowIfCancellationRequested();
            SkinToneFix? toneFix = null;
            if (tone is { Any: true } && analysis.Tone is { } toneReport)
            {
                // What the seam fixes write: their changed materials, and the materials that read a texture they change.
                var changed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (analysis.Report is { } faceReport)
                {
                    if (neckFix?.Material is { } faceMaterial)
                        changed[faceReport.FaceMaterialPath] = faceMaterial;
                    if (neckFix?.BodyMaterial is { } neckBody)
                        changed[faceReport.BodyMaterialPath] = neckBody;
                    if (neckFix is { Textures.Count: > 0 })
                        owners.Add(faceReport.FaceMaterialPath);
                }
                foreach (var material in bodyFix?.Materials ?? [])
                    changed[material.GamePath] = material.Bytes;
                owners.UnionWith(bodyFix?.Textures.SelectMany(t => t.Materials) ?? []);
                var target = PathRules.NormalizeGamePath(tone.TargetPath);
                if (changed.ContainsKey(target) || owners.Contains(target))
                    throw new InvalidOperationException($"The skin tone match and a seam fix both change {target[(target.LastIndexOf('/') + 1)..]}. " +
                                                        "Untick one of them, make the preview, and apply or discard it before the other.");
                toneFix = SkinToneFixer.Build(toneReport, tone, changed);
            }
            return new SkinSeamFix(neckFix, bodyFix, toneFix);
        }, token);

    /// <summary>
    /// Puts the fixed files in a new Penumbra mod enabled for the character. Changed textures get new
    /// game paths (the materials in the mod point at them), so other materials that share the original
    /// textures don't change while previewing.
    /// </summary>
    public async Task<(NeckSeamPreview Preview, NeckSeamOutcome Outcome)> CreatePreviewAsync(NeckSeamAnalysis analysis, SkinSeamFix fix, CancellationToken token)
    {
        if (fix.Empty)
            throw new InvalidOperationException("The selected fixes don't change anything.");
        if (analysis.PreviewSources.Count > 0)
            throw new InvalidOperationException("A skin seam preview is active for this character. Apply or discard it first, then measure again.");
        var plan = SkinSeamPreviewPlan.Build(analysis, fix);
        var id = Guid.NewGuid();
        var tag = id.ToString("N")[..8];
        var files = new List<(string GamePath, byte[] Bytes)>();
        var records = new List<NeckSeamPreviewFile>();
        NeckSeamSource SourceOf(string gamePath) => analysis.Captured.Source(gamePath)
            ?? throw new InvalidDataException($"The source of {gamePath} is unknown; measure the seams again.");
        NeckSeamPreviewFile Record(string kind, string gamePath, string previewPath, byte[] bytes) => new()
        {
            Kind = kind, GamePath = gamePath, PreviewGamePath = previewPath, PreviewRelativePath = "Files/" + previewPath,
            PreviewSha256 = NeckSeamCapture.Hash(bytes), Source = SourceOf(gamePath),
        };

        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "neck-seam", id.ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            for (var i = 0; i < plan.Textures.Count; i++)
            {
                var texture = plan.Textures[i];
                var previewRequested = plan.PreviewPath(texture, tag) ?? throw new InvalidDataException($"No material in the fix names {texture.GamePath}.");
                var bytes = await EncodeAsync(texture.Image, texture.Original, $"{i}-{NeckSeamFixer.Label(texture.Sampler)}", work, token).ConfigureAwait(false);
                files.Add((previewRequested, bytes));
                records.Add(Record(texture.Kind, texture.GamePath, previewRequested, bytes));
            }
            // New files keep their own paths: the changed material names them already, so applying adds them to its mod.
            for (var i = 0; i < plan.NewTextures.Count; i++)
            {
                var texture = plan.NewTextures[i];
                var bytes = await EncodeAsync(texture.Image, texture.Original, $"new-{i}-{NeckSeamFixer.Label(texture.Sampler)}", work, token).ConfigureAwait(false);
                files.Add((texture.GamePath, bytes));
                records.Add(new NeckSeamPreviewFile
                {
                    Kind = texture.Kind, GamePath = texture.GamePath, PreviewGamePath = texture.GamePath, PreviewRelativePath = "Files/" + texture.GamePath,
                    PreviewSha256 = NeckSeamCapture.Hash(bytes), Source = SourceOf(texture.Material), NewFile = true,
                });
            }
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e, "Could not remove the skin seam work folder."); }
        }

        var changed = plan.Textures.Select(t => t.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var material in plan.Materials)
        {
            var rewrites = SkinSeamPreviewPlan.Rewrites(material.Bytes, changed, tag);
            var previewMaterial = rewrites.Count > 0 ? PenumbraService.RewriteMaterialTexturePaths(material.Bytes, rewrites) : material.Bytes;
            files.Add((material.GamePath, previewMaterial));
            records.Add(Record(material.Kind, material.GamePath, material.GamePath, previewMaterial) with
            {
                TextureRewrites = rewrites.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase),
            });
        }
        foreach (var model in plan.Models)
        {
            files.Add((model.GamePath, model.Bytes));
            records.Add(Record(model.Kind, model.GamePath, model.GamePath, model.Bytes));
        }

        var description = "Skin seam preview made by XIV Instant Edit for " + analysis.ActorName + ":\n" + string.Join("\n", fix.Changes) +
                          "\n\nApply or discard it from the skin seam check on XIV Instant Edit's Quick Actions tab.";
        var mod = await _previews.CreateAsync($"Skin Seam Preview - {analysis.ActorName}", description,
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

    /// <param name="label">A name for the work files, unique within the preview.</param>
    private async Task<byte[]> EncodeAsync(SeamImage image, byte[] original, string label, string work, CancellationToken token)
    {
        var type = TextureType.RgbaTex;
        var mips = true;
        try
        {
            var header = TextureFiles.ReadOriginal(original).Header;
            mips = header.Mips > 1;
            if (_recompress())
                type = TextureFiles.OutputType(header.Format);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            // Formats the texture editor can't preserve are saved uncompressed.
        }
        var tga = Path.Combine(work, label + ".tga");
        var output = Path.Combine(work, label + ".tex");
        await File.WriteAllBytesAsync(tga, TgaImage.WriteBgra32(new RgbaImage(image.Width, image.Height, image.Rgba)), token)
            .ConfigureAwait(false);
        await ((ITextureEditBackend)_penumbra).ConvertAsync(tga, output, type, mips).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
        _ = TextureFiles.ReadTex(bytes);
        // Where the blend didn't reach, the original's own blocks go back in, so a fix never recompresses the rest of the texture.
        try
        {
            var before = SeamTextures.Decode(original);
            if (before.Width == image.Width && before.Height == image.Height)
            {
                var changed = new bool[image.Width * image.Height];
                for (var i = 0; i < changed.Length; i++)
                    changed[i] = !before.Rgba.AsSpan(i * 4, 4).SequenceEqual(image.Rgba.AsSpan(i * 4, 4));
                bytes = TextureFiles.KeepUnchanged(original, bytes, changed);
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            _log.Debug(e, "Could not keep the original blocks of {Label}; the whole texture is re-encoded.", label);
        }
        return bytes;
    }

    /// <summary> What <see cref="ApplyAsync"/> would write, for the confirmation. </summary>
    public static IReadOnlyList<(string Kind, NeckSeamSource Source)> ApplyTargets(NeckSeamPreview preview)
        => preview.Files.Select(f => (f.Kind, f.Source)).ToList();

    /// <summary>
    /// Writes the preview's files over their sources: mod files in place (backed up first), game data
    /// into a new "Skin Seam Fix" mod. The materials get their original texture paths back, since
    /// the textures themselves are replaced. Then the preview mod is deleted.
    /// </summary>
    public async Task<NeckSeamOutcome> ApplyAsync(NeckSeamPreview preview)
    {
        var result = await _previews.ApplyAsync(Shared(preview), $"Skin Seam Fix - {preview.ActorName}",
            "Skin seam fix made by XIV Instant Edit for game files " + preview.ActorName + " uses.", FixModFallback, Recheck).ConfigureAwait(false);
        Store.Remove(preview.Id);
        return new NeckSeamOutcome(result.Describe("Skin seam fix applied"), result.Warnings);
    }

    /// <summary>
    /// The kept backups of the mod files the analysis read (models, skin materials and textures),
    /// grouped by when they were made, newest first: each apply of a fix makes one group.
    /// </summary>
    public IReadOnlyList<PreviewBackupGroup> Backups(NeckSeamAnalysis analysis) => _previews.Backups(analysis.Captured.Sources.Values);

    /// <summary> Puts a group's files back as they were before its backups were made. The current files are backed up first, so this can be undone the same way. </summary>
    public async Task<NeckSeamOutcome> RestoreAsync(PreviewBackupGroup group, ushort objectIndex)
    {
        var warnings = await _previews.RestoreAsync(group, objectIndex, Recheck).ConfigureAwait(false);
        var mods = group.Files.Select(f => f.Source.ModName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new NeckSeamOutcome($"Restored {group.Files.Count} file{(group.Files.Count == 1 ? "" : "s")} in {string.Join(", ", mods)} " +
                                   $"as they were before {group.Created.ToLocalTime():d MMM, HH:mm}. The files replaced are backed up too.", warnings);
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
            Source = file.Source, TextureRewrites = file.TextureRewrites, NewFile = file.NewFile,
        }).ToList(),
    };
}
