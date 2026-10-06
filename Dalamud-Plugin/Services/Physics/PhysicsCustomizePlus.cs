using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services.Physics;

/// <summary>
/// Reads the Customize+ profile a character uses, so a body can be measured with it but without the
/// pose it changes. Customize+ shares regular profiles only: temporary ones, like a sync plugin's or a
/// posing tool's, can't be read. Call on the framework thread: Customize+ reads the object table.
/// </summary>
internal sealed class PhysicsCustomizePlus
{
    // Customize+ 2.x's IPC; a new breaking version may change these calls.
    private const int ApiVersion = 6;
    private const int Success = 0;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> activeProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> profile;

    public PhysicsCustomizePlus(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        activeProfile = pi.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        profile = pi.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
    }

    /// <summary>
    /// The bones of the profile the character at <paramref name="objectIndex"/> uses: null without one,
    /// with <paramref name="installed"/> false when Customize+ isn't running, and a problem when a profile
    /// exists but couldn't be read.
    /// </summary>
    public IReadOnlyDictionary<string, PhysicsPlusBone>? Read(ushort objectIndex, out bool installed, out string? problem)
    {
        installed = false;
        problem = null;
        try
        {
            if (!apiVersion.HasFunction)
                return null;
            var (breaking, _) = apiVersion.InvokeFunc();
            installed = true;
            if (breaking != ApiVersion)
            {
                problem = $"Customize+'s IPC version {breaking} isn't one this version reads";
                return null;
            }
            var (found, id) = activeProfile.InvokeFunc(objectIndex);
            if (found != Success || id is not { } unique)
                return null;
            var (result, json) = profile.InvokeFunc(unique);
            if (result != Success || json is null)
            {
                problem = $"Customize+ didn't share the profile (error {result})";
                return null;
            }
            return PhysicsPose.ParsePlus(json);
        }
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not read the Customize+ profile to measure the body.");
            problem = "the Customize+ profile couldn't be read: " + e.Message;
            return null;
        }
    }
}
