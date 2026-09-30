using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace InstantEdit.Services;

public sealed record ManagedBackupTarget(string Id, string Directory);

/// <summary> One kept backup of a mod file: its target, its file name in the store, and when it was made. </summary>
public sealed record ManagedBackup(string TargetId, string Name, DateTimeOffset Created);

/// <summary>Plugin-owned backup history, isolated from Penumbra mod folders.</summary>
public sealed partial class ModelBackupStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
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
            !(PenumbraService.IsSafeRelativeModelPath(targetRelativePath) ||
              PenumbraService.IsSafeGameResourcePath(targetRelativePath, ".tex") ||
              PenumbraService.IsSafeGameResourcePath(targetRelativePath, ".mtrl") ||
              PenumbraService.IsSafeGameResourcePath(targetRelativePath, ".pap")))
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
            throw new FileNotFoundException("The resource to back up no longer exists.", targetFile);
        Directory.CreateDirectory(target.Directory);
        var original = Path.GetFileName(targetFile);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{original}.{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss.ffffff'Z'}.bak");
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

    /// <summary>
    /// The hair EST entries that belong with a model backup, kept beside it as
    /// <c>&lt;backup&gt;.est.json</c>. Restoring the backup puts them back.
    /// </summary>
    public void WriteEstState(string backupPath, System.Text.Json.Nodes.JsonObject state)
    {
        var path = EstStatePath(backupPath);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, state.ToJsonString());
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public System.Text.Json.Nodes.JsonObject? ReadEstState(string backupPath)
    {
        var path = EstStatePath(backupPath);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return null;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private string EstStatePath(string backupPath)
    {
        var full = Path.GetFullPath(backupPath);
        if (!PathRules.IsPathWithin(full, _root) || !BackupNameRegex().IsMatch(Path.GetFileName(full)))
            throw new ArgumentException("The managed backup path is invalid.");
        return full + EstStateSuffix;
    }

    private const string EstStateSuffix = ".est.json";

    // A backup's EST state goes with it: once the .bak is gone (expired, cleared, or removed by the add-on) so is this.
    private static void DeleteOrphanedEstStates(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*" + EstStateSuffix, SearchOption.TopDirectoryOnly))
        {
            var backup = path[..^EstStateSuffix.Length];
            if (BackupNameRegex().IsMatch(Path.GetFileName(backup)) && !File.Exists(backup))
                File.Delete(path);
        }
    }

    /// <summary> The backups kept of a mod file, newest first; none when the target isn't one the store backs up. </summary>
    public IReadOnlyList<ManagedBackup> List(string modDirectory, string targetRelativePath)
    {
        ManagedBackupTarget target;
        try { target = Describe(modDirectory, targetRelativePath); }
        catch (ArgumentException) { return []; }
        if (!Directory.Exists(target.Directory) || (File.GetAttributes(target.Directory) & FileAttributes.ReparsePoint) != 0)
            return [];
        var backups = new List<ManagedBackup>();
        foreach (var path in Directory.EnumerateFiles(target.Directory, "*.bak", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var match = BackupNameRegex().Match(name);
            if (match.Success && DateTimeOffset.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var created))
                backups.Add(new ManagedBackup(target.Id, name, created));
        }
        return backups.OrderByDescending(b => b.Created).ToList();
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
        DeleteOrphanedEstStates(directory);
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
            DeleteOrphanedEstStates(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetIdRegex();

    [GeneratedRegex("^[^\\\\/:*?\"<>|]+\\.(?:mdl|fbx|tex|pap|mtrl)\\.(?<stamp>\\d{8}T\\d{6}\\.\\d{6}Z)\\.bak$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BackupNameRegex();
}
