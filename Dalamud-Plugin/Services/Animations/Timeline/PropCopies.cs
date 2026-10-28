using System.Collections.Immutable;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The files of a private prop copy. <paramref name="Aliases"/> maps each other material version folder
/// (<c>v0000</c>, <c>v0002</c>..<c>v0255</c>) to the <c>v0001</c> file it shares. <paramref name="Missing"/> lists the
/// private texture paths the materials name that neither the mod nor the game supplied.
/// </summary>
internal sealed record PropCopy(ImmutableDictionary<string, byte[]> Files, ImmutableDictionary<string, string> Aliases, ImmutableArray<string> Missing);

/// <summary>Turns a <see cref="TimelinePropSource"/> snapshot into the files of its private weapon body.</summary>
internal static class PropCopies
{
    private const string MaterialMarker = "/material/v0001/";

    /// <summary>
    /// Reads every file of <paramref name="source"/> and points each material's textures at the private texture
    /// paths. A <c>file:</c> source that is gone, or a model or material the game doesn't have, throws; a texture the
    /// game doesn't have is left out and listed in <see cref="PropCopy.Missing"/>.
    /// </summary>
    internal static PropCopy Build(TimelinePropSource source, Func<string, byte[]?> game)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        var aliases = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (path, from) in source.Files)
        {
            var texture = path.EndsWith(".tex", StringComparison.Ordinal);
            byte[]? bytes;
            if (from.StartsWith("file:", StringComparison.Ordinal))
                bytes = File.Exists(from[5..]) ? File.ReadAllBytes(from[5..]) : null;
            else
                bytes = game(from[5..]);
            if (bytes is null)
            {
                if (texture && from.StartsWith("game:", StringComparison.Ordinal))
                    continue;
                throw Gone(source);
            }
            var at = path.IndexOf(MaterialMarker, StringComparison.Ordinal);
            if (at < 0 || !path.EndsWith(".mtrl", StringComparison.Ordinal))
            {
                files[path] = bytes;
                continue;
            }
            files[path] = Rewritten(bytes, source.Textures);
            var folder = path[..(at + "/material/".Length)];
            var name = path[(at + MaterialMarker.Length)..];
            for (var version = 0; version <= 255; ++version)
                if (version != 1)
                    aliases[$"{folder}v{version:D4}/{name}"] = path;
        }
        var missing = source.Textures.Values.Where(path => !files.ContainsKey(path)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToImmutableArray();
        return new PropCopy(files.ToImmutable(), aliases.ToImmutable(), missing);
    }

    /// <summary>The private copies of every distinct prop source (by set and body) of <paramref name="doc"/>'s events, merged.</summary>
    internal static PropCopy Of(TimelineDocument doc, Func<string, byte[]?> game)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        var aliases = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var built = new HashSet<(ushort, ushort)>();
        foreach (var source in doc.Events.Select(ev => ev.Source).OfType<TimelinePropSource>())
        {
            if (!built.Add((source.Set, source.Body)))
                continue;
            var copy = Build(source, game);
            foreach (var (path, bytes) in copy.Files)
                files[path] = bytes;
            foreach (var (path, target) in copy.Aliases)
                aliases[path] = target;
            missing.UnionWith(copy.Missing);
        }
        return new PropCopy(files.ToImmutable(), aliases.ToImmutable(), [..missing]);
    }

    /// <summary>The files and each alias with its target's bytes, for a preview that has no alias to follow.</summary>
    internal static ImmutableDictionary<string, byte[]> Flatten(PropCopy copy)
    {
        var all = copy.Files.ToBuilder();
        foreach (var (alias, target) in copy.Aliases)
            all[alias] = copy.Files[target];
        return all.ToImmutable();
    }

    /// <summary>
    /// The material with the paths of the textures it lists replaced by their private paths. Only the entries this
    /// material lists are passed on: the rewrite refuses a replacement the material doesn't hold.
    /// </summary>
    private static byte[] Rewritten(byte[] material, IReadOnlyDictionary<string, string> textures)
    {
        var listed = MaterialPreviewBundleBuilder.ReadMaterialTextures(material)
            .Select(texture => texture.StoredPath.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var rewrites = textures.Where(pair => listed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return MashupPlanning.RewriteMaterialTexturePaths(material, rewrites);
    }

    private static InvalidOperationException Gone(TimelinePropSource source)
        => new($"The prop's model from {source.ModName} is gone. Pick it again.");
}
