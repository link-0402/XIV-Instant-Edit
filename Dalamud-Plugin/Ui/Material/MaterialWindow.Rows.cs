using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Materials;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Previews;
using InstantEdit.Studio.Models;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The Colour rows section: the 16 row pairs the index texture picks from, as swatches; the
/// selected pair's two rows side by side with named fields; copy and paste between rows; and the
/// old 16-row tables with a way to convert them.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const int PairCount = 16;
    private const string PastePopup = "##row-paste";

    private int selectedPair;
    // Shared, so a row copied in one Material window pastes in another.
    private static DtColorRow? rowClipboard;

    private enum PasteKind
    {
        All,
        Colours,
        Surface,
    }

    /// <summary>Whether the main column offers the colour rows: the material has a table and its shader reads one.</summary>
    private static bool ShowsRows(MaterialDocument doc)
        => doc.Editable && doc.Table != null && (ShaderPackages.Get(doc.ShaderPackage)?.SamplesColorTable ?? true);

    private void DrawRows(MaterialSession current, MaterialDocument doc)
    {
        var table = doc.Table!;
        switch (table.Kind)
        {
            case ColorTableKind.Dawntrail:
                DrawDyeBar(current, doc, table);
                var swatchRow = DrawPairs(ShownTable(table));
                ImGui.Spacing();
                var editorRow = DrawPairEditors(current, doc, table);
                // After the editors, so a paste under the mouse pulses the pasted row.
                HighlightRow(current.Current, swatchRow >= 0 ? swatchRow : editorRow);
                break;
            case ColorTableKind.Legacy:
                DrawLegacyRows(current, doc, table);
                break;
            default:
                Widgets.EmptyState(FontAwesomeIcon.Table, "Unknown table", "This colour table has a layout the editor doesn't know. It is kept as it is.");
                break;
        }
    }

    // ---- Pairs ----

    /// <summary>The pair swatches, each half a row; returns the hovered row, or -1.</summary>
    private int DrawPairs(ColorTable table)
    {
        var hoveredRow = -1;
        var spacing = Theme.Scaled(4);
        var width = (ImGui.GetContentRegionAvail().X - spacing * 7) / 8;
        var height = ImGui.GetFrameHeight() * 1.6f;
        var draw = ImGui.GetWindowDrawList();
        for (var pair = 0; pair < PairCount; pair++)
        {
            if (pair % 8 != 0)
                ImGui.SameLine(0, spacing);
            var position = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##pair-{pair}", new Vector2(width, height)))
                selectedPair = pair;
            var hovered = ImGui.IsItemHovered();
            var a = table.Rows[pair * 2];
            var b = table.Rows[pair * 2 + 1];
            var half = height / 2;
            // Each half is its own row: the top half row 2p, the bottom half row 2p + 1.
            var bottom = hovered && ImGui.GetMousePos().Y >= position.Y + half;
            if (hovered)
                hoveredRow = pair * 2 + (bottom ? 1 : 0);
            DrawSwatches(draw, position, new Vector2(width, half), a);
            DrawSwatches(draw, position + new Vector2(0, half), new Vector2(width, half), b);
            // A line between the rows, so a pair of matching rows still reads as two.
            draw.AddLine(position + new Vector2(0, half), position + new Vector2(width, half), ImGui.GetColorU32(new Vector4(0, 0, 0, .6f)), Theme.Scaled(1));
            // Pairs the index texture never selects are drawn at 40%, and so is a used pair's row it never picks.
            var dim = ImGui.GetColorU32(Theme.WithAlpha(Theme.PanelBg, 0.6f));
            if (usage != null && !usage.PairUsed(pair))
                draw.AddRectFilled(position, position + new Vector2(width, height), dim);
            else if (usage != null)
                for (var side = 0; side < 2; side++)
                    if (!RowUsed(pair * 2 + side))
                        draw.AddRectFilled(position + new Vector2(0, half * side), position + new Vector2(width, half * (side + 1)), dim);
            var outline = pair == selectedPair ? Theme.Accent : hovered ? Theme.Label : Theme.WithAlpha(Theme.Muted, .5f);
            draw.AddRect(position, position + new Vector2(width, height), ImGui.GetColorU32(outline), Theme.Scaled(3), ImDrawFlags.None, pair == selectedPair ? 2 : 1);
            if (hovered)
            {
                var top = position + new Vector2(0, bottom ? half : 0);
                draw.AddRect(top, top + new Vector2(width, half), ImGui.GetColorU32(Theme.Text), Theme.Scaled(2), ImDrawFlags.None, 2);
            }
            var number = (pair + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = ImGui.CalcTextSize(number);
            draw.AddRectFilled(position + new Vector2(2, 2), position + new Vector2(text.X + 6, text.Y + 2), ImGui.GetColorU32(new Vector4(0, 0, 0, .55f)), Theme.Scaled(2));
            draw.AddText(position + new Vector2(4, 2), ImGui.GetColorU32(Theme.Text), number);
            if (hovered)
                RowTooltip(pair, hoveredRow);
        }
        return hoveredRow;
    }

    /// <summary>The hovered half's row: which it is, how much of its pair it gets, and how it shows on the character.</summary>
    private void RowTooltip(int pair, int row)
    {
        var isTop = row % 2 == 0;
        var other = isTop ? row + 1 : row - 1;
        ImGui.SetTooltip($"Row {row + 1}, the {(isTop ? "top" : "bottom")} of pair {pair + 1}.\n{RowUsageText(pair, row)}\n" +
                         $"It pulses {(isTop ? "yellow" : "blue")} on the character. The {(isTop ? "bottom" : "top")} half is row {other + 1}.\nBase, shine and glow colours.");
    }

    private bool RowUsed(int row) => usage is not { RowUsed.Length: ColorTable.DtRowCount } read || read.RowUsed[row];

    private string RowUsageText(int pair, int row)
    {
        if (usage is not { RowShare.Length: ColorTable.DtRowCount } read || !read.PairUsed(pair))
            return UsageText(pair);
        if (!RowUsed(row))
            return $"{UsageText(pair)} The index never picks this row.";
        var part = read.PairShare(pair) > 0 ? read.RowShare[row] / read.PairShare(pair) * 100 : 0;
        return $"{UsageText(pair)} This row gets {part:0}% of it.";
    }

    private static void DrawSwatches(ImDrawListPtr draw, Vector2 position, Vector2 size, DtColorRow row)
    {
        var third = size.X / 3;
        Vector4 Shown(Vector3 colour) => new(Math.Clamp(colour.X, 0, 1), Math.Clamp(colour.Y, 0, 1), Math.Clamp(colour.Z, 0, 1), 1);
        draw.AddRectFilled(position, position + new Vector2(third, size.Y), ImGui.GetColorU32(Shown(row.Diffuse)));
        draw.AddRectFilled(position + new Vector2(third, 0), position + new Vector2(third * 2, size.Y), ImGui.GetColorU32(Shown(row.Specular)));
        draw.AddRectFilled(position + new Vector2(third * 2, 0), position + size, ImGui.GetColorU32(Shown(row.Emissive * row.Exposure)));
    }

    // ---- The selected pair ----

    /// <summary>The selected pair's two rows; returns the row whose name or copy/paste button is hovered, or -1.</summary>
    private int DrawPairEditors(MaterialSession current, MaterialDocument doc, ColorTable table)
    {
        var hoveredRow = -1;
        selectedPair = Math.Clamp(selectedPair, 0, PairCount - 1);
        if (usage != null)
        {
            ImGui.TextColored(usage.PairUsed(selectedPair) ? Theme.Muted : Theme.Warning, $"Pair {selectedPair + 1}: {UsageText(selectedPair)}");
            ImGui.SameLine(0, Theme.Gap * 2);
            DrawWhere();
        }
        else
            ImGui.TextColored(Theme.Hint, usageNote + " Every pair may be in use.");
        using var editors = ImRaii.Table("##pair-editors", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV);
        if (!editors.Success)
            return hoveredRow;
        ImGui.TableNextRow();
        for (var side = 0; side < 2; side++)
        {
            ImGui.TableNextColumn();
            var row = selectedPair * 2 + side;
            using var id = ImRaii.PushId($"row-{row}");
            if (DrawRowEditor(current, doc, table, row))
                hoveredRow = row;
        }
        return hoveredRow;
    }

    /// <summary>One row's fields; true when its name or a copy/paste button is hovered (the row pulses on the character).</summary>
    private bool DrawRowEditor(MaterialSession current, MaterialDocument doc, ColorTable table, int row)
    {
        var values = table.Rows[row];
        var name = $"Row {row + 1}";
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, name);
        var pulse = ImGui.IsItemHovered();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, row % 2 == 0 ? "(top, index green 255)" : "(bottom, index green 0)");
        pulse |= ImGui.IsItemHovered();
        pulse |= DrawRowActions(current, table, row, values, name);

        Section("Colour");
        RowColour(current, row, "Base colour", values.Diffuse, colour => values with { Diffuse = colour }, name);
        RowColour(current, row, "Shine colour", values.Specular, colour => values with { Specular = colour }, name);
        RowColour(current, row, "Glow colour", values.Emissive, colour => values with { Emissive = colour }, name);
        var stops = values.Exposure <= 0 ? -4f : RowMath.ExposureToStops(values.Exposure);
        if (RowLabel("Glow strength", "How bright the glow is, in stops. Each stop is 4 times brighter."))
        {
            var changed = ImGui.SliderFloat("##exposure", ref stops, -4f, 4f, "%.2f stops");
            Track(current, changed, d => EditRow(d, row, r => r with { Exposure = RowMath.StopsToExposure(stops) }), $"{name} glow strength");
        }

        Section("Surface");
        RowPercent(current, row, "Roughness", "Higher is more matte.", values.Roughness, v => values with { Roughness = v }, name);
        RowPercent(current, row, "Metalness", "How much it reflects like metal.", values.Metalness, v => values with { Metalness = v }, name);
        RowSlider(current, row, "Anisotropy", "Stretches highlights along the surface.", values.Anisotropy, -1f, 1f, v => values with { Anisotropy = v }, name);
        RowPercent(current, row, "Sheen", "Soft light at glancing angles.", values.SheenRate, v => values with { SheenRate = v }, name);
        RowPercent(current, row, "Sheen tint", "How much the sheen takes the base colour.", values.SheenTint, v => values with { SheenTint = v }, name);
        RowSlider(current, row, "Sheen spread", "Lower spreads the sheen wider.", values.SheenAperture, 0f, 20f, v => values with { SheenAperture = v }, name);

        Section("Pattern");
        if (RowLabel("Detail pattern", "Which detail pattern the row uses (0 to 63)."))
        {
            var tile = values.TileIndex;
            // One pick is one undo step, so no gesture tracking: the last item may be a list entry in the popup.
            if (SlicePicker("##tile", ref tile, 63, TextureArraySlices.ArrayKind.PatternColour, TextureArraySlices.ArrayKind.PatternNormal))
                _ = current.Apply(d => EditRow(d, row, r => r with { TileIndex = tile }), $"{name} detail pattern");
        }
        RowPercent(current, row, "Detail strength", "How strongly the pattern shows.", values.TileAlpha, v => values with { TileAlpha = v }, name);
        var (scaleU, scaleV, rotation, shear) = TileTransform.Decompose(values.TileUU, values.TileUV, values.TileVU, values.TileVV);
        // Mods store scales well past 64 (250 is common), so the drags must not clamp them on a touch.
        RowTransform(current, row, "Detail scale across", "How often the pattern repeats across.", scaleU, -1024f, 1024f,
            v => Compose(values, v, scaleV, rotation, shear), name);
        RowTransform(current, row, "Detail scale down", "How often the pattern repeats down.", scaleV, -1024f, 1024f,
            v => Compose(values, scaleU, v, rotation, shear), name);
        RowTransform(current, row, "Detail rotation", "Turns the pattern, in degrees.", rotation, -180f, 180f,
            v => Compose(values, scaleU, scaleV, v, shear), name);
        RowTransform(current, row, "Detail shear", "Slants the pattern, in degrees.", shear, -80f, 80f,
            v => Compose(values, scaleU, scaleV, rotation, v), name);

        Section("Reflection");
        if (RowLabel("Reflection map", "Which reflection texture the row uses."))
        {
            var sphere = values.SphereMapIndex;
            if (SlicePicker("##sphere", ref sphere, 127, TextureArraySlices.ArrayKind.Reflection))
                _ = current.Apply(d => EditRow(d, row, r => r with { SphereMapIndex = sphere }), $"{name} reflection map");
        }
        RowPercent(current, row, "Reflection strength", "How much of the reflection map shows.", values.SphereMapMask, v => values with { SphereMapMask = v }, name);
        if (doc.ShaderPackage.Equals("characterscroll.shpk", StringComparison.OrdinalIgnoreCase) && RowLabel("Scroll layer", "Which scrolling layer the row shows: 0 none, 1 or 2."))
        {
            var layer = (int)MathF.Round(values.ScrollLayer);
            var changed = ImGui.SliderInt("##scroll", ref layer, 0, 2);
            Track(current, changed, d => EditRow(d, row, r => r with { ScrollLayer = layer }), $"{name} scroll layer");
        }

        if (table.Dye != null)
        {
            Section("Dye");
            DrawRowDye(current, table, row, name);
        }

        if (ImGui.TreeNode("Advanced##row-advanced"))
        {
            if (RowLabel("Lighting style", "Which lighting the row uses."))
            {
                var style = values.ShaderId;
                var changed = ImGui.InputInt("##style", ref style, 1, 1);
                style = Math.Clamp(style, 0, 31);
                Track(current, changed, d => EditRow(d, row, r => r with { ShaderId = style }), $"{name} lighting style");
            }
            if (RowLabel("Detail matrix", "The pattern transform as stored: UU, UV, VU, VV."))
            {
                var matrix = new Vector4(values.TileUU, values.TileUV, values.TileVU, values.TileVV);
                var changed = ImGui.DragFloat4("##matrix", ref matrix, 0.05f, 0, 0, "%.2f");
                Track(current, changed, d => EditRow(d, row, r => r with { TileUU = matrix.X, TileUV = matrix.Y, TileVU = matrix.Z, TileVV = matrix.W }),
                    $"{name} detail matrix");
            }
            RawValue(current, row, 3, values.Scalar3, v => values with { Scalar3 = v }, name);
            RawValue(current, row, 7, values.Scalar7, v => values with { Scalar7 = v }, name);
            RawValue(current, row, 15, values.Scalar15, v => values with { Scalar15 = v }, name);
            RawValue(current, row, 17, values.Scalar17, v => values with { Scalar17 = v }, name);
            RawValue(current, row, 20, values.Scalar20, v => values with { Scalar20 = v }, name);
            RawValue(current, row, 22, values.Scalar22, v => values with { Scalar22 = v }, name);
            ImGui.TreePop();
        }
        return pulse;
    }

    /// <summary>Copy, paste and discard for one row; true while one of them is hovered or the paste menu is open.</summary>
    private bool DrawRowActions(MaterialSession current, ColorTable table, int row, DtColorRow values, string name)
    {
        var buttons = ImGui.GetFrameHeight() * 3 + ImGui.GetStyle().ItemSpacing.X * 2;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttons));
        if (Widgets.GhostIconButton("##copy", FontAwesomeIcon.Copy, "Copy this row"))
        {
            rowClipboard = values;
            Say(FeedbackSeverity.Info, $"Copied {name}.");
        }
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine();
        using (ImRaii.Disabled(rowClipboard == null))
        {
            if (Widgets.GhostIconButton("##paste", FontAwesomeIcon.Paste, "Paste onto this row"))
                ImGui.OpenPopup(PastePopup);
        }
        hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.SameLine();
        // The row as last saved; nothing to go back to when the table was added or converted since.
        var saved = current.History.Saved.Table is { Kind: ColorTableKind.Dawntrail } before ? before : null;
        var savedDye = saved?.Dye?[row];
        var edited = saved != null && (saved.Rows[row] != values || (savedDye is { } dye && table.Dye?[row] is { } now && now != dye));
        using (ImRaii.Disabled(!edited))
        {
            if (Widgets.GhostIconButton("##reset-row", FontAwesomeIcon.UndoAlt, "Discard the changes to this row (hold Ctrl)") && ImGui.GetIO().KeyCtrl && saved != null)
            {
                var savedRow = saved.Rows[row];
                _ = current.Apply(d => DiscardRow(d, row, savedRow, savedDye), $"Discard changes to {name}");
            }
        }
        hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        using var popup = ImRaii.Popup(PastePopup);
        if (!popup.Success || rowClipboard is not { } copied)
            return hovered;
        foreach (var (kind, label) in new[] { (PasteKind.All, "Everything"), (PasteKind.Colours, "Colours only"), (PasteKind.Surface, "Surface only") })
            if (ImGui.MenuItem(label))
                _ = current.Apply(d => EditRow(d, row, r => Paste(r, copied, kind)), $"Paste onto {name}");
        return true;
    }

    private static DtColorRow Paste(DtColorRow target, DtColorRow copied, PasteKind kind) => kind switch
    {
        PasteKind.Colours => target with { Diffuse = copied.Diffuse, Specular = copied.Specular, Emissive = copied.Emissive, Exposure = copied.Exposure },
        PasteKind.Surface => target with
        {
            Roughness = copied.Roughness, Metalness = copied.Metalness, Anisotropy = copied.Anisotropy,
            SheenRate = copied.SheenRate, SheenTint = copied.SheenTint, SheenAperture = copied.SheenAperture,
        },
        _ => copied,
    };

    private static DtColorRow Compose(DtColorRow row, float scaleU, float scaleV, float rotation, float shear)
    {
        var (uu, uv, vu, vv) = TileTransform.Compose(scaleU, scaleV, rotation, shear);
        return row with { TileUU = uu, TileUV = uv, TileVU = vu, TileVV = vv };
    }

    /// <summary>The document with one row of its Dawntrail table changed.</summary>
    private static MaterialDocument EditRow(MaterialDocument doc, int row, Func<DtColorRow, DtColorRow> change)
    {
        if (doc.Table is not { Kind: ColorTableKind.Dawntrail } source)
            return doc;
        var table = source.Clone();
        table.Rows[row] = change(table.Rows[row]);
        return doc.WithTable(table);
    }

    /// <summary>The document with one row (and its dye, when both tables have one) back as saved.</summary>
    private static MaterialDocument DiscardRow(MaterialDocument doc, int row, DtColorRow saved, DtDyeRow? savedDye)
    {
        if (doc.Table is not { Kind: ColorTableKind.Dawntrail } source)
            return doc;
        var table = source.Clone();
        table.Rows[row] = saved;
        if (savedDye is { } dye && table.Dye != null)
            table.Dye[row] = dye;
        return doc.WithTable(table);
    }

    // ---- Row controls ----

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Accent, title);
    }

    /// <summary>A label on the left and the control's width set; false when the row is clipped away.</summary>
    private static bool RowLabel(string label, string help)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        Tip(help);
        ImGui.SameLine(Theme.Scaled(150));
        ImGui.SetNextItemWidth(-1);
        return true;
    }

    /// <summary>
    /// A detail pattern or reflection map: the picked one's thumbnails, then a list of every one in the
    /// game's arrays with theirs. A number box while the game hasn't loaded the arrays. True on a change.
    /// </summary>
    private bool SlicePicker(string id, ref int value, int max, params TextureArraySlices.ArrayKind[] arrays)
    {
        if (slices.Info(arrays[0]) is not { } info)
        {
            var typed = ImGui.InputInt(id, ref value, 1, 4);
            value = Math.Clamp(value, 0, max);
            return typed;
        }
        var thumb = ImGui.GetFrameHeight();
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(ThumbnailsWidth(arrays.Length, thumb), thumb));
        DrawThumbnails(ImGui.GetWindowDrawList(), arrays, value, at, thumb);
        if (ImGui.IsItemHovered())
            ThumbnailTooltip(arrays, value);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        var changed = false;
        using var combo = ImRaii.Combo(id, value < info.Count ? $"{value}" : $"{value} (not in the game)", ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
            return false;
        using var align = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f));
        var size = Theme.Scaled(48);
        var step = size + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.IsWindowAppearing())
            ImGui.SetScrollY(Math.Max(0, value - 1) * step);
        var draw = ImGui.GetWindowDrawList();
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(info.Count, step);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var start = ImGui.GetCursorScreenPos();
                    if (ImGui.Selectable($"{index}##slice-{index}", index == value, ImGuiSelectableFlags.None, new Vector2(0, size)) && index != value)
                    {
                        value = index;
                        changed = true;
                    }
                    DrawThumbnails(draw, arrays, index, start + new Vector2(Theme.Scaled(36), 0), size);
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
        return changed;
    }

    private static float ThumbnailsWidth(int count, float size) => count * size + (count - 1) * Theme.Scaled(2);

    private void DrawThumbnails(ImDrawListPtr draw, TextureArraySlices.ArrayKind[] arrays, int slice, Vector2 at, float size)
    {
        for (var i = 0; i < arrays.Length; i++)
            slices.Draw(draw, arrays[i], slice, at + new Vector2(i * (size + Theme.Scaled(2)), 0), new Vector2(size));
    }

    /// <summary>The slice's thumbnails, large, in a tooltip; nothing when the game doesn't have it.</summary>
    private void ThumbnailTooltip(TextureArraySlices.ArrayKind[] arrays, int slice)
    {
        if (slices.Info(arrays[0]) is not { } info || slice < 0 || slice >= info.Count)
            return;
        using var tooltip = ImRaii.Tooltip();
        var size = Theme.Scaled(128);
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(ThumbnailsWidth(arrays.Length, size), size));
        DrawThumbnails(ImGui.GetWindowDrawList(), arrays, slice, at, size);
    }

    private static void RowColour(MaterialSession current, int row, string label, Vector3 stored, Func<Vector3, DtColorRow> with, string name)
    {
        RowLabel(label, "Edited as the game lights it; values above 1 make it brighter.");
        var edit = RowMath.ToEdit(stored);
        var changed = ImGui.ColorEdit3($"##{label}", ref edit, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr);
        var value = RowMath.ToStored(edit);
        var updated = changed ? with(value) : default;
        Track(current, changed, d => EditRow(d, row, _ => updated), $"{name} {label.ToLowerInvariant()}");
    }

    private static void RowPercent(MaterialSession current, int row, string label, string help, float stored, Func<float, DtColorRow> with, string name)
    {
        RowLabel(label, help);
        var percent = stored * 100f;
        var changed = ImGui.SliderFloat($"##{label}", ref percent, 0f, 100f, "%.0f%%");
        var updated = changed ? with(percent / 100f) : default;
        Track(current, changed, d => EditRow(d, row, _ => updated), $"{name} {label.ToLowerInvariant()}");
    }

    private static void RowSlider(MaterialSession current, int row, string label, string help, float stored, float min, float max, Func<float, DtColorRow> with, string name)
    {
        RowLabel(label, help);
        var value = stored;
        var changed = ImGui.SliderFloat($"##{label}", ref value, min, max, "%.2f");
        var updated = changed ? with(value) : default;
        Track(current, changed, d => EditRow(d, row, _ => updated), $"{name} {label.ToLowerInvariant()}");
    }

    private static void RowTransform(MaterialSession current, int row, string label, string help, float shown, float min, float max, Func<float, DtColorRow> with, string name)
    {
        RowLabel(label, help);
        var value = shown;
        var changed = ImGui.DragFloat($"##{label}", ref value, 0.1f, min, max, "%.2f");
        var updated = changed ? with(value) : default;
        Track(current, changed, d => EditRow(d, row, _ => updated), $"{name} {label.ToLowerInvariant()}");
    }

    private static void RawValue(MaterialSession current, int row, int half, float stored, Func<float, DtColorRow> with, string name)
    {
        RowLabel(ShaderLabels.RowField(half), "What this value does is not known. It is kept as it is.");
        var value = stored;
        var changed = ImGui.DragFloat($"##raw-{half}", ref value, 0.01f, 0, 0, "%.3f");
        var updated = changed ? with(value) : default;
        Track(current, changed, d => EditRow(d, row, _ => updated), $"{name} value {half}");
    }

    // ---- Legacy tables ----

    private void DrawLegacyRows(MaterialSession current, MaterialDocument doc, ColorTable table)
    {
        var onCharacter = doc.ShaderPackage.Equals("character.shpk", StringComparison.OrdinalIgnoreCase);
        if (onCharacter)
        {
            Widgets.Banner("##legacy-ignored", FeedbackSeverity.Warning, "The game ignores this old 16-row table on character.shpk. Convert it to Dawntrail rows.");
            using (ImRaii.Disabled(Busy))
            {
                if (ImGui.Button("Convert to Dawntrail##legacy-convert"))
                    ConvertToDawntrail(current, doc);
            }
        }
        else
        {
            Widgets.HintWrapped("An old 16-row table. Convert to Dawntrail in the menu to use the newer rows.");
        }
        using var grid = ImRaii.Table("##legacy-rows", 11, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY);
        if (!grid.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var header in new[] { "#", "Base", "Shine", "Shine strength", "Gloss", "Glow", "Detail", "UU", "UV", "VU", "VV" })
            ImGui.TableSetupColumn(header, header is "#" or "Base" or "Shine" or "Glow" ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        for (var row = 0; row < ColorTable.LegacyRowCount; row++)
        {
            using var id = ImRaii.PushId($"legacy-{row}");
            var values = table.LegacyRows[row];
            var name = $"Row {row + 1}";
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            var unused = usage is { RowUsed.Length: ColorTable.LegacyRowCount } && !usage.RowUsed[row];
            ImGui.TextColored(unused ? Theme.Inactive : Theme.Muted, (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(usage == null ? usageNote : unused ? "Not used by the index texture." : $"Used by {usage.RowShare[row] * 100:0.#}% of the index texture.");
            LegacyColour(current, row, "base", values.Diffuse, colour => values with { Diffuse = colour }, name);
            LegacyColour(current, row, "shine", values.Specular, colour => values with { Specular = colour }, name);
            LegacyFloat(current, row, "strength", values.SpecularMask, v => values with { SpecularMask = v }, name);
            LegacyFloat(current, row, "gloss", values.Gloss, v => values with { Gloss = v }, name);
            LegacyColour(current, row, "glow", values.Emissive, colour => values with { Emissive = colour }, name);
            ImGui.TableNextColumn();
            var tile = values.TileIndex;
            ImGui.SetNextItemWidth(-1);
            var tileChanged = ImGui.InputInt("##tile", ref tile, 0, 0);
            if (ImGui.IsItemHovered() && !ImGui.IsItemActive())
                ThumbnailTooltip([TextureArraySlices.ArrayKind.PatternColour, TextureArraySlices.ArrayKind.PatternNormal], values.TileIndex);
            tile = Math.Clamp(tile, 0, 63);
            var tiled = values with { TileIndex = tile };
            Track(current, tileChanged, d => EditLegacyRow(d, row, _ => tiled), $"{name} detail pattern");
            LegacyFloat(current, row, "uu", values.TileUU, v => values with { TileUU = v }, name);
            LegacyFloat(current, row, "uv", values.TileUV, v => values with { TileUV = v }, name);
            LegacyFloat(current, row, "vu", values.TileVU, v => values with { TileVU = v }, name);
            LegacyFloat(current, row, "vv", values.TileVV, v => values with { TileVV = v }, name);
        }
    }

    private static void LegacyColour(MaterialSession current, int row, string label, Vector3 stored, Func<Vector3, LegacyColorRow> with, string name)
    {
        ImGui.TableNextColumn();
        var edit = RowMath.ToEdit(stored);
        var changed = ImGui.ColorEdit3($"##{label}", ref edit, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.NoInputs);
        var updated = changed ? with(RowMath.ToStored(edit)) : default;
        Track(current, changed, d => EditLegacyRow(d, row, _ => updated), $"{name} {label}");
    }

    private static void LegacyFloat(MaterialSession current, int row, string label, float stored, Func<float, LegacyColorRow> with, string name)
    {
        ImGui.TableNextColumn();
        var value = stored;
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.DragFloat($"##{label}", ref value, 0.05f, 0, 0, "%.2f");
        var updated = changed ? with(value) : default;
        Track(current, changed, d => EditLegacyRow(d, row, _ => updated), $"{name} {label}");
    }

    private static MaterialDocument EditLegacyRow(MaterialDocument doc, int row, Func<LegacyColorRow, LegacyColorRow> change)
    {
        if (doc.Table is not { Kind: ColorTableKind.Legacy } source)
            return doc;
        var table = source.Clone();
        table.LegacyRows[row] = change(table.LegacyRows[row]);
        return doc.WithTable(table);
    }

    /// <summary>
    /// A legacy material moves to character.shpk with Dawntrail rows. Legacy shaders pick rows with the
    /// normal map's alpha, so unless the material has an index texture that loads, one is made from the
    /// normal map; the conversion and the texture land as one undo step.
    /// </summary>
    private void ConvertToDawntrail(MaterialSession current, MaterialDocument doc)
    {
        var normal = doc.Slots.FirstOrDefault(slot => slot.SamplerId == MaterialDocument.NormalSampler && slot.Path.Length > 0)?.Path;
        var existing = doc.Slots.FirstOrDefault(slot => slot.SamplerId == IndexSampler && slot.Path.Length > 0)?.Path;
        var indexPath = existing ?? (normal == null ? null : MaterialSession.IndexPathFor(normal));
        var source = current.Source;
        var normalFile = normal != null && resolvedTextures.TryGetValue(normal, out var foundNormal) ? foundNormal : normal;
        var indexFile = existing != null && resolvedTextures.TryGetValue(existing, out var foundIndex) ? foundIndex : existing;
        byte[]? built = null;
        Run(async () =>
        {
            // A mod material's textures come from its own mod: the character's collection may load other ones (or the mod is off).
            if (!source.IsGameData && source.ModRootPath.Length > 0)
            {
                string? Own(string? gamePath) => gamePath == null
                    ? null
                    : ModMetadata.MappedFileBeside(source.ModRootPath, source.GamePath, source.RelativePath, gamePath) is { } relative
                      && Path.Combine(source.ModRootPath, relative.Replace('/', Path.DirectorySeparatorChar)) is var full && File.Exists(full)
                        ? full
                        : null;
                normalFile = Own(normal) ?? normalFile;
                indexFile = Own(existing) ?? indexFile;
            }
            if (await ReadTextureAsync(indexFile).ConfigureAwait(false) != null)
                return null;
            var bytes = await ReadTextureAsync(normalFile).ConfigureAwait(false);
            if (bytes == null || indexPath == null)
                return "The normal map couldn't be read, so no index texture was made. The whole model shows pair 1 until you add one.";
            try
            {
                built = LegacyIndexTexture.FromNormal(TextureDecoder.Decode(bytes, int.MaxValue));
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
            {
                return "The normal map's format can't be read, so no index texture was made. The whole model shows pair 1 until you add one.";
            }
            return null;
        }, warning =>
        {
            if (built != null)
                current.AddNewFile(indexPath!, built);
            IReadOnlyList<string> notes = [];
            _ = current.Apply(d =>
            {
                var (converted, changed) = ConvertedToDawntrail(d, built != null ? indexPath : null);
                notes = changed;
                return converted;
            }, "Convert to Dawntrail");
            var text = built != null ? $"Converted to Dawntrail rows, with an index texture made from the normal map ({indexPath})." : "Converted to Dawntrail rows.";
            return string.Join(" ", new[] { text, warning }.Concat(notes).Where(part => !string.IsNullOrEmpty(part)));
        });
    }

    private static (MaterialDocument Doc, IReadOnlyList<string> Notes) ConvertedToDawntrail(MaterialDocument doc, string? indexPath)
    {
        IReadOnlyList<string> notes = [];
        if (!doc.ShaderPackage.Equals("character.shpk", StringComparison.OrdinalIgnoreCase))
            (doc, notes) = MaterialSession.Reconcile(doc.WithShaderPackage("character.shpk"), ShaderPackages.Get("character.shpk")!);
        else if (doc.Table is { Kind: ColorTableKind.Legacy } table)
            doc = doc.WithTable(table.ToDawntrail());
        if (indexPath == null)
            return (doc, notes);
        // The made texture replaces the index slot Reconcile may have pointed elsewhere.
        var slot = doc.Slots.ToList().FindIndex(s => s.SamplerId == IndexSampler);
        doc = slot < 0 ? doc.WithAddedSlot(IndexSampler, indexPath) : doc.WithSlot(slot, doc.Slots[slot] with { Path = indexPath });
        return (doc, notes.Where(note => !note.Contains("Make that texture", StringComparison.Ordinal)).ToList());
    }

    /// <summary>A texture's bytes from a mod file or the game's files; null when there is none.</summary>
    private async Task<byte[]?> ReadTextureAsync(string? file)
    {
        if (string.IsNullOrEmpty(file))
            return null;
        try
        {
            if (Path.IsPathRooted(file))
                return File.Exists(file) ? await File.ReadAllBytesAsync(file, life.Token).ConfigureAwait(false) : null;
            return (await data.GetFileAsync<Lumina.Data.FileResource>(file, life.Token).ConfigureAwait(false))?.Data;
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
