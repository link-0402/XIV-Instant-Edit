using InstantEdit.Models;
using InstantEdit.Services.Animations;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    internal void CheckAnimationCollectionOnFramework(ushort objectIndex, Guid collection)
    {
        var current = _getCollectionForObject.Invoke(objectIndex);
        if (!current.ObjectValid || current.EffectiveCollection.Id != collection)
            throw new IOException("The player's collection changed.");
    }
    internal async Task<string> ResolveAnimationPathAsync(Guid collection, string gamePath)
    {
        if (collection == Guid.Empty || !AnimationDependencies.SafeGamePath(gamePath))
            throw new InvalidDataException("Invalid animation collection or game path.");
        return await _framework.RunOnFrameworkThread(() =>
        {
            var result = new ResolvePath(_pi).Invoke(collection, gamePath, out var resolved);
            if (result != PenumbraApiEc.Success || string.IsNullOrWhiteSpace(resolved))
                throw new IOException($"Penumbra could not resolve {gamePath} ({result}).");
            return resolved;
        });
    }

    internal async Task AnimationExportAsync(Func<Task> action, CancellationToken token)
    {
        await _exportGate.WaitAsync(token);
        try { await action(); }
        finally { _exportGate.Release(); }
    }

    internal Task<string> AnimationModRootAsync() => _framework.RunOnFrameworkThread(() =>
        _getModDirectory.Invoke() ?? throw new IOException("Penumbra has no mod directory."));

    internal Task<string> AnimationMetadataAsync(Guid collection) => _framework.RunOnFrameworkThread(() =>
    {
        var player = _objects?.LocalPlayer ?? throw new IOException("The player is unavailable.");
        var target = _getCollectionForObject.Invoke(player.ObjectIndex);
        if (!target.ObjectValid || target.EffectiveCollection.Id != collection) throw new IOException("The player collection changed.");
        return new GetMetaManipulations(_pi).Invoke(player.ObjectIndex);
    });

    internal async Task CheckAnimationModRootAsync(string mod, string root)
    {
        await _framework.RunOnFrameworkThread(() =>
        {
            var registered = GetRegisteredModPath(mod);
            if (registered == null || !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The registered location of {mod} changed. Recovery will not write to the old location.");
        });
        _ = LoadV4ModMetadata(root);
    }

    internal async Task ActivateAnimationAsync(AnimationEditJournal journal)
    {
        if (journal.Request.Destination == AnimationDestination.NewMod)
        {
            var add = await AddNewModAsync(journal.ModDirectory);
            if (add is { Success: false }) throw new IOException(add.Message);
        }
        await _framework.RunOnFrameworkThread(() =>
        {
            foreach (var mod in journal.Files.Select(f => f.ModDirectory).Distinct())
            {
                var reload = ReloadModOnFramework(mod);
                if (reload is { Success: false }) throw new IOException(reload.Message);
            }
            if (journal.Request.Destination == AnimationDestination.NewMod)
            {
                // Go above every enabled provider, including inherited settings and file-swap providers.
                var highest = 0;
                foreach (var mod in _getModList.Invoke().Keys.Where(m => m != journal.ModDirectory))
                {
                    var settings = _getCurrentModSettings.Invoke(journal.Request.Capture.CollectionId, mod, "", false);
                    if (settings.Item1 != PenumbraApiEc.Success || settings.Item2 == null)
                        throw new IOException($"Could not inspect the priority of {mod}.");
                    if (settings.Item2.Value.Item1) highest = Math.Max(highest, settings.Item2.Value.Item2);
                }
                if (highest == int.MaxValue) throw new IOException("An enabled mod already has maximum priority. Lower its priority before retrying.");
                var configured = ConfigureModForCollectionOnFramework(journal.ModDirectory,
                    journal.Request.Capture.CollectionId, journal.Request.Capture.CollectionName, priority: highest + 1);
                if (!configured.Success || configured.WarningList.Count > 0)
                    throw new IOException(configured.Message + " " + string.Join(" ", configured.WarningList));
            }
            else if (RedrawPlayerOwnedEntitiesOnFramework() is { } error) throw new IOException(error);
        });
    }

    internal Task DisableAnimationModAsync(AnimationEditJournal journal) => _framework.RunOnFrameworkThread(() =>
    {
        if (!_getModList.Invoke().ContainsKey(journal.ModDirectory)) return;
        var registered = GetRegisteredModPath(journal.ModDirectory);
        if (registered == null || !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(journal.ModRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The created mod's registered directory changed. Recovery will not disable a different mod.");
        var result = _trySetMod.Invoke(journal.Request.Capture.CollectionId, journal.ModDirectory, false, journal.ModDirectory);
        if (result is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            throw new IOException($"Penumbra could not disable the edited mod ({result}).");
        if (RedrawPlayerOwnedEntitiesOnFramework() is { } error) throw new IOException(error);
    });

    internal Task ReloadAnimationSourcesAsync(IEnumerable<string> mods) => _framework.RunOnFrameworkThread(() =>
    {
        foreach (var mod in mods.Distinct())
            if (ReloadModOnFramework(mod) is { Success: false } error) throw new IOException(error.Message);
        if (RedrawPlayerOwnedEntitiesOnFramework() is { } warning) throw new IOException(warning);
    });
}
