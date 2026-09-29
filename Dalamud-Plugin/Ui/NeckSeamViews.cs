using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui;

/// <summary> Text for the skin seam dialog. No ImGui or Dalamud dependencies. </summary>
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

    /// <summary> One line summing up a measured body seam. </summary>
    public static string BodySummary(BodySeam seam)
    {
        if (seam.Chains.Count == 0)
            return $"The {seam.A.Label.ToLowerInvariant()} and {seam.B.Label.ToLowerInvariant()} don't meet at the {seam.Title.ToLowerInvariant()}.";
        var problems = seam.Findings.Count(f => f.Severity == NeckSeamSeverity.Problem);
        var warnings = seam.Findings.Count(f => f.Severity == NeckSeamSeverity.Warning);
        if (problems == 0 && warnings == 0)
            return $"The {seam.A.Label.ToLowerInvariant()} and {seam.B.Label.ToLowerInvariant()} match at the {seam.Title.ToLowerInvariant()}.";
        var parts = new List<string>();
        if (problems > 0) parts.Add(problems == 1 ? "1 difference shows as a seam" : $"{problems} differences show as a seam");
        if (warnings > 0) parts.Add(warnings == 1 ? "1 smaller difference" : $"{warnings} smaller differences");
        return string.Join(", ", parts) + (seam.AnyFix ? "." : "; none of them can be fixed here.");
    }

    /// <summary> A seam tab's label: the seam and how many of its findings need a look. </summary>
    public static string TabLabel(string title, IReadOnlyList<NeckSeamFinding>? findings)
    {
        var issues = findings?.Count(f => f.Severity is NeckSeamSeverity.Problem or NeckSeamSeverity.Warning) ?? 0;
        return issues > 0 ? $"{title} ({issues})###skin-seam-{title}" : $"{title}###skin-seam-{title}";
    }

    /// <summary> The body seams' before and after lines, each named after its seam. </summary>
    public static IReadOnlyList<string> Expected(BodySeamFix fix)
        => fix.Expected.OrderBy(p => p.Key).SelectMany(p => p.Value.Select(line => $"{BodySeamAnalyzer.Title(p.Key)}: {line}")).ToList();

    /// <summary> The warning for models a preview writes on Apply: every character and outfit wearing them changes. </summary>
    public static string? ModelWarning(NeckSeamPreview preview)
    {
        var models = preview.Files.Where(f => f.Kind.EndsWith(" model", StringComparison.Ordinal)).ToList();
        if (models.Count == 0)
            return null;
        return "Changing a model changes it for every character, outfit and collection that uses it: " +
               string.Join(", ", models.Select(m => m.Source.IsModFile ? $"{m.Source.ModName}: {m.Source.RelativePath}" : m.GamePath)) + ".";
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

    /// <summary>
    /// The meeting point slider's text: how far each side moves, for the face and body at the neck or
    /// the two parts of a body seam. ImGui formats it, so percent signs are doubled.
    /// </summary>
    public static string MeetLabel(float meet, string first = "face", string second = "body")
    {
        var share = (int)MathF.Round(Math.Clamp(meet, 0f, 1f) * 100);
        // "the gloves change", "the top changes".
        static string Changes(string part) => part.EndsWith('s') ? $"the {part} change" : $"the {part} changes";
        return share switch
        {
            0 => "Only " + Changes(second),
            100 => "Only " + Changes(first),
            50 => "Both meet in the middle",
            _ => $"{char.ToUpperInvariant(first[0])}{first[1..]} moves {share}%% · {second} {100 - share}%%",
        };
    }

    /// <summary> One line per skin setting the meeting point changes: each side's values before and after. </summary>
    public static IReadOnlyList<string> MaterialPlan(NeckSeamMaterialMatch match, float meet, string first = "face", string second = "body")
    {
        var (face, body) = match.Plan(meet);
        var lines = new List<string>();
        string Values(float[] values) => string.Join(", ", values.Select(v => v.ToString("0.##")));
        string Side(string label, IReadOnlyDictionary<uint, float[]> changes, uint id, float[] before)
            => changes.TryGetValue(id, out var after) ? $"{label} {Values(before)} → {Values(after)}" : $"{label} {Values(before)} (unchanged)";

        if (match.TileScaleOff && match.FaceTiles > 0 && match.BodyTiles > 0)
        {
            var target = match.TargetTiles(meet);
            lines.Add($"Pore tile size: {first} {match.FaceTiles:0} → {target:0} per m, {second} {match.BodyTiles:0} → {target:0} per m " +
                      $"(g_TileScale: {Side(first, face, SkinMaterial.TileScale, match.FaceTileScale)}; {Side(second, body, SkinMaterial.TileScale, match.BodyTileScale)})");
        }
        if (match.TileAlphaOff)
            lines.Add($"Pore tile strength (g_TileAlpha): {Side(first, face, SkinMaterial.TileAlpha, [match.FaceTileAlpha])}; " +
                      Side(second, body, SkinMaterial.TileAlpha, [match.BodyTileAlpha]));
        if (match.TileIndexOff)
            lines.Add($"Pore pattern (g_TileIndex): {Side(first, face, SkinMaterial.TileIndex, [match.FaceTileIndex])}; " +
                      Side(second, body, SkinMaterial.TileIndex, [match.BodyTileIndex]));
        foreach (var (id, (faceValues, bodyValues)) in match.Other)
            lines.Add($"{SkinMaterial.Names.GetValueOrDefault(id, $"0x{id:X8}")}: {Side(first, face, id, faceValues)}; {Side(second, body, id, bodyValues)}");
        return lines;
    }

    public static string PreviewLine(NeckSeamPreview preview)
        => $"Preview mod \"{preview.ModDirectory}\" is enabled in {preview.CollectionName}. Look at the character in game, then apply the fix to your mods or discard it.";
}
