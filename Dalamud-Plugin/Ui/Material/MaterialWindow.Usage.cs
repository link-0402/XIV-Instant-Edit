using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using InstantEdit.Services.Materials;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui.Material;

/// <summary>
/// Which row pairs the index texture uses: read at full size off the draw thread whenever the index
/// texture changes, so unused pairs can be greyed and a pair's texels shown.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const int WhereSize = 256;

    private Task<IndexUsage?>? usageTask;
    private string? usageFor;
    private IndexUsage? usage;
    private string usageNote = "No index texture.";
    private bool showWhere;
    private (IndexUsage Usage, int Pair)? whereFor;
    private (IndexUsage Usage, int Pair)? whereBuilding;
    private Task<IDalamudTextureWrap>? whereTask;
    private IDalamudTextureWrap? whereWrap;

    /// <summary>Starts reading the index texture when the one the material points at changed.</summary>
    private void RefreshUsage(MaterialDocument doc)
    {
        var path = doc.Slots.FirstOrDefault(slot => slot.SamplerId == IndexSampler && slot.Path.Length > 0)?.Path;
        var file = path != null && resolvedTextures.TryGetValue(path, out var resolved) ? resolved : null;
        var kind = doc.Table?.Kind ?? ColorTableKind.None;
        var key = file == null || kind is not (ColorTableKind.Dawntrail or ColorTableKind.Legacy) ? null : $"{kind}|{file}";
        if (key == usageFor || usageTask != null)
            return;
        usageFor = key;
        if (key == null)
        {
            usage = null;
            usageNote = path == null ? "No index texture." : "The index texture was not found.";
            return;
        }
        usageTask = Task.Run(async () =>
        {
            var bytes = Path.IsPathRooted(file)
                ? await File.ReadAllBytesAsync(file!, life.Token).ConfigureAwait(false)
                : (await data.GetFileAsync<Lumina.Data.FileResource>(file!, life.Token).ConfigureAwait(false))?.Data;
            if (bytes == null)
                return null;
            return IndexUsageCalc.Compute(TextureDecoder.Decode(bytes, int.MaxValue), kind);
        });
    }

    private void PollUsage()
    {
        if (usageTask is not { IsCompleted: true } done)
            return;
        usageTask = null;
        if (done.IsCompletedSuccessfully && done.Result is { } read)
        {
            usage = read;
            usageNote = string.Empty;
        }
        else
        {
            usage = null;
            usageNote = "The index texture could not be read.";
            if (done.Exception is { } error)
                log.Debug(error, "Could not read the material's index texture.");
        }
        if (session != null)
            RefreshUsage(session.Current);
    }

    /// <summary>What the pair's swatch says about its use, for the tooltip.</summary>
    private string UsageText(int pair)
        => usage == null ? usageNote
            : usage.PairUsed(pair) ? $"Used by {usage.PairShare(pair) * 100:0.#}% of the index texture."
            : "Not used by the index texture.";

    /// <summary>The index texture with the selected pair's texels lit (top row yellow, bottom row blue).</summary>
    private void DrawWhere()
    {
        if (usage == null)
            return;
        ImGui.Checkbox("Show where##rows-where", ref showWhere);
        Tip("Shows which part of the index texture uses this pair: top row yellow, bottom row blue.");
        if (!showWhere)
            return;
        if (whereFor is not { } shown || !ReferenceEquals(shown.Usage, usage) || shown.Pair != selectedPair)
            whereFor = (usage, selectedPair);
        if (whereTask is { IsCompleted: true } built)
        {
            whereTask = null;
            whereWrap?.Dispose();
            whereWrap = built.IsCompletedSuccessfully ? built.Result : null;
        }
        if (whereTask == null && whereBuilding != whereFor)
            BuildWhere(usage, selectedPair);
        if (whereWrap != null)
            Widgets.FittedImage(whereWrap, new Vector2(Theme.Scaled(WhereSize)));
        else
            Widgets.Spinner();
    }

    private void BuildWhere(IndexUsage read, int pair)
    {
        whereBuilding = (read, pair);
        whereTask = Task.Run(() =>
        {
            var pixels = new byte[read.Width * read.Height * 4];
            for (var i = 0; i < read.RowOfTexel.Length; i++)
            {
                var row = read.RowOfTexel[i];
                var (b, g, r) = row == pair * 2 ? ((byte)40, (byte)215, (byte)255) : row == pair * 2 + 1 ? ((byte)255, (byte)190, (byte)50) : ((byte)45, (byte)40, (byte)40);
                pixels[i * 4] = b;
                pixels[i * 4 + 1] = g;
                pixels[i * 4 + 2] = r;
                pixels[i * 4 + 3] = 255;
            }
            var (small, width, height) = TextureDecoder.Downscale(pixels, read.Width, read.Height, WhereSize);
            return textures.CreateFromRawAsync(RawImageSpecification.Bgra32(width, height), small, "InstantEdit.Material.Where");
        });
    }

    private void ReleaseUsage()
    {
        whereWrap?.Dispose();
        whereWrap = null;
        whereFor = null;
        whereBuilding = null;
        usage = null;
        usageFor = null;
    }
}
