using System.Numerics;
using System.Text.Json.Nodes;
using Lumina.Data.Files;

namespace InstantEdit.Services.Painter;

/// <summary> A texture of one texture set, as the channel map sees it. </summary>
/// <param name="Key">Export file name for textures sent back to the game; empty for reference-only textures.</param>
/// <param name="Usage">diffuse, normal, mask, specular, index or other (see MaterialPreviewBundleBuilder.TextureUsage).</param>
internal sealed record PainterTextureInput(string Key, string Usage, uint Format, int Width, int Height, string SeedStem)
{
    public bool Exported => Key.Length > 0;
}

internal sealed record PainterTextureSetInput(string Name, string ShaderPackage, IReadOnlyList<PainterTextureInput> Textures);

/// <summary> Where one RGBA component of a texture lives in Painter. </summary>
internal sealed record PainterComponent(int Component, string Channel, string Label);

internal sealed record PainterTextureLayout(PainterTextureInput Texture, IReadOnlyList<PainterComponent> Components, string Problem)
{
    public bool Exported => Texture.Exported && Problem.Length == 0;
}

internal sealed record PainterChannelSpec(string Type, string Format, string Label);

/// <param name="Width">Texture width the set is laid out for; Painter gets a square set of <see cref="Size"/>.</param>
internal sealed record PainterSetLayout(string Name, int Width, int Height, IReadOnlyList<PainterTextureLayout> Textures,
    IReadOnlyList<string> RemoveChannels, IReadOnlyList<PainterChannelSpec> AddChannels)
{
    /// <summary>
    /// Painter shows every texture set as a square, so a non-square texture would look squashed:
    /// the set is square instead, and the texture fills its top-left Width × Height corner.
    /// </summary>
    public int Size => Math.Max(Width, Height);

    /// <summary> How many times the texture's width fits the square set; the mesh's U is divided by it. </summary>
    public int ScaleU => Size / Width;

    public int ScaleV => Size / Height;

    public bool Uses(string channel) => Textures.Any(t => t.Components.Any(c => c.Channel == channel));
}

internal enum PainterSeedKind { Color, Normal, Channel }

/// <summary> A seed image: the texture's color, its normal with Z rebuilt, or one component as grayscale. </summary>
internal sealed record PainterSeed(PainterTextureInput Texture, PainterSeedKind Kind, int Component, string Channel, string ColorSpace, string FileName);

/// <summary>
/// Maps FFXIV texture channels onto Painter channels. Semantic channels (base color, normal,
/// opacity, roughness, ...) are used where the shader's meaning fits, so Painter's viewport and
/// materials work on them; everything else goes to labelled user channels. Every channel of an
/// exported texture has exactly one home, so an untouched export reproduces the texture.
/// </summary>
internal static class PainterChannelMap
{
    public const int MinSize = 128;
    public const int MaxSize = 8192;
    public const int UserChannels = 8;
    private const string Rgb = "rgb";
    private static readonly string[] ComponentNames = ["r", "g", "b", "a"];
    private static readonly string[] UsageOrder = ["diffuse", "normal", "mask", "specular", "index"];

    private enum Family { Character, CharacterLegacy, Skin, Hair, Iris, Tattoo, Other }

    private static Family FamilyOf(string shaderPackage) => shaderPackage.ToLowerInvariant() switch
    {
        "characterlegacy.shpk" => Family.CharacterLegacy,
        "character.shpk" or "charactertransparency.shpk" or "characterglass.shpk" or "characterscroll.shpk"
            or "characterstockings.shpk" or "characterinc.shpk" => Family.Character,
        "skin.shpk" => Family.Skin,
        "hair.shpk" => Family.Hair,
        "iris.shpk" => Family.Iris,
        "charactertattoo.shpk" => Family.Tattoo,
        _ => Family.Other,
    };

    /// <summary> Shaders that color by their colorset, picked per texel by the index texture. </summary>
    public static bool IsCharacterShader(string shaderPackage) => FamilyOf(shaderPackage) is Family.Character or Family.CharacterLegacy;

    /// <summary> Components the TEX format stores; the others can't carry edits. </summary>
    public static int[] StoredComponents(uint format) => (TexFile.TextureFormat)format switch
    {
        TexFile.TextureFormat.BC4 => [0],
        TexFile.TextureFormat.BC5 => [0, 1],
        _ => [0, 1, 2, 3],
    };

