using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Materials;

/// <summary>
/// Which colour rows an index texture selects: per row, the share of the texture's weight it gets
/// and whether any texel uses it at all, and per texel the row it mostly shows (for "show where").
/// </summary>
public sealed record IndexUsage(float[] RowShare, bool[] RowUsed, int Width, int Height, byte[] RowOfTexel)
{
    /// <summary>The share of the texture that shows pair <paramref name="pair"/> (rows 2p and 2p + 1).</summary>
    public float PairShare(int pair) => RowShare.Length == ColorTable.DtRowCount && pair is >= 0 and < 16 ? RowShare[pair * 2] + RowShare[pair * 2 + 1] : 0f;

    public bool PairUsed(int pair) => RowUsed.Length == ColorTable.DtRowCount && pair is >= 0 and < 16 && (RowUsed[pair * 2] || RowUsed[pair * 2 + 1]);
}

/// <summary>
/// The index texture a converted legacy table needs. Legacy shaders pick the row with the normal map's
/// alpha, and converting puts legacy row p into both rows of pair p, so red = 17 p (the pair of that
/// alpha) and green = 255 (the top row; both rows are the same).
/// </summary>
public static class LegacyIndexTexture
{
    /// <summary>
    /// An index texture from a legacy normal map's alpha, for a table converted with
    /// <see cref="ColorTable.ToDawntrail(bool)"/> blendNext: alpha a sits between legacy rows p = floor(a / 17)
    /// and p + 1, so red picks pair p (rows p and p + 1) and green gives row p its share, 1 - (a / 17 - p).
    /// </summary>
    public static byte[] FromNormal(DecodedTexture normal)
    {
        var texels = normal.Width * normal.Height;
        var bgra = new byte[texels * 4];
        for (var i = 0; i < texels; i++)
        {
            var row = normal.Bgra[i * 4 + 3] / 17f;
            var pair = Math.Clamp((int)MathF.Floor(row), 0, 15);
            bgra[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(255f * (1f - (row - pair))), 0, 255);
            bgra[i * 4 + 2] = (byte)(pair * 17);
            bgra[i * 4 + 3] = 255;
        }
        return TextureFiles.BgraTex(bgra, normal.Width, normal.Height);
    }
}

/// <summary>
/// Reads an index texture the way the shader does. Dawntrail: red picks pair p = round(r / 17), green
/// blends it, the top row 2p getting g / 255 and the bottom row 2p + 1 the rest. Legacy: red alone
/// picks one of 16 rows. A row counts as used when some texel gives it more than 1 / 255 and the
/// texel's four neighbours pick the same pair.
/// </summary>
/// <remarks>
/// BC compression blurs the edge between two pairs into a ramp a texel wide whose reds round to every
/// pair in between (in game 2026-10-17 a BC7 index with pairs 1 and 16 touched all 16, none of the
/// middle ones on a texel whose neighbours agree). The shares still count every texel.
/// </remarks>
public static class IndexUsageCalc
{
    private const float UsedWeight = 1f / 255f;

    /// <param name="index">The index texture at full size: a downscaled one blends neighbouring pairs into ones nobody uses.</param>
    public static IndexUsage Compute(DecodedTexture index, ColorTableKind kind)
    {
        var rows = kind == ColorTableKind.Legacy ? ColorTable.LegacyRowCount : ColorTable.DtRowCount;
        var (width, height) = (index.Width, index.Height);
        var texels = width * height;
        var weight = new double[rows];
        var used = new bool[rows];
        var rowOfTexel = new byte[texels];
        var bgra = index.Bgra;
        var picks = new byte[texels];
        for (var i = 0; i < texels; i++)
            picks[i] = (byte)Math.Clamp((int)MathF.Round(bgra[i * 4 + 2] / 17f), 0, 15);
        for (var i = 0; i < texels; i++)
        {
            var green = bgra[i * 4 + 1];
            var pick = picks[i];
            var (x, y) = (i % width, i / width);
            // Past the texture's edge counts as agreeing.
            var settled = (x == 0 || picks[i - 1] == pick) && (x == width - 1 || picks[i + 1] == pick)
                && (y == 0 || picks[i - width] == pick) && (y == height - 1 || picks[i + width] == pick);
            if (kind == ColorTableKind.Legacy)
            {
                weight[pick] += 1;
                used[pick] |= settled;
                rowOfTexel[i] = pick;
                continue;
            }
            var top = green / 255f;
            var bottom = 1f - top;
            weight[pick * 2] += top;
            weight[pick * 2 + 1] += bottom;
            if (settled && top > UsedWeight)
                used[pick * 2] = true;
            if (settled && bottom > UsedWeight)
                used[pick * 2 + 1] = true;
            rowOfTexel[i] = (byte)(top >= bottom ? pick * 2 : pick * 2 + 1);
        }
        var share = weight.Select(total => texels == 0 ? 0f : (float)(total / texels)).ToArray();
        return new IndexUsage(share, used, index.Width, index.Height, rowOfTexel);
    }
}
