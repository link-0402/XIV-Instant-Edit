using InstantEdit.Services.Materials;
using InstantEdit.Services.Previews;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Which colour rows an index texture selects, and how much of the texture uses each.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunUsage()
    {
        CheckUsage();
        CheckLegacyIndexTexture();
        CheckNormalBesideMaterial();
    }

    /// <summary>
    /// The index for a converted mod material comes from that mod's normal map, from the option the
    /// material sits in; the character's collection may load another one (in game 2026-10-17 a mod
    /// window's copy got the vanilla normal map, all alpha 255).
    /// </summary>
    private static void CheckNormalBesideMaterial()
    {
        const string material = "chara/equipment/e5033/material/v0008/mt_c0101e5033_met_a.mtrl";
        const string normal = "chara/equipment/e5033/texture/v01_c0101e5033_met_n.tex";
        var root = Path.Combine(Path.GetTempPath(), "ie-material-beside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var meta = new System.Text.Json.Nodes.JsonObject
            {
                ["FileVersion"] = 4, ["Name"] = "Halo",
                ["DefaultData"] = new System.Text.Json.Nodes.JsonObject { ["Files"] = new System.Text.Json.Nodes.JsonObject { [normal] = "base\\n.tex" } },
                ["Groups"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["Type"] = "Single", ["Name"] = "Halo",
                    ["Options"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject { ["Name"] = "Red", ["Files"] = new System.Text.Json.Nodes.JsonObject { [material] = "red\\a.mtrl", [normal] = "red\\n.tex" } },
                        new System.Text.Json.Nodes.JsonObject { ["Name"] = "Blue", ["Files"] = new System.Text.Json.Nodes.JsonObject { [material] = "blue/a.mtrl" } }),
                }),
            };
            File.WriteAllText(Path.Combine(root, "meta.json"), meta.ToJsonString());
            Require(InstantEdit.Services.PenumbraMods.ModMetadata.MappedFileBeside(root, material, "red/a.mtrl", normal) == "red/n.tex"
                    && InstantEdit.Services.PenumbraMods.ModMetadata.MappedFileBeside(root, material, "blue/a.mtrl", normal) == "base/n.tex"
                    && InstantEdit.Services.PenumbraMods.ModMetadata.MappedFileBeside(root, material, "red/a.mtrl", "chara/x.tex") == null
                    && InstantEdit.Services.PenumbraMods.ModMetadata.MappedFileBeside(Path.Combine(root, "missing"), material, "red/a.mtrl", normal) == null,
                "a mod material's texture comes from its own option, else the mod's first mapping, else nothing");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Converting a legacy table needs an index texture, or the game picks one pair everywhere (review
    /// 2026-10-17). Legacy shaders pick the row with the normal map's alpha; the built index sends each
    /// texel to the top row of the pair holding that legacy row.
    /// </summary>
    private static void CheckLegacyIndexTexture()
    {
        byte[] alphas = [0, 17, 34, 120, 255, 255, 34, 25];
        var bgra = new byte[alphas.Length * 4];
        for (var i = 0; i < alphas.Length; i++)
            (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2], bgra[i * 4 + 3]) = ((byte)128, (byte)128, (byte)255, alphas[i]);
        var normal = new DecodedTexture(bgra, 4, 2, 4, 2, 0, 1);
        var tex = LegacyIndexTexture.FromNormal(normal);
        var index = TextureDecoder.Decode(tex, int.MaxValue);
        var usage = IndexUsageCalc.Compute(index, ColorTableKind.Dawntrail);
        Require(index.Width == 4 && index.Height == 2 && usage.RowOfTexel.SequenceEqual(new byte[] { 0, 2, 4, 14, 30, 30, 4, 2 }),
            "the index built from the normal map's alpha picks the top row of each legacy row's pair (alpha 120 is row 7)");
        // Alpha 25 sits about half way from legacy row 1 to row 2: pair 1 (old rows 1 and 2 after a blended
        // conversion), old row 1 getting 1 - (25/17 - 1), about 53 %.
        var between = index.Bgra[7 * 4 + 1];
        Require(index.Bgra[7 * 4 + 2] == 17 && Math.Abs(between - 135) <= 1,
            "an alpha between two legacy rows blends them through the green channel, as the legacy shader does");

        // The made index goes where nothing is yet: a Dawntrail game texture named like it, or a path the mod maps, is passed over.
        const string derived = "chara/equipment/e6001/texture/v01_c0201e6001_top_id.tex";
        HashSet<string> taken = [derived, "chara/equipment/e6001/texture/v01_c0201e6001_top_id_ie.tex"];
        Require(InstantEdit.Services.Materials.MaterialSession.FreeTexturePath(derived, taken.Contains) == "chara/equipment/e6001/texture/v01_c0201e6001_top_id_ie2.tex"
                && InstantEdit.Services.Materials.MaterialSession.FreeTexturePath(derived, _ => false) == derived,
            "a made index takes the derived path only while nothing uses it, else the first free _ie name beside it");
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

        // Pair 1 on the left, pair 16 on the right, a BC-style ramp column between them (red 128 is
        // pair 9), and a 3 x 3 patch of pair 5 whose middle texel's neighbours agree.
        var edge = Solid(12, 0, 0);
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++)
            {
                var red = x == 6 ? 128 : x > 6 ? 255 : x is >= 1 and <= 3 && y is >= 1 and <= 3 ? 68 : 0;
                edge.Bgra[(y * 12 + x) * 4 + 2] = (byte)red;
            }
        var noisy = IndexUsageCalc.Compute(edge, ColorTableKind.Dawntrail);
        Require(Used(noisy).SequenceEqual([1, 9, 31]) && noisy.RowShare[17] > 0,
            "a ramp texel between two pairs doesn't make its pair used, though it still counts in the shares; a 3 x 3 patch does");
    }
}
