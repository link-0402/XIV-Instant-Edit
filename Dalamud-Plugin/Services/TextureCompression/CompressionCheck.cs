using InstantEdit.Ui;

namespace InstantEdit.Services.TextureCompression;

/// <summary> What the check measured on one texture, and why it keeps the original when it does. </summary>
internal sealed record CompressionCheckResult
{
    /// <summary> Why the compressed texture isn't used, for the card; empty when it passed. </summary>
    public string Problem { get; init; } = string.Empty;
    /// <summary> Of the pixels a cut-out draws, the share that would appear or disappear (worst use). </summary>
    public double CutoutChange { get; init; }
    /// <summary> The same share in the worst tile, so damage to a small lace area can't hide in a large texture. </summary>
    public double CutoutTileChange { get; init; }
    /// <summary> Of the pixels a blended material draws, the share whose opacity would change by more than <see cref="CompressionTolerances.OpacityStep"/>. </summary>
    public double OpacityChange { get; init; }
    public double OpacityTileChange { get; init; }
    /// <summary> Of an index map's pixels, the share that would pick a colorset row pair none of their neighbours use. </summary>
    public double RowChange { get; init; }
    public double RowTileChange { get; init; }
    /// <summary> The 99th percentile of how far drawn normals turn, in degrees. </summary>
    public double NormalAngleP99 { get; init; }
    /// <summary> Of the drawn pixels, the share with a channel off by more than <see cref="CompressionTolerances.LargeError"/>. </summary>
    public double LargeErrorShare { get; init; }

    public bool Passed => Problem.Length == 0;
}

/// <summary>
/// How much the check lets compression change. Calibrated on 2026-09-30 against Penumbra's own
/// encodes of the uncompressed textures in a 4,200-mod library, with crops of the changes looked at
/// side by side: cut-outs that lost lace strands or grew ragged edges changed 0.4 % to 9 % of their
/// drawn pixels, ones that only moved an edge pixel here and there 0.05 % or less.
/// </summary>
internal sealed record CompressionTolerances
{
    public static CompressionTolerances Default { get; } = new();

    public double MaxCutoutChange { get; init; } = 0.001;
    public double MaxCutoutTileChange { get; init; } = 0.10;
    public double MaxOpacityChange { get; init; } = 0.005;
    public double MaxOpacityTileChange { get; init; } = 0.15;
    /// <summary> A blended pixel counts as changed when its opacity moves by more than this. </summary>
    public double OpacityStep { get; init; } = 0.1;
    public double MaxRowChange { get; init; } = 0.0005;
    public double MaxRowTileChange { get; init; } = 0.10;
    public double MaxNormalAngleP99 { get; init; } = 25;
    /// <summary> A pixel counts as a large error when a channel moves by more than this many levels. </summary>
    public int LargeError { get; init; } = 48;
    public double MaxLargeErrorShare { get; init; } = 0.002;
    /// <summary> Tiles this many pixels square are checked on their own. </summary>
    public int Tile { get; init; } = 32;
    /// <summary> Tiles with fewer counted pixels than this are only judged as part of the whole, since a few pixels of a thin strand make large shares. </summary>
    public int MinTilePixels { get; init; } = 256;
}

/// <summary>
/// Decides whether a compressed texture keeps what its shaders read, by comparing its top level
/// with the original's. Block compression is close to lossless on most textures but not on all:
/// on real mods, gear normal maps whose blue channel cuts out fine lace or fishnet lost up to 9 %
/// of their drawn pixels, and index maps with dense multi-row patterns switched colorset row pairs
/// on up to 3 % of their pixels, while diffuse and mask maps stayed within a few levels. So each
/// texture is judged on what matters for it: cut-out edges and blended opacity for the channel a
/// material reads opacity from, colorset row pairs for index maps, how far normals turn, and large
/// errors anywhere drawn. Both images are BGRA8, top level only. Dalamud-free.
/// </summary>
internal static class CompressionCheck
{
    /// <summary> Bumped when the tolerances change, so textures an older check kept are looked at again. </summary>
    public const int Version = 1;

    /// <summary> How a material reads opacity from the texture: the byte (BGRA order), the level that draws, and whether it blends. </summary>
    private readonly record struct Opacity(int Byte, float Level, bool Blended);

