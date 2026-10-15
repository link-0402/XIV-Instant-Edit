using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Writes an exported model over its file in a source mod, or a managed backup back over it, keeping a
/// managed backup of the file it replaces.
/// </summary>
internal sealed class ModelFileWriter
{
    private readonly ModelBackupStore _backups;

    public ModelFileWriter(ModelBackupStore backups)
    {
        _backups = backups;
    }

    internal string? WriteModelToOriginalLocation(
        string modFolder,
        string targetFile,
        string exportedFile,
        bool backupExisting = false,
        string? modDirectory = null,
        string? targetRelativePath = null,
        JsonObject? estState = null)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetFile);
            var parent = Path.GetDirectoryName(fullTarget);
            if (parent is null || !IsPathWithin(fullTarget, modFolder) ||
                !string.Equals(Path.GetExtension(fullTarget), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                HasReparsePointInPath(modFolder, parent) ||
                (File.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0))
                return "The original mod destination is unsafe.";

            Directory.CreateDirectory(parent);
            if (backupExisting && File.Exists(fullTarget))
            {
                if (_backups is null || modDirectory is null || targetRelativePath is null)
                    return "Managed backup storage is unavailable.";
                var backup = _backups.Create(fullTarget, modDirectory, targetRelativePath);
                if (estState is not null)
                    _backups.WriteEstState(backup, estState);
            }
            var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(exportedFile, temporary, true);
                File.Move(temporary, fullTarget, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            return null;
        }
        catch (Exception e)
        {
            return $"Could not write the original model file: {e.Message}";
        }
    }

    internal string? RestoreModelBackup(
        string targetFile, string backupFile, string modDirectory, string targetRelativePath, JsonObject? estState = null)
    {
        try
        {
            if (File.Exists(targetFile) && _backups is not null)
            {
                var backup = _backups.Create(targetFile, modDirectory, targetRelativePath);
                if (estState is not null)
                    _backups.WriteEstState(backup, estState);
            }
            var temporary = Path.Combine(Path.GetDirectoryName(targetFile)!, $".instant-edit-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(backupFile, temporary, false);
                File.Move(temporary, targetFile, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return null;
        }
        catch (Exception e)
        {
            return $"Could not restore the model backup: {e.Message}";
        }
    }

    internal static bool TryGetBackupOriginal(string? backupName, out string originalName)
    {
        originalName = "";
        if (string.IsNullOrWhiteSpace(backupName) || backupName.Length > 512 ||
            backupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || backupName.Contains('/') || backupName.Contains('\\'))
            return false;
        var match = Regex.Match(
            backupName,
            @"^(?<original>.+\.mdl)\.\d{8}T\d{6}\.\d{6}Z\.bak$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            originalName = match.Groups["original"].Value;
        else if (backupName.EndsWith(".mdl.bak", StringComparison.OrdinalIgnoreCase))
            originalName = backupName[..^4];
        else
            return false;
        return originalName.Length <= 255 && originalName is not ".mdl" and not "." and not "..";
    }
}
