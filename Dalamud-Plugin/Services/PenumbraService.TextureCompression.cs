using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    /// <summary>
    /// Writes new contents over a mod file, for automatic texture compression and restoring its
    /// originals. The mod must still be registered, the file must still sit at its relative path
    /// inside the mod's folder and must still hash as <paramref name="expectedSha256"/>; the file is
    /// replaced in one move, so a crash leaves either version. File work stays off the game thread.
    /// </summary>
    /// <param name="recheck">What happens after a conflict, such as "It is looked at again the next time it loads".</param>
    internal async Task ReplaceModFileAsync(PreviewSource source, string expectedSha256, byte[] bytes, string recheck)
    {
        if (!source.IsModFile || !IsSafeModName(source.ModDirectory) || !IsSafeRelativeResourcePath(source.RelativePath))
            throw new IOException($"{source.Label} is not a writable mod file.");
        var scan = await _framework.RunOnFrameworkThread(() => ResolveModScanOnFramework(source.ModDirectory, source.ModStableId)).ConfigureAwait(false);
        await LeaveFrameworkThread();
        if (scan is null)
            throw new TextureConflictException($"The mod {source.ModName} is no longer registered in Penumbra.");
        var file = Path.GetFullPath(source.ActualPath);
        var root = scan.CandidateRoots.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(Path.Combine(candidate, source.RelativePath.Replace('/', Path.DirectorySeparatorChar))), file,
                StringComparison.OrdinalIgnoreCase));
        if (root is null || !IsSafeModResourceFile(root, file) || !File.Exists(file))
            throw new TextureConflictException($"{source.Label} moved or is no longer part of its mod. {recheck}.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.Equals(PreviewSource.Hash(await File.ReadAllBytesAsync(file).ConfigureAwait(false)), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new TextureConflictException($"{source.Label} changed since it was read. {recheck}.");
            var temporary = file + $".instant-edit-{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, file, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>
    /// Reloads the mods whose files changed, so the next load reads the new files, then redraws the
    /// local player once. Returns what couldn't be done.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ReloadModsAndRedrawPlayerAsync(IReadOnlyCollection<string> modDirectories)
    {
        var warnings = new List<string>();
        foreach (var mod in modDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            if (await _framework.RunOnFrameworkThread(() => ReloadModOnFramework(mod)).ConfigureAwait(false) is { } reload)
                warnings.Add(reload.Message);
        if (await _framework.RunOnFrameworkThread(RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        await LeaveFrameworkThread();
        return warnings;
    }
}
