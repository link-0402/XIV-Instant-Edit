using System.Buffers.Binary;
using System.Numerics;
using InstantEdit.Studio.Services;

namespace InstantEdit.Services.Materials;

/// <summary>What a material's data set holds, from the table flags in its additional data.</summary>
public enum ColorTableKind
{
    None,
    Legacy,
    Dawntrail,

    /// <summary>A table whose flags or size the editor does not know; kept as raw bytes.</summary>
    Opaque,
}

/// <summary>
/// The first four bytes of a material's additional data: bit 2 has a colour table, bit 3 has a dye
/// table, bits 4-7 and 8-11 the table texture's width and height as powers of two (8 x 32 for
/// Dawntrail, 4 x 16 or 0 for the Endwalker layout). Other bits are kept as they are.
/// </summary>
public readonly record struct TableFlags(uint Raw)
{
    public bool HasTable => (Raw & 0x4) != 0;
    public bool HasDyeTable => (Raw & 0x8) != 0;
    public int WidthLog => (int)((Raw >> 4) & 0xF);
    public int HeightLog => (int)((Raw >> 8) & 0xF);

    public ColorTableKind Kind => !HasTable ? ColorTableKind.None : (WidthLog, HeightLog) switch
    {
        (3, 5) => ColorTableKind.Dawntrail,
        (0, 0) or (2, 4) => ColorTableKind.Legacy,
        _ => ColorTableKind.Opaque,
    };

    public static TableFlags Read(byte[] additionalData)
    {
        Span<byte> first = stackalloc byte[4];
        additionalData.AsSpan(0, Math.Min(4, additionalData.Length)).CopyTo(first);
        return new TableFlags(BinaryPrimitives.ReadUInt32LittleEndian(first));
    }

    /// <summary>A copy of the additional data with these flags in its first four bytes (grown to four when shorter).</summary>
    public byte[] Write(byte[] additionalData)
    {
        var result = new byte[Math.Max(4, additionalData.Length)];
        additionalData.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result, Raw);
        return result;
    }

    public TableFlags WithTable(bool on) => new(on ? Raw | 0x4 : Raw & ~0x4u);
    public TableFlags WithDyeTable(bool on) => new(on ? Raw | 0x8 : Raw & ~0x8u);
    public TableFlags WithLogs(int width, int height) => new((Raw & ~0xFF0u) | (uint)(width & 0xF) << 4 | (uint)(height & 0xF) << 8);
}

/// <summary>Half-float packing shared by the row types.</summary>
internal static class HalfPacking
{
    public static float Get(ReadOnlySpan<byte> row, int half)
        => (float)BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(row[(half * 2)..]));

    public static void Set(Span<byte> row, int half, float value)
        => BinaryPrimitives.WriteInt16LittleEndian(row[(half * 2)..], BitConverter.HalfToInt16Bits((Half)value));

    public static Vector3 Get3(ReadOnlySpan<byte> row, int half) => new(Get(row, half), Get(row, half + 1), Get(row, half + 2));

    public static void Set3(Span<byte> row, int half, Vector3 value)
    {
        Set(row, half, value.X);
        Set(row, half + 1, value.Y);
        Set(row, half + 2, value.Z);
    }

    /// <summary>The value a half stores for <paramref name="value"/>, so defaults compare equal after a round trip.</summary>
    public static float Quantize(float value) => (float)(Half)value;

    /// <summary>Tile indices are stored as (index + 0.5) / 64 so any reader that rounds or floors gets the same tile.</summary>
    public static float TileValue(int index) => Quantize((Math.Clamp(index, 0, 63) + 0.5f) / 64f);

    public static int TileIndex(float value) => Math.Clamp((int)MathF.Floor(value * 64f), 0, 63);
}

/// <summary>
/// One Dawntrail colour-table row: 32 halves (64 bytes). Pairs of rows are picked by the index
/// texture's red channel and blended by its green channel.
/// </summary>
public record struct DtColorRow
{
    public const int Size = 64;

    public Vector3 Diffuse;
    public float Scalar3;
    public Vector3 Specular;
    public float Scalar7;
    public Vector3 Emissive;
    public float Exposure;
    public float SheenRate;
    public float SheenTint;
    public float SheenAperture;
    public float Scalar15;
    public float Roughness;
    public float Scalar17;
    public float Metalness;
    public float Anisotropy;
    public float Scalar20;
    public float SphereMapMask;
    public float Scalar22;
    public float ScrollLayer;

