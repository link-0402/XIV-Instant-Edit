using InstantEdit.Models;
using InstantEdit.Services.Previews;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;

namespace InstantEdit.Services.CharacterWeight;

/// <summary>
/// The default limits of the Mare-based sync plugins (Lightless, PlayerSync): a player above a
/// warning limit is reported, one above an auto-pause limit is paused if the viewer turned that on.
/// Both compare with "greater than", in MiB of 1024 × 1024 bytes and whole triangles.
/// </summary>
internal static class SyncThresholds
{
    public const long WarningVram = 375L * 1024 * 1024;
    public const long AutoPauseVram = 550L * 1024 * 1024;
    public const long WarningTriangles = 165_000;
    public const long AutoPauseTriangles = 250_000;
}

/// <summary> What the weight check reads from one texture: its first bytes, its length and a hash of the whole file. </summary>
internal sealed record WeightFileSample(byte[] Head, long Length, string Sha256);

/// <summary> One texture file the character renders, however many materials use it. </summary>
internal sealed record WeightTexture
{
    /// <summary> Identifies the resolved file: its full path, or its game path for game data. </summary>
    public required string Key { get; init; }
    public required PreviewSource Source { get; init; }
    /// <summary> Every path the game requests this file under; a preview maps them all. </summary>
    public required IReadOnlyList<string> GamePaths { get; init; }
    /// <summary> "g_SamplerNormal in mt_c0201e6100_top_a.mtrl" for each material that uses it. </summary>
    public required IReadOnlyList<string> Uses { get; init; }
    /// <summary> The slots of the models that use it ("Body", "Face"). </summary>
    public required IReadOnlyList<string> Slots { get; init; }
    public required TextureRole Role { get; init; }
    /// <summary> Every use is an index map, whose shaders read only red and green. </summary>
    public required bool IndexOnly { get; init; }
    public TexInfo? Info { get; init; }
    public long FileLength { get; init; }
    /// <summary> Video memory from the header, including the mip chain. </summary>
    public long Vram { get; init; }
    /// <summary> The file is in a preview mod (of this or another Quick Action). </summary>
    public bool InPreview { get; init; }
    public string Error { get; init; } = string.Empty;

    /// <summary> Sync plugins count modded files only; game files are never sent. </summary>
    public bool CountsForSync => Source.State == ResourceSourceState.LoadedMod && Error.Length == 0;
    public bool Uncompressed => Info is { } info && !info.IsBlockCompressed;
    public bool Oversized => Info is { } info && Math.Max(info.Width, info.Height) > ShrinkRules.SizeCap;
    public bool NoMips => Info is { Mips: <= 1 } info && Math.Max(info.Width, info.Height) > 1;
    public string Label => Source.IsModFile ? $"{Source.ModName}: {Source.RelativePath}" : Source.GamePath;
    public string FileName => Path.GetFileName(Source.IsModFile ? Source.RelativePath : Source.GamePath);
}

/// <summary> One model file the character renders. </summary>
internal sealed record WeightModel
{
    public required string Key { get; init; }
    public required PreviewSource Source { get; init; }
    public required IReadOnlyList<string> GamePaths { get; init; }
    public required IReadOnlyList<string> Slots { get; init; }
    /// <summary> Triangles of the most detailed level of detail, which is what the game draws up close and sync plugins count. </summary>
    public int Triangles { get; init; }
    public long FileLength { get; init; }
    public bool InPreview { get; init; }
    public string Error { get; init; } = string.Empty;

    public bool CountsForSync => Source.State == ResourceSourceState.LoadedMod && Error.Length == 0;
    public string Label => Source.IsModFile ? $"{Source.ModName}: {Source.RelativePath}" : Source.GamePath;
    public string FileName => Path.GetFileName(Source.IsModFile ? Source.RelativePath : Source.GamePath);
}

