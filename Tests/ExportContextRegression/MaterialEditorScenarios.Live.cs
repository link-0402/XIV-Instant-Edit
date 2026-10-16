using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The pure parts of the live preview: finding the loaded material by its path, and what an edit has to write.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunLive()
    {
        CheckPathMatch();
        CheckLiveDiff();
    }

    private static void CheckPathMatch()
    {
        const string game = "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl";
        Require(MaterialPathMatch.Matches("|abc_12_3_4567|chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl", game.ToUpperInvariant()),
            "a loaded material's path matches with Penumbra's collection prefix and in any case");
        Require(MaterialPathMatch.Matches(@"chara\equipment\e0085\material\v0002\mt_c0201e0085_glv_a.mtrl", game) && MaterialPathMatch.Matches(game, "/" + game),
            "backslashes and a leading slash don't matter");
        Require(!MaterialPathMatch.Matches("|x|chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_b.mtrl", game) && !MaterialPathMatch.Matches("", game),
            "another file does not match");
    }

    private static void CheckLiveDiff()
    {
        var mask = PresetDocument(MaskPreset);
        var after = mask.WithConstant(0x29AC0223, [0.25f]).WithFlags(mask.ShaderFlags | 0x10).WithSlot(1, mask.Slots[1].WithWrap(2, 2));
        var diff = MaterialSession.LiveDiff(mask, after);
        Require(diff.Constants.SequenceEqual([0x29AC0223u]) && diff.Flags && diff.Samplers.SequenceEqual([0x8A4E82B6u]) && !diff.Table && diff.Keys.Length == 0,
            "the live diff names the changed constant, the flags and the changed sampler");

        var table = mask.Table!.Clone();
        table.Rows[5].Metalness = 1f;
        var keyed = mask.WithKey(0xD2777173, 0x4242B842).WithTable(table).WithoutConstant(0xD62BF368);
        var second = MaterialSession.LiveDiff(mask, keyed);
        Require(second.Table && second.Keys.SequenceEqual([0xD2777173u]) && second.Constants.SequenceEqual([0xD62BF368u]) && !second.Flags && second.Samplers.Length == 0,
            "a row, a key and a removed constant show up in the live diff");
        Require(MaterialSession.LiveDiff(mask, mask) is { Constants.Length: 0, Keys.Length: 0, Samplers.Length: 0, Flags: false, Table: false },
            "an unchanged document needs no writes");
    }
}