    /// <param name="twoChannel">The texture was compressed to red and green only (BC5), so blue and alpha aren't compared.</param>
    public static CompressionCheckResult Compare(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded, int width, int height,
        IReadOnlyList<TextureUse> uses, bool twoChannel, CompressionTolerances? tolerances = null)
    {
        tolerances ??= CompressionTolerances.Default;
        var pixels = (long)width * height;
        if (width <= 0 || height <= 0 || original.Length < pixels * 4 || decoded.Length < pixels * 4)
            throw new ArgumentException("The images don't match the texture's size.");

        // Blended materials scale opacity by 1 / g_AlphaThreshold; with no threshold the channel draws everything.
        var opacities = uses.Where(use => use.OpacityChannel >= 0 && !(twoChannel && use.OpacityChannel >= 2) &&
                                          (use.CutsOut || use.Translucent && use.AlphaThreshold > 0))
            .Select(use => new Opacity(ByteOf(use.OpacityChannel), Math.Clamp(use.AlphaThreshold, 0f, 1f) * 255f, !use.CutsOut))
            .Distinct().ToArray();
        var index = uses.Any(use => use.Role == TextureRole.Index);
        var normal = uses.Any(use => use.Role == TextureRole.Normal);
        // Normal maps' red and green are judged by how far the normals turn; as colors they'd count twice.
        var firstColorChannel = uses.Count > 0 && uses.All(use => use.Role == TextureRole.Normal) ? 2 : 0;
        var tileSize = tolerances.Tile;
        int tilesX = (width + tileSize - 1) / tileSize, tilesY = (height + tileSize - 1) / tileSize, tiles = tilesX * tilesY;
        var counted = new long[opacities.Length];
        var changed = new long[opacities.Length];
        var tileCounted = new int[opacities.Length, tiles];
        var tileChanged = new int[opacities.Length, tiles];
        var tileRowFlips = index ? new int[tiles] : [];
        long rowFlips = 0, drawn = 0, large = 0, angles = 0;
        var angleHistogram = normal ? new long[1801] : [];
        var channels = twoChannel ? 2 : 4;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var tile = y / tileSize * tilesX + x / tileSize;
            var shown = true;
            for (var o = 0; o < opacities.Length; o++)
            {
                var (at, level, blended) = opacities[o];
                if (blended)
                {
                    // Opacity as the material blends it; pixels drawn at under a quarter don't count as shown.
                    double before = Math.Min(1, original[i + at] / level), after = Math.Min(1, decoded[i + at] / level);
                    if (before < 0.25)
                        shown = false;
                    if (before < 0.02 && after < 0.02)
                        continue;
                    counted[o]++;
                    tileCounted[o, tile]++;
                    if (Math.Abs(before - after) > tolerances.OpacityStep)
                    {
                        changed[o]++;
                        tileChanged[o, tile]++;
                    }
                }
                else
                {
                    bool before = original[i + at] >= level, after = decoded[i + at] >= level;
                    if (before)
                    {
                        counted[o]++;
                        tileCounted[o, tile]++;
                    }
                    else
                        shown = false;
                    if (before != after)
                    {
                        changed[o]++;
                        tileChanged[o, tile]++;
                    }
                }
            }
            if (index && Pair(decoded[i + 2]) is var pair && pair != Pair(original[i + 2]) && !NearbyPair(original, width, height, x, y, pair))
            {
                rowFlips++;
                tileRowFlips[tile]++;
            }
            if (!shown)
                continue;
            drawn++;
            for (var c = firstColorChannel; c < channels; c++)
            {
                // Red, green, blue, alpha; BGRA order puts them at bytes 2, 1, 0 and 3.
                var at = ByteOf(c);
                if (Math.Abs(original[i + at] - decoded[i + at]) > tolerances.LargeError)
                {
                    large++;
                    break;
                }
            }
            if (normal)
            {
                angleHistogram[Math.Min(1800, (int)(Angle(original, decoded, i) * 20))]++;
                angles++;
            }
        }

