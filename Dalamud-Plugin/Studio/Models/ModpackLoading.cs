using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>How far reading the game's emotes and animation timelines has got.</summary>
internal enum AnimationScanState { Pending, Ready, Failed }

/// <summary>
/// The rules for reading saved modpack keys back. Animation keys resolve from the emote list, which
/// is read on a worker: the pack waits for it rather than blocking the frame, and keeps animation
/// items when it could not be read rather than dropping them.
/// </summary>
internal static class ModpackLoading
{
    public static bool IsAnimationKey(uint key) => PortSubject.KeyTag(key) is KeyTagKind.Emote or KeyTagKind.Timeline;

    /// <summary>Whether <paramref name="keys"/> can be resolved now without waiting for the emote list.</summary>
    public static bool CanResolve(IEnumerable<uint> keys, AnimationScanState animations)
        => animations != AnimationScanState.Pending || !keys.Any(IsAnimationKey);

    /// <summary>
    /// Whether a key that resolved to nothing names something the game no longer has (it leaves the
    /// pack), rather than an animation that couldn't be looked up this time (it stays saved).
    /// </summary>
    public static bool IsGone(uint key, AnimationScanState animations)
        => !IsAnimationKey(key) || animations == AnimationScanState.Ready;
}
