using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace InstantEdit.Services;

public sealed record ManagedBackupTarget(string Id, string Directory);

/// <summary>Plugin-owned backup history, isolated from Penumbra mod folders.</summary>
public sealed partial class ModelBackupStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _root;

    public ModelBackupStore(string configDirectory)
    {
        _root = Path.Combine(Path.GetFullPath(configDirectory), "Backups");
        Directory.CreateDirectory(_root);
        Cleanup();
    }

    public ManagedBackupTarget Describe(string modDirectory, string targetRelativePath)
    {
        if (!PenumbraService.IsSafeModName(modDirectory) ||
            !PenumbraService.IsSafeRelativeModelPath(targetRelativePath))
            throw new ArgumentException("The backup target is invalid.");
        var key = $"{modDirectory.Trim().ToLowerInvariant()}\n{targetRelativePath.Replace('\\', '/').Trim().ToLowerInvariant()}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return new ManagedBackupTarget(id, Path.Combine(_root, id));
    }

    public string Create(string targetFile, string modDirectory, string targetRelativePath)
    {
        var target = Describe(modDirectory, targetRelativePath);
        targetFile = Path.GetFullPath(targetFile);
        if (!File.Exists(targetFile))
            throw new FileNotFoundException("The model to back up no longer exists.", targetFile);
        Directory.CreateDirectory(target.Directory);
        var original = Path.GetFileName(targetFile);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = $"{original}.{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss.ffffff'Z'}.bak";
            var path = Path.Combine(target.Directory, name);
            try
            {
                File.Copy(targetFile, path, false);
                Cleanup();
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Retry if two backups landed on the same timestamp.
            }
        }
        throw new IOException("Could not allocate a managed backup filename.");
    }

    public string Resolve(string targetId, string backupName)
    {
        if (!TargetIdRegex().IsMatch(targetId) || !BackupNameRegex().IsMatch(backupName) ||
            !string.Equals(Path.GetFileName(backupName), backupName, StringComparison.Ordinal))
            throw new ArgumentException("The managed backup identity is invalid.");
        var directory = Path.Combine(_root, targetId);
        var path = Path.GetFullPath(Path.Combine(directory, backupName));
        if (!PathRules.IsPathWithin(path, directory) || !File.Exists(path))
            throw new FileNotFoundException("The managed backup no longer exists.", backupName);
        return path;
    }

    public void Clear(string targetId)
    {
        if (!TargetIdRegex().IsMatch(targetId))
            throw new ArgumentException("The managed backup target is invalid.");
        var directory = Path.Combine(_root, targetId);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.bak", SearchOption.TopDirectoryOnly))
            if (BackupNameRegex().IsMatch(Path.GetFileName(path)))
                File.Delete(path);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    public void Cleanup(DateTimeOffset? now = null)
    {
        if (!Directory.Exists(_root))
            return;
        var cutoff = (now ?? DateTimeOffset.UtcNow) - Retention;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (!TargetIdRegex().IsMatch(Path.GetFileName(directory)) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.bak", SearchOption.TopDirectoryOnly))
            {
                var match = BackupNameRegex().Match(Path.GetFileName(path));
                if (match.Success && DateTimeOffset.TryParseExact(match.Groups["stamp"].Value,
                        "yyyyMMdd'T'HHmmss.ffffff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var created) && created < cutoff)
                    File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetIdRegex();

    [GeneratedRegex("^[^\\\\/:*?\"<>|]+\\.(?:mdl|fbx)\\.(?<stamp>\\d{8}T\\d{6}\\.\\d{6}Z)\\.bak$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BackupNameRegex();
}
