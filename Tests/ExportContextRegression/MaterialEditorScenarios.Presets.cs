using InstantEdit.Services.Materials;
using InstantEdit.Services.Materials.Presets;
using InstantEdit.Services.Previews;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The one-click effects: each turns the bundled Mask material into its effect and says what else it needs.</summary>
internal static partial class MaterialEditorScenarios
{
    private const uint CatchlightSampler = 0xFEA0F3D2;

    public static void RunPresets()
    {
        CheckMetal();
        CheckTranslucentCrashes();
        CheckScrollingPattern();
        CheckModelUv2();
        CheckModelChangeSaves();
        CheckGlass();
        CheckCleanUp();
        CheckUsedRowsTarget();
        CheckEveryPreset();
    }

    private static PresetContext Context(MaterialDocument doc, IndexUsage? usage = null, DecodedTexture? mask = null, int? pair = null)
        => new(doc, ShaderPackages.Get(doc.ShaderPackage)!, usage, pair, mask, "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a.mtrl");

    private static MaterialEffectPreset PresetNamed(string name) => EffectPresets.All.Single(preset => preset.Name == name);

    private static void CheckMetal()
    {
        var metal = PresetNamed("Metal");
        var result = metal.Apply(Context(PresetDocument(MaskPreset), mask: Solid(4, 255, 128)), RowTarget.All);
        var rows = result.Doc.Table!.Rows;
        Require(rows.All(row => row.Metalness == 1f && Math.Abs(row.Roughness - 0.2f) < 1e-3f && row.Specular == row.Diffuse) && result.Changes.Count > 0
                && result.Warnings.Count == 0,
            "Metal makes all 32 rows metal with roughness 20% and a shine of their own colour; a smooth, bright mask raises no warning");
        var rough = metal.Apply(Context(PresetDocument(MaskPreset), mask: Solid(4, 255, 255)), RowTarget.All);
        Require(rough.Warnings.Any(warning => warning.Contains("green", StringComparison.Ordinal)), "Metal warns when the mask's green channel is rough");
        Require(metal.Apply(Context(PresetDocument(MaskPreset)), RowTarget.All).Warnings.Any(warning => warning.Contains("mask", StringComparison.OrdinalIgnoreCase)),
            "Metal says so when the mask couldn't be checked");
        Require(!metal.Applies(Context(PresetDocument(HairPreset))), "Metal needs colour rows, which hair has none of");
    }

    private static void CheckTranslucentCrashes()
    {
        // Translucent skin crashes the game (seen in game and in Penumbra's editor, 2026-10-17), like stockings.
        Require(PresetChecks.TranslucentCrashes("skin.shpk") && PresetChecks.TranslucentCrashes("characterstockings.shpk")
                && !PresetChecks.TranslucentCrashes("character.shpk") && !PresetChecks.TranslucentCrashes("hair.shpk"),
            "translucency is refused on skin and stockings only");
        Require(!PresetNamed("See-through").Applies(Context(PresetDocument(MaskPreset).WithShaderPackage("skin.shpk"))),
            "See-through doesn't apply to skin");
    }

    private static void CheckScrollingPattern()
    {
        var result = PresetNamed("Scrolling pattern").Apply(Context(PresetDocument(MaskPreset)), RowTarget.All);
        var doc = result.Doc;
        var catchlight = doc.Slots.SingleOrDefault(slot => slot.SamplerId == CatchlightSampler);
        Require(doc.ShaderPackage == "characterscroll.shpk" && doc.KeyValue(0xF886E10E) == 0x9A8A46F5
                && catchlight is { Path: "chara/equipment/e0085/material/v0002/mt_c0201e0085_glv_a_fx.tex" },
            "Scrolling pattern moves to the scroll shader, reads the catchlight texture and adds one named after the material");
        Require(doc.Constant(0x738A241C)?.Values.SequenceEqual([0.2f]) == true && doc.Constant(0x71CC9A45)?.Values.SequenceEqual([0.2f]) == true
                && doc.Constant(0x43345395)?.Values.SequenceEqual([1.5f]) == true && doc.Table!.Rows.All(row => row.ScrollLayer == 1f),
            "layer 1 moves at 0.2 and repeats 1.5 times, and every row shows layer 1");
        Require(doc.Slots.Select(slot => slot.SamplerId).ToHashSet().SetEquals([0x0C5EC1F1u, 0x8A4E82B6u, 0x565F8FD8u, CatchlightSampler]),
            "the result has the Hair RGB Shader mod's shape: normal, mask, index and catchlight");
        Require(doc.Table!.Rows.All(row => MathF.Max(row.Emissive.X, MathF.Max(row.Emissive.Y, row.Emissive.Z)) > 0f),
            "every scrolling row glows, since the pattern only shows through the glow");
        Require(PresetChecks.ScrollGlow(DtColorRow.Default with { Emissive = new System.Numerics.Vector3(0.5f, 0f, 0f) }) == new System.Numerics.Vector3(0.5f, 0f, 0f)
                && PresetChecks.ScrollGlow(DtColorRow.Default with { Diffuse = new System.Numerics.Vector3(0.2f, 0.4f, 0.6f) }) == new System.Numerics.Vector3(0.2f, 0.4f, 0.6f)
                && PresetChecks.ScrollGlow(DtColorRow.Default with { Diffuse = System.Numerics.Vector3.Zero }) == System.Numerics.Vector3.One,
            "a glowing row keeps its glow, a dark one glows in its base colour, a black one in white");
        Require(result.NewTextures.Count == 1 && result.NewTextures[0].GamePath == catchlight?.Path && MtrlTexLooksValid(result.NewTextures[0].Bytes),
            "the pattern texture comes with the preset");
        var pixels = result.NewTextures[0].Bytes.AsSpan(80);
        var greys = new HashSet<byte>();
        var grey = true;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            grey &= pixels[i] == pixels[i + 1] && pixels[i] == pixels[i + 2] && pixels[i + 3] == 255;
            greys.Add(pixels[i]);
        }
        Require(grey && greys.Contains(0) && greys.Contains(255) && greys.Count > 64 && result.NewTextures[0].Bytes.AsSpan().SequenceEqual(PresetChecks.ScrollPattern()),
            "the pattern is grey (the glow colour tints it), runs from black to white, and is the same every time");

