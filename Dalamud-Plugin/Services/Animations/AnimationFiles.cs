using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>A PAP file offered for export, such as a mod's file with the game path it replaces.</summary>
internal sealed record AnimationFileSource(string GamePath, string FilePath, string DisplayName, string? ModName = null,
    string? ModDirectory = null);

/// <summary>One clip inside a PAP file. Facial clips animate the face skeleton rather than the body.</summary>
internal sealed record AnimationFileClip(AnimationFileSource Source, string Name, int BindingIndex, float Duration, bool Face);

/// <summary>What a PAP file holds: its clips and the race its header names.</summary>
internal sealed record AnimationFileInfo(AnimationFileSource Source, string Hash, string? Model, ImmutableArray<AnimationFileClip> Clips);

/// <summary>
/// Sends PAP files that no character is playing: reads the file, resolves the skeleton each clip
/// was made for against the local player's collection and skeleton library, and samples it.
/// </summary>
internal sealed class AnimationFiles(IFramework framework, IObjectTable objects, PenumbraService penumbra,
    AnimationResources resources, AnimationSkeletonIndex skeletons)
{
    private sealed record PlayerContext(Guid Collection, string[] Loaded, Dictionary<string, HashSet<string>>? PathMap);

    public async Task<AnimationFileInfo> InspectAsync(AnimationFileSource source, CancellationToken token)
    {
        var bytes = await ReadAsync(source, token);
        var pap = new AnimationPap(bytes);
        var durations = await framework.RunOnTick(() =>
        {
            token.ThrowIfCancellationRequested();
            return AnimationRuntime.InspectPapDurations(bytes);
        }, delayTicks: 1);
        var clips = pap.Entries
            .Select(e => new AnimationFileClip(source, e.Name, e.Binding, durations.GetValueOrDefault(e.Binding), e.Face != 0))
            .ToImmutableArray();
        return new AnimationFileInfo(source, AnimationPap.Hash(bytes), ModelOf(pap), clips);
    }

    /// <summary>Finds the skeleton a clip was made for, as the Animations tab does for animations it detected.</summary>
    public async Task<SkeletonResolution> ResolveAsync(AnimationFileClip clip, CancellationToken token)
    {
        var bytes = await ReadAsync(clip.Source, token);
        var player = await PlayerAsync(token);
        return await skeletons.ResolveAsync(player.Collection, Clip(clip, bytes, player), bytes, player.Loaded, player.PathMap, null, token);
    }

    public async Task<AnimationTake> SampleAsync(AnimationFileClip clip, SkeletonCandidate skeleton, Action<string> status, CancellationToken token)
    {
        var bytes = await ReadAsync(clip.Source, token);
        var player = await PlayerAsync(token);
        var (_, sklb) = await resources.ReadSkeletonAsync(player.Collection, skeleton.Source, token);
        var facts = new Dictionary<string, string>
        {
            ["gamePath"] = clip.Source.GamePath,
            ["clip"] = clip.Name,
            ["skeleton"] = skeleton.Source.Resource.GamePath,
            ["mod"] = clip.Source.ModName ?? clip.Source.ModDirectory ?? "",
        };
        return await AnimationClipExport.SampleAsync(framework, bytes, sklb, Clip(clip, bytes, player), skeleton,
            ExportName(clip), facts, status, token);
    }

    /// <summary>The Blender action name of a file clip: the file's name, and the clip's when the file holds several.</summary>
    public static string ExportName(AnimationFileClip clip)
    {
        var file = Path.GetFileNameWithoutExtension(clip.Source.GamePath);
        return string.Equals(file, clip.Name, StringComparison.OrdinalIgnoreCase) || file.Length == 0 ? clip.Name : $"{file} ({clip.Name})";
    }

    private static string? ModelOf(AnimationPap pap) => pap.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(pap.ModelId) : null;

    /// <summary>
    /// The clip as the skeleton resolver sees it. Body clips resolve from the PAP's race; facial
    /// clips need the race's face skeleton, preferring the player's own face when the race matches.
    /// </summary>
    private static AnimationClip Clip(AnimationFileClip clip, byte[] bytes, PlayerContext player)
    {
        var pap = new AnimationPap(bytes);
        var model = ModelOf(pap) ?? AnimationSkeletonIndex.ModelFromPath(clip.Source.GamePath);
        var identity = AnimationSkeletonIndex.SourceIdentity([clip.Source.GamePath], ModelOf(pap));
        var skeletonPath = "";
        if (clip.Face && model != null)
            skeletonPath = player.Loaded.FirstOrDefault(p => p.StartsWith($"chara/human/{model}/skeleton/face/", StringComparison.Ordinal) &&
                                                               p.EndsWith(".sklb", StringComparison.Ordinal))
                           ?? $"chara/human/{model}/skeleton/face/f0001/skl_{model}f0001.sklb";
        var loop = clip.Name.EndsWith("lp", StringComparison.OrdinalIgnoreCase) ||
                   clip.Source.GamePath.Contains("loop", StringComparison.OrdinalIgnoreCase);
        return new AnimationClip(clip.Source.GamePath, clip.Name, clip.BindingIndex, clip.Face ? 1 : 0, 0, skeletonPath,
            SourceContext: $"file:{AnimationPap.Hash(bytes)}", SourceIdentity: identity, Duration: clip.Duration, IsLoop: loop);
    }

    private async Task<PlayerContext> PlayerAsync(CancellationToken token)
    {
        var index = await framework.RunOnFrameworkThread(() => objects.LocalPlayer?.ObjectIndex ?? 0);
        var collection = await penumbra.GetCollectionTargetAsync(index)
            ?? throw new InvalidOperationException("Penumbra's collection for your character is unavailable. Is Penumbra running?");
        token.ThrowIfCancellationRequested();
        // The player's loaded skeletons rank the skeleton mods they actually use first.
        var paths = await penumbra.GetResourcePathsAsync(index);
        token.ThrowIfCancellationRequested();
        var loaded = paths?.Values.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var pathMap = paths?.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        return new PlayerContext(collection.Id, loaded, pathMap);
    }

    private static async Task<byte[]> ReadAsync(AnimationFileSource source, CancellationToken token)
    {
        if (!source.FilePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(source.FilePath))
            throw new InvalidDataException("Only .pap files can be sent as animations.");
        TextureFiles.EnsureLocalPath(source.FilePath);
        await using var stream = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("The animation file exceeds 256 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}
