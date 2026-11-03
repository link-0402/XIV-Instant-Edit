using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary> The dock's tabs, in their order: what plays and the game's animations, saved edits, and Blender with Record. </summary>
internal enum DockTab : byte { Animations, Saved, Blender }

/// <summary>
/// The Timeline window's layout, shared by every actor's workspace so switching actors keeps it: the dock's tab, width
/// and fold, the side panel's width, the curve strip and its height, and the loader's search and group. Kept for the
/// session only.
/// </summary>
internal sealed class WindowLayout
{
    /// <summary>The dock's width when nothing set it yet, before scaling.</summary>
    public const float DefaultDock = 230;

    /// <summary>The narrowest and widest the dock can be dragged, before scaling.</summary>
    public const float MinimumDock = 180, MaximumDock = 420;

    /// <summary>The dock's tab.</summary>
    public DockTab Tab { get; set; } = DockTab.Animations;

    /// <summary>The dock's width, scaled; 0 until the dock is first drawn.</summary>
    public float DockWidth { get; set; }

    /// <summary>The dock is folded to its strip of actors.</summary>
    public bool DockFolded { get; set; }

    /// <summary>The side panel's width, scaled; 0 until it is first drawn.</summary>
    public float SideWidth { get; set; }

    /// <summary>The curve strip shows under the lanes.</summary>
    public bool Curves { get; set; }

    /// <summary>The curve strip's height, scaled; 0 until it is first drawn.</summary>
    public float CurveHeight { get; set; }

    /// <summary>What the loader's search box holds; a field so the box can write it.</summary>
    public string LoaderSearch = string.Empty;

    /// <summary>The loader's group while nothing is searched.</summary>
    public AnimationLibraryGroup LoaderGroup { get; set; } = AnimationLibraryGroup.Emotes;

    /// <summary>
    /// The dock's width in a window <paramref name="window"/> wide at <paramref name="scale"/>: the width set (the default
    /// before one is), within its limits and leaving <paramref name="rest"/> for the timeline beside it.
    /// </summary>
    public static float Dock(float wanted, float window, float rest, float scale)
    {
        var low = MinimumDock * scale;
        var high = Math.Max(low, Math.Min(MaximumDock * scale, window - rest));
        return Math.Clamp(wanted > 0 ? wanted : DefaultDock * scale, low, high);
    }
}
