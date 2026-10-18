using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using InstantEdit.Studio.Services;

namespace InstantEdit.Studio.Windows.UI;

internal enum PathKind { File, Folder }

/// <summary>Lets a path field take dropped files: why a drop would be refused (null = accepted), and what to do with it.</summary>
internal sealed record FileDropRule(Func<DroppedPaths, string?> Reject, Action<DroppedPaths> OnDrop);

/// <summary>
/// Shared drawing vocabulary: section headers, label/field rows, icon buttons, path
/// pickers, pills and splitters. Panels draw through these instead of hand-rolling
/// the same Text/SameLine/SetTooltip sequence, so the windows look like one tool.
/// </summary>
internal static class Gui
{
    /// <summary>
    /// The one file dialog every window opens through. Drawn once per frame by the
    /// plugin after its windows, so a dialog opened from either window shows exactly once.
    /// </summary>
    public static FileDialogManager Dialogs { get; } = new();

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="text"/> as a tooltip when the last item is hovered.</summary>
    public static void Tooltip(string? text)
    {
        if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    /// <summary>An accent-coloured heading with a rule under it.</summary>
    public static void SectionHeader(string label, string? help = null)
    {
        ImGui.TextColored(Theme.Accent, label);
        Tooltip(help);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>A muted explanatory line, e.g. an empty-state message.</summary>
    public static void Hint(string text) => ImGui.TextColored(Theme.Muted, text);

    /// <summary>A wrapped muted paragraph.</summary>
    public static void HintWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Draws a FontAwesome glyph as text, e.g. a status or warning marker.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
    }

    /// <summary>Muted label in the fixed gutter, leaving the cursor on the same line for the field.</summary>
    public static void Label(string label, string? help = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        Tooltip(help);
        ImGui.SameLine(Theme.LabelWidth);
    }

    /// <summary>Label + read-only value, value muted and click-to-copy.</summary>
    public static void LabeledValue(string label, string value, string? help = null)
    {
        Label(label, help);
        ImGui.TextUnformatted(value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(help != null ? $"{help}\n\nClick to copy." : "Click to copy.");
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(value);
    }

    // ── Fields ───────────────────────────────────────────────────────────────

    public static bool LabeledInput(string label, string id, ref string value, int maxLength,
        string hint = "", string? help = null, float width = -1)
    {
        Label(label, help);
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.InputTextWithHint(id, hint, ref value, maxLength);
        Tooltip(help);
        return changed;
    }

    public static bool LabeledCombo(string label, string id, ref int index, string[] items,
        string? help = null, float width = -1)
    {
        Label(label, help);
        ImGui.SetNextItemWidth(width);
        bool changed = InstantEdit.Ui.ComboWheel.Combo(id, ref index, items);
        Tooltip(help);
        return changed;
    }

    // ── Buttons ──────────────────────────────────────────────────────────────

    /// <summary>A square FontAwesome button. Danger buttons draw their glyph in the error colour.</summary>
    public static bool IconButton(FontAwesomeIcon icon, string id, string tooltip, bool danger = false)
    {
        if (danger) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool clicked = ImGuiComponents.IconButton(id, icon);
        if (danger) ImGui.PopStyleColor();
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>A FontAwesome glyph + label button.</summary>
    public static bool IconTextButton(FontAwesomeIcon icon, string label, string? tooltip = null, float width = 0)
    {
        bool clicked = width > 0
            ? ImGuiComponents.IconButtonWithText(icon, label, new Vector2(width, 0))
            : ImGuiComponents.IconButtonWithText(icon, label);
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>A prominent accent-filled button for the one primary action on screen.</summary>
    public static bool PrimaryButton(FontAwesomeIcon icon, string label, float width, string? tooltip = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.Accent with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = 0.75f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent);
        bool clicked = IconTextButton(icon, label, tooltip, width);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    /// <summary>
    /// A path text field with a browse button. Typed edits return true immediately;
    /// a path chosen in the dialog arrives later through <paramref name="onPicked"/>,
    /// because the dialog completes on a later frame.
    /// </summary>
    public static bool PathPicker(string id, ref string path, string hint, PathKind kind, string filter,
        Action<string> onPicked, string? help = null, float reserveRight = 0, FileDropRule? drop = null)
    {
        float buttonW = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(buttonW + ImGui.GetStyle().ItemInnerSpacing.X + reserveRight));
        bool changed = ImGui.InputTextWithHint($"##{id}", hint, ref path, 1024);
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(path))
            ImGui.SetTooltip(help != null ? $"{path}\n\n{help}" : path);
        if (drop != null && FileDrop.Target(drop.Reject, out var dropped))
            drop.OnDrop(dropped);

        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        if (IconButton(kind == PathKind.Folder ? FontAwesomeIcon.FolderOpen : FontAwesomeIcon.File,
                $"{id}Browse", kind == PathKind.Folder ? "Browse for a folder" : "Browse for a file"))
        {
            var start = StartDirectory(path);
            if (kind == PathKind.Folder)
            {
                void Done(bool ok, string picked) { if (ok) onPicked(picked); }
                if (start != null) Dialogs.OpenFolderDialog("Select folder", Done, start, false);
                else               Dialogs.OpenFolderDialog("Select folder", Done);
            }
            else if (start != null)
            {
                // Only the multi-select overload takes a start folder; cap it at one pick.
                Dialogs.OpenFileDialog("Select file", filter, (ok, picked) =>
                {
                    if (ok && picked.Count > 0) onPicked(picked[0]);
                }, 1, start, false);
            }
            else
            {
                Dialogs.OpenFileDialog("Select file", filter, (ok, picked) => { if (ok) onPicked(picked); });
            }
        }

        return changed;
    }

    /// <summary>
    /// A button that opens a multi-file picker and hands the picked paths to
    /// <paramref name="onPicked"/> once the dialog completes (on a later frame).
    /// </summary>
    public static bool PickMultipleFiles(string id, string label, string filter, Action<List<string>> onPicked, string? startFrom = null)
    {
        const int maxSelection = 64;
        bool clicked = ImGui.Button($"{label}##{id}");
        if (clicked)
        {
            var start = (startFrom != null ? StartDirectory(startFrom) : null) ?? string.Empty;
            void Done(bool ok, List<string> picked) { if (ok && picked.Count > 0) onPicked(picked); }
            Dialogs.OpenFileDialog("Select files", filter, Done, maxSelection, start, false);
        }
        return clicked;
    }

    private static string? StartDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            if (System.IO.Directory.Exists(path)) return path;
            var dir = System.IO.Path.GetDirectoryName(path);
            return dir != null && System.IO.Directory.Exists(dir) ? dir : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ── Pills / badges ───────────────────────────────────────────────────────

    public static Vector4 SeverityColor(Severity severity) => severity switch
    {
        Severity.Error   => Theme.Bad,
        Severity.Warning => Theme.Warn,
        _                => Theme.Muted,
    };

    /// <summary>A small rounded pill with text, laid out inline like any other item.</summary>
    public static void Pill(string text, Vector4 color)
    {
        var textSize = ImGui.CalcTextSize(text);
        var pad = new Vector2(Theme.S(6), Theme.S(1));
        var size = textSize + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        float yOffset = (ImGui.GetFrameHeight() - size.Y) * 0.5f;
        pos.Y += MathF.Max(0, yOffset);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color with { W = 0.22f }), size.Y * 0.5f);
        draw.AddText(pos + pad, ImGui.GetColorU32(color), text);
        ImGui.Dummy(new Vector2(size.X, MathF.Max(size.Y, ImGui.GetFrameHeight())));
    }

