using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InstantEdit.Studio.Services.Unity;

/// <summary>
/// The few things read from Unity's YAML files: a meta file's GUID, texture type and material
/// remap, a material's name, shader and textures, and the materials a prefab references. Only
/// that subset, by pattern, not a YAML parser: Unity writes these files in one fixed layout.
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

    // "  externalObjects:" and the lines that belong to it: deeper ones, and "- " entries at its own
    // depth. An empty remap is written "externalObjects: {}", which this leaves unmatched.
    [GeneratedRegex(@"^(?<indent>[ ]*)externalObjects:[ \t]*\r?\n(?<items>(?:\k<indent>(?:[ ]+|-[ ]).*(?:\n|\z)|[ \t]*\r?\n)*)", RegexOptions.Multiline)]
    private static partial Regex ExternalObjects();

    [GeneratedRegex(@"^[ ]*-[ ]", RegexOptions.Multiline)]
    private static partial Regex ItemStart();

    // An entry: "- first:" with "type: UnityEngine:Material", "assembly: UnityEngine.CoreModule" and
    // "name: Body" in any order, then "second: {fileID: 2100000, guid: <32 hex>, type: 2}", which
    // Unity may wrap after a comma.
    [GeneratedRegex(@"^[ \t]*type:[ \t]*UnityEngine:Material[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex MaterialTypeLine();

    [GeneratedRegex(@"^[ \t]*name:[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex RemapNameLine();

    [GeneratedRegex(@"second:[ \t]*\{[^}]*?guid:\s*([0-9a-fA-F]{32})")]
    private static partial Regex RemapTarget();

    [GeneratedRegex(@"\\(?:u([0-9a-fA-F]{4})|x([0-9a-fA-F]{2})|(.))")]
    private static partial Regex Escape();

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
        var name = NameLine().Match(text, header) is { Success: true } n ? Unquote(n.Groups[1].Value) : string.Empty;
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

    /// <summary>
    /// A model's material remap from its meta file (the importer's <c>externalObjects</c>): each
    /// material name of the model → the .mat GUID Unity uses for it. Entries of other types, and
    /// empty or built-in references, are left out; a name listed twice keeps the first.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadMaterialRemap(string text)
    {
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ExternalObjects().Match(text) is not { Success: true } section)
            return remap;
        var items = section.Groups["items"].Value;
        var starts = ItemStart().Matches(items);
        for (int i = 0; i < starts.Count; i++)
        {
            var item = items[(starts[i].Index + starts[i].Length)..(i + 1 < starts.Count ? starts[i + 1].Index : items.Length)];
            if (!MaterialTypeLine().IsMatch(item)
                || RemapNameLine().Match(item) is not { Success: true } name
                || RemapTarget().Match(item) is not { Success: true } target)
                continue;
            var model = Unquote(name.Groups[1].Value);
            var guid = target.Groups[1].Value.ToLowerInvariant();
            if (model.Length > 0 && guid != NullGuid)
                remap.TryAdd(model, guid);
        }
        return remap;
    }

    /// <summary>A YAML scalar's text: plain, 'single-quoted', or "double-quoted" with escapes (Unity writes names outside ASCII as \u escapes).</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return Escape().Replace(value[1..^1], e => e.Groups[3].Success
                ? e.Groups[3].Value
                : ((char)Convert.ToInt32(e.Groups[e.Groups[1].Success ? 1 : 2].Value, 16)).ToString());
        return value;
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
