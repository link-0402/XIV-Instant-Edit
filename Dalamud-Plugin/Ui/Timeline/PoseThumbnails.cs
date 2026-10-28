using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The pose browser's tile pictures, made small as they load: a pose pack's picture is often 4K, and a folder
/// shows dozens. A pose shows its own image, else the one its file holds, else its folder's. Pictures load two
/// at a time and only for tiles drawn; past <see cref="Kept"/>, the ones not drawn for a while are let go.
/// </summary>
internal sealed class PoseThumbnails(ITextureProvider textures) : IDisposable
{
    // Sharp at the largest tile size (see Configuration.MaxPoseBrowserTile) under a raised UI scale.
    private const int Edge = 512;
    private const int Loading = 2;
    private const int Kept = 160;
    // Frames a picture stays after it was last drawn before it can be let go.
    private const int Grace = 120;
    private const long MaximumImageBytes = 64L * 1024 * 1024;

    private sealed class Picture
    {
        public Task<IDalamudTextureWrap?>? Reading;
        public IDalamudTextureWrap? Wrap;
        // Read, with nothing to show: an image that failed, or a pose file holding no picture.
        public bool None;
        public int Used;
    }

    // By source: an image file's path, or a pose file's for the picture it holds.
    private readonly Dictionary<string, Picture> pictures = new(StringComparer.OrdinalIgnoreCase);
    private int reading;
    private bool disposed;

    /// <summary>The picture for <paramref name="entry"/>'s tile, or null while it loads or when there is none.</summary>
    public IDalamudTextureWrap? Get(PoseLibraryEntry entry)
    {
        if (entry.Own is { } own)
            return Get(own, false);
        if (entry.Folder)
            return null;
        // While the pose file is looked into, its folder's picture shows.
        return Get(entry.Path, true) ?? (entry.Shared is { } shared ? Get(shared, false) : null);
    }

    private IDalamudTextureWrap? Get(string path, bool pose)
    {
        if (disposed)
            return null;
        var frame = ImGui.GetFrameCount();
        if (!pictures.TryGetValue(path, out var picture))
            pictures[path] = picture = new Picture();
        picture.Used = frame;
        if (picture.Reading is { IsCompleted: true } done)
        {
            picture.Reading = null;
            reading--;
            if (done.IsCompletedSuccessfully && done.Result is { } wrap)
                picture.Wrap = wrap;
            else
                picture.None = true;
        }
        if (picture.Wrap == null && !picture.None && picture.Reading == null && reading < Loading)
        {
            reading++;
            picture.Reading = Task.Run(() => pose ? Held(path) : Read(path));
            LetGo(frame);
        }
        return picture.Wrap;
    }

    private async Task<IDalamudTextureWrap?> Read(string path)
    {
        if (new FileInfo(path).Length > MaximumImageBytes)
            return null;
        return await Small(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task<IDalamudTextureWrap?> Held(string pose)
    {
        if (new FileInfo(pose).Length > PoseFiles.MaximumBytes)
            return null;
        return PoseLibrary.EmbeddedImage(await File.ReadAllBytesAsync(pose).ConfigureAwait(false)) is { } image
            ? await Small(image).ConfigureAwait(false)
            : null;
    }

    /// <summary>The image at most <see cref="Edge"/> pixels on its longer side.</summary>
    private async Task<IDalamudTextureWrap?> Small(byte[] image)
    {
        var full = await textures.CreateFromImageAsync(image, "InstantEdit.PoseBrowser").ConfigureAwait(false);
        var scale = (float)Edge / Math.Max(full.Width, full.Height);
        if (scale >= 1)
            return full;
        try
        {
            var args = new TextureModificationArgs
            {
                NewWidth = Math.Max(1, (int)(full.Width * scale)),
                NewHeight = Math.Max(1, (int)(full.Height * scale)),
            };
            return await textures.CreateFromExistingTextureAsync(full, args, true, "InstantEdit.PoseBrowser").ConfigureAwait(false);
        }
        finally
        {
            full.Dispose();
        }
    }

    /// <summary>Lets go of the pictures not drawn for a while, oldest first, while more than <see cref="Kept"/> are held.</summary>
    private void LetGo(int frame)
    {
        var held = pictures.Count(pair => pair.Value.Wrap != null);
        if (held <= Kept)
            return;
        foreach (var (path, picture) in pictures.Where(pair => pair.Value.Wrap != null && frame - pair.Value.Used > Grace)
                     .OrderBy(pair => pair.Value.Used).Take(held - Kept).ToList())
        {
            picture.Wrap!.Dispose();
            pictures.Remove(path);
        }
    }

    /// <summary>Lets go of every picture, and of the ones still loading once they are made.</summary>
    public void Clear()
    {
        foreach (var picture in pictures.Values)
        {
            picture.Wrap?.Dispose();
            picture.Reading?.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                    task.Result?.Dispose();
            }, TaskScheduler.Default);
        }
        pictures.Clear();
        reading = 0;
    }

    public void Dispose()
    {
        disposed = true;
        Clear();
    }
}
