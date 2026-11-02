using System.Text;
using InstantEdit.Services.Materials;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// A material whose shader section was not fully read is written with its raw sampler table, so only a
/// texture's path can change in it; and half floats in a colour table never become infinity.
/// </summary>
internal static class MaterialReadOnlyScenarios
{
    public static void Run()
    {
        ReadOnlyMaterialKeepsItsSamplers();
        HalfFloatsClamp();
    }

    private static void ReadOnlyMaterialKeepsItsSamplers()
    {
        var document = MaterialDocument.From(MtrlReader.Read(Material(trailingBytes: true)));
        Require(!document.Editable && document.Slots.Count == 3, "a material with bytes after its value block is read-only, with its three texture slots");

        Require(MaterialDocument.Same(document, document.WithoutSlot(0)),
            "removing a slot from it changes nothing: its sampler table would keep pointing at the old texture indices");
        Require(MaterialDocument.Same(document, document.WithAddedSlot(0x1DF8D3A5, "chara/new_s.tex")),
            "and adding one adds nothing: the new texture would have no sampler");

        var slot = document.Slots[0];
        var renamed = document.WithSlot(0, slot with { Path = "chara/other_d.tex", SamplerId = 0x12345678, SamplerSettings = 0 });
        var written = MtrlReader.Read(renamed.Write());
        var original = MtrlReader.Read(document.Write());
        Require(written.TextureOffsets[0].Path == "chara/other_d.tex" && written.TextureOffsets.Length == 3 &&
                written.Tail.AsSpan().SequenceEqual(original.Tail),
            "but a texture's path can still be changed, with the sampler table written as it was");

        var editable = MaterialDocument.From(MtrlReader.Read(Material(trailingBytes: false)));
        var smaller = editable.WithoutSlot(0);
        Require(editable.Editable && smaller.Slots.Count == 2 && smaller.TexturePaths.Count == 2 && editable.WithAddedSlot(0x1DF8D3A5, "chara/new_s.tex").Slots.Count == 4,
            "a material that was read in full still gains and loses slots");
    }

    private static void HalfFloatsClamp()
    {
        var row = new byte[8];
        HalfPacking.Set(row, 0, 90000f);
        HalfPacking.Set(row, 1, -1e9f);
        HalfPacking.Set(row, 2, float.NaN);
        HalfPacking.Set(row, 3, 0.5f);
        Require(HalfPacking.Get(row, 0) == 65504f && HalfPacking.Get(row, 1) == -65504f && HalfPacking.Get(row, 2) == 0f && HalfPacking.Get(row, 3) == 0.5f,
            "a colour table value beyond the half-float range is stored as the largest half float, not infinity, and NaN as zero");
    }

    /// <summary>Three textures read by three samplers, no keys, constants or colour table; with 4 stray bytes after the sampler table when asked.</summary>
    private static byte[] Material(bool trailingBytes)
    {
        string[] paths = ["a_d.tex", "b_n.tex", "c_m.tex"];
        var strings = new List<byte>();
        var textureOffsets = new uint[3];
        for (var i = 0; i < 3; i++)
        {
            textureOffsets[i] = (uint)strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(paths[i]));
            strings.Add(0);
        }
        var shaderOffset = (ushort)strings.Count;
        strings.AddRange(Encoding.UTF8.GetBytes("character.shpk"));
        strings.Add(0);
        while (strings.Count % 4 != 0)
            strings.Add(0);

        var body = new List<byte>();
        foreach (var offset in textureOffsets)
            body.AddRange(BitConverter.GetBytes(offset));
        body.AddRange(strings);
        body.AddRange(BitConverter.GetBytes((ushort)0));   // value block size
        body.AddRange(BitConverter.GetBytes((ushort)0));   // shader keys
        body.AddRange(BitConverter.GetBytes((ushort)0));   // constants
        body.AddRange(BitConverter.GetBytes((ushort)3));   // samplers
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        uint[] ids = [0x115306BE, 0x0C5EC1F1, 0x8A4E82B6];
        for (var i = 0; i < 3; i++)
        {
            body.AddRange(BitConverter.GetBytes(ids[i]));
            body.AddRange(BitConverter.GetBytes(0x000F8340u));
            body.Add((byte)i);
            body.AddRange(new byte[3]);
        }
        if (trailingBytes)
            body.AddRange(new byte[] { 1, 2, 3, 4 });   // bytes the reader does not account for

        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes(0x01030000u));
        file.AddRange(BitConverter.GetBytes((uint)((16 + body.Count) & 0xFFFF)));
        file.AddRange(BitConverter.GetBytes((ushort)strings.Count));
        file.AddRange(BitConverter.GetBytes(shaderOffset));
        file.AddRange(new byte[] { 3, 0, 0, 0 });
        file.AddRange(body);
        return file.ToArray();
    }
}
