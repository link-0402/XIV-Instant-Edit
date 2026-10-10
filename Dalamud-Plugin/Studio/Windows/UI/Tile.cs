using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>A small icon button on the right edge of a tile.</summary>
internal sealed record TileAction(FontAwesomeIcon Icon, string Tooltip, Action OnClick, Vector4? Color = null, bool Enabled = true);

/// <summary>Everything one tile shows. Built per visible tile per frame.</summary>
internal sealed class TileSpec
{
    public required string Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public Vector4? TitleColor { get; init; }
    public string? Subtitle { get; init; }
    public Vector4? SubtitleColor { get; init; }

    /// <summary>A third, fainter line (a game path, a source file).</summary>
    public string? Detail { get; init; }
    public Vector4? DetailColor { get; init; }

    public FontAwesomeIcon Icon { get; init; } = FontAwesomeIcon.File;
    public Vector4 IconColor { get; init; } = Theme.Muted;

    /// <summary>Draws a thumbnail of the given side at the cursor; the icon is shown when null.</summary>
    public Action<float>? DrawThumb { get; init; }

    public IReadOnlyList<Badge>? Badges { get; init; }
    public IReadOnlyList<TileAction>? Actions { get; init; }

    public Severity? Issue { get; init; }
    public string? IssueText { get; init; }

    public bool Selected { get; init; }
    public bool Dimmed { get; init; }

    /// <summary>Header style: the larger heading font.</summary>
    public bool Large { get; init; }

    /// <summary>Left indent of the tile within its row (tree depth).</summary>
    public float Indent { get; init; }

    /// <summary>A fold arrow: null for none, else whether the node is open.</summary>
    public bool? Expanded { get; init; }

    /// <summary>Files dropped onto the tile from Explorer or the Sims importer.</summary>
    public FileDropRule? Drop { get; init; }

    /// <summary>Called right after the tile's button, while it is still the last item: a drag source goes here.</summary>
    public Action? AfterButton { get; init; }

    public string? Tooltip { get; init; }
}

/// <summary>What happened to a tile this frame.</summary>
internal struct TileResult
{
    public bool Clicked;
    public bool DoubleClicked;
    public bool RightClicked;
    public bool Toggled;
    public bool Hovered;
    public DroppedPaths? Dropped;
}

/// <summary>
/// A tile: a rounded card with a thumbnail or icon, a title, up to two lines under it, badges, an
/// issue marker and action buttons. It is a single invisible button underneath — so clicks, hover,
/// drag-and-drop of files and of gear all work on the whole card — with the content drawn over it,
/// and its action buttons (which win the mouse, since the button allows overlap) on the right.
/// </summary>
internal static class Tile
{
    /// <summary>The height of a tile with a subtitle (and a detail line when <paramref name="detail"/>).</summary>
    public static float Height(bool detail = false, bool large = false)
        => large ? Theme.T(92) : detail ? Theme.T(66) : Theme.T(52);

    public static TileResult Draw(TileSpec spec, Vector2 size)
    {
        var result = new TileResult();
        var start = ImGui.GetCursorScreenPos();
        var min = start + new Vector2(spec.Indent, 0);
        var max = start + size;
        var tileSize = max - min;
        var draw = ImGui.GetWindowDrawList();
        float pad = Theme.S(8);
        float rounding = Theme.TileRounding;

        ImGui.PushID(spec.Id);

        // ── The card itself: one item for clicks, hover and drops ──────────
        ImGui.SetCursorScreenPos(min);
        result.Clicked = ImGui.InvisibleButton("##tile", Vector2.Max(tileSize, Vector2.One));
        result.Hovered = ImGui.IsItemHovered();
        result.DoubleClicked = result.Hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        result.RightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        ImGui.SetItemAllowOverlap();
        spec.AfterButton?.Invoke();

        if (spec.Drop != null && FileDrop.Target(spec.Drop.Reject, out var dropped))
            result.Dropped = dropped;

        var bg = spec.Selected ? Theme.TileSelected : result.Hovered ? Theme.TileHovered : Theme.Tile;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(bg), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(spec.Selected ? Theme.Accent with { W = 0.55f } : Theme.TileBorder), rounding, ImDrawFlags.None,
            spec.Selected ? Theme.S(1.5f) : 1f);

        float x = min.X + pad;

        // ── Fold arrow ───────────────────────────────────────────────────────
        if (spec.Expanded is { } open)
        {
            float arrowW = Theme.S(16);
            ImGui.SetCursorScreenPos(new Vector2(x - Theme.S(4), min.Y));
            if (ImGui.InvisibleButton("##fold", new Vector2(arrowW + Theme.S(4), tileSize.Y)))
                result.Toggled = true;
            bool arrowHot = ImGui.IsItemHovered();
            DrawIcon(draw, open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight,
                new Vector2(x, min.Y), new Vector2(arrowW, tileSize.Y), arrowHot ? Theme.Accent : Theme.Muted);
            x += arrowW;
        }

