using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Stash;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Another game's maps made into what the game reads: a gear normal's blue as opacity (solid, or an
/// opacity map), a Dawntrail mask from roughness, metallic and occlusion maps, and the Build fit finding
/// a Substance set's maps beside its textures.
/// </summary>
internal static class GameTextureScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "GameTextures");
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache");

        BlueIsReadAsOpacity(root);
        NormalsGetTheirOpacity(root, cache);
        MasksAreMadeFromMaps(root, cache);
        TheFitFindsTheSetsMaps(root);
    }

    /// <summary>A bumpy normal, its blue the surface's Z (as another game saves it), or solid with holes (as the game's are).</summary>
    private static Image<Rgba32> Bumpy(int size, Func<int, int, byte, byte>? blue = null)
    {
        var image = new Image<Rgba32>(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float nx = 0.6f * MathF.Sin(x * 0.4f), ny = 0.5f * MathF.Cos(y * 0.3f);
                float nz = MathF.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
                byte z = (byte)Math.Round(nz * 127.5f + 127.5f);
                image[x, y] = new Rgba32((byte)Math.Round(nx * 127.5f + 127.5f), (byte)Math.Round(ny * 127.5f + 127.5f), blue?.Invoke(x, y, z) ?? z, 255);
            }
        return image;
    }

    private static void BlueIsReadAsOpacity(string root)
    {
        using (var pbr = Bumpy(128))
            Require(!GameNormal.CarriesOpacity(pbr), "game normal: a blue that is the surface's Z holds no opacity");
        using (var game = Bumpy(128, (x, y, _) => x < 16 ? (byte)0 : (byte)255))
            Require(GameNormal.CarriesOpacity(game), "game normal: a solid blue with see-through parts is an opacity");
        using (var opaque = Bumpy(128, (_, _, _) => 255))
            Require(GameNormal.CarriesOpacity(opaque), "game normal: a solid blue over slopes is one too (writing it solid again changes nothing)");
        using (var empty = Bumpy(128, (_, _, _) => 0))
            Require(!GameNormal.CarriesOpacity(empty), "game normal: an empty blue (a two-channel normal) is no opacity");
        using (var flatHoles = new Image<Rgba32>(128, 128, new Rgba32(128, 128, 255, 255)))
        {
            for (int x = 0; x < 4; x++)
                flatHoles[x, 5] = new Rgba32(128, 128, 0, 255);
            Require(GameNormal.CarriesOpacity(flatHoles), "game normal: on a flat normal, a few holes are enough to tell");
        }

        // The real thing, when it is on this machine: a Substance Painter export.
        var set = Environment.GetEnvironmentVariable("IE_SUBSTANCE_SET");
        var substance = string.IsNullOrEmpty(set) ? "" : Path.Combine(set, "ASA_Normal_OpenGL.png");
        if (File.Exists(substance))
            Require(!GameNormal.CarriesOpacity(substance), "game normal: Substance Painter's normal holds no opacity");
    }

    private static void NormalsGetTheirOpacity(string root, string cache)
    {
        var pbrPath = Path.Combine(root, "pbr_normal.png");
        using (var pbr = Bumpy(64))
            pbr.SaveAsPng(pbrPath);
        var gamePath = Path.Combine(root, "game_normal.png");
        using (var game = Bumpy(64, (x, _, _) => x < 8 ? (byte)0 : (byte)255))
            game.SaveAsPng(gamePath);

        var tex = new TextureSlot { Type = TextureType.Normal, SourcePath = pbrPath, GreenDirection = NormalGreenMode.OpenGl };
        Require(AlphaNormal.SourceOf(tex, cache, out _, out _, ShaderType.Hair) == pbrPath,
            "game normal: a hair normal keeps its blue");
        var solid = AlphaNormal.SourceOf(tex, cache, out var error, out var note, ShaderType.Character);
        Require(solid != null && solid != pbrPath && note == "blue made solid", $"game normal: a gear normal from another game is made solid ({error}, {note})");
        using (var result = Image.Load<Rgba32>(solid!))
        using (var original = Image.Load<Rgba32>(pbrPath))
            Require(result[10, 10].B == 255 && result[10, 10].R == original[10, 10].R && result[10, 10].G == original[10, 10].G,
                "game normal: only its blue changes");

        tex.SourcePath = gamePath;
        Require(AlphaNormal.SourceOf(tex, cache, out _, out note, ShaderType.CharacterLegacy) == gamePath && note.Length == 0,
            "game normal: one made for the game is kept as it is");

        // An opacity map, half see-through: into the blue as it is, or times the cutoff for a Translucent material.
        var opacityPath = Path.Combine(root, "opacity.png");
        using (var opacity = new Image<L8>(32, 32, new L8(255)))
        {
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 16; x++)
                    opacity[x, y] = new L8(100);
            opacity.SaveAsPng(opacityPath);
        }
        tex.SourcePath = pbrPath;
        tex.UseAlphaOverlay = true;
        tex.AlphaSourcePath = opacityPath;
        var cut = AlphaNormal.SourceOf(tex, cache, out error, out _, ShaderType.Character);
        var blended = AlphaNormal.SourceOf(tex, cache, out _, out _, ShaderType.Character, 0.5f);
        Require(cut != null && blended != null && cut != blended, $"game normal: the opacity map makes a normal per scale ({error})");
        using (var result = Image.Load<Rgba32>(cut!))
            Require(Math.Abs(result[4, 10].B - 100) <= 2 && result[60, 10].B == 255 && result.Width == 64,
                $"game normal: the opacity goes into the blue, at the larger size ({result[4, 10].B}, {result[60, 10].B})");
        using (var result = Image.Load<Rgba32>(blended!))
            Require(Math.Abs(result[4, 10].B - 50) <= 2 && Math.Abs(result[60, 10].B - 128) <= 1,
                $"game normal: a Translucent material's opacity is written times its cutoff ({result[4, 10].B}, {result[60, 10].B})");
        Require(AlphaNormal.SourceOf(tex, cache, out _, out _, ShaderType.Hair) is { } hair && hair != cut,
            "game normal: on hair the map still goes into the alpha");

        Require(GameNormal.MostlySheer(opacityPath) == false, "game normal: half solid is not mostly see-through");
        var sheerPath = Path.Combine(root, "sheer.png");
        using (var sheer = new Image<L8>(16, 16, new L8(70)))
            sheer.SaveAsPng(sheerPath);
        Require(GameNormal.MostlySheer(sheerPath), "game normal: a map around 27% is mostly see-through");
    }

    private static void MasksAreMadeFromMaps(string root, string cache)
    {
        string Map(string name, int size, Func<int, int, Rgba32> pixel)
        {
            var path = Path.Combine(root, name);
            using var image = new Image<Rgba32>(size, size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    image[x, y] = pixel(x, y);
            image.SaveAsPng(path);
            return path;
        }
        Rgba32 Gray(int v) => new((byte)v, (byte)v, (byte)v, 255);
        var rough = Map("Set_Roughness.png", 16, (x, _) => Gray(x < 8 ? 40 : 200));
        var gloss = Map("Set_Glossiness.png", 16, (x, _) => Gray(x < 8 ? 40 : 200));
        var metal = Map("Set_Metallic.png", 16, (x, _) => Gray(x < 8 ? 255 : 0));
        var ao    = Map("Set_AO.png", 32, (_, y) => Gray(y < 16 ? 90 : 255));
        var black = Map("Set_Mixed_AO.png", 16, (_, _) => Gray(0));
        var unity = Map("Set_MetallicSmoothness.png", 16, (x, _) => new Rgba32(0, 0, 0, (byte)(x < 8 ? 230 : 20)));

        var tex = new TextureSlot { Type = TextureType.Mask, UseMadeMask = true };
        Require(AlphaNormal.SourceOf(tex, cache, out var error, out _) == null && error.Contains("no maps"), "game mask: no maps, no mask");

        tex.SourcePath = rough;
        tex.OcclusionSourcePath = ao;
        var made = AlphaNormal.SourceOf(tex, cache, out error, out var note);
        Require(made != null && note.Contains("Set_Roughness.png") && note.Contains("Set_AO.png"), $"game mask: made, and the note names its maps ({error}, {note})");
        using (var mask = Image.Load<Rgba32>(made!))
            Require(mask.Width == 32 && mask[2, 2] is { R: 90, G: 40, B: 90, A: 255 } && mask[30, 30] is { R: 255, G: 200, B: 255 },
                $"game mask: roughness in green, occlusion in red and blue, at the larger size ({mask[2, 2]}, {mask[30, 30]})");

        using (var glossy = GameMask.Make(gloss, null, null))
            Require(glossy[2, 2] is { R: 255, G: 215, B: 255 } && glossy[12, 2].G == 55, $"game mask: a gloss map is turned around ({glossy[2, 2]})");
        using (var metallic = GameMask.Make(null, metal, black))
            Require(metallic[2, 2] is { R: 255, G: 0, B: 255 } && metallic[12, 2].G == 255,
                $"game mask: without roughness metal is glossy, and an all-black occlusion counts as none ({metallic[2, 2]})");
        using (var smooth = GameMask.Make(null, unity, null))
            Require(smooth[2, 2].G == 25 && smooth[12, 2].G == 235, $"game mask: a metallic map's smoothness alpha gives the roughness ({smooth[2, 2]})");

        tex.SourcePath = string.Empty;
        tex.MetallicSourcePath = Path.Combine(root, "gone.png");
        Require(AlphaNormal.SourceOf(tex, cache, out error, out _) == null && error.Contains("not found"), "game mask: a missing map fails with why");
    }

    /// <summary>A Substance set (a Painter export's layout): the fit finds its opacity, roughness, metallic and occlusion maps beside its textures.</summary>
    private static void TheFitFindsTheSetsMaps(string root)
    {
        var folder = Path.Combine(root, "PBR");
        Directory.CreateDirectory(folder);
        string File(string name, Rgba32 colour)
        {
            var path = Path.Combine(folder, name);
            using var image = new Image<Rgba32>(8, 8, colour);
            image.SaveAsPng(path);
            return path;
        }
        var diffuse = File("ASA_Base_color.png", new Rgba32(200, 40, 40, 255));
        var normal  = File("ASA_Normal_OpenGL.png", new Rgba32(128, 128, 255, 255));
        var opacity = File("ASA_Opacity.png", new Rgba32(70, 70, 70, 255));
        var rough   = File("ASA_Roughness.png", new Rgba32(90, 90, 90, 255));
        var metal   = File("ASA_Metallic.png", new Rgba32(255, 255, 255, 255));
        var ao      = File("ASA_Mixed_AO.png", new Rgba32(200, 200, 200, 255));
        File("ASA_Height.png", new Rgba32(128, 128, 128, 255));
        File("ASB_Roughness.png", new Rgba32(10, 10, 10, 255));
        var lone = File("Other_Base_color.png", new Rgba32(10, 200, 10, 255));
        var loneMask = File("Other_Mask.png", new Rgba32(255, 0, 0, 255));

        var subject = new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body });
        var female = RaceInfo.BaseFor(PlayerGender.Female);
        IReadOnlyDictionary<TexturePurpose, string> TexturesFor(string name) => name == "/ASA.mtrl"
            ? new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = diffuse, [TexturePurpose.Normal] = normal, [TexturePurpose.Mask] = metal, [TexturePurpose.Occlusion] = ao }
            : new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = lone, [TexturePurpose.Mask] = loneMask };
        var fit = MaterialFit.Fit(new[] { "/ASA.mtrl", "/Other.mtrl" }, TexturesFor, subject, female, otherPreset: null);

        var asa = fit.Materials[0];
        var asaNormal = asa.Textures.Single(t => t.Type == TextureType.Normal);
        var asaMask = asa.Textures.Single(t => t.Type == TextureType.Mask);
        Require(asaNormal is { UseAlphaOverlay: true } && asaNormal.SourcePath == normal && asaNormal.AlphaSourcePath == opacity,
            "game fit: the set's opacity map goes into the normal");
        Require(asa.Translucent, "game fit: a mostly see-through opacity makes the material Translucent");
        Require(asaMask is { UseMadeMask: true, UseWhiteDummy: false } && asaMask.SourcePath == rough && asaMask.MetallicSourcePath == metal
                && asaMask.OcclusionSourcePath == ao,
            $"game fit: the mask is made from the set's own roughness, metallic and occlusion, not another set's ({asaMask.SourcePath})");

        var other = fit.Materials[1];
        Require(other.Textures.Single(t => t.Type == TextureType.Mask) is { UseWhiteDummy: true, UseMadeMask: false, SourcePath: "" } && !other.Translucent,
            "game fit: another game's mask with nothing to make one from is left white");

        var hair = MaterialFit.Fit(new[] { "/ASA.mtrl" }, TexturesFor, new FeatureSubject(SubjectKind.Hair, female, 5), female,
            MaterialPresets.All.First(p => p.Name == "Hair")).Materials.Single();
        Require(hair.Textures.All(t => !t.UseAlphaOverlay && !t.UseMadeMask) && !hair.Translucent, "game fit: a hair is left as it was");

        // A VRChat set: "Body_Alpha" beside "Body" is the diffuse with an alpha of its own, not an opacity map.
        var vrc = Path.Combine(root, "VRC");
        Directory.CreateDirectory(vrc);
        string VrcFile(string name)
        {
            var path = Path.Combine(vrc, name);
            using var image = new Image<Rgba32>(8, 8, new Rgba32(200, 150, 120, 255));
            image.SaveAsPng(path);
            return path;
        }
        var body = VrcFile("Body.png");
        VrcFile("Body_Alpha.png");
        var bodyNormal = VrcFile("Body_Normal.png");
        var vrcMaps = SetMaps.Find(new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = body, [TexturePurpose.Normal] = bodyNormal });
        Require(vrcMaps.Opacity == null, $"game fit: a diffuse's alpha variant is no opacity map ({vrcMaps.Opacity})");
        var opacityBeside = VrcFile("Body_Opacity.png");
        Require(SetMaps.Find(new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = body, [TexturePurpose.Normal] = bodyNormal }).Opacity == opacityBeside,
            "game fit: one named Opacity is, whatever it extends");

        // The real set, when IE_SUBSTANCE_SET points at its PBR folder: a Substance export with ASA, ASB and ASW sets.
        var real = Environment.GetEnvironmentVariable("IE_SUBSTANCE_SET");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
            return;
        SetMaps RealSet(string set) => SetMaps.Find(new Dictionary<TexturePurpose, string>
        {
            [TexturePurpose.Diffuse] = Path.Combine(real, $"{set}_Base_color.png"),
            [TexturePurpose.Normal]  = Path.Combine(real, $"{set}_Normal_OpenGL.png"),
        });
        var asaMaps = RealSet("ASA");
        Require(asaMaps == new SetMaps(Path.Combine(real, "ASA_Opacity.png"), Path.Combine(real, "ASA_Roughness.png"),
                                       Path.Combine(real, "ASA_Metallic.png"), Path.Combine(real, "ASA_Mixed_AO.png")),
            $"game fit: the real ASA set finds its own four maps ({asaMaps})");
        Require(RealSet("ASB").Opacity == null && RealSet("ASB").Roughness == Path.Combine(real, "ASB_Roughness.png"),
            "game fit: ASB has no opacity map, so its normal is only made solid");
        Require(!GameNormal.MostlySheer(asaMaps.Opacity!) && GameNormal.MostlySheer(RealSet("ASW").Opacity!),
            "game fit: ASA's sheer parts stay cut-out, ASW (around 27% opaque) is Translucent");
    }
}
