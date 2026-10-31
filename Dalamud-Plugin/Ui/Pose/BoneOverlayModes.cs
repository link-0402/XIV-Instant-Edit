using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Pose;

/// <summary>Which bones the Bones window shows on the GPose character (View, On character).</summary>
internal enum BoneOverlayMode : byte
{
    Off,
    /// <summary>Only the selected bones, with the gizmo.</summary>
    Selected,
    /// <summary>The map's bones, the selected ones and the ones the layer changes.</summary>
    All,
}

/// <summary>The overlay switch's steps and which bones get a dot. Pure, so it is tested without ImGui.</summary>
internal static class BoneOverlayModes
{
    /// <summary>The mode after <paramref name="mode"/>: off, selected, all and round again.</summary>
    public static BoneOverlayMode Next(BoneOverlayMode mode) => mode switch
    {
        BoneOverlayMode.Off => BoneOverlayMode.Selected,
        BoneOverlayMode.Selected => BoneOverlayMode.All,
        _ => BoneOverlayMode.Off,
    };

    /// <summary>
    /// Whether a bone gets a dot: <paramref name="selected"/> in the selection, <paramref name="placed"/> on the
    /// bone map, <paramref name="changed"/> by the selected layer.
    /// </summary>
    public static bool Dot(BoneOverlayMode mode, bool selected, bool placed, bool changed) => mode switch
    {
        BoneOverlayMode.Selected => selected,
        BoneOverlayMode.All => selected || placed || changed,
        _ => false,
    };

    /// <summary>The onion skins' frames: the last key before <paramref name="frame"/> and the first after it, null where there is none.</summary>
    public static (int? Before, int? After) GhostFrames(ImmutableArray<MotionKey> keys, int frame)
    {
        // shortcut: no wrap round a loop, the first key's ghost doesn't show before the last; wrap it if users ask.
        int? before = null, after = null;
        foreach (var key in keys)
        {
            if (key.Frame < frame && (before == null || key.Frame > before))
                before = key.Frame;
            else if (key.Frame > frame && (after == null || key.Frame < after))
                after = key.Frame;
        }
        return (before, after);
    }
}