        // ── Thumbnail or icon ───────────────────────────────────────────────
        float thumbSide = tileSize.Y - pad * 2;
        if (spec.DrawThumb != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, min.Y + pad));
            spec.DrawThumb(thumbSide);
        }
        else
        {
            draw.AddRectFilled(new Vector2(x, min.Y + pad), new Vector2(x + thumbSide, min.Y + pad + thumbSide),
                ImGui.GetColorU32(Theme.Band), Theme.S(4));
            DrawIcon(draw, spec.Icon, new Vector2(x, min.Y + pad), new Vector2(thumbSide), spec.Dimmed ? Theme.Faint : spec.IconColor,
                thumbSide * 0.45f);
        }
        x += thumbSide + pad;

        // ── Actions and the issue marker on the right ───────────────────────
        float right = max.X - pad;
        if (spec.Actions is { Count: > 0 } actions)
        {
            float button = ImGui.GetFrameHeight();
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var action = actions[i];
                right -= button;
                ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (tileSize.Y - button) * 0.5f));
                using (Gui.Disabled(!action.Enabled))
                {
                    if (action.Color is { } c) ImGui.PushStyleColor(ImGuiCol.Text, c);
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButton($"##act{i}", action.Icon);
                    ImGui.PopStyleColor(action.Color != null ? 2 : 1);
                    Gui.Tooltip(action.Tooltip);
                    if (clicked)
                        action.OnClick();
                }
                right -= Theme.S(2);
            }
            right -= Theme.S(4);
        }

        if (spec.Issue is { } severity)
        {
            var glyphSize = IconSize(Panels.IssueList.IconFor(severity));
            var pos = new Vector2(right - glyphSize.X, min.Y + pad);
            DrawIcon(draw, Panels.IssueList.IconFor(severity), pos, glyphSize, Gui.SeverityColor(severity));
            if (spec.IssueText != null && ImGui.IsMouseHoveringRect(pos, pos + glyphSize))
                ImGui.SetTooltip(spec.IssueText);
            right -= glyphSize.X + Theme.S(6);
        }

        // ── Text ─────────────────────────────────────────────────────────────
        float textWidth = Math.Max(0, right - x);
        var titleColor = spec.Dimmed ? Theme.Faint : spec.TitleColor ?? new Vector4(1, 1, 1, 0.95f);
        float y = min.Y + pad - Theme.S(1);
        float badgesW = BadgesWidth(spec.Badges, textWidth * 0.55f);   // measured in the body font they are drawn in
        float lineH, titleEnd;
        using (spec.Large ? Fonts.Heading() : Fonts.Title())
        {
            lineH = ImGui.GetTextLineHeight();
            var title = ResourceStyle.Fit(spec.Title, textWidth - badgesW - (badgesW > 0 ? Theme.S(6) : 0));
            draw.AddText(new Vector2(x, y), ImGui.GetColorU32(titleColor), title);
            titleEnd = x + ImGui.CalcTextSize(title).X;
        }
        if (spec.Tooltip != null && ImGui.IsMouseHoveringRect(new Vector2(x, y), new Vector2(titleEnd, y + lineH)))
            ImGui.SetTooltip(spec.Tooltip);
        if (badgesW > 0)
        {
            // Badges sit after the title, vertically centred on its line.
            float bx = titleEnd + Theme.S(6);
            float by = y + (lineH - ImGui.GetTextLineHeight()) * 0.5f;
            DrawBadges(draw, spec.Badges!, new Vector2(bx, by), x + textWidth - bx);
        }
        y += lineH + Theme.S(2);

        if (!string.IsNullOrEmpty(spec.Subtitle))
        {
            var sub = ResourceStyle.Fit(spec.Subtitle!, textWidth);
            draw.AddText(new Vector2(x, y), ImGui.GetColorU32(spec.Dimmed ? Theme.Faint : spec.SubtitleColor ?? Theme.Muted), sub);
            y += ImGui.GetTextLineHeight() + Theme.S(1);
        }

        if (!string.IsNullOrEmpty(spec.Detail) && y + ImGui.GetTextLineHeight() <= max.Y)
        {
            var detail = ResourceStyle.FitPath(spec.Detail!, textWidth);
            draw.AddText(new Vector2(x, y), ImGui.GetColorU32(spec.DetailColor ?? Theme.Faint), detail);
        }

        // Leave the cursor below the tile, as if it were one item of its size.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(size);
        ImGui.PopID();
        return result;
    }

    private static float BadgesWidth(IReadOnlyList<Badge>? badges, float limit)
    {
        if (badges == null || badges.Count == 0)
            return 0;
        float w = 0;
        foreach (var badge in badges)
        {
            float bw = ResourceStyle.BadgeWidth(badge) + Theme.S(4);
            if (w + bw > limit)
                break;
            w += bw;
        }
        return w;
    }

    private static void DrawBadges(ImDrawListPtr draw, IReadOnlyList<Badge> badges, Vector2 pos, float width)
    {
        float x = pos.X;
        float limit = pos.X + width;
        foreach (var badge in badges)
        {
            float bw = ResourceStyle.BadgeWidth(badge);
            if (x + bw > limit)
                break;
            ResourceStyle.DrawBadge(draw, new Vector2(x, pos.Y), badge);
            if (badge.Tooltip != null && ImGui.IsMouseHoveringRect(new Vector2(x, pos.Y), new Vector2(x + bw, pos.Y + ImGui.GetTextLineHeight())))
                ImGui.SetTooltip(badge.Tooltip);
            x += bw + Theme.S(4);
        }
    }

    private static Vector2 IconSize(FontAwesomeIcon icon)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var size = ImGui.CalcTextSize(icon.ToIconString());
        ImGui.PopFont();
        return size;
    }

    /// <summary>Draws a FontAwesome glyph centred in a box, without adding an item; at <paramref name="fontSize"/> pixels when given.</summary>
    public static void DrawIcon(ImDrawListPtr draw, FontAwesomeIcon icon, Vector2 pos, Vector2 box, Vector4 color, float? fontSize = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var size = ImGui.CalcTextSize(glyph);
        if (fontSize is { } wanted && wanted > 0)
        {
            float baseSize = ImGui.GetFontSize();
            var scaled = size * (wanted / baseSize);
            draw.AddText(UiBuilder.IconFont, wanted, pos + (box - scaled) * 0.5f, ImGui.GetColorU32(color), glyph);
        }
        else
        {
            draw.AddText(pos + (box - size) * 0.5f, ImGui.GetColorU32(color), glyph);
        }
        ImGui.PopFont();
    }
}

