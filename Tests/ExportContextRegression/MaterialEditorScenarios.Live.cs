using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The pure parts of the live preview: finding the loaded material by its path, and what an edit has to write.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunLive()
    {
        CheckPathMatch();
        CheckLiveDiff();
        CheckSnapshotsOutliveBindings();
    }

    private static void CheckSnapshotsOutliveBindings()
    {
        // After a zone change the game handed the same material, still holding live edits, to a new draw object (2026-10-17).
        var snapshots = new MaterialSnapshots<string>();
        var reads = 0;
        string Read(string value) { reads++; return value; }
        Require(snapshots.Take(0x100, 0x900, () => Read("own")) == "own" && reads == 1, "a newly bound material is read once");
        Require(snapshots.Take(0x100, 0x900, () => Read("edited")) == "own" && reads == 1, "binding it again keeps the first snapshot");
        snapshots.Retire([]);
        Require(!snapshots.TryGetBound(0x100, out _) && snapshots.Bound.Count == 0, "a material whose binding went stale is no longer bound");
        Require(snapshots.Take(0x100, 0x900, () => Read("edited")) == "own" && reads == 1,
            "the same material with the same file, bound again after its binding went stale, gets its first snapshot back");
        snapshots.Retire([]);
        Require(snapshots.Take(0x100, 0x901, () => Read("other file")) == "other file" && reads == 2, "the same address with another file is a new material");
        snapshots.Retire([0x100]);
        Require(snapshots.TryGetBound(0x100, out var kept) && kept == "other file", "a kept material stays bound");
        for (var i = 0; i < 40; i++)
        {
            snapshots.Take(0x1000 + i, 0x900, () => "x");
            snapshots.Retire([]);
        }
        Require(snapshots.RetiredCount <= MaterialSnapshots<string>.RetiredLimit, "retired snapshots are capped");
        snapshots.Clear();
        Require(snapshots.Bound.Count == 0 && snapshots.RetiredCount == 0, "Clear forgets bound and retired snapshots");
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

        // A mod's material is loaded under its file on disk, as read from the game (2026-10-17).
        const string dress = "chara/equipment/e6059/material/v0001/mt_c0201e6059_top_dress.mtrl";
        const string handle = "|2_2_1D79C9AE_0076|G:/Penumbra/Critique/chara/equipment/e6059/material/v0001/mt_c0201e6059_top_dress.mtrl";
        var modFile = MaterialPathMatch.Candidates(dress, @"G:\Penumbra\Critique\chara\equipment\e6059\material\v0001\mt_c0201e6059_top_dress.mtrl", null);
        Require(MaterialPathMatch.MatchesAny(handle, modFile), "a mod's material matches by the mod file it was loaded from");
        Require(!MaterialPathMatch.MatchesAny(handle, MaterialPathMatch.Candidates(dress, dress, null)), "a game file's candidates don't match a mod file");
        var preview = MaterialPathMatch.Candidates(dress, @"G:\Penumbra\Critique\x.mtrl", @"C:\Cache\material-preview\0123456789abcdef\2\" + dress.Replace('/', '\\'));
        Require(MaterialPathMatch.MatchesAny("|1_2_3|C:/Cache/material-preview/0123456789abcdef/2/" + dress, preview),
            "the structural preview's file matches while it is shown");
        Require(modFile.Count == 2 && MaterialPathMatch.Candidates(dress, "", " ").Count == 1, "empty paths are left out");
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
