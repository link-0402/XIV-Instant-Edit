using System.Numerics;
using InstantEdit.Services.Materials;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Material editor's colour table: Dawntrail and legacy rows read and written as named
/// fields, the table flags in a material's additional data, and whole tables from real files.
/// </summary>
internal static partial class MaterialEditorScenarios
{
    internal const string MaskPreset = "character/Default Equipment - Mask.mtrl";
    internal const string HairPreset = "hair/Default Hair.mtrl";

    internal static string PresetPath(string relative)
        => Path.Combine(AppContext.BaseDirectory, "Studio", "Resources", "MaterialPresets", relative);

    internal static MtrlInfo Preset(string relative) => MtrlReader.Read(File.ReadAllBytes(PresetPath(relative)));

    public static void RunRows()
    {
        CheckRowRoundTrip();
        CheckLegacyToDawntrail();
        CheckTableFlags();
        CheckTableRead();
    }

    private static void CheckRowRoundTrip()
    {
        var bytes = new byte[64];
        DtColorRow.Default.Write(bytes);
        Require(DtColorRow.Read(bytes) == DtColorRow.Default, "the default Dawntrail row reads back as written");

        var row = new DtColorRow
        {
            Diffuse = new Vector3(0.5f, 0.25f, 0.125f), Scalar3 = 0.75f,
            Specular = new Vector3(2f, 1.5f, 0.0625f), Scalar7 = 3f,
            Emissive = new Vector3(4f, 0.375f, 0.875f), Exposure = 2.5f,
            SheenRate = 0.3125f, SheenTint = 0.4375f, SheenAperture = 6f, Scalar15 = 7f,
            Roughness = 0.5625f, Scalar17 = 8f, Metalness = 0.6875f, Anisotropy = 0.8125f,
            Scalar20 = 9f, SphereMapMask = 0.9375f, Scalar22 = 10f, ScrollLayer = 2f,
            ShaderId = 3, TileIndex = 41, TileAlpha = 0.15625f, SphereMapIndex = 12,
            TileUU = 16f, TileUV = -2f, TileVU = 1.25f, TileVV = 24f,
        };
        row.Write(bytes);
        var back = DtColorRow.Read(bytes);
        Require(back == row && back.TileIndex == 41 && back.ShaderId == 3 && back.SphereMapIndex == 12,
            "every Dawntrail row field round-trips through halves");

        var top = DtColorRow.Default;
        top.TileIndex = 63;
        top.Write(bytes);
        var stored = (float)BitConverter.Int16BitsToHalf(BitConverter.ToInt16(bytes, 25 * 2));
        Require(MathF.Abs(stored - 63.5f / 64f) < 1e-6f && DtColorRow.Read(bytes).TileIndex == 63,
            "tile index 63 is stored as (63 + 0.5) / 64 and reads back as 63");

        var legacyBytes = new byte[32];
        var legacy = new LegacyColorRow
        {
            Diffuse = new Vector3(0.5f, 0.25f, 1f), SpecularMask = 0.75f, Specular = new Vector3(1f, 0.5f, 0.125f),
            Gloss = 20f, Emissive = new Vector3(0.0625f, 0f, 2f), TileIndex = 7,
            TileUU = 16f, TileUV = 0f, TileVU = 0.5f, TileVV = 16f,
        };
        legacy.Write(legacyBytes);
        Require(LegacyColorRow.Read(legacyBytes) == legacy && LegacyColorRow.Read(legacyBytes).TileIndex == 7,
            "every legacy row field round-trips through halves");
    }

    private static void CheckLegacyToDawntrail()
    {
        var legacy = new LegacyColorRow { Diffuse = new Vector3(0.5f), Specular = new Vector3(0.25f), Gloss = 20f, TileIndex = 9, TileUU = 16f, TileVV = 16f };
        var dt = legacy.ToDawntrail();
        Require(MathF.Abs(dt.Roughness - float.Exp2(-20f / 15f)) < 1e-3f && dt.Metalness == 0f && dt.Exposure == 1f
                && dt.Diffuse == legacy.Diffuse && dt.Specular == legacy.Specular && dt.TileIndex == 9
                && dt.SheenRate == DtColorRow.Default.SheenRate,
            "a legacy row with gloss 20 converts to roughness 2^(-20/15), no metal, default sheen");

        legacy.Gloss = 0f;
        Require(MathF.Abs(legacy.ToDawntrail().Roughness - 1f) < 1e-3f, "gloss 0 converts to roughness 1");
    }

