using System.Numerics;

namespace InstantEdit.Services.Materials;

/// <summary>A pair of rows lit up on the character while its swatch is hovered.</summary>
public readonly record struct RowHighlight(int RowA, int RowB, Vector3 ColourA, Vector3 ColourB);

/// <summary>
/// The colour table as the game's table texture holds it: 32 rows of 8 RGBA half texels (the
/// Dawntrail row layout), legacy tables converted first.
/// </summary>
public static class ColorTablePacking
{
    public const int Width = 8;
    public const int Height = ColorTable.DtRowCount;
    public const int Halves = Width * Height * 4;

    private static readonly int[] Coloured = [0, 1, 2, 4, 5, 6, 8, 9, 10];

    /// <summary>The highlight's brightness over time: (sin(2 pi t) + 2) / 3, between a third and one.</summary>
    public static float Pulse(float seconds) => (MathF.Sin(2 * MathF.PI * seconds) + 2) / 3;

    public static Half[] Pack(ColorTable table, RowHighlight? highlight, float seconds)
    {
        if (table.Kind == ColorTableKind.Legacy)
            table = table.ToDawntrail();
        if (table.Kind != ColorTableKind.Dawntrail)
            throw new InvalidOperationException("Only Dawntrail and legacy tables can be shown.");
        var bytes = table.WriteDataSet();
        var halves = new Half[Halves];
        for (var i = 0; i < Halves; i++)
            halves[i] = BitConverter.ToHalf(bytes, i * 2);
        if (highlight is not { } lit)
            return halves;
        var pulse = Pulse(seconds);
        Light(halves, lit.RowA, lit.ColourA * pulse);
        Light(halves, lit.RowB, lit.ColourB * pulse);
        return halves;
    }

    /// <summary>Sets a row's base, shine and glow colour to <paramref name="colour"/>.</summary>
    private static void Light(Half[] halves, int row, Vector3 colour)
    {
        if (row is < 0 or >= Height)
            return;
        foreach (var half in Coloured)
            halves[row * 32 + half] = (Half)((half % 4) switch { 0 => colour.X, 1 => colour.Y, _ => colour.Z });
    }
}
