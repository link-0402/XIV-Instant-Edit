using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Actors;

namespace InstantEdit.Services;

/// <summary>
/// Customize+'s profiles for partners: a profile's bones are put on an object as a temporary profile, which Customize+
/// never saves. Nothing throws: without Customize+ the list is empty and the apply is NotLoaded, and any other error is
/// logged. Call on the framework thread: Customize+ reads the object table.
/// </summary>
internal sealed class CustomizePlusProfiles : IPartnerProfiles
{
    // Customize+ 2.x's IPC; a new breaking version may change these calls.
    private const int ApiVersion = 6;
    private const int Success = 0;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>> profileList;
    private readonly ICallGateSubscriber<Guid, (int, string?)> profileJson;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporaryProfile;
    private readonly ICallGateSubscriber<Guid, int> deleteTemporaryProfile;

    public CustomizePlusProfiles(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        profileList = pi.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
        profileJson = pi.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        setTemporaryProfile = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        deleteTemporaryProfile = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
    }

    /// <summary>Whether Customize+ is loaded with an IPC version this reads.</summary>
    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.HasFunction && apiVersion.InvokeFunc().Item1 == ApiVersion;
            }
            catch (IpcNotReadyError)
            {
                return false;
            }
            catch (Exception e)
            {
                log.Warning(e, "Could not read Customize+'s IPC version.");
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
            return profileList.InvokeFunc().Select(entry => new PartnerLink(entry.Item1, entry.Item2))
                .OrderBy(link => link.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IpcNotReadyError)
        {
            return [];
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not list Customize+'s profiles for a partner.");
            return [];
        }
    }

    /// <summary>Reads the profile and puts its bones on the object as a temporary profile.</summary>
    public (LinkOutcome Outcome, Guid? Temporary) Apply(ushort objectIndex, Guid profile)
    {
        if (!Available)
            return (new LinkOutcome(LinkStatus.NotLoaded), null);
        try
        {
            var (found, json) = profileJson.InvokeFunc(profile);
            if (found != Success)
                return (Outcome(found), null);
            if (json is null)
                return (new LinkOutcome(LinkStatus.Failed, -1), null);
            var (result, temporary) = setTemporaryProfile.InvokeFunc(objectIndex, json);
            var outcome = Outcome(result);
            if (outcome.Status != LinkStatus.Applied)
                return (outcome, null);
            return temporary is { } id ? (outcome, id) : (new LinkOutcome(LinkStatus.Failed, -1), null);
        }
        catch (IpcNotReadyError)
        {
            return (new LinkOutcome(LinkStatus.NotLoaded), null);
        }
        catch (Exception e)
        {
            log.Warning(e, $"Could not put a Customize+ profile on object {objectIndex}.");
            return (new LinkOutcome(LinkStatus.Failed, -1), null);
        }
    }

    public void Remove(Guid temporary)
    {
        try
        {
            var result = deleteTemporaryProfile.InvokeFunc(temporary);
            if (result != Success)
                log.Warning($"Customize+ did not remove a partner's temporary profile (error {result}).");
        }
        catch (IpcNotReadyError)
        {
            // Customize+ never saves temporary profiles, so without it there is nothing to remove.
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not remove a partner's temporary Customize+ profile.");
        }
    }

    /// <summary>What Customize+'s answer means for the link: 3 is a profile not found.</summary>
    internal static LinkOutcome Outcome(int code) => code switch
    {
        Success => new LinkOutcome(LinkStatus.Applied),
        3 => new LinkOutcome(LinkStatus.Missing),
        _ => new LinkOutcome(LinkStatus.Failed, code),
    };
}