    private static void CheckTableFlags()
    {
        var dt = TableFlags.Read([0x3C, 0x05, 0x00, 0x00]);
        Require(dt.Raw == 0x0000053Cu && dt.HasTable && dt.HasDyeTable && dt.WidthLog == 3 && dt.HeightLog == 5 && dt.Kind == ColorTableKind.Dawntrail,
            "flags 0x53C read as a dyeable 8 x 32 Dawntrail table");
        Require(TableFlags.Read([0x0C, 0, 0, 0]).Kind == ColorTableKind.Legacy, "flags 0x0C read as a legacy table");
        Require(TableFlags.Read([0, 0, 0, 0]).Kind == ColorTableKind.None, "flags 0 read as no table");
        Require(TableFlags.Read([0x35, 0xC5, 0x01, 0x00]) is { Kind: ColorTableKind.Dawntrail, HasDyeTable: false },
            "the glasses preset's flags read as a Dawntrail table without dye");

        byte[] additional = [0x35, 0xC5, 0x01, 0x00, 0xAA];
        Require(TableFlags.Read(additional).Write(additional).SequenceEqual(additional), "writing the flags back keeps the same bytes");
        Require(TableFlags.Read([]).Kind == ColorTableKind.None && new TableFlags(0x53C).Write([]).SequenceEqual(new byte[] { 0x3C, 0x05, 0, 0 }),
            "materials without additional data have no table and get four bytes when one is added");
    }

    private static void CheckTableRead()
    {
        var mask = Preset(MaskPreset);
        var table = ColorTable.Read(mask);
        Require(table is { Kind: ColorTableKind.Dawntrail } && table.Rows.Length == 32 && table.Dye is { Length: 32 },
            "the Mask preset reads as a dyeable Dawntrail table of 32 rows");
        Require(table != null && table.WriteDataSet().SequenceEqual(mask.DataSet), "the Mask preset's table writes back byte for byte");
        Require(table != null && table.Flags().Write(mask.AdditionalData).SequenceEqual(mask.AdditionalData), "the Mask preset's table flags write back unchanged");
        Require(ColorTable.Read(Preset(HairPreset)) == null, "the hair preset has no colour table");

        var glasses = Preset("characterglass/Glasses.mtrl");
        var glassTable = ColorTable.Read(glasses);
        Require(glassTable is { Kind: ColorTableKind.Dawntrail, Dye: null } && glassTable.WriteDataSet().SequenceEqual(glasses.DataSet),
            "the glasses preset's undyeable table writes back byte for byte");

        var legacyInfo = mask.Clone();
        legacyInfo.AdditionalData = [0x0C, 0, 0, 0];
        legacyInfo.DataSet = new byte[544];
        for (var i = 0; i < 16; i++)
            new LegacyColorRow { Diffuse = new Vector3(i / 16f), Gloss = i, TileIndex = i, TileUU = 16f, TileVV = 16f }.Write(legacyInfo.DataSet.AsSpan(i * 32, 32));
        BitConverter.TryWriteBytes(legacyInfo.DataSet.AsSpan(512 + 2 * 3), (ushort)((7 << 5) | 0b00011));
        var legacyTable = ColorTable.Read(legacyInfo);
        Require(legacyTable is { Kind: ColorTableKind.Legacy } && legacyTable.LegacyRows.Length == 16 && legacyTable.LegacyDye is { Length: 16 }
                && legacyTable.LegacyDye[3].Template == 7 && legacyTable.LegacyDye[3].Flags == 0b00011
                && legacyTable.WriteDataSet().SequenceEqual(legacyInfo.DataSet),
            "a legacy table with its dye rows reads and writes back byte for byte");

        var converted = legacyTable!.ToDawntrail();
        Require(converted.Kind == ColorTableKind.Dawntrail && converted.Rows.Length == 32
                && converted.Rows[6].Diffuse == new Vector3(3 / 16f) && converted.Rows[7].Diffuse == new Vector3(3 / 16f)
                && converted.Dye is { Length: 32 } && converted.Dye[6].Template == 7 && converted.Dye[7].Template == 7
                && converted.Flags() is { Kind: ColorTableKind.Dawntrail, HasDyeTable: true },
            "converting a legacy table gives each old row both rows of its Dawntrail pair, dye included");
        var blended = legacyTable.ToDawntrail(blendNext: true);
        Require(blended.Rows[6].Diffuse == new Vector3(3 / 16f) && blended.Rows[7].Diffuse == new Vector3(4 / 16f)
                && blended.Dye is { Length: 32 } && blended.Dye[6].Template == 7 && blended.Dye[7].Template == 0 && blended.Dye[5].Template == 7
                && blended.Rows[31].Diffuse == blended.Rows[30].Diffuse,
            "for an index made from the normal map, pair p holds old rows p and p + 1, the last pair its row twice");

        var odd = mask.Clone();
        odd.DataSet = odd.DataSet[..2112];
        var opaque = ColorTable.Read(odd);
        Require(opaque is { Kind: ColorTableKind.Opaque } && opaque.WriteDataSet().SequenceEqual(odd.DataSet),
            "a table whose size does not match its flags is kept as raw bytes");

        var dye = new DtDyeRow(0).WithFlags(0b1_0010_0111).WithTemplate(1234).WithChannel(2);
        Require(dye.Flags == 0b1_0010_0111 && dye.Template == 1234 && dye.Channel == 2 && new DtDyeRow(dye.Raw) == dye,
            "Dawntrail dye rows keep flags, template and channel in their own bits");
    }
}
