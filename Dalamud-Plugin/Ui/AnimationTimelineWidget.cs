using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace InstantEdit.Ui;

/// <summary>
/// Drawing for the animation timeline. Built on an invisible button and the window
/// draw list rather than a plotting library, so the plugin's release archive keeps
/// the exact assembly list it asserts today.
/// </summary>
internal static class AnimationTimelineWidget
{
    private const float Height = 34;
    private const float HandleWidth = 7;

    /// <summary>
    /// Geometry of a length bar: where the new length sits within the span shown, and
    /// how far apart the second ticks are. Kept separate from drawing so the mapping
    /// between seconds and pixels is checkable without a UI.
    /// </summary>
    internal readonly record struct Scale(float Span, float Width)
    {
        public float X(float seconds) => Span <= 0 ? 0 : Math.Clamp(seconds / Span, 0, 1) * Width;
        public float Seconds(float x) => Width <= 0 ? 0 : Math.Clamp(x / Width, 0, 1) * Span;
        /// <summary>Whole-second ticks, thinned so they never crowd into a solid block.</summary>
        public int TickStep => Span <= 0 || Width <= 0 ? 1 : Math.Max(1, (int)MathF.Ceiling(Span / Math.Max(1, Width / 28)));
    }

    /// <summary>Space the bar shows: the longer of the two lengths, with headroom to drag into.</summary>
    internal static float SpanFor(float original, float target) =>
        Math.Max(0.001f, Math.Max(original, target) * 1.25f);

    /// <summary>
    /// Draw the length bar and return the length the user dragged to, or null when
    /// they did not change it this frame.
    /// </summary>
    public static float? DrawLength(string id, float original, float target, bool enabled)
    {
        var width = Math.Max(120, ImGui.GetContentRegionAvail().X - 16);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(width, Height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var draw = ImGui.GetWindowDrawList();
        var scale = new Scale(SpanFor(original, target), width);

        var top = origin.Y + 8;
        var bottom = origin.Y + Height - 6;
        draw.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + width, bottom),
            ImGui.GetColorU32(ImGuiCol.FrameBg), 3);
        for (var second = 0; second <= (int)scale.Span; second += scale.TickStep)
        {
            var x = origin.X + scale.X(second);
            draw.AddLine(new Vector2(x, bottom - 4), new Vector2(x, bottom), ImGui.GetColorU32(ImGuiCol.TextDisabled));
        }
        // The original length stays visible behind the new one so the change is legible.
        draw.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + scale.X(original), bottom),
            ImGui.GetColorU32(new Vector4(.35f, .45f, .6f, .45f)), 3);
        var targetX = origin.X + scale.X(target);
        draw.AddRectFilled(new Vector2(origin.X, top), new Vector2(targetX, bottom),
            ImGui.GetColorU32(new Vector4(.35f, .75f, .5f, .55f)), 3);
        draw.AddRectFilled(new Vector2(targetX - HandleWidth / 2, top - 3), new Vector2(targetX + HandleWidth / 2, bottom + 3),
            ImGui.GetColorU32(enabled && (hovered || active) ? ImGuiCol.ButtonHovered : ImGuiCol.Button), 2);

        if (!enabled || !active) return null;
        var dragged = scale.Seconds(ImGui.GetIO().MousePos.X - origin.X);
        // Snap to whole frames at the sampling rate so the handle cannot land between
        // two representable lengths.
        return MathF.Max(1, MathF.Round(dragged * 30)) / 30;
    }
}
