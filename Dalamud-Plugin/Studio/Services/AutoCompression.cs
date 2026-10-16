using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Services;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;
using PenumbraTextureType = Penumbra.Api.Enums.TextureType;
using TextureType = InstantEdit.Studio.Models.TextureType;

namespace InstantEdit.Studio.Services;

/// <summary>What Auto wrote a texture as: the .tex, its format, and why it stayed uncompressed (empty when it was compressed).</summary>
internal sealed record AutoCompressionResult(byte[] Tex, TextureCompression Format, string Kept);

/// <summary>
/// Picks a new texture's format the way Optimize textures does: Penumbra encodes the image (BC5 for
/// an index map, BC7 otherwise) and <see cref="CompressionCheck"/> compares what the texture's
/// shaders read against the uncompressed image. An encode that passes is used; one that would change
/// how the texture looks leaves it uncompressed. Dalamud-free; Penumbra comes in through <see cref="TexConvert"/>.
/// </summary>
internal static class AutoCompression
{
    /// <summary>
    /// The cut-out threshold assumed when the material is not known yet: an opacity channel is then
    /// checked as a cut-out at half, which catches lost lace or fishnet a real threshold would show.
    /// </summary>
    private const float AssumedAlphaThreshold = 0.5f;

    /// <summary>How a material of <paramref name="shader"/> reads a slot's texture: from its template material when there is one.</summary>
    public static IReadOnlyList<TextureUse> UsesFor(TextureType type, ShaderType shader, MtrlInfo? material)
    {
        var package = material is { ShaderPackageName.Length: > 0 } ? material.ShaderPackageName : ShaderInfo.ShaderPackName(shader);
        if (material is not { ShaderDataParsed: true })
            return Unknown(RoleOf(type), package);
        var (threshold, translucent) = Transparency(material);
        return new[] { new TextureUse { Role = RoleOf(type), ShaderPackage = package, AlphaThreshold = threshold, Translucent = translucent } };
    }

    /// <summary>
    /// How an image replacing the texture at <paramref name="gamePath"/> is read, when its materials
    /// are not looked at: the role from its path, the shader its kind of model uses. A face's
    /// <c>_etc_</c> textures belong to its brows and lashes, which hair.shpk draws.
    /// </summary>
    public static IReadOnlyList<TextureUse> UsesFor(string gamePath, string sourcePath)
    {
        var path = PathNormalizer.GamePath(gamePath);
        var role = TextureRoleClassifier.Classify(InstantEdit.Ui.ResourceKinds.Texture, null, path, sourcePath);
        var package = path.Contains("/obj/hair/") || path.Contains("/obj/tail/") || path.Contains("/obj/zear/") ? "hair.shpk"
            : path.Contains("/obj/face/") && Path.GetFileName(path).Contains("_etc_") ? "hair.shpk"
            : path.Contains("/obj/body/") || path.Contains("/obj/face/") ? "skin.shpk"
            : "character.shpk";
        return Unknown(role, package);
    }

    /// <summary>
    /// A texture whose material isn't read: any opacity channel is checked both as a cut-out at
    /// <see cref="AssumedAlphaThreshold"/> and as blended at it, since either may be what draws it.
    /// </summary>
    private static IReadOnlyList<TextureUse> Unknown(TextureRole role, string package) => new[]
    {
        new TextureUse { Role = role, ShaderPackage = package, AlphaThreshold = AssumedAlphaThreshold },
        new TextureUse { Role = role, ShaderPackage = package, AlphaThreshold = AssumedAlphaThreshold, Translucent = true },
    };

    /// <summary>What a set of uses is cached and planned under: a change to any of them, or to the check, encodes the texture again.</summary>
    public static string KeyOf(IReadOnlyList<TextureUse> uses)
        => $"check{CompressionCheck.Version}:" + string.Join(";", uses.Select(u => $"{u.Role}/{u.ShaderPackage}/{u.AlphaThreshold:R}/{u.Translucent}"));

