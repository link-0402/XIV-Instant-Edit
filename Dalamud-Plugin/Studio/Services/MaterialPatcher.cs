using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>How one configured texture ends up in the built material.</summary>
public enum TextureWiring
{
    /// <summary>The template already had a slot for it; its path was swapped in.</summary>
    Replaced,
    /// <summary>The template had no slot; a texture entry and its sampler were added.</summary>
    Added,
    /// <summary>It could not be put into the material: written to the mod, but nothing reads it.</summary>
    NotWired,
}

public sealed record TextureWiringEntry(TextureType Type, TextureWiring Wiring, string? Reason = null);

/// <summary>What building a material from a template does: per texture, plus the shader keys it sets.</summary>
public sealed class MaterialPlan
{
    public List<TextureWiringEntry> Textures { get; } = new();

    /// <summary>Placeholder textures of a bundled preset that nothing was configured for, dropped from the material.</summary>
    public List<TextureType> Removed { get; } = new();

    /// <summary>Human-readable shader key changes, e.g. "Texture Mode → Compatibility".</summary>
    public List<string> KeyChanges { get; } = new();

    /// <summary>False when the template's shader section could not be read, so only texture paths were swapped.</summary>
    public bool ParametersEditable { get; set; } = true;

    public IEnumerable<TextureType> NotWired => Textures.Where(t => t.Wiring == TextureWiring.NotWired).Select(t => t.Type);

    /// <summary>One line for the report of an update: what was added, dropped and switched.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        var added = Textures.Where(t => t.Wiring == TextureWiring.Added).Select(t => t.Type.ToString()).ToList();
        if (added.Count > 0)         parts.Add($"added {string.Join(", ", added)}");
        if (Removed.Count > 0)       parts.Add($"dropped unused {string.Join(", ", Removed)}");
        if (KeyChanges.Count > 0)    parts.Add(string.Join(", ", KeyChanges));
        var notWired = Textures.Where(t => t.Wiring == TextureWiring.NotWired).ToList();
        if (notWired.Count > 0)      parts.Add($"not wired: {string.Join("; ", notWired.Select(t => $"{t.Type} ({t.Reason})"))}");
        if (!ParametersEditable)     parts.Add("shader data unreadable, only texture paths swapped");
        return string.Join("; ", parts);
    }
}

/// <summary>
/// Builds a mod's .mtrl from a template (a bundled shader preset, or a vanilla material):
/// <list type="bullet">
/// <item>each template texture's role is read from the sampler that uses it (falling back to its
///   "_n"/"_mask"/… suffix), and the configured texture of that role is swapped in;</item>
/// <item>a configured texture the template has no slot for is added, with a sampler of the matching
///   id (settings copied from the template's normal sampler);</item>
/// <item>a bundled preset's own placeholder textures that nothing was configured for are dropped, so
///   the material never points at the sample item the preset was made from (a vanilla template
///   keeps them — they are that item's real textures), except the Matcap preset's MatCap Library
///   texture, which is shared on purpose;</item>
/// <item>on character.shpk, the shader keys that select textures follow what is configured: a diffuse
///   map needs Texture Mode = Compatibility, a specular map Specular Mode = its own sampler.</item>
/// </list>
/// The colour table, constants and everything else are carried over unchanged. Ids come from
/// xivModdingFramework's ShaderHelpers.
/// </summary>
public static class MaterialPatcher
{
    // ── Sampler ids ──────────────────────────────────────────────────────────
    private const uint SamplerNormal     = 0x0C5EC1F1;
    private const uint SamplerSpecular   = 0x2B99E025;
    private const uint SamplerDiffuse    = 0x115306BE;
    private const uint SamplerMask       = 0x8A4E82B6;
    private const uint SamplerIndex      = 0x565F8FD8;
    private const uint SamplerReflection = 0x87F6474D;
    private const uint SamplerReflectionArray = 0xC5C4CB3C;
    private const uint SamplerCatchlight = 0xFEA0F3D2;   // characterreflection.shpk: where a matcap image goes

    /// <summary>
    /// The MatCap Library mod's shared matcap textures. Matcap mods point at these instead of shipping
    /// their own, so the Matcap preset keeps its placeholder when no image of the user's replaces it.
    /// </summary>
    private const string MatcapLibraryPrefix = "chara/common/texture/matcaplib_";

    /// <summary>The MatCap Library texture a matcap material reads until an image of the user's own replaces it (slot A).</summary>
    public const string MatcapLibraryDefault = MatcapLibraryPrefix + "a.tex";

