namespace InstantEdit.Services.PreviewMods;

/// <summary> A kept backup of one file a preview can write: the file's source and the backup. </summary>
internal sealed record PreviewBackupFile(PreviewSource Source, ManagedBackup Backup);

/// <summary> Backups made together, such as by one apply: restoring them puts every file back as it was before that moment. </summary>
internal sealed record PreviewBackupGroup(DateTimeOffset Created, IReadOnlyList<PreviewBackupFile> Files);

/// <summary>
/// Groups the kept backups of files a Quick Action reads by when they were made. An apply backs up
/// all the files it writes within a second or two, so backups less than
/// <see cref="Window"/> apart belong together; a file backed up twice within one group keeps its
/// earlier backup, the state before the first write.
/// </summary>
internal static class PreviewBackups
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary> The groups, newest first, each file in it once. </summary>
    public static IReadOnlyList<PreviewBackupGroup> Group(IEnumerable<PreviewBackupFile> files)
    {
        var groups = new List<List<PreviewBackupFile>>();
        foreach (var file in files.OrderBy(f => f.Backup.Created))
        {
            if (groups.Count == 0 || file.Backup.Created - groups[^1][0].Backup.Created > Window)
                groups.Add([]);
            var group = groups[^1];
            if (!group.Any(f => SameFile(f.Source, file.Source)))
                group.Add(file);
        }
        return groups.Select(g => new PreviewBackupGroup(g[0].Backup.Created, g)).Reverse().ToList();
    }

    private static bool SameFile(PreviewSource a, PreviewSource b)
        => string.Equals(a.ModDirectory, b.ModDirectory, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(a.RelativePath.Replace('\\', '/'), b.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
