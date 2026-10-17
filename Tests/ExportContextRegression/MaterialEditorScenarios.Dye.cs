using System.Numerics;
using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Dye templates (.stm) and applying a stain to a colour row.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunDye()
    {
        CheckStmParse();
        CheckApplyDye();
        CheckGameStm();
    }

    /// <summary>
    /// A Dawntrail template file with one template (1100) whose columns use each storage form: base
    /// colour as a palette, shine as one value, glow left out, the fourth column direct per stain.
    /// </summary>
    private static byte[] SyntheticStm()
    {
        var columns = new List<byte[]>();
        var palette = new List<byte>();
        foreach (var value in new[] { 0.25f, 0.5f, 0.75f, 1f, 0.125f, 0.0625f })
            palette.AddRange(BitConverter.GetBytes((Half)value));
        var indices = new byte[StainTemplates.StainCount];
        indices[0] = 255;
        indices[1] = 1;
        indices[2] = 2;
        indices[253] = 2;
        columns.Add([.. palette, .. indices]);
        columns.Add([.. BitConverter.GetBytes((Half)0.5f), .. BitConverter.GetBytes((Half)0.25f), .. BitConverter.GetBytes((Half)2f)]);
        columns.Add([]);
        columns.Add(Enumerable.Range(0, StainTemplates.StainCount).SelectMany(stain => BitConverter.GetBytes((Half)(stain / 8f))).ToArray());
        for (var i = 4; i < 12; i++)
            columns.Add([]);

        var entry = new List<byte>();
        var end = 0;
        foreach (var column in columns)
        {
            end += column.Length / 2;
            entry.AddRange(BitConverter.GetBytes((ushort)end));
        }
        foreach (var column in columns)
            entry.AddRange(column);

        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes((ushort)0x534D));
        file.AddRange(BitConverter.GetBytes((ushort)0x0201));
        file.AddRange(BitConverter.GetBytes((ushort)1));
        file.AddRange(BitConverter.GetBytes((ushort)0x0903));
        file.AddRange(BitConverter.GetBytes(1100u));
        file.AddRange(BitConverter.GetBytes(0u));
        file.AddRange(entry);
        return file.ToArray();
    }

    private static void CheckStmParse()
    {
        var templates = StainTemplates.Read(SyntheticStm(), null);
        Require(templates.Templates.SequenceEqual([1100]), "the synthetic file has template 1100");
        Require(templates.TryGet(1100, 1, out var one) && one.Diffuse == new Vector3(0.25f, 0.5f, 0.75f) && one.Specular == new Vector3(0.5f, 0.25f, 2f)
                && one.Emissive == Vector3.Zero && one.Exposure == 1 / 8f,
            "stain 1 reads palette entry 1, the one shine value, no glow and its own direct value");
        Require(templates.TryGet(1100, 2, out var two) && two.Diffuse == new Vector3(1f, 0.125f, 0.0625f) && two.Exposure == 2 / 8f
                && templates.TryGet(1100, 253, out var last) && last.Diffuse == two.Diffuse && last.Exposure == 253 / 8f,
            "stains 2 and 253 read palette entry 2 and their direct values");
        Require(templates.TryGet(1100, 0, out var none) && none.Diffuse == Vector3.Zero && !templates.TryGet(1200, 1, out _) && !templates.TryGet(1100, 254, out _),
            "no dye reads nothing, and an unknown template or stain is refused");
    }

    private static void CheckApplyDye()
    {
        var row = DtColorRow.Default with { Diffuse = new Vector3(0.1f), Specular = new Vector3(0.2f), Roughness = 0.5f, Metalness = 0.3f };
        var pack = new DyePack(new Vector3(0.9f, 0.1f, 0.1f), Vector3.Zero, new Vector3(1f), 3f, 1f, 0.25f, 0.4f, 0.6f, 2f, 0.5f, 7, 0.8f);
        var dye = new DtDyeRow(0).WithFlags(0b100001);
        var dyed = StainTemplates.ApplyDye(row, dye, pack);
        Require(dyed == row with { Diffuse = pack.Diffuse, Roughness = 0.25f }, "a dye with base and roughness flags changes only those two fields");
        var shine = StainTemplates.ApplyDye(row, new DtDyeRow(0).WithFlags(0b10), pack);
        Require(shine == row, "a black dye shine leaves the row's shine alone");
        var all = StainTemplates.ApplyDye(row, new DtDyeRow(0).WithFlags(0xFFF), pack with { Specular = new Vector3(0.5f) });
        Require(all.Specular == new Vector3(0.5f) && all.Emissive == Vector3.One && all.Exposure == 3f && all.Metalness == 1f && all.SheenRate == 0.4f
                && all.SheenTint == 0.6f && all.SheenAperture == 2f && all.Anisotropy == 0.5f && all.SphereMapIndex == 7 && all.SphereMapMask == 0.8f,
            "every flag replaces its field");
    }

    /// <summary>The game's own template files, when the game is installed where XIVLauncher says.</summary>
    private static void CheckGameStm()
    {
        var sqpack = PhysicsScenarios.GameSqpack();
        if (sqpack is null)
        {
            Console.WriteLine("[SKIP] dye: the game isn't installed where XIVLauncher says");
            return;
        }
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var gud = game.GetFile(StainTemplates.DawntrailPath)?.Data;
        var legacy = game.GetFile(StainTemplates.LegacyPath)?.Data;
        Require(gud != null && legacy != null, "the game has both dye template files");
        if (gud == null)
            return;
        var templates = StainTemplates.Read(gud, legacy);
        Require(templates.Templates.Count >= 40 && templates.Templates.Contains(1100) && templates.LegacyTemplates.Contains(100),
            $"the game's templates read ({templates.Templates.Count} Dawntrail, {templates.LegacyTemplates.Count} legacy)");
        Require(templates.TryGet(1100, 1, out var pack) && Vector3.Distance(pack.Diffuse, new Vector3(0.7993f, 0.7646f, 0.6655f)) < 1e-3f
                && float.IsFinite(pack.Roughness) && pack.Roughness > 0,
            "template 1100 dyes stain 1 a light base colour with a roughness");
    }
}
