using System;
using System.IO;
using System.Threading;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Converts textures by handing them to Penumbra, which is the same route its own texture editor
/// takes: OtterTex, a wrapper around Microsoft's native DirectXTex, with a Direct3D device for the
/// compute path. The encoder bundled with this plugin is managed C# — correct, but it searches every
/// BC7 block in plain IL, which costs minutes of CPU on a large image where the native one costs
/// seconds. So Penumbra does it whenever it is there, and the bundled encoder is the fallback for
/// when it is not.
///
/// Called from the build and update workers; the IPC call itself is marshalled to the framework
/// thread, which is where Dalamud expects plugins to talk to each other, and the worker waits for
/// the task Penumbra hands back.
/// </summary>
internal sealed class PenumbraTextureConverter
{
    /// <summary>How long to wait on one texture before giving up and encoding it here instead.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly PenumbraIpcService _ipc;

    public PenumbraTextureConverter(PenumbraIpcService ipc) => _ipc = ipc;

    /// <summary>Whether Penumbra is loaded and offers the conversion call.</summary>
    public bool Available => _ipc.CanConvertTextures;

    /// <summary>
    /// Writes <paramref name="sourcePath"/> to <paramref name="outputPath"/> as a .tex in the given
    /// format, with a mip chain. Returns null on success, or why it could not be done — in which
    /// case the caller falls back to the bundled encoder.
    /// </summary>
    public string? Convert(string sourcePath, TextureCompression compression, string outputPath, CancellationToken ct)
    {
        int type = compression switch
        {
            TextureCompression.Bc7 => 7,    // Penumbra.Api TextureType.Bc7Tex
            TextureCompression.Bc3 => 5,    // TextureType.Bc3Tex
            TextureCompression.Bc5 => 14,   // TextureType.Bc5Tex
            _                      => 3,    // TextureType.RgbaTex
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Asking Penumbra happens on the framework thread; the work it starts does not, and the
            // task that comes back completes when the conversion itself has.
            var work = StudioServices.Framework.RunOnTick(
                () => _ipc.ConvertTextureFile(sourcePath, outputPath, type) ?? System.Threading.Tasks.Task.CompletedTask);

            if (!work.Wait((int)Timeout.TotalMilliseconds, ct))
                return "Penumbra took too long over it";

            return File.Exists(outputPath) ? null : "Penumbra wrote no file";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {(ex.InnerException ?? ex).Message}";
        }
    }

    /// <summary>
    /// The conversion as <see cref="InstantEdit.Services.TextureCompression.TexConvert"/> takes it, for
    /// Auto's encode-and-check: any of Penumbra's types, with or without mipmaps. Throws when it fails.
    /// </summary>
    public async System.Threading.Tasks.Task ConvertChecked(string input, string output, Penumbra.Api.Enums.TextureType type, bool mipMaps)
    {
        var work = StudioServices.Framework.RunOnTick(() =>
            _ipc.ConvertTextureFile(input, output, (int)type, mipMaps)
            ?? System.Threading.Tasks.Task.FromException(new IOException("Penumbra could not convert the texture.")));
        if (await System.Threading.Tasks.Task.WhenAny(work, System.Threading.Tasks.Task.Delay(Timeout)).ConfigureAwait(false) != work)
            throw new TimeoutException("Penumbra took too long to convert the texture.");
        await work.ConfigureAwait(false);
    }
}
