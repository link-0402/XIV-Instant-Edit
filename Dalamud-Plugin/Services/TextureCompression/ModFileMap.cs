using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// Every file redirection a Penumbra mod can apply: its default files and each option's, or each
/// container's for groups that combine options, as Penumbra 1.7 keeps them in meta.json. Game paths
/// and files are compared without regard to case; files are paths inside the mod folder with forward
/// slashes. Dalamud-free.
/// </summary>
internal sealed class ModFileMap
{
    public const string Default = "default";

    /// <param name="Container"><see cref="Default"/>, or the option or container that applies the redirection.</param>
    public sealed record Mapping(string GamePath, string File, string Container);

    private readonly ILookup<string, Mapping> _byGamePath;
    private readonly ILookup<string, Mapping> _byFile;

    public ModFileMap(IEnumerable<Mapping> mappings)
    {
        Mappings = mappings.Select(m => m with { GamePath = GamePathKey(m.GamePath), File = FileKey(m.File) })
            .Where(m => m.GamePath.Length > 0 && m.File.Length > 0).Distinct().ToArray();
        _byGamePath = Mappings.ToLookup(m => m.GamePath, StringComparer.OrdinalIgnoreCase);
        _byFile = Mappings.ToLookup(m => m.File, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Mapping> Mappings { get; }

    /// <summary> The mod's files of one kind, such as ".mdl", each once. </summary>
    public IEnumerable<string> Files(string extension)
        => _byFile.Select(group => group.Key).Where(file => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary> Where the mod redirects a game path, in every option. </summary>
    public IEnumerable<Mapping> ForGamePath(string gamePath) => _byGamePath[GamePathKey(gamePath)];

    /// <summary> The game paths a file of the mod is used for, in every option. </summary>
    public IEnumerable<Mapping> ForFile(string file) => _byFile[FileKey(file)];

    public static string GamePathKey(string gamePath) => PathRules.NormalizeGamePath(gamePath).ToLowerInvariant();

    public static string FileKey(string file)
    {
        var normalized = file.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    /// <summary> The redirections in a mod's meta.json (Penumbra 1.7, file version 4). </summary>
    public static ModFileMap Read(string metaJson)
    {
        JsonObject meta;
        try
        {
            meta = JsonNode.Parse(metaJson) as JsonObject ?? throw new InvalidDataException("The mod's meta.json is not an object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The mod's meta.json can't be read.", e);
        }
        if (meta["FileVersion"]?.GetValueKind() != JsonValueKind.Number || meta["FileVersion"]!.GetValue<int>() != 4)
            throw new InvalidDataException("The mod's meta.json is not from Penumbra 1.7 or newer.");

        var mappings = new List<Mapping>();
        void AddFiles(JsonNode? container, string name)
        {
            if (container is not JsonObject value || value["Files"] is not JsonObject files)
                return;
            foreach (var (gamePath, file) in files)
                if (file?.GetValueKind() == JsonValueKind.String)
                    mappings.Add(new Mapping(gamePath, file.GetValue<string>(), name));
        }

        AddFiles(meta["DefaultData"], Default);
        if (meta["Groups"] is JsonArray groups)
            for (var g = 0; g < groups.Count; g++)
            {
                if (groups[g] is not JsonObject group)
                    continue;
                if (group["Options"] is JsonArray options)
                    for (var o = 0; o < options.Count; o++)
                        AddFiles(options[o], $"group {g} option {o}");
                if (group["Containers"] is JsonArray containers)
                    for (var c = 0; c < containers.Count; c++)
                        AddFiles(containers[c], $"group {g} container {c}");
            }
        return new ModFileMap(mappings);
    }
}
