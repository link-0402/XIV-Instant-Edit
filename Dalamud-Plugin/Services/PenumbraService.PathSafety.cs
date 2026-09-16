using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
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

    internal static bool IsSafeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 1024 ||
            gamePath.Contains('\0') || Path.IsPathRooted(gamePath) || gamePath.Contains('\\') ||
            !string.Equals(Path.GetExtension(gamePath), ".mdl", StringComparison.OrdinalIgnoreCase))
            return false;

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in gamePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(invalid) >= 0)
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

    private static bool IsSafeRelativeModPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') ||
            Path.IsPathRooted(path) || path.Contains('\\') ||
            !string.Equals(Path.GetExtension(path), ".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }
    internal static string Dx11TexturePath(string path, ushort flags)
        => PathRules.Dx11TexturePath(path, flags);

    private static string ReadNullTerminated(byte[] strings, int offset)
        => PathRules.ReadNullTerminated(strings, offset);

    private static string NormalizeGamePath(string value)
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

    private static bool IsSafeModResourceFile(string root, string file)
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

    private static string? NormalizePhysicalPath(string? path)
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

    private static void AddCandidateRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePhysicalPath(path);
        if (normalized is not null && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            roots.Add(normalized);
    }

    private static bool IsPathWithin(string path, string root)
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

    private static Dictionary<string, HashSet<string>>?[] EmptyResourceResults(int count)
        => Enumerable.Repeat<Dictionary<string, HashSet<string>>?>(null, count).ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
