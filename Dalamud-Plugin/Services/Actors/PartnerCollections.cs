using Dalamud.Plugin.Ipc.Exceptions;
using InstantEdit.Services.PenumbraMods;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.Actors;

/// <summary>
/// A partner's Penumbra calls. Nothing throws: without Penumbra each call answers as if nothing is there (NotLoaded for
/// <see cref="Assign"/>), and any other error is logged. Call on the framework thread.
/// </summary>
internal sealed class PartnerCollections(PenumbraIpc ipc, Func<bool> available) : IPartnerCollections
{
    public bool Available
    {
        get
        {
            try { return available(); }
            catch (Exception e)
            {
                ipc.Log.Warning(e, "Could not check whether Penumbra is available for a partner.");
                return false;
            }
        }
    }

    public IReadOnlyList<PartnerLink> List()
    {
        if (!Available)
            return [];
        try
        {
            return ipc.CollectionsOnFramework().Select(pair => new PartnerLink(pair.Key, pair.Value))
                .OrderBy(link => link.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IpcNotReadyError)
        {
            return [];
        }
        catch (Exception e)
        {
            ipc.Log.Warning(e, "Could not list Penumbra's collections for a partner.");
            return [];
        }
    }

    public int ParentOf(int objectIndex)
    {
        if (!Available)
            return -1;
        try
        {
            return ipc.CutsceneParentOnFramework(objectIndex);
        }
        // An unknown parent refuses the spawn: assigning to an actor parented to you would assign you.
        catch (IpcNotReadyError)
        {
            return int.MinValue;
        }
        catch (Exception e)
        {
            ipc.Log.Warning(e, $"Could not read Penumbra's parent of object {objectIndex}.");
            return int.MinValue;
        }
    }

    public CollectionAssignment? Current(int objectIndex)
    {
        if (!Available)
            return null;
        try
        {
            return ipc.TemporaryModCollectionOnFramework(objectIndex) is { } current ? new CollectionAssignment(current.IndividualSet, current.Id) : null;
        }
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception e)
        {
            ipc.Log.Warning(e, $"Could not read Penumbra's collection of object {objectIndex}.");
            return null;
        }
    }

    public LinkOutcome Assign(int objectIndex, Guid? collection, bool allowCreate, bool allowDelete)
    {
        // The despawn assigns without checking Available first.
        if (!Available)
            return new LinkOutcome(LinkStatus.NotLoaded);
        try
        {
            return Outcome(ipc.SetCollectionForObjectOnFramework(objectIndex, collection, allowCreate, allowDelete));
        }
        catch (IpcNotReadyError)
        {
            return new LinkOutcome(LinkStatus.NotLoaded);
        }
        catch (Exception e)
        {
            ipc.Log.Warning(e, $"Could not set Penumbra's collection of object {objectIndex}.");
            return new LinkOutcome(LinkStatus.Failed, -1);
        }
    }

    /// <summary>What Penumbra's answer to an assignment means for the link.</summary>
    internal static LinkOutcome Outcome(PenumbraApiEc code) => code switch
    {
        PenumbraApiEc.Success or PenumbraApiEc.NothingChanged => new LinkOutcome(LinkStatus.Applied),
        PenumbraApiEc.CollectionMissing => new LinkOutcome(LinkStatus.Missing),
        _ => new LinkOutcome(LinkStatus.Failed, (int)code),
    };
}
