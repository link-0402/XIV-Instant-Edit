using System.Globalization;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The visible part of a timeline: the frame at the left edge, the zoom in pixels per frame and the
/// width of the track area in pixels. X is measured from the track area's left edge, legend excluded.
/// No ImGui or Dalamud dependencies.
/// </summary>
internal readonly record struct TimelineView(float First, float PixelsPerFrame, float Width)
{
    /// <summary>Tick spacings in frames, from one frame up to two minutes at 30 fps.</summary>
    public static readonly int[] Steps = [1, 2, 5, 10, 15, 30, 60, 150, 300, 600, 1800, 3600];

    /// <summary>How far past the length the view may scroll, so the length handle can be dragged out.</summary>
    public const float Headroom = 1.25f;

    /// <summary>The share of the width a fitted timeline fills, leaving room to grab the length handle.</summary>
    public const float FitMargin = 1.1f;

    private const float MinScale = 1e-4f;

    /// <summary>The zoom, never zero, so mapping never divides by zero before the first layout.</summary>
    public float Scale => PixelsPerFrame > MinScale && float.IsFinite(PixelsPerFrame) ? PixelsPerFrame : MinScale;

    public float X(float frame) => (frame - First) * Scale;

    public float FrameAt(float x) => First + x / Scale;

    /// <summary>The frame at the right edge.</summary>
    public float Last => FrameAt(Width);

    /// <summary>Zooms by <paramref name="factor"/> keeping the frame under <paramref name="x"/> where it is.</summary>
    public TimelineView ZoomAt(float x, float factor, float min, float max)
    {
        if (!float.IsFinite(factor) || factor <= 0 || !float.IsFinite(x))
            return this;
        var anchor = FrameAt(x);
        var scale = Math.Clamp(Scale * factor, Math.Max(MinScale, min), Math.Max(MinScale, Math.Max(min, max)));
        return this with { PixelsPerFrame = scale, First = anchor - x / scale };
    }

    /// <summary>Scrolls by <paramref name="dx"/> pixels; positive moves the content right.</summary>
    public TimelineView Pan(float dx) => float.IsFinite(dx) ? this with { First = First - dx / Scale } : this;

    /// <summary>Keeps the view between frame 0 and <see cref="Headroom"/> times the length.</summary>
    public TimelineView Clamp(int length)
    {
        var last = Math.Max(0, Math.Max(1, length) * Headroom - Math.Max(0, Width) / Scale);
        var first = float.IsFinite(First) ? Math.Clamp(First, 0, last) : 0;
        return first == First && PixelsPerFrame == Scale ? this : new TimelineView(first, Scale, Width);
    }

    /// <summary>The whole timeline in <paramref name="width"/> pixels, with a little room past the end.</summary>
    public static TimelineView Fit(int length, float width)
    {
        width = float.IsFinite(width) ? Math.Max(0, width) : 0;
        return new TimelineView(0, Math.Max(MinScale, width / Math.Max(1, Math.Max(1, length) * FitMargin)), width);
    }

    /// <summary>The smallest step whose ticks are at least <paramref name="minPixels"/> apart; labelled ticks.</summary>
    public int MajorStep(float minPixels)
    {
        foreach (var step in Steps)
            if (step * Scale >= minPixels)
                return step;
        return Steps[^1];
    }

    /// <summary>The smallest step dividing <paramref name="major"/> with ticks at least <paramref name="minPixels"/> apart; the major step when none fits.</summary>
    public int MinorStep(int major, float minPixels)
    {
        foreach (var step in Steps)
        {
            if (step >= major)
                break;
            if (major % step == 0 && step * Scale >= minPixels)
                return step;
        }
        return Math.Max(1, major);
    }

    /// <summary>The first tick of <paramref name="step"/> at or before the left edge, never below frame 0.</summary>
    public int FirstTick(int step)
    {
        step = Math.Max(1, step);
        return Math.Max(0, (int)MathF.Floor(First / step) * step);
    }
}

/// <summary>Short time texts for the ruler and readouts. Invariant culture, never an ellipsis.</summary>
internal static class TimelineText
{
    /// <summary>
    /// A ruler label: seconds ("0s", "1.5s") when the step is a half second or more, else frames
    /// ("12f") with whole seconds still shown as seconds so the ruler keeps its bearings.
    /// </summary>
    public static string Tick(int frame, int major)
    {
        const int fps = TimelineDocument.Fps;
        if (major >= fps / 2 || frame % fps == 0)
            return Seconds(frame);
        return frame.ToString(CultureInfo.InvariantCulture) + "f";
    }

    /// <summary>Frames as seconds with at most two decimals: "0s", "1.5s", "2.03s".</summary>
    public static string Seconds(int frames) =>
        (frames / (double)TimelineDocument.Fps).ToString("0.##", CultureInfo.InvariantCulture) + "s";
}
