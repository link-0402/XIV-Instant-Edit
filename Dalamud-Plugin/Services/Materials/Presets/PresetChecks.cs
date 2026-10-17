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
    /// The glow a scrolling row needs, since the pattern only shows through the glow: a row that
    /// glows keeps its colour, a dark one glows in its base colour, a black one in white.
    /// </summary>
    public static Vector3 ScrollGlow(DtColorRow row)
        => MathF.Max(row.Emissive.X, MathF.Max(row.Emissive.Y, row.Emissive.Z)) > 0f ? row.Emissive
            : MathF.Max(row.Diffuse.X, MathF.Max(row.Diffuse.Y, row.Diffuse.Z)) >= 0.1f ? row.Diffuse
            : Vector3.One;

    /// <summary>
    /// The scrolling pattern's texture: glowing energy veins with sparkles on black, in grey so the
    /// rows' glow colour tints it, tiling seamlessly, as an uncompressed 256 x 256 texture. Like the
    /// energy and sparkle textures scrolling mods use.
    /// </summary>
    public static byte[] ScrollPattern()
    {
        const int size = 256;
        const int cells = 6;
        const float warp = 0.55f;
        var warpU = PeriodicNoise(size, 4, 3);
        var warpU2 = PeriodicNoise(size, 8, 5);
        var warpV = PeriodicNoise(size, 4, 4);
        var warpV2 = PeriodicNoise(size, 8, 6);
        var flicker = PeriodicNoise(size, 8, 21);
        var bgra = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = y * size + x;
                // Bend the straight cell borders into lightning-like curves; the warp wraps, so the tile still repeats.
                var u = x / (float)size * cells + (warpU[p] - 0.5f) * 2 * warp + (warpU2[p] - 0.5f) * warp;
                var v = y / (float)size * cells + (warpV[p] - 0.5f) * 2 * warp + (warpV2[p] - 0.5f) * warp;
                var edge = CellEdge(Wrap(u, cells), Wrap(v, cells), cells);
                var value = (MathF.Exp(-edge / 0.035f) + MathF.Exp(-edge / 0.16f) * 0.35f) * (0.55f + 0.45f * flicker[p]);
                for (var k = 0; k < 36; k++)
                {
                    var dx = Wrap(x - Hash(k, 1, 77) * size + size / 2f, size) - size / 2f;
                    var dy = Wrap(y - Hash(k, 2, 77) * size + size / 2f, size) - size / 2f;
                    var radius = 0.7f + Hash(k, 3, 77) * 1.3f;
                    value = MathF.Max(value, MathF.Exp(-(dx * dx + dy * dy) / (2 * radius * radius)) * (0.5f + 0.5f * Hash(k, 4, 77)));
                }
                var grey = (byte)(Math.Clamp(value, 0f, 1f) * 255);
                bgra[p * 4] = grey;
                bgra[p * 4 + 1] = grey;
                bgra[p * 4 + 2] = grey;
                bgra[p * 4 + 3] = 255;
            }
        return TextureFiles.BgraTex(bgra, size, size);
    }

    /// <summary>The distance to the nearest border of a wrapping grid of jittered cells (second nearest point minus nearest), in cells.</summary>
    private static float CellEdge(float u, float v, int cells)
    {
        int gx = (int)MathF.Floor(u), gy = (int)MathF.Floor(v);
        float f1 = 9f, f2 = 9f;
        for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                int cx = gx + ox, cy = gy + oy;
                int wx = ((cx % cells) + cells) % cells, wy = ((cy % cells) + cells) % cells;
                var dx = cx + Hash(wx, wy, 11) - u;
                var dy = cy + Hash(wx, wy, 12) - v;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < f1)
                {
                    f2 = f1;
                    f1 = d;
                }
                else if (d < f2)
                    f2 = d;
            }
        return f2 - f1;
    }

    /// <summary>Smooth value noise with <paramref name="period"/> cells across a <paramref name="size"/> tile, wrapping at its edges.</summary>
    private static float[] PeriodicNoise(int size, int period, int seed)
    {
        var noise = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var fy = y * period / (float)size;
            var y0 = (int)fy;
            var ty = Fade(fy - y0);
            var y1 = (y0 + 1) % period;
            for (var x = 0; x < size; x++)
            {
                var fx = x * period / (float)size;
                var x0 = (int)fx;
                var tx = Fade(fx - x0);
                var x1 = (x0 + 1) % period;
                var a = Hash(x0, y0, seed) * (1 - tx) + Hash(x1, y0, seed) * tx;
                var b = Hash(x0, y1, seed) * (1 - tx) + Hash(x1, y1, seed) * tx;
                noise[y * size + x] = a * (1 - ty) + b * ty;
            }
        }
        return noise;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static float Wrap(float value, float period) => value - MathF.Floor(value / period) * period;

    /// <summary>A repeatable pseudo-random value in 0 to 1 for a lattice point.</summary>
    private static float Hash(int x, int y, int seed)
    {
        var h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }
}
