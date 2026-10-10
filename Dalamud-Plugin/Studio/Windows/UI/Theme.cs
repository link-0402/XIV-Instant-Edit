using System.Numerics;
using Dalamud.Interface.Utility;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// The one place colours and metrics live, so every window draws with the same
/// vocabulary. Sizes are authored at 100% and pass through <see cref="S"/>, which
/// applies Dalamud's global UI scale — hardcoded pixel widths clip at other scales.
/// </summary>
internal static class Theme
{
    // ── Colours ──────────────────────────────────────────────────────────────

    // Instant Edit's palette (Ui/Theme.cs), so the mod and build windows match the main window.
    public static readonly Vector4 Accent = InstantEdit.Ui.Theme.Accent;
    public static readonly Vector4 Muted  = InstantEdit.Ui.Theme.Muted;
    public static readonly Vector4 Faint  = InstantEdit.Ui.Theme.Hint;
    public static readonly Vector4 Good   = InstantEdit.Ui.Theme.Success;
    public static readonly Vector4 Warn   = InstantEdit.Ui.Theme.Warning;
    public static readonly Vector4 Bad    = InstantEdit.Ui.Theme.Error;

    /// <summary>Subtle fill behind the status bar and panel headers.</summary>
    public static readonly Vector4 Band   = new(1f, 1f, 1f, 0.04f);

    /// <summary>Row highlight for the object the inspector is showing.</summary>
    public static readonly Vector4 Focus  = Accent with { W = 0.22f };

    /// <summary>A tile at rest, hovered, and selected.</summary>
    public static readonly Vector4 Tile         = new(1f, 1f, 1f, 0.035f);
    public static readonly Vector4 TileHovered  = new(1f, 1f, 1f, 0.075f);
    public static readonly Vector4 TileSelected = Accent with { W = 0.20f };
    public static readonly Vector4 TileBorder   = new(1f, 1f, 1f, 0.07f);

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>Scales a 100%-authored pixel size by the user's global UI scale.</summary>
    public static float S(float px) => px * ImGuiHelpers.GlobalScale;

    /// <summary>The user's tile size setting, applied on top of the UI scale (set from the config each frame).</summary>
    public static float TileScale { get; set; } = 1f;

    /// <summary>Scales a tile dimension by the UI scale and the tile size setting.</summary>
    public static float T(float px) => S(px * TileScale);

    /// <summary>Width of the label gutter in label/field rows.</summary>
    public static float LabelWidth => S(112);

    /// <summary>Width of the item browser on first use.</summary>
    public const float DefaultBrowserWidth = 300;

    /// <summary>Width of the inspector on first use.</summary>
    public const float DefaultInspectorWidth = 460;

    /// <summary>Width of the contents pane on first use.</summary>
    public const float DefaultContentsWidth = 330;

    public const float MinPaneWidth = 180;

    /// <summary>Thickness of the draggable splitter between regions.</summary>
    public static float SplitterWidth => S(6);

    /// <summary>Corner rounding of tiles.</summary>
    public static float TileRounding => S(6);

    /// <summary>Side length of a texture thumbnail in the material inspector.</summary>
    public static float ThumbSize => S(40);
}
