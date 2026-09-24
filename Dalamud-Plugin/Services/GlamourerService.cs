using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Helpers;

namespace InstantEdit.Services;

/// <summary>
/// Listens to Glamourer's state events. Glamourer swaps gear, weapons and customizations in
/// place, so most of its changes never reach Penumbra's redraw event.
/// </summary>
/// <remarks>
/// The subscription is a plain call gate, so it survives Glamourer loading, reloading or
/// being absent. Glamourer.Api registers its state events under a "Penumbra." prefix, and
/// its <c>StateChangeType</c> argument crosses the call gate as the underlying int.
/// </remarks>
public sealed class GlamourerService : IDisposable
{
    private const string StateChangedWithTypeLabel = "Penumbra.StateChangedWithType";

    // Glamourer.Api.Enums.StateChangeType values that recolor an actor without changing
    // which files it loads. Parameter and MaterialValue also fire continuously while a
    // Glamourer slider is dragged.
    private const int StainsChange = 5, ParameterChange = 7, MaterialValueChange = 8;

    private readonly IPluginLog _log;
    private EventSubscriber<nint, int>? _stateChanged;

    /// <summary>
    /// Raised, on Glamourer's thread, with the game object address (0 when unknown) whose
    /// appearance Glamourer changed. Handlers must be cheap.
    /// </summary>
    public event Action<nint>? AppearanceChanged;

    public GlamourerService(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        try
        {
            _stateChanged = new EventSubscriber<nint, int>(pi, StateChangedWithTypeLabel, OnStateChanged);
        }
        catch (Exception e)
        {
            _log.Debug($"Could not subscribe to Glamourer state changes: {e.Message}");
        }
    }

    private void OnStateChanged(nint address, int type)
    {
        if (type is StainsChange or ParameterChange or MaterialValueChange)
            return;
        try
        {
            AppearanceChanged?.Invoke(address);
        }
        catch (Exception e)
        {
            _log.Debug($"A Glamourer state change handler failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _stateChanged?.Dispose();
        _stateChanged = null;
    }
}