    /// <summary>The stored halves behind <see cref="ShaderId"/>, <see cref="TileIndex"/> and <see cref="SphereMapIndex"/>, kept exact for round trips.</summary>
    public float ShaderIdValue;
    public float TileIndexValue;
    public float TileAlpha;
    public float SphereMapIndexValue;
    public float TileUU;
    public float TileUV;
    public float TileVU;
    public float TileVV;

    public int ShaderId
    {
        readonly get => (int)MathF.Round(ShaderIdValue);
        set => ShaderIdValue = value;
    }

    public int TileIndex
    {
        readonly get => HalfPacking.TileIndex(TileIndexValue);
        set => TileIndexValue = HalfPacking.TileValue(value);
    }

    public int SphereMapIndex
    {
        readonly get => (int)MathF.Round(SphereMapIndexValue);
        set => SphereMapIndexValue = value;
    }

    /// <summary>The values the game ships in an unused row.</summary>
    public static DtColorRow Default => new()
    {
        Diffuse = Vector3.One, Scalar3 = 1f, Specular = Vector3.One, Emissive = Vector3.Zero, Exposure = 1f,
        SheenRate = HalfPacking.Quantize(0.1f), SheenTint = HalfPacking.Quantize(0.2f), SheenAperture = 5f,
        Roughness = 0.5f, TileIndex = 0, TileAlpha = 1f, TileUU = 16f, TileVV = 16f,
    };

    public static DtColorRow Read(ReadOnlySpan<byte> row)
    {
        var h = HalfPacking.Get;
        return new DtColorRow
        {
            Diffuse = HalfPacking.Get3(row, 0), Scalar3 = h(row, 3),
            Specular = HalfPacking.Get3(row, 4), Scalar7 = h(row, 7),
            Emissive = HalfPacking.Get3(row, 8), Exposure = h(row, 11),
            SheenRate = h(row, 12), SheenTint = h(row, 13), SheenAperture = h(row, 14), Scalar15 = h(row, 15),
            Roughness = h(row, 16), Scalar17 = h(row, 17), Metalness = h(row, 18), Anisotropy = h(row, 19),
            Scalar20 = h(row, 20), SphereMapMask = h(row, 21), Scalar22 = h(row, 22), ScrollLayer = h(row, 23),
            ShaderIdValue = h(row, 24), TileIndexValue = h(row, 25), TileAlpha = h(row, 26), SphereMapIndexValue = h(row, 27),
            TileUU = h(row, 28), TileUV = h(row, 29), TileVU = h(row, 30), TileVV = h(row, 31),
        };
    }

    public readonly void Write(Span<byte> row)
    {
        HalfPacking.Set3(row, 0, Diffuse);
        HalfPacking.Set(row, 3, Scalar3);
        HalfPacking.Set3(row, 4, Specular);
        HalfPacking.Set(row, 7, Scalar7);
        HalfPacking.Set3(row, 8, Emissive);
        ReadOnlySpan<float> rest =
        [
            Exposure, SheenRate, SheenTint, SheenAperture, Scalar15, Roughness, Scalar17, Metalness, Anisotropy,
            Scalar20, SphereMapMask, Scalar22, ScrollLayer, ShaderIdValue, TileIndexValue, TileAlpha, SphereMapIndexValue,
            TileUU, TileUV, TileVU, TileVV,
        ];
        for (var i = 0; i < rest.Length; i++)
            HalfPacking.Set(row, 11 + i, rest[i]);
    }
}

/// <summary>One Endwalker colour-table row: 16 halves (32 bytes).</summary>
public record struct LegacyColorRow
{
    public const int Size = 32;

    public Vector3 Diffuse;
    public float SpecularMask;
    public Vector3 Specular;
    public float Gloss;
    public Vector3 Emissive;
    public float TileIndexValue;
    public float TileUU;
    public float TileUV;
    public float TileVU;
    public float TileVV;

    public int TileIndex
    {
        readonly get => HalfPacking.TileIndex(TileIndexValue);
        set => TileIndexValue = HalfPacking.TileValue(value);
    }

    public static LegacyColorRow Read(ReadOnlySpan<byte> row) => new()
    {
        Diffuse = HalfPacking.Get3(row, 0), SpecularMask = HalfPacking.Get(row, 3),
        Specular = HalfPacking.Get3(row, 4), Gloss = HalfPacking.Get(row, 7),
        Emissive = HalfPacking.Get3(row, 8), TileIndexValue = HalfPacking.Get(row, 11),
        TileUU = HalfPacking.Get(row, 12), TileUV = HalfPacking.Get(row, 13), TileVU = HalfPacking.Get(row, 14), TileVV = HalfPacking.Get(row, 15),
    };

