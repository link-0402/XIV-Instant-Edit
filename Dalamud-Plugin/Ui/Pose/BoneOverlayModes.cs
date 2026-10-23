namespace InstantEdit.Ui.Pose;

/// <summary>What the Bones window's crosshairs show on the GPose character.</summary>
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
    /// <summary>The mode a click on the switch goes to: off, selected, all and round again.</summary>
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
}
