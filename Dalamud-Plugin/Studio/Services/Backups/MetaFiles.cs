using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Backups;

/// <summary>
/// A mod's option files as they were before an edit: meta.json, and for a mod in Penumbra's old
/// layout its default_mod.json and group_*.json, which writing meta.json sets aside. Put back as a
/// whole when Penumbra rejects the edit. Dalamud-free.
/// </summary>
internal sealed class MetaFiles
{
    private readonly List<(string RelPath, byte[] Bytes)> _files = new();
    private readonly List<string> _newAsides = new();

    private MetaFiles() { }

    /// <summary>The option files of the old layout, by name, that lie in the folder now.</summary>
    public static IReadOnlyList<string> LegacyFiles(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "group_*.json").Select(Path.GetFileName).OfType<string>().ToList();
            if (File.Exists(Path.Combine(folder, LegacyMetaReader.DefaultModFile)))
                files.Insert(0, LegacyMetaReader.DefaultModFile);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Reads them; throws <see cref="IOException"/> when meta.json can't be read.</summary>
    public static MetaFiles Take(string folder)
    {
        var taken = new MetaFiles();
        taken._files.Add((PenumbraModMeta.FileName, File.ReadAllBytes(Path.Combine(folder, PenumbraModMeta.FileName))));
        foreach (var file in LegacyFiles(folder))
        {
            taken._files.Add((file, File.ReadAllBytes(Path.Combine(folder, file))));
            if (!File.Exists(Path.Combine(folder, file + PenumbraModMeta.BackupSuffix)))
                taken._newAsides.Add(file + PenumbraModMeta.BackupSuffix);
        }
        return taken;
    }

    /// <summary>Writes every file back as it was and removes the copies set aside since.</summary>
    public void PutBack(string folder)
    {
        foreach (var (relPath, bytes) in _files)
        {
            var path = Path.Combine(folder, relPath);
            var temp = path + ".instant-edit-tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        foreach (var aside in _newAsides)
        {
            var path = Path.Combine(folder, aside);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