    public readonly void Write(Span<byte> row)
    {
        HalfPacking.Set3(row, 0, Diffuse);
        HalfPacking.Set(row, 3, SpecularMask);
        HalfPacking.Set3(row, 4, Specular);
        HalfPacking.Set(row, 7, Gloss);
        HalfPacking.Set3(row, 8, Emissive);
        HalfPacking.Set(row, 11, TileIndexValue);
        HalfPacking.Set(row, 12, TileUU);
        HalfPacking.Set(row, 13, TileUV);
        HalfPacking.Set(row, 14, TileVU);
        HalfPacking.Set(row, 15, TileVV);
    }

    /// <summary>The same look in the Dawntrail layout: gloss becomes roughness 2^(-gloss/15), no metal, default sheen.</summary>
    public readonly DtColorRow ToDawntrail()
    {
        var row = DtColorRow.Default;
        row.Diffuse = Diffuse;
        row.Specular = Specular;
        row.Emissive = Emissive;
        row.Roughness = float.Exp2(-Gloss / 15f);
        row.Metalness = 0f;
        row.TileIndexValue = TileIndexValue;
        row.TileUU = TileUU;
        row.TileUV = TileUV;
        row.TileVU = TileVU;
        row.TileVV = TileVV;
        return row;
    }
}

/// <summary>
/// A Dawntrail dye row: bits 0-11 say which fields the dye replaces (diffuse, specular, emissive,
/// exposure, metalness, roughness, sheen rate, sheen tint, sheen aperture, anisotropy, sphere-map
/// index, sphere-map mask), bits 16-26 the template, bits 27-28 the dye channel.
/// </summary>
public readonly record struct DtDyeRow(uint Raw)
{
    public const int Size = 4;

    public ushort Flags => (ushort)(Raw & 0xFFF);
    public int Template => (int)((Raw >> 16) & 0x7FF);
    public int Channel => (int)((Raw >> 27) & 0x3);

    public DtDyeRow WithFlags(ushort flags) => new((Raw & ~0xFFFu) | (flags & 0xFFFu));
    public DtDyeRow WithTemplate(int template) => new((Raw & ~(0x7FFu << 16)) | ((uint)template & 0x7FF) << 16);
    public DtDyeRow WithChannel(int channel) => new((Raw & ~(0x3u << 27)) | ((uint)channel & 0x3) << 27);
}

/// <summary>An Endwalker dye row: bits 0-4 are the dyed fields (diffuse, specular, emissive, gloss, specular mask), the rest the template.</summary>
public readonly record struct LegacyDyeRow(ushort Raw)
{
    public const int Size = 2;

    public ushort Flags => (ushort)(Raw & 0x1F);
    public int Template => Raw >> 5;

    public LegacyDyeRow WithFlags(ushort flags) => new((ushort)((Raw & ~0x1F) | (flags & 0x1F)));
    public LegacyDyeRow WithTemplate(int template) => new((ushort)((Raw & 0x1F) | (template << 5)));
}

/// <summary>
/// A material's colour table and dye table as typed rows. Tables the editor cannot lay out are
/// kept as <see cref="ColorTableKind.Opaque"/> raw bytes. Instances are treated as snapshots: edit
/// a <see cref="Clone"/> and hand it to the document.
/// </summary>
public sealed class ColorTable
{
    public const int DtRowCount = 32;
    public const int LegacyRowCount = 16;

    private readonly TableFlags baseFlags;
    private readonly byte[] raw;

    // The rows as read, with their bytes, so unchanged rows write back bit for bit.
    private readonly DtColorRow[]? sourceRows;
    private readonly LegacyColorRow[]? sourceLegacyRows;

    public ColorTableKind Kind { get; }
    public DtColorRow[] Rows { get; }
    public LegacyColorRow[] LegacyRows { get; }
    public DtDyeRow[]? Dye { get; }
    public LegacyDyeRow[]? LegacyDye { get; }

    public ColorTable(DtColorRow[] rows, DtDyeRow[]? dye, TableFlags baseFlags = default)
        : this(ColorTableKind.Dawntrail, baseFlags, [], rows, [], dye, null, null, null)
    {
        if (rows.Length != DtRowCount || dye is { Length: not DtRowCount })
            throw new ArgumentException("A Dawntrail table has 32 rows.");
    }

