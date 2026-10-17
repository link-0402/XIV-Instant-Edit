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
    public static byte[] FromNormal(DecodedTexture normal)
    {
        var texels = normal.Width * normal.Height;
        var bgra = new byte[texels * 4];
        for (var i = 0; i < texels; i++)
        {
            var pair = Math.Clamp((int)MathF.Round(normal.Bgra[i * 4 + 3] / 17f), 0, 15);
            bgra[i * 4 + 1] = 255;
            bgra[i * 4 + 2] = (byte)(pair * 17);
            bgra[i * 4 + 3] = 255;
        }
        return TextureFiles.BgraTex(bgra, normal.Width, normal.Height);
    }
}

/// <summary>
/// Reads an index texture the way the shader does. Dawntrail: red picks pair p = round(r / 17), green
/// blends it, the top row 2p getting g / 255 and the bottom row 2p + 1 the rest. Legacy: red alone
/// picks one of 16 rows. A row counts as used when some texel gives it more than 1 / 255.
/// </summary>
public static class IndexUsageCalc
{
    private const float UsedWeight = 1f / 255f;

    /// <param name="index">The index texture at full size: a downscaled one blends neighbouring pairs into ones nobody uses.</param>
    public static IndexUsage Compute(DecodedTexture index, ColorTableKind kind)
    {
        var rows = kind == ColorTableKind.Legacy ? ColorTable.LegacyRowCount : ColorTable.DtRowCount;
        var texels = index.Width * index.Height;
        var weight = new double[rows];
        var used = new bool[rows];
        var rowOfTexel = new byte[texels];
        var bgra = index.Bgra;
        for (var i = 0; i < texels; i++)
        {
            var green = bgra[i * 4 + 1];
            var red = bgra[i * 4 + 2];
            var pick = Math.Clamp((int)MathF.Round(red / 17f), 0, 15);
            if (kind == ColorTableKind.Legacy)
            {
                weight[pick] += 1;
                used[pick] = true;
                rowOfTexel[i] = (byte)pick;
                continue;
            }
            var top = green / 255f;
            var bottom = 1f - top;
            weight[pick * 2] += top;
            weight[pick * 2 + 1] += bottom;
            if (top > UsedWeight)
                used[pick * 2] = true;
            if (bottom > UsedWeight)
                used[pick * 2 + 1] = true;
            rowOfTexel[i] = (byte)(top >= bottom ? pick * 2 : pick * 2 + 1);
        }
        var share = weight.Select(total => texels == 0 ? 0f : (float)(total / texels)).ToArray();
        return new IndexUsage(share, used, index.Width, index.Height, rowOfTexel);
    }
}
