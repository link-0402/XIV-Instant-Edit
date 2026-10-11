using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private TextureCompressionService? _compression;
    private bool _compressionConfirmRestore;
    private string _compressionMessage = string.Empty;
    private IReadOnlyList<string> _compressionDetails = [];
    // The totals lines, worked out again only when the list of compressed textures or the cleanup setting changes.
    private (IReadOnlyList<CompressedTexture>? Entries, IReadOnlyList<RefitGroup>? Refits, bool Cleanup, string Totals, string Backups) _compressionTotals;
    private (string? Directory, bool InTemp) _compressionCacheInTemp;

    internal void AttachTextureCompression(TextureCompressionService service)
    {
        _compression = service;
        service.Optimized += NotifyOptimized;
    }

    /// <summary> Runs mostly happen while the window is closed, so each one that changed something shows a toast. </summary>
    private void NotifyOptimized(CompressionRun run)
    {
        if (_config.ShowNotifications)
            ShowNotification(TextureCompressionViews.Toast(run), NotificationType.Success);
    }

    private QuickAction TextureCompressionCard => new("texture-compression", FontAwesomeIcon.CompressArrowsAlt, "Optimize textures",
        "Automatically optimize your character's textures shortly after they load. Every compression is checked for visual quality impact " +
        "Originals are backed up for a week and can be easily restored for the current appearance.",
        DrawTextureCompressionAction,
        () =>
        {
            if (_compression is { Enabled: true } service)
                service.SetEnabled(false);
        },
        () => _compression is { Enabled: true });

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
                ImGui.SetTooltip("Hair ported from other games often keeps its strands in one corner of a texture several times their size.\n" +
                                 "This moves the UVs of every model in the hair's mod that uses those textures onto that part, in every option\n" +
                                 "and for every race, and cuts the textures down to the active area. This never has a visual impact.\n" +
                                 "It covers everything drawn with the hair shader, which also includes eyebrows, lashes, ears and tails.");
        }
        Widgets.HintWrapped(TextureCompressionViews.Status(service.Phase, service.Progress));
        if (service.Backups.LoadError.Length > 0)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Error, service.Backups.LoadError);
        }

        var entries = service.Backups.Compressed;
        var refits = service.Backups.Refits;
        var cleanup = _config.AutomaticCacheCleanup;
        if (!ReferenceEquals(entries, _compressionTotals.Entries) || !ReferenceEquals(refits, _compressionTotals.Refits) || cleanup != _compressionTotals.Cleanup)
            _compressionTotals = (entries, refits, cleanup, TextureCompressionViews.Totals(entries, refits),
                TextureCompressionViews.Backups(service.Backups.BackupTotals(), cleanup));
        Widgets.MutedWrapped(_compressionTotals.Totals);
        if (service.LastRun is { } run)
            DrawCompressionRun(run);

        ImGui.Spacing();
        DrawCompressionButtons(service, entries.Count > 0 || refits.Count > 0, busy);
        if (_compressionMessage.Length > 0)
        {
            Widgets.HintWrapped(_compressionMessage);
            if (_compressionDetails.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", _compressionDetails));
        }
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
        if (TextureCompressionViews.LeftRestored(run) is { Length: > 0 } restored)
        {
            Widgets.MutedWrapped(restored);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", run.Restored));
        }
        foreach (var problem in run.Failed.Concat(run.Warnings))
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, problem);
        }
    }

    private void DrawCompressionButtons(TextureCompressionService service, bool backups, bool busy)
    {
        if (backups && _compressionTotals.Backups.Length > 0)
            Widgets.MutedWrapped(_compressionTotals.Backups);
        using (ImRaii.Disabled(busy))
        {
            if (_compressionConfirmRestore && backups)
            {
                Widgets.HintWrapped("Restore your character's original textures? They're skipped until you use Optimize now.");
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
            if (ImGui.Button("Optimize now##quick-compression"))
            {
                _compressionMessage = string.Empty;
                _compressionDetails = [];
                service.OptimizeNow();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Optimize all loaded textures of your current appearance.");
            if (!backups)
                return;
            ImGui.SameLine();
            if (ImGui.Button("Restore originals##quick-compression"))
            {
                _compressionConfirmRestore = true;
                _compressionMessage = string.Empty;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Restore your character's original textures.");
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
