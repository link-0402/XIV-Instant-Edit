using InstantEdit.Models;

namespace InstantEdit.Services.CharacterWeight;

/// <summary> The format a shrunk texture is saved in; Keep saves it in its current one. </summary>
internal enum ShrinkFormat : byte
{
    Keep,
    /// <summary> Four channels, a quarter of uncompressed. </summary>
    Bc7,
    /// <summary> Red and green only, for index maps. </summary>
    Bc5,
}

/// <summary> How to shrink one texture: its new format and how many times to halve its width and height. </summary>
internal readonly record struct ShrinkChoice(ShrinkFormat Format, int Halvings)
{
    public static ShrinkChoice None => default;
    public bool IsNone => Format == ShrinkFormat.Keep && Halvings == 0;
}

/// <summary> Texture memory after a plan: the sync plugins' count and the video memory of everything rendered. </summary>
internal readonly record struct WeightPlanTotals(int Files, long SyncVram, long AllVram);

/// <summary>
/// What a texture may be turned into. The formats follow Mod Optimizer's Auto rule so both plugins
/// agree: BC5 for index maps, BC7 for other textures. BC5 keeps only red and green, which is all
/// an index map's shader reads (the game ships its own index maps as BC5); normal, mask and base
/// maps keep data in blue and alpha, so they get BC7. Sizes shrink by halving, like Mod Optimizer's
/// size cap. Every conversion writes a full mip chain. Dalamud-free.
/// </summary>
internal static class ShrinkRules
{
    /// <summary> Textures above this many pixels on a side are flagged and halved by default. </summary>
    public const int SizeCap = 4096;
    /// <summary> Halving stops before a side gets shorter than this. </summary>
    public const int MinSide = 64;
    public const int MaxHalvings = 4;

    /// <summary> Why the texture can't be changed here, or null when it can. </summary>
    public static string? Blocker(WeightTexture texture)
    {
        if (texture.InPreview)
            return "It is in a preview mod. Apply or discard that preview first.";
        if (texture.Error.Length > 0 || texture.Info is not { } info)
            return texture.Error.Length > 0 ? texture.Error : "Its header couldn't be read.";
        if (!info.Is2D)
            return "Only ordinary 2D textures are changed here.";
        if (!texture.Source.IsModFile && texture.Source.State != ResourceSourceState.GameData)
            return "It isn't a file of a mod or of the game.";
        if (texture.GamePaths.Count == 0 || texture.GamePaths.Any(path => !PenumbraService.IsSafeGameResourcePath(path, ".tex")))
            return "Penumbra didn't report a game path a mod can replace.";
        if (Formats(texture).Count == 0)
            return $"{TextureCost.FormatName(info.Format)} textures stay as they are.";
        return null;
    }

    /// <summary> The formats offered for the texture. </summary>
    public static IReadOnlyList<ShrinkFormat> Formats(WeightTexture texture)
    {
        if (texture.Info is not { } info)
            return [];
        var formats = new List<ShrinkFormat>(3);
        if (CanKeep(info.Format))
            formats.Add(ShrinkFormat.Keep);
        if (TextureCost.IsCompressibleColour(info.Format))
        {
            formats.Add(ShrinkFormat.Bc7);
            if (texture.IndexOnly)
                formats.Add(ShrinkFormat.Bc5);
        }
        return formats;
    }

    /// <summary> Formats Penumbra can write back as they are: the block-compressed ones textures use and BGRA32. </summary>
    private static bool CanKeep(uint format)
        => format is TextureCost.Bc1 or TextureCost.Bc3 or TextureCost.Bc4 or TextureCost.Bc5 or TextureCost.Bc7 or TextureCost.Bgra8;

    public static uint TargetFormat(WeightTexture texture, ShrinkFormat format) => format switch
    {
        ShrinkFormat.Bc7 => TextureCost.Bc7,
        ShrinkFormat.Bc5 => TextureCost.Bc5,
        _ => texture.Info?.Format ?? 0,
    };

    public static (int Width, int Height) SizeAfter(TexInfo info, int halvings) => (info.Width >> halvings, info.Height >> halvings);

    /// <summary>
    /// Whether the texture can be saved this way: a format it offers, halvings that divide its size
    /// evenly and leave at least <see cref="MinSide"/> pixels, and whole 4 × 4 tiles for block compression.
    /// </summary>
    public static bool IsValid(WeightTexture texture, ShrinkChoice choice)
    {
        if (texture.Info is not { } info || choice.Halvings is < 0 or > MaxHalvings || Blocker(texture) is not null ||
            !Formats(texture).Contains(choice.Format))
            return false;
        var step = 1 << choice.Halvings;
        if (info.Width % step != 0 || info.Height % step != 0)
            return false;
        var (width, height) = SizeAfter(info, choice.Halvings);
        if (choice.Halvings > 0 && Math.Min(width, height) < MinSide)
            return false;
        return !TextureCost.IsBlockCompressed(TargetFormat(texture, choice.Format)) || (width % 4 == 0 && height % 4 == 0);
    }

