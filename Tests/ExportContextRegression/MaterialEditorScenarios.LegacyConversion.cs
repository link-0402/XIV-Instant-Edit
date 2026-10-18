using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Materials;
using InstantEdit.Services.Previews;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Moving a material from characterlegacy.shpk to character.shpk: rows read with the legacy shader's
/// fields, the mask rebuilt for character.shpk, keys, slots and constants fitted, dye, and pasting rows
/// between the two shaders.
/// </summary>
internal static partial class MaterialEditorScenarios
{
    private const string LegacyMaskPreset = "characterlegacy/Default Equipment - Mask.mtrl";
    private const string LegacyCompatibilityPreset = "characterlegacy/Default Equipment - Diffuse + Specular.mtrl";
    private const string LegacyShineMaskPreset = "characterlegacy/Default Equipment - Diffuse + Mask.mtrl";
    private static readonly Vector3 ShaderLuma = new(0.29891f, 0.58661f, 0.11448f);

    public static void RunLegacyConversion()
    {
        CheckLegacyShaderRow();
        CheckLegacyDye();
        CheckLegacyMaskTexels();
        CheckLegacyMaskTexture();
        CheckLegacyDocument();
        CheckLegacySession();
        CheckLegacyPaste();
        CheckLegacyLibrary();
    }

    private static DtColorRow LegacyShaderRow(float gloss, float strength, Vector3 specular) => DtColorRow.Default with
    {
        Diffuse = new Vector3(0.6f, 0.3f, 0.1f), Scalar3 = gloss, Specular = specular, Scalar7 = strength,
        Emissive = new Vector3(0.25f, 0f, 0.5f), Exposure = 0f, SheenRate = 0.1f, Roughness = 0.5f, ShaderId = 0,
        TileIndex = 9, TileAlpha = 0.75f, TileUU = 32f, TileUV = 1f, TileVU = -1f, TileVV = 24f,
    };

