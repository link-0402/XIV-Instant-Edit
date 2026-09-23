using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui;

/// <summary>
/// Reusable controls shared by every window. Everything here draws with <see cref="Theme"/>
/// tokens, so a window never carries its own colours or pixel sizes.
/// </summary>
internal static class Widgets
{
    /// <summary> A square icon button. The id must be unique within the current id scope. </summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        if (tooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return clicked && enabled;
    }

    /// <summary> A rounded pill with tinted background, used for sources and states. </summary>
    public static void Badge(string text, Vector4 colour, string? tooltip = null)
    {
        var padding = new Vector2(Theme.Scaled(6), Theme.Scaled(2));
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var position = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(Theme.WithAlpha(colour, .18f)), Theme.Scaled(4));
        drawList.AddText(position + padding, ImGui.GetColorU32(colour), text);
        ImGui.Dummy(size);
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary> A labelled section start with an optional muted detail (a count, a path). </summary>
    public static void SectionHeader(string text, string? detail = null)
    {
        ImGui.TextColored(Theme.Label, text);
        if (detail is null)
            return;
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, detail);
    }

    /// <summary> Dalamud's (?) marker with a tooltip. </summary>
    public static void HelpTip(string text) => ImGuiComponents.HelpMarker(text);

    /// <summary> One explanatory line under a control. </summary>
    public static void Hint(string text) => ImGui.TextColored(Theme.Hint, text);

    /// <summary> An explanatory paragraph that wraps to the available width. </summary>
    public static void HintWrapped(string text)
    {
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Hint, text);
    }

    /// <summary> Muted text that wraps to the available width. </summary>
    public static void MutedWrapped(string text)
    {
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Muted, text);
    }

    /// <summary> "● Name: value" with the dot in the state colour. </summary>
    public static void StatusDot(string name, Vector4 colour, string value, string? tooltip = null)
    {
        ImGui.TextColored(colour, "●");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.TextColored(Theme.Muted, $"{name}: {value}");
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// <summary> Height a <see cref="Banner"/> needs for the text at the current width. </summary>
    public static float BannerHeight(string text)
    {
        var style = ImGui.GetStyle();
        var iconAndSpacingWidth = ImGui.CalcTextSize("⚠").X + Theme.Gap;
        var wrapWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - style.WindowPadding.X * 2 - iconAndSpacingWidth);
        var textHeight = ImGui.CalcTextSize(text, false, wrapWidth).Y;
        return Math.Max(ImGui.GetFrameHeightWithSpacing() * 2, textHeight + style.WindowPadding.Y * 2 + Theme.Scaled(4));
    }

    /// <summary> A bordered, tinted box with a severity glyph and wrapped text. </summary>
    public static void Banner(string id, FeedbackSeverity severity, string text, float? height = null)
    {
        var (accent, background, icon) = Theme.Severity(severity);
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, background).Push(ImGuiCol.Border, accent);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Theme.Scaled(4)).Push(ImGuiStyleVar.ChildBorderSize, 1f);
        using var child = ImRaii.Child(id, new Vector2(0, height ?? BannerHeight(text)), true);
        if (!child.Success)
            return;
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Text, text);
    }
}
