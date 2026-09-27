using System.Numerics;

namespace InstantEdit.Services.Painter;

/// <summary> How Painter draws a texture set: its transparency ("opaque", "test" or "blend") and whether back faces show. </summary>
internal sealed record PainterDisplay(string Alpha, float Threshold, bool DoubleSided)
{
    public static readonly PainterDisplay Default = new("opaque", 0.5f, false);

    public const uint HideBackFaces = 0x01;
    public const uint Translucent = 0x10;

    /// <summary>
    /// The game's rule, as TexTools and MeddleTools render it: translucent materials blend by their
    /// opacity; the others discard texels below g_AlphaThreshold and draw opaque when it is 0. Back
    /// faces show unless the material hides them, and always when the mesh carried them as separate
    /// reversed copies that the Painter mesh leaves out.
    /// </summary>
    public static PainterDisplay For(uint? flags, float? threshold, bool hasOpacity, bool backFaceCopiesRemoved)
    {
        var doubleSided = backFaceCopiesRemoved || (flags is { } f && (f & HideBackFaces) == 0);
        var alpha = !hasOpacity || flags is null ? "opaque"
            : (flags.Value & Translucent) != 0 ? "blend"
            : threshold is > 0f ? "test"
            : "opaque";
        return new PainterDisplay(alpha, alpha == "test" ? Math.Clamp(threshold!.Value, 0f, 1f) : Default.Threshold, doubleSided);
    }
}

/// <summary> The character's own colors, as display values (0..1), for materials whose color isn't a texture. </summary>
public sealed record PainterCharacterColors(Vector3 Hair, Vector3 Highlight);

/// <summary>
/// Base color images for texture sets whose color the game doesn't take from a texture. They only
/// fill Painter's base color channel, which those sets never export.
/// </summary>
internal static class PainterPreviews
{
    // xivModdingFramework's un-customized preview colors, for when the character's aren't known.
    public static readonly PainterCharacterColors DefaultColors = new(
        new Vector3(110, 77, 35) / 255f, new Vector3(91, 110, 129) / 255f);

    /// <summary> hair.shpk: the hair color, blended toward the highlight color by normal B. </summary>
    public static RgbaImage Hair(RgbaImage normal, PainterCharacterColors colors)
    {
        var result = new RgbaImage(normal.Width, normal.Height);
        var source = normal.Pixels;
        var pixels = result.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var color = Vector3.Lerp(colors.Hair, colors.Highlight, source[i + 2] / 255f);
            Write(pixels, i, color);
        }
        return result;
    }

    /// <summary>
    /// character.shpk's colorset color for each texel of the index texture: index R picks a pair of
    /// rows, and inverted index G blends between them. Colorset colors are linear.
    /// </summary>
    public static RgbaImage ColorSet(RgbaImage index, TexturePlanColorSet colorSet)
    {
        var rows = colorSet.Rows;
        var result = new RgbaImage(index.Width, index.Height);
        var source = index.Pixels;
        var pixels = result.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var pair = (int)Math.Round(source[i] / 17.0, MidpointRounding.ToEven);
            var first = Math.Clamp(pair * 2, 0, rows - 1);
            var second = Math.Min(first + 1, rows - 1);
            var blend = 1f - source[i + 1] / 255f;
            var color = Vector3.Lerp(Row(colorSet, first), Row(colorSet, second), blend);
            Write(pixels, i, new Vector3(ToSrgb(color.X), ToSrgb(color.Y), ToSrgb(color.Z)));
        }
        return result;
    }

    private static Vector3 Row(TexturePlanColorSet colorSet, int row)
    {
        var at = row * colorSet.RowWidth;
        return new Vector3(colorSet.Values[at], colorSet.Values[at + 1], colorSet.Values[at + 2]);
    }

    private static float ToSrgb(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    private static void Write(byte[] pixels, int at, Vector3 color)
    {
        pixels[at] = ToByte(color.X);
        pixels[at + 1] = ToByte(color.Y);
        pixels[at + 2] = ToByte(color.Z);
        pixels[at + 3] = 255;
    }

    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
