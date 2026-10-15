using Dalamud.Plugin.Ipc.Exceptions;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services;

/// <summary> The collection an object uses, and whether it was assigned to that character alone. </summary>
internal sealed record TemporaryModCollection(Guid Id, string Name, bool IndividualSet);

/// <summary>
/// Penumbra temporary mods: in-memory redirects Penumbra never saves, which win over regular mods
/// only with a strictly higher priority. The *OnFramework calls must run on the framework thread.
/// </summary>
public sealed partial class PenumbraService
{
    private AddTemporaryMod? _addTemporaryMod;
    private RemoveTemporaryMod? _removeTemporaryMod;
    private AddTemporaryModAll? _addTemporaryModAll;
    private RemoveTemporaryModAll? _removeTemporaryModAll;
    private ResolveInterfacePath? _resolveInterfacePath;
    private GetCollections? _getCollections;

    /// <summary> The effective collection of an object, with IndividualSet; null when the object has none. </summary>
    internal TemporaryModCollection? TemporaryModCollectionOnFramework(int objectIndex)
    {
        var (valid, individual, (id, name)) = _getCollectionForObject.Invoke(objectIndex);
        return valid && id != Guid.Empty ? new TemporaryModCollection(id, name ?? string.Empty, individual) : null;
    }

    internal Task<TemporaryModCollection?> TemporaryModCollectionAsync(int objectIndex)
        => _framework.RunOnFrameworkThread(() => TemporaryModCollectionOnFramework(objectIndex));

    /// <summary>
    /// Registers, or with the same tag and priority updates in place, a temporary mod in one
    /// collection. Rooted values are file redirects; no meta manipulations.
    /// </summary>
    internal PenumbraApiEc AddTemporaryModOnFramework(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, int priority)
    {
        if (collection == Guid.Empty)
            throw new InvalidDataException("The temporary mod is invalid.");
        return (_addTemporaryMod ??= new AddTemporaryMod(_pi)).Invoke(tag, collection, Redirects(tag, paths), string.Empty, priority);
    }

    /// <summary>
    /// Registers, or with the same tag and priority updates in place, a temporary mod in every
    /// collection, for a file whose game path nothing else uses (such as a sound the Timeline plays
    /// to listen to). Rooted values are file redirects; no meta manipulations.
    /// </summary>
    internal PenumbraApiEc AddTemporaryModAllOnFramework(string tag, IReadOnlyDictionary<string, string> paths, int priority)
        => (_addTemporaryModAll ??= new AddTemporaryModAll(_pi)).Invoke(tag, Redirects(tag, paths), string.Empty, priority);

    /// <summary> What the Interface collection resolves <paramref name="gamePath"/> to: the game path itself when nothing changes it. </summary>
    internal string ResolveInterfacePathOnFramework(string gamePath)
        => (_resolveInterfacePath ??= new ResolveInterfacePath(_pi)).Invoke(gamePath);

    /// <summary> Removes a temporary mod from every collection; Gone as for <see cref="RemoveTemporaryModOnFramework"/>. </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModAllOnFramework(string tag, int priority)
    {
        try
        {
            var result = (_removeTemporaryModAll ??= new RemoveTemporaryModAll(_pi)).Invoke(tag, priority);
            return result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged
                ? (true, null)
                : (false, $"Penumbra could not remove the temporary mod ({result}).");
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
    }

    private static Dictionary<string, string> Redirects(string tag, IReadOnlyDictionary<string, string> paths)
    {
        if (string.IsNullOrWhiteSpace(tag) || paths.Count == 0)
            throw new InvalidDataException("The temporary mod is invalid.");
        var redirects = new Dictionary<string, string>(paths.Count, StringComparer.Ordinal);
        foreach (var (gamePath, file) in paths)
        {
            if (!Path.IsPathFullyQualified(file))
                throw new InvalidDataException($"The temporary mod file for {gamePath} is not a full path.");
            redirects.Add(gamePath, file);
        }
        return redirects;
    }

    /// <summary>
    /// Removes a temporary mod. Gone is true when Penumbra no longer holds it: removed, never
    /// registered, its collection deleted, or Penumbra not loaded (it keeps temporary mods in memory only).
    /// </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModOnFramework(string tag, Guid collection, int priority)
    {
        try
        {
            var result = (_removeTemporaryMod ??= new RemoveTemporaryMod(_pi)).Invoke(tag, collection, priority);
            return result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.CollectionMissing
                ? (true, null)
                : (false, $"Penumbra could not remove the preview ({result}).");
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
    }

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModAsync(string tag, Guid collection, int priority)
        => _framework.RunOnFrameworkThread(() => RemoveTemporaryModOnFramework(tag, collection, priority));

    /// <summary>
    /// Removes a temporary mod from every collection, for when the record of where it was
    /// registered is lost. Gone is true only when no collection may still hold it.
    /// </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModEverywhereOnFramework(string tag, int priority)
    {
        Dictionary<Guid, string> collections;
        try
        {
            collections = (_getCollections ??= new GetCollections(_pi)).Invoke();
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
        foreach (var collection in collections.Keys)
            if (RemoveTemporaryModOnFramework(tag, collection, priority) is { Gone: false } failed)
                return failed;
        return (true, null);
    }

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModEverywhereAsync(string tag, int priority)
        => _framework.RunOnFrameworkThread(() => RemoveTemporaryModEverywhereOnFramework(tag, priority));

    /// <summary>
    /// Calls <paramref name="restarted"/> when Penumbra is disposed or initialized; either way it no
    /// longer holds any temporary mod. The handler runs on Penumbra's thread and must be cheap.
    /// The caller owns and disposes the subscription.
    /// </summary>
    internal IDisposable SubscribePenumbraLifecycle(Action restarted)
        => new LifecycleSubscription(Initialized.Subscriber(_pi, restarted), Disposed.Subscriber(_pi, restarted));

    private sealed class LifecycleSubscription(EventSubscriber initialized, EventSubscriber disposed) : IDisposable
    {
        public void Dispose()
        {
            initialized.Dispose();
            disposed.Dispose();
        }
    }
}
