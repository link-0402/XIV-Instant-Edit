using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> Writes files into a mod folder and removes staging folders, refusing paths that leave the folder. </summary>
internal static class ModFiles
{
    internal static void WriteBytesAtomic(string root, string relativePath, byte[] bytes)
    {
        if (!IsSafeRelativeResourcePath(relativePath))
            throw new InvalidDataException("A generated mashup path is unsafe.");
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Mashup file has no parent.");
        if (!IsPathWithin(target, root) || HasReparsePointInPath(root, parent) ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("A generated mashup destination is unsafe.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void TryDeleteMashupNamespace(string root, string folder)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullFolder) || !IsPathWithin(fullFolder, fullRoot) ||
                string.Equals(fullFolder, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(fullFolder) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(fullFolder, true);
        }
        catch
        {
            // Cleanup is best-effort; the error returned to Blender remains authoritative.
        }
    }
}
