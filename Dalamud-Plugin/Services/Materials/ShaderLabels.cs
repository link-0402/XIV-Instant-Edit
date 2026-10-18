namespace InstantEdit.Services.Materials;

/// <summary>How the editor shows and edits a value.</summary>
public enum ValueKind
{
    Float,

    /// <summary>0 to 1, shown as 0 to 100 percent.</summary>
    Percent,

    /// <summary>An RGB colour (may go above 1).</summary>
    Color,

    /// <summary>An integer stored as its raw 32 bits, not as a float.</summary>
    Int,

    Enum,

    /// <summary>A detail-pattern tile, stored as a float.</summary>
    TileIndex,

    /// <summary>A sphere-map texture, stored as a float.</summary>
    SphereMapIndex,

    /// <summary>A lighting style id, stored as a float.</summary>
    ShaderId,

    Hidden,
}

/// <summary>
/// A material constant as the editor shows it. <see cref="Min"/> and <see cref="Max"/> both 0 mean
/// no limit; <see cref="Name"/> is the game's name for it (empty when unknown).
/// </summary>
public sealed record ConstantLabel(uint Id, string Name, string Label, string Help, ValueKind Kind, string Group, float Min, float Max, float Step);

/// <summary>A material key as the editor shows it, with a label for each value.</summary>
public sealed record KeyLabel(uint Id, string Label, string Help, IReadOnlyDictionary<uint, string> Values);

/// <summary>
/// Plain labels for every material setting, written for the editor (no dev-kit files). Unknown
/// ids get a hex label; nothing here throws.
/// </summary>
public static class ShaderLabels
{
    public const string Alpha = "Alpha";
    public const string Surface = "Surface";
    public const string Shadows = "Shadows";
    public const string Tile = "Tile";
    public const string Scroll = "Scroll";
    public const string Glass = "Glass";
    public const string Skin = "Skin";
    public const string Hair = "Hair";
    public const string Iris = "Iris";
    public const string Toon = "Toon";
    public const string Other = "Other";

    /// <summary>The order the editor draws the groups in.</summary>
    public static readonly string[] Groups = [Surface, Alpha, Shadows, Tile, Scroll, Glass, Skin, Hair, Iris, Toon, Other];

    public static readonly string[] WrapModes = ["Repeat", "Mirror", "Clamp", "Border"];

    private const string UnknownHelp = "The game reads this, but what it does is not known.";
    private const string UnusedHelp = "No character shader reads this.";

    private static ConstantLabel C(uint id, string name, string label, string help, ValueKind kind, string group, float min = 0, float max = 0, float step = 0.01f)
        => new(id, name, label, help, kind, group, min, max, step);

    private static ConstantLabel Unknown(uint id, string group, string help) => C(id, "", $"Unknown value 0x{id:X8}", help, ValueKind.Float, group);

