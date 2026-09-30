namespace InstantEdit.Services;

/// <summary> A file's size and last write time, to notice that it changed. Dalamud-free. </summary>
internal readonly record struct FileStamp(long Length, DateTime Written)
{
    /// <summary> The file's stamp now; null for a game file or one that can't be read. </summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
                return null;
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
