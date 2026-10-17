using System.Numerics;

namespace InstantEdit.Services.Materials;

/// <summary>A pair of rows lit up on the character while its swatch is hovered.</summary>
public readonly record struct RowHighlight(int RowA, int RowB, Vector3 ColourA, Vector3 ColourB);

/// <summary>
/// The colour table as the game's table texture holds it: 32 rows of 8 RGBA half texels (the
/// Dawntrail row layout), legacy tables converted first.
/// </summary>
public enum TableSlotAction
{
    Keep,
    /// <summary>The game made a new table texture for the same material (a dye change): follow it.</summary>
    Adopt,
    /// <summary>Another material sits in the place now (a gear change): let go of the slot.</summary>
    Drop,
    /// <summary>As <see cref="Drop"/>, but our texture is still in the slot, so the original goes back first.</summary>
    DropPuttingBack,
}

/// <summary>What a bound colour-table slot does when the texture or material in its place changed.</summary>
public static class TableSlotFollow
{
    public static TableSlotAction Decide(bool sameMaterial, nint holding, nint current, nint original)
    {
        if (!sameMaterial)
            return current != 0 && holding == current ? TableSlotAction.DropPuttingBack : TableSlotAction.Drop;
        return holding == current || (current == 0 && holding == original) ? TableSlotAction.Keep : TableSlotAction.Adopt;
    }
}

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