    private static readonly ConstantLabel[] ConstantTable =
    [
        C(0x2C2A34DD, "g_DiffuseColor", "Base colour", "Tints the base colour.", ValueKind.Color, Surface, 0, 4),
        C(0x29AC0223, "g_AlphaThreshold", "Alpha cutoff", "Pixels under this alpha are not drawn.", ValueKind.Percent, Alpha, 0, 1),
        C(0xCB0338DC, "g_SpecularColorMask", "Shine colour", "Tints the shine.", ValueKind.Color, Surface, 0, 4),
        C(0xD925FF32, "g_ShadowAlphaThreshold", "Shadow cutoff", "Pixels under this alpha cast no shadow.", ValueKind.Float, Shadows, 0, 2),
        C(0x11C90091, "g_WhiteEyeColor", "Eye white colour", "Colour of the white of the eye.", ValueKind.Color, Iris, 0, 4),
        C(0x59BDA0B1, "g_ShaderID", "Lighting style", "Which lighting the shader uses.", ValueKind.ShaderId, Surface, 0, 31, 1),
        C(0x38A64362, "g_EmissiveColor", "Glow colour", "Light the material gives off.", ValueKind.Color, Surface, 0, 16),
        C(0x3632401A, "g_LipRoughnessScale", "Lip roughness", "How matte the lips look.", ValueKind.Percent, Skin, 0, 1),
        C(0x50E36D56, "g_IrisRingColor", "Limbal ring colour", "Colour of the ring around the iris.", ValueKind.Color, Iris, 0, 4),
        C(0x074953E9, "g_SphereMapIndex", "Reflection map", "Which reflection texture is used.", ValueKind.SphereMapIndex, Surface, 0, 127, 1),
        C(0x58DE06E2, "g_IrisRingForceColor", "Limbal ring override", "Replaces the limbal ring colour.", ValueKind.Color, Iris, 0, 4),
        C(0xE211758E, "g_SphereMapRate", "Reflection amount", "Name is a guess. " + UnusedHelp, ValueKind.Percent, Other, 0, 1),
        C(0x623CC4FE, "g_OutlineColor", "Outline colour", "Colour of the toon outline.", ValueKind.Color, Toon, 0, 4),
        C(0xB7FA33E2, "g_SSAOMask", "Ambient shadow", "How much nearby shading darkens it.", ValueKind.Percent, Shadows, 0, 1),
        C(0x2E60B071, "g_TileScale", "Detail scale", "How often the detail pattern repeats.", ValueKind.Float, Tile, 0, 512, 0.5f),
        C(0xE18398AE, "g_IrisRingUvRadius", "Limbal ring size", "Inner and outer size of the ring.", ValueKind.Float, Iris, 0, 1, 0.001f),
        C(0x5B608CFE, "g_IrisRingUvFadeWidth", "Limbal ring fade", "How soft the ring's edges are.", ValueKind.Float, Iris, 0, 1, 0.001f),
        Unknown(0x1A60F60E, Iris, "Two values the iris shader reads. Mods often use 0.3 and 0."),
        Unknown(0x07C3EFE6, Other, UnusedHelp),
        Unknown(0x9ECABE5C, Other, UnusedHelp),
        Unknown(0xE9CD8ECA, Other, UnusedHelp),
        C(0x4255F2F4, "g_TileIndex", "Detail pattern", "Which detail pattern is used.", ValueKind.TileIndex, Tile, 0, 63, 1),
        C(0x12C6AC9F, "g_TileAlpha", "Detail strength", "How strongly the pattern shows.", ValueKind.Percent, Tile, 0, 1),
        C(0x15B70E35, "g_SpecularVelvetParam", "Velvet", "Name is a guess. Read by legacy gear with a shine mask.", ValueKind.Float, Surface, 0, 1),
        C(0xB5545FBB, "g_NormalScale", "Bump strength", "Scales the normal map.", ValueKind.Float, Surface, 0, 2),
        C(0x800EE35F, "g_SheenRate", "Sheen", "Soft light at glancing angles.", ValueKind.Percent, Surface, 0, 1),
        C(0x1F264897, "g_SheenTintRate", "Sheen tint", "How much the sheen takes the base colour.", ValueKind.Percent, Surface, 0, 1),
        C(0xF490F76E, "g_SheenAperture", "Sheen spread", "Lower spreads the sheen wider.", ValueKind.Float, Surface, 0, 20, 0.1f),
        C(0x641E0F22, "g_VertexMovementScale", "Sway", "How much the mesh moves in the wind.", ValueKind.Float, Other, 0, 2),
        C(0xD26FF0AE, "g_VertexMovementMaxLength", "Sway limit", "How far the mesh may move.", ValueKind.Float, Other, 0, 2),
        C(0x37DEA328, "g_IrisUvRadius", "Iris size", "Size of the iris.", ValueKind.Float, Iris, 0, 1, 0.001f),
        C(0x7DABA471, "g_IrisRingEmissiveIntensity", "Limbal ring glow", "How much the ring glows.", ValueKind.Float, Iris, 0, 4),
        C(0x285F72D2, "g_IrisRingOddRate", "Limbal ring style", "A whole number the iris shader reads.", ValueKind.Int, Iris, 0, 16, 1),
        C(0x66C93D3E, "g_IrisThickness", "Iris depth", "How deep the iris looks.", ValueKind.Float, Iris, 0, 2),
        C(0x29253809, "g_IrisOptionColorRate", "Option colour", "How much the second eye colour shows.", ValueKind.Percent, Iris, 0, 1),
        C(0x8EA14846, "g_IrisOptionColorEmissiveRate", "Option colour glow", "How much the second eye colour glows.", ValueKind.Percent, Iris, 0, 1),
        C(0x7918D232, "g_IrisOptionColorEmissiveIntensity", "Option glow strength", "Brightness of that glow.", ValueKind.Float, Iris, 0, 8),
        C(0xD62BF368, "g_AlphaAperture", "Alpha sharpness", "Higher makes alpha edges harder.", ValueKind.Float, Alpha, 0, 10, 0.05f),
        C(0xD07A6A65, "g_AlphaOffset", "Alpha offset", "Shifts the alpha up or down.", ValueKind.Float, Alpha, -2, 2),
        Unknown(0xAD94E254, Other, UnknownHelp),
        Unknown(0xAE4F649C, Glass, "The glass shader reads this; what it does is not known."),
        C(0x7801E004, "g_GlassIOR", "Refraction", "How much the glass bends what is behind it.", ValueKind.Float, Glass, 0, 3),
        C(0xC4647F37, "g_GlassThicknessMax", "Glass thickness", "How thick the glass looks.", ValueKind.Float, Glass, 0, 1, 0.001f),
        C(0x39551220, "g_TextureMipBias", "Texture blur", "Below 0 is sharper, above 0 blurrier.", ValueKind.Float, Other, -4, 4, 0.1f),
        Unknown(0xB61D7498, Other, UnknownHelp),
        C(0x8870C938, "g_OutlineWidth", "Outline width", "Width of the toon outline.", ValueKind.Float, Toon, 0, 10),
        C(0xDF15112D, "g_ToonIndex", "Toon shading", "Which toon shading ramp is used.", ValueKind.Float, Toon, 0, 16, 1),
        C(0x00A680BC, "g_ToonSpecIndex", "Toon highlight", "Which toon highlight ramp is used.", ValueKind.Int, Toon, 0, 16, 1),
        Unknown(0x2B5EB116, Toon, "The toon shader reads this. Default -45."),
        Unknown(0x5C598180, Toon, "The toon shader reads this. Default 45."),
        C(0x3CCE9E4C, "g_ToonLightScale", "Toon light", "Strength of toon lighting.", ValueKind.Float, Toon, 0, 10),
        C(0x759036EE, "g_ToonLightSpecAperture", "Toon highlight size", "Lower makes the highlight bigger.", ValueKind.Float, Toon, 0, 200, 1),
        Unknown(0x6C159E95, Toon, "The toon shader reads this. Default 0.85."),
        C(0xD96FAF7A, "g_ToonReflectionScale", "Toon reflection", "Strength of toon reflections.", ValueKind.Float, Toon, 0, 10),
        C(0x5351646E, "g_ShadowPosOffset", "Shadow offset", "Moves the shadow away from the surface.", ValueKind.Float, Shadows, -1, 1),
        C(0x6421DD30, "g_TileMipBiasOffset", "Detail blur", "Below 0 is sharper, above 0 blurrier.", ValueKind.Float, Tile, -4, 4, 0.1f),
        Unknown(0x4048A102, Other, UnusedHelp),
        Unknown(0x6BE878E0, Other, UnusedHelp),
        C(0x43345395, "g_ScrollBaseScaleU_1", "Layer 1 scale across", "How often layer 1 repeats across.", ValueKind.Float, Scroll, 0, 100, 0.05f),
        C(0x4172EDCC, "g_ScrollBaseScaleV_1", "Layer 1 scale down", "How often layer 1 repeats down.", ValueKind.Float, Scroll, 0, 100, 0.05f),
        C(0x738A241C, "g_ScrollTransTimeU_1", "Layer 1 speed across", "How fast layer 1 moves across. 0 is still.", ValueKind.Float, Scroll, -10, 10),
        C(0x71CC9A45, "g_ScrollTransTimeV_1", "Layer 1 speed down", "How fast layer 1 moves down. 0 is still.", ValueKind.Float, Scroll, -10, 10),
        Unknown(0x72DFF568, Other, UnusedHelp),
        Unknown(0xDCA5BF7D, Other, UnusedHelp),
        C(0xDA3D022F, "g_ScrollBaseScaleU_2", "Layer 2 scale across", "How often layer 2 repeats across.", ValueKind.Float, Scroll, 0, 100, 0.05f),
        C(0xD87BBC76, "g_ScrollBaseScaleV_2", "Layer 2 scale down", "How often layer 2 repeats down.", ValueKind.Float, Scroll, 0, 100, 0.05f),
        C(0xEA8375A6, "g_ScrollTransTimeU_2", "Layer 2 speed across", "How fast layer 2 moves across. 0 is still.", ValueKind.Float, Scroll, -10, 10),
        C(0xE8C5CBFF, "g_ScrollTransTimeV_2", "Layer 2 speed down", "How fast layer 2 moves down. 0 is still.", ValueKind.Float, Scroll, -10, 10),
        Unknown(0xEBD6A4D2, Other, UnusedHelp),
        Unknown(0x45ACEEC7, Other, UnusedHelp),
        C(0x4B9CB34A, "g_EmissiveColorScale", "Glow scale", "Name is a guess. " + UnusedHelp, ValueKind.Float, Other, 0, 16),
        Unknown(0xE96E969F, Other, UnusedHelp),
        Unknown(0xEB2828C6, Other, UnusedHelp),
        Unknown(0xEBC6DC63, Other, UnusedHelp),
        Unknown(0x7067C725, Other, UnusedHelp),
        Unknown(0x7221797C, Other, UnusedHelp),
        Unknown(0x72CF8DD9, Other, UnusedHelp),
        Unknown(0x0760F7B3, Other, UnusedHelp),
        Unknown(0x052649EA, Other, UnusedHelp),
        Unknown(0x05C8BD4F, Other, UnusedHelp),
        Unknown(0xD4F2D701, Other, UnusedHelp),

        // Older materials carry these; no Dawntrail package declares them.
        C(0x575ABFB2, "g_AmbientOcclusionMask", "Old ambient shadow", "From older tools. The game ignores it.", ValueKind.Float, Other),
        C(0x36080AD0, "g_SpecularMask", "Old shine mask", "From older tools. The game ignores it.", ValueKind.Float, Other),
        C(0xB500BB24, "g_ScatteringLevel", "Old scattering", "From older tools. The game ignores it.", ValueKind.Float, Other),
    ];

