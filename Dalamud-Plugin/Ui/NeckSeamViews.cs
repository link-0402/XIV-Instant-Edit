using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui;

/// <summary> Text for the neck seam dialog. No ImGui or Dalamud dependencies. </summary>
internal static class NeckSeamViews
{
    public static FeedbackSeverity Feedback(NeckSeamSeverity severity) => severity switch
    {
        NeckSeamSeverity.Problem => FeedbackSeverity.Error,
        NeckSeamSeverity.Warning => FeedbackSeverity.Warning,
        NeckSeamSeverity.Info => FeedbackSeverity.Info,
        _ => FeedbackSeverity.Success,
    };

    /// <summary> One line summing up the measured seam. </summary>
    public static string Summary(NeckSeamReport report)
    {
        var problems = report.Findings.Count(f => f.Severity == NeckSeamSeverity.Problem);
        var warnings = report.Findings.Count(f => f.Severity == NeckSeamSeverity.Warning);
        if (problems == 0 && warnings == 0)
            return "The face and body match at the neck.";
        var parts = new List<string>();
        if (problems > 0) parts.Add(problems == 1 ? "1 difference shows as a seam" : $"{problems} differences show as a seam");
        if (warnings > 0) parts.Add(warnings == 1 ? "1 smaller difference" : $"{warnings} smaller differences");
        return string.Join(", ", parts) + (report.AnyFix ? "." : "; none of them can be fixed here.");
    }

    /// <summary> Before → after of the measured seam values the fix changes. </summary>
    public static IReadOnlyList<string> Expected(NeckSeamFix fix)
    {
        var lines = new List<string>();
        if (fix.Before.Normal is { } before && fix.After.Normal is { } after)
            lines.Add($"Surface normal at the seam: {before.Mean:0.0}° → {after.Mean:0.0}° on average, {before.Max:0.0}° → {after.Max:0.0}° at most");
        void Pair(string label, NeckSeamAnalyzer.Pair? was, NeckSeamAnalyzer.Pair? now)
        {
            if (was is { } w && now is { } n && MathF.Abs(w.Face - n.Face) > 0.002f)
                lines.Add($"{label} (face / body): {w.Face:0.000} / {w.Body:0.000} → {n.Face:0.000} / {n.Body:0.000}");
        }
        Pair("Roughness", fix.Before.Roughness, fix.After.Roughness);
        Pair("Specular strength", fix.Before.Specular, fix.After.Specular);
        Pair("Subsurface scattering", fix.Before.Subsurface, fix.After.Subsurface);
        Pair("Skin tone influence", fix.Before.SkinInfluence, fix.After.SkinInfluence);
        if (fix.Before.Diffuse is { } colourBefore && fix.After.Diffuse is { } colourAfter && colourBefore.MaxDifference - colourAfter.MaxDifference > 0.002f)
            lines.Add($"Largest colour difference along the seam: {colourBefore.MaxDifference * 255:0} → {colourAfter.MaxDifference * 255:0} of 255");
        return lines;
    }

    /// <summary> What applying the preview writes, one line per file. </summary>
    public static IReadOnlyList<string> ApplyLines(NeckSeamPreview preview)
        => preview.Files.Select(file => file.Source.IsModFile
                ? $"Overwrite {file.Source.ModName}: {file.Source.RelativePath} ({file.Kind})"
                : $"Put the {file.Kind} for {file.GamePath} in a new mod (it is game data)")
            .ToList();

    /// <summary> The meeting point slider's text: how far each side moves. ImGui formats it, so percent signs are doubled. </summary>
    public static string MeetLabel(float meet)
    {
        var face = (int)MathF.Round(Math.Clamp(meet, 0f, 1f) * 100);
        return face switch
        {
            0 => "Only the body changes",
            100 => "Only the face changes",
            50 => "Both meet in the middle",
            _ => $"Face moves {face}%% · body {100 - face}%%",
        };
    }

    /// <summary> One line per skin setting the meeting point changes: the face's and body's values before and after. </summary>
    public static IReadOnlyList<string> MaterialPlan(NeckSeamMaterialMatch match, float meet)
    {
        var (face, body) = match.Plan(meet);
        var lines = new List<string>();
        string Values(float[] values) => string.Join(", ", values.Select(v => v.ToString("0.##")));
        string Side(string label, IReadOnlyDictionary<uint, float[]> changes, uint id, float[] before)
            => changes.TryGetValue(id, out var after) ? $"{label} {Values(before)} → {Values(after)}" : $"{label} {Values(before)} (unchanged)";

        if (match.TileScaleOff && match.FaceTiles > 0 && match.BodyTiles > 0)
        {
            var target = match.TargetTiles(meet);
            lines.Add($"Pore tile size: face {match.FaceTiles:0} → {target:0} per m, body {match.BodyTiles:0} → {target:0} per m " +
                      $"(g_TileScale: {Side("face", face, SkinMaterial.TileScale, match.FaceTileScale)}; {Side("body", body, SkinMaterial.TileScale, match.BodyTileScale)})");
        }
        if (match.TileAlphaOff)
            lines.Add($"Pore tile strength (g_TileAlpha): {Side("face", face, SkinMaterial.TileAlpha, [match.FaceTileAlpha])}; " +
                      Side("body", body, SkinMaterial.TileAlpha, [match.BodyTileAlpha]));
        if (match.TileIndexOff)
            lines.Add($"Pore pattern (g_TileIndex): {Side("face", face, SkinMaterial.TileIndex, [match.FaceTileIndex])}; " +
                      Side("body", body, SkinMaterial.TileIndex, [match.BodyTileIndex]));
        foreach (var (id, (faceValues, bodyValues)) in match.Other)
            lines.Add($"{SkinMaterial.Names.GetValueOrDefault(id, $"0x{id:X8}")}: {Side("face", face, id, faceValues)}; {Side("body", body, id, bodyValues)}");
        return lines;
    }

    public static string PreviewLine(NeckSeamPreview preview)
        => $"Preview mod \"{preview.ModDirectory}\" is enabled in {preview.CollectionName}. Look at the character in game, then apply the fix to your mods or discard it.";
}