        double cutoutChange = 0, cutoutTileChange = 0, opacityChange = 0, opacityTileChange = 0;
        for (var o = 0; o < opacities.Length; o++)
        {
            var share = counted[o] > 0 ? changed[o] / (double)counted[o] : 0;
            var tileShare = 0.0;
            for (var t = 0; t < tiles; t++)
                if (tileCounted[o, t] >= tolerances.MinTilePixels)
                    tileShare = Math.Max(tileShare, tileChanged[o, t] / (double)tileCounted[o, t]);
            if (opacities[o].Blended)
            {
                opacityChange = Math.Max(opacityChange, share);
                opacityTileChange = Math.Max(opacityTileChange, tileShare);
            }
            else
            {
                cutoutChange = Math.Max(cutoutChange, share);
                cutoutTileChange = Math.Max(cutoutTileChange, tileShare);
            }
        }
        double rowTileChange = 0;
        if (index)
            for (var t = 0; t < tiles; t++)
            {
                int tileWidth = Math.Min(tileSize, width - t % tilesX * tileSize), tileHeight = Math.Min(tileSize, height - t / tilesX * tileSize);
                if (tileWidth * tileHeight >= tolerances.MinTilePixels)
                    rowTileChange = Math.Max(rowTileChange, tileRowFlips[t] / (double)(tileWidth * tileHeight));
            }
        double angleP99 = 0;
        if (angles > 0)
        {
            long seen = 0;
            for (var bin = 0; bin < angleHistogram.Length; bin++)
            {
                seen += angleHistogram[bin];
                if (seen >= angles * 0.99)
                {
                    angleP99 = bin / 20.0;
                    break;
                }
            }
        }

        var result = new CompressionCheckResult
        {
            CutoutChange = cutoutChange,
            CutoutTileChange = cutoutTileChange,
            OpacityChange = opacityChange,
            OpacityTileChange = opacityTileChange,
            RowChange = index ? rowFlips / (double)pixels : 0,
            RowTileChange = rowTileChange,
            NormalAngleP99 = angleP99,
            LargeErrorShare = drawn > 0 ? large / (double)drawn : 0,
        };
        return result with { Problem = ProblemOf(result, tolerances) };
    }

    /// <summary> Why the result fails, in the words the card shows; empty when it passes. </summary>
    public static string ProblemOf(CompressionCheckResult result, CompressionTolerances? tolerances = null)
    {
        tolerances ??= CompressionTolerances.Default;
        if (result.CutoutChange > tolerances.MaxCutoutChange || result.CutoutTileChange > tolerances.MaxCutoutTileChange)
            return "compressing would change the edges of its see-through parts";
        if (result.OpacityChange > tolerances.MaxOpacityChange || result.OpacityTileChange > tolerances.MaxOpacityTileChange)
            return "compressing would change how see-through it is";
        if (result.RowChange > tolerances.MaxRowChange || result.RowTileChange > tolerances.MaxRowTileChange)
            return "compressing would change which dye colors parts of it use";
        if (result.NormalAngleP99 > tolerances.MaxNormalAngleP99)
            return "compressing would change how light falls on it";
        if (result.LargeErrorShare > tolerances.MaxLargeErrorShare)
            return "compressing would change its colors noticeably";
        return string.Empty;
    }

    /// <summary> The colorset row pair an index map's red channel picks: pairs are 17 levels apart, rounded. </summary>
    public static int Pair(byte red) => (red + 8) / 17;

    /// <summary>
    /// Whether the original already uses this row pair next to the pixel. A pixel that takes its
    /// neighbour's pair only moves the border between two dye regions by a pixel, which the borders'
    /// own blended pixels already do; a pair that appears from nowhere is a stray dye color.
    /// </summary>
    private static bool NearbyPair(ReadOnlySpan<byte> original, int width, int height, int x, int y, int pair)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx, ny = y + dy;
            if ((dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < width && ny < height && Pair(original[(ny * width + nx) * 4 + 2]) == pair)
                return true;
        }
        return false;
    }

    /// <summary> The byte of a channel (0 red, 1 green, 2 blue, 3 alpha) in BGRA order. </summary>
    private static int ByteOf(int channel) => channel switch { 0 => 2, 1 => 1, 2 => 0, _ => 3 };

    /// <summary> How far a normal turns, in degrees, with Z rebuilt from red and green as the shaders do. </summary>
    private static double Angle(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded, int i)
    {
        double x0 = original[i + 2] / 127.5 - 1, y0 = original[i + 1] / 127.5 - 1, x1 = decoded[i + 2] / 127.5 - 1, y1 = decoded[i + 1] / 127.5 - 1;
        double z0 = Math.Sqrt(Math.Max(0, 1 - x0 * x0 - y0 * y0)), z1 = Math.Sqrt(Math.Max(0, 1 - x1 * x1 - y1 * y1));
        var length = Math.Sqrt((x0 * x0 + y0 * y0 + z0 * z0) * (x1 * x1 + y1 * y1 + z1 * z1));
        var cosine = length > 1e-12 ? (x0 * x1 + y0 * y1 + z0 * z1) / length : 1;
        return Math.Acos(Math.Clamp(cosine, -1, 1)) * 180 / Math.PI;
    }
}
