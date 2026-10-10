using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>Which half of a texture's V range holds the content the mesh uses.</summary>
public enum SimsTextureHalf
{
    /// <summary>The mesh uses both halves, so nothing can be cropped away.</summary>
    None,

    /// <summary>Content in V 0…0.5 — the top of the image, as hair layouts use.</summary>
    Top,

    /// <summary>Content in V 0.5…1 — the bottom of the image, as body garments use.</summary>
    Bottom,
}

/// <summary>
/// A decision to throw away the unused half of a texture and rescale UV0 to match.
///
/// Sims CAS sheets are routinely twice as tall as they need to be, with the mesh packed
/// into one half and the other left empty — a body garment sits in V 0.5…1, hair in the
/// top quarter. Halving the sheet turns a 2048x4096 diffuse into a square 2048x2048 and
/// costs nothing, provided UV0 is rescaled by exactly the same amount.
///
/// The direction is taken from the mesh's own UV0 range rather than from how empty the
/// image looks, for two reasons: it is what actually decides whether the crop is safe,
/// and emptiness on its own can be undecidable — a placeholder texture is blank in both
/// halves, and a Sims normal map is fully opaque in both. Texture content is still
/// checked afterwards, but only to report a crop that would discard something.
/// </summary>
public readonly struct SimsHalfCrop
{
    /// <summary>How far outside a half a UV may stray and still count as inside it.</summary>
    private const float Tolerance = 0.002f;

    public readonly SimsTextureHalf Half;

    public SimsHalfCrop(SimsTextureHalf half) => Half = half;

    public static SimsHalfCrop None => new(SimsTextureHalf.None);

    public bool Crops => Half != SimsTextureHalf.None;

    /// <summary>
    /// Rescales a V coordinate into the cropped texture. The kept half becomes the whole
    /// 0…1 range, so the same UV addresses the same texels it did before.
    /// </summary>
    public float RemapV(float v)
    {
        float remapped = Half switch
        {
            SimsTextureHalf.Top    => v * 2f,
            SimsTextureHalf.Bottom => (v - 0.5f) * 2f,
            _                      => v,
        };
        // A UV a hair outside the half (the tolerance above) would land just outside 0…1.
        return Math.Clamp(remapped, 0f, 1f);
    }

    /// <summary>First row of the kept half, for a texture of this height.</summary>
    public int StartRow(int height) => Half == SimsTextureHalf.Bottom ? height - KeptHeight(height) : 0;

    /// <summary>Height of the kept half. Odd heights keep the larger part rather than losing a row.</summary>
    public int KeptHeight(int height) => Math.Max(height / 2, 1);

    public string Describe() => Half switch
    {
        SimsTextureHalf.Top    => "top half",
        SimsTextureHalf.Bottom => "bottom half",
        _                      => "no crop",
    };

    /// <summary>
    /// Works out which half every given mesh fits inside. Meshes that disagree, or a mesh
    /// that spans the middle, mean no crop is possible — the result has to hold for all of
    /// them at once, since they share one UV rescale.
    /// </summary>
    public static SimsHalfCrop Detect(IEnumerable<SimsGeomMesh> meshes)
    {
        float min = float.MaxValue, max = float.MinValue;

        foreach (var mesh in meshes)
        {
            if (mesh.UvChannels.Count == 0)
                continue;

            foreach (var uv in mesh.UvChannels[0])
            {
                if (uv.Y < min) min = uv.Y;
                if (uv.Y > max) max = uv.Y;
            }
        }

        if (min > max)
            return None;                                    // no UVs at all

        if (max <= 0.5f + Tolerance) return new SimsHalfCrop(SimsTextureHalf.Top);
        if (min >= 0.5f - Tolerance) return new SimsHalfCrop(SimsTextureHalf.Bottom);
        return None;
    }
}

/// <summary>Cropping helpers on a decoded texture.</summary>
public static class SimsTextureCrop
{
    /// <summary>
    /// Returns the kept half of <paramref name="texture"/>, or the texture itself when the
    /// crop is a no-op or the image is too small to halve.
    /// </summary>
    public static DecodedTexture Apply(DecodedTexture texture, SimsHalfCrop crop)
    {
        if (!crop.Crops || texture.Height < 2)
            return texture;

        int keptHeight = crop.KeptHeight(texture.Height);
        int startRow   = crop.StartRow(texture.Height);
        int stride     = texture.Width * 4;

        var rgba = new byte[stride * keptHeight];
        Buffer.BlockCopy(texture.Rgba, startRow * stride, rgba, 0, rgba.Length);

        return new DecodedTexture
        {
            Width        = texture.Width,
            Height       = keptHeight,
            Rgba         = rgba,
            SourceFormat = texture.SourceFormat,
        };
    }

    /// <summary>
    /// True when the half about to be discarded still holds something visible. This is
    /// only for the report: the crop follows the mesh's UVs, so content here is content
    /// nothing references — a Sims normal map, for instance, routinely fills the half its
    /// mesh never samples. Sampled on a grid rather than per pixel, which is ample for a
    /// yes/no note and keeps a sheet of 24 large diffuses quick to check.
    /// </summary>
    public static bool DiscardedHalfHasContent(DecodedTexture texture, SimsHalfCrop crop)
    {
        if (!crop.Crops || texture.Height < 2)
            return false;

        int keptHeight   = crop.KeptHeight(texture.Height);
        int keptStart    = crop.StartRow(texture.Height);
        int discardStart = keptStart == 0 ? keptHeight : 0;
        int discardEnd   = keptStart == 0 ? texture.Height : keptHeight;

        for (int y = discardStart; y < discardEnd; y += SampleStride)
        for (int x = 0; x < texture.Width; x += SampleStride)
        {
            if (texture.Rgba[(y * texture.Width + x) * 4 + 3] > AlphaFloor)
                return true;
        }

        return false;
    }

    /// <summary>Alpha at or below this counts as nothing being there.</summary>
    private const byte AlphaFloor = 8;

    private const int SampleStride = 4;
}
