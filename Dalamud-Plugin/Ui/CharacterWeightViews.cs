using System.Globalization;
using InstantEdit.Services;
using InstantEdit.Services.CharacterWeight;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Ui;

/// <summary> How a total compares with the sync plugins' default limits. </summary>
internal enum WeightLevel
{
    Below,
    Warning,
    AutoPause,
}

/// <summary> Text for the character weight card and dialog. No ImGui or Dalamud dependencies. </summary>
internal static class CharacterWeightViews
{
    /// <summary> "612.35 MiB": binary units with two decimals, as the sync plugins print sizes. </summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (unit < units.Length - 1 && Math.Abs(value) >= 1024)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? bytes.ToString(CultureInfo.InvariantCulture) + " B" : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Size(int width, int height) => $"{width} × {height}";

    /// <summary> Above a limit means strictly greater, as the sync plugins compare. </summary>
    public static WeightLevel Level(long value, long warning, long autoPause)
        => value > autoPause ? WeightLevel.AutoPause : value > warning ? WeightLevel.Warning : WeightLevel.Below;

    public static WeightLevel VramLevel(long bytes) => Level(bytes, SyncThresholds.WarningVram, SyncThresholds.AutoPauseVram);

    public static WeightLevel TriangleLevel(long triangles) => Level(triangles, SyncThresholds.WarningTriangles, SyncThresholds.AutoPauseTriangles);

    public static string VramStatus(long bytes) => VramLevel(bytes) switch
    {
        WeightLevel.AutoPause => $"Above the {Bytes(SyncThresholds.AutoPauseVram)} auto-pause default",
        WeightLevel.Warning => $"Above the {Bytes(SyncThresholds.WarningVram)} warning default",
        _ => $"Below the {Bytes(SyncThresholds.WarningVram)} warning default",
    };

    public static string TriangleStatus(long triangles) => TriangleLevel(triangles) switch
    {
        WeightLevel.AutoPause => $"Above the {Count(SyncThresholds.AutoPauseTriangles)} auto-pause default",
        WeightLevel.Warning => $"Above the {Count(SyncThresholds.WarningTriangles)} warning default",
        _ => $"Below the {Count(SyncThresholds.WarningTriangles)} warning default",
    };

    public static FeedbackSeverity Feedback(WeightLevel level) => level switch
    {
        WeightLevel.AutoPause => FeedbackSeverity.Error,
        WeightLevel.Warning => FeedbackSeverity.Warning,
        _ => FeedbackSeverity.Success,
    };

    /// <summary> The card's one-line result: "612.35 MiB · 212,301 triangles". </summary>
    public static string Totals(CharacterWeightReport report) => $"{Bytes(report.SyncVram)} · {Count(report.SyncTriangles)} triangles";

    /// <summary> What else the dialog knows about the texture: "uncompressed · no mipmaps". </summary>
    public static string Notes(WeightTexture texture)
    {
        var notes = new List<string>();
        if (texture.Error.Length > 0)
            notes.Add("unreadable");
        if (texture.Uncompressed)
            notes.Add("uncompressed");
        if (texture.Oversized)
            notes.Add($"over {ShrinkRules.SizeCap}");
        if (texture.NoMips)
            notes.Add("no mipmaps");
        if (texture.InPreview)
            notes.Add("preview");
        if (texture.Source.State == Models.ResourceSourceState.GameData)
            notes.Add("game file");
        return string.Join(" · ", notes);
    }

