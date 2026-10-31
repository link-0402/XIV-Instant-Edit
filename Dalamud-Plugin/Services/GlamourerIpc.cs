using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Actors;

namespace InstantEdit.Services;

/// <summary>
/// Glamourer's designs for partners: applied to an object and locked under Instant Edit's key, so nothing else changes
/// the look until it is released. Nothing throws: without Glamourer the list is empty and the apply is NotLoaded, and any
/// other error is logged. Call on the framework thread.
/// </summary>
internal sealed class GlamourerIpc : IPartnerLooks
{
    /// <summary>The key Instant Edit locks a partner's Glamourer state with.</summary>
    public const uint Key = 0x49455054;

    // Glamourer 1.x's IPC; a new major version may change these calls.
    private const int ApiMajor = 1;
    // ApplyFlag: Equipment 2, Customization 4, Lock 8.
    private const ulong ApplyFlags = 14, RevertFlags = 6;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> designList;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> applyDesign;
    private readonly ICallGateSubscriber<int, uint, int> unlockState;
    private readonly ICallGateSubscriber<int, uint, ulong, int> revertState;

    public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        designList = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
        applyDesign = pi.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        unlockState = pi.GetIpcSubscriber<int, uint, int>("Glamourer.UnlockState");
        revertState = pi.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
    }

    /// <summary>Whether Glamourer is loaded with an IPC version this reads.</summary>
    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.HasFunction && apiVersion.InvokeFunc().Item1 == ApiMajor;
            }
            catch (IpcNotReadyError)
            {
                return false;
            }
            catch (Exception e)
            {
                log.Warning(e, "Could not read Glamourer's IPC version.");
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
            return designList.InvokeFunc().Select(pair => new PartnerLink(pair.Key, pair.Value))
                .OrderBy(link => link.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IpcNotReadyError)
        {
            return [];
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not list Glamourer's designs for a partner.");
            return [];
        }
    }

    /// <summary>Applies the design locked under <see cref="Key"/>, after unlocking a lock a crash may have left.</summary>
    public LinkOutcome Apply(int objectIndex, Guid design)
    {
        if (!Available)
            return new LinkOutcome(LinkStatus.NotLoaded);
        try
        {
            unlockState.InvokeFunc(objectIndex, Key);
            return Outcome(applyDesign.InvokeFunc(design, objectIndex, Key, ApplyFlags));
        }
        catch (IpcNotReadyError)
        {
            return new LinkOutcome(LinkStatus.NotLoaded);
        }
        catch (Exception e)
        {
            log.Warning(e, $"Could not apply a Glamourer design to object {objectIndex}.");
            return new LinkOutcome(LinkStatus.Failed, -1);
        }
    }

    public void Release(int objectIndex)
    {
        try
        {
            unlockState.InvokeFunc(objectIndex, Key);
            var reverted = revertState.InvokeFunc(objectIndex, Key, RevertFlags);
            if (Outcome(reverted).Status != LinkStatus.Applied)
                log.Warning($"Glamourer did not revert object {objectIndex} (error {reverted}).");
        }
        catch (IpcNotReadyError)
        {
            // Glamourer keeps its state in memory, so without it there is nothing left to revert.
        }
        catch (Exception e)
        {
            log.Warning(e, $"Could not release object {objectIndex}'s Glamourer state.");
        }
    }

    /// <summary>What Glamourer's answer means for the link: success and nothing done apply, 4 is a design not found.</summary>
    internal static LinkOutcome Outcome(int code) => code switch
    {
        0 or 1 => new LinkOutcome(LinkStatus.Applied),
        4 => new LinkOutcome(LinkStatus.Missing),
        _ => new LinkOutcome(LinkStatus.Failed, code),
    };
}