        var session = MaterialSession.CreateForRegression(Source(InstantEdit.Models.ResourceSourceState.LoadedMod), PresetDocument(MaskPreset), (_, _) => Task.CompletedTask);
        session.AddNewFile(result.NewTextures[0].GamePath, result.NewTextures[0].Bytes);
        Require(session.NewFilesFor(doc).Single().GamePath == catchlight?.Path && session.NewFilesFor(session.Current).Count == 0,
            "a preset's texture is previewed and saved only while a slot of the material reads it");
    }

    private static bool MtrlTexLooksValid(byte[] bytes)
    {
        try
        {
            var decoded = TextureDecoder.Decode(bytes);
            return decoded.SourceWidth == 256 && decoded.SourceHeight == 256;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private static void CheckGlass()
    {
        var doc = PresetNamed("Glass").Apply(Context(PresetDocument(MaskPreset)), RowTarget.All).Doc;
        Require(doc.ShaderPackage == "characterglass.shpk" && doc.KeyValue(0xB616DC5A) == null && (doc.ShaderFlags & 0x10) != 0
                && doc.Constant(0x7801E004)?.Values.SequenceEqual([1.5f]) == true,
            "Glass moves to the glass shader without the texture mode key, turns translucency on and sets the refraction");
    }

    private static void CheckCleanUp()
    {
        var glass = PresetDocument("characterglass/Glasses.mtrl").WithKey(0xD2777173, 0xF35F5131).WithKey(0xB616DC5A, 0x600EF9DF).WithConstant(0x36080AD0, [1f]);
        var clean = PresetNamed("Clean up").Apply(Context(glass), RowTarget.All);
        Require(clean.Doc.KeyValue(0xD2777173) is null or 0x4242B842 && clean.Doc.KeyValue(0xB616DC5A) == null && clean.Doc.Constant(0x36080AD0) == null
                && clean.Changes.Count >= 3,
            "Clean up drops a decal value glass has no shader for, a key glass doesn't have and a constant it ignores, and lists them");
        var mask = PresetDocument(MaskPreset).WithKey(0xF52CCF05, 0xDFE74BAC);
        Require(PresetNamed("Clean up").Apply(Context(mask), RowTarget.All).Doc.KeyValue(0xF52CCF05) == 0xA7D2FF60,
            "Clean up sets the vertex colour key to the value mods use");
    }

    private static void CheckUsedRowsTarget()
    {
        var used = new bool[32];
        used[0] = used[1] = true;
        var share = new float[32];
        share[0] = share[1] = 0.5f;
        var usage = new IndexUsage(share, used, 1, 1, [0]);
        var mask = PresetDocument(MaskPreset);
        var result = PresetNamed("Metal").Apply(Context(mask, usage), RowTarget.Used);
        var rows = result.Doc.Table!.Rows;
        Require(rows[0].Metalness == 1f && rows[1].Metalness == 1f && rows.Skip(2).All(row => row.Metalness == mask.Table!.Rows[0].Metalness),
            "the Used rows target changes only the rows the index texture uses");
        var selected = PresetNamed("Metal").Apply(Context(mask, pair: 5), RowTarget.Selected).Doc.Table!.Rows;
        Require(selected[10].Metalness == 1f && selected[11].Metalness == 1f && selected.Where((_, i) => i is not (10 or 11)).All(row => row.Metalness == 0f),
            "the Selected pair target changes only that pair's two rows");
    }

    private static void CheckEveryPreset()
    {
        var names = EffectPresets.All.Select(preset => preset.Name).ToArray();
        Require(names.SequenceEqual(["Metal", "Glow", "Scrolling pattern", "Glass", "See-through", "Matcap", "Dyeable", "Detail pattern", "Velvet", "Clean up"]),
            "the presets are the spec's ten, in its order");
        var mask = PresetDocument(MaskPreset);
        var failures = new List<string>();
        foreach (var preset in EffectPresets.All)
        {
            var context = Context(mask);
            if (!preset.Applies(context))
                continue;
            var result = preset.Apply(context, RowTarget.All);
            var reread = MaterialDocument.From(InstantEdit.Studio.Services.MtrlReader.Read(result.Doc.Write()));
            if (!reread.Source.ShaderDataParsed || preset.Description.Contains('…') || preset.Description.Contains("...", StringComparison.Ordinal))
                failures.Add(preset.Name);
        }
        Require(failures.Count == 0, "every preset that applies to the Mask material writes a readable file" + (failures.Count > 0 ? ": " + string.Join(", ", failures) : ""));
        var before = Context(mask).Doc.Table!.Rows;
        var glowed = PresetNamed("Glow").Apply(Context(mask), RowTarget.All).Doc.Table!.Rows;
        Require(glowed.Zip(before).All(pair => pair.First.Emissive == pair.Second.Diffuse * 2f && pair.First.Exposure == pair.Second.Exposure),
            "Glow sets every row's glow to twice its base colour and keeps half 11 (it brightens the base colour, not the glow)");
        Require(PresetNamed("Dyeable").Apply(Context(mask), RowTarget.All).Doc.Table!.Dye!.All(dye => dye.Flags == 0x37),
            "Dyeable lets the dye change base, shine, glow, metalness and roughness");
    }
}