    /// <summary> The Painter channel a component would ideally use; "rgb" groups R, G and B into a color channel. </summary>
    private static string? Preferred(Family family, string usage, int component) => (usage, component) switch
    {
        ("diffuse", < 3) => "BaseColor",
        ("diffuse", 3) => family == Family.Skin ? "Opacity" : null,
        ("normal", < 2) => "Normal",
        ("normal", 2) => family is Family.Character or Family.CharacterLegacy ? "Opacity" : null,
        ("normal", 3) => family is Family.Hair or Family.Tattoo ? "Opacity" : null,
        ("mask", 0) => family is Family.Character or Family.CharacterLegacy or Family.Skin or Family.Hair ? "Specularlevel" : null,
        ("mask", 1) => family switch
        {
            Family.CharacterLegacy => "Glossiness",
            Family.Character or Family.Skin or Family.Hair => "Roughness",
            _ => null,
        },
        ("mask", 2) => family switch
        {
            Family.Character or Family.CharacterLegacy => "AO",
            Family.Skin or Family.Hair => "Scattering",
            _ => null,
        },
        ("mask", 3) => family == Family.Hair ? "AO" : null,
        ("specular", < 3) => "Specular",
        _ => null,
    };

    public static PainterSetLayout Layout(PainterTextureSetInput input)
    {
        var family = FamilyOf(input.ShaderPackage);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var nextUser = 0;
        var ordered = input.Textures
            .OrderBy(t => t.Exported ? 0 : 1)
            .ThenBy(t => Array.IndexOf(UsageOrder, t.Usage) is var i && i >= 0 ? i : UsageOrder.Length)
            .ToList();
        var layouts = new List<PainterTextureLayout>();
        foreach (var texture in ordered)
        {
            var components = new List<PainterComponent>();
            var problem = "";
            var stored = StoredComponents(texture.Format);
            var groupChannel = GroupChannel(family, texture.Usage);
            var grouped = new HashSet<int>();
            if (groupChannel is not null && !taken.Contains(groupChannel))
            {
                var members = groupChannel == "Normal" ? new[] { 0, 1 } : new[] { 0, 1, 2 };
                if (members.All(stored.Contains))
                {
                    taken.Add(groupChannel);
                    foreach (var member in members)
                    {
                        components.Add(new PainterComponent(member, groupChannel, ""));
                        grouped.Add(member);
                    }
                }
            }
            foreach (var component in stored)
            {
                if (grouped.Contains(component))
                    continue;
                var preferred = Preferred(family, texture.Usage, component);
                if (preferred is not null && preferred != "BaseColor" && preferred != "Normal" && preferred != "Specular"
                    && taken.Add(preferred))
                {
                    components.Add(new PainterComponent(component, preferred, ""));
                    continue;
                }
                // Reference-only textures only fill semantic channels; user channels are for textures that go back.
                if (!texture.Exported)
                    continue;
                if (nextUser >= UserChannels)
                {
                    problem = "Painter has no free channel left for this texture.";
                    break;
                }
                var user = $"User{nextUser++}";
                taken.Add(user);
                components.Add(new PainterComponent(component, user, $"{texture.Usage}.{ComponentNames[component]}"));
            }
            layouts.Add(new PainterTextureLayout(texture, components, problem));
        }

        var seeded = layouts.Where(l => l.Components.Count > 0).Select(l => l.Texture).ToList();
        var width = Clamp(seeded.Count == 0 ? 1024 : seeded.Max(t => NextPowerOfTwo(t.Width)));
        var height = Clamp(seeded.Count == 0 ? 1024 : seeded.Max(t => NextPowerOfTwo(t.Height)));
        var used = layouts.SelectMany(l => l.Components.Select(c => c.Channel)).ToHashSet(StringComparer.Ordinal);
        var remove = new List<string> { "Metallic" };
        if (!used.Contains("Roughness"))
            remove.Add("Roughness");
        var add = new List<PainterChannelSpec>();
        foreach (var layout in layouts)
            foreach (var component in layout.Components)
            {
                if (component.Channel is "Normal" || add.Any(a => a.Type == component.Channel))
                    continue;
                var format = component.Channel is "BaseColor" or "Specular" ? "sRGB8" : "L8";
                add.Add(new PainterChannelSpec(component.Channel, format, component.Label));
            }
        return new PainterSetLayout(input.Name, width, height, layouts, remove, add);
    }

    private static string? GroupChannel(Family family, string usage) => usage switch
    {
        "diffuse" => "BaseColor",
        "normal" => "Normal",
        "specular" => family is Family.Character or Family.CharacterLegacy or Family.Other ? "Specular" : null,
        _ => null,
    };

    public static IReadOnlyList<PainterSeed> Seeds(PainterSetLayout set)
    {
        var seeds = new List<PainterSeed>();
        foreach (var layout in set.Textures)
        {
            foreach (var group in layout.Components.GroupBy(c => c.Channel))
            {
                var first = group.First();
                var (kind, space, suffix) = first.Channel switch
                {
                    "BaseColor" or "Specular" => (PainterSeedKind.Color, "color", Rgb),
                    "Normal" => (PainterSeedKind.Normal, "normal", "normal"),
                    _ => (PainterSeedKind.Channel, "data", ComponentNames[first.Component]),
                };
                seeds.Add(new PainterSeed(layout.Texture, kind, first.Component, first.Channel, space,
                    $"{layout.Texture.SeedStem}.{suffix}.tga"));
            }
        }
        return seeds;
    }