    public ColorTable(LegacyColorRow[] rows, LegacyDyeRow[]? dye, TableFlags baseFlags = default)
        : this(ColorTableKind.Legacy, baseFlags, [], [], rows, null, dye, null, null)
    {
        if (rows.Length != LegacyRowCount || dye is { Length: not LegacyRowCount })
            throw new ArgumentException("A legacy table has 16 rows.");
    }

    private ColorTable(ColorTableKind kind, TableFlags baseFlags, byte[] raw, DtColorRow[] rows, LegacyColorRow[] legacyRows,
        DtDyeRow[]? dye, LegacyDyeRow[]? legacyDye, DtColorRow[]? sourceRows, LegacyColorRow[]? sourceLegacyRows)
    {
        Kind = kind;
        this.baseFlags = baseFlags;
        this.raw = raw;
        Rows = rows;
        LegacyRows = legacyRows;
        Dye = dye;
        LegacyDye = legacyDye;
        this.sourceRows = sourceRows;
        this.sourceLegacyRows = sourceLegacyRows;
    }

    /// <summary>A fresh 32-row table with every row at the game's defaults.</summary>
    public static ColorTable NewDawntrail(bool dyeable)
        => new(Enumerable.Repeat(DtColorRow.Default, DtRowCount).ToArray(), dyeable ? new DtDyeRow[DtRowCount] : null);

    /// <summary>The material's table, or null when its flags say it has none.</summary>
    public static ColorTable? Read(MtrlInfo material)
    {
        var flags = TableFlags.Read(material.AdditionalData);
        var data = material.DataSet;
        switch (flags.Kind)
        {
            case ColorTableKind.None:
                return null;

            case ColorTableKind.Dawntrail when data.Length == DtRowCount * DtColorRow.Size + (flags.HasDyeTable ? DtRowCount * DtDyeRow.Size : 0):
            {
                var rows = new DtColorRow[DtRowCount];
                for (var i = 0; i < rows.Length; i++)
                    rows[i] = DtColorRow.Read(data.AsSpan(i * DtColorRow.Size, DtColorRow.Size));
                DtDyeRow[]? dye = null;
                if (flags.HasDyeTable)
                {
                    dye = new DtDyeRow[DtRowCount];
                    for (var i = 0; i < dye.Length; i++)
                        dye[i] = new DtDyeRow(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DtRowCount * DtColorRow.Size + i * DtDyeRow.Size)));
                }
                return new ColorTable(ColorTableKind.Dawntrail, flags, data.ToArray(), rows, [], dye, null, rows.ToArray(), null);
            }