    /// <summary>Issue-count pill: error count in red, else warning count in amber, else nothing.</summary>
    public static void IssueBadge(int errors, int warnings)
    {
        if (errors > 0)
            Pill(errors.ToString(), Theme.Bad);
        else if (warnings > 0)
            Pill(warnings.ToString(), Theme.Warn);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A draggable vertical divider between two side-by-side regions. With
    /// <paramref name="invert"/>, dragging right shrinks the size (for a pane on the right).
    /// Returns true on the frame the drag ends, so the caller can persist the width once.
    /// </summary>
    public static bool VerticalSplitter(string id, ref float size, float min, float max, float height, bool invert = false)
    {
        ImGui.SameLine(0, 0);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(Theme.SplitterWidth, height));

        bool hot = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (hot)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive())
        {
            float delta = ImGui.GetIO().MouseDelta.X;
            size = Math.Clamp(size + (invert ? -delta : delta), min, Math.Max(min, max));
        }

        float x = pos.X + Theme.SplitterWidth * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, pos.Y), new Vector2(x, pos.Y + height),
            ImGui.GetColorU32(hot ? Theme.Accent : Theme.Band with { W = 0.10f }), hot ? 2f : 1f);

        ImGui.SameLine(0, 0);
        return ImGui.IsItemDeactivated();
    }

    /// <summary>
    /// A draggable horizontal divider between two stacked regions. With <paramref name="invert"/>,
    /// dragging down shrinks the size (for the lower pane). Returns true on the frame the drag
    /// ends, so the caller can persist the height once.
    /// </summary>
    public static bool HorizontalSplitter(string id, ref float size, float min, float max, float width, bool invert = false)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new System.Numerics.Vector2(width, Theme.SplitterWidth));

        bool hot = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (hot)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (ImGui.IsItemActive())
        {
            float delta = ImGui.GetIO().MouseDelta.Y;
            size = Math.Clamp(size + (invert ? -delta : delta), min, Math.Max(min, max));
        }

        float y = pos.Y + Theme.SplitterWidth * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new System.Numerics.Vector2(pos.X, y), new System.Numerics.Vector2(pos.X + width, y),
            ImGui.GetColorU32(hot ? Theme.Accent : Theme.Band with { W = 0.10f }), hot ? 2f : 1f);

        return ImGui.IsItemDeactivated();
    }

    /// <summary>Disables every widget drawn inside the using-block when <paramref name="disabled"/> is set.</summary>
    public static DisabledScope Disabled(bool disabled) => new(disabled);

    public readonly struct DisabledScope : IDisposable
    {
        private readonly bool _on;
        public DisabledScope(bool on) { _on = on; if (on) ImGui.BeginDisabled(); }
        public void Dispose() { if (_on) ImGui.EndDisabled(); }
    }

    /// <summary>
    /// Right-aligns the next item of the given width on the current line. Measured from the space
    /// left, not the window's content edge, so it also stays inside a table cell.
    /// </summary>
    public static void AlignRight(float width)
    {
        float x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX())
            ImGui.SameLine(x);
    }
}
