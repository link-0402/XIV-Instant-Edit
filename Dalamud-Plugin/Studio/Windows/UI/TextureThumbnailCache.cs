using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Data.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Sims;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>
/// Small GPU previews of textures, shared by every window: local images (png, jpeg, tga, dds), the
/// game's own texture format (.tex / .atex, inside a mod folder or read from the game by its path),
/// and in-memory PNGs. Everything is decoded and shrunk on a worker — a 4096² diffuse would otherwise
/// become a 64 MB GPU texture just to draw a tile's thumbnail; for game textures a mip close to the
/// thumbnail size is decoded instead of the full image — cached by path (and, for files, last-write
/// time), and evicted least-recently-drawn first. A miss draws a placeholder rather than blocking.
/// </summary>
internal sealed class TextureThumbnailCache : IDisposable
{
    private const int MaxSide  = 256;
    private const int Capacity = 192;

    /// <summary>How often (in frames) a cached file's timestamp is re-checked for edits.</summary>
    private const int RecheckFrames = 90;

    private const string GamePrefix   = "game://";
    private const string MemoryPrefix = "mem://";

    private sealed class Entry
    {
        public required Task<IDalamudTextureWrap> Load;
        public DateTime Stamp;
        public long     LastUsed;
        public long     CheckedAt;
    }

    private sealed record FolderListing(string? FirstImage, int Count, long ListedAt);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderListing> _folders = new(StringComparer.OrdinalIgnoreCase);
    private long _frame;

