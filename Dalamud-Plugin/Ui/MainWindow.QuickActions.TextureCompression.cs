using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private TextureCompressionService? _compression;
    private bool _compressionConfirmRestore;
    private bool _compressionConfirmDelete;
    private string _compressionMessage = string.Empty;
    private IReadOnlyList<string> _compressionDetails = [];
    // The totals lines, worked out again only when the list of compressed textures changes.
    private (IReadOnlyList<CompressedTexture>? Entries, IReadOnlyList<RefitGroup>? Refits, string Totals, string Backups) _compressionTotals;
    private (string? Directory, bool InTemp) _compressionCacheInTemp;

    internal void AttachTextureCompression(TextureCompressionService service) => _compression = service;

    private QuickAction TextureCompressionCard => new("texture-compression", FontAwesomeIcon.CompressArrowsAlt, "Optimize textures",
        "Sync plugins such as Lightless and PlayerSync count the size of your mods' texture files, and warn about or pause heavy characters. " +
        "While this is on, the textures your character wears are made smaller when they load: uncompressed ones are compressed, and ones that " +
        $"hold a single color everywhere shrink to {SingleColor.Size} × {SingleColor.Size} pixels. Each one is decoded again and compared first, " +
        "and stays as it is if its see-through edges, dye colors or shading would change. It can also refit hair whose UVs use only part of its " +
        "textures. The originals are backed up in the cache folder.",
        DrawTextureCompressionAction,
        () =>
        {
            if (_compression is { Enabled: true } service)
                service.SetEnabled(false);
        });

    private void DrawTextureCompressionAction()
    {
        if (_compression is not { } service)
        {
            Widgets.Hint("Unavailable: texture compression did not start.");
            return;
        }
        var busy = service.Busy;
        var enabled = service.Enabled;
        using (ImRaii.Disabled(service.Phase == CompressionPhase.Restoring))
        {
            if (ImGui.Checkbox("Optimize textures automatically##quick-compression", ref enabled))
            {
                _compressionMessage = string.Empty;
                _compressionDetails = [];
                service.SetEnabled(enabled);
            }
        }
        if (busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
        using (ImRaii.PushIndent())
        using (ImRaii.Disabled(service.Phase == CompressionPhase.Restoring))
        {
            var refit = service.RefitHair;
            if (ImGui.Checkbox("Also refit hair that uses only part of its textures##quick-compression-refit", ref refit))
                service.SetRefitHair(refit);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hair ported from The Sims 4 often keeps its strands in one corner of a texture several times their size.\n" +
                                 "This moves the UVs of every model in the hair's mod that uses those textures onto that part, in every option\n" +
                                 "and for every race, and cuts the textures down to it. What is drawn stays the same, pixel for pixel.\n" +
                                 "It covers everything drawn with the hair shader, which also draws eyebrows, lashes, ears and tails.\n" +
                                 "Hair is left as it is when the game's own models or materials, or another shader, could read the same files.");
        }
        Widgets.HintWrapped(TextureCompressionViews.Status(service.Phase, service.Progress));
        if (service.Backups.LoadError.Length > 0)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Error, service.Backups.LoadError);
        }

        var entries = service.Backups.Compressed;
        var refits = service.Backups.Refits;
        if (!ReferenceEquals(entries, _compressionTotals.Entries) || !ReferenceEquals(refits, _compressionTotals.Refits))
            _compressionTotals = (entries, refits, TextureCompressionViews.Totals(entries, refits), TextureCompressionViews.Backups(service.Backups.BackupTotals()));
        Widgets.MutedWrapped(_compressionTotals.Totals);
        if (service.LastRun is { } run)
            DrawCompressionRun(run);

        ImGui.Spacing();
        if (entries.Count > 0 || refits.Count > 0)
            DrawCompressionBackups(service, entries.Count + refits.Sum(group => group.Files.Count), busy);
        if (_compressionMessage.Length > 0)
        {
            Widgets.HintWrapped(_compressionMessage);
            if (_compressionDetails.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", _compressionDetails));
        }
        Widgets.MutedWrapped("An optimized file changes for every mod option, item and collection that uses it, like any edit to a mod's file.");
        if (CacheInTempFolder())
            Widgets.HintWrapped("The cache folder is inside Windows' temporary folder, which cleanup tools can empty. " +
                                "To keep the backups safe, choose another cache folder in Settings.");
    }

    private void DrawCompressionRun(CompressionRun run)
    {
        if (TextureCompressionViews.LastRun(run) is { Length: > 0 } line)
            Widgets.MutedWrapped(line);
        if (TextureCompressionViews.Kept(run) is { Length: > 0 } kept)
        {
            Widgets.MutedWrapped(kept);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", run.Kept));
        }
        if (TextureCompressionViews.NotRefit(run) is { Length: > 0 } notRefit)
        {
            Widgets.MutedWrapped(notRefit);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", run.NotRefit));
        }
        foreach (var problem in run.Failed.Concat(run.Warnings))
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, problem);
        }
    }

    private void DrawCompressionBackups(TextureCompressionService service, int count, bool busy)
    {
        if (_compressionTotals.Backups.Length > 0)
            Widgets.MutedWrapped(_compressionTotals.Backups);
        using (ImRaii.Disabled(busy))
        {
            if (_compressionConfirmRestore)
            {
                Widgets.HintWrapped($"Put back the originals of {(count == 1 ? "1 file" : $"{count:N0} files")}? This turns automatic optimization off, " +
                                    "so they aren't optimized again. Files that changed since they were optimized are left as they are.");
                using (ImRaii.PushColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Important, .45f)))
                {
                    if (ImGui.Button("Restore##quick-compression-restore"))
                        RestoreCompressedTextures(service);
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##quick-compression-restore"))
                    _compressionConfirmRestore = false;
                return;
            }
            if (_compressionConfirmDelete)
            {
                Widgets.HintWrapped("Delete the backups? The optimized textures stay, and their originals can't be restored afterwards.");
                using (ImRaii.PushColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Error, .45f)))
                {
                    if (ImGui.Button("Delete##quick-compression-delete"))
                    {
                        var deleted = service.DeleteBackups();
                        _compressionConfirmDelete = false;
                        _compressionMessage = $"Deleted the backups of {(deleted == 1 ? "1 file" : $"{deleted:N0} files")}.";
                        _compressionDetails = [];
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##quick-compression-delete"))
                    _compressionConfirmDelete = false;
                return;
            }
            if (ImGui.Button("Restore originals##quick-compression"))
            {
                _compressionConfirmRestore = true;
                _compressionMessage = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete backups##quick-compression"))
            {
                _compressionConfirmDelete = true;
                _compressionMessage = string.Empty;
            }
        }
    }

    private void RestoreCompressedTextures(TextureCompressionService service)
    {
        _compressionConfirmRestore = false;
        _compressionMessage = string.Empty;
        _compressionDetails = [];
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await service.RestoreAsync().ConfigureAwait(false);
                _compressionDetails = result.Changed.Select(label => $"{label}: changed since it was compressed").Concat(result.Failed).Concat(result.Warnings).ToList();
                _compressionMessage = TextureCompressionViews.Restored(result);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not restore the original textures.");
                _compressionMessage = "Restoring failed: " + error.Message;
            }
        });
    }

    /// <summary> Whether the configured cache folder is in the temp folder, worked out again only when the setting changes. </summary>
    private bool CacheInTempFolder()
    {
        var directory = _config.TextureCacheDirectory;
        if (_compressionCacheInTemp.Directory != directory)
        {
            bool inTemp;
            try
            {
                inTemp = TextureCompressionViews.InTempFolder(TextureFiles.CacheRootFor(directory), Path.GetTempPath());
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
            {
                inTemp = false;
            }
            _compressionCacheInTemp = (directory, inTemp);
        }
        return _compressionCacheInTemp.InTemp;
    }
}
