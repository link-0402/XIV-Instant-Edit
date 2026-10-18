using System.Buffers.Binary;
using System.Numerics;

namespace InstantEdit.Services.Materials;

/// <summary>What one stain of a Dawntrail dye template gives the fields a dye row lets it replace.</summary>
public sealed record DyePack(Vector3 Diffuse, Vector3 Specular, Vector3 Emissive, float Exposure, float Metalness, float Roughness, float SheenRate,
    float SheenTint, float SheenAperture, float Anisotropy, int SphereMapIndex, float SphereMapMask);

/// <summary>What one stain of an Endwalker dye template gives a legacy row.</summary>
public sealed record LegacyDyePack(Vector3 Diffuse, Vector3 Specular, Vector3 Emissive, float Gloss, float SpecularMask);

/// <summary>
/// The game's dye templates (stainingtemplate_gud.stm for Dawntrail rows, stainingtemplate.stm for
/// legacy ones), read from the file format: a header (magic "MS", version, template count, then
/// the colour column count in the low byte and the scalar one in the high byte), the template ids, their offsets in 2-byte units, and per
/// template the cumulative end of each column followed by the columns. A column holds nothing (no
/// value), one value for every stain, one value per stain, or a palette and one byte per stain
/// (0 and 255 for no value, k for palette entry k). Stain ids index the per-stain bytes directly.
/// </summary>
public sealed class StainTemplates
{
    public const string DawntrailPath = "chara/base_material/stainingtemplate_gud.stm";
    public const string LegacyPath = "chara/base_material/stainingtemplate.stm";
    public const int StainCount = 254;
    private const ushort Magic = 0x534D;

    private readonly Dictionary<int, float[][]> dawntrail;
    private readonly Dictionary<int, float[][]> legacy;

    private StainTemplates(Dictionary<int, float[][]> dawntrail, Dictionary<int, float[][]> legacy)
    {
        this.dawntrail = dawntrail;
        this.legacy = legacy;
        Templates = dawntrail.Keys.Order().ToArray();
        LegacyTemplates = legacy.Keys.Order().ToArray();
    }

    /// <summary>The Dawntrail template ids (1100 and up).</summary>
    public IReadOnlyList<int> Templates { get; }

    public IReadOnlyList<int> LegacyTemplates { get; }

    public static StainTemplates Read(byte[] gud, byte[]? legacyFile)
        => new(Parse(gud, colours: 3, scalars: 9), legacyFile == null ? [] : Parse(legacyFile, colours: 3, scalars: 2));

    public bool TryGet(int template, int stainId, out DyePack pack)
    {
        pack = null!;
        if (stainId is < 0 or >= StainCount || !dawntrail.TryGetValue(template, out var columns))
            return false;
        Vector3 Colour(int column) => new(columns[column][stainId * 3], columns[column][stainId * 3 + 1], columns[column][stainId * 3 + 2]);
        float Scalar(int column) => columns.Length > column ? columns[column][stainId] : 0f;
        pack = new DyePack(Colour(0), Colour(1), Colour(2), Scalar(3), Scalar(4), Scalar(5), Scalar(6), Scalar(7), Scalar(8), Scalar(9),
            (int)MathF.Round(Scalar(10)), Scalar(11));
        return true;
    }

    public bool TryGetLegacy(int template, int stainId, out LegacyDyePack pack)
    {
        pack = null!;
        if (stainId is < 0 or >= StainCount || !legacy.TryGetValue(template, out var columns))
            return false;
        Vector3 Colour(int column) => new(columns[column][stainId * 3], columns[column][stainId * 3 + 1], columns[column][stainId * 3 + 2]);
        pack = new LegacyDyePack(Colour(0), Colour(1), Colour(2), columns[3][stainId], columns[4][stainId]);
        return true;
    }

    /// <summary>
    /// The row with the dye's flagged fields replaced by the stain's values (bits: base, shine, glow,
    /// exposure, metalness, roughness, sheen, sheen tint, sheen spread, anisotropy, reflection map,
    /// reflection strength). A black shine leaves the row's own.
    /// </summary>
    public static DtColorRow ApplyDye(DtColorRow row, DtDyeRow dye, DyePack pack)
    {
        bool On(int bit) => (dye.Flags & (1 << bit)) != 0;
        if (On(0)) row.Diffuse = pack.Diffuse;
        if (On(1) && pack.Specular != Vector3.Zero) row.Specular = pack.Specular;
        if (On(2)) row.Emissive = pack.Emissive;
        if (On(3)) row.Exposure = pack.Exposure;
        if (On(4)) row.Metalness = pack.Metalness;
        if (On(5)) row.Roughness = pack.Roughness;
        if (On(6)) row.SheenRate = pack.SheenRate;
        if (On(7)) row.SheenTint = pack.SheenTint;
        if (On(8)) row.SheenAperture = pack.SheenAperture;
        if (On(9)) row.Anisotropy = pack.Anisotropy;
        if (On(10)) row.SphereMapIndex = pack.SphereMapIndex;
        if (On(11)) row.SphereMapMask = pack.SphereMapMask;
        return row;
    }

