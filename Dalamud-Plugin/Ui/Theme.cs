using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace InstantEdit.Ui;

/// <summary>
/// The plugin's design tokens: every colour and size the windows use. Colours are the
/// ones the original UI used as literals, merged into one meaning each; sizes go through
/// Dalamud's global scale so the UI follows the user's font scaling.
/// </summary>
internal static class Theme
{
    // Text
    public static readonly Vector4 Accent = new(.95f, .78f, .35f, 1);
    public static readonly Vector4 Label = new(.76f, .78f, .84f, 1);
    public static readonly Vector4 Text = new(.92f, .93f, .96f, 1);
    public static readonly Vector4 Muted = new(.62f, .65f, .72f, 1);
    public static readonly Vector4 Hint = new(.55f, .57f, .64f, 1);
    public static readonly Vector4 Inactive = new(.35f, .37f, .43f, 1);

    // Meaning
    public static readonly Vector4 Success = new(.35f, .85f, .55f, 1);
    public static readonly Vector4 Warning = new(1f, .78f, .2f, 1);
    public static readonly Vector4 Error = new(1f, .3f, .3f, 1);
    public static readonly Vector4 Info = new(.45f, .7f, .95f, 1);
    public static readonly Vector4 Highlight = new(1f, .82f, .35f, 1);
    public static readonly Vector4 Important = new(1f, .52f, .38f, 1);

    // Sources and states
    public static readonly Vector4 ModSource = new(.3f, .9f, .35f, 1);
    public static readonly Vector4 GameSource = new(.45f, .7f, .95f, 1);
    public static readonly Vector4 Online = new(.3f, .78f, .5f, 1);
    public static readonly Vector4 Mismatch = new(1f, .65f, .1f, 1);
    public static readonly Vector4 Offline = new(.9f, .45f, .32f, 1);
    public static readonly Vector4 Paused = new(.95f, .78f, .35f, 1);
    public static readonly Vector4 Watching = new(.65f, .83f, .7f, 1);
    public static readonly Vector4 Conflict = new(1f, .45f, .35f, 1);

    // Resource kinds and texture roles: row glyphs, faint row tints and the kind chips' swatches.
    // Normal maps take the lavender they look like; other textures stay neutral.
    public static readonly Vector4 ModelKind = new(.36f, .80f, .84f, 1);
    public static readonly Vector4 MaterialKind = new(.90f, .52f, .88f, 1);
    public static readonly Vector4 AnimationKind = new(.98f, .84f, .42f, 1);
    public static readonly Vector4 BaseTexture = new(1f, .66f, .40f, 1);
    public static readonly Vector4 NormalTexture = new(.58f, .62f, 1f, 1);
    public static readonly Vector4 MaskTexture = new(.76f, .88f, .40f, 1);
    public static readonly Vector4 IndexTexture = new(1f, .50f, .60f, 1);
    public static readonly Vector4 OtherTexture = new(.66f, .70f, .78f, 1);

    /// <summary> Opacity of a row's tint: enough to tell neighbouring rows apart, not to compete with their text. </summary>
    public const float KindTintAlpha = .10f;

    /// <summary> The resource tree's guide lines between a row and its children. </summary>
    public static readonly Vector4 TreeLine = new(.42f, .45f, .53f, 1);

    public static Vector4 TextureRoleColour(TextureRole role)
        => role switch
        {
            TextureRole.Base => BaseTexture,
            TextureRole.Normal => NormalTexture,
            TextureRole.Mask => MaskTexture,
            TextureRole.Index => IndexTexture,
            _ => OtherTexture,
        };

    // Surfaces
    public static readonly Vector4 PanelBg = new(.075f, .085f, .105f, 1);
    public static readonly Vector4 RowAlt = new(.11f, .12f, .15f, 1);
    public static readonly Vector4 Selection = new(.45f, .34f, .16f, 1);
    public static readonly Vector4 SuccessBg = new(.08f, .18f, .12f, 1);
    public static readonly Vector4 WarningBg = new(.22f, .16f, .04f, 1);
    public static readonly Vector4 ErrorBg = new(.24f, .055f, .055f, 1);
    public static readonly Vector4 InfoBg = new(.06f, .12f, .2f, 1);

    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float Scaled(float value) => value * ImGuiHelpers.GlobalScale;
    public static Vector2 Scaled(float x, float y) => ImGuiHelpers.ScaledVector2(x, y);

    /// <summary> Horizontal gap between related controls. </summary>
    public static float Gap => Scaled(6);

    /// <summary> Width of the small expand/collapse arrow button in the resource tree. </summary>
    public static float ArrowWidth => Scaled(22);

    /// <summary> Indent per tree level. </summary>
    public static float TreeIndent => Scaled(18);

    /// <summary> Square icon size that matches a text row. </summary>
    public static float IconSize => ImGui.GetFrameHeight();

    /// <summary> Thumbnail edge used by session cards and hover previews. </summary>
    public static float ThumbSize => Scaled(64);

    /// <summary> Accent, background and glyph for a banner or status line of the given severity. </summary>
    public static (Vector4 Accent, Vector4 Background, string Icon) Severity(FeedbackSeverity severity)
        => severity switch
        {
            FeedbackSeverity.Warning => (Warning, WarningBg, "⚠"),
            FeedbackSeverity.Error => (Error, ErrorBg, "✕"),
            FeedbackSeverity.Info => (Info, InfoBg, "i"),
            _ => (Success, SuccessBg, "✓"),
        };

    /// <summary> A colour with its alpha replaced, for tinted fills behind text. </summary>
    public static Vector4 WithAlpha(Vector4 colour, float alpha) => new(colour.X, colour.Y, colour.Z, alpha);
}