    /// <summary> The halvings offered in a format, starting with 0 when the size can stay. </summary>
    public static IReadOnlyList<int> Halvings(WeightTexture texture, ShrinkFormat format)
        => Enumerable.Range(0, MaxHalvings + 1).Where(halvings => IsValid(texture, new ShrinkChoice(format, halvings))).ToList();

    /// <summary>
    /// The preselected change: uncompressed colour textures get compressed (BC5 for index maps, BC7
    /// otherwise), and textures above <see cref="SizeCap"/> are halved until they fit. Others stay.
    /// </summary>
    public static ShrinkChoice Default(WeightTexture texture)
    {
        if (texture.Info is not { } info || Blocker(texture) is not null)
            return ShrinkChoice.None;
        var format = TextureCost.IsCompressibleColour(info.Format) ? texture.IndexOnly ? ShrinkFormat.Bc5 : ShrinkFormat.Bc7 : ShrinkFormat.Keep;
        var halvings = 0;
        while (Math.Max(info.Width >> halvings, info.Height >> halvings) > SizeCap && IsValid(texture, new ShrinkChoice(format, halvings + 1)))
            halvings++;
        var choice = new ShrinkChoice(format, halvings);
        return !choice.IsNone && IsValid(texture, choice) ? choice : ShrinkChoice.None;
    }

    /// <summary>
    /// The change a texture gets when it is ticked: the default, or else one halving in its own
    /// format, or else the first change it allows. None when it allows none.
    /// </summary>
    public static ShrinkChoice Suggested(WeightTexture texture)
    {
        var choice = Default(texture);
        if (!choice.IsNone)
            return choice;
        if (IsValid(texture, new ShrinkChoice(ShrinkFormat.Keep, 1)))
            return new ShrinkChoice(ShrinkFormat.Keep, 1);
        foreach (var format in Formats(texture))
            foreach (var halvings in Halvings(texture, format))
                if (halvings > 0 || format != ShrinkFormat.Keep)
                    return new ShrinkChoice(format, halvings);
        return ShrinkChoice.None;
    }

    /// <summary> The choice in another format, keeping its halvings when that format allows them, or else the nearest it does. </summary>
    public static ShrinkChoice WithFormat(WeightTexture texture, ShrinkChoice choice, ShrinkFormat format)
    {
        var halvings = Halvings(texture, format);
        if (halvings.Count == 0)
            return choice;
        return new ShrinkChoice(format, halvings.OrderBy(h => Math.Abs(h - choice.Halvings)).ThenByDescending(h => h).First());
    }

    /// <summary> Video memory after the change, with the full mip chain every conversion writes. </summary>
    public static long VramAfter(WeightTexture texture, ShrinkChoice choice)
    {
        if (choice.IsNone || texture.Info is not { } info)
            return texture.Vram;
        var (width, height) = SizeAfter(info, choice.Halvings);
        return TextureCost.Vram(TargetFormat(texture, choice.Format), width, height, TextureFiles.FullMipCount(width, height));
    }

    /// <summary> The file size after the change, which is what sync plugins count. </summary>
    public static long FileAfter(WeightTexture texture, ShrinkChoice choice)
        => choice.IsNone ? texture.FileLength : TextureCost.HeaderSize + VramAfter(texture, choice);

    /// <summary>
    /// Totals with the chosen textures changed. A changed game file moves into a mod, so sync plugins
    /// start counting it; files with the same content and the same change still count once.
    /// </summary>
    public static WeightPlanTotals Totals(CharacterWeightReport report, IReadOnlyDictionary<string, ShrinkChoice> choices)
    {
        var sync = new List<(string Content, long Length)>();
        long all = 0;
        var files = 0;
        foreach (var texture in report.Textures)
        {
            var choice = choices.TryGetValue(texture.Key, out var chosen) && !chosen.IsNone && IsValid(texture, chosen) ? chosen : ShrinkChoice.None;
            all += VramAfter(texture, choice);
            if (!choice.IsNone)
            {
                files++;
                sync.Add(($"{texture.Source.Sha256}|{choice.Format}|{choice.Halvings}", FileAfter(texture, choice)));
            }
            else if (texture.CountsForSync)
                sync.Add((texture.Source.Sha256, texture.FileLength));
        }
        return new WeightPlanTotals(files, CharacterWeightCapture.SyncVram(sync), all);
    }
}
