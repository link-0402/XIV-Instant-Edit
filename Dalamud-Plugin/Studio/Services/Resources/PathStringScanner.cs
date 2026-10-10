using System;
using System.Collections.Generic;
using System.Text;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>
/// Finds the game paths a binary game file refers to by name. Timelines (.tmb), animations (.pap,
/// which embed their timelines), effects (.avfx) and skeleton files (.sklb, .phyb) carry the paths
/// of what they load — effects, effect textures, sounds, other timelines — as plain null-terminated
/// ASCII, so a byte scan for runs that look like game paths finds them without parsing any of
/// these formats.
///
/// A run counts when it starts at a known top-level game folder and ends in a known extension; the
/// rest of a binary file is too varied to produce many of those by chance, and whatever slips
/// through only becomes a link if it names a file the mod or the game actually has.
/// </summary>
internal static class PathStringScanner
{
    /// <summary>Files larger than this are not scanned.</summary>
    public const int MaxBytes = 48 * 1024 * 1024;

    private const int MinLength = 10;
    private const int MaxLength = 255;

    private static readonly byte[][] Roots =
    {
        Encoding.ASCII.GetBytes("chara/"),
        Encoding.ASCII.GetBytes("bgcommon/"),
        Encoding.ASCII.GetBytes("bg/"),
        Encoding.ASCII.GetBytes("vfx/"),
        Encoding.ASCII.GetBytes("sound/"),
        Encoding.ASCII.GetBytes("ui/"),
        Encoding.ASCII.GetBytes("common/"),
        Encoding.ASCII.GetBytes("music/"),
        Encoding.ASCII.GetBytes("shader/"),
    };

    private static readonly string[] Extensions =
    {
        ".avfx", ".atex", ".scd", ".tmb", ".pap", ".sklb", ".skp", ".phyb", ".eid", ".mtrl", ".tex", ".mdl", ".shpk", ".uld",
    };

    /// <summary>The distinct game paths in <paramref name="data"/>, lower case, in the order they first appear.</summary>
    public static IReadOnlyList<string> Scan(ReadOnlySpan<byte> data)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (data.Length > MaxBytes)
            return found;

        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && IsPathByte(data[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                Consider(data[start..i], found, seen);
                start = -1;
            }
        }
        return found;
    }

    private static bool IsPathByte(byte b)
        => b is >= (byte)'a' and <= (byte)'z'
        or >= (byte)'A' and <= (byte)'Z'
        or >= (byte)'0' and <= (byte)'9'
        or (byte)'_' or (byte)'-' or (byte)'.' or (byte)'/';

    private static void Consider(ReadOnlySpan<byte> run, List<string> found, HashSet<string> seen)
    {
        if (run.Length < MinLength)
            return;

        // The run may have picked up bytes in front of the path; it starts at the first known root
        // that begins a segment.
        int begin = -1;
        for (int i = 0; i < run.Length && begin < 0; i++)
        {
            if (i > 0 && run[i - 1] != (byte)'/' && IsLetterOrDigit(run[i - 1]))
                continue;
            foreach (var root in Roots)
            {
                if (StartsWithIgnoreCase(run[i..], root))
                {
                    begin = i;
                    break;
                }
            }
        }
        if (begin < 0)
            return;

        var path = Encoding.ASCII.GetString(run[begin..]).ToLowerInvariant();
        if (path.Length < MinLength || path.Length > MaxLength || path.Contains("//", StringComparison.Ordinal))
            return;

        // It must end in a known extension; anything glued on after it (rare: the next field
        // starting with a letter) is cut off at the extension.
        foreach (var ext in Extensions)
        {
            int at = path.LastIndexOf(ext, StringComparison.Ordinal);
            if (at <= 0)
                continue;
            int end = at + ext.Length;
            if (end < path.Length && IsLetterOrDigit((byte)path[end]))
                continue;
            var candidate = path[..end];
            if (candidate.EndsWith("/" + ext, StringComparison.Ordinal))
                continue;   // no file name, just an extension
            if (seen.Add(candidate))
                found.Add(candidate);
            return;
        }
    }

    private static bool IsLetterOrDigit(byte b)
        => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> run, byte[] prefix)
    {
        if (run.Length < prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte a = run[i];
            if (a is >= (byte)'A' and <= (byte)'Z')
                a = (byte)(a + 32);
            if (a != prefix[i])
                return false;
        }
        return true;
    }
}
