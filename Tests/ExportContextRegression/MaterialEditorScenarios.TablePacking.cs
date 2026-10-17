using System.Numerics;
using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The colour table as the live preview hands it to the game: 8 x 32 RGBA halves, with a pulsing highlight on one pair.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunTablePacking()
    {
        CheckTablePacking();
        CheckTableSlotFollowing();
    }

    /// <summary>What a bound table slot does when the slot changes under it (review 2026-10-17: a gear change kept the old rows).</summary>
    private static void CheckTableSlotFollowing()
    {
        const nint ours = 0x10, original = 0x20, game = 0x30;
        Require(TableSlotFollow.Decide(sameMaterial: true, holding: ours, current: ours, original: original) == TableSlotAction.Keep
                && TableSlotFollow.Decide(true, original, 0, original) == TableSlotAction.Keep,
            "a slot holding our rows, or the original while ours aren't in, is kept");
        Require(TableSlotFollow.Decide(true, game, ours, original) == TableSlotAction.Adopt,
            "a new table texture for the same material (a dye change) becomes the original and the rows go back in");
        Require(TableSlotFollow.Decide(false, game, ours, original) == TableSlotAction.Drop
                && TableSlotFollow.Decide(false, original, 0, original) == TableSlotAction.Drop,
            "another material in the place (a gear change) drops the slot, whatever texture it holds");
        Require(TableSlotFollow.Decide(false, ours, ours, original) == TableSlotAction.DropPuttingBack,
            "another material in the place with our rows still in the slot puts the original back first");
    }

    private static void CheckTablePacking()
    {
        var mask = Preset(MaskPreset);
        var table = ColorTable.Read(mask)!;
        var packed = ColorTablePacking.Pack(table, null, 0f);
        var stored = Enumerable.Range(0, 1024).Select(i => BitConverter.ToHalf(mask.DataSet, i * 2)).ToArray();
        Require(packed.Length == 1024 && packed.SequenceEqual(stored), "the Mask preset's table packs to its own 1024 halves");

        var highlight = new RowHighlight(2, 3, new Vector3(1f, 0.5f, 0f), new Vector3(0f, 0.25f, 1f));
        var lit = ColorTablePacking.Pack(table, highlight, 0.25f);
        int[] coloured = [0, 1, 2, 4, 5, 6, 8, 9, 10];
        var mismatch = Enumerable.Range(0, 1024).FirstOrDefault(i => !Expected(i), -1);
        bool Expected(int i)
        {
            var row = i / 32;
            var half = i % 32;
            if (row is 2 or 3 && coloured.Contains(half))
            {
                var colour = row == 2 ? highlight.ColourA : highlight.ColourB;
                var expected = (Half)((half % 4) switch { 0 => colour.X, 1 => colour.Y, _ => colour.Z });
                return lit[i] == expected;
            }
            return lit[i] == packed[i];
        }
        Require(mismatch < 0, "a highlight at t = 0.25 sets rows 2 and 3's base, shine and glow to the pulse colour and nothing else"
                              + (mismatch < 0 ? "" : $" (half {mismatch}: {lit[mismatch]} vs {packed[mismatch]})"));
        Require(ColorTablePacking.Pulse(0.75f) < ColorTablePacking.Pulse(0.25f) && MathF.Abs(ColorTablePacking.Pulse(0.25f) - 1f) < 1e-6f,
            "the pulse peaks at a quarter second and dims at three quarters");

        var legacyInfo = mask.Clone();
        legacyInfo.AdditionalData = [0x04, 0, 0, 0];
        legacyInfo.DataSet = new byte[512];
        for (var i = 0; i < 16; i++)
            new LegacyColorRow { Diffuse = new Vector3(i / 16f), Gloss = 15f, TileUU = 16f, TileVV = 16f }.Write(legacyInfo.DataSet.AsSpan(i * 32, 32));
        var legacy = ColorTablePacking.Pack(ColorTable.Read(legacyInfo)!, null, 0f);
        Require(legacy.Length == 1024 && legacy[7 * 32] == (Half)(3 / 16f) && MathF.Abs((float)legacy[7 * 32 + 16] - 0.5f) < 1e-3f,
            "a legacy table packs as its Dawntrail conversion");
    }
}
