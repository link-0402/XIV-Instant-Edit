using System.Text.Json;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A folder or a .pose file as the pose browser shows it.</summary>
/// <param name="Own">Its own picture: a folder's (<see cref="PoseLibrary.FolderPreview"/>), or the image named like a pose beside it.</param>
/// <param name="Shared">For a pose, its folder's picture, shown when it has none of its own (it may hold one, see <see cref="PoseLibrary.EmbeddedImage"/>).</param>
/// <param name="Added">When it came into its folder: the later of when it was made and last written (UTC), as a copy keeps its old write time.</param>
internal sealed record PoseLibraryEntry(string Path, string Name, bool Folder, string? Own, string? Shared, DateTime Added);

/// <summary>
/// Reads pose folders for the pose browser: a folder's subfolders, then its .pose files, each in name
/// order with the image that previews it. A pose pack usually holds one picture for all its poses, named
/// like its folder or alone in it. Unreadable folders are skipped. No ImGui or Dalamud dependencies.
/// </summary>
internal static class PoseLibrary
{
    /// <summary>
    /// <paramref name="entries"/> folders first, then poses, each by name or with <paramref name="newestFirst"/>
    /// the newest first (by name when as new).
    /// </summary>
    public static List<PoseLibraryEntry> Sorted(IEnumerable<PoseLibraryEntry> entries, bool newestFirst)
    {
        var folders = entries.OrderBy(entry => !entry.Folder);
        return [.. newestFirst
            ? folders.ThenByDescending(entry => entry.Added).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            : folders.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static DateTime Added(FileSystemInfo info) => info.CreationTimeUtc > info.LastWriteTimeUtc ? info.CreationTimeUtc : info.LastWriteTimeUtc;

    public const string Extension = ".pose";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    private static readonly EnumerationOptions TopLevel = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
    private static readonly EnumerationOptions Everywhere = new() { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The library's own folders, as folders; one that is gone stays, so it can be taken off the list.</summary>
    public static List<PoseLibraryEntry> Roots(IEnumerable<string> roots, CancellationToken token)
    {
        var entries = new List<PoseLibraryEntry>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(root);
            entries.Add(info.Exists
                ? new PoseLibraryEntry(root, RootName(root), true, FolderPreview(root, Images(root)), null, Added(info))
                : new PoseLibraryEntry(root, RootName(root), true, null, null, DateTime.MinValue));
        }
        return entries;
    }

    /// <summary>A library folder's name: its last part, or the whole path for a drive.</summary>
    public static string RootName(string root) =>
        System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } name ? name : root;

    /// <summary>The folders, then the poses, in <paramref name="folder"/>.</summary>
    public static List<PoseLibraryEntry> List(string folder, CancellationToken token)
    {
        var entries = new List<PoseLibraryEntry>();
        var info = new DirectoryInfo(folder);
        foreach (var sub in info.EnumerateDirectories("*", TopLevel).OrderBy(sub => sub.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            entries.Add(FolderEntry(sub));
        }
        AddPoses(entries, folder, info.EnumerateFiles("*" + Extension, TopLevel), token);
        return entries;
    }

    private static PoseLibraryEntry FolderEntry(DirectoryInfo folder) =>
        new(folder.FullName, folder.Name, true, FolderPreview(folder.FullName, Images(folder.FullName)), null, Added(folder));

    /// <summary>The folders and poses in and below <paramref name="folder"/> whose names hold <paramref name="text"/>, at most <paramref name="limit"/>.</summary>
    public static List<PoseLibraryEntry> Search(string folder, string text, int limit, CancellationToken token)
    {
        var entries = new List<PoseLibraryEntry>();
        var info = new DirectoryInfo(folder);
        foreach (var sub in info.EnumerateDirectories("*", Everywhere))
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= limit)
                return entries;
            if (sub.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                entries.Add(FolderEntry(sub));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        var poses = info.EnumerateFiles("*" + Extension, Everywhere)
            .Where(pose => System.IO.Path.GetFileNameWithoutExtension(pose.Name).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(limit - entries.Count);
        foreach (var group in poses.GroupBy(pose => pose.DirectoryName))
            AddPoses(entries, group.Key ?? folder, group, token);
        return entries;
    }

    /// <summary>Adds the poses of one folder, <paramref name="folder"/>, in name order.</summary>
    private static void AddPoses(List<PoseLibraryEntry> entries, string folder, IEnumerable<FileInfo> poses, CancellationToken token)
    {
        var images = Images(folder);
        var shared = FolderPreview(folder, images);
        foreach (var pose in poses.OrderBy(pose => pose.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileNameWithoutExtension(pose.Name);
            entries.Add(new PoseLibraryEntry(pose.FullName, name, false, PosePreview(name, images), shared, Added(pose)));
        }
    }

    private static List<string> Images(string folder)
    {
        try { return [.. Directory.EnumerateFiles(folder, "*", TopLevel).Where(IsImage)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A folder's own picture: the image named like the folder, or the only image in it.</summary>
    public static string? FolderPreview(string folder, IReadOnlyList<string> images)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        return images.FirstOrDefault(image => string.Equals(System.IO.Path.GetFileNameWithoutExtension(image), name, StringComparison.OrdinalIgnoreCase))
            ?? (images.Count == 1 ? images[0] : null);
    }

    /// <summary>A pose's own picture: the image of the same name beside it.</summary>
    public static string? PosePreview(string pose, IReadOnlyList<string> images) =>
        images.FirstOrDefault(image => string.Equals(System.IO.Path.GetFileNameWithoutExtension(image), pose, StringComparison.OrdinalIgnoreCase));

    /// <summary>The picture a pose file holds itself (Brio saves one as Base64Image), or null.</summary>
    public static byte[]? EmbeddedImage(byte[] pose)
    {
        if (pose.Length > PoseFiles.MaximumBytes)
            return null;
        var start = pose.Length >= 3 && pose[0] == 0xEF && pose[1] == 0xBB && pose[2] == 0xBF ? 3 : 0;
        try
        {
            using var document = JsonDocument.Parse(pose.AsMemory(start),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Base64Image", out var image) || image.ValueKind != JsonValueKind.String)
                return null;
            var text = image.GetString();
            return string.IsNullOrEmpty(text) ? null : Convert.FromBase64String(text);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }
}