    /// <summary>
    /// Has Penumbra convert the image to an uncompressed .tex with a mip chain, then encodes and checks
    /// that. Throws when Penumbra can't convert it; the caller decides what to write then.
    /// </summary>
    public static AutoCompressionResult Encode(string input, IReadOnlyList<TextureUse> uses, TexConvert convert, CancellationToken token)
    {
        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "auto-compression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Task? inFlight = null;
        // Each conversion stops waiting when the token does; Penumbra's own work runs on.
        var penumbra = convert;
        TexConvert watched = (source, output, type, mipMaps) =>
        {
            var task = penumbra(source, output, type, mipMaps);
            inFlight = task;
            return task.WaitAsync(token);
        };
        try
        {
            var plainPath = Path.Combine(work, "source.tex");
            watched(input, plainPath, PenumbraTextureType.RgbaTex, true).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            var plain = TextureFiles.NormalizeMipOffsets(File.ReadAllBytes(plainPath));
            var info = TextureCost.Read(plain) ?? throw new InvalidDataException("Penumbra wrote a texture this plugin can't read.");
            var result = VerifiedEncode.RunAsync(new VerifiedEncodeRequest(plain, info, uses, null, null, AllowSingleColor: false), watched, work, token)
                .GetAwaiter().GetResult();
            return result.Outcome switch
            {
                VerifiedEncodeOutcome.Encoded     => new(result.Encoded!, result.Target == TextureCost.Bc5 ? TextureCompression.Bc5 : TextureCompression.Bc7, string.Empty),
                VerifiedEncodeOutcome.FailedCheck => new(plain, TextureCompression.None, result.Check!.Problem),
                _                                 => new(plain, TextureCompression.None, NotCompressibleReason(info)),
            };
        }
        finally
        {
            // A conversion a stop left running still writes into the folder: delete it once that ends.
            if (inFlight is { IsCompleted: false } pending)
                pending.ContinueWith(_ => DeleteWork(work), TaskScheduler.Default);
            else
                DeleteWork(work);
        }
    }

    private static void DeleteWork(string work)
    {
        try { Directory.Delete(work, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder that outlives us is harmless */ }
    }

    /// <summary>Why a texture Penumbra could not compress stayed uncompressed.</summary>
    private static string NotCompressibleReason(TexInfo info)
        => Math.Max(info.Width, info.Height) > TextureFiles.MaxDimension
            ? $"it is larger than {TextureFiles.MaxDimension} pixels"
            : "it is too small or an odd size to compress";

    /// <summary>
    /// Why a cached Auto texture of this header stayed uncompressed: what the first encode said, as
    /// far as the file tells it (a texture that could be compressed failed the check).
    /// </summary>
    public static string KeptReason(ReadOnlySpan<byte> head)
        => TextureCost.Read(head) is not { } info || FormatOf(head) != TextureCompression.None ? string.Empty
            : CompressionCapture.Compressible(info) ? "compressing it would change how it looks"
            : NotCompressibleReason(info);

    /// <summary>The format a .tex was written in, from its header: what a cached Auto texture came out as.</summary>
    public static TextureCompression FormatOf(ReadOnlySpan<byte> head)
        => TextureCost.Read(head)?.Format switch
        {
            TextureCost.Bc7 => TextureCompression.Bc7,
            TextureCost.Bc5 => TextureCompression.Bc5,
            _               => TextureCompression.None,
        };

    /// <summary>The report line for a texture Auto wrote.</summary>
    public static string Describe(string sourceName, TextureCompression format, string kept, string reused)
    {
        var how = format switch
        {
            TextureCompression.Bc7 => "BC7 (Auto)",
            TextureCompression.Bc5 => "BC5 (Auto)",
            _ when kept.Length > 0 => $"uncompressed (Auto: {kept})",
            _                      => "uncompressed (Auto)",
        };
        return $"{sourceName}, {how}{reused}";
    }

    private static (float Threshold, bool Translucent) Transparency(MtrlInfo material)
    {
        var threshold = 0f;
        if (material.Constants.FirstOrDefault(c => c.Id == TextureUse.AlphaThresholdConstant) is { Size: >= 4 } constant
            && constant.Offset + 4 <= material.ShaderValues.Length)
            threshold = BitConverter.ToSingle(material.ShaderValues, constant.Offset);
        return (threshold, (material.MaterialFlags1 & TextureUse.TranslucencyFlag) != 0);
    }

    private static TextureRole RoleOf(TextureType type) => type switch
    {
        TextureType.Diffuse => TextureRole.Base,
        TextureType.Normal  => TextureRole.Normal,
        TextureType.Mask    => TextureRole.Mask,
        TextureType.Index   => TextureRole.Index,
        _                   => TextureRole.Other,
    };
}
