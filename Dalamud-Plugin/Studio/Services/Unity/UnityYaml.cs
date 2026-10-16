using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InstantEdit.Studio.Services.Unity;

/// <summary>
/// The few things read from Unity's YAML files: a meta file's GUID and texture type, a material's
/// name, shader and textures, and the materials a prefab references. Only that subset, by pattern,
/// not a YAML parser: Unity writes these files in one fixed layout.
/// </summary>
internal static partial class UnityYaml
{
    /// <summary>Unity's GUID for "no asset" (a built-in or missing reference).</summary>
    private const string NullGuid = "0000000000000000f000000000000000";

    [GeneratedRegex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline)]
    private static partial Regex MetaGuid();

    [GeneratedRegex(@"^TextureImporter:\s*$", RegexOptions.Multiline)]
    private static partial Regex TextureImporter();

    [GeneratedRegex(@"^\s+textureType:\s*(-?\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex TextureTypeLine();

    [GeneratedRegex(@"^\s*m_Name:\s*(.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^\s*m_Shader:\s*\{[^}]*?guid:\s*([0-9a-fA-F]{32})", RegexOptions.Multiline)]
    private static partial Regex ShaderLine();

    // "    - _MainTex:" then "        m_Texture: {fileID: 2800000, guid: <32 hex>, type: 3}".
    [GeneratedRegex(@"^\s*-\s+(\w+):\s*\r?\n\s*m_Texture:\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]{32}))?", RegexOptions.Multiline)]
    private static partial Regex TextureEntry();

    [GeneratedRegex(@"\{fileID:\s*2100000,\s*guid:\s*([0-9a-fA-F]{32})")]
    private static partial Regex MaterialReference();

    /// <summary>The asset's GUID, and the texture importer's type (1 = normal map; -1 when there is no texture importer).</summary>
    public static (string? Guid, int TextureType) ReadMeta(string text)
    {
        var guid = MetaGuid().Match(text) is { Success: true } g ? g.Groups[1].Value.ToLowerInvariant() : null;
        int type = -1;
        if (TextureImporter().Match(text) is { Success: true } importer
            && TextureTypeLine().Match(text, importer.Index) is { Success: true } line
            && int.TryParse(line.Groups[1].Value, out var parsed))
            type = parsed;
        return (guid, type);
    }

    /// <summary>A material's name, shader GUID and the texture GUID of every property that has one; null when it is no material.</summary>
    public static UnityMaterial? ReadMaterial(string guid, string text)
    {
        var header = text.IndexOf("Material:", StringComparison.Ordinal);
        if (header < 0)
            return null;
        var name = NameLine().Match(text, header) is { Success: true } n ? n.Groups[1].Value : string.Empty;
        var shader = ShaderLine().Match(text, header) is { Success: true } s ? s.Groups[1].Value.ToLowerInvariant() : string.Empty;

        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match entry in TextureEntry().Matches(text, header))
        {
            var texture = entry.Groups[3].Value.ToLowerInvariant();
            if (entry.Groups[2].Value == "0" || texture.Length == 0 || texture == NullGuid)
                continue;
            textures.TryAdd(entry.Groups[1].Value, texture);
        }
        return new UnityMaterial(guid.ToLowerInvariant(), name, shader, textures);
    }

    /// <summary>The GUIDs of the materials a prefab references, each once, in the order they first appear.</summary>
    public static IReadOnlyList<string> ReadPrefabMaterials(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match match in MaterialReference().Matches(text))
        {
            var guid = match.Groups[1].Value.ToLowerInvariant();
            if (guid != NullGuid && seen.Add(guid))
                result.Add(guid);
        }
        return result;
    }
}
