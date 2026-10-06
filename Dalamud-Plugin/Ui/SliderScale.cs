using System.Globalization;

namespace InstantEdit.Ui;

/// <summary>
/// Where a value sits along an ImGui slider, worked out the way ImGui places its grab, so marks drawn
/// over a slider line up with it, logarithmic sliders included.
/// </summary>
internal static class SliderScale
{
    /// <summary> ImGui leaves this much room at each end of a slider's frame. </summary>
    public const float GrabPadding = 2f;

    /// <summary> How far along the slider a value is, from 0 to 1. </summary>
    public static float Ratio(float value, float min, float max, bool logarithmic, int decimals)
    {
        if (!(max > min))
            return 0;
        var clamped = Math.Clamp(value, min, max);
        if (!logarithmic || min < 0)
            return (clamped - min) / (max - min);
        // ImGui moves a zero end of a logarithmic slider to the smallest value its format shows.
        var epsilon = MathF.Pow(0.1f, decimals);
        var low = MathF.Abs(min) < epsilon ? epsilon : min;
        var high = MathF.Abs(max) < epsilon ? epsilon : max;
        if (clamped <= low)
            return 0;
        if (clamped >= high)
            return 1;
        return MathF.Log(clamped / low) / MathF.Log(high / low);
    }

    /// <summary> The x of a value on a slider frame from <paramref name="left"/> to <paramref name="right"/>. </summary>
    public static float X(float ratio, float left, float right, float grabMinSize)
    {
        var slider = right - left - GrabPadding * 2;
        var grab = Math.Min(grabMinSize, slider);
        return left + GrabPadding + grab / 2 + ratio * (slider - grab);
    }

    /// <summary> The decimals a printf format like "%.4f" shows; ImGui's default 3 without one. </summary>
    public static int Decimals(string format)
    {
        var percent = format.IndexOf('%');
        var dot = percent >= 0 ? format.IndexOf('.', percent) : -1;
        if (dot < 0)
            return 3;
        var end = dot + 1;
        while (end < format.Length && char.IsAsciiDigit(format[end]))
            end++;
        return end > dot + 1 && int.TryParse(format.AsSpan(dot + 1, end - dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var decimals)
            ? decimals
            : 3;
    }

    /// <summary> A value written the way a printf format like "%.1f cm" shows it. </summary>
    public static string Text(float value, string format)
    {
        var decimals = Decimals(format);
        var f = format.IndexOf('f', Math.Max(0, format.IndexOf('%')));
        var suffix = f >= 0 && f + 1 < format.Length ? format[(f + 1)..] : string.Empty;
        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + suffix;
    }
}