    // ── character.shpk keys ──────────────────────────────────────────────────
    private const uint KeyTextureMode        = 0xB616DC5A;
    private const uint TextureModeDefault    = 0x5CC605B5;
    private const uint TextureModeCompat     = 0x600EF9DF;
    private const uint KeySpecularMode       = 0xC8BD1DEF;
    private const uint SpecularModeSampler   = 0x198D11CD;   // "COMPAT_DEFAULT": the dedicated specular sampler

    private static TextureType? TypeOfSampler(uint id) => id switch
    {
        SamplerDiffuse                                                   => TextureType.Diffuse,
        SamplerNormal                                                    => TextureType.Normal,
        SamplerSpecular                                                  => TextureType.Specular,
        SamplerMask                                                      => TextureType.Mask,
        SamplerIndex                                                     => TextureType.Index,
        SamplerReflection or SamplerReflectionArray or SamplerCatchlight => TextureType.Reflection,
        _                                                                => null,
    };

    private static uint? SamplerFor(TextureType type) => type switch
    {
        TextureType.Diffuse  => SamplerDiffuse,
        TextureType.Normal   => SamplerNormal,
        TextureType.Specular => SamplerSpecular,
        TextureType.Mask     => SamplerMask,
        TextureType.Index    => SamplerIndex,
        _                    => null,   // reflection samplers vary by shader; only replaced, never added
    };

    /// <summary>
    /// What building would do with <paramref name="configured"/> texture roles on this template,
    /// without writing anything — for the validator and the inspector.
    /// </summary>
    public static MaterialPlan Plan(MtrlInfo template, IEnumerable<TextureType> configured, bool isPreset)
    {
        var paths = configured.Distinct().ToDictionary(t => t, t => $"plan/{t}.tex");
        return Apply(template.Clone(), paths, isPreset);
    }

