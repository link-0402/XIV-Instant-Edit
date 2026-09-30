namespace InstantEdit.Services.PreviewMods;

/// <summary> What applying a preview wrote, with follow-up warnings. </summary>
internal sealed record PreviewApplyResult(IReadOnlyList<string> WrittenMods, int ModFiles, string? FixMod, int GameFiles, IReadOnlyList<string> Warnings)
{
    /// <summary> "&lt;applied&gt;: wrote 2 files into A, B (backups kept for 7 days); put 1 game file in the mod X." </summary>
    public string Describe(string applied)
    {
        var parts = new List<string>();
        if (ModFiles > 0)
            parts.Add($"wrote {ModFiles} file{(ModFiles == 1 ? "" : "s")} into {string.Join(", ", WrittenMods)} (backups kept for 7 days)");
        if (FixMod is not null)
            parts.Add($"put {GameFiles} game file{(GameFiles == 1 ? "" : "s")} in the mod {FixMod}");
        return parts.Count == 0 ? "Nothing needed writing; the preview is removed." : applied + ": " + string.Join("; ", parts) + ".";
    }
}

/// <summary> What discarding a preview did. The preview is gone unless <see cref="Removed"/> is false. </summary>
internal sealed record PreviewDiscardResult(bool Removed, string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// The preview-mod workflow the Quick Actions share: changed files go into a new mod enabled for the
/// character above its other mods. Applying writes them over their files in the source mods (with
/// managed backups, after checking each source still hashes as it did) and puts changed game files
/// in a new mod; discarding deletes the preview mod.
/// </summary>
internal sealed class PreviewModService(PenumbraService penumbra)
{
    public Task<PreviewModResult> CreateAsync(string name, string description, IReadOnlyList<PreviewModEntry> files, int objectIndex,
        string fallbackName, CancellationToken token)
        => penumbra.CreatePreviewModAsync(name, description, files, objectIndex, fallbackName, token);

    /// <summary>
    /// Writes the preview's files over their sources: mod files in place (backed up first), game data
    /// into a new mod named <paramref name="fixModName"/>. Materials get their original texture paths
    /// back first. Then the preview mod is deleted; forgetting the preview is up to the caller.
    /// </summary>
    /// <param name="recheck">What to do after a source changed, such as "Measure the seam again".</param>
    public async Task<PreviewApplyResult> ApplyAsync(PreviewMod preview, string fixModName, string fixModDescription, string fallbackName,
        string recheck)
    {
        var folder = await PreviewFolderAsync(preview).ConfigureAwait(false);
        var modWrites = new List<(PreviewSource Source, byte[] Bytes)>();
        var gameFiles = new List<PreviewModEntry>();
        foreach (var file in preview.Files)
        {
            var path = Path.GetFullPath(Path.Combine(folder, file.PreviewRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathRules.IsPathWithin(path, folder) || !File.Exists(path))
                throw new IOException($"The preview mod lost its {file.Kind}. Discard the preview and make a new one.");
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (!string.Equals(PreviewSource.Hash(bytes), file.PreviewSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The preview mod's {file.Kind} was edited. Discard the preview and make a new one.");
            if (file.TextureRewrites.Count > 0)
                bytes = PenumbraService.RewriteMaterialTexturePaths(bytes, file.TextureRewrites);
            if (file.Source.IsModFile)
            {
                if (!string.Equals(PreviewSource.Hash(bytes), file.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                    modWrites.Add((file.Source, bytes));
            }
            else
                gameFiles.Add(new PreviewModEntry("Files/" + PathRules.NormalizeGamePath(file.GamePath), bytes, file.FixGamePaths));
        }

        var warnings = new List<string>();
        if (modWrites.Count > 0)
            warnings.AddRange(await penumbra.WritePreviewSourcesAsync(modWrites, preview.ObjectIndex, recheck).ConfigureAwait(false));
        string? fixMod = null;
        if (gameFiles.Count > 0)
        {
            var created = await penumbra.CreatePreviewModAsync(fixModName, fixModDescription, gameFiles, preview.ObjectIndex, fallbackName,
                CancellationToken.None).ConfigureAwait(false);
            fixMod = created.ModDirectory;
            warnings.AddRange(created.Warnings);
        }
        var (_, delete) = await penumbra.DeletePreviewModAsync(preview.ModDirectory, preview.ModIdentifier, preview.ObjectIndex).ConfigureAwait(false);
        if (delete is not null)
            warnings.Add(delete);
        return new PreviewApplyResult(modWrites.Select(w => w.Source.ModName).Distinct().ToList(), modWrites.Count, fixMod, gameFiles.Count, warnings);
    }

    /// <summary> Backups of the mod files among <paramref name="sources"/>, grouped by when they were made (one group per apply), newest first. </summary>
    public IReadOnlyList<PreviewBackupGroup> Backups(IEnumerable<PreviewSource> sources)
        => PreviewBackups.Group(sources.Where(s => s.IsModFile)
            .DistinctBy(s => (s.ModDirectory.ToLowerInvariant(), s.RelativePath.Replace('\\', '/').ToLowerInvariant()))
            .SelectMany(s => penumbra.ManagedBackupsFor(s).Select(b => new PreviewBackupFile(s, b))));

    /// <summary> Puts a group's backups back over their files (backing up the current files first), then reloads the mods and redraws the character. </summary>
    public Task<IReadOnlyList<string>> RestoreAsync(PreviewBackupGroup group, int objectIndex, string recheck)
        => penumbra.RestorePreviewSourcesAsync(group.Files.Select(f => (f.Source, f.Backup)).ToList(), objectIndex, recheck);

    public async Task<PreviewDiscardResult> DiscardAsync(PreviewMod preview)
    {
        var (removed, warning) = await penumbra.DeletePreviewModAsync(preview.ModDirectory, preview.ModIdentifier, preview.ObjectIndex).ConfigureAwait(false);
        return new PreviewDiscardResult(removed, removed ? $"Discarded the preview mod {preview.ModDirectory}." : "The preview mod could not be deleted.",
            warning is null ? [] : [warning]);
    }

    private async Task<string> PreviewFolderAsync(PreviewMod preview)
    {
        var root = await penumbra.PreviewModRootAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root) || !PenumbraService.IsSafeModName(preview.ModDirectory))
            throw new IOException("Penumbra's mod directory is unavailable.");
        var folder = Path.GetFullPath(Path.Combine(root, preview.ModDirectory));
        if (!Directory.Exists(folder) || PenumbraService.ReadModStableIdentifier(folder) != preview.ModIdentifier)
            throw new IOException($"The preview mod {preview.ModDirectory} is gone or was replaced. Discard it and make a new preview.");
        return folder;
    }
}
