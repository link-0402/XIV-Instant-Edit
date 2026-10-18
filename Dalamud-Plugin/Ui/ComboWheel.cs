using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace InstantEdit.Ui;

/// <summary>
/// Ctrl + mouse wheel over a closed dropdown steps through its entries, as in Penumbra: wheel down is the
/// next one, wheel up the one before, wrapping at the ends. Call right after the combo, while it is closed;
/// the window doesn't scroll while the wheel turns a dropdown.
/// </summary>
internal static class ComboWheel
{
    /// <summary><c>ImGui.Combo</c> that the wheel steps too.</summary>
    public static bool Combo(string id, ref int index, string[] items)
        => ImGui.Combo(id, ref index, items, items.Length) | Scroll(ref index, items.Length);

    /// <summary>Entries to move for the combo just drawn (positive is later); 0 when the wheel isn't turning it.</summary>
    public static int Steps()
    {
        if (!ImGui.GetIO().KeyCtrl || !ImGui.IsItemHovered())
            return 0;
        ImGuiP.SetItemUsingMouseWheel();
        return -(int)ImGui.GetIO().MouseWheel;
    }

    /// <summary>The index <paramref name="steps"/> on from <paramref name="index"/> among <paramref name="count"/>, wrapping; outside the range starts at an end.</summary>
    public static int Step(int index, int count, int steps)
    {
        if (count <= 0)
            return index;
        if (index < 0 || index >= count)
            index = steps > 0 ? -1 : 0;
        return ((index + steps) % count + count) % count;
    }

    /// <summary>The entry <paramref name="steps"/> on from the first one <paramref name="isCurrent"/> matches.</summary>
    public static T Pick<T>(IReadOnlyList<T> items, Func<T, bool> isCurrent, int steps)
    {
        var index = -1;
        for (var i = 0; i < items.Count && index < 0; i++)
            if (isCurrent(items[i]))
                index = i;
        return items[Step(index, items.Count, steps)];
    }

    /// <summary>The entry <paramref name="steps"/> on from <paramref name="current"/>.</summary>
    public static T Pick<T>(IReadOnlyList<T> items, T current, int steps)
        => Pick(items, item => EqualityComparer<T>.Default.Equals(item, current), steps);

    /// <summary>Steps <paramref name="index"/> through <paramref name="count"/> entries; true when it changed.</summary>
    public static bool Scroll(ref int index, int count)
    {
        var steps = Steps();
        if (steps == 0 || count <= 0)
            return false;
        var next = Step(index, count, steps);
        if (next == index)
            return false;
        index = next;
        return true;
    }

    /// <summary>Steps through <paramref name="items"/> from <paramref name="current"/>; true with the new pick when it changed.</summary>
    public static bool Scroll<T>(IReadOnlyList<T> items, T current, out T picked)
    {
        picked = current;
        var steps = Steps();
        if (steps == 0 || items.Count == 0)
            return false;
        picked = Pick(items, current, steps);
        return !EqualityComparer<T>.Default.Equals(picked, current);
    }
}
