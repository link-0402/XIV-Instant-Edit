using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Pauses Customize+ on one character with an empty temporary profile. Customize+ changes a
/// character's bones as the game renders them, so a recording of the live pose would carry the
/// character's C+ changes, and MagicFit's Customize+ would add them a second time in Blender.
/// </summary>
/// <remarks>
/// Only characters with an active Customize+ profile are paused. A temporary profile replaces the
/// character's current one, and Customize+ can't report temporary profiles (such as Mare's on
/// synced players or Brio's on its actors), so a character without a profile is left alone rather
/// than losing one. Customize+ keeps one temporary profile per character, though, so one with both
/// a profile of its own and a temporary one loses the temporary one to the pause. Customize+'s
/// template editor outranks temporary profiles, so a character it previews keeps its changes.
/// <see cref="RecordingScale"/> reports scaling left in a recording either way. Customize+ never
/// saves temporary profiles, so a pause can't outlive the game session. Call on the framework
/// thread: Customize+ reads the object table.
/// </remarks>
internal sealed class CustomizePlusPause
{
    // Customize+ 2.x's IPC; a new breaking version may change these calls.
    private const int ApiVersion = 6;
    private const int Success = 0;
    private const string EmptyProfile = "{\"Bones\":{}}";

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> activeProfile;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> setTemporaryProfile;
    private readonly ICallGateSubscriber<Guid, int> deleteTemporaryProfile;

    public CustomizePlusPause(IDalamudPluginInterface pi, IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        activeProfile = pi.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        setTemporaryProfile = pi.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        deleteTemporaryProfile = pi.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
    }

    /// <summary>
    /// Pauses Customize+ on the character at <paramref name="objectIndex"/>. Returns the temporary
    /// profile to pass to <see cref="Resume"/>, or null when nothing was paused; then
    /// <paramref name="problem"/> says why a character with a profile keeps it, or is null.
    /// </summary>
    public Guid? Pause(ushort objectIndex, out string? problem)
    {
        problem = null;
        try
        {
            if (!apiVersion.HasFunction) return null;
            var (breaking, _) = apiVersion.InvokeFunc();
            if (breaking != ApiVersion)
            {
                problem = $"Customize+ could not be paused (unsupported IPC version {breaking})";
                return null;
            }
            var (found, profile) = activeProfile.InvokeFunc(objectIndex);
            if (found != Success || profile is null) return null;
            var (result, paused) = setTemporaryProfile.InvokeFunc(objectIndex, EmptyProfile);
            if (result == Success && paused is { } id) return id;
            problem = $"Customize+ could not be paused (error {result})";
            return null;
        }
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not pause Customize+ for a recording.");
            problem = "Customize+ could not be paused: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// Removes a pause made by <see cref="Pause"/>, so the character's own profile applies again.
    /// Runs at once on the framework thread and is queued there from any other thread.
    /// </summary>
    public void Resume(Guid pause)
    {
        try
        {
            _ = framework.RunOnFrameworkThread(() => ResumeNow(pause));
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not resume Customize+ after a recording; reloading Customize+ restores the profile.");
        }
    }

    private void ResumeNow(Guid pause)
    {
        try
        {
            var result = deleteTemporaryProfile.InvokeFunc(pause);
            if (result != Success)
                log.Warning($"Customize+ did not remove the recording's pause (error {result}); reloading Customize+ restores the profile.");
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not resume Customize+ after a recording; reloading Customize+ restores the profile.");
        }
    }
}
