using InstantEdit.Services.Materials;
using InstantEdit.Services.Previews;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Which colour rows an index texture selects, and how much of the texture uses each.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunUsage()
    {
        CheckUsage();
    }

    /// <summary>A BGRA image of one colour: the index texture's red picks the pair, green blends its rows.</summary>
    private static DecodedTexture Solid(int size, byte red, byte green)
    {
        var bgra = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            bgra[i * 4] = 0;
            bgra[i * 4 + 1] = green;
            bgra[i * 4 + 2] = red;
            bgra[i * 4 + 3] = 255;
        }
        return new DecodedTexture(bgra, size, size, size, size, 0, 1);
    }

    private static int[] Used(IndexUsage usage) => Enumerable.Range(0, usage.RowUsed.Length).Where(row => usage.RowUsed[row]).ToArray();

    private static void CheckUsage()
    {
        var bottom = IndexUsageCalc.Compute(Solid(4, 0, 0), ColorTableKind.Dawntrail);
        Require(Used(bottom).SequenceEqual([1]) && MathF.Abs(bottom.RowShare[1] - 1f) < 1e-6f && bottom.RowShare.Length == 32,
            "red 0 and green 0 select only row 2 (the bottom of pair 1), on every texel");

        var blend = IndexUsageCalc.Compute(Solid(4, 17, 128), ColorTableKind.Dawntrail);
        Require(Used(blend).SequenceEqual([2, 3]) && MathF.Abs(blend.RowShare[2] - 0.5f) < 0.01f && MathF.Abs(blend.RowShare[3] - 0.5f) < 0.01f,
            "red 17 and green 128 split pair 2's rows about half and half");

        var last = IndexUsageCalc.Compute(Solid(4, 255, 200), ColorTableKind.Dawntrail);
        Require(Used(last).SequenceEqual([30, 31]), "red 255 selects the last pair, rows 31 and 32");

        var legacy = IndexUsageCalc.Compute(Solid(4, 34, 77), ColorTableKind.Legacy);
        Require(legacy.RowShare.Length == 16 && Used(legacy).SequenceEqual([2]), "on a legacy table red 34 selects row 3 alone");

        var placeholder = IndexUsageCalc.Compute(Solid(32, 51, 128), ColorTableKind.Dawntrail);
        Require(Used(placeholder).Length == 2 && placeholder.Width == 32 && placeholder.RowOfTexel.Length == 32 * 32 && placeholder.RowOfTexel.All(row => row == 6),
            "a 32 x 32 one-colour placeholder index uses exactly two rows, and each texel names its heavier row");

        var pure = IndexUsageCalc.Compute(Solid(4, 34, 255), ColorTableKind.Dawntrail);
        Require(Used(pure).SequenceEqual([4]), "green 255 gives the top row all of the weight, so the bottom row stays unused");
    }
}
