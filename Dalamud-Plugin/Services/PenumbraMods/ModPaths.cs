using System.Text.RegularExpressions;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> Checks for the paths and names an export request and a Penumbra mod carry. </summary>
internal static class ModPaths
{
    internal static string? ValidateExportRequest(string? modName, string? gamePath, string? exportedFile)
    {
        try
        {
            if (!IsSafeModName(modName))
                return "Invalid mod name.";

            if (!IsSafeGamePath(gamePath))
                return "Invalid game path. Expected a safe relative .mdl path.";

            if (string.IsNullOrWhiteSpace(exportedFile) ||
                !string.Equals(Path.GetExtension(exportedFile), ".mdl", StringComparison.OrdinalIgnoreCase))
                return "Exported file must be a .mdl file.";

            var info = new FileInfo(exportedFile);
            if (!info.Exists || info.Length == 0)
                return "Exported .mdl file was not found or is empty.";

            using var file = new FileStream(exportedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = file.ReadByte();
            return null;
        }
        catch (Exception e)
        {
            return $"Exported .mdl file is not readable: {e.Message}";
        }
    }

    // Path.GetInvalidFileNameChars returns a new array every call.
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary> A safe relative .mdl game path. Allocation-free: table rows ask every frame. </summary>
    internal static bool IsSafeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 1024 ||
            gamePath.Contains('\0') || Path.IsPathRooted(gamePath) || gamePath.Contains('\\') ||
            !Path.GetExtension(gamePath.AsSpan()).Equals(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = gamePath.AsSpan();
        foreach (var range in path.Split('/'))
        {
            var segment = path[range];
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(InvalidFileNameChars) >= 0)
                return false;
        }

        return true;
    }

    internal static bool IsSafeGameResourcePath(string? gamePath, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 4096 || gamePath.Contains('\0') ||
            Path.IsPathRooted(gamePath) || gamePath.Contains('\\') ||
            !extensions.Any(extension => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return false;
        var invalid = Path.GetInvalidFileNameChars();
        return gamePath.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..") && segment.IndexOfAny(invalid) < 0);
    }

    internal static bool IsSafeLocalModelPath(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096 || filePath.Contains('\0') ||
                !Path.IsPathRooted(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(filePath), ".mdl", StringComparison.OrdinalIgnoreCase))
                return false;

            var fullPath = Path.GetFullPath(filePath);
            return filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment is not ("." or "..")) &&
                !string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSafeVariantName(string value)
        => PathRules.IsSafeVariantName(value);

    internal static bool IsSafeVariantGroupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
            return false;
        return value.All(c => !char.IsControl(c));
    }

    internal static bool IsSafeRelativeModPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') ||
            Path.IsPathRooted(path) || path.Contains('\\') ||
            !string.Equals(Path.GetExtension(path), ".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }
    internal static string Dx11TexturePath(string path, ushort flags)
        => PathRules.Dx11TexturePath(path, flags);

    internal static string ReadNullTerminated(byte[] strings, int offset)
        => PathRules.ReadNullTerminated(strings, offset);

    internal static string NormalizeGamePath(string value)
        => PathRules.NormalizeGamePath(value);

    internal static bool IsSafeRelativeModelPath(string? path)
        => path is not null && IsSafeRelativeModPath(path);

    internal static bool IsSafeRelativeResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') ||
            Path.IsPathRooted(path) || path.Contains('\\'))
            return false;
        var extension = Path.GetExtension(path);
        if (extension is not (".mdl" or ".mtrl" or ".tex") &&
            !extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    internal static bool IsSafeModResourceFile(string root, string file)
    {
        try
        {
            var fullPath = Path.GetFullPath(file);
            return IsPathWithin(fullPath, root) &&
                   (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0 &&
                   !HasReparsePointInPath(root, fullPath);
        }
        catch
        {
            return false;
        }
    }

    internal static string? NormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    internal static void AddCandidateRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePhysicalPath(path);
        if (normalized is not null && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            roots.Add(normalized);
    }

    internal static bool IsPathWithin(string path, string root)
        => PathRules.IsPathWithin(path, root);

    internal static bool IsSafeModName(string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName) || modName is "." or ".." ||
            Path.IsPathRooted(modName) || modName.Contains('/') || modName.Contains('\\') ||
            modName.Length > 128)
            return false;

        return modName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    internal static bool IsSafeNewModName(string? modName)
    {
        if (!IsSafeModName(modName) ||
            !string.Equals(modName, modName!.Trim(), StringComparison.Ordinal) ||
            modName.EndsWith(".", StringComparison.Ordinal) || modName.Any(char.IsControl))
            return false;
        var device = modName.Split('.', 2)[0];
        return !device.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
               !Regex.IsMatch(device, @"^(?:COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static bool TryNormalizeRelativeModPath(string? value, out string path)
    {
        path = (value ?? "").Replace('\\', '/');
        return IsSafeRelativeModPath(path);
    }

    internal static bool SameGamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static bool HasReparsePointInPath(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current.Length >= fullRoot.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return false;
    }
}