    private static readonly Dictionary<uint, ConstantLabel> Constants = ConstantTable.ToDictionary(label => label.Id);

    private static KeyLabel K(uint id, string label, string help, params (uint Value, string Label)[] values)
        => new(id, label, help, values.ToDictionary(value => value.Value, value => value.Label));

    private static readonly KeyLabel[] KeyTable =
    [
        K(0xB616DC5A, "Texture mode", "Colour table mode, or a plain diffuse texture.",
            (0x5CC605B5, "Colour table"), (0x600EF9DF, "Diffuse texture")),
        K(0x40D1481E, "Flow map", "A flow texture bends the shine, like on hair.",
            (0x337C6BC4, "Off"), (0x71ADA939, "On")),
        K(0xD2777173, "Face decal", "How the face paint texture is used.",
            (0x4242B842, "Off"), (0x584265DD, "Alpha"), (0xF35F5131, "Colour")),
        K(0xF52CCF05, "Vertex colour", "How the mesh's vertex colours are read.",
            (0xA7D2FF60, "Usual for gear (0xA7D2FF60)"), (0xDFE74BAC, "All four channels (0xDFE74BAC)")),
        K(0xC8BD1DEF, "Shine from", "Legacy gear: where the shine comes from.",
            (0x198D11CD, "Specular texture"), (0xA02F4828, "Mask texture")),
        K(0xE8DA5B62, "Depth", "How the see-through parts are sorted.",
            (0x7B804D6E, "Dither"), (0x71A1FBF8, "Plain (0x71A1FBF8)")),
        K(0xF886E10E, "Reflection source", "Scroll shader: what the reflection reads.",
            (0x69EB4AE0, "Sphere map (0x69EB4AE0)"), (0x9A8A46F5, "Catchlight texture (0x9A8A46F5)"), (0xD21B9BED, "Both (0xD21B9BED)")),
        K(0x0033C8B5, "Lighting", "Whether light and shadow affect it.",
            (0xD1E60FD9, "On"), (0x93D6C21A, "Off")),
        K(0x380CAED0, "Skin part", "Which kind of skin this is.",
            (0xF5673524, "Face"), (0xF421D264, "Face with wrinkles"), (0x72E697CD, "Face with glow"),
            (0x2BDB45F1, "Body"), (0x57FF3B64, "Body (JJM)")),
        K(0x24826489, "Hair colour from", "Which character colour tints it.",
            (0xF7B8956E, "Hair colour"), (0x6E5B8F10, "Face feature colour"), (0x4B3D7863, "Hair colour (0x4B3D7863)")),
        K(0xB3DC6CB8, "Second UV set", "Hair: also read the mesh's second UV set.",
            (0xE3D0D77E, "Off (0xE3D0D77E)"), (0x0DDEB652, "On (0x0DDEB652)")),
        K(0x63030C80, "Diffuse texture", "Eyes: use a diffuse texture.",
            (0x3839C7E4, "Off"), (0xEFDEA8F6, "On")),
    ];

