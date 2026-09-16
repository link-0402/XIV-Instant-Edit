using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private async Task<BlenderStatus> CheckBlenderStatusAsync(int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        return await _blender.GetStatusAsync(port, timeout.Token).ConfigureAwait(false);
    }

    private async Task<bool> SynchronizeBlenderCacheAsync(
        BlenderStatus status,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (status.Classify(_pluginVersion) != BlenderConnectionState.Online || !status.CacheSettingsSupported)
            return false;

        var expectedRoot = _textures.EnsureConfiguredCache();
        var synchronizedRoot = await _blender.ConfigureCacheAsync(
            port, _config.TextureCacheDirectory, _config.AutomaticCacheCleanup, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(synchronizedRoot))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(expectedRoot), Path.GetFullPath(synchronizedRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public void RequestCacheSynchronization()
    {
        if (_config.AutomaticCacheCleanup)
            _textures.RequestCacheCleanup();
        lock (_stateLock)
            _lastBlenderCheck = DateTime.MinValue;
        StartBlenderCheckIfNeeded();
    }

    private void StartBlenderCheckIfNeeded()
    {
        lock (_stateLock)
        {
            if (_blenderChecking || (DateTime.UtcNow - _lastBlenderCheck).TotalSeconds <= 5)
                return;
            _blenderChecking = true;
        }

        _ = Task.Run(async () =>
        {
            var state = BlenderConnectionState.Offline;
            try
            {
                var status = await CheckBlenderStatusAsync(_config.BlenderPort).ConfigureAwait(false);
                state = status.Classify(_pluginVersion);
                if (state == BlenderConnectionState.Online)
                    await SynchronizeBlenderCacheAsync(status, _config.BlenderPort).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Debug($"Blender status check failed: {e.Message}");
            }
            finally
            {
                lock (_stateLock)
                {
                    _blenderState = state;
                    _blenderChecking = false;
                    _lastBlenderCheck = DateTime.UtcNow;
                }
            }
        });
    }
}
