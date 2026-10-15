using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Puts a written mod into effect: adds a new mod to Penumbra or reloads a changed one, enables it in
/// a collection, and redraws the local player and what they own. The *OnFramework calls must run on
/// the framework thread.
/// </summary>
internal sealed class ModActivation
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IObjectTable _objects;
    private readonly AddMod _addMod;
    private readonly ReloadMod _reloadMod;
    private readonly GetCollectionForObject _getCollectionForObject;
    private readonly TrySetMod _trySetMod;
    private readonly TrySetModPriority _trySetModPriority;
    private readonly RedrawObject _redrawObject;

    public ModActivation(PenumbraIpc ipc, IFramework framework, IPluginLog log, IObjectTable objects)
    {
        _ipc = ipc;
        _framework = framework;
        _log = log;
        _objects = objects;
        _addMod = ipc.AddMod;
        _reloadMod = ipc.ReloadMod;
        _getCollectionForObject = ipc.GetCollectionForObject;
        _trySetMod = ipc.TrySetMod;
        _trySetModPriority = ipc.TrySetModPriority;
        _redrawObject = ipc.RedrawObject;
    }

    internal ExportResult? ReloadModOnFramework(string modName)
    {
        PenumbraApiEc modResult;
        try
        {
            modResult = _reloadMod.Invoke(modName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while reloading the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add/reload failed: {e.Message}");
        }

        if (modResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected the mod ({modResult}).");

        return null;
    }

    internal async Task<ExportResult?> AddNewModAsync(string modName)
    {
        var added = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventSubscriber<string>? subscription = null;

        try
        {
            subscription = _ipc.SubscribeModAdded(directory =>
            {
                if (string.Equals(directory, modName, StringComparison.OrdinalIgnoreCase))
                    added.TrySetResult(directory);
            });
            subscription.Enable();

            // AddMod only queues discovery. Do not wait for its event from this
            // callback; the continuation waits on a thread-pool context instead.
            var addResult = await _framework.RunOnFrameworkThread(
                () => AddModOnFramework(modName)).ConfigureAwait(false);
            if (addResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected adding the mod ({addResult}).");

            try
            {
                await added.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new ExportResult(false, $"Timed out waiting for Penumbra to add '{modName}'.");
            }

            return null;
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while adding the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add failed: {e.Message}");
        }
        finally
        {
            if (subscription is not null)
            {
                try
                {
                    subscription.Disable();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not disable Penumbra mod-added subscription: {e.Message}");
                }

                try
                {
                    subscription.Dispose();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not dispose Penumbra mod-added subscription: {e.Message}");
                }
            }
        }
    }

    private PenumbraApiEc AddModOnFramework(string modName)
        => _addMod.Invoke(modName);

    internal ExportResult ConfigureModOnFramework(
        string modName,
        int objectIndex,
        bool setPriority = true,
        int priority = int.MaxValue)
    {

        (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) collection;
        try
        {
            collection = _getCollectionForObject.Invoke(objectIndex);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the target Penumbra collection.");
            return new ExportResult(false, $"Could not retrieve the target collection: {e.Message}");
        }

        if (!collection.ObjectValid || collection.EffectiveCollection.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(collection.EffectiveCollection.Name))
            return new ExportResult(false, "The target object has no valid Penumbra collection.");

        return ConfigureModForCollectionOnFramework(
            modName,
            collection.EffectiveCollection.Id,
            collection.EffectiveCollection.Name,
            setPriority,
            priority);
    }

    internal ExportResult ConfigureModForCollectionOnFramework(
        string modName,
        Guid collectionId,
        string collectionName,
        bool setPriority = true,
        int priority = int.MaxValue,
        bool redraw = true)
        => ApplyModToCollection(
            modName,
            collectionName,
            () => _trySetMod.Invoke(collectionId, modName, true, modName),
            setPriority ? () => _trySetModPriority.Invoke(collectionId, modName, priority, modName) : null,
            redraw ? RedrawPlayerOwnedEntitiesOnFramework : null,
            (message, error) => _log.Error(error, message));

    /// <summary>
    /// Enables a mod in a collection, optionally sets its priority, and optionally redraws the
    /// player-owned entities afterwards. The steps are delegates so the sequencing can be
    /// regression-tested without Penumbra: skipping the redraw must never skip the enable.
    /// </summary>
    internal static ExportResult ApplyModToCollection(
        string modName,
        string collectionName,
        Func<PenumbraApiEc> enable,
        Func<PenumbraApiEc>? setPriority,
        Func<string?>? redraw,
        Action<string, Exception>? logError = null)
    {
        PenumbraApiEc enabledResult;
        try
        {
            enabledResult = enable();
        }
        catch (Exception e)
        {
            logError?.Invoke("Could not enable the XIV Instant Edit mod in Penumbra.", e);
            return new ExportResult(false, $"Penumbra enable failed: {e.Message}");
        }

        if (enabledResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected enabling the mod ({enabledResult}).");

        if (setPriority is not null)
        {
            PenumbraApiEc priorityResult;
            try
            {
                priorityResult = setPriority();
            }
            catch (Exception e)
            {
                logError?.Invoke("Could not prioritize the XIV Instant Edit mod in Penumbra.", e);
                return new ExportResult(false, $"Penumbra priority failed: {e.Message}");
            }

            if (priorityResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected the mod priority ({priorityResult}).");
        }

        var applied = $"Applied {modName} to {collectionName}.";
        if (redraw is null)
            return new ExportResult(true, applied);

        try
        {
            var redrawWarning = redraw();
            return redrawWarning is null
                ? new ExportResult(true, applied)
                : new ExportResult(true, ResultCode.ExportAppliedWithWarnings, applied, [redrawWarning]);
        }
        catch (Exception e)
        {
            logError?.Invoke("Penumbra redraw failed after applying export.", e);
            return new ExportResult(false, $"Penumbra redraw failed: {e.Message}");
        }
    }

    /// <summary>Redraw the local player and the currently spawned entities they own.</summary>
    internal string? RedrawPlayerOwnedEntitiesOnFramework()
    {
        if (_objects.LocalPlayer is not { Address: not 0 } localPlayer)
            return "The local player could not be found; player-owned redraw was skipped.";

        var targets = new HashSet<ushort> { localPlayer.ObjectIndex };
        var warnings = new List<string>();
        try
        {
            if (localPlayer.EntityId != 0)
            {
                var localOwnerId = localPlayer.EntityId;
                foreach (var candidate in _objects)
                {
                    if (candidate is null || candidate.Address == nint.Zero ||
                        candidate.ObjectIndex == localPlayer.ObjectIndex ||
                        candidate.OwnerId != localOwnerId)
                        continue;

                    if (candidate.ObjectKind is ObjectKind.Companion or ObjectKind.Mount or ObjectKind.FollowMount ||
                        candidate is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet })
                        targets.Add(candidate.ObjectIndex);
                }
            }
            else
            {
                warnings.Add("The local player entity ID was unavailable; only the player was redrawn.");
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not enumerate player-owned entities for redraw.");
            warnings.Add("Could not enumerate all player-owned entities; continuing with the targets found.");
        }

        var failed = 0;
        foreach (var objectIndex in targets)
        {
            try
            {
                _redrawObject.Invoke(objectIndex);
            }
            catch (Exception e)
            {
                failed++;
                _log.Error(e, "Penumbra redraw failed for player-owned object {ObjectIndex}.", objectIndex);
            }
        }

        if (failed > 0)
            warnings.Add($"Penumbra redraw failed for {failed} player-owned object(s).");
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }
}
