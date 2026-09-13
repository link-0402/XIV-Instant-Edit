using InstantEdit.Models;
using InstantEdit.Services.Animations;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    internal Task<(string Directory, string Root)[]> AnimationSkeletonRootsAsync() => _framework.RunOnFrameworkThread(() =>
    {
        using var mods = new GetModListAdapter(_pi).Invoke();
        return mods.Select(m => (m.Identifier, m.ModPath.FullName)).ToArray();
    });
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
            var registered = RegisteredAnimationModRootOnFramework(mod);
            if (!PathRules.SamePhysicalPath(registered, root))
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
                    if (settings.Item1 != PenumbraApiEc.Success)
                        throw new IOException($"Could not inspect the priority of {mod}.");
                    if (settings.Item2 is { } current && current.Item1)
                        highest = Math.Max(highest, current.Item2);
                }
                if (highest == int.MaxValue) throw new IOException("An enabled mod already has maximum priority. Lower its priority before retrying.");
                var priority = _trySetModPriority.Invoke(journal.Request.Capture.CollectionId, journal.ModDirectory, highest + 1, journal.ModDirectory);
                if (priority is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                    throw new IOException($"Penumbra could not prioritize the edited mod ({priority}).");
                var enable = _trySetMod.Invoke(journal.Request.Capture.CollectionId, journal.ModDirectory, true, journal.ModDirectory);
                if (enable is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                    throw new IOException($"Penumbra could not enable the edited mod ({enable}).");
            }
        });
    }

    private string? RegisteredAnimationModRootOnFramework(string directory)
    {
        // GetModPath is Penumbra's virtual UI folder/sort path. ModPath from the
        // synchronized mod adapter is the actual registered directory on disk.
        using var mods = new GetModListAdapter(_pi).Invoke();
        foreach (var mod in mods)
            if (string.Equals(mod.Identifier, directory, StringComparison.Ordinal)) return mod.ModPath.FullName;
        return null;
    }

    internal Task RedrawAnimationAsync() => _framework.RunOnFrameworkThread(() =>
    {
        if (RedrawPlayerOwnedEntitiesOnFramework() is { } error) throw new IOException(error);
    });

    internal Task DisableAnimationModAsync(AnimationEditJournal journal) => _framework.RunOnFrameworkThread(() =>
    {
        if (!_getModList.Invoke().ContainsKey(journal.ModDirectory)) return;
        var registered = RegisteredAnimationModRootOnFramework(journal.ModDirectory);
        if (!PathRules.SamePhysicalPath(registered, journal.ModRoot))
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