    /// <summary>
    /// The export map for one texture: a TGA named after its key, each channel read from its Painter
    /// home. In a square set laid out for a non-square texture, the whole set is exported at the
    /// size that makes its top-left corner the texture's own size; Instant Edit crops that corner.
    /// </summary>
    public static JsonObject ExportMap(PainterTextureLayout layout, int scaleU = 1, int scaleV = 1)
    {
        var channels = new JsonArray();
        for (var component = 0; component < 4; component++)
        {
            var destination = "RGBA"[component].ToString();
            var home = layout.Components.FirstOrDefault(c => c.Component == component);
            // Components the format doesn't store get a constant; the session's encoder drops them.
            channels.Add(home is null
                ? Channel(destination, "L", "defaultMap", component == 3 ? "white" : "black")
                : home.Channel switch
                {
                    "BaseColor" => Channel(destination, destination, "documentMap", "basecolor"),
                    "Specular" => Channel(destination, destination, "documentMap", "specular"),
                    "Normal" => Channel(destination, destination, "virtualMap", "Normal_OpenGL"),
                    _ => Channel(destination, "L", "documentMap", DocumentMapName(home.Channel)),
                });
        }
        return new JsonObject
        {
            ["fileName"] = layout.Texture.Key,
            ["channels"] = channels,
            ["parameters"] = new JsonObject
            {
                ["fileFormat"] = "tga",
                ["bitDepth"] = "8",
                ["dithering"] = false,
                ["paddingAlgorithm"] = "infinite",
                ["sizeLog2"] = new JsonArray(Log2(NextPowerOfTwo(layout.Texture.Width) * scaleU), Log2(NextPowerOfTwo(layout.Texture.Height) * scaleV)),
            },
        };
    }

    /// <summary>
    /// The whole export: one single-map preset per exported texture, each listed against its texture
    /// set. Painter applies a preset's first map parameters to all of its maps, so textures of one
    /// set that differ in size need presets of their own.
    /// </summary>
    public static JsonObject ExportConfig(IEnumerable<PainterSetLayout> sets)
    {
        var presets = new JsonArray();
        var list = new JsonArray();
        foreach (var set in sets)
        {
            foreach (var layout in set.Textures.Where(l => l.Exported))
            {
                var preset = $"xiv_{set.Name}__{layout.Texture.Key}";
                presets.Add(new JsonObject { ["name"] = preset, ["maps"] = new JsonArray(ExportMap(layout, set.ScaleU, set.ScaleV)) });
                list.Add(new JsonObject { ["rootPath"] = set.Name, ["exportPreset"] = preset });
            }
        }
        return new JsonObject { ["exportShaderParams"] = false, ["exportPresets"] = presets, ["exportList"] = list };
    }

    private static JsonObject Channel(string destination, string source, string mapType, string mapName) => new()
    {
        ["destChannel"] = destination,
        ["srcChannel"] = source,
        ["srcMapType"] = mapType,
        ["srcMapName"] = mapName,
    };

    private static string DocumentMapName(string channel) => channel switch
    {
        "Opacity" => "opacity",
        "Roughness" => "roughness",
        "Glossiness" => "glossiness",
        "AO" => "ambientOcclusion",
        "Specularlevel" => "specularlevel",
        "Scattering" => "scattering",
        _ when channel.StartsWith("User", StringComparison.Ordinal) => "user" + channel[4..],
        _ => throw new InvalidOperationException($"No export map for channel {channel}."),
    };

    /// <summary> The normal map's RG with Z rebuilt, since FFXIV reuses B and A for other data. </summary>
    public static RgbaImage NormalSeed(RgbaImage source)
    {
        var result = new RgbaImage(source.Width, source.Height, (byte[])source.Pixels.Clone());
        var p = result.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var x = p[i] / 255f * 2f - 1f;
            var y = p[i + 1] / 255f * 2f - 1f;
            var z = MathF.Sqrt(Math.Clamp(1f - x * x - y * y, 0f, 1f));
            p[i + 2] = (byte)Math.Round((z * 0.5f + 0.5f) * 255f);
            p[i + 3] = 255;
        }
        return result;
    }

    public static int NextPowerOfTwo(int value) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, value));

    private static int Clamp(int size) => Math.Clamp(size, MinSize, MaxSize);

    private static int Log2(int value) => BitOperations.Log2((uint)value);
}