/// <summary>
/// A dashed box inviting a drop: "drop a texture here, or click to browse". Takes files from
/// Explorer and the Sims importer (and, where asked, gear from the item browser).
/// </summary>
internal static class DropZone
{
    public static TileResult Draw(string id, string hint, FontAwesomeIcon icon, Vector2 size, FileDropRule? drop, float indent = 0)
    {
        var result = new TileResult();
        var start = ImGui.GetCursorScreenPos();
        var min = start + new Vector2(indent, 0);
        var max = start + size;
        var draw = ImGui.GetWindowDrawList();

        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(min);
        result.Clicked = ImGui.InvisibleButton("##zone", Vector2.Max(max - min, Vector2.One));
        result.Hovered = ImGui.IsItemHovered();
        if (drop != null && FileDrop.Target(drop.Reject, out var dropped))
            result.Dropped = dropped;

        var color = result.Hovered ? Theme.Accent : Theme.Faint;
        if (result.Hovered)
            draw.AddRectFilled(min, max, ImGui.GetColorU32(Theme.Accent with { W = 0.06f }), Theme.TileRounding);
        DashedRect(draw, min, max, ImGui.GetColorU32(color with { W = 0.8f }));

        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var glyphSize = ImGui.CalcTextSize(glyph);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(hint);
        float total = glyphSize.X + Theme.S(8) + textSize.X;
        var origin = new Vector2(min.X + Math.Max(Theme.S(10), ((max.X - min.X) - total) * 0.5f), min.Y + ((max.Y - min.Y) - textSize.Y) * 0.5f);
        ImGui.PushFont(UiBuilder.IconFont);
        draw.AddText(origin + new Vector2(0, (textSize.Y - glyphSize.Y) * 0.5f), ImGui.GetColorU32(color), glyph);
        ImGui.PopFont();
        draw.AddText(origin + new Vector2(glyphSize.X + Theme.S(8), 0), ImGui.GetColorU32(color),
            ResourceStyle.Fit(hint, max.X - origin.X - glyphSize.X - Theme.S(18)));

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(size);
        ImGui.PopID();
        return result;
    }

    private static void DashedRect(ImDrawListPtr draw, Vector2 min, Vector2 max, uint color)
    {
        float dash = Theme.S(6), gap = Theme.S(4);
        void Line(Vector2 a, Vector2 b)
        {
            var dir = b - a;
            float length = dir.Length();
            if (length <= 0) return;
            dir /= length;
            for (float t = 0; t < length; t += dash + gap)
                draw.AddLine(a + dir * t, a + dir * MathF.Min(t + dash, length), color, 1f);
        }
        Line(min, new Vector2(max.X, min.Y));
        Line(new Vector2(max.X, min.Y), max);
        Line(max, new Vector2(min.X, max.Y));
        Line(new Vector2(min.X, max.Y), min);
    }
}
