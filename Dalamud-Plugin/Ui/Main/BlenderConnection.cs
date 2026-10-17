using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui.Main;

/// <summary>
/// The Blender add-on as the main window sees it: the toolbar's connection state, checked every few
/// seconds in the background, the shared cache folder, and the checks every send runs first.
/// </summary>
internal sealed class BlenderConnection
{
    private readonly BlenderClient _blender;
    private readonly Configuration _config;
    private readonly TextureEditService _textures;
    private readonly IPluginLog _log;
    private readonly object _stateLock = new();
    private BlenderConnectionState _blenderState = BlenderConnectionState.Offline;
    private string? _addonVersion;
    private bool _blenderChecking;
    private DateTime _lastBlenderCheck = DateTime.MinValue;

    public BlenderConnection(BlenderClient blender, Configuration config, TextureEditService textures, IPluginLog log)
    {
        _blender = blender;
        _config = config;
        _textures = textures;
        _log = log;
    }

    public string PluginVersion { get; } = BlenderClient.CurrentPluginVersion;

    /// <summary> The add-on version the last background check found, or null. </summary>
    public string? AddonVersion
    {
        get
        {
            lock (_stateLock)
                return _addonVersion;
        }
    }

    /// <summary> The state the last background check found. </summary>
    public BlenderConnectionState State
    {
        get
        {
            lock (_stateLock)
                return _blenderState;
        }
    }

    public async Task<BlenderStatus> CheckStatusAsync(int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        return await _blender.GetStatusAsync(port, timeout.Token).ConfigureAwait(false);
    }

    private async Task<bool> SynchronizeCacheAsync(
        BlenderStatus status,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (status.Classify(ComponentVersions.BlenderAddonMinimum) != BlenderConnectionState.Online || !status.CacheSettingsSupported)
            return false;

        var expectedRoot = _textures.EnsureConfiguredCache();
        var synchronizedRoot = await _blender.ConfigureCacheAsync(
            port, _config.TextureCacheDirectory, _config.AutomaticCacheCleanup, _config.CacheRetentionDays, cancellationToken).ConfigureAwait(false);
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
        StartCheckIfNeeded();
    }

    public void StartCheckIfNeeded()
    {
        lock (_stateLock)
        {
            if (_blenderChecking || (DateTime.UtcNow - _lastBlenderCheck).TotalSeconds <= 5)
                return;
            _blenderChecking = true;
        }

        _ = Task.Run(async () =>
        {
            var found = BlenderConnectionState.Offline;
            string? addonVersion = null;
            try
            {
                var status = await CheckStatusAsync(_config.BlenderPort).ConfigureAwait(false);
                found = status.Classify(ComponentVersions.BlenderAddonMinimum);
                addonVersion = status.AddonVersion;
                if (found == BlenderConnectionState.Online)
                    await SynchronizeCacheAsync(status, _config.BlenderPort).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Debug($"Blender status check failed: {e.Message}");
            }
            finally
            {
                lock (_stateLock)
                {
                    _blenderState = found;
                    _addonVersion = addonVersion;
                    _blenderChecking = false;
                    _lastBlenderCheck = DateTime.UtcNow;
                }
            }
        });
    }

    /// <summary>
    /// Throws with a user-facing message unless Blender runs this version of the add-on, shares the
    /// plugin's cache, and takes imports with these options (and of game data, with <paramref name="gameData"/>).
    /// </summary>
    public async Task EnsureReadyForImportAsync(int blenderPort, BlenderImportOptions importOptions, bool gameData,
        CancellationToken cancellationToken)
    {
        var blenderStatus = await CheckStatusAsync(blenderPort, cancellationToken).ConfigureAwait(false);
        if (!blenderStatus.Reachable)
            throw new InvalidOperationException("Blender is offline. Start Blender and enable the XIV Instant Edit add-on before editing.");
        if (blenderStatus.Classify(ComponentVersions.BlenderAddonMinimum) != BlenderConnectionState.Online)
            throw new InvalidOperationException(BlenderClient.VersionMismatchMessage(blenderStatus.AddonVersion, ComponentVersions.BlenderAddonMinimum));
        if (!await SynchronizeCacheAsync(blenderStatus, blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Blender's cache could not be synchronized. Update and restart the XIV Instant Edit add-on, then retry.");
        if (importOptions.ArmatureMode == BlenderImportOptions.ExistingMode &&
            !await _blender.SupportsImportOptionsAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for custom import options. Update the add-on and restart Blender.");
        if (importOptions.ApplyTexturesAndMaterials &&
            !await _blender.SupportsMaterialPreviewAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for texture and material previews. Update the add-on and restart Blender.");
        if (gameData && !await _blender.SupportsVanillaContextAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for vanilla model contexts. Update the add-on and restart Blender.");
    }
}