    private static void CheckLegacyShaderRow()
    {
        var specular = new Vector3(0.4f, 0.2f, 0.8f);
        var legacy = LegacyShaderRow(20f, 0.5f, specular);
        var look = LegacyLook.FromLegacyShader(legacy);
        Require(look.Gloss == legacy.Scalar3 && look.ShineStrength == legacy.Scalar7 && look.TileAlpha == legacy.TileAlpha,
            "characterlegacy.shpk reads a 32-row row's gloss from half 3, its shine strength from half 7, and its detail strength");

        var row = LegacyConversion.ToCharacter(look);
        Require(MathF.Abs(row.Roughness - float.Exp2(-20f / 15f)) < 1e-6f,
            "gloss 20 becomes roughness 2^(-20/15), what the legacy G-buffer pass writes");
        Require(row.Exposure == 1f && row.Metalness == 0f && row.Anisotropy == 0f && row.SphereMapMask == 0f,
            "exposure (half 11 scales the base colour on character.shpk) is 1, and metal, the angle blend and reflections are off");
        Require(MathF.Abs(row.SheenRate - 0.25f * 0.5f * Vector3.Dot(specular, ShaderLuma)) < 1e-6f && row.SheenTint == 0f && row.SheenAperture == 4f,
            "a white sheen at a quarter of the legacy shine (strength x luma) and aperture 4 stands in for legacy's room reflections at the edges");
        Require(row.ShaderId == LegacyConversion.LegacyLightingShaderId && LegacyConversion.LegacyLightingShaderId == 12,
            "rows get shader id 12: character.shpk writes lighting type 32 + id, legacy writes 44");
        Require(row.Diffuse == legacy.Diffuse && row.Emissive == legacy.Emissive && row.TileIndex == 9 && row.TileAlpha == 0.75f
                && row.TileUU == 32f && row.TileUV == 1f && row.TileVU == -1f && row.TileVV == 24f,
            "base and glow colours and the detail pattern carry over");
        var product = Vector3.Dot(row.Specular, ShaderLuma) * row.Specular;
        Require(Vector3.Distance(product, 0.5f * specular) < 1e-4f,
            "luma(shine) x shine on character.shpk equals strength x shine on the legacy shader");
        Require(Vector3.Normalize(row.Specular) is var direction && Vector3.Distance(direction, Vector3.Normalize(specular)) < 1e-5f,
            "the shine keeps its hue");

        Require(LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(LegacyShaderRow(20f, 0f, specular))) is { Specular: var none, SheenRate: 0f } && none == Vector3.Zero
                && LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(LegacyShaderRow(20f, 1f, Vector3.Zero))).Specular == Vector3.Zero,
            "no shine strength or a black shine colour gives no shine and no sheen");
        Require(LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(LegacyShaderRow(-10f, 1f, specular))).Roughness == 1f,
            "a negative gloss stops at roughness 1");

        var white = LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(LegacyShaderRow(30f, 1f, Vector3.One)));
        Require(Vector3.Distance(white.Specular, Vector3.One) < 1e-3f && MathF.Abs(white.SheenRate - 0.25f) < 1e-3f,
            "a white shine at full strength stays white, with the full 25% sheen");

        var old = new LegacyColorRow
        {
            Diffuse = new Vector3(0.5f), SpecularMask = 0.25f, Specular = new Vector3(0.5f), Gloss = 30f, Emissive = new Vector3(0.1f),
            TileIndex = 4, TileUU = 16f, TileVV = 16f,
        };
        var fromOld = old.ToDawntrail();
        Require(MathF.Abs(fromOld.Roughness - float.Exp2(-2f)) < 1e-6f && fromOld.TileAlpha == 1f && fromOld.TileIndex == 4
                && Vector3.Distance(Vector3.Dot(fromOld.Specular, ShaderLuma) * fromOld.Specular, 0.25f * old.Specular) < 1e-4f,
            "a 16-row row (strength in half 3, gloss in half 7) converts the same way");
    }

    private static void CheckLegacyDye()
    {
        var dye = LegacyConversion.DyeToCharacter(0b11111, 205, 1);
        Require(dye.Flags == 0b100111 && dye.Template == 1205 && dye.Channel == 1,
            "a legacy dye row keeps base, shine and glow, dyes roughness for gloss, drops shine strength, and takes template t + 1000");
        Require(LegacyConversion.DyeToCharacter(0b1, 1500, 0).Template == 1500 && LegacyConversion.DyeToCharacter(0, 0, 0).Template == 0,
            "a Dawntrail template id or none stays as it is");

        var table = ColorTable.NewDawntrail(dyeable: true);
        table.Rows[4] = LegacyShaderRow(20f, 0.5f, Vector3.One);
        table.Dye![4] = new DtDyeRow(0).WithFlags(0b11001).WithTemplate(300).WithChannel(1);
        var converted = LegacyConversion.TableToCharacter(table);
        Require(converted.Dye![4] is { Flags: 0b100001, Template: 1300, Channel: 1 } && converted.Dye[5] == table.Dye[5] && converted.Rows[4].Exposure == 1f,
            "a whole 32-row table converts row and dye by row; undyed rows stay undyed");

        var dyed = StainTemplates.ApplyLegacyDye(table.Rows[4], new DtDyeRow(0).WithFlags(0b11010),
            new LegacyDyePack(Vector3.One, new Vector3(0.1f), new Vector3(0.2f), 7f, 0.3f));
        Require(dyed.Scalar3 == 7f && dyed.Scalar7 == 0.3f && dyed.Specular == new Vector3(0.1f) && dyed.Diffuse == table.Rows[4].Diffuse && dyed.Emissive == table.Rows[4].Emissive,
            "a legacy-template dye on characterlegacy.shpk sets gloss (bit 3) in half 3 and shine strength (bit 4) in half 7");
    }

    private static void CheckLegacyMaskTexels()
    {
        Require(LegacyConversion.MaskTexel(LegacyMaskKind.ShineColour, 1f, 1f, 1f) == (1f, 0.5f, 1f)
                && LegacyConversion.MaskTexel(LegacyMaskKind.Multi, 1f, 1f, 1f) == (1f, 0.5f, 1f),
            "a white legacy mask becomes red 1, green 0.5 (roughness unchanged), blue 1");
        var (r, g, b) = LegacyConversion.MaskTexel(LegacyMaskKind.ShineColour, 0.8f, 0.4f, 0.2f);
        var legacy = Vector3.Dot(new Vector3(0.64f, 0.16f, 0.04f), ShaderLuma);
        Require(MathF.Abs(r * r * r * r - legacy) < 1e-5f && g == 0.5f && b == 1f,
            "a shine colour map: red^4 is the luma of the map squared, the factor legacy multiplies the shine by");
        var (mr, _, mb) = LegacyConversion.MaskTexel(LegacyMaskKind.Multi, 0.5f, 0.8f, 0.3f);
        Require(MathF.Abs(mr * mr * mr * mr - 0.25f * 0.64f) < 1e-5f && mb == 0.3f,
            "multi-material: red^4 = red^2 x green^2 (strength and colour), blue stays the base-colour factor");
        Require(LegacyConversion.MaskTexel(LegacyMaskKind.ShineMask, 0.6f, 0.9f, 0.1f) == (0.6f, 0.5f, 1f),
            "the Mask shine type keeps red, drops the velvet amount in blue");

        var doc = PresetDocument(LegacyMaskPreset);
        Require(LegacyConversion.MaskKind(doc) == LegacyMaskKind.Multi
                && LegacyConversion.MaskKind(PresetDocument(LegacyCompatibilityPreset)) == LegacyMaskKind.ShineColour
                && LegacyConversion.MaskKind(PresetDocument(LegacyShineMaskPreset)) == LegacyMaskKind.ShineMask,
            "the keys pick the mask kind: multi-material, Compatibility, Compatibility with the Mask shine type");
    }

    private static void CheckLegacyMaskTexture()
    {
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255];
        var tex = LegacyConversion.MaskTexture(LegacyMaskKind.Multi, new DecodedTexture(bgra, 2, 2, 2, 2, 0, 1));
        var header = TextureFiles.ReadTex(tex);
        Require(header is { Width: 2, Height: 2, Mips: 2 } && tex.Length == 80 + 16 + 4,
            "the new mask is a BGRA texture of the same size with a full mip chain");
        // Texel 3 is white: red 1, green 0.5, blue 1. Texel 0 is pure red: multi gives red sqrt(1 x 0) = 0.
        Require(tex[80 + 12] == 255 && tex[80 + 13] == 128 && tex[80 + 14] == 255 && tex[80 + 2] == 0 && tex[80 + 3] == 255,
            "texels are written blue, green, red, alpha");
        var flat = TextureFiles.ReadTex(LegacyConversion.MaskTexture(LegacyMaskKind.ShineColour, null));
        Require(flat is { Width: 4, Height: 4, Mips: 3 }, "with no mask, a small white-equivalent one is made");
    }

    private static void CheckLegacyDocument()
    {
        const string newMask = "chara/equipment/e0399/texture/v01_c0201e0399_top_m_ie.tex";
        foreach (var preset in new[] { LegacyMaskPreset, LegacyCompatibilityPreset, LegacyShineMaskPreset })
        {
            var doc = PresetDocument(preset);
            var (converted, notes) = LegacyConversion.ToCharacter(doc, newMask);
            Require(converted.ShaderPackage == "character.shpk" && converted.KeyValue(0xC8BD1DEF) == null && converted.KeyValue(0x40D1481E) == 0x337C6BC4
                    && converted.KeyValue(TextureMode) == (doc.KeyValue(TextureMode) ?? 0x5CC605B5),
                $"{preset}: on character.shpk, without the shine-type key, with the flow key at Standard and the same texture mode");
            Require(converted.Slots.Single(slot => slot.SamplerId == LegacyConversion.MaskSampler).Path == newMask
                    && converted.Slots.Count == doc.Slots.Count
                    && converted.Slots.Where(slot => slot.SamplerId != LegacyConversion.MaskSampler).Select(slot => slot.Path)
                        .SequenceEqual(doc.Slots.Where(slot => slot.SamplerId != LegacyConversion.MaskSampler).Select(slot => slot.Path)),
                $"{preset}: the mask slot reads the new mask; the other textures stay");
            Require(converted.Constant(0xB5545FBB) == null && converted.Constant(0x29AC0223) != null,
                $"{preset}: constants only character.shpk reads go back to their defaults; the others stay");
            Require(converted.Table is { Kind: ColorTableKind.Dawntrail } table
                    && table.Rows.Zip(doc.Table!.Rows).All(pair => pair.First == LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(pair.Second))),
                $"{preset}: every row is converted");
            Require(notes.All(note => !note.Contains("Shine from", StringComparison.Ordinal) && !note.Contains("Make that texture", StringComparison.Ordinal)),
                $"{preset}: the expected key removal isn't reported");
            var reread = MaterialDocument.From(MtrlReader.Read(converted.Write()));
            Require(reread.Write().SequenceEqual(converted.Write()), $"{preset}: the converted material writes and reads back the same");
            Require(reread.ShaderPackage == "character.shpk" && reread.Table is { Kind: ColorTableKind.Dawntrail } back && back.Rows.All(row => row.Exposure == 1f)
                    && back.Rows.Zip(converted.Table!.Rows).All(pair => MathF.Abs(pair.First.Roughness - pair.Second.Roughness) < 1e-3f),
                $"{preset}: read back, it is a character.shpk material with the converted rows (as halves)");
        }

        var legacyInfo = Preset(LegacyMaskPreset).Clone();
        legacyInfo.AdditionalData = [0x04, 0, 0, 0];
        legacyInfo.DataSet = new byte[512];
        new LegacyColorRow { Diffuse = new Vector3(0.5f), SpecularMask = 1f, Specular = Vector3.One, Gloss = 15f, TileUU = 16f, TileVV = 16f }.Write(legacyInfo.DataSet.AsSpan(32, 32));
        const string madeIndex = "chara/equipment/e0399/texture/v01_c0201e0399_top_id_ie.tex";
        var (sixteen, _) = LegacyConversion.ToCharacter(MaterialDocument.From(legacyInfo), newMask, madeIndex);
        Require(sixteen.Table is { Kind: ColorTableKind.Dawntrail } t16 && MathF.Abs(t16.Rows[2].Roughness - 0.5f) < 1e-6f && t16.Rows[1].Roughness == t16.Rows[2].Roughness
                && sixteen.Slots.Single(slot => slot.SamplerId == LegacyConversion.IndexSampler).Path == madeIndex,
            "a 16-row table on characterlegacy.shpk fills pairs (blending into the next row for a made index) and points at the made index");

        var opaque = Preset(LegacyMaskPreset).Clone();
        opaque.DataSet = opaque.DataSet[..2112];
        var refused = false;
        try { LegacyConversion.ToCharacter(MaterialDocument.From(opaque), newMask); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "a table the editor can't lay out isn't converted");
        Require(LegacyConversion.IsLegacyShader("characterlegacy.shpk") && LegacyConversion.IsLegacyShader("shader/sm5/shpk/characterlegacy.shpk")
                && !LegacyConversion.IsLegacyShader("character.shpk") && !LegacyConversion.IsLegacyShader("xcharacterlegacy.shpk"),
            "the legacy shader is found by file name");
    }

    private static void CheckLegacySession()
    {
        const string newMask = "chara/equipment/e0399/texture/v01_c0201e0399_top_m_ie.tex";
        var kinds = new List<EditClass>();
        var session = MaterialSession.CreateForRegression(Source(ResourceSourceState.LoadedMod), PresetDocument(LegacyMaskPreset), (_, kind) =>
        {
            kinds.Add(kind);
            return Task.CompletedTask;
        });
        var original = session.Current;
        session.AddNewFile(newMask, [1, 2, 3]);
        session.Apply(doc => LegacyConversion.ToCharacter(doc, newMask).Doc, "Convert to Dawntrail");
        Require(session.History.CanUndo && session.History.UndoLabel == "Convert to Dawntrail" && kinds.SequenceEqual([EditClass.Structural])
                && session.NewFilesFor(session.Current).Single().GamePath == newMask,
            "the conversion is one structural undo step, and the new mask goes with it");
        session.Undo();
        Require(ReferenceEquals(session.Current, original) && session.NewFilesFor(session.Current).Count == 0,
            "undoing it gives the legacy material back, which doesn't read the new mask");
    }

    private static void CheckLegacyPaste()
    {
        var legacy = LegacyShaderRow(20f, 0.5f, new Vector3(0.4f));
        var onCharacter = LegacyConversion.RowForShader(legacy, fromLegacy: true, toLegacy: false);
        Require(onCharacter == LegacyConversion.ToCharacter(LegacyLook.FromLegacyShader(legacy)) && onCharacter.Exposure == 1f,
            "a row copied on characterlegacy.shpk pastes onto character.shpk converted, so its exposure isn't 0");
        var back = LegacyConversion.RowForShader(onCharacter, fromLegacy: false, toLegacy: true);
        Require(MathF.Abs(back.Scalar3 - 20f) < 0.05f && MathF.Abs(Vector3.Dot(back.Specular, ShaderLuma) * back.Scalar7 - 0.5f * Vector3.Dot(legacy.Specular, ShaderLuma)) < 1e-3f,
            "pasted back onto characterlegacy.shpk, gloss and the shine come out as they were");
        Require(LegacyConversion.RowForShader(legacy, true, true) == legacy && LegacyConversion.RowForShader(onCharacter, false, false) == onCharacter,
            "rows paste unchanged between materials of the same kind");
    }

    /// <summary>Every characterlegacy.shpk material in IE_MTRL_LIBRARY converts and writes back; skipped when unset.</summary>
    private static void CheckLegacyLibrary()
    {
        var root = Environment.GetEnvironmentVariable("IE_MTRL_LIBRARY");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            Console.WriteLine("[SKIP] legacy conversion library run: set IE_MTRL_LIBRARY to a mod folder to run it");
            return;
        }
        var converted = 0;
        var opaque = 0;
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.mtrl", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            if (!MtrlReader.LooksLikeMtrl(bytes))
                continue;
            MaterialDocument doc;
            try { doc = MaterialDocument.From(MtrlReader.Read(bytes)); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { continue; }
            if (!doc.Editable || !LegacyConversion.IsLegacyShader(doc.ShaderPackage))
                continue;
            if (doc.Table is { Kind: ColorTableKind.Opaque })
            {
                opaque++;
                continue;
            }
            try
            {
                var (result, _) = LegacyConversion.ToCharacter(doc, "chara/x/mask_ie.tex");
                var written = result.Write();
                var reread = MaterialDocument.From(MtrlReader.Read(written));
                if (!reread.Write().SequenceEqual(written) || reread.ShaderPackage != "character.shpk" || reread.Table?.Rows.Any(row => row.Exposure != 1f) != false)
                    failures.Add(file);
                converted++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                failures.Add($"{file} ({e.GetType().Name}: {e.Message})");
            }
        }
        Console.WriteLine($"legacy conversion library run: {converted} converted, {opaque} with unknown tables, {failures.Count} failures");
        foreach (var file in failures.Take(20))
            Console.WriteLine("  failure: " + file);
        Require(converted > 0 && failures.Count == 0, $"all {converted} characterlegacy.shpk library materials convert and write back");
    }
}