    private static readonly Dictionary<uint, KeyLabel> Keys = KeyTable.ToDictionary(label => label.Id);

    private static readonly Dictionary<uint, string> SamplerTable = new()
    {
        [0x0C5EC1F1] = "Normal",
        [0x8A4E82B6] = "Mask",
        [0x565F8FD8] = "Index",
        [0x115306BE] = "Diffuse",
        [0x2B99E025] = "Specular",
        [0xA7E197F6] = "Flow",
        [0xFEA0F3D2] = "Catchlight",
        [0x0237CB94] = "Decal",
        [0x3334D3CA] = "Sphere map",
        [0x87F6474D] = "Reflection",
        [0xC5C4CB3C] = "Reflection array",
        [0x0261CDCB] = "Second normal",
        [0xB3F13975] = "Wrinkles mask",
    };

    private static readonly string[] RowFields =
    [
        "Base red", "Base green", "Base blue", "Value 3",
        "Shine red", "Shine green", "Shine blue", "Value 7",
        "Glow red", "Glow green", "Glow blue", "Base brightness",
        "Sheen", "Sheen tint", "Sheen spread", "Value 15",
        "Roughness", "Value 17", "Metalness", "Anisotropy",
        "Value 20", "Reflection strength", "Value 22", "Scroll layer",
        "Lighting style", "Detail pattern", "Detail strength", "Reflection map",
        "Detail UU", "Detail UV", "Detail VU", "Detail VV",
    ];

