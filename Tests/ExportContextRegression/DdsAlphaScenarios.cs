using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// A 32-bit .dds without an alpha channel carries garbage, usually zero, in its fourth byte. The
/// game reads that byte as alpha, so it has to come out as 255.
/// </summary>
internal static class DdsAlphaScenarios
{
    public static void Run(string root)
    {
        // One pixel, written B, G, R, A in memory with the alpha byte 0x4D.
        var pixel = new byte[] { 0x10, 0x20, 0x30, 0x4D };

        Require(AlphaOf(root, "no-alpha.dds", Legacy(rMask: 0x00FF0000, aMask: 0, pixel)) == 255,
            "an X8R8G8B8 .dds, with no alpha mask, comes out opaque whatever its fourth byte holds");
        Require(AlphaOf(root, "with-alpha.dds", Legacy(rMask: 0x00FF0000, aMask: 0xFF000000, pixel)) == 0x4D,
            "an A8R8G8B8 .dds keeps its alpha");
        Require(AlphaOf(root, "rgba-no-alpha.dds", Legacy(rMask: 0x000000FF, aMask: 0, pixel)) == 255,
            "and so does an RGB-ordered one without an alpha mask");
        Require(AlphaOf(root, "rgba.dds", Legacy(rMask: 0x000000FF, aMask: 0xFF000000, pixel)) == 0x4D,
            "an RGBA-ordered .dds with an alpha mask keeps its alpha");
        Require(AlphaOf(root, "x8.dds", Dx10(88, pixel)) == 255, "a DXGI B8G8R8X8 .dds is opaque");
        Require(AlphaOf(root, "a8.dds", Dx10(87, pixel)) == 0x4D, "a DXGI B8G8R8A8 .dds keeps its alpha");
    }

    /// <summary>The alpha byte of the one pixel in the .tex the file converts to, or -1 when it does not convert.</summary>
    private static int AlphaOf(string root, string name, byte[] dds)
    {
        var path = Path.Combine(root, "dds-alpha-" + name);
        File.WriteAllBytes(path, dds);
        return TextureConverter.Convert(path, TextureCompression.None, out var tex, out _) && tex.Length >= 84 ? tex[83] : -1;
    }

    private static byte[] Legacy(uint rMask, uint aMask, byte[] pixel)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Header(bw);
        bw.Write(32);
        bw.Write(aMask == 0 ? 0x40u : 0x41u);   // DDPF_RGB, plus DDPF_ALPHAPIXELS when there is an alpha mask
        bw.Write(0u);                           // no FourCC
        bw.Write(32u);
        bw.Write(rMask);
        bw.Write(0x0000FF00u);
        bw.Write(rMask == 0x000000FF ? 0x00FF0000u : 0x000000FFu);
        bw.Write(aMask);
        bw.Write(0x1000u);
        bw.Write(new byte[16]);
        bw.Write(pixel);
        return ms.ToArray();
    }

    private static byte[] Dx10(uint dxgi, byte[] pixel)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        Header(bw);
        bw.Write(32);
        bw.Write(0x4u);
        bw.Write(0x30315844u);   // DX10
        bw.Write(new byte[20]);
        bw.Write(0x1000u);
        bw.Write(new byte[16]);
        bw.Write(dxgi);
        bw.Write(3u);
        bw.Write(0u);
        bw.Write(1u);
        bw.Write(0u);
        bw.Write(pixel);
        return ms.ToArray();
    }

    /// <summary>Magic and the header up to the pixel format, for a 1×1 texture with one mip.</summary>
    private static void Header(BinaryWriter bw)
    {
        bw.Write(0x20534444u);
        bw.Write(124);
        bw.Write(0x1007u);
        bw.Write(1);            // height
        bw.Write(1);            // width
        bw.Write(0);
        bw.Write(0);
        bw.Write(1);            // mip count
        bw.Write(new byte[44]);
    }
}
