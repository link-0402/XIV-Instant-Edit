using Penumbra.Api.Enums;

namespace InstantEdit.Services.TextureCompression;

/// <summary>Has Penumbra convert a TEX file into another type, with or without a mip chain.</summary>
internal delegate Task TexConvert(string input, string output, TextureType type, bool mipMaps);

internal enum VerifiedEncodeOutcome
{
    /// <summary>Encoded, and the check passed: <see cref="VerifiedEncodeResult.Encoded"/> may replace the original.</summary>
    Encoded,

    /// <summary>Encoded, but the check found it changes what the texture's shaders read.</summary>
    FailedCheck,

    /// <summary>Nothing to do: an already compressed texture that isn't one colour, with nothing else asked for.</summary>
    NotCompressible,
}

/// <param name="Source">The original TEX, its mip offsets normalized.</param>
/// <param name="Target">The TEX format to encode to (<see cref="TextureCost.Bc7"/> …), or null for Auto: BC5 when every use is an index map, else BC7.</param>
/// <param name="MaxSide">Halve the texture until both sides fit (16 at least), or null to keep its size.</param>
/// <param name="AllowSingleColor">Shrink a texture of one colour to 16 × 16 when the check allows it.</param>
internal sealed record VerifiedEncodeRequest(byte[] Source, TexInfo Info, IReadOnlyList<TextureUse> Uses, uint? Target, int? MaxSide, bool AllowSingleColor);

/// <param name="Width">The size written: the original's, the capped one, or <see cref="SingleColor.Size"/>.</param>
/// <param name="RetriedAtFullSize">It held nearly one colour, but shrinking it failed the check, so it was encoded at its size.</param>
internal sealed record VerifiedEncodeResult(VerifiedEncodeOutcome Outcome, byte[]? Encoded, CompressionCheckResult? Check, bool OneColor,
    int Width, int Height, uint Target, bool RetriedAtFullSize);

/// <summary>
/// Encodes one texture through Penumbra and checks the result before anyone writes it: a texture
/// of one colour can become a 16 × 16 one, a size cap halves it first, Penumbra encodes and decodes
/// it, and <see cref="CompressionCheck"/> compares what its shaders read against the original (or
/// the original halved to the same size). Shared by automatic texture optimization and a mod's
/// texture clean-up. Dalamud-free; files go through <paramref name="work"/>, which the caller owns.
/// </summary>
internal static class VerifiedEncode
{
    public const int MinSide = 16;