/// <summary> A measured character: its textures ranked by memory and its models ranked by triangles, with totals. </summary>
internal sealed record CharacterWeightReport
{
    public required IReadOnlyList<WeightTexture> Textures { get; init; }
    public required IReadOnlyList<WeightModel> Models { get; init; }

    /// <summary> Texture memory as sync plugins count it: the file size of each distinct modded texture. </summary>
    public long SyncVram { get; init; }
    /// <summary> Triangles as sync plugins count them: each distinct modded model once. </summary>
    public long SyncTriangles { get; init; }
    /// <summary> Video memory of every texture the character renders, game files included. </summary>
    public long AllVram { get; init; }
    public long GameVram { get; init; }
    public long AllTriangles { get; init; }
    public long GameTriangles { get; init; }
}

/// <summary>
/// Measures a character from its On Screen resource tree: every texture and model it renders,
/// each resolved file once however many models or materials use it. Totals follow the sync
/// plugins: only modded files count, each distinct file content once, textures by file size and
/// models by the triangles of their first level of detail. Dalamud-free; reading is delegated.
/// </summary>
internal static class CharacterWeightCapture
{
    public const long MaxModelBytes = 256L * 1024 * 1024;

    /// <param name="sampleTexture">Reads a texture: a full path, or a game path for game data. Null when unavailable.</param>
    /// <param name="readModel">Reads a whole model the same way.</param>
    /// <param name="isPreviewMod">Whether a mod folder holds a preview mod.</param>
    /// <param name="progress">Called with the files read so far and the total, before each file.</param>
    public static CharacterWeightReport Capture(IReadOnlyList<ResourceNode> roots, Func<string, WeightFileSample?> sampleTexture,
        Func<string, byte[]?> readModel, Func<string, bool> isPreviewMod, CancellationToken token = default, Action<int, int>? progress = null)
    {
        var textures = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            Visit(root, root.SlotLabel, null, textures, models);

        var total = textures.Count + models.Count;
        var textureRows = new List<WeightTexture>();
        foreach (var group in textures.Values)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(textureRows.Count, total);
            textureRows.Add(Texture(group, sampleTexture, isPreviewMod));
        }
        var modelRows = new List<WeightModel>();
        foreach (var group in models.Values)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(textureRows.Count + modelRows.Count, total);
            modelRows.Add(Model(group, readModel, isPreviewMod));
        }

        return new CharacterWeightReport
        {
            Textures = textureRows.OrderByDescending(t => t.Vram).ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            Models = modelRows.OrderByDescending(m => m.Triangles).ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            SyncVram = SyncVram(textureRows.Where(t => t.CountsForSync).Select(t => (t.Source.Sha256, t.FileLength))),
            SyncTriangles = modelRows.Where(m => m.CountsForSync)
                .GroupBy(m => m.Source.Sha256, StringComparer.OrdinalIgnoreCase).Sum(g => (long)g.First().Triangles),
            AllVram = textureRows.Sum(t => t.Vram),
            GameVram = textureRows.Where(t => t.Source.State == ResourceSourceState.GameData).Sum(t => t.Vram),
            AllTriangles = modelRows.Sum(m => (long)m.Triangles),
            GameTriangles = modelRows.Where(m => m.Source.State == ResourceSourceState.GameData).Sum(m => (long)m.Triangles),
        };
    }

    /// <summary> Sums file sizes the way sync plugins do: each distinct content once. </summary>
    public static long SyncVram(IEnumerable<(string Content, long Length)> files)
        => files.GroupBy(f => f.Content, StringComparer.OrdinalIgnoreCase).Sum(g => g.First().Length);

    private sealed class Group(ResourceNode first, string key)
    {
        public ResourceNode First { get; } = first;
        public string Key { get; } = key;
        public List<string> GamePaths { get; } = [];
        public List<string> Uses { get; } = [];
        public List<string> Slots { get; } = [];
        public List<TextureRole> Roles { get; } = [];
    }

    private static void Visit(ResourceNode node, string slot, ResourceNode? material, Dictionary<string, Group> textures, Dictionary<string, Group> models)
    {
        var isTexture = IsKind(node, ".tex");
        var isModel = IsKind(node, ".mdl");
        if ((isTexture || isModel) && node.SourceState is ResourceSourceState.LoadedMod or ResourceSourceState.GameData &&
            node.ActualPath.Length > 0 && KeyOf(node) is { } key)
        {
            var groups = isTexture ? textures : models;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new Group(node, key);
            AddOnce(group.GamePaths, PathRules.NormalizeGamePath(node.GamePath));
            AddOnce(group.Slots, slot);
            if (isTexture)
            {
                var role = TextureRoleClassifier.Classify(ResourceKinds.Texture, node.Name, node.GamePath, node.ActualPath);
                group.Roles.Add(role);
                // Penumbra names a material's textures after their shader samplers (g_SamplerNormal).
                var use = node.Name.Length > 0 ? node.Name : TextureRoleClassifier.Label(role) + " map";
                AddOnce(group.Uses, material is null ? use : $"{use} in {Path.GetFileName(material.GamePath)}");
            }
        }
        var childMaterial = IsKind(node, ".mtrl") ? node : material;
        foreach (var child in node.Children)
            Visit(child, slot, childMaterial, textures, models);
    }

    private static bool IsKind(ResourceNode node, string extension)
        => node.GamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static void AddOnce(List<string> list, string value)
    {
        if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            list.Add(value);
    }

    private static string? KeyOf(ResourceNode node)
    {
        if (node.SourceState == ResourceSourceState.GameData)
            return "game:" + PathRules.NormalizeGamePath(node.ActualPath);
        try
        {
            return Path.IsPathRooted(node.ActualPath) ? "file:" + Path.GetFullPath(node.ActualPath) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static WeightTexture Texture(Group group, Func<string, WeightFileSample?> sample, Func<string, bool> isPreviewMod)
    {
        var node = group.First;
        var file = sample(node.ActualPath);
        var info = file is null ? null : TextureCost.Read(file.Head);
        var error = file is null ? "The file couldn't be read." : info is null ? "The file isn't a valid texture." : string.Empty;
        var role = group.Roles.Count > 0 && group.Roles.All(r => r == group.Roles[0]) ? group.Roles[0] : TextureRole.Other;
        return new WeightTexture
        {
            Key = group.Key,
            Source = PreviewSource.Of(node, file?.Sha256 ?? string.Empty),
            GamePaths = group.GamePaths,
            Uses = group.Uses,
            Slots = group.Slots,
            Role = role,
            IndexOnly = group.Roles.Count > 0 && group.Roles.All(r => r == TextureRole.Index),
            Info = info,
            FileLength = file?.Length ?? 0,
            Vram = info?.Vram ?? Math.Max(0, (file?.Length ?? 0) - TextureCost.HeaderSize),
            InPreview = node.SourceState == ResourceSourceState.LoadedMod && isPreviewMod(node.SourceModDirectory ?? string.Empty),
            Error = error,
        };
    }

    private static WeightModel Model(Group group, Func<string, byte[]?> read, Func<string, bool> isPreviewMod)
    {
        var node = group.First;
        var bytes = read(node.ActualPath);
        var info = bytes is null ? null : ModelInfoReader.Read(bytes);
        return new WeightModel
        {
            Key = group.Key,
            Source = PreviewSource.Of(node, bytes is null ? string.Empty : PreviewSource.Hash(bytes)),
            GamePaths = group.GamePaths,
            Slots = group.Slots,
            Triangles = info?.Lod0Triangles ?? 0,
            FileLength = bytes?.LongLength ?? 0,
            InPreview = node.SourceState == ResourceSourceState.LoadedMod && isPreviewMod(node.SourceModDirectory ?? string.Empty),
            Error = bytes is null ? "The file couldn't be read." : info is { Partial: true, Lod0Indices: 0 } ? info.Note ?? "The model couldn't be read." : string.Empty,
        };
    }
}
