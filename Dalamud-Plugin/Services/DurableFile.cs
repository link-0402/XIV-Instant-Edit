using System.Text;

namespace InstantEdit.Services;

/// <summary>
/// Writes a temporary file all the way to disk. The plugin replaces its files by writing a temporary
/// one and moving it over the real one; without the flush NTFS can keep the rename but lose the data
/// after a crash or power cut, and the file the move published is empty.
/// </summary>
internal static class DurableFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Utf8.GetBytes(text));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