    /// <summary> The tooltip line for each note. </summary>
    public static IReadOnlyList<string> NoteDetails(WeightTexture texture)
    {
        var lines = new List<string>();
        if (texture.Uncompressed)
            lines.Add("Uncompressed: it takes four times the memory of BC7. Compressing it is the biggest saving.");
        if (texture.Oversized)
            lines.Add($"Over {ShrinkRules.SizeCap} pixels on a side: more detail than the game shows on a character.");
        if (texture.NoMips)
            lines.Add("No mipmaps: the smaller copies the game draws at a distance are missing, so it shimmers far away and costs more to draw. Converting it adds them (a third more memory).");
        if (texture.Source.State == Models.ResourceSourceState.GameData)
            lines.Add("Game file: sync plugins don't count it, since other players already have it. Changing it puts it in a mod, which they then count.");
        if (texture.InPreview)
            lines.Add("In a preview mod: apply or discard that preview first.");
        return lines;
    }

    public static string FormatLabel(WeightTexture texture, ShrinkFormat format) => format switch
    {
        ShrinkFormat.Bc7 => "BC7",
        ShrinkFormat.Bc5 => "BC5 (red and green)",
        _ => texture.Info is { } info ? $"Keep {TextureCost.FormatName(info.Format)}" : "Keep",
    };

    public static string SizeLabel(WeightTexture texture, int halvings)
    {
        if (texture.Info is not { } info)
            return "";
        var (width, height) = ShrinkRules.SizeAfter(info, halvings);
        return halvings == 0 ? $"Keep {Size(width, height)}" : Size(width, height);
    }

    /// <summary> "c0201b0001_b_d.tex: BGRA32 4096 × 4096 → BC7 (85.33 → 21.33 MiB)". </summary>
    public static string Change(WeightTexture texture, ShrinkChoice choice)
    {
        if (texture.Info is not { } info)
            return texture.FileName;
        var target = ShrinkRules.TargetFormat(texture, choice.Format);
        var (width, height) = ShrinkRules.SizeAfter(info, choice.Halvings);
        var after = TextureCost.FormatName(target) + (choice.Halvings > 0 ? " " + Size(width, height) : "");
        return $"{texture.FileName}: {TextureCost.FormatName(info.Format)} {Size(info.Width, info.Height)} → {after} " +
               $"({Bytes(texture.Vram)} → {Bytes(ShrinkRules.VramAfter(texture, choice))})";
    }

    /// <summary> "used by Default and Top: Red" or "used by 4 options"; notes files mapped from several game paths. </summary>
    public static string Usage(ModFileUsage usage)
    {
        var options = usage.Options.Count switch
        {
            0 => "",
            1 => "used by " + usage.Options[0],
            2 or 3 => "used by " + string.Join(", ", usage.Options.Take(usage.Options.Count - 1)) + " and " + usage.Options[^1],
            _ => $"used by {usage.Options.Count} options",
        };
        if (!usage.SeveralGamePaths)
            return options;
        return options.Length == 0 ? "mapped to several game paths" : options + ", mapped to several game paths";
    }

    /// <summary> What applying the preview writes, one line per file. </summary>
    public static IReadOnlyList<string> ApplyLines(PreviewMod preview)
        => preview.Files.Select(file => file.Source.IsModFile
                ? $"Overwrite {file.Source.ModName}: {file.Source.RelativePath}" + (file.Note.Length > 0 ? $" ({file.Note})" : "")
                : $"Put the {file.Kind} for {file.GamePath} in a new mod (it is game data)")
            .ToList();

    public static string PreviewLine(PreviewMod preview)
        => $"Preview mod \"{preview.ModDirectory}\" is enabled in {preview.CollectionName}. Look at your character in game, then apply the smaller textures to your mods or discard them.";

    /// <summary> The planned change in one line: "12 textures ticked: sync plugins would count 612.35 MiB → 221.40 MiB" and the full total. </summary>
    public static string Plan(CharacterWeightReport report, WeightPlanTotals plan)
        => plan.Files == 0
            ? "No texture is ticked."
            : $"{plan.Files} texture{(plan.Files == 1 ? "" : "s")} ticked: sync plugins would count {Bytes(report.SyncVram)} → {Bytes(plan.SyncVram)}, " +
              $"everything rendered {Bytes(report.AllVram)} → {Bytes(plan.AllVram)}.";
}
