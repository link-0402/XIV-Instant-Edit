using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// Draws only the rows of a long list that are in view, for lists whose rows differ in height
/// (section headers between tiles, tiles of two sizes). The heights are summed once per frame, the
/// visible range found from the scroll position, and the space of every row not drawn is kept, so the
/// scrollbar behaves as if all of them were there. Bringing a row into view (for "go to" from an issue
/// or a report) scrolls to its offset whether or not it was drawn.
/// </summary>
internal static class VirtualRows
{
    /// <summary>
    /// Draws the visible rows of a list inside the current child window. <paramref name="drawRow"/>
    /// is called for each visible row with the cursor at its top-left. With <paramref name="reveal"/>
    /// set to a row index, the list scrolls so that row sits a third of the way down.
    /// </summary>
    public static void Draw(IReadOnlyList<float> heights, Action<int> drawRow, int reveal = -1, bool keepInView = false)
    {
        int count = heights.Count;
        float startY = ImGui.GetCursorPosY();
        float spacing = ImGui.GetStyle().ItemSpacing.Y;

        // Prefix sums: where each row starts, relative to the list's top. Pooled, as lists draw every frame.
        var offsets = System.Buffers.ArrayPool<float>.Shared.Rent(count + 1);
        try
        {
            Draw(heights, drawRow, reveal, keepInView, count, startY, spacing, offsets);
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(offsets);
        }
    }

    private static void Draw(IReadOnlyList<float> heights, Action<int> drawRow, int reveal, bool keepInView, int count, float startY,
        float spacing, float[] offsets)
    {
        offsets[0] = 0;
        for (int i = 0; i < count; i++)
            offsets[i + 1] = offsets[i] + heights[i] + spacing;

        if (reveal >= 0 && reveal < count)
        {
            float rowTop = startY + offsets[reveal];
            float rowBottom = rowTop + heights[reveal];
            float scroll = ImGui.GetScrollY();
            float view = ImGui.GetWindowHeight();
            if (!keepInView)
                ImGui.SetScrollY(Math.Max(0, rowTop - view * 0.33f));
            else if (rowTop < scroll)
                ImGui.SetScrollY(Math.Max(0, rowTop - spacing));
            else if (rowBottom > scroll + view)
                ImGui.SetScrollY(rowBottom - view + spacing);
        }

        float top    = ImGui.GetScrollY() - startY;
        float bottom = top + ImGui.GetWindowHeight();

        int first = LowerBound(offsets, count + 1, top) - 1;
        if (first < 0) first = 0;
        int last = first;
        while (last < count && offsets[last] < bottom)
            last++;

        for (int i = first; i < last; i++)
        {
            ImGui.SetCursorPosY(startY + offsets[i]);
            drawRow(i);
        }

        ImGui.SetCursorPosY(startY + offsets[count]);
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>The first index whose offset is greater than <paramref name="value"/>.</summary>
    private static int LowerBound(float[] offsets, int length, float value)
    {
        int lo = 0, hi = length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (offsets[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
