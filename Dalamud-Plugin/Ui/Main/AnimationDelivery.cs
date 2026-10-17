using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.CharacterSend;

namespace InstantEdit.Ui.Main;

/// <summary>
/// Sends animations to Blender, keyed onto a scene armature as an action: a clip's animation
/// file, a recording of a character's live skeleton, or the pose of a character send.
/// </summary>
internal sealed class AnimationDelivery
{
    private readonly Configuration _config;
    private readonly BlenderClient _blender;
    private readonly BlenderConnection _blenderConnection;

    public AnimationDelivery(Configuration config, BlenderClient blender, BlenderConnection blenderConnection)
    {
        _config = config;
        _blender = blender;
        _blenderConnection = blenderConnection;
    }

    // As a character send names its armature, so animations find the armature it built.
    public string AnimationArmature => CharacterSendPlan.ArmatureName(_config.AnimationArmatureName);

    /// <summary>Encodes a take and sends it to Blender. Returns what Blender did with it, or throws with a user-facing message.</summary>
    public Task<string> DeliverAnimationAsync(AnimationTake take, CancellationToken token)
        => DeliverTakeAsync(take, null, token);

    /// <summary>
    /// Encodes a take and sends it to Blender, onto the armature of the character send
    /// <paramref name="targetCharacter"/> names, else the configured armature. Returns what Blender
    /// did with it, or throws with a user-facing message.
    /// </summary>
    public async Task<string> DeliverTakeAsync(AnimationTake take, string? targetCharacter, CancellationToken token)
    {
        var pluginVersion = _blenderConnection.PluginVersion;
        var port = _config.BlenderPort;
        var status = await _blenderConnection.CheckStatusAsync(port, token).ConfigureAwait(false);
        if (!status.Reachable)
            throw new InvalidOperationException("Blender is offline. Start Blender with the XIV Instant Edit add-on, then send the animation again.");
        if (status.Classify(ComponentVersions.BlenderAddonMinimum) != BlenderConnectionState.Online)
            throw new InvalidOperationException(BlenderClient.VersionMismatchMessage(status.AddonVersion, ComponentVersions.BlenderAddonMinimum));
        if (!await _blender.SupportsAnimationImportAsync(port, token).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old to receive animations. Update the add-on and restart Blender.");
        var armature = AnimationArmature;
        var keyScale = _config.AnimationKeyScale;
        var body = await Task.Run(() => AnimationTakeFormat.Write(take, armature, keyScale, pluginVersion, targetCharacter), token)
            .ConfigureAwait(false);
        try
        {
            return (await _blender.SendAnimationAsync(port, body, token).ConfigureAwait(false)).Describe();
        }
        catch (BlenderBridgeException e)
        {
            throw new InvalidOperationException(e.Failure.UserMessage);
        }
    }

    /// <summary>
    /// Reads the motion of the armature animations are sent to back from Blender (Motion from Blender).
    /// Returns Blender's reply, checked against <paramref name="request"/>, or throws with a user-facing message.
    /// </summary>
    public async Task<AnimationRead> ReadMotionAsync(AnimationReadRequest request, CancellationToken token)
    {
        var pluginVersion = _blenderConnection.PluginVersion;
        var port = _config.BlenderPort;
        var status = await _blenderConnection.CheckStatusAsync(port, token).ConfigureAwait(false);
        if (!status.Reachable)
            throw new InvalidOperationException("Blender is offline. Start Blender with the XIV Instant Edit add-on, then try again.");
        if (status.Classify(ComponentVersions.BlenderAddonMinimum) != BlenderConnectionState.Online)
            throw new InvalidOperationException(BlenderClient.VersionMismatchMessage(status.AddonVersion, ComponentVersions.BlenderAddonMinimum));
        if (!await _blender.SupportsAnimationReadAsync(port, token).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old to send animations back. Update the add-on and restart Blender.");
        var body = AnimationReadFormat.WriteRequest(request, AnimationArmature, null, pluginVersion);
        try
        {
            return await _blender.ReadAnimationAsync(port, body, request, token).ConfigureAwait(false);
        }
        catch (BlenderBridgeException e)
        {
            throw new InvalidOperationException(e.Failure.UserMessage);
        }
    }
}
