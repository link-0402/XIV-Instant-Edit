using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The skin tone match on synthetic parts: a Viera face tube and an ears tube that overlaps its top,
/// each with its own face-type skin material and single-colour textures that differ in colour, skin
/// tone influence, shine and settings. Covers capturing face skin on a model other than the face, the
/// contact and the default pair, the findings, the fix's new textures and material, matching again
/// over the earlier copies, the preview plan, adding the new files to a mod beside the material, and
/// the dialog's texts.
/// </summary>
internal static class SkinToneScenarios
{
    private const string FaceModelPath = "chara/human/c1801/obj/face/f0001/model/c1801f0001_fac.mdl";
    private const string FaceMaterialPath = "chara/human/c1801/obj/face/f0001/material/mt_c1801f0001_fac_a.mtrl";
    private const string EarModelPath = "chara/human/c1801/obj/zear/z0001/model/c1801z0001_zer.mdl";
    private const string EarMaterialPath = "chara/human/c1801/obj/zear/z0001/material/v0001/mt_c1801z0001_a.mtrl";
    private static readonly string[] FaceTextures =
    [
        "chara/human/c1801/obj/face/f0001/texture/c1801f0001_fac_base.tex", "chara/human/c1801/obj/face/f0001/texture/c1801f0001_fac_norm.tex",
        "chara/human/c1801/obj/face/f0001/texture/c1801f0001_fac_mask.tex",
    ];
    private static readonly string[] EarTextures =
    [
        "chara/human/c1801/obj/zear/z0001/texture/c1801z0001_ear_base.tex", "chara/common/texture/null_normal.tex",
        "chara/human/c1801/obj/zear/z0001/texture/c1801z0001_ear_mask.tex",
    ];

    public static void Run(string testRoot)
    {
        CheckPaths();
        var captured = Capture(testRoot, EarTextures);
        var report = CheckAnalysis(captured);
        var fix = CheckFix(report);
        CheckAgain(testRoot);
        CheckPlan(captured, report, fix);
        CheckModFiles(testRoot);
        CheckViews(report);
    }

    private static void CheckPaths()
    {
        Require(SkinToneFixer.TonePath(EarMaterialPath, "base") == "chara/human/c1801/obj/zear/z0001/texture/c1801z0001_a_v0001_tone_base.tex" &&
                SkinToneFixer.TonePath("chara/human/c0801/obj/face/f0002/material/mt_c0801f0002_fac_e.mtrl", "norm") ==
                "chara/human/c0801/obj/face/f0002/texture/c0801f0002_fac_e_tone_norm.tex" &&
                SkinToneFixer.TonePath("chara/x/mt_y.mtrl", "mask") == "chara/x/y_tone_mask.tex" &&
                SkinToneFixer.TonePath("chara/equipment/e6001/material/v0002/mt_c0201e6001_top_b.mtrl", "base") ==
                "chara/equipment/e6001/texture/c0201e6001_top_b_v0002_tone_base.tex",
            "skin tone: a matched texture goes in the texture folder beside the material's, named after the material and its role");
        Require(SkinToneAnalyzer.ShortName(EarMaterialPath) == "a" && SkinToneAnalyzer.ShortName("chara/x/mt_c0801f0002_fac_e.mtrl") == "fac_e" &&
                SkinToneAnalyzer.PartName(EarModelPath) == "Ears" && SkinToneAnalyzer.PartName(FaceModelPath) == "Face" &&
                SkinToneAnalyzer.PartName("chara/equipment/e0000/model/c0201e0000_glv.mdl") == "Gloves" &&
                SkinToneAnalyzer.PartName("chara/human/c1401/obj/tail/t0001/model/c1401t0001_til.mdl") == "Tail",
            "skin tone: materials and models get short names for the pickers");
    }

    private static byte[] FaceMaterial() => NeckSeamScenarios.Material(FaceTextures, [],
        [(SkinMaterial.TileScale, [40, 40]), (SkinMaterial.TileAlpha, [0.5f]), (SkinMaterial.TileIndex, [60])]);

    private static byte[] EarMaterial(string[] textures) => NeckSeamScenarios.Material(textures, [],
        [(SkinMaterial.DiffuseColor, [1.4f, 1.4f, 1.4f]), (SkinMaterial.TileScale, [40, 40]), (SkinMaterial.TileAlpha, [1f]), (SkinMaterial.TileIndex, [63])]);