    /// <summary>
    /// A 32-row row on characterlegacy.shpk dyed: it takes legacy templates, and bits 3 and 4 dye the
    /// gloss (half 3) and the shine strength (half 7). A black shine leaves the row's own.
    /// </summary>
    public static DtColorRow ApplyLegacyDye(DtColorRow row, DtDyeRow dye, LegacyDyePack pack)
    {
        bool On(int bit) => (dye.Flags & (1 << bit)) != 0;
        if (On(0)) row.Diffuse = pack.Diffuse;
        if (On(1) && pack.Specular != Vector3.Zero) row.Specular = pack.Specular;
        if (On(2)) row.Emissive = pack.Emissive;
        if (On(3)) row.Scalar3 = pack.Gloss;
        if (On(4)) row.Scalar7 = pack.SpecularMask;
        return row;
    }

    /// <summary>
    /// A copy of a Dawntrail-sized table with each dyed row dyed by the stain its channel picks (0 = no
    /// dye); <paramref name="legacyShader"/> for characterlegacy.shpk, which dyes with the legacy templates.
    /// </summary>
    public ColorTable Dyed(ColorTable table, IReadOnlyList<int> stains, bool legacyShader = false)
    {
        if (table is not { Kind: ColorTableKind.Dawntrail, Dye: { } dyeRows })
            return table;
        var copy = table.Clone();
        for (var i = 0; i < copy.Rows.Length; i++)
        {
            var dye = dyeRows[i];
            var stain = dye.Channel < stains.Count ? stains[dye.Channel] : 0;
            if (dye.Flags == 0 || stain <= 0)
                continue;
            if (legacyShader && TryGetLegacy(dye.Template, stain, out var legacy))
                copy.Rows[i] = ApplyLegacyDye(copy.Rows[i], dye, legacy);
            else if (!legacyShader && TryGet(dye.Template, stain, out var pack))
                copy.Rows[i] = ApplyDye(copy.Rows[i], dye, pack);
        }
        return copy;
    }

    private static Dictionary<int, float[][]> Parse(byte[] file, int colours, int scalars)
    {
        if (file.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(file) != Magic)
            throw new InvalidDataException("This is not a dye template file.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        var counts = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        if (version >= 0x0200 && counts != 0)
            (colours, scalars) = (counts & 0xFF, counts >> 8);
        var widths = Enumerable.Repeat(3, colours).Concat(Enumerable.Repeat(1, scalars)).ToArray();
        var dataStart = 8 + count * 8;
        if (dataStart > file.Length)
            throw new InvalidDataException("The dye template file is cut short.");
        var templates = new Dictionary<int, float[][]>();
        for (var t = 0; t < count; t++)
        {
            var key = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8 + t * 4));
            var start = dataStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8 + count * 4 + t * 4)) * 2;
            var columnsStart = start + widths.Length * 2;
            if (columnsStart > file.Length)
                throw new InvalidDataException($"Dye template {key} is cut short.");
            var columns = new float[widths.Length][];
            var previous = 0;
            for (var c = 0; c < widths.Length; c++)
            {
                var end = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(start + c * 2));
                var from = columnsStart + previous * 2;
                var to = columnsStart + end * 2;
                if (end < previous || to > file.Length)
                    throw new InvalidDataException($"Dye template {key} has a broken column.");
                columns[c] = Column(file.AsSpan(from, to - from), widths[c]);
                previous = end;
            }
            templates[key] = columns;
        }
        return templates;
    }

    /// <summary>One column as <see cref="StainCount"/> values of <paramref name="width"/> floats; stains without a value read 0.</summary>
    private static float[] Column(ReadOnlySpan<byte> data, int width)
    {
        var values = new float[StainCount * width];
        var size = width * 2;
        if (data.Length == 0)
            return values;
        if (data.Length == size)
        {
            for (var stain = 0; stain < StainCount; stain++)
                for (var k = 0; k < width; k++)
                    values[stain * width + k] = Half(data, k);
            return values;
        }
        if (data.Length == size * StainCount)
        {
            for (var i = 0; i < values.Length; i++)
                values[i] = Half(data, i);
            return values;
        }
        var paletteBytes = data.Length - StainCount;
        if (paletteBytes < 0 || paletteBytes % size != 0)
            throw new InvalidDataException("A dye template column has an unknown size.");
        var palette = paletteBytes / size;
        var indices = data[paletteBytes..];
        for (var stain = 0; stain < StainCount; stain++)
        {
            var index = indices[stain];
            if (index == 0 || index == byte.MaxValue || index > palette)
                continue;
            for (var k = 0; k < width; k++)
                values[stain * width + k] = Half(data, (index - 1) * width + k);
        }
        return values;
    }

    private static float Half(ReadOnlySpan<byte> data, int index) => (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(data[(index * 2)..]));
}
