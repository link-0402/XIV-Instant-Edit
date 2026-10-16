using System.Text;
using InstantEdit.Services.Materials;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The Material editor's document: reads a material, edits it in snapshots, writes it back byte for byte.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunDocument()
    {
        CheckRoundTripPresets();
        CheckRoundTripLibrary();
        CheckConstantAddRemove();
        CheckSlotAddRemove();
        CheckStringPadding();
        CheckSnapshotsStayApart();
    }

    internal static MaterialDocument PresetDocument(string relative) => MaterialDocument.From(Preset(relative));

    private static void CheckRoundTripPresets()
    {
        var files = Directory.GetFiles(PresetPath(""), "*.mtrl", SearchOption.AllDirectories);
        var mismatches = files.Where(file =>
        {
            var bytes = File.ReadAllBytes(file);
            return !MaterialDocument.From(MtrlReader.Read(bytes)).Write().SequenceEqual(bytes);
        }).Select(Path.GetFileName).ToList();
        Require(files.Length >= 10 && mismatches.Count == 0,
            $"all {files.Length} bundled preset materials write back byte for byte" + (mismatches.Count > 0 ? ": " + string.Join(", ", mismatches) : ""));
        Require(PresetDocument(HairPreset).Table == null && PresetDocument(MaskPreset).Table is { Kind: ColorTableKind.Dawntrail },
            "the hair preset opens without a colour table, the Mask preset with its Dawntrail table");
    }

    /// <summary>Every .mtrl in a mod library folder (IE_MTRL_LIBRARY, e.g. G:\Penumbra); skipped when unset.</summary>
    private static void CheckRoundTripLibrary()
    {
        var root = Environment.GetEnvironmentVariable("IE_MTRL_LIBRARY");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            Console.WriteLine("[SKIP] library round trip: set IE_MTRL_LIBRARY to a mod folder to run it");
            return;
        }
        var count = 0;
        var skipped = 0;
        var mismatches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.mtrl", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { skipped++; continue; }
            if (!MtrlReader.LooksLikeMtrl(bytes)) { skipped++; continue; }
            count++;
            try
            {
                if (!MaterialDocument.From(MtrlReader.Read(bytes)).Write().SequenceEqual(bytes))
                    mismatches.Add(file);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                mismatches.Add($"{file} ({e.GetType().Name}: {e.Message})");
            }
        }
        Console.WriteLine($"library round trip: {count} materials, {skipped} skipped, {mismatches.Count} mismatches");
        foreach (var file in mismatches.Take(20))
            Console.WriteLine("  mismatch: " + file);
        Require(count > 0 && mismatches.Count == 0, $"all {count} library materials write back byte for byte");
    }

    private static void CheckConstantAddRemove()
    {
        var mask = PresetDocument(MaskPreset);
        var withSheen = mask.WithConstant(0x800EE35F, [0.2f]);
        Require(withSheen.Constants.Any(constant => constant.Id == 0x800EE35F && constant.Values.SequenceEqual([0.2f])),
            "a new constant is added to the document");
        var reread = MaterialDocument.From(MtrlReader.Read(withSheen.Write()));
        Require(reread.Source.ShaderDataParsed && reread.Constants.Single(constant => constant.Id == 0x800EE35F).Values.SequenceEqual([0.2f])
                && reread.Constants.Count == mask.Constants.Count + 1,
            "the added constant is written and reads back with its value");

        var changed = mask.WithConstant(0xD62BF368, [3.5f]);
        Require(MaterialDocument.From(MtrlReader.Read(changed.Write())).Constants.Single(constant => constant.Id == 0xD62BF368).Values.SequenceEqual([3.5f])
                && changed.Constants.Count == mask.Constants.Count,
            "changing an existing constant writes the new value in place");

        var source = mask.Source.Constants.ToDictionary(constant => constant.Id, constant => constant.Offset);
        var without = mask.WithoutConstant(0x29AC0223);
        var written = MtrlReader.Read(without.Write());
        Require(written.ShaderDataParsed && written.Constants.All(constant => constant.Id != 0x29AC0223)
                && written.Constants.All(constant => constant.Offset == source[constant.Id] - 4)
                && written.ShaderValues.Length == mask.Source.ShaderValues.Length - 4,
            "removing the alpha cutoff drops it and moves every later constant 4 bytes down");
        Require(MaterialDocument.From(written).Constants.Single(constant => constant.Id == 0xD62BF368).Values.SequenceEqual([2f]),
            "the other constants keep their values after a removal");
    }

    private static void CheckSlotAddRemove()
    {
        var mask = PresetDocument(MaskPreset);
        var added = mask.WithAddedSlot(0xFEA0F3D2, "chara/x_fx.tex");
        var written = MtrlReader.Read(added.Write());
        var catchlight = written.Samplers.Single(sampler => sampler.Id == 0xFEA0F3D2);
        Require(added.Slots.Count == 4 && added.Slots[3] is { SamplerId: 0xFEA0F3D2, Path: "chara/x_fx.tex" }
                && written.TextureOffsets.Length == 4 && catchlight.TextureIndex == 3 && written.TextureOffsets[3].Path == "chara/x_fx.tex"
                && catchlight.Settings == mask.Source.Samplers.Single(sampler => sampler.Id == 0x0C5EC1F1).Settings,
            "adding a slot adds texture 3 and a sampler on it with the normal map's settings");

        var removed = mask.WithoutSlot(0);
        var after = MtrlReader.Read(removed.Write());
        Require(removed.Slots.Count == 2 && after.TextureOffsets.Length == 2 && after.Samplers.All(sampler => sampler.TextureIndex < after.TextureOffsets.Length)
                && removed.Slots.Select(slot => slot.Path).SequenceEqual(mask.Slots.Skip(1).Select(slot => slot.Path)),
            "removing the first slot renumbers the others so none points past the end");

        var slot = mask.Slots[1];
        var retargeted = mask.WithSlot(1, slot with { Path = "chara/common/texture/other_m.tex", Dx11 = true, SamplerSettings = slot.SamplerSettings ^ 0x3 });
        var retargetedFile = MtrlReader.Read(retargeted.Write());
        Require(retargeted.Slots[1] is { Path: "chara/common/texture/other_m.tex", Dx11: true } && retargetedFile.TextureOffsets[1].Dx11
                && retargetedFile.Samplers[1].Settings == (slot.SamplerSettings ^ 0x3),
            "a slot's path, DX11 flag and sampler settings change together");
        Require(new MaterialSlot(0, "", false, 0x000F8355) is { WrapU: 1, WrapV: 1 } && new MaterialSlot(0, "", false, 0x3FFu << 10).LodBias == -1f / 64f,
            "sampler settings expose wrap U/V and a signed LoD bias");
    }

    private static void CheckStringPadding()
    {
        var bytes = PaddedMaterial();
        var document = MaterialDocument.From(MtrlReader.Read(bytes));
        Require(document.Source.ShaderDataParsed && document.Write().SequenceEqual(bytes),
            "a material with its shader name first and a padded string block writes back byte for byte");

        var moved = document.WithSlot(0, document.Slots[0] with { Path = "chara/a_much_longer_path.tex" });
        var written = moved.Write();
        var stringsSize = BitConverter.ToUInt16(written, 8);
        var reread = MtrlReader.Read(written);
        Require(stringsSize % 4 == 0 && reread.ShaderDataParsed && reread.TextureOffsets[0].Path == "chara/a_much_longer_path.tex"
                && reread.ShaderPackageName == "character.shpk",
            "a changed path writes a 4-byte-aligned string block");
    }

    private static void CheckSnapshotsStayApart()
    {
        var mask = PresetDocument(MaskPreset);
        var before = mask.Write();
        var table = mask.Table!.Clone();
        table.Rows[0].Metalness = 1f;
        var metal = mask.WithTable(table).WithKey(0xB616DC5A, 0x600EF9DF).WithFlags(0x1D).WithShaderPackage("characterscroll.shpk");
        Require(mask.Write().SequenceEqual(before) && metal.Table!.Rows[0].Metalness == 1f && mask.Table!.Rows[0].Metalness == 0f,
            "editing a snapshot leaves the one it came from untouched");
        var reread = MaterialDocument.From(MtrlReader.Read(metal.Write()));
        Require(reread.ShaderPackage == "characterscroll.shpk" && reread.ShaderFlags == 0x1D && reread.Keys.Contains((0xB616DC5Au, 0x600EF9DFu))
                && reread.Table!.Rows[0].Metalness == 1f && !MaterialDocument.Same(mask, metal) && MaterialDocument.Same(mask, PresetDocument(MaskPreset)),
            "shader, flags, keys and table edits are written");
        var noTable = mask.WithTable(null);
        var bare = MtrlReader.Read(noTable.Write());
        Require(bare.DataSet.Length == 0 && TableFlags.Read(bare.AdditionalData) is { HasTable: false, HasDyeTable: false } && noTable.WithoutKey(0xF52CCF05).Keys.Count == mask.Keys.Count - 1,
            "removing the table clears its flags and data; removing a key drops it");
    }

    /// <summary>A small parsed material whose string block starts with the shader name and is padded to 4 bytes.</summary>
    private static byte[] PaddedMaterial()
    {
        var strings = new List<byte>(Encoding.UTF8.GetBytes("character.shpk\0chara/a.tex\0"));
        while (strings.Count % 4 != 0)
            strings.Add(0);
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)15 | 0x8000u << 16)); // texture 0 at offset 15, DX11
        body.AddRange(strings);
        body.AddRange(new byte[] { 0, 0, 0, 0 });                         // no colour table
        body.AddRange(BitConverter.GetBytes((ushort)4));                  // values size
        body.AddRange(BitConverter.GetBytes((ushort)0));                  // keys
        body.AddRange(BitConverter.GetBytes((ushort)1));                  // constants
        body.AddRange(BitConverter.GetBytes((ushort)1));                  // samplers
        body.AddRange(BitConverter.GetBytes((ushort)0x0D));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(BitConverter.GetBytes(0x29AC0223u));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(BitConverter.GetBytes((ushort)4));
        body.AddRange(BitConverter.GetBytes(0x0C5EC1F1u));
        body.AddRange(BitConverter.GetBytes(0x000F8355u));
        body.AddRange(new byte[] { 0, 0, 0, 0 });
        body.AddRange(BitConverter.GetBytes(0.5f));

        var header = new List<byte>();
        header.AddRange(BitConverter.GetBytes(MtrlReader.Signature));
        header.AddRange(BitConverter.GetBytes((ushort)(16 + body.Count)));
        header.AddRange(BitConverter.GetBytes((ushort)0));                // data set size
        header.AddRange(BitConverter.GetBytes((ushort)strings.Count));
        header.AddRange(BitConverter.GetBytes((ushort)0));                // shader name offset
        header.AddRange(new byte[] { 1, 0, 0, 4 });                       // textures, uv sets, colour sets, additional data
        return header.Concat(body).ToArray();
    }
}
