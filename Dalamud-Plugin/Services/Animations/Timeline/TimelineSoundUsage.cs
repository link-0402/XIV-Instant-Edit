using System.Collections.Immutable;
using System.Text;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>Who else in a mod plays a sound, and the checks before a save writes over it. Dalamud-free.</summary>
internal static class TimelineSoundUsage
{
    /// <summary>
    /// The mod's animations and effects (.pap, .tmb, .avfx files up to <see cref="AnimationPap.MaxFileSize"/>)
    /// other than <paramref name="excludeResolved"/> whose bytes name <paramref name="gamePath"/>, in
    /// any case: changing that sound in place changes them too. Paths relative to the mod, with "/".
    /// When <paramref name="excludeResolved"/> is a PAP and <paramref name="excludeEntry"/> its edited
    /// entry, each other entry whose timeline names the sound counts too, as "file.pap entry n".
    /// </summary>
    public static IReadOnlyList<string> OtherUsers(string modRoot, string gamePath, string? excludeResolved, int excludeEntry = -1)
    {
        var needle = Encoding.ASCII.GetBytes(gamePath.ToLowerInvariant());
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var users = new List<string>();
        foreach (var file in Directory.EnumerateFiles(modRoot, "*", options))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".pap" or ".tmb" or ".avfx"))
                continue;
            var info = new FileInfo(file);
            if (info.Length > AnimationPap.MaxFileSize)
                continue;
            var relative = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            if (excludeResolved != null && PathRules.SamePhysicalPath(file, excludeResolved))
            {
                // The edited clip's own PAP: its other timelines play the same file.
                if (extension == ".pap" && excludeEntry >= 0)
                    users.AddRange(OtherEntries(new AnimationPap(File.ReadAllBytes(file)), excludeEntry, needle).Select(i => $"{relative} entry {i}"));
                continue;
            }
            if (Names(File.ReadAllBytes(file), needle))
                users.Add(relative);
        }
        return users;
    }

    /// <summary>The entries of <paramref name="pap"/> other than <paramref name="exclude"/> whose timeline names <paramref name="lower"/>.</summary>
    private static IEnumerable<int> OtherEntries(AnimationPap pap, int exclude, byte[] lower) =>
        Enumerable.Range(0, pap.Entries.Length).Where(i => i != exclude && Names(pap.TimelineBytes(i), lower)).ToList();

    /// <summary>
    /// Of <paramref name="sounds"/> (game path, the file it resolves to), the game paths whose file
    /// another of them resolves to as well. Writing one physical file over twice in one save can't
    /// work, so those go into copies.
    /// </summary>
    public static IReadOnlySet<string> SharedFiles(IReadOnlyList<(string GamePath, string Resolved)> sounds) =>
        sounds.Where(s => sounds.Any(o => !string.Equals(o.GamePath, s.GamePath, StringComparison.OrdinalIgnoreCase) &&
                                          PathRules.SamePhysicalPath(o.Resolved, s.Resolved)))
            .Select(s => s.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="outputs"/> with the mod's sound files a save writes over in place. Each must be
    /// among <paramref name="resources"/> (read for this save), still have the bytes the edit was
    /// made on, and belong to <paramref name="edited"/>'s mod.
    /// </summary>
    public static ImmutableDictionary<string, byte[]> ReplacedOutputs(ImmutableDictionary<string, byte[]> outputs, IReadOnlyList<AnimationResource> resources,
        AnimationResource edited, IReadOnlyDictionary<string, TimelineSoundReplacement> replaced)
    {
        foreach (var (path, replacement) in replaced)
        {
            var file = Path.GetFileName(path);
            var resource = resources.FirstOrDefault(r => string.Equals(r.GamePath, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{file} wasn't read for this save.");
            if (resource.Hash != replacement.BaseHash)
                throw new IOException($"{file} changed while saving. Open the timeline again.");
            if (resource.ModDirectory == null || resource.ModDirectory != edited.ModDirectory)
                throw new IOException($"{file} belongs to another mod. Save it as a copy.");
            outputs = outputs.SetItem(resource.GamePath, replacement.Bytes);
        }
        return outputs;
    }

    /// <summary>
    /// Refuses a save whose sound copy of <paramref name="gamePath"/> was made from bytes
    /// (<paramref name="baseHash"/>) the collection no longer gives (<paramref name="nowHash"/>):
    /// the copy would drop the newer sound.
    /// </summary>
    public static void CheckCopied(string gamePath, string baseHash, string nowHash)
    {
        if (!string.Equals(baseHash, nowHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{Path.GetFileName(gamePath)} changed while saving. Open the timeline again.");
    }

    /// <summary>Whether <paramref name="bytes"/> contain <paramref name="lower"/> (lowercase ASCII) in any case.</summary>
    private static bool Names(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> lower)
    {
        if (lower.IsEmpty)
            return false;
        var first = lower[0];
        var upper = (byte)char.ToUpperInvariant((char)first);
        for (var at = bytes.IndexOfAny(first, upper); at >= 0 && at <= bytes.Length - lower.Length;)
        {
            var match = true;
            for (var i = 1; i < lower.Length && match; i++)
                match = (byte)char.ToLowerInvariant((char)bytes[at + i]) == lower[i];
            if (match)
                return true;
            var next = bytes[(at + 1)..].IndexOfAny(first, upper);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return false;
    }
}