    public static async Task<VerifiedEncodeResult> RunAsync(VerifiedEncodeRequest request, TexConvert convert, string work, CancellationToken token)
    {
        var info = request.Info;
        var source = request.Source;
        var target = request.Target ?? (request.Uses.Count > 0 && request.Uses.All(use => use.Role == Ui.TextureRole.Index) ? TextureCost.Bc5 : TextureCost.Bc7);
        var twoChannel = target == TextureCost.Bc5;
        // Mipmaps only when the original has them: a texture drawn without them keeps its look far away.
        var mipMaps = (source[14] & 0x7F) > 1;
        var (cappedWidth, cappedHeight) = request.MaxSide is { } maxSide ? Restricted(info.Width, info.Height, Math.Max(MinSide, maxSide)) : (info.Width, info.Height);
        var resizing = cappedWidth != info.Width || cappedHeight != info.Height;
        // An explicit target or a size cap is a request to encode; Auto alone only takes what block compression fits.
        var canEncodeNormally = resizing || request.Target is not null || CompressionCapture.Compressible(info);

        var input = Path.Combine(work, "original.tex");
        var output = Path.Combine(work, "compressed.tex");
        var check = Path.Combine(work, "check.tex");
        await File.WriteAllBytesAsync(input, source, token).ConfigureAwait(false);

        ArraySegment<byte> originalPixels = default;
        // The colours the game samples from the original: its own pixels when they are BGRA32, else Penumbra's decode.
        async Task<ArraySegment<byte>> OriginalPixelsAsync()
        {
            if (info.Format == TextureCost.Bgra8)
                return TextureCost.TopLevelBgra(source).Pixels;
            var plain = Path.Combine(work, "plain.tex");
            await convert(input, plain, TextureType.RgbaTex, mipMaps).ConfigureAwait(false);
            return TextureCost.TopLevelBgra(await File.ReadAllBytesAsync(plain, token).ConfigureAwait(false)).Pixels;
        }

        // What the result is compared against: the original, or the original halved to the cap.
        ArraySegment<byte> reference = default;
        async Task<ArraySegment<byte>> ReferenceAsync()
        {
            if (reference.Array is not null)
                return reference;
            if (originalPixels.Array is null)
                originalPixels = await OriginalPixelsAsync().ConfigureAwait(false);
            if (!resizing)
                return reference = originalPixels;
            var pixels = originalPixels.ToArray();
            int width = info.Width, height = info.Height;
            while (width > cappedWidth || height > cappedHeight)
            {
                pixels = Halve(pixels, width, height);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            return reference = pixels;
        }

        (byte B, byte G, byte R, byte A)? color = null;
        var retried = false;
        int outWidth = cappedWidth, outHeight = cappedHeight;
        byte[] encoded;
        CompressionCheckResult result;
        while (true)
        {
            if (request.AllowSingleColor && originalPixels.Array is null && !retried && SingleColor.MayHoldOneColor(new MemoryStream(source, false), info))
            {
                originalPixels = await OriginalPixelsAsync().ConfigureAwait(false);
                color = SingleColor.ColorOf(originalPixels, info.Width, info.Height, twoChannel);
            }
            if (color is null && !canEncodeNormally)
                return new VerifiedEncodeResult(VerifiedEncodeOutcome.NotCompressible, null, null, false, info.Width, info.Height, target, false);

            var encode = input;
            if (color is { } one)
            {
                encode = Path.Combine(work, "single-color.tex");
                await File.WriteAllBytesAsync(encode, SingleColor.Tex(one), token).ConfigureAwait(false);
                outWidth = outHeight = SingleColor.Size;
            }
            else if (resizing)
            {
                encode = Path.Combine(work, "resized.tex");
                await File.WriteAllBytesAsync(encode, TextureFiles.BgraTex(await ReferenceAsync().ConfigureAwait(false), cappedWidth, cappedHeight), token).ConfigureAwait(false);
            }
            await convert(encode, output, TextureFiles.OutputType(target), mipMaps).ConfigureAwait(false);
            encoded = await File.ReadAllBytesAsync(output, token).ConfigureAwait(false);
            TextureFiles.ValidateOutput(encoded, target, outWidth, outHeight, mipMaps);
            await convert(output, check, TextureType.RgbaTex, mipMaps).ConfigureAwait(false);
            var decoded = TextureCost.TopLevelBgra(await File.ReadAllBytesAsync(check, token).ConfigureAwait(false));
            if (decoded.Width != outWidth || decoded.Height != outHeight)
                throw new InvalidDataException("Penumbra decoded the compressed texture at another size.");

            var compared = await ReferenceAsync().ConfigureAwait(false);
            // A shrunk texture is compared as the game samples it, stretched over the reference's pixels.
            result = color is null
                ? CompressionCheck.Compare(compared, decoded.Pixels, cappedWidth, cappedHeight, request.Uses, twoChannel)
                : CompressionCheck.Compare(compared, SingleColor.Stretch(decoded.Pixels, outWidth, outHeight, cappedWidth, cappedHeight), cappedWidth, cappedHeight,
                    request.Uses, twoChannel, SingleColor.CheckTolerances);
            // An uncompressed texture the stricter one-colour check keeps may still compress at its own size.
            if (result.Passed || color is null || !canEncodeNormally)
                break;
            color = null;
            retried = true;
            outWidth = cappedWidth;
            outHeight = cappedHeight;
        }

        return result.Passed
            ? new VerifiedEncodeResult(VerifiedEncodeOutcome.Encoded, encoded, result, color is not null, outWidth, outHeight, target, retried)
            : new VerifiedEncodeResult(VerifiedEncodeOutcome.FailedCheck, null, result, color is not null, outWidth, outHeight, target, retried);
    }

    /// <summary>A size halved until both sides fit under <paramref name="limit"/>.</summary>
    public static (int Width, int Height) Restricted(int width, int height, int limit)
    {
        while (width > limit || height > limit)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return (width, height);
    }

    /// <summary>A BGRA32 image at half its size, each pixel the average of the 2 × 2 it covers.</summary>
    public static byte[] Halve(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var c = 0; c < 4; c++)
        {
            int x0 = Math.Min(x * 2, width - 1), x1 = Math.Min(x * 2 + 1, width - 1);
            int y0 = Math.Min(y * 2, height - 1), y1 = Math.Min(y * 2 + 1, height - 1);
            var sum = bgra[(y0 * width + x0) * 4 + c] + bgra[(y0 * width + x1) * 4 + c] + bgra[(y1 * width + x0) * 4 + c] + bgra[(y1 * width + x1) * 4 + c];
            result[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
        }
        return result;
    }
}
