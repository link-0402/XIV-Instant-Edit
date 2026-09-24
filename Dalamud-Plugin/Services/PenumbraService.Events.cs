using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

public sealed partial class PenumbraService : IDisposable
{
    private OpenMainWindow? _openMainWindow;
    private EventSubscriber<ModSettingChange, Guid, string, bool>? _modSettingChanged;
    private EventSubscriber<nint, int>? _gameObjectRedrawn;

    /// <summary>
    /// Raised, on Penumbra's thread, when a mod setting changed in any collection or a game
    /// object was redrawn. Handlers must be cheap and must not call back into Penumbra.
    /// </summary>
    public event Action? ResourcesChanged;

    private void SubscribeToResourceChanges(IDalamudPluginInterface pi)
    {
        _openMainWindow = new OpenMainWindow(pi);
        _modSettingChanged = ModSettingChanged.Subscriber(pi, (_, _, _, _) => RaiseResourcesChanged());
        _gameObjectRedrawn = GameObjectRedrawn.Subscriber(pi, (_, _) => RaiseResourcesChanged());
    }

    private void RaiseResourcesChanged()
    {
        try
        {
            ResourcesChanged?.Invoke();
        }
        catch (Exception e)
        {
            _log.Debug($"A resource change handler failed: {e.Message}");
        }
    }

    /// <summary> Opens Penumbra's main window on the Mods tab with the given mod selected. </summary>
    public void OpenModInPenumbra(string modDirectory, string modName)
        => _openMainWindow?.Invoke(TabType.Mods, modDirectory, modName);

    public void Dispose()
    {
        _modSettingChanged?.Dispose();
        _gameObjectRedrawn?.Dispose();
        _modSettingChanged = null;
        _gameObjectRedrawn = null;
    }
}
