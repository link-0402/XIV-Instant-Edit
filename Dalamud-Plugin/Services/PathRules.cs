using System.Text;

namespace InstantEdit.Services;

/// <summary>Shared normalization and containment rules for plugin-controlled paths.</summary>
internal static class PathRules
{
    /// <summary>The name with every em-dash made a plain hyphen: Penumbra refuses mod names with one.</summary>
    public static string PlainDashes(string name) => name.Replace('—', '-');

    public static bool SamePhysicalPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
            !Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        return normalized.Length is > 0 and <= 4096 &&
               !normalized.Contains('\0') &&
               !Path.IsPathRooted(normalized) &&
               normalized.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    public static bool IsSafeVariantName(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && value is not ("." or "..") &&
           !value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !value.Contains('/') && !value.Contains('\\') &&
           value.All(c => !char.IsControl(c)) &&
           !IsReservedDeviceName(value);

    /// <summary>
    /// True for a name Windows reads as a device whatever follows it: CON, PRN, AUX, NUL and COM1-9 or
    /// LPT1-9, with or without an extension ("nul", "Aux.v2"). A file of that name is never created; the
    /// write goes to the device and reports success.
    /// </summary>
    public static bool IsReservedDeviceName(string name)
    {
        var device = name.Split('.', 2)[0].TrimEnd(' ');
        return device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (device.Length == 4 && device[3] is >= '1' and <= '9' &&
                (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// True when a path starts with two directory separators of either kind. Windows treats / and \ alike,
    /// so "//host/share" and "/\host\share" are the network path \\host\share too; a test for "\\"
    /// alone lets them through, and the next File.Exists would contact the host.
    /// </summary>
    public static bool IsNetworkPath(string path)
        => path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    public static string NormalizeGamePath(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

    public static string Dx11TexturePath(string path, ushort flags)
    {
        var normalized = NormalizeGamePath(path);
        if ((flags & 0x8000) == 0)
            return normalized;
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? "--" + normalized : normalized[..(separator + 1)] + "--" + normalized[(separator + 1)..];
    }

    public static string ReadNullTerminated(byte[] strings, int offset)
    {
        if (offset < 0 || offset >= strings.Length)
            return string.Empty;
        var end = Array.IndexOf(strings, (byte)0, offset);
        if (end < 0)
            end = strings.Length;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }
}
