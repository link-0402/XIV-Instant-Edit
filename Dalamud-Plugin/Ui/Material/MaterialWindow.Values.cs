using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Materials;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The Values section: the shader constants the shader reads, grouped and named, with a control
/// that fits each one; constants the shader ignores are listed apart and can be removed.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const string IgnoredGroup = "Ignored by this shader";

    /// <summary>One constant as the pane shows it: set in the material, or read by the shader at its default.</summary>
    private sealed record ValueRow(ConstantLabel Label, float[] Values, bool InMaterial, bool Read, int Order);

    private void DrawValues(MaterialSession current, MaterialDocument doc)
    {
        var facts = ShaderPackages.Get(doc.ShaderPackage);
        var rows = ValueRows(doc, facts);
        if (rows.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.SlidersH, "No values", "This shader reads no material values.");
            return;
        }
        foreach (var group in ShaderLabels.Groups)
        {
            var shown = rows.Where(row => row.Read && row.Label.Group == group).ToList();
            if (shown.Count == 0)
                continue;
            if (!ImGui.CollapsingHeader($"{group}##values-{group}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            DrawValueTable(current, group, shown, ignored: false);
        }
        var ignored = rows.Where(row => !row.Read && row.InMaterial).ToList();
        if (ignored.Count > 0 && ImGui.CollapsingHeader($"{IgnoredGroup} ({ignored.Count})##values-ignored"))
        {
            Widgets.HintWrapped("The material sets these, but this shader never reads them. Removing them changes nothing you can see.");
            DrawValueTable(current, "ignored", ignored, ignored: true);
        }
    }

    /// <summary>The material's constants and the ones the shader reads at their defaults, in parameter order.</summary>
    private static List<ValueRow> ValueRows(MaterialDocument doc, ShaderPackageFacts? facts)
    {
        var rows = new List<ValueRow>();
        foreach (var constant in doc.Constants)
            rows.Add(new ValueRow(ShaderLabels.Constant(constant.Id), constant.Values, true, facts?.Reads(constant.Id, doc.Keys) ?? true, Order(constant.Id)));
        if (facts != null)
            foreach (var id in facts.ReadConstants)
                if (doc.Constant(id) == null && facts.Reads(id, doc.Keys) && ShaderPackages.Param(id) is { } param)
                    rows.Add(new ValueRow(ShaderLabels.Constant(id), param.Default, false, true, param.ByteOffset));
        rows.Sort((a, b) => a.Order.CompareTo(b.Order));
        return rows;
    }

    private static int Order(uint id) => ShaderPackages.Param(id)?.ByteOffset ?? int.MaxValue;

    private void DrawValueTable(MaterialSession current, string group, IReadOnlyList<ValueRow> rows, bool ignored)
    {
        using var table = ImRaii.Table($"##values-table-{group}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(190));
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X);
        foreach (var row in rows)
        {
            using var id = ImRaii.PushId($"value-{row.Label.Id:X8}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(row.InMaterial ? Theme.Label : Theme.Muted, ValueName(row.Label));
            Tip(row.InMaterial ? row.Label.Help : row.Label.Help + "\nNot in the material yet: the shader uses this default. Changing it adds it.");
            ImGui.TableNextColumn();
            if (ignored)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, string.Join(", ", row.Values.Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
            }
            else
                DrawValueControl(current, row);
            ImGui.TableNextColumn();
            if (ignored)
            {
                if (Widgets.GhostIconButton("##remove", FontAwesomeIcon.Trash, "Remove it from the material"))
                    _ = current.Apply(doc => doc.WithoutConstant(row.Label.Id), "Remove value");
                continue;
            }
            var defaults = ShaderPackages.Param(row.Label.Id)?.Default;
            var atDefault = defaults == null || defaults.AsSpan().SequenceEqual(row.Values);
            var ctrl = ImGui.GetIO().KeyCtrl;
            if (Widgets.IconButton("##reset", FontAwesomeIcon.UndoAlt, null, !atDefault && ctrl) && defaults != null)
                _ = current.Apply(doc => doc.WithConstant(row.Label.Id, defaults.ToArray()), $"Reset {row.Label.Label}");
            Tip(atDefault ? "At the default." : "Hold Ctrl and click to reset to the default.");
        }
    }

    private string ValueName(ConstantLabel label)
        => customShader && !label.Label.StartsWith("Unknown", StringComparison.Ordinal) ? $"{label.Label} (0x{label.Id:X8})" : label.Label;

    private static void DrawValueControl(MaterialSession current, ValueRow row)
    {
        var label = row.Label;
        var values = row.Values.ToArray();
        var name = label.Label;
        ImGui.SetNextItemWidth(-1);
        switch (label.Kind)
        {
            case ValueKind.Color when values.Length >= 3:
            {
                var colour = new Vector3(values[0], values[1], values[2]);
                var changed = ImGui.ColorEdit3("##colour", ref colour, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr);
                Track(current, changed, doc => doc.WithConstant(label.Id, [colour.X, colour.Y, colour.Z, .. values.Skip(3)]), name);
                break;
            }
            case ValueKind.Percent when values.Length == 1:
            {
                var percent = values[0] * 100f;
                var changed = ImGui.SliderFloat("##percent", ref percent, label.Min * 100f, label.Max * 100f, "%.0f%%");
                Track(current, changed, doc => doc.WithConstant(label.Id, [percent / 100f]), name);
                break;
            }
            case ValueKind.Int when values.Length == 1:
            {
                var number = (int)BitConverter.SingleToUInt32Bits(values[0]);
                var changed = ImGui.InputInt("##int", ref number, 1, 1);
                if (label.Max > label.Min)
                    number = Math.Clamp(number, (int)label.Min, (int)label.Max);
                Track(current, changed, doc => doc.WithConstant(label.Id, [BitConverter.UInt32BitsToSingle((uint)number)]), name);
                break;
            }
            case ValueKind.TileIndex or ValueKind.SphereMapIndex or ValueKind.ShaderId when values.Length == 1:
            {
                var number = (int)MathF.Round(values[0]);
                var changed = ImGui.InputInt("##index", ref number, 1, 1);
                if (label.Max > label.Min)
                    number = Math.Clamp(number, (int)label.Min, (int)label.Max);
                Track(current, changed, doc => doc.WithConstant(label.Id, [number]), name);
                break;
            }
            case ValueKind.Hidden:
                ImGui.TextColored(Theme.Hint, "Set by the game");
                break;
            default:
                DrawFloats(current, label, values);
                break;
        }
    }

    /// <summary>One to four floats: a slider when the label has a range, a drag field when it hasn't.</summary>
    private static void DrawFloats(MaterialSession current, ConstantLabel label, float[] values)
    {
        if (values.Length == 0)
        {
            ImGui.TextColored(Theme.Hint, "Empty");
            return;
        }
        var ranged = label.Max > label.Min;
        var step = label.Step > 0 ? label.Step : 0.01f;
        var changed = false;
        switch (values.Length)
        {
            case 1:
            {
                var value = values[0];
                changed = ranged ? ImGui.SliderFloat("##float", ref value, label.Min, label.Max, "%.3f") : ImGui.DragFloat("##float", ref value, step, 0, 0, "%.3f");
                values[0] = value;
                break;
            }
            case 2:
            {
                var pair = new Vector2(values[0], values[1]);
                changed = ImGui.DragFloat2("##float2", ref pair, step, ranged ? label.Min : 0, ranged ? label.Max : 0, "%.3f");
                (values[0], values[1]) = (pair.X, pair.Y);
                break;
            }
            case 3:
            {
                var triple = new Vector3(values[0], values[1], values[2]);
                changed = ImGui.DragFloat3("##float3", ref triple, step, ranged ? label.Min : 0, ranged ? label.Max : 0, "%.3f");
                (values[0], values[1], values[2]) = (triple.X, triple.Y, triple.Z);
                break;
            }
            default:
            {
                var quad = new Vector4(values[0], values[1], values[2], values[3]);
                changed = ImGui.DragFloat4("##float4", ref quad, step, ranged ? label.Min : 0, ranged ? label.Max : 0, "%.3f");
                (values[0], values[1], values[2], values[3]) = (quad.X, quad.Y, quad.Z, quad.W);
                break;
            }
        }
        var edited = values.ToArray();
        Track(current, changed, doc => doc.WithConstant(label.Id, edited), label.Label);
    }
}
