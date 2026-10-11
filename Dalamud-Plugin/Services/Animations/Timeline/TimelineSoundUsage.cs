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
    /// </summary>
    public static IReadOnlyList<string> OtherUsers(string modRoot, string gamePath, string? excludeResolved)
    {
        var needle = Encoding.ASCII.GetBytes(gamePath.ToLowerInvariant());
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var users = new List<string>();
        foreach (var file in Directory.EnumerateFiles(modRoot, "*", options))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".pap" or ".tmb" or ".avfx") ||
                excludeResolved != null && PathRules.SamePhysicalPath(file, excludeResolved))
                continue;
            var info = new FileInfo(file);
            if (info.Length > AnimationPap.MaxFileSize)
                continue;
            if (Names(File.ReadAllBytes(file), needle))
                users.Add(Path.GetRelativePath(modRoot, file).Replace('\\', '/'));
        }
        return users;
    }

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
