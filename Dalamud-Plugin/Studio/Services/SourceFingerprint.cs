using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace InstantEdit.Studio.Services;

/// <summary>
/// A fingerprint of the local files the set-ups are made from — each one's size and timestamp, and
/// for a folder of variants the images in it — so the mod can be updated when one of them is saved
/// again, without anything having to be read. Cheap enough to take every couple of seconds.
/// </summary>
internal static class SourceFingerprint
{
    public static string Of(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(path).Append('|');
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var image in TextureSources.EnumerateImages(path))
                        Append(sb, new FileInfo(image));
                }
                else
                {
                    Append(sb, new FileInfo(path));
                }
            }
            catch (Exception)
            {
                sb.Append('?');   // unreadable now; whatever it turns into later differs
            }
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void Append(StringBuilder sb, FileInfo info)
        => sb.Append(info.Name).Append(':')
             .Append(info.Exists ? info.Length : -1).Append(':')
             .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append(';');
}
