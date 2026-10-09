using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// A sound or effect file brought in from disk: the game path the timeline names, and every file
/// to package under its game path (the file itself and, for an effect, what it needs that the
/// game lacks). <paramref name="Seconds"/> is a sound's length, 0 when unknown or for effects.
/// <paramref name="Beside"/> names, for a file that is an edited copy, the game path of the file it
/// copies: saved in place, the copy is mapped wherever that file is (such as a mod's toggle
/// option), so it comes and goes with it. <paramref name="Rebuild"/> makes such a copy the same way
/// from another file: when the mod maps the original to different files in different options, each
/// of those gets its own copy. A copy without one is mapped only where the original is the file it was made from.
/// </summary>
internal sealed record TimelineImport(string GamePath, ImmutableDictionary<string, byte[]> Files, float Seconds,
    ImmutableDictionary<string, string>? Beside = null, ImmutableDictionary<string, Func<byte[], byte[]>>? Rebuild = null);

/// <summary>
/// Turns files picked on disk into packaged timeline resources. Each gets a new game path under
/// instant_edit/ named after the file and its content hash, so it never replaces a vanilla file
/// or another import, and an edited file never reuses a path the game already cached.
/// </summary>
internal static class TimelineImports
{
    public const int MaxSlugLength = 40;
    public const int MaxFileSize = 64 * 1024 * 1024;
    // Bounds the search for an effect's files when it was picked in a large folder such as Downloads.
    private const int MaxSearchedFiles = 20000;
    private const int MaxListedMissing = 3;

