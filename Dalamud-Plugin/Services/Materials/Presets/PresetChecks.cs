using System.Numerics;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Materials.Presets;

/// <summary>What the presets share: the rows a target means, row and constant edits, the mask check, the scroll pattern.</summary>
public static class PresetChecks
{
    public const uint NormalSampler = 0x0C5EC1F1;
    public const uint MaskSampler = 0x8A4E82B6;
    public const uint IndexSampler = 0x565F8FD8;
    public const uint CatchlightSampler = 0xFEA0F3D2;
    public const uint EmissiveColor = 0x38A64362;
    public const uint TileIndex = 0x4255F2F4;
    public const uint TileScale = 0x2E60B071;
    public const uint TileAlpha = 0x12C6AC9F;
    public const uint SheenRate = 0x800EE35F;
    public const uint SheenTint = 0x1F264897;
    public const uint SheenAperture = 0xF490F76E;
    public const uint Translucent = 0x10;
    public const string TranslucentCrashNote = "Translucent skin and stockings crash the game.";

    /// <summary>Whether turning Translucent on for this shader crashes the game (skin and stockings).</summary>
    public static bool TranslucentCrashes(string shaderPackage)
        => shaderPackage.Equals("skin.shpk", StringComparison.OrdinalIgnoreCase)
           || shaderPackage.Equals("characterstockings.shpk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the material has Dawntrail rows its shader reads.</summary>
    public static bool HasRows(PresetContext context)
        => context.Facts.SamplesColorTable && context.Doc.Table is { Kind: ColorTableKind.Dawntrail };

    /// <summary>Whether the shader reads the constant with the material's keys.</summary>
    public static bool Reads(PresetContext context, uint constant) => context.Facts.Reads(constant, context.Doc.Keys);

    /// <summary>The rows a target means; a note when it had to fall back to every row.</summary>
    public static (int[] Rows, string? Note) Rows(PresetContext context, RowTarget target)
    {
        var all = Enumerable.Range(0, ColorTable.DtRowCount).ToArray();
        switch (target)
        {
            case RowTarget.Used when context.Usage is { RowUsed.Length: ColorTable.DtRowCount } usage:
                var used = all.Where(row => usage.RowUsed[row]).ToArray();
                return used.Length > 0 ? (used, null) : (all, "The index texture uses no rows, so every row changed.");
            case RowTarget.Used:
                return (all, "The index texture couldn't be read, so every row changed.");
            case RowTarget.Selected when context.SelectedPair is { } pair and >= 0 and < 16:
                return ([pair * 2, pair * 2 + 1], null);
            case RowTarget.Selected:
                return (all, "No pair was selected, so every row changed.");
            default:
                return (all, null);
        }
    }

    /// <summary>The document with <paramref name="change"/> applied to each of <paramref name="rows"/> of its Dawntrail table.</summary>
    public static MaterialDocument EditRows(MaterialDocument doc, IEnumerable<int> rows, Func<DtColorRow, DtColorRow> change)
    {
        if (doc.Table is not { Kind: ColorTableKind.Dawntrail } source)
            return doc;
        var table = source.Clone();
        foreach (var row in rows)
            table.Rows[row] = change(table.Rows[row]);
        return doc.WithTable(table);
    }

    public static string RowCount(int count) => count == ColorTable.DtRowCount ? "every row" : count == 1 ? "1 row" : $"{count} rows";

    /// <summary>The average of the mask's red (shine strength) and green (roughness) channels, 0 to 1.</summary>
    public static (float Red, float Green) MaskMeans(DecodedTexture mask)
    {
        double red = 0, green = 0;
        var texels = mask.Width * mask.Height;
        for (var i = 0; i < texels; i++)
        {
            green += mask.Bgra[i * 4 + 1];
            red += mask.Bgra[i * 4 + 2];
        }
        return texels == 0 ? (0, 0) : ((float)(red / texels / 255), (float)(green / texels / 255));
    }

    /// <summary>"chara/x/material/v0001/mt_a.mtrl" gives "chara/x/material/v0001/mt_a" + <paramref name="suffix"/>.</summary>
    public static string BesideMaterial(string materialGamePath, string suffix)
    {
        var dot = materialGamePath.LastIndexOf('.');
        return (dot > materialGamePath.LastIndexOf('/') ? materialGamePath[..dot] : materialGamePath) + suffix;
    }

    /// <summary>Points the slot reading <paramref name="sampler"/> at <paramref name="path"/>, adding the slot when there is none.</summary>
    public static MaterialDocument WithSamplerPath(MaterialDocument doc, uint sampler, string path)
    {
        for (var i = 0; i < doc.Slots.Count; i++)
            if (doc.Slots[i].SamplerId == sampler)
                return doc.WithSlot(i, doc.Slots[i] with { Path = path });
        return doc.WithAddedSlot(sampler, path);
    }

    /// <summary>
    /// The scrolling pattern's texture: soft diagonal stripes cycling through the hues, tiling
    /// seamlessly, as an uncompressed 256 x 256 texture.
    /// </summary>
    public static byte[] ScrollPattern()
    {
        const int size = 256;
        var bgra = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Two stripes across the tile, so the pattern repeats at the tile's edges.
                var phase = (x + y) / (float)size * 2f;
                var hue = phase - MathF.Floor(phase);
                var soft = 0.75f + 0.25f * MathF.Cos(phase * 2 * MathF.PI * 2);
                var colour = Hue(hue) * soft;
                var i = (y * size + x) * 4;
                bgra[i] = (byte)(Math.Clamp(colour.Z, 0, 1) * 255);
                bgra[i + 1] = (byte)(Math.Clamp(colour.Y, 0, 1) * 255);
                bgra[i + 2] = (byte)(Math.Clamp(colour.X, 0, 1) * 255);
                bgra[i + 3] = 255;
            }
        return TextureFiles.BgraTex(bgra, size, size);
    }

    private static Vector3 Hue(float hue)
    {
        var r = Math.Clamp(MathF.Abs(hue * 6 - 3) - 1, 0, 1);
        var g = Math.Clamp(2 - MathF.Abs(hue * 6 - 2), 0, 1);
        var b = Math.Clamp(2 - MathF.Abs(hue * 6 - 4), 0, 1);
        return new Vector3(r, g, b);
    }
}
