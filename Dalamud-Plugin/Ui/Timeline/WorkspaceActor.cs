using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Actors;
using InstantEdit.Services.Animations.Preview;

namespace InstantEdit.Ui.Timeline;

/// <summary>Which workspace the GPose target shows, and which actor yours acts on.</summary>
internal static class WorkspaceChoice
{
    /// <summary>A targeted partner (spawned or still spawning) shows its own workspace; anything else, or no GPose, shows yours.</summary>
    public static WorkspaceKey Shown(bool gposing, ushort? targetIndex, Func<ushort, Guid?> presetAt) =>
        gposing && targetIndex is { } index && presetAt(index) is { } preset ? new WorkspaceKey(preset) : WorkspaceKey.Self;

    /// <summary>
    /// Your workspace's actor in GPose: the target when it is a GPose character (200 and up) that is no partner, such as a
    /// Brio actor, else your posed character at 201 when it is there; null out of GPose.
    /// </summary>
    public static ushort? SelfIndex(bool gposing, ushort? targetIndex, Func<ushort, Guid?> presetAt, bool posedExists)
    {
        if (!gposing)
            return null;
        if (targetIndex is { } index && index >= AnimationPlayback.FirstGPoseIndex && presetAt(index) == null)
            return index;
        return posedExists ? (ushort)AnimationPlayback.GPosePlayerIndex : null;
    }
}

/// <summary>The actors the workspaces' playbacks act on.</summary>
internal static class WorkspaceActors
{
    /// <summary>Your workspace: <see cref="WorkspaceChoice.SelfIndex"/> in GPose; the bone reads fall back to your own character.</summary>
    public static PlaybackActor Self(IObjectTable objects, IClientState clientState, ITargetManager targets, Func<ushort, Guid?> presetAt)
    {
        ICharacter? GPose()
        {
            var target = targets.GPoseTarget is ICharacter { Address: not 0 } character ? character.ObjectIndex : (ushort?)null;
            var posed = objects[AnimationPlayback.GPosePlayerIndex] is ICharacter { Address: not 0 };
            return WorkspaceChoice.SelfIndex(clientState.IsGPosing, target, presetAt, posed) is { } index &&
                   objects[index] is ICharacter { Address: not 0 } actor ? actor : null;
        }
        return new PlaybackActor(GPose, () => (IGameObject?)GPose() ?? objects.LocalPlayer);
    }

    /// <summary>A partner's workspace: its preset's actor once it is Ready in GPose, else none, for plays and reads alike.</summary>
    public static PlaybackActor Partner(Guid preset, PartnerService partners, IObjectTable objects, IClientState clientState)
    {
        ICharacter? Actor() =>
            clientState.IsGPosing && partners.Find(preset) is { Stage: PartnerStage.Ready } view &&
            objects[view.ObjectIndex] is ICharacter { Address: not 0 } actor ? actor : null;
        return new PlaybackActor(Actor, () => Actor());
    }
}
