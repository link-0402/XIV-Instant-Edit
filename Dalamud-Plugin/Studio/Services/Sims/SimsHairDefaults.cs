using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>
/// What a hair port needs from a Sims hair package, which carries much more than that.
///
/// Each detail level of a hair lists its meshes in sets that share a shader: the alpha hair
/// cards and the solid cap under them that hides bald spots. Each set holds the full mesh and
/// cut-down copies the game swaps in under hats, with the top of the hair removed. The full
/// mesh is the one with the most vertices, since a hat version is the full one minus the part
/// a hat covers. (An overlap test is no good: a style's hat versions may be shared with
/// another style and only half lie on its full mesh.)
///
/// A hair also ships one diffuse per colour swatch, and a port wants one: the lightest grey,
/// which is turned into the hair's normal texture. Not every creator includes a white or grey
/// swatch, so without a near-grey one the lightest of all is taken.
/// </summary>
public static class SimsHairDefaults
{
    /// <summary>Average colour spread (max minus min channel, 0-255) up to which a diffuse counts as grey.</summary>
    public const float GreySaturation = 15f;

    /// <summary>
    /// The hat versions among one detail level's meshes: within each shader, every mesh but
    /// the one with the most vertices (the first of equals).
    /// </summary>
    public static HashSet<T> HatVersions<T>(IEnumerable<(T Mesh, uint Shader, int Vertices)> meshes)
    {
        var hat = new HashSet<T>();
        foreach (var set in meshes.GroupBy(m => m.Shader))
        {
            var full = set.First(m => m.Vertices == set.Max(x => x.Vertices)).Mesh;
            foreach (var (mesh, _, _) in set)
                if (!EqualityComparer<T>.Default.Equals(mesh, full))
                    hat.Add(mesh);
        }
        return hat;
    }

    /// <summary>The index of the diffuse to keep: the lightest grey one, else the lightest. -1 for none.</summary>
    public static int PickDiffuse(IReadOnlyList<(float Lightness, float Saturation)> diffuses)
    {
        int best = -1;
        for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int i = 0; i < diffuses.Count; i++)
            {
                var (lightness, saturation) = diffuses[i];
                if (lightness < 0 || (pass == 0 && saturation > GreySaturation))
                    continue;
                if (best < 0 || lightness > diffuses[best].Lightness)
                    best = i;
            }
        return best;
    }

    /// <summary>
    /// Mean lightness (0-255) and colour spread of the opaque pixels of straight RGBA8 data,
    /// sampled every few pixels. (-1, 0) when none is opaque.
    /// </summary>
    public static (float Lightness, float Saturation) Measure(byte[] rgba)
    {
        const int Step = 4 * 7;   // every 7th pixel; an odd step avoids sampling one column
        double lightness = 0, saturation = 0;
        long count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += Step)
        {
            if (rgba[i + 3] < 128)
                continue;
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            lightness += (r + g + b) / 3.0;
            saturation += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            count++;
        }
        return count == 0 ? (-1f, 0f) : ((float)(lightness / count), (float)(saturation / count));
    }
}