    /// <summary>
    /// Advances the frame clock and trims the cache. Call once per frame. Thumbnails drawn in the
    /// last frame are kept even past <see cref="Capacity"/>: evicting them would only load them
    /// again straight away, every frame, while more than that many are on screen.
    /// </summary>
    public void NewFrame()
    {
        _frame++;
        if (_entries.Count <= Capacity)
            return;

        var stale = _entries.Where(e => e.Value.LastUsed < _frame - 1).OrderBy(e => e.Value.LastUsed)
            .Take(_entries.Count - Capacity).Select(e => e.Key).ToList();
        foreach (var key in stale)
        {
            Release(_entries[key]);
            _entries.Remove(key);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drawing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the thumbnail for a texture slot in whichever mode it is in: a white swatch for
    /// a generated dummy, the first image (with a count) for a variant folder, or the file
    /// itself. Hovering shows a larger preview.
    /// </summary>
    public void Draw(TextureSlot tex, float side)
    {
        if (tex.UseWhiteDummy)
        {
            var option = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);
            Swatch(side, new Vector4(option.R, option.G, option.B, option.A) / 255f);
            Gui.Tooltip($"Generated {option.Label.ToLowerInvariant()} {DummyTextureLibrary.Size}×{DummyTextureLibrary.Size} texture");
            return;
        }

        if (string.IsNullOrWhiteSpace(tex.SourcePath))
        {
            Placeholder(side, FontAwesomeIcon.Image, Theme.Faint, "No source set");
            return;
        }

        if (tex.UseVariants)
        {
            DrawFolder(tex.SourcePath, side);
            return;
        }

        DrawFile(tex.SourcePath, side, null);
    }

    /// <summary>The first image of a variant folder, with the folder's image count over it.</summary>
    public void DrawFolder(string folder, float side)
    {
        var listing = ListFolder(folder);
        if (listing.FirstImage == null)
        {
            Placeholder(side, FontAwesomeIcon.FolderOpen, Theme.Bad, listing.Count < 0 ? "Folder not found" : "No images in folder");
            return;
        }

        DrawFile(listing.FirstImage, side, $"{listing.Count} variant image(s) — first shown");
        var min = ImGui.GetItemRectMin();
        var countText = listing.Count.ToString();
        var size = ImGui.CalcTextSize(countText) + new Vector2(Theme.S(6), Theme.S(2));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.75f)), Theme.S(3));
        draw.AddText(min + new Vector2(Theme.S(3), Theme.S(1)), ImGui.GetColorU32(Theme.Accent), countText);
    }

    /// <summary>A file on disk: an image, or a texture in the game's own format.</summary>
    public void DrawFile(string path, float side, string? caption = null)
    {
        if (!File.Exists(path))
        {
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Bad, $"File not found:\n{path}");
            return;
        }

        DrawLoaded(Get(path, () => MakeThumbnailPng(path), checkStamp: true), side, caption ?? Path.GetFileName(path));
    }

    /// <summary>One of the game's own textures, by its game path.</summary>
    public void DrawGame(string gamePath, float side, string? caption = null)
        => DrawLoaded(Get(GamePrefix + gamePath, () => GameThumbnailPng(gamePath), checkStamp: false), side, caption ?? gamePath);

    private static void DrawLoaded(Task<IDalamudTextureWrap>? load, float side, string caption)
    {
        if (load == null || !load.IsCompleted)
        {
            Placeholder(side, FontAwesomeIcon.Hourglass, Theme.Faint, "Loading preview");
            return;
        }

        if (!load.IsCompletedSuccessfully)
        {
            var reason = load.Exception?.GetBaseException().Message ?? "unknown error";
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Warn, $"No preview: {reason}");
            return;
        }

        var wrap = load.Result;
        var fitted = Fit(wrap.Size, side);
        var pos = ImGui.GetCursorScreenPos();
        // Centre a non-square image in its square, so tiles stay aligned.
        ImGui.SetCursorScreenPos(pos + (new Vector2(side) - fitted) * 0.5f);
        ImGui.Image(wrap.Handle, fitted);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Image(wrap.Handle, Fit(wrap.Size, Theme.S(MaxSide)));
            ImGui.TextColored(Theme.Muted, caption);
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(side));
    }

    /// <summary>
    /// Draws an already-encoded PNG (e.g. a Sims package thumbnail) cached under
    /// <paramref name="key"/>. Keys are namespaced so they never collide with file paths.
    /// </summary>
    public void DrawPng(string key, byte[] png, float side)
    {
        var cacheKey = MemoryPrefix + key;
        if (!_entries.TryGetValue(cacheKey, out var entry))
        {
            entry = new Entry { Load = StudioServices.TextureProvider.CreateFromImageAsync(png), CheckedAt = long.MaxValue };
            _entries[cacheKey] = entry;
        }
        entry.LastUsed = _frame;

        if (!entry.Load.IsCompleted)
        {
            Placeholder(side, FontAwesomeIcon.Hourglass, Theme.Faint, "Loading preview");
            return;
        }
        if (!entry.Load.IsCompletedSuccessfully)
        {
            Placeholder(side, FontAwesomeIcon.ExclamationTriangle, Theme.Warn, "No preview");
            return;
        }

        var wrap = entry.Load.Result;
        ImGui.Image(wrap.Handle, Fit(wrap.Size, side));
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Image(wrap.Handle, Fit(wrap.Size, Theme.S(MaxSide)));
            ImGui.TextColored(Theme.Muted, $"{wrap.Width}×{wrap.Height} preview");
            ImGui.EndTooltip();
        }
    }

    /// <summary>Drops every in-memory thumbnail, e.g. when a new package is scanned.</summary>
    public void ForgetMemoryEntries()
    {
        foreach (var key in _entries.Keys.Where(k => k.StartsWith(MemoryPrefix, StringComparison.Ordinal)).ToList())
        {
            Release(_entries[key]);
            _entries.Remove(key);
        }
    }

    private static Vector2 Fit(Vector2 size, float side)
        => size.X >= size.Y
            ? new Vector2(side, side * size.Y / Math.Max(size.X, 1f))
            : new Vector2(side * size.X / Math.Max(size.Y, 1f), side);

    private static void Swatch(float side, Vector4 color)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(side), ImGui.GetColorU32(color), Theme.S(3));
        draw.AddRect(pos, pos + new Vector2(side), ImGui.GetColorU32(Theme.Faint), Theme.S(3));
        ImGui.Dummy(new Vector2(side));
    }

    /// <summary>An outlined square with an icon in it, standing in where there is no picture.</summary>
    public static void Placeholder(float side, FontAwesomeIcon icon, Vector4 color, string tooltip)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(pos, pos + new Vector2(side), ImGui.GetColorU32(Theme.Faint), Theme.S(3));
        Tile.DrawIcon(draw, icon, pos, new Vector2(side), color, side >= Theme.S(30) ? side * 0.4f : null);
        ImGui.Dummy(new Vector2(side));
        Gui.Tooltip(tooltip);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The (possibly still running) load for a key, restarted if its file changed on disk.</summary>
    private Task<IDalamudTextureWrap>? Get(string key, Func<byte[]> makePng, bool checkStamp)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.LastUsed = _frame;
            if (!checkStamp || _frame - entry.CheckedAt < RecheckFrames)
                return entry.Load;

            entry.CheckedAt = _frame;
            if (Stamp(key) == entry.Stamp)
                return entry.Load;

            Release(entry);
            _entries.Remove(key);
        }

        var created = new Entry
        {
            Load      = Task.Run(async () => await StudioServices.TextureProvider.CreateFromImageAsync(makePng()).ConfigureAwait(false)),
            Stamp     = checkStamp ? Stamp(key) : default,
            LastUsed  = _frame,
            CheckedAt = _frame,
        };
        _entries[key] = created;
        return created.Load;
    }

    private static byte[] MakeThumbnailPng(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tex" or ".atex")
            return TexThumbnail(StudioServices.DataManager.GameData.GetFileFromDisk<TexFile>(path));

        var data = File.ReadAllBytes(path);
        if (ext == ".dds")
            return SimsTextureDecoder.Decode(SimsResourceType.ImgDds, data).ToThumbnailPng(MaxSide);

        using var image = Image.Load<Rgba32>(InstantEdit.Studio.Services.StudioImages.Decoder, data);   // png, jpeg, tga …
        return ToPng(image);
    }

    private static byte[] GameThumbnailPng(string gamePath)
    {
        var tex = StudioServices.DataManager.GetFile<TexFile>(gamePath)
            ?? throw new FileNotFoundException("The game has no file at this path.");
        return TexThumbnail(tex);
    }

    /// <summary>A texture in the game's format as a small PNG: the smallest mip still at least the thumbnail size, decoded to BGRA.</summary>
    private static byte[] TexThumbnail(TexFile tex)
    {
        var buffer = tex.TextureBuffer;
        int mips = Math.Max(1, buffer.MipmapAllocations.Length);
        int mip = 0;
        while (mip + 1 < mips && Math.Max(buffer.WidthOfMipmap(mip + 1), buffer.HeightOfMipmap(mip + 1)) >= MaxSide)
            mip++;

        var filtered = buffer.Filter(mip, 0, TexFile.TextureFormat.B8G8R8A8);
        using var image = Image.LoadPixelData<Bgra32>(filtered.RawData, filtered.Width, filtered.Height);
        return ToPng(image);
    }

    private static byte[] ToPng<TPixel>(Image<TPixel> image) where TPixel : unmanaged, IPixel<TPixel>
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxSide, MaxSide), Mode = ResizeMode.Max }));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static DateTime Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return default; }
    }

    /// <summary>First image and image count of a variant folder, re-listed every few seconds. Count is -1 if missing.</summary>
    private FolderListing ListFolder(string folder)
    {
        if (_folders.TryGetValue(folder, out var cached) && _frame - cached.ListedAt < RecheckFrames * 2)
            return cached;

        FolderListing listing;
        try
        {
            if (!Directory.Exists(folder))
            {
                listing = new FolderListing(null, -1, _frame);
            }
            else
            {
                var images = TextureSources.EnumerateImages(folder).ToList();
                listing = new FolderListing(images.FirstOrDefault(), images.Count, _frame);
            }
        }
        catch (Exception)
        {
            listing = new FolderListing(null, -1, _frame);
        }

        _folders[folder] = listing;
        return listing;
    }

    private static void Release(Entry entry)
    {
        // Dispose once the load finishes, whenever that is — the wrap may still be in flight.
        entry.Load.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result.Dispose();
        }, TaskScheduler.Default);
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
            Release(entry);
        _entries.Clear();
        _folders.Clear();
    }

    public void Dispose() => Clear();
}