            case ColorTableKind.Legacy when data.Length == LegacyRowCount * LegacyColorRow.Size + (flags.HasDyeTable ? LegacyRowCount * LegacyDyeRow.Size : 0):
            {
                var rows = new LegacyColorRow[LegacyRowCount];
                for (var i = 0; i < rows.Length; i++)
                    rows[i] = LegacyColorRow.Read(data.AsSpan(i * LegacyColorRow.Size, LegacyColorRow.Size));
                LegacyDyeRow[]? dye = null;
                if (flags.HasDyeTable)
                {
                    dye = new LegacyDyeRow[LegacyRowCount];
                    for (var i = 0; i < dye.Length; i++)
                        dye[i] = new LegacyDyeRow(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(LegacyRowCount * LegacyColorRow.Size + i * LegacyDyeRow.Size)));
                }
                return new ColorTable(ColorTableKind.Legacy, flags, data.ToArray(), [], rows, null, dye, null, rows.ToArray());
            }

            default:
                return new ColorTable(ColorTableKind.Opaque, flags, data.ToArray(), [], [], null, null, null, null);
        }
    }

    public ColorTable Clone() => new(Kind, baseFlags, raw, Rows.ToArray(), LegacyRows.ToArray(), Dye?.ToArray(), LegacyDye?.ToArray(), sourceRows, sourceLegacyRows);

    /// <summary>The same rows with a dye table added (all rows undyed) or removed.</summary>
    public ColorTable WithDyeTable(bool dyeable) => Kind switch
    {
        ColorTableKind.Dawntrail => new(Kind, baseFlags, raw, Rows.ToArray(), [], dyeable ? Dye?.ToArray() ?? new DtDyeRow[DtRowCount] : null, null, sourceRows, null),
        ColorTableKind.Legacy => new(Kind, baseFlags, raw, [], LegacyRows.ToArray(), null, dyeable ? LegacyDye?.ToArray() ?? new LegacyDyeRow[LegacyRowCount] : null, null, sourceLegacyRows),
        _ => this,
    };

    /// <summary>The flags this table needs in the material's additional data; bits the editor does not know are kept.</summary>
    public TableFlags Flags() => ApplyTo(baseFlags);

    /// <summary>A material's flags with this table's bits (table, dye table, size) set and the rest left alone.</summary>
    public TableFlags ApplyTo(TableFlags flags) => Kind switch
    {
        ColorTableKind.Dawntrail => flags.WithTable(true).WithDyeTable(Dye != null).WithLogs(3, 5),
        ColorTableKind.Legacy => flags.WithTable(true).WithDyeTable(LegacyDye != null).WithLogs(baseFlags.WidthLog == 2 ? 2 : 0, baseFlags.HeightLog == 4 ? 4 : 0),
        _ => new TableFlags((flags.Raw & ~0xFFCu) | (baseFlags.Raw & 0xFFC)),
    };

    /// <summary>The material's data set: the rows, then the dye rows when the table is dyeable.</summary>
    public byte[] WriteDataSet()
    {
        switch (Kind)
        {
            case ColorTableKind.Dawntrail:
            {
                var bytes = new byte[DtRowCount * DtColorRow.Size + (Dye != null ? DtRowCount * DtDyeRow.Size : 0)];
                for (var i = 0; i < DtRowCount; i++)
                {
                    var target = bytes.AsSpan(i * DtColorRow.Size, DtColorRow.Size);
                    if (sourceRows != null && sourceRows[i] == Rows[i])
                        raw.AsSpan(i * DtColorRow.Size, DtColorRow.Size).CopyTo(target);
                    else
                        Rows[i].Write(target);
                }
                if (Dye != null)
                    for (var i = 0; i < DtRowCount; i++)
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(DtRowCount * DtColorRow.Size + i * DtDyeRow.Size), Dye[i].Raw);
                return bytes;
            }

            case ColorTableKind.Legacy:
            {
                var bytes = new byte[LegacyRowCount * LegacyColorRow.Size + (LegacyDye != null ? LegacyRowCount * LegacyDyeRow.Size : 0)];
                for (var i = 0; i < LegacyRowCount; i++)
                {
                    var target = bytes.AsSpan(i * LegacyColorRow.Size, LegacyColorRow.Size);
                    if (sourceLegacyRows != null && sourceLegacyRows[i] == LegacyRows[i])
                        raw.AsSpan(i * LegacyColorRow.Size, LegacyColorRow.Size).CopyTo(target);
                    else
                        LegacyRows[i].Write(target);
                }
                if (LegacyDye != null)
                    for (var i = 0; i < LegacyRowCount; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(LegacyRowCount * LegacyColorRow.Size + i * LegacyDyeRow.Size), LegacyDye[i].Raw);
                return bytes;
            }

            default:
                return raw.ToArray();
        }
    }

    /// <summary>
    /// The table in the Dawntrail layout. A legacy row p fills both rows of Dawntrail pair p, so the
    /// index texture picks the same colours whatever its blend channel says; its dye row keeps the
    /// template and maps diffuse, specular, emissive and gloss (as roughness).
    /// </summary>
    public ColorTable ToDawntrail()
    {
        if (Kind == ColorTableKind.Dawntrail)
            return Clone();
        if (Kind != ColorTableKind.Legacy)
            throw new InvalidOperationException("Only a legacy table can be converted.");

        var rows = new DtColorRow[DtRowCount];
        DtDyeRow[]? dye = LegacyDye != null ? new DtDyeRow[DtRowCount] : null;
        for (var p = 0; p < LegacyRowCount; p++)
        {
            rows[2 * p] = rows[2 * p + 1] = LegacyRows[p].ToDawntrail();
            if (dye == null)
                continue;
            var old = LegacyDye![p];
            var flags = (ushort)((old.Flags & 0b111) | ((old.Flags & 0b1000) != 0 ? 1 << 5 : 0));
            dye[2 * p] = dye[2 * p + 1] = new DtDyeRow(0).WithFlags(flags).WithTemplate(old.Template);
        }
        return new ColorTable(rows, dye, baseFlags);
    }
}