    /// <summary>The file name as a path part: lowercase a-z, 0-9 and _, at most 40 characters.</summary>
    public static string Slug(string fileName)
    {
        var text = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant())
        {
            var keep = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (keep) text.Append(c);
            else if (text.Length > 0 && text[^1] != '_') text.Append('_');
            if (text.Length >= MaxSlugLength) break;
        }
        var slug = text.ToString().Trim('_');
        return slug.Length > 0 ? slug : "file";
    }

    /// <summary>The first 8 hex digits of the SHA-256 of <paramref name="bytes"/>, lowercase.</summary>
    public static string Hash8(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];

    public static string SoundPath(string fileName, byte[] scd) => $"sound/instant_edit/{Slug(fileName)}_{Hash8(scd)}.scd";

    public static string EffectPath(string fileName, byte[] avfx) => $"vfx/instant_edit/{Slug(fileName)}_{Hash8(avfx)}.avfx";

    public static bool IsScd(ReadOnlySpan<byte> bytes) => bytes.StartsWith("SEDBSSCF"u8);

    /// <summary>
    /// A sound for a C063 entry. An .scd is taken as it is (<paramref name="volume"/> does not apply);
    /// a .wav or .ogg goes through <paramref name="convert"/>, which encodes it at that volume and
    /// reports its length. <paramref name="seconds"/> reads an .scd's length when one is given.
    /// </summary>
    /// <param name="verify">Checks an .scd taken as it is, throwing <see cref="InvalidDataException"/> for one that doesn't parse.</param>
    public static TimelineImport Sound(string file, float volume, Func<string, float, (byte[] Scd, float Seconds)>? convert,
        Func<byte[], float?>? seconds = null, Action<byte[]>? verify = null)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        byte[] scd; float length;
        switch (extension)
        {
            case ".scd":
                scd = ReadLimited(file);
                if (!IsScd(scd)) throw new InvalidDataException("This is not an SCD sound file.");
                verify?.Invoke(scd);
                length = seconds?.Invoke(scd) is { } known && float.IsFinite(known) && known >= 0 ? known : 0;
                break;
            case ".wav" or ".ogg":
                if (convert == null) throw new InvalidDataException("Converting .wav and .ogg files is not available.");
                if (!float.IsFinite(volume) || volume < 0) throw new InvalidDataException("The volume is invalid.");
                if (!File.Exists(file)) throw new FileNotFoundException("The sound file is missing.", file);
                (scd, length) = convert(file, volume);
                if (scd is not { Length: > 0 } || !IsScd(scd)) throw new InvalidDataException("The sound could not be converted.");
                if (scd.Length > MaxFileSize) throw new InvalidDataException("The converted sound is too large.");
                if (!float.IsFinite(length) || length < 0) length = 0;
                break;
            default: throw new InvalidDataException("Pick a .scd, .wav or .ogg file.");
        }
        var gamePath = SoundPath(file, scd);
        RequireSafe(gamePath);
        return new TimelineImport(gamePath, ImmutableDictionary<string, byte[]>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add(gamePath, scd), length);
    }

    /// <summary>
    /// An effect for a C012 entry, with the textures and sounds it names. One the game has
    /// (<paramref name="exists"/>) needs nothing; any other must be on disk, packaged under the
    /// exact path the effect names: at that game path below the effect's folder or one above it
    /// (a mod's layout), else a same-named file beside the .avfx or in a folder below it.
    /// </summary>
    public static TimelineImport Effect(string file, Func<string, bool> exists)
    {
        if (!Path.GetExtension(file).Equals(".avfx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pick an .avfx file.");
        var avfx = ReadLimited(file);
        AnimationVfxPaths paths;
        try { paths = AnimationDependencies.ReadVfxPaths(avfx); }
        catch (InvalidDataException) { throw new InvalidDataException("This is not a readable effect file."); }
        var gamePath = EffectPath(file, avfx);
        RequireSafe(gamePath);
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        files.Add(gamePath, avfx);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? throw new InvalidDataException("The effect's folder is unavailable.");
        Dictionary<string, List<string>>? nearby = null;
        var missing = new List<string>();
        foreach (var dependency in paths.Textures.Concat(paths.Sounds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!AnimationDependencies.SafeGamePath(dependency))
                throw new InvalidDataException($"The effect names a path the game cannot load: {dependency}");
            if (files.ContainsKey(dependency) || exists(dependency)) continue;
            // Saving and previewing package only these kinds; refuse others now, not after the edit.
            if (Path.GetExtension(dependency).ToLowerInvariant() is not (".atex" or ".scd" or ".avfx" or ".tmb"))
                throw new InvalidDataException($"The effect needs {dependency}, which can't be added to a mod.");
            var found = AtGamePath(folder, dependency);
            if (found == null)
            {
                nearby ??= FilesBelow(folder);
                found = Pick(nearby, folder, dependency);
            }
            if (found != null) files.Add(dependency, ReadLimited(found));
            else missing.Add(Path.GetFileName(dependency));
        }
        if (missing.Count > 0)
            throw new InvalidDataException($"Missing next to the effect: {string.Join(", ", missing.Take(MaxListedMissing))}" +
                (missing.Count > MaxListedMissing ? $" and {missing.Count - MaxListedMissing} more." : "."));
        return new TimelineImport(gamePath, files.ToImmutable(), 0);
    }

    private static void RequireSafe(string gamePath)
    {
        if (!AnimationDependencies.SafeGamePath(gamePath)) throw new InvalidDataException("The file name cannot be used in a game path.");
    }

    private static byte[] ReadLimited(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("The file is missing.", file);
        if (info.Length > MaxFileSize) throw new InvalidDataException($"{info.Name} is too large.");
        return File.ReadAllBytes(file);
    }

    /// <summary>
    /// The file at <paramref name="gamePath"/> below the effect's folder or one above it. Mods keep
    /// files at their game paths, so an effect picked from mod/vfx/x/eff/ finds mod/vfx/x/texture/.
    /// </summary>
    private static string? AtGamePath(string folder, string gamePath)
    {
        var relative = gamePath.Replace('/', Path.DirectorySeparatorChar);
        var directory = new DirectoryInfo(folder);
        for (var depth = 0; directory != null && depth < 8; directory = directory.Parent, depth++)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The files in <paramref name="folder"/> and the folders below it, by file name.</summary>
    private static Dictionary<string, List<string>> FilesBelow(string folder)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            if (++count > MaxSearchedFiles) break;
            var name = Path.GetFileName(path);
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add(path);
        }
        return byName;
    }

    /// <summary>
    /// The file for <paramref name="gamePath"/>: of the same-named files, the one whose folders match
    /// most of the game path's last folders, so tex/a.atex wins over a stray a.atex elsewhere.
    /// </summary>
    private static string? Pick(Dictionary<string, List<string>> byName, string folder, string gamePath)
    {
        if (!byName.TryGetValue(Path.GetFileName(gamePath), out var candidates)) return null;
        var wanted = gamePath.Split('/');
        return candidates.OrderByDescending(path =>
            {
                var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var match = 0;
                while (match < parts.Length && match < wanted.Length &&
                       string.Equals(parts[^(match + 1)], wanted[^(match + 1)], StringComparison.OrdinalIgnoreCase)) match++;
                return match;
            })
            .ThenBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First();
    }
}
