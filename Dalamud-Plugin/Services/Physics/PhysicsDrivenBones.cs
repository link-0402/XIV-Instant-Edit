using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.PenumbraMods;
using Penumbra.Api.Helpers;

namespace InstantEdit.Services.Physics;

/// <summary>
/// The bones the game's physics moves on a character: every bone a chain of its loaded <c>.phyb</c> files names. A spring layer
/// leaves them out of what it adds, as the game would move them again.
/// </summary>
internal static class PhysicsDrivenBones
{
    /// <summary>Every bone a node of a chain of <paramref name="files"/> names, nodes without a bone left out.</summary>
    public static ImmutableHashSet<string> From(IEnumerable<PhybFile> files) =>
        files.SelectMany(file => file.Simulators).SelectMany(simulator => simulator.Chains).SelectMany(chain => chain.Nodes)
            .Select(node => node.Bone.Text).Where(bone => bone.Length > 0).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The physics bones of the character at object index <paramref name="actor"/>: the <c>.phyb</c> files Penumbra's resource
    /// tree lists for it, read as its collection loads them. Null when they can't be known (no Penumbra, no tree, a file that
    /// can't be read); this never throws but for cancellation. The tree is asked for on the framework thread, the files read off it.
    /// </summary>
    public static async Task<ImmutableHashSet<string>?> ReadAsync(PenumbraIpc penumbra, IDataManager data, IFramework framework, IPluginLog log,
        ushort actor, CancellationToken token)
    {
        try
        {
            var trees = await framework.RunOnFrameworkThread(() => penumbra.GetResourceTrees([actor], withUiData: false)).ConfigureAwait(false);
            if (trees is not [{ } tree])
                return null;
            var paths = Walk(tree.Nodes)
                .Where(node => (node.GamePath ?? node.ActualPath)?.EndsWith(".phyb", StringComparison.OrdinalIgnoreCase) == true)
                .Select(node => (Game: node.GamePath ?? "", Actual: string.IsNullOrEmpty(node.ActualPath) ? node.GamePath ?? "" : node.ActualPath))
                .Where(path => path.Actual.Length > 0).Distinct().ToArray();
            var files = new List<PhybFile>();
            foreach (var (game, actual) in paths)
            {
                token.ThrowIfCancellationRequested();
                var bytes = await Task.Run(() => Read(data, actual), token).ConfigureAwait(false);
                if (bytes == null)
                {
                    log.Debug($"The physics file {game} could not be read for spring layers.");
                    return null;
                }
                files.Add(PhybFile.Parse(bytes));
            }
            return From(files);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            log.Debug(e, "The character's physics bones could not be read for spring layers.");
            return null;
        }
    }

    /// <summary>A loaded file's bytes: a mod's file on disk, else the game's own; null when it isn't there or is too large.</summary>
    private static byte[]? Read(IDataManager data, string actual)
    {
        if (Path.IsPathFullyQualified(actual))
        {
            TextureFiles.EnsureLocalPath(actual);
            var info = new FileInfo(actual);
            return info.Exists && info.Length is > 0 and <= PhybFile.MaxFileBytes ? File.ReadAllBytes(info.FullName) : null;
        }
        return AnimationDependencies.SafeGamePath(actual) ? data.GetFile(actual)?.Data : null;
    }

    private static IEnumerable<ResourceNodeDto> Walk(List<ResourceNodeDto>? nodes)
    {
        foreach (var node in nodes ?? [])
        {
            yield return node;
            foreach (var child in Walk(node.Children))
                yield return child;
        }
    }
}
