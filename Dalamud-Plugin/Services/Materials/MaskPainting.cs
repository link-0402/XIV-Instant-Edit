using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Materials;

/// <summary>
/// What a preset wants in character.shpk's mask where its rows show, 0 to 1 per channel; null leaves a
/// channel as it is. The shader scales the shine by red² and leaves the row's roughness as it is at green
/// 0.5 (<see cref="LegacyConversion"/>).
/// </summary>
public sealed record MaskEdit(float? Red, float? Green, float? Blue = null);

/// <summary>
/// Paints a mask where some Dawntrail rows show. The mask and the index texture share the first UV map,
/// so a mask texel reads the index texel at the same place (nearest, whatever their sizes) and moves
/// toward the edit by the rows' share there, as the shader blends them: red picks pair p = round(r / 17),
/// green gives the top row 2p g / 255 and the bottom row the rest.
/// </summary>
public static class MaskPainting
{
    /// <summary>
    /// The painted mask, BGRA at the mask's size, and how many texels changed. BC compression leaves a ramp a
    /// texel or two wide between two pairs whose reds round to every pair in between (<see cref="IndexUsageCalc"/>),
    /// so an index texel whose pair lies strictly between its two neighbours' (across or down) takes the largest
    /// share of its neighbours that aren't: the ramp between pairs 1 and 16 doesn't paint pair 8, and the edge of a
    /// painted area has no gap. A thin line of one pair inside another still paints.
    /// </summary>
    /// <param name="mask">The mask at full size.</param>
    /// <param name="index">The index texture at full size; null paints the whole mask (for every row).</param>
    public static (byte[] Bgra, int Changed) Paint(DecodedTexture mask, DecodedTexture? index, IReadOnlyCollection<int> rows, MaskEdit edit)
    {
        var (iw, ih) = index == null ? (1, 1) : (index.Width, index.Height);
        float[] shares = index == null ? [1f] : Shares(index, rows);
        var (mw, mh) = (mask.Width, mask.Height);
        var bgra = (byte[])mask.Bgra.Clone();
        var changed = 0;
        for (var y = 0; y < mh; y++)
        {
            var iy = Math.Min(ih - 1, (int)((y + 0.5f) * ih / mh));
            for (var x = 0; x < mw; x++)
            {
                var share = shares[iy * iw + Math.Min(iw - 1, (int)((x + 0.5f) * iw / mw))];
                if (share <= 0f)
                    continue;
                var p = (y * mw + x) * 4;
                var before = (bgra[p], bgra[p + 1], bgra[p + 2]);
                Blend(bgra, p, edit.Blue, share);
                Blend(bgra, p + 1, edit.Green, share);
                Blend(bgra, p + 2, edit.Red, share);
                if ((bgra[p], bgra[p + 1], bgra[p + 2]) != before)
                    changed++;
            }
        }
        return (bgra, changed);
    }

    /// <summary>The share of <paramref name="rows"/> at each index texel, 0 to 1.</summary>
    private static float[] Shares(DecodedTexture index, IReadOnlyCollection<int> rows)
    {
        var wanted = new bool[ColorTable.DtRowCount];
        foreach (var row in rows)
            if (row is >= 0 and < ColorTable.DtRowCount)
                wanted[row] = true;
        var (width, height) = (index.Width, index.Height);
        var texels = width * height;
        var picks = new byte[texels];
        for (var i = 0; i < texels; i++)
            picks[i] = (byte)Math.Clamp((int)MathF.Round(index.Bgra[i * 4 + 2] / 17f), 0, 15);
        var shares = new float[texels];
        var ramp = new bool[texels];
        for (var i = 0; i < texels; i++)
        {
            var pick = picks[i];
            var (x, y) = (i % width, i / width);
            ramp[i] = x > 0 && x < width - 1 && Between(picks[i - 1], pick, picks[i + 1])
                      || y > 0 && y < height - 1 && Between(picks[i - width], pick, picks[i + width]);
            var top = index.Bgra[i * 4 + 1] / 255f;
            shares[i] = (wanted[pick * 2] ? top : 0f) + (wanted[pick * 2 + 1] ? 1f - top : 0f);
        }
        var result = (float[])shares.Clone();
        for (var i = 0; i < texels; i++)
        {
            if (!ramp[i])
                continue;
            var (x, y) = (i % width, i / width);
            var best = 0f;
            if (x > 0 && !ramp[i - 1]) best = MathF.Max(best, shares[i - 1]);
            if (x < width - 1 && !ramp[i + 1]) best = MathF.Max(best, shares[i + 1]);
            if (y > 0 && !ramp[i - width]) best = MathF.Max(best, shares[i - width]);
            if (y < height - 1 && !ramp[i + width]) best = MathF.Max(best, shares[i + width]);
            result[i] = best;
        }
        return result;
    }

    /// <summary>Whether <paramref name="pick"/> lies strictly between its two neighbours' pairs, as on a compression ramp.</summary>
    private static bool Between(byte before, byte pick, byte after)
        => before < pick && pick < after || before > pick && pick > after;

    private static void Blend(byte[] bgra, int at, float? target, float share)
    {
        if (target is not { } value)
            return;
        var old = bgra[at];
        bgra[at] = (byte)Math.Clamp((int)MathF.Round(old + (value * 255f - old) * MathF.Min(share, 1f)), 0, 255);
    }
}