    /// <summary>
    /// The face (rings 1.40 to 1.45 m) and the ears (rings 1.44 to 1.46 m, the first three on the face's
    /// surface). The ears read <paramref name="earTextures"/>: the face-mod files, or earlier matched copies.
    /// </summary>
    private static NeckSeamCaptured Capture(string testRoot, string[] earTextures)
    {
        var folder = Path.Combine(testRoot, "SkinTone");
        Directory.CreateDirectory(folder);
        string F(string name) => Path.Combine(folder, name);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [F("face.mdl")] = NeckSeamScenarios.Tube("/mt_c1801f0001_fac_a.mtrl", [1.40f, 1.425f, 1.45f], 0.2f, 0.8f),
            [F("face.mtrl")] = FaceMaterial(),
            [F("face_base.tex")] = NeckSeamScenarios.Texture(170, 140, 120, 255),
            [F("face_norm.tex")] = NeckSeamScenarios.Texture(128, 128, 255, 0),
            [F("face_mask.tex")] = NeckSeamScenarios.Texture(160, 130, 150, 255),
            // The same UV density per metre as the face, so only the tile settings differ, not its size.
            [F("ears.mdl")] = NeckSeamScenarios.Tube("/mt_c1801z0001_a.mtrl", [1.44f, 1.445f, 1.45f, 1.46f], 0.2f, 0.44f),
            [F("ears.mtrl")] = EarMaterial(earTextures),
            [F("ear_base.tex")] = NeckSeamScenarios.Texture(150, 120, 110, 255),
            [F("ear_norm.tex")] = NeckSeamScenarios.Texture(128, 128, 0, 255),
            [F("ear_mask.tex")] = NeckSeamScenarios.Texture(140, 110, 150, 255),
        };
        ResourceNode[] roots =
        [
            NeckSeamScenarios.Node(FaceModelPath, F("face.mdl"), "Face Mod",
            [
                NeckSeamScenarios.Node(FaceMaterialPath, F("face.mtrl"), "Face Mod",
                [
                    NeckSeamScenarios.Node(FaceTextures[0], F("face_base.tex"), "Face Mod"), NeckSeamScenarios.Node(FaceTextures[1], F("face_norm.tex"), "Face Mod"),
                    NeckSeamScenarios.Node(FaceTextures[2], F("face_mask.tex"), "Face Mod"),
                ]),
            ]),
            NeckSeamScenarios.Node(EarModelPath, F("ears.mdl"), "Ears Mod",
            [
                NeckSeamScenarios.Node(EarMaterialPath, F("ears.mtrl"), "Ears Mod",
                [
                    NeckSeamScenarios.Node(earTextures[0], F("ear_base.tex"), "Ears Mod"), NeckSeamScenarios.Node(earTextures[1], F("ear_norm.tex"), "Ears Mod"),
                    NeckSeamScenarios.Node(earTextures[2], F("ear_mask.tex"), "Ears Mod"),
                ]),
            ]),
        ];
        return NeckSeamCapture.Capture(roots, path => files.GetValueOrDefault(path), null);
    }

    private static SkinToneReport CheckAnalysis(NeckSeamCaptured captured)
    {
        Require(captured.Input.Bodies.Single().GamePath == EarModelPath && captured.Source(EarMaterialPath)?.ModName == "Ears Mod" &&
                captured.Source(EarTextures[1])?.ModName == "Ears Mod" && captured.Input.Clothing.Count == 0,
            "skin tone: capture keeps a model whose only skin is face skin (Viera ears) with its skin material and textures");

        var report = SkinToneAnalyzer.Analyze(captured);
        int face = report.IndexOf(FaceMaterialPath)!.Value, ears = report.IndexOf(EarMaterialPath)!.Value;
        Require(report.Parts.Count == 2 && report.Parts[face].OnFace && !report.Parts[ears].OnFace && report.Parts[ears].Parts == "Ears" &&
                report.Parts[ears].Mod == "Ears Mod" && report.Parts[face].Label == "Face · mt_c1801f0001_fac_a.mtrl",
            "skin tone: every drawn skin material is a part, named after what draws it and the mod it comes from");
        Require(report.Contacts.TryGetValue((ears, face), out var contact) && contact.Points == 30 && MathF.Abs(contact.Share - 0.75f) < 1e-3f &&
                !report.Contacts.ContainsKey((face, ears)),
            "skin tone: the ears' three rings on the face's skin touch it; the face's one ring on the ears is too few to count");
        Require(report.DefaultTarget == ears && report.DefaultBase == face,
            "skin tone: the dialog starts with the extra skin that touches the face matched to the face");

        var comparison = report.Compare(ears, face);
        NeckSeamFinding Finding(string title) => comparison.Findings.Single(f => f.Title == title);
        Require(comparison.Touching && comparison.Points == 30 && ReferenceEquals(report.Compare(ears, face), comparison),
            "skin tone: a pair is compared where it touches, once");
        Require(Finding("Skin colour") is { Severity: NeckSeamSeverity.Problem, Face: "150 120 110", Body: "170 140 120" } &&
                Finding("Colour multiplier").Severity == NeckSeamSeverity.Problem &&
                Finding("Skin tone influence") is { Severity: NeckSeamSeverity.Problem } influence && influence.Face == $"{0f:0.00}" && influence.Body == $"{1f:0.00}" &&
                Finding("Specular strength").Severity == NeckSeamSeverity.Warning && Finding("Roughness").Severity == NeckSeamSeverity.Warning &&
                Finding("Subsurface scattering").Severity == NeckSeamSeverity.Ok &&
                Finding("Skin detail tile strength").Severity == NeckSeamSeverity.Warning && Finding("Skin detail tile pattern") is { Face: "63", Body: "60" },
            "skin tone: colour, multiplier, skin tone influence, shine and the pore tile are compared where the parts meet");
        Require(comparison.Gain(settingsMatched: true) is { } gain && MathF.Abs(gain.X - 170f / 150) < 1e-3f && MathF.Abs(gain.Y - 140f / 120) < 1e-3f &&
                comparison.Gain(settingsMatched: false) is { } withMultiplier && MathF.Abs(withMultiplier.X - 170f / 150 / 1.4f) < 1e-3f,
            "skin tone: the diffuse gain takes the target's colour to the base's, and makes up for the multiplier unless the settings are matched too");
        var neckBase = SkinMaterial.Read(NeckSeamScenarios.Material(FaceTextures, [], [(SkinMaterial.DiffuseColor, [1.2f, 1.2f, 1.2f])]));
        Require(comparison.Gain(settingsMatched: false, neckBase) is { } afterNeck && MathF.Abs(afterNeck.X - 170f / 150 * 1.2f / 1.4f) < 1e-3f,
            "skin tone: the gain makes up for the base's multiplier as another fix left it, not as the file had it");
        var settings = comparison.Settings(influenceMatched: true);
        var noTile = comparison.Settings(influenceMatched: false);
        Require(settings.TileAlphaOff && settings.TileIndexOff && !noTile.TileAlphaOff && !noTile.TileIndexOff && noTile.Other.ContainsKey(SkinMaterial.DiffuseColor),
            "skin tone: the pore tile only counts where it shows on both sides, which needs the ears to take the face's skin tone influence");
        Require(comparison.MaskOffset is var offset && MathF.Abs(offset.X - 20f / 255) < 1e-3f && MathF.Abs(offset.Y - 20f / 255) < 1e-3f && offset.Z == 0,
            "skin tone: the shine moves only the mask channels that differ");
        return report;
    }

    private static SkinToneFix CheckFix(SkinToneReport report)
    {
        var fix = SkinToneFixer.Build(report, new SkinToneFixOptions(EarMaterialPath, FaceMaterialPath, true, true, true, true));
        var copies = fix.NewTextures.ToDictionary(t => t.Sampler);
        Require(fix.Textures.Count == 0 && copies.Count == 3 && copies[SkinMaterial.DiffuseSampler].GamePath == SkinToneFixer.TonePath(EarMaterialPath, "base") &&
                copies[SkinMaterial.NormalSampler].GamePath == SkinToneFixer.TonePath(EarMaterialPath, "norm") &&
                copies.Values.All(t => t.Material == EarMaterialPath),
            "skin tone: every changed texture becomes a new file for the ears alone, so the shared null_normal.tex stays as it is");
        Vector4 Texel(uint sampler) => copies[sampler].Image.Texel(5, 5) * 255;
        Require(Vector4.Distance(Texel(SkinMaterial.DiffuseSampler), new Vector4(170, 140, 120, 255)) < 1.5f &&
                Texel(SkinMaterial.NormalSampler) is { X: 128, Y: 128, Z: 255, W: 255 } &&
                Vector4.Distance(Texel(SkinMaterial.MaskSampler), new Vector4(160, 130, 150, 255)) < 1.5f,
            "skin tone: the copies carry the face's colour, skin tone influence and shine");
        var material = SkinMaterial.Read(fix.Material!.Bytes);
        Require(material.TextureFor(SkinMaterial.DiffuseSampler) == SkinToneFixer.TonePath(EarMaterialPath, "base") &&
                material.TextureFor(SkinMaterial.NormalSampler) == SkinToneFixer.TonePath(EarMaterialPath, "norm") &&
                material.Constant(SkinMaterial.TileAlpha).SequenceEqual([0.5f]) && material.Constant(SkinMaterial.TileIndex).SequenceEqual([60f]) &&
                material.Constant(SkinMaterial.DiffuseColor).SequenceEqual([1f, 1f, 1f]) && material.IsFaceSkin,
            "skin tone: the ears' material points at the copies and takes the face's multiplier and pore tile, keeping its skin type");
        Require(fix.Expected.Any(l => l.StartsWith("Skin colour: 150 120 110 → 170 140 120", StringComparison.Ordinal)) &&
                fix.Expected.Any(l => l.StartsWith($"Skin tone influence: {0f:0.00} → {1f:0.00}", StringComparison.Ordinal)) &&
                fix.Changes.All(c => c.StartsWith("Skin tone: ", StringComparison.Ordinal)),
            "skin tone: the fix lists what it changes and the values measured again");

        var colourOnly = SkinToneFixer.Build(report, new SkinToneFixOptions(EarMaterialPath, FaceMaterialPath, true, false, false, false));
        var onlyCopy = colourOnly.NewTextures.Single();
        Require(onlyCopy.Sampler == SkinMaterial.DiffuseSampler && Vector4.Distance(onlyCopy.Image.Texel(5, 5) * 255, new Vector4(121, 100, 86, 255)) < 1.5f &&
                SkinMaterial.Read(colourOnly.Material!.Bytes).Constant(SkinMaterial.DiffuseColor).SequenceEqual([1.4f, 1.4f, 1.4f]),
            "skin tone: matching the colour alone makes up for the ears' stronger multiplier in the texture and leaves the material's settings");
        var threw = false;
        try { SkinToneFixer.Build(report, new SkinToneFixOptions(EarMaterialPath, EarMaterialPath, true, true, true, true)); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "skin tone: a material can't be matched to itself");
        return fix;
    }

    /// <summary> Matching again, with the ears already reading the copies: the copies are written over, not copied again. </summary>
    private static void CheckAgain(string testRoot)
    {
        string[] copies = [SkinToneFixer.TonePath(EarMaterialPath, "base"), SkinToneFixer.TonePath(EarMaterialPath, "norm"), SkinToneFixer.TonePath(EarMaterialPath, "mask")];
        var report = SkinToneAnalyzer.Analyze(Capture(Path.Combine(testRoot, "Again"), copies));
        var fix = SkinToneFixer.Build(report, new SkinToneFixOptions(EarMaterialPath, FaceMaterialPath, true, true, true, false));
        Require(fix.NewTextures.Count == 0 && fix.Textures.Count == 3 && fix.Textures.All(t => copies.Contains(t.GamePath) && t.Materials.SequenceEqual([EarMaterialPath])) &&
                fix.Material is null,
            "skin tone: matching again writes over the ears' own copies and leaves the material as it is");
    }

    private static void CheckPlan(NeckSeamCaptured captured, SkinToneReport report, SkinToneFix fix)
    {
        var analysis = new NeckSeamAnalysis(null, captured, "A", 0, 0) { Tone = report };
        var plan = SkinSeamPreviewPlan.Build(analysis, new SkinSeamFix(null, null, fix));
        Require(plan.Materials.Single().GamePath == EarMaterialPath && plan.Textures.Count == 0 && plan.NewTextures.Count == 3 &&
                new SkinSeamFix(null, null, fix).Changes.Count == fix.Changes.Count && !new SkinSeamFix(null, null, fix).Empty,
            "skin tone: the preview holds the ears' material and its new textures at the paths the material names");
        var threw = false;
        try
        {
            NeckSeamService.BuildFixAsync(analysis, null, new Dictionary<BodySeamKind, BodySeamFixOptions>(),
                new SkinToneFixOptions(EarMaterialPath, FaceMaterialPath, true, false, false, false), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception) { threw = true; }
        Require(!threw, "skin tone: the tone match builds on its own, without a neck or body seam fix");
    }

    /// <summary> Adding the new textures to a mod beside the material: an option folder layout, the entries that map it, a conflict, and applying again. </summary>
    private static void CheckModFiles(string testRoot)
    {
        var mod = Path.Combine(testRoot, "SkinToneMod");
        if (Directory.Exists(mod))
            Directory.Delete(mod, true);
        var material = Path.Combine(mod, "ears a", EarMaterialPath.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(material)!);
        File.WriteAllBytes(material, EarMaterial(EarTextures));
        JsonObject Option(string name, string folder) => new()
        {
            ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = name,
            ["Files"] = new JsonObject { [EarMaterialPath] = folder + "\\" + EarMaterialPath.Replace('/', '\\') },
        };
        var meta = PenumbraService.CreateV4ModMetadata("Ears Mod", "A", "", "", new JsonObject { ["Files"] = new JsonObject { ["chara/other.tex"] = "chara\\other.tex" } });
        meta["Groups"] = new JsonArray
        {
            new JsonObject
            {
                ["Type"] = "Single", ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = "Ears",
                ["Options"] = new JsonArray { Option("A", "ears a"), Option("B", "ears b") },
            },
        };
        File.WriteAllText(Path.Combine(mod, "meta.json"), meta.ToJsonString());
        var copy = SkinToneFixer.TonePath(EarMaterialPath, "base");
        var plan = PenumbraService.PlanFileAdditions(mod, "Ears Mod", material, EarMaterialPath, [(copy, [1, 2, 3])], "Measure again");
        Require(plan.Entries.Count == 1 && plan.Files.Single().RelativePath == "ears a/" + copy,
            "skin tone: a new texture goes in the material's option folder, for the one option that maps the material there");
        PenumbraService.CommitFileAdditions(plan, null);
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        var options = written["Groups"]![0]!["Options"]!.AsArray();
        Require(File.ReadAllBytes(Path.Combine(mod, "ears a", copy.Replace('/', '\\'))).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                (string?)options[0]!["Files"]![copy] == "ears a\\" + copy.Replace('/', '\\') && options[1]!["Files"]![copy] is null &&
                written["DefaultData"]!["Files"]![copy] is null && (int)written["FileVersion"]! == 4,
            "skin tone: the new texture is written and mapped where the material is, and nowhere else");

        var again = PenumbraService.PlanFileAdditions(mod, "Ears Mod", material, EarMaterialPath, [(copy, [4, 5])], "Measure again");
        PenumbraService.CommitFileAdditions(again, null);
        Require(File.ReadAllBytes(Path.Combine(mod, "ears a", copy.Replace('/', '\\'))).SequenceEqual(new byte[] { 4, 5 }),
            "skin tone: applying again replaces the mod's own copy");

        options[0]!["Files"]![copy] = "somewhere\\else.tex";
        File.WriteAllText(Path.Combine(mod, "meta.json"), written.ToJsonString());
        var conflict = false;
        try { PenumbraService.PlanFileAdditions(mod, "Ears Mod", material, EarMaterialPath, [(copy, [1])], "Measure again"); }
        catch (TextureConflictException) { conflict = true; }
        Require(conflict, "skin tone: a game path the material's option maps to another file is never taken over");

        var files = Path.Combine(testRoot, "SkinToneFilesMod");
        if (Directory.Exists(files))
            Directory.Delete(files, true);
        var filesMaterial = Path.Combine(files, "Files", EarMaterialPath.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(filesMaterial)!);
        File.WriteAllBytes(filesMaterial, [1]);
        File.WriteAllText(Path.Combine(files, "meta.json"), PenumbraService.CreateV4ModMetadata("Files Mod", "A", "", "",
            new JsonObject { ["Files"] = new JsonObject { [EarMaterialPath] = "Files\\" + EarMaterialPath.Replace('/', '\\') } }).ToJsonString());
        Require(PenumbraService.PlanFileAdditions(files, "Files Mod", filesMaterial, EarMaterialPath, [(copy, [1])], "Measure again").Files.Single().RelativePath == "Files/" + copy,
            "skin tone: a mod that keeps its files under Files gets the new texture there too");
    }

    private static void CheckViews(SkinToneReport report)
    {
        var comparison = report.Compare(report.IndexOf(EarMaterialPath)!.Value, report.IndexOf(FaceMaterialPath)!.Value);
        Require(NeckSeamViews.ToneSummary(comparison).StartsWith("3 differences show, 4 smaller differences between the a skin and the fac_a skin", StringComparison.Ordinal) &&
                NeckSeamViews.ToneWhere(comparison).Contains("30 points", StringComparison.Ordinal) &&
                NeckSeamViews.PartTooltip(report.Parts[0]).Contains("drawn by c1801f0001_fac.mdl", StringComparison.Ordinal),
            "skin tone: the tab sums up the comparison and where it was read");
        var plan = NeckSeamViews.TonePlan(comparison, true, true, true, true);
        var shine = 20f / 255;
        Require(plan.Any(l => l.StartsWith($"Colour: the a skin's diffuse × {170f / 150:0.00} red", StringComparison.Ordinal)) &&
                plan.Any(l => l.StartsWith($"Skin tone influence: {0f:0.00} → {1f:0.00}", StringComparison.Ordinal)) &&
                plan.Any(l => l == $"Shine: specular strength {shine:+0.00;-0.00}, roughness {shine:+0.00;-0.00}") &&
                plan.Any(l => l.StartsWith("Settings: Pore tile strength", StringComparison.Ordinal)) &&
                NeckSeamViews.TonePlan(comparison, true, false, false, false).Any(l => l.Contains("one skin colour only", StringComparison.Ordinal)),
            "skin tone: the fix section lists what each tick changes, and warns when the colour is matched without the skin tone influence");

        var source = new NeckSeamSource
        {
            GamePath = EarMaterialPath, ActualPath = @"C:\Mods\Ears\ears.mtrl", State = ResourceSourceState.LoadedMod, ModName = "Ears Mod",
            ModDirectory = "Ears Mod", RelativePath = "ears a/mt_c1801z0001_a.mtrl", Sha256 = "AB",
        };
        var copy = SkinToneFixer.TonePath(EarMaterialPath, "base");
        var preview = new NeckSeamPreview
        {
            Id = Guid.NewGuid(), ModDirectory = "Skin Seam Preview - A", ModIdentifier = Guid.NewGuid(), CollectionId = Guid.NewGuid(), CollectionName = "Default",
            ActorName = "A", ObjectIndex = 0, Created = DateTimeOffset.UtcNow,
            Files =
            [
                new NeckSeamPreviewFile
                {
                    Kind = "skin diffuse texture", GamePath = copy, PreviewGamePath = copy, PreviewRelativePath = "Files/" + copy, PreviewSha256 = "00",
                    Source = source, NewFile = true,
                },
                new NeckSeamPreviewFile
                {
                    Kind = "skin diffuse texture", GamePath = copy, PreviewGamePath = copy, PreviewRelativePath = "Files/" + copy, PreviewSha256 = "00",
                    Source = source with { State = ResourceSourceState.GameData, ModName = "", ModDirectory = "", RelativePath = "" }, NewFile = true,
                },
            ],
        };
        var lines = NeckSeamViews.ApplyLines(preview);
        Require(lines[0] == "Add c1801z0001_a_v0001_tone_base.tex to Ears Mod (skin diffuse texture), mapped wherever mt_c1801z0001_a.mtrl is" &&
                lines[1].StartsWith("Put the new skin diffuse texture chara/human/c1801/obj/zear/z0001/texture/c1801z0001_a_v0001_tone_base.tex in a new mod", StringComparison.Ordinal),
            "skin tone: applying lists each new texture with the mod it joins");
        Require(new PreviewApplyResult(["Ears Mod"], 1, null, 0, [], NewFiles: 3).Describe("Skin seam fix applied") ==
                "Skin seam fix applied: wrote 1 file and added 3 new files in Ears Mod (backups kept for 7 days)." &&
                new PreviewApplyResult(["Ears Mod"], 0, null, 0, [], NewFiles: 1).Describe("Applied") == "Applied: added 1 new file to Ears Mod.",
            "skin tone: the apply message counts the new files");
    }
}