    /// <summary>Shader flags by bit. Only two are known.</summary>
    public static readonly (string Label, string Help)[] Flags = Enumerable.Range(0, 32)
        .Select(bit => bit switch
        {
            0 => ("Hide backfaces", "Faces seen from behind are not drawn."),
            4 => ("Translucent", "Draws the material see-through."),
            _ => ($"Flag 0x{1u << bit:X}", "What this flag does is not known."),
        })
        .ToArray();

    public static IEnumerable<ConstantLabel> KnownConstants => ConstantTable;
    public static IEnumerable<KeyLabel> KnownKeys => KeyTable;
    public static IEnumerable<string> KnownSamplers => SamplerTable.Values;

    public static ConstantLabel Constant(uint id)
        => Constants.TryGetValue(id, out var label) ? label : C(id, "", $"Unknown value 0x{id:X8}", UnknownHelp, ValueKind.Float, Other);

    public static KeyLabel Key(uint id)
        => Keys.TryGetValue(id, out var label) ? label : new KeyLabel(id, $"Unknown key 0x{id:X8}", "What this key does is not known.", new Dictionary<uint, string>());

    public static string KeyValue(uint key, uint value)
        => Keys.TryGetValue(key, out var label) && label.Values.TryGetValue(value, out var name) ? name : $"0x{value:X8}";

    public static string Sampler(uint id) => SamplerTable.TryGetValue(id, out var label) ? label : $"Unknown texture 0x{id:X8}";

    public static string RowField(int half) => half >= 0 && half < RowFields.Length ? RowFields[half] : $"Value {half}";
}
