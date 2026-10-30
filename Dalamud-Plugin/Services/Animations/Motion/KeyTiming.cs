namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// The frame maths of the picked keys' timing tools: scaling about a frame (the playhead), reversing between the first
/// and the last key, and the scale a drag of the mouse asks for. Percents are whole, as the status line shows them.
/// </summary>
internal static class KeyTiming
{
    /// <summary>The smallest and largest scale offered, in percent; a negative one is Reverse.</summary>
    public const int MinPercent = 10;
    public const int MaxPercent = 1000;

    /// <summary>
    /// How far from the pivot, in pixels, a scale drag's starting point counts at least: started on the playhead, the
    /// mouse would otherwise have no distance to measure against.
    /// </summary>
    public const float MinReach = 24;

    /// <summary><paramref name="frame"/> scaled by <paramref name="percent"/> about <paramref name="pivot"/>, to the nearest frame (halves away from the pivot).</summary>
    public static int Scaled(int frame, int pivot, int percent) =>
        pivot + (int)Math.Round((long)(frame - pivot) * percent / 100.0, MidpointRounding.AwayFromZero);

    /// <summary><paramref name="frame"/> mirrored between <paramref name="first"/> and <paramref name="last"/>.</summary>
    public static int Reversed(int frame, int first, int last) => first + last - frame;

    /// <summary>
    /// The scale of a drag about <paramref name="pivot"/> that began at <paramref name="from"/> and is now at
    /// <paramref name="to"/> (frames; <paramref name="pixelsPerFrame"/> the view's zoom): how many times as far from the
    /// pivot the mouse is as where it began, at least <see cref="MinReach"/> pixels away, in whole percent from
    /// <see cref="MinPercent"/> to <see cref="MaxPercent"/>. Crossing the pivot gives the smallest.
    /// </summary>
    public static int DragPercent(float pivot, float from, float to, float pixelsPerFrame)
    {
        var reach = from - pivot;
        var least = MinReach / Math.Max(pixelsPerFrame, 1e-6f);
        if (Math.Abs(reach) < least)
            reach = reach < 0 ? -least : least;
        var factor = (to - pivot) / reach;
        return float.IsFinite(factor) ? Math.Clamp((int)MathF.Round(factor * 100, MidpointRounding.AwayFromZero), MinPercent, MaxPercent) : 100;
    }
}
