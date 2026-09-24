using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Lumina.Data;

namespace InstantEdit.Services.Previews;

/// <summary> A texture ready to draw: the colour image and, once requested, its alpha channel. </summary>
public sealed class TexturePreview : IDisposable
{
    private int _alphaRequested;

    public TexturePreview(IDalamudTextureWrap rgb, DecodedTexture info)
    {
        Rgb = rgb;
        Info = info;
    }

    public IDalamudTextureWrap Rgb { get; }
    public IDalamudTextureWrap? Alpha { get; internal set; }
    public DecodedTexture Info { get; }

    internal bool ClaimAlphaBuild() => Interlocked.CompareExchange(ref _alphaRequested, 1, 0) == 0;

    public void Dispose()
    {
        Rgb.Dispose();
        Alpha?.Dispose();
        Alpha = null;
    }
}

/// <summary>
/// Owns the preview caches for textures and materials. Loads run on worker threads (file or
/// game-data read, CPU decode, GPU upload); <see cref="GetTexture"/>, <see cref="GetMaterial"/>,
/// <see cref="KeyFor"/> and <see cref="Pump"/> are called from the draw thread.
/// </summary>
public sealed class PreviewService : IDisposable
{
    private const int KeyMemoMs = 1000;
    private const int KeyMemoLimit = 1024;
    private readonly ITextureProvider _textures;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly AsyncPreviewCache<TexturePreview> _textureCache;
    private readonly AsyncPreviewCache<MaterialPreview> _materialCache;
    private readonly Dictionary<string, (long At, PreviewKey Key)> _keys = new(StringComparer.OrdinalIgnoreCase);

    public PreviewService(ITextureProvider textures, IDataManager data, IPluginLog log, long byteBudget = 64L << 20)
    {
        _textures = textures;
        _data = data;
        _log = log;
        _textureCache = new AsyncPreviewCache<TexturePreview>(LoadTextureAsync, preview => preview.Info.Bgra.Length, preview => preview.Dispose(),
            maxEntries: 32, byteBudget: byteBudget, maxConcurrent: 2);
        _materialCache = new AsyncPreviewCache<MaterialPreview>(LoadMaterialAsync, _ => 0, _ => { }, maxEntries: 64, byteBudget: long.MaxValue, maxConcurrent: 2);
    }

    /// <summary> The cache key for a row: file size and write time for mod files (re-read at most once a second), zeros for game data. </summary>
    public PreviewKey KeyFor(string path, bool isGameData)
    {
        if (isGameData)
            return new PreviewKey(path, true, 0, 0);
        var now = Environment.TickCount64;
        lock (_keys)
        {
            if (_keys.TryGetValue(path, out var memo) && now - memo.At < KeyMemoMs)
                return memo.Key;
        }

        long size = 0, mtime = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                size = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception)
        {
            // An unreadable path simply keys as an empty file; the load reports the real error.
        }

        var key = new PreviewKey(path, false, size, mtime);
        lock (_keys)
        {
            if (_keys.Count >= KeyMemoLimit)
                _keys.Clear();
            _keys[path] = (now, key);
        }
        return key;
    }

    public PreviewEntry<TexturePreview> GetTexture(PreviewKey key) => _textureCache.Get(key);

    public PreviewEntry<MaterialPreview> GetMaterial(PreviewKey key) => _materialCache.Get(key);

    /// <summary> Builds the alpha-only image of a ready preview in the background, once. </summary>
    public void RequestAlpha(PreviewEntry<TexturePreview> entry)
    {
        if (entry.State != PreviewState.Ready || entry.Value is not { } preview || preview.Alpha is not null || !preview.ClaimAlphaBuild())
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var alpha = TextureDecoder.AlphaOnly(preview.Info.Bgra);
                var wrap = await _textures.CreateFromRawAsync(RawImageSpecification.Bgra32(preview.Info.Width, preview.Info.Height), alpha,
                    "InstantEdit.Preview.Alpha").ConfigureAwait(false);
                if (entry.State == PreviewState.Ready && ReferenceEquals(entry.Value, preview))
                    preview.Alpha = wrap;
                else
                    wrap.Dispose();
            }
            catch (Exception e)
            {
                _log.Debug($"Could not build an alpha preview: {e.Message}");
            }
        });
    }

    /// <summary> Forgets every preview of the path; the next hover reloads it. Safe from any thread. </summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        lock (_keys)
            _keys.Remove(path);
        _textureCache.Invalidate(path);
        _materialCache.Invalidate(path);
    }

    /// <summary> Releases previews evicted since the last frame. Call once per frame from the draw thread. </summary>
    public void Pump()
    {
        _textureCache.Pump();
        _materialCache.Pump();
    }

    public void Dispose()
    {
        _textureCache.Dispose();
        _materialCache.Dispose();
    }

    private async Task<byte[]> ReadBytesAsync(PreviewKey key, CancellationToken token)
    {
        if (key.IsGameData)
        {
            var file = await _data.GetFileAsync<FileResource>(key.Path, token).ConfigureAwait(false);
            return file?.Data ?? throw new FileNotFoundException($"Game file not found: {key.Path}");
        }

        TextureFiles.EnsureLocalPath(key.Path);
        return await File.ReadAllBytesAsync(key.Path, token).ConfigureAwait(false);
    }

    private async Task<TexturePreview> LoadTextureAsync(PreviewKey key, CancellationToken token)
    {
        var bytes = await ReadBytesAsync(key, token).ConfigureAwait(false);
        var decoded = TextureDecoder.Decode(bytes);
        token.ThrowIfCancellationRequested();
        var wrap = await _textures.CreateFromRawAsync(
            RawImageSpecification.Bgra32(decoded.Width, decoded.Height),
            decoded.Bgra,
            $"InstantEdit.Preview:{Path.GetFileName(key.Path)}",
            token).ConfigureAwait(false);
        return new TexturePreview(wrap, decoded);
    }

    private async Task<MaterialPreview> LoadMaterialAsync(PreviewKey key, CancellationToken token)
    {
        var bytes = await ReadBytesAsync(key, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return MaterialReader.Read(bytes);
    }
}
