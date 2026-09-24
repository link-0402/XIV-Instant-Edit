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

    /// <summary>
    /// A rounded pill with tinted background, used for sources and states. It occupies one
    /// frame height so it lines up with buttons in a row. Returns true while hovered so the
    /// caller can build a tooltip only when needed.
    /// </summary>
    public static bool Badge(string text, Vector4 colour)
    {
        var padding = new Vector2(Theme.Scaled(6), Theme.Scaled(2));
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var frame = ImGui.GetFrameHeight();
        var position = ImGui.GetCursorScreenPos() + new Vector2(0, Math.Max(0, (frame - size.Y) / 2));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(Theme.WithAlpha(colour, .18f)), Theme.Scaled(4));
        drawList.AddText(position + padding, ImGui.GetColorU32(colour), text);
        ImGui.Dummy(new Vector2(size.X, Math.Max(frame, size.Y)));
        return ImGui.IsItemHovered();
    }

    /// <summary> A glyph from the icon font, aligned with framed controls on the same line. </summary>
    public static void Icon(FontAwesomeIcon icon, Vector4 colour)
    {
        ImGui.AlignTextToFramePadding();
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(colour, icon.ToIconString());
    }

    /// <summary> An icon button without a frame; highlights on hover. </summary>
    public static bool GhostIconButton(string id, FontAwesomeIcon icon, string? tooltip = null)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGuiComponents.IconButton(id, icon);
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary> A filter chip with a count; filled when selected. Returns true when clicked. </summary>
    public static bool Chip(string label, int count, bool selected)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, selected)
            .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), selected);
        return ImGui.SmallButton($"{label}  {count}##chip-{label}");
    }

    /// <summary> A search field that fills the row, with a clear button when it has text and Ctrl+F focus. </summary>
    public static bool SearchBox(string id, ref string text, string hint)
    {
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F) && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            ImGui.SetKeyboardFocusHere();
        var clearWidth = text.Length > 0 ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(80), ImGui.GetContentRegionAvail().X - clearWidth));
        var changed = ImGui.InputTextWithHint(id, hint, ref text, 256);
        if (text.Length == 0)
            return changed;
        ImGui.SameLine();
        if (GhostIconButton(id + "-clear", FontAwesomeIcon.Times, "Clear the search"))
        {
            text = string.Empty;
            changed = true;
        }
        return changed;
    }

    /// <summary> A path in the monospace font; hovering shows the full path and a click copies it. </summary>
    public static void PathText(string display, string fullPath)
    {
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushFont(UiBuilder.MonoFont))
            ImGui.TextUnformatted(display);
        if (!ImGui.IsItemHovered())
            return;
        if (fullPath.Length == 0)
        {
            ImGui.SetTooltip("No resolved path");
            return;
        }
        ImGui.SetTooltip($"{fullPath}\nClick to copy");
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(fullPath);
    }

    /// <summary> A vertical drag handle between two panes. Adjusts <paramref name="size"/> while dragged. </summary>
    public static void Splitter(string id, ref float size, float min, float max, float height)
    {
        ImGui.InvisibleButton(id, new Vector2(Theme.Scaled(6), Math.Max(1, height)));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (active)
            size = Math.Clamp(size + ImGui.GetIO().MouseDelta.X, min, Math.Max(min, max));
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        var colour = active ? Theme.Accent : hovered ? Theme.Muted : Theme.WithAlpha(Theme.Muted, .25f);
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(rectMin.X + Theme.Scaled(2), rectMin.Y),
            new Vector2(rectMax.X - Theme.Scaled(2), rectMax.Y),
            ImGui.GetColorU32(colour));
    }

    /// <summary> A centred placeholder for empty lists: an icon, a title and an optional hint. </summary>
    public static void EmptyState(FontAwesomeIcon icon, string title, string? hint = null)
    {
        ImGui.Dummy(new Vector2(0, Theme.Scaled(28)));
        var iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            CentreCursor(ImGui.CalcTextSize(iconText).X);
            ImGui.TextColored(Theme.Inactive, iconText);
        }
        ImGui.Spacing();
        CentreCursor(ImGui.CalcTextSize(title).X);
        ImGui.TextColored(Theme.Label, title);
        if (hint is null)
            return;
        var available = ImGui.GetContentRegionAvail().X;
        var width = Math.Min(available, Theme.Scaled(440));
        var hintWidth = Math.Min(width, ImGui.CalcTextSize(hint).X);
        CentreCursor(hintWidth);
        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(Theme.Hint, hint);
    }

    private static void CentreCursor(float width)
    {
        var available = ImGui.GetContentRegionAvail().X;
        if (available > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available - width) / 2);
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

    /// <summary> Width a <see cref="StatusDot"/> with this text occupies, for right-aligned layouts. </summary>
    public static float StatusDotWidth(string name, string value)
        => ImGui.CalcTextSize("●").X + Theme.Scaled(3) + ImGui.CalcTextSize($"{name}: {value}").X;

    /// <summary> A toolbar tab: icon + label, filled when active. Returns true when clicked. </summary>
    public static bool TabButton(FontAwesomeIcon icon, string label, bool active)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, active)
            .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), active);
        return ImGuiComponents.IconButtonWithText(icon, label, null, null, null, null);
    }

    /// <summary> A small rotating arc that signals background work. Occupies one frame-height square. </summary>
    public static void Spinner()
    {
        var size = new Vector2(ImGui.GetFrameHeight());
        var position = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var centre = position + size / 2;
        var radius = size.X * .32f;
        var start = (float)(ImGui.GetTime() * 5.0 % (Math.PI * 2));
        var drawList = ImGui.GetWindowDrawList();
        drawList.PathArcTo(centre, radius, start, start + MathF.PI * 1.5f, 16);
        drawList.PathStroke(ImGui.GetColorU32(Theme.Accent), ImDrawFlags.None, Theme.Scaled(2));
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