    public static bool TryBuildPatchedMaterial(
        MtrlInfo template,
        IReadOnlyDictionary<TextureType, string> replacements,
        bool isPreset,
        out byte[] result,
        out MaterialPlan plan,
        out string? error)
    {
        result = Array.Empty<byte>();
        plan = new MaterialPlan();
        error = null;

        try
        {
            var material = template.Clone();
            plan = Apply(material, replacements, isPreset);
            result = material.ShaderDataParsed ? Write(material) : WritePathsOnly(material);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Wiring
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Edits <paramref name="m"/> in place (it must be a copy) and reports what it did.</summary>
    private static MaterialPlan Apply(MtrlInfo m, IReadOnlyDictionary<TextureType, string> paths, bool isPreset)
    {
        var plan = new MaterialPlan { ParametersEditable = m.ShaderDataParsed };
        var textures = m.TextureOffsets.ToList();
        var types = Enumerable.Range(0, textures.Count).Select(i => TypeOf(m, i)).ToList();
        var wired = new HashSet<TextureType>();

        // Swap paths into the slots the template already has.
        for (int i = 0; i < textures.Count; i++)
        {
            if (types[i] is { } type && paths.TryGetValue(type, out var path))
            {
                textures[i] = new MtrlTextureOffset(path, textures[i].Flags);
                if (wired.Add(type))
                    plan.Textures.Add(new(type, TextureWiring.Replaced));
            }
        }

        // Add what the template has no slot for.
        foreach (var (type, path) in paths.OrderBy(p => p.Key))
        {
            if (wired.Contains(type))
                continue;

            string? reason = !m.ShaderDataParsed ? "the template's shader data could not be read"
                           : SamplerFor(type) == null ? $"a {TextureTypeInfo.DisplayName(type).ToLowerInvariant()} map can only replace one the template already has"
                           : m.Samplers.Count == 0 ? "the template has no samplers to copy settings from"
                           : textures.Count >= byte.MaxValue ? "the template has too many textures"
                           : null;
            if (reason != null)
            {
                plan.Textures.Add(new(type, TextureWiring.NotWired, reason));
                continue;
            }

            var like = m.Samplers.FirstOrDefault(s => s.Id == SamplerNormal) ?? m.Samplers[0];
            textures.Add(new MtrlTextureOffset(path, textures.Count > 0 ? textures[0].Flags : (ushort)0));
            types.Add(type);
            m.Samplers.Add(new MtrlSampler { Id = SamplerFor(type)!.Value, Settings = like.Settings, TextureIndex = (byte)(textures.Count - 1) });
            wired.Add(type);
            plan.Textures.Add(new(type, TextureWiring.Added));
        }

        // A preset's own placeholders point at the sample item it was made from — drop the unused ones.
        if (isPreset && m.ShaderDataParsed)
        {
            for (int i = textures.Count - 1; i >= 0; i--)
            {
                if (types[i] is not { } type || paths.ContainsKey(type) || IsMatcapLibraryTexture(textures[i].Path))
                    continue;
                RemoveTexture(m, textures, i);
                types.RemoveAt(i);
                plan.Removed.Add(type);
            }
        }

        if (m.ShaderDataParsed && string.Equals(m.ShaderPackageName, "character.shpk", StringComparison.OrdinalIgnoreCase))
        {
            var mode = m.ShaderKeys.FirstOrDefault(k => k.Id == KeyTextureMode)?.Value;
            if (wired.Contains(TextureType.Diffuse) && mode != TextureModeCompat)
            {
                SetKey(m, KeyTextureMode, TextureModeCompat);
                plan.KeyChanges.Add("Texture Mode → Compatibility");
            }
            else if (!wired.Contains(TextureType.Diffuse) && mode == TextureModeCompat)
            {
                SetKey(m, KeyTextureMode, TextureModeDefault);
                plan.KeyChanges.Add("Texture Mode → Default");
            }

            if (wired.Contains(TextureType.Specular) && m.ShaderKeys.FirstOrDefault(k => k.Id == KeySpecularMode)?.Value != SpecularModeSampler)
            {
                SetKey(m, KeySpecularMode, SpecularModeSampler);
                plan.KeyChanges.Add("Specular Mode → specular map");
            }
        }

        m.TextureOffsets = textures.ToArray();
        m.TextureCount = (byte)textures.Count;
        return plan;
    }

    /// <summary>A texture's role: from the sampler that reads it, else from its file name.</summary>
    internal static TextureType? TypeOf(MtrlInfo m, int textureIndex)
    {
        if (m.ShaderDataParsed)
            foreach (var s in m.Samplers)
                if (s.TextureIndex == textureIndex && TypeOfSampler(s.Id) is { } t)
                    return t;
        return ClassifyBySuffix(m.TextureOffsets[textureIndex].Path);
    }

    /// <summary>The roles of a material's textures (for reading a vanilla material's layout).</summary>
    public static IEnumerable<TextureType> TextureTypes(MtrlInfo m)
        => Enumerable.Range(0, m.TextureOffsets.Length).Select(i => TypeOf(m, i)).OfType<TextureType>().Distinct();

    internal static bool IsMatcapLibraryTexture(string path)
        => path.StartsWith(MatcapLibraryPrefix, StringComparison.OrdinalIgnoreCase);

    private static void RemoveTexture(MtrlInfo m, List<MtrlTextureOffset> textures, int index)
    {
        textures.RemoveAt(index);
        m.Samplers.RemoveAll(s => s.TextureIndex == index);
        foreach (var s in m.Samplers)
            if (s.TextureIndex > index && s.TextureIndex != byte.MaxValue)
                s.TextureIndex--;
    }

    private static void SetKey(MtrlInfo m, uint id, uint value)
    {
        var key = m.ShaderKeys.FirstOrDefault(k => k.Id == id);
        if (key != null) key.Value = value;
        else m.ShaderKeys.Add(new MtrlShaderKey { Id = id, Value = value });
    }

    /// <summary>
    /// Maps a texture path's suffix to a <see cref="TextureType"/>: this tool's own short
    /// convention ("_d"/"_n"/"_s"/"_m"/"_id"/"_r") and the longer spellings TexTools' presets use
    /// ("_base"/"_norm"/"_mask", …). Only a fallback — samplers say it for certain.
    /// </summary>
    internal static TextureType? ClassifyBySuffix(string texturePath)
    {
        var stem = Path.GetFileNameWithoutExtension(texturePath);
        int idx = stem.LastIndexOf('_');
        if (idx < 0 || idx == stem.Length - 1) return null;

        return stem[(idx + 1)..] switch
        {
            "d" or "base" or "diffuse"    => TextureType.Diffuse,
            "n" or "norm" or "normal"     => TextureType.Normal,
            "s" or "spec" or "specular"   => TextureType.Specular,
            "m" or "mask"                 => TextureType.Mask,
            "id"                          => TextureType.Index,
            "r" or "refl" or "reflection" => TextureType.Reflection,
            _                             => null,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Writing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Serialises a fully parsed material.</summary>
    public static byte[] Write(MtrlInfo m)
    {
        if (!m.ShaderDataParsed)
            throw new InvalidOperationException("Material shader data was not parsed; it cannot be rewritten.");

        var (strings, textureOffsets, uvSets, colorSets, shpkOffset) = BuildStrings(m);

        var body = new List<byte>();
        foreach (var o in textureOffsets) WriteU32(body, o);
        foreach (var (nameOffset, index, unknown1) in uvSets)    { WriteU16(body, nameOffset); body.Add(index); body.Add(unknown1); }
        foreach (var (nameOffset, index, unknown1) in colorSets) { WriteU16(body, nameOffset); body.Add(index); body.Add(unknown1); }
        body.AddRange(strings);
        body.AddRange(m.AdditionalData);
        body.AddRange(m.DataSet);

        WriteU16(body, (ushort)m.ShaderValues.Length);
        WriteU16(body, (ushort)m.ShaderKeys.Count);
        WriteU16(body, (ushort)m.Constants.Count);
        WriteU16(body, (ushort)m.Samplers.Count);
        WriteU16(body, m.MaterialFlags1);
        WriteU16(body, m.MaterialFlags2);
        foreach (var k in m.ShaderKeys) { WriteU32(body, k.Id); WriteU32(body, k.Value); }
        foreach (var c in m.Constants)  { WriteU32(body, c.Id); WriteU16(body, c.Offset); WriteU16(body, c.Size); }
        foreach (var s in m.Samplers)
        {
            WriteU32(body, s.Id);
            WriteU32(body, s.Settings);
            body.Add(s.TextureIndex);
            body.AddRange(s.Padding.Length == 3 ? s.Padding : new byte[3]);
        }
        body.AddRange(m.ShaderValues);

        var header = new List<byte>(16);
        uint fileSize = (uint)(16 + body.Count);
        WriteU32(header, m.Version);
        WriteU32(header, (fileSize & 0xFFFF) | ((uint)m.DataSet.Length << 16));
        WriteU16(header, (ushort)strings.Count);
        WriteU16(header, shpkOffset);
        header.Add((byte)m.TextureOffsets.Length);
        header.Add((byte)m.UvColorSets.Length);
        header.Add((byte)m.ColorSets.Length);
        header.Add((byte)m.AdditionalData.Length);

        header.AddRange(body);
        return header.ToArray();
    }

    /// <summary>For a material whose shader section could not be parsed: swaps texture paths, copies the rest verbatim.</summary>
    private static byte[] WritePathsOnly(MtrlInfo m)
    {
        var (strings, textureOffsets, uvSets, colorSets, shpkOffset) = BuildStrings(m);

        var output = new List<byte>();
        WriteU32(output, m.Version);
        WriteU32(output, m.FileSize | ((uint)m.DataSetSize << 16));
        WriteU16(output, (ushort)strings.Count);
        WriteU16(output, shpkOffset);
        output.Add((byte)m.TextureOffsets.Length);
        output.Add(m.UvSetCount);
        output.Add(m.ColorSetCount);
        output.Add(m.AdditionalDataSize);
        foreach (var o in textureOffsets) WriteU32(output, o);
        foreach (var (nameOffset, index, unknown1) in uvSets)    { WriteU16(output, nameOffset); output.Add(index); output.Add(unknown1); }
        foreach (var (nameOffset, index, unknown1) in colorSets) { WriteU16(output, nameOffset); output.Add(index); output.Add(unknown1); }
        output.AddRange(strings);
        output.AddRange(m.Tail);
        return output.ToArray();
    }

    private static (List<byte> Strings, uint[] TextureOffsets, (ushort, byte, byte)[] UvSets, (ushort, byte, byte)[] ColorSets, ushort ShpkOffset)
        BuildStrings(MtrlInfo m)
    {
        var strings = new List<byte>();

        var textureOffsets = new uint[m.TextureOffsets.Length];
        for (int i = 0; i < textureOffsets.Length; i++)
        {
            uint offset = (uint)strings.Count;
            AppendCString(strings, m.TextureOffsets[i].Path);
            textureOffsets[i] = offset | ((uint)m.TextureOffsets[i].Flags << 16);
        }

        var uvSets = new (ushort, byte, byte)[m.UvColorSets.Length];
        for (int i = 0; i < uvSets.Length; i++)
        {
            var offset = (ushort)strings.Count;
            AppendCString(strings, m.UvColorSets[i].Name);
            uvSets[i] = (offset, m.UvColorSets[i].Index, m.UvColorSets[i].Unknown1);
        }

        var colorSets = new (ushort, byte, byte)[m.ColorSets.Length];
        for (int i = 0; i < colorSets.Length; i++)
        {
            var offset = (ushort)strings.Count;
            AppendCString(strings, m.ColorSets[i].Name);
            colorSets[i] = (offset, m.ColorSets[i].Index, m.ColorSets[i].Unknown1);
        }

        var shpkOffset = (ushort)strings.Count;
        AppendCString(strings, m.ShaderPackageName);

        while (strings.Count % 4 != 0)
            strings.Add(0);

        return (strings, textureOffsets, uvSets, colorSets, shpkOffset);
    }

    private static void AppendCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.UTF8.GetBytes(s ?? string.Empty));
        b.Add(0);
    }

    private static void WriteU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
}
