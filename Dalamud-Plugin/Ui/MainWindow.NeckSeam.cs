using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string NeckSeamDialog = "Skin seams##neck-seam";
    private NeckSeamService? _neckSeam;
    private bool _openNeckSeamDialog;
    private OnScreenObject? _neckSeamActor;
    private NeckSeamAnalysis? _neckSeamAnalysis;
    private SkinSeamFix? _neckSeamFix;
    private string _neckSeamError = string.Empty;
    private string _neckSeamBusyText = string.Empty;
    private int _neckSeamBusy;
    private bool _neckSeamMorph = true, _neckSeamMaterial = true, _neckSeamTextures = true;
    private float _neckSeamBandCm = NeckSeamFixer.DefaultBand * 100;
    /// <summary> Where the skin settings meet: 0 keeps the face's (only the body changes), 1 takes the body's (only the face changes). </summary>
    private float _neckSeamMeet = 0.5f;
    private bool _neckSeamConfirmApply;
    /// <summary> The fixes chosen for each body seam, replaced whole on every measurement. </summary>
    private Dictionary<BodySeamKind, BodySeamChoice> _bodySeamChoices = new();
    /// <summary>
    /// Which seams the preview fixes, by tab title. Only the neck starts ticked: body seam fixes change
    /// gear models and shared skin textures, so they are opt-in. Kept across measurements, reset when
    /// the dialog opens.
    /// </summary>
    private readonly Dictionary<string, bool> _skinSeamIncluded = new(StringComparer.Ordinal);

    private bool SeamIncluded(string title) => _skinSeamIncluded.TryGetValue(title, out var included) ? included : title == NeckTab;
    private const string NeckTab = "Neck";
    private const string ToneTab = "Skin tone";
    /// <summary> The tone match's picks by material path: the skin that changes and the one it should match. Kept across measurements while both are drawn. </summary>
    private string _skinToneTargetPath = string.Empty, _skinToneBasePath = string.Empty;
    private bool _skinToneColour, _skinToneInfluence, _skinToneShine, _skinToneSettings;
    /// <summary> Kept backups of the measured files, by the apply that made them; read with each measurement. </summary>
    private IReadOnlyList<PreviewBackupGroup> _skinSeamBackups = [];
    /// <summary> The backup group waiting for its restore to be confirmed. </summary>
    private PreviewBackupGroup? _skinSeamRestore;
    private bool _skinSeamRemeasure;

    /// <summary> One body seam's ticked fixes and where its two parts meet (0 keeps the first part, 1 the second). </summary>
    private sealed class BodySeamChoice
    {
        public bool Weld, Normals, Material, Textures;
        public float Meet = 0.5f;
        public float BandCm = NeckSeamFixer.DefaultBand * 100;
    }

    internal void AttachNeckSeam(NeckSeamService service) => _neckSeam = service;

    private void OpenNeckSeam(OnScreenObject actor)
    {
        _neckSeamActor = actor;
        _neckSeamConfirmApply = false;
        _openNeckSeamDialog = true;
        _skinSeamIncluded.Clear();
        _skinToneTargetPath = _skinToneBasePath = string.Empty;
        // Mods may have changed since the last look, so every opening measures again.
        MeasureNeckSeam();
    }

    private void MeasureNeckSeam()
    {
        if (_neckSeam is not { } service || _neckSeamActor is not { } actor || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = "Measuring the skin seams";
        _neckSeamError = string.Empty;
        _neckSeamAnalysis = null;
        _neckSeamFix = null;
        // The latest snapshot of this actor, so a remeasure after a redraw sees the preview's files.
        var current = _onScreen.Items.FirstOrDefault(item => item.ObjectIndex == actor.ObjectIndex && item.Name == actor.Name) ?? actor;
        _neckSeamActor = current;
        _ = Task.Run(async () =>
        {
            try
            {
                var analysis = await service.AnalyzeAsync(current, _lifetimeCts.Token).ConfigureAwait(false);
                if (analysis.Report is { } report)
                {
                    _neckSeamMorph = report.NeckMorphs.Count > 0;
                    _neckSeamMaterial = report.Material.Any;
                    _neckSeamTextures = report.TexturesDiffer;
                }
                _bodySeamChoices = (analysis.Body?.Seams ?? []).ToDictionary(seam => seam.Kind, seam => new BodySeamChoice
                {
                    Weld = seam.CanWeld, Normals = seam.NormalsDiffer, Material = seam.MaterialDiffers, Textures = seam.TexturesDiffer,
                });
                if (analysis.Tone is { } tone)
                {
                    if (tone.IndexOf(_skinToneTargetPath) is null || tone.IndexOf(_skinToneBasePath) is null)
                    {
                        _skinToneTargetPath = tone.DefaultTarget is { } target ? tone.Parts[target].MaterialPath : string.Empty;
                        _skinToneBasePath = tone.DefaultBase is { } @base ? tone.Parts[@base].MaterialPath : string.Empty;
                    }
                    ResetSkinToneChoices(tone);
                }
                try { _skinSeamBackups = service.Backups(analysis); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Debug(e, "Could not list the skin seam backups.");
                    _skinSeamBackups = [];
                }
                _skinSeamRestore = null;
                _neckSeamAnalysis = analysis;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not measure the skin seams.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
        });
    }

    /// <summary> The fixes ticked across the ticked seams, one line each, for the create button's tooltip; empty when none is. </summary>
    private List<string> SelectedSkinSeamFixes(NeckSeamAnalysis analysis)
    {
        var lines = new List<string>();
        if (analysis.Report is { } report && SeamIncluded(NeckTab))
        {
            if (_neckSeamMorph && report.NeckMorphs.Count > 0) lines.Add("Neck: add connection data to the face model");
            if (_neckSeamMaterial && report.Material.Any) lines.Add("Neck: bring the skin settings together");
            if (_neckSeamTextures && report.TexturesDiffer) lines.Add("Neck: blend the face textures into the body");
        }
        foreach (var seam in analysis.Body?.Seams ?? [])
        {
            if (!SeamIncluded(seam.Title) || !_bodySeamChoices.TryGetValue(seam.Kind, out var choice))
                continue;
            if (choice.Weld && seam.CanWeld) lines.Add($"{seam.Title}: close the gap between the edges");
            if (choice.Normals && seam.NormalsDiffer) lines.Add($"{seam.Title}: match the vertex normals");
            if (choice.Material && seam.MaterialDiffers) lines.Add($"{seam.Title}: bring the skin materials together");
            if (choice.Textures && seam.TexturesDiffer) lines.Add($"{seam.Title}: blend the skin textures");
        }
        if (SeamIncluded(ToneTab) && SkinToneOptions(analysis) is { } tone && analysis.Tone is { } toneReport &&
            toneReport.IndexOf(tone.TargetPath) is { } target && toneReport.IndexOf(tone.BasePath) is { } @base)
        {
            string changed = toneReport.Parts[target].Short, matched = toneReport.Parts[@base].Short;
            if (tone.Colour) lines.Add($"Skin tone: match the {changed} skin's colour to the {matched} skin's");
            if (tone.Influence) lines.Add($"Skin tone: give the {changed} skin the {matched} skin's skin tone influence");
            if (tone.Shine) lines.Add($"Skin tone: match the {changed} skin's shine to the {matched} skin's");
            if (tone.Settings) lines.Add($"Skin tone: give the {changed} skin the {matched} skin's material settings");
        }
        return lines;
    }

    /// <summary> The tone match's picks as indices into the measured skin materials; -1 when not picked or no longer drawn. </summary>
    private (int Target, int Base) SkinToneSelection(SkinToneReport report)
        => (report.IndexOf(_skinToneTargetPath) ?? -1, report.IndexOf(_skinToneBasePath) ?? -1);

    /// <summary> The picked pair compared, or null when the picks don't make a pair. </summary>
    private SkinToneComparison? PickedSkinTone(NeckSeamAnalysis analysis)
        => analysis.Tone is { } report && SkinToneSelection(report) is var (target, @base) && target >= 0 && @base >= 0 && target != @base
            ? report.Compare(target, @base)
            : null;

    /// <summary> Ticks every part of the match that differs for the picked pair. </summary>
    private void ResetSkinToneChoices(SkinToneReport report)
    {
        var (target, @base) = SkinToneSelection(report);
        if (target < 0 || @base < 0 || target == @base)
        {
            _skinToneColour = _skinToneInfluence = _skinToneShine = _skinToneSettings = false;
            return;
        }
        var comparison = report.Compare(target, @base);
        _skinToneColour = comparison.ColourDiffers;
        _skinToneInfluence = comparison.InfluenceDiffers;
        _skinToneShine = comparison.ShineDiffers;
        _skinToneSettings = comparison.Settings(_skinToneInfluence).Any;
    }

    /// <summary> The ticked parts of the tone match that change something, or null when none does. </summary>
    private SkinToneFixOptions? SkinToneOptions(NeckSeamAnalysis analysis)
    {
        if (PickedSkinTone(analysis) is not { } comparison)
            return null;
        var options = new SkinToneFixOptions(comparison.Target.MaterialPath, comparison.Base.MaterialPath,
            _skinToneColour && comparison.ColourDiffers, _skinToneInfluence && comparison.InfluenceDiffers, _skinToneShine && comparison.ShineDiffers,
            _skinToneSettings && comparison.Settings(_skinToneInfluence && comparison.InfluenceDiffers).Any);
        return options.Any ? options : null;
    }

    private void CreateNeckSeamPreview()
    {
        if (_neckSeam is not { } service || _neckSeamAnalysis is not { } analysis || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = "Building the fixed files";
        _neckSeamError = string.Empty;
        var neck = analysis.Report is not null && SeamIncluded(NeckTab) && (_neckSeamMorph || _neckSeamMaterial || _neckSeamTextures)
            ? new NeckSeamFixOptions(_neckSeamMorph, _neckSeamMaterial, _neckSeamTextures, _neckSeamBandCm / 100f, _neckSeamMeet)
            : null;
        var body = _bodySeamChoices.Where(p => SeamIncluded(BodySeamAnalyzer.Title(p.Key)) && (p.Value.Weld || p.Value.Normals || p.Value.Material || p.Value.Textures))
            .ToDictionary(p => p.Key, p => new BodySeamFixOptions(p.Value.Weld, p.Value.Normals, p.Value.Material, p.Value.Textures, p.Value.BandCm / 100f, p.Value.Meet));
        var tone = SeamIncluded(ToneTab) ? SkinToneOptions(analysis) : null;
        _ = Task.Run(async () =>
        {
            try
            {
                var fix = await NeckSeamService.BuildFixAsync(analysis, neck, body, tone, _lifetimeCts.Token).ConfigureAwait(false);
                _neckSeamFix = fix;
                _neckSeamBusyText = "Creating the preview mod";
                var (_, outcome) = await service.CreatePreviewAsync(analysis, fix, _lifetimeCts.Token).ConfigureAwait(false);
                ReportNeckSeam(outcome);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not create the skin seam preview.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
        });
    }

    private void FinishNeckSeamPreview(NeckSeamPreview preview, bool apply)
    {
        if (_neckSeam is not { } service || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = apply ? "Writing the fix into your mods" : "Removing the preview mod";
        _neckSeamError = string.Empty;
        _neckSeamConfirmApply = false;
        _ = Task.Run(async () =>
        {
            try
            {
                ReportNeckSeam(apply ? await service.ApplyAsync(preview).ConfigureAwait(false) : await service.DiscardAsync(preview).ConfigureAwait(false));
                _neckSeamAnalysis = null;
                _neckSeamFix = null;
            }
            catch (Exception error)
            {
                _log.Warning(error, apply ? "Could not apply the skin seam fix." : "Could not discard the skin seam preview.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
        });
    }

    private void RestoreSkinSeamBackup(PreviewBackupGroup group)
    {
        if (_neckSeam is not { } service || _neckSeamActor is not { } actor || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = "Restoring the backups";
        _neckSeamError = string.Empty;
        _skinSeamRestore = null;
        _ = Task.Run(async () =>
        {
            var restored = false;
            try
            {
                ReportNeckSeam(await service.RestoreAsync(group, actor.ObjectIndex).ConfigureAwait(false));
                _neckSeamAnalysis = null;
                _neckSeamFix = null;
                _skinSeamBackups = [];
                restored = true;
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not restore the skin seam backups.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
            // Measured again from the draw loop, with the restored files.
            _skinSeamRemeasure = restored;
        });
    }

    private void ReportNeckSeam(NeckSeamOutcome outcome)
        => SetStatus(outcome.Warnings.Count == 0 ? outcome.Message : outcome.Message + " " + string.Join(" ", outcome.Warnings),
            outcome.Warnings.Count == 0 ? FeedbackSeverity.Success : FeedbackSeverity.Warning);

    private void DrawNeckSeamDialog()
    {
        if (_openNeckSeamDialog)
        {
            _openNeckSeamDialog = false;
            ImGui.OpenPopup(NeckSeamDialog);
        }
        ImGui.SetNextWindowSize(Theme.Scaled(780, 680), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(560, 380), Theme.Scaled(1100, 1000));
        var open = true;
        if (!ImGui.BeginPopupModal(NeckSeamDialog, ref open))
            return;
        try
        {
            if (!open)
            {
                ImGui.CloseCurrentPopup();
                return;
            }
            DrawNeckSeamContent();
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawNeckSeamContent()
    {
        if (_skinSeamRemeasure && Volatile.Read(ref _neckSeamBusy) == 0)
        {
            _skinSeamRemeasure = false;
            MeasureNeckSeam();
        }
        var busy = Volatile.Read(ref _neckSeamBusy) != 0;
        var actor = _neckSeamActor;
        var analysis = _neckSeamAnalysis;
        var preview = actor is null ? null : _neckSeam?.PreviewFor(actor.Name);
        ImGui.TextColored(Theme.Text, actor?.Name ?? "Character");
        Widgets.HintWrapped("Compares the skin where your character's models meet, the way the game's skin shader draws them: the face and body at the " +
                            "neck, and the body parts at the wrists, waist and ankles. Only skin the game draws counts.");

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var body = ImRaii.Child("##neck-seam-body", new Vector2(0, -footer), false))
        {
            if (body.Success)
            {
                if (busy)
                {
                    Widgets.Spinner();
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Muted, _neckSeamBusyText);
                }
                if (_neckSeamError.Length > 0)
                    Widgets.Banner("##neck-seam-error", FeedbackSeverity.Error, _neckSeamError);
                if (preview is not null)
                    DrawNeckSeamPreview(preview, busy);
                if (analysis is not null)
                    DrawSkinSeamTabs(analysis, fixing: preview is null && analysis.PreviewSources.Count == 0);
                if (analysis is not null && preview is null && analysis.PreviewSources.Count == 0)
                    DrawSkinSeamBackups(busy);
            }
        }

        using (ImRaii.Disabled(busy || actor is null))
        {
            if (ImGui.Button("Measure again"))
                MeasureNeckSeam();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Measure with the files the character uses now (with a preview active, that is the preview).");
        if (analysis is not null && preview is null && analysis.PreviewSources.Count == 0)
        {
            ImGui.SameLine();
            var selected = SelectedSkinSeamFixes(analysis);
            using (ImRaii.Disabled(busy || selected.Count == 0))
            {
                if (ImGui.Button("Create preview mod"))
                    CreateNeckSeamPreview();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(selected.Count == 0
                    ? "Tick a seam to fix, and a fix, in its tab first."
                    : "Puts the fixed files in a new Penumbra mod enabled for this character. Your mods stay unchanged until you apply the fix.\n\n" +
                      string.Join("\n", selected.Select(line => "• " + line)));
            var fixing = new[] { NeckTab }.Concat(BodySeamAnalyzer.Kinds.Select(BodySeamAnalyzer.Title)).Append(ToneTab).Where(SeamIncluded).ToList();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, fixing.Count == 0 ? "No seam ticked" : "Fixing: " + string.Join(", ", fixing));
        }
        ImGui.SameLine();
        var closeWidth = ImGui.CalcTextSize("Close").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - closeWidth);
        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawNeckSeamPreview(NeckSeamPreview preview, bool busy)
    {
        Widgets.Banner("##neck-seam-preview", FeedbackSeverity.Info, NeckSeamViews.PreviewLine(preview));
        foreach (var change in preview.Changes)
            Widgets.MutedWrapped("• " + change);
        if (_neckSeamFix is { } fix)
        {
            if (fix.Neck is { } neck)
                foreach (var line in NeckSeamViews.Expected(neck))
                    Widgets.MutedWrapped("• Neck: " + line);
            if (fix.Body is { } body)
                foreach (var line in NeckSeamViews.Expected(body))
                    Widgets.MutedWrapped("• " + line);
            if (fix.Tone is { } tone)
                foreach (var line in tone.Expected)
                    Widgets.MutedWrapped("• Skin tone: " + line);
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(busy))
        {
            if (!_neckSeamConfirmApply)
            {
                if (ImGui.Button("Apply to my mods"))
                    _neckSeamConfirmApply = true;
                ImGui.SameLine();
                if (ImGui.Button("Discard preview"))
                    FinishNeckSeamPreview(preview, apply: false);
            }
            else
            {
                Widgets.SectionHeader("Apply the fix", "backups are kept for 7 days");
                foreach (var line in NeckSeamViews.ApplyLines(preview))
                    Widgets.MutedWrapped("• " + line);
                if (NeckSeamViews.ModelWarning(preview) is { } models)
                    Widgets.HintWrapped(models);
                Widgets.HintWrapped("Every character, option and collection that uses these files changes too. The preview mod is removed afterwards.");
                using (ImRaii.PushColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Important, .45f)))
                {
                    if (ImGui.Button("Apply"))
                        FinishNeckSeamPreview(preview, apply: true);
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##neck-seam-apply"))
                    _neckSeamConfirmApply = false;
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// The kept backups of the measured files, one row per apply (or other edit) that made them, each
    /// with a restore. Restoring a row puts its files back as they were just before it.
    /// </summary>
    private void DrawSkinSeamBackups(bool busy)
    {
        var groups = _skinSeamBackups;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"Restore backups ({groups.Count})###skin-seam-backups"))
            return;
        Widgets.HintWrapped("Backups of the files this check reads, made when a fix or another edit changed them, and kept for 7 days. Restoring a row puts " +
                            "its files back as they were just before that time. The files it replaces are backed up first, so a restore can be undone the same way.");
        if (groups.Count == 0)
        {
            Widgets.MutedWrapped("No backups of these files.");
            return;
        }
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            using var id = ImRaii.PushId(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, group.Created.ToLocalTime().ToString("d MMM, HH:mm:ss"));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, group.Files.Count == 1 ? "1 file" : $"{group.Files.Count} files");
            ImGui.SameLine(0, Theme.Gap * 2);
            using (ImRaii.Disabled(busy))
            {
                if (_skinSeamRestore == group)
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Important, .45f)))
                    {
                        if (ImGui.Button("Restore these files"))
                            RestoreSkinSeamBackup(group);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        _skinSeamRestore = null;
                }
                else if (ImGui.Button("Restore"))
                    _skinSeamRestore = group;
            }
            foreach (var file in group.Files)
                Widgets.MutedWrapped("• " + file.Source.Label);
        }
    }

    /// <summary> One tab per seam; a tab's label shows how many of its findings need a look, in the colour of the worst. </summary>
    private void DrawSkinSeamTabs(NeckSeamAnalysis analysis, bool fixing)
    {
        using var tabs = ImRaii.TabBar("##skin-seams");
        if (!tabs.Success)
            return;
        SkinSeamTab(NeckTab, analysis.Report?.Findings, () => DrawNeckSeamTab(analysis, fixing));
        foreach (var kind in BodySeamAnalyzer.Kinds)
        {
            var seam = analysis.Body?.Seam(kind);
            SkinSeamTab(BodySeamAnalyzer.Title(kind), seam?.Findings, () => DrawBodySeamTab(analysis, kind, seam, fixing));
        }
        SkinSeamTab(ToneTab, PickedSkinTone(analysis)?.Findings, () => DrawSkinToneTab(analysis, fixing));
    }

    /// <summary>
    /// The skin tone match: two pickers of the skin materials the character draws (grouped by mod), the
    /// pair compared where they touch, and the ticks for what the match changes on the first one.
    /// </summary>
    private void DrawSkinToneTab(NeckSeamAnalysis analysis, bool fixing)
    {
        if (analysis.Tone is not { } report)
        {
            Widgets.HintWrapped("The skin tones weren't measured: " + analysis.ToneError);
            return;
        }
        Widgets.HintWrapped("Matches one skin material to another over the whole part, for parts that use the skin shader with textures of their own: " +
                            "a face mod's ears, a tail, extra skin on gear. Pick the skin to change and the skin it should match.");
        if (report.Parts.Count < 2)
        {
            Widgets.HintWrapped("Your character draws fewer than two skin materials, so there is nothing to match.");
            return;
        }
        ImGui.Spacing();
        var (target, @base) = SkinToneSelection(report);
        var changed = SkinToneCombo("Change", "##skin-tone-target", report, ref target,
            "The skin that changes: its whole diffuse, normal map blue and mask, and its material settings.");
        changed |= SkinToneCombo("To match", "##skin-tone-base", report, ref @base, "The skin to match. It stays as it is.");
        if (changed)
        {
            _skinToneTargetPath = target >= 0 ? report.Parts[target].MaterialPath : string.Empty;
            _skinToneBasePath = @base >= 0 ? report.Parts[@base].MaterialPath : string.Empty;
            ResetSkinToneChoices(report);
        }
        if (target < 0 || @base < 0)
        {
            Widgets.MutedWrapped("Pick the skin to change and the skin to match.");
            return;
        }
        if (target == @base)
        {
            Widgets.HintWrapped("Pick two different skin materials.");
            return;
        }

        var comparison = report.Compare(target, @base);
        ImGui.Spacing();
        var (accent, _, icon) = Theme.Severity(NeckSeamViews.Feedback(comparison.Worst));
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, NeckSeamViews.ToneSummary(comparison));
        Widgets.MutedWrapped(NeckSeamViews.ToneWhere(comparison));
        if (comparison.TargetSample.Colour is { } targetColour && comparison.BaseSample.Colour is { } baseColour)
        {
            ToneSwatch("##tone-target", targetColour, $"The {comparison.TargetName}'s diffuse: {SkinToneComparison.Rgb(targetColour)}");
            ImGui.SameLine(0, Theme.Gap);
            ToneSwatch("##tone-base", baseColour, $"The {comparison.BaseName}'s diffuse: {SkinToneComparison.Rgb(baseColour)}");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{comparison.TargetName} · {comparison.BaseName} (diffuse only, before your skin colour and lighting)");
        }
        ImGui.Spacing();
        DrawSeamFindings("##skin-tone-findings", comparison.Findings, comparison.Target.Column, comparison.Base.Column);

        if (!fixing)
            return;
        ImGui.Spacing();
        Widgets.SectionHeader("Fix");
        if (!comparison.AnyFix)
        {
            Widgets.HintWrapped("Nothing here can be fixed automatically.");
            return;
        }
        string t = comparison.TargetName, b = comparison.BaseName;
        using var include = IncludeSeam(ToneTab,
            $"Puts the ticked matches into the preview mod. Off by default: they change the {t} material for every character and outfit that uses it. " +
            "Its changed textures become new files that only it uses, so other materials that read the same textures stay as they are.");
        FixOption("Match the colour##tone-colour", ref _skinToneColour, comparison.ColourDiffers,
            $"Scales every texel of the {t}'s diffuse by one factor per channel, so its colour where the parts meet becomes the {b}'s. Its own shading and detail stay.");
        FixOption("Match the skin tone influence##tone-influence", ref _skinToneInfluence, comparison.InfluenceDiffers,
            $"Gives the {t}'s normal map blue the {b}'s value, so your character's skin colour tints both alike, whatever skin colour you pick.");
        FixOption("Match the shine##tone-shine", ref _skinToneShine, comparison.ShineDiffers,
            $"Moves the {t}'s mask (specular strength, roughness, subsurface scattering) to the {b}'s values.");
        FixOption("Match the material settings##tone-settings", ref _skinToneSettings, comparison.Settings(_skinToneInfluence).Any,
            $"Gives the {t}'s material the {b}'s colour multiplier, pore tile (where it shows) and other skin settings.");
        using (ImRaii.PushIndent())
            foreach (var line in NeckSeamViews.TonePlan(comparison, _skinToneColour, _skinToneInfluence, _skinToneShine, _skinToneSettings))
                Widgets.MutedWrapped("• " + line);
    }

    /// <summary> A labelled picker of the measured skin materials, grouped under the mods they come from. Returns whether the pick changed. </summary>
    private static bool SkinToneCombo(string label, string id, SkinToneReport report, ref int selected, string tooltip)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.SameLine(Theme.Scaled(90));
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(520), ImGui.GetContentRegionAvail().X));
        var preview = selected >= 0 && selected < report.Parts.Count ? $"{report.Parts[selected].Mod} · {report.Parts[selected].Label}" : "Pick a skin material";
        var changed = false;
        using (var combo = ImRaii.Combo(id, preview))
        {
            if (combo.Success)
                foreach (var group in report.Parts.Select((part, index) => (Part: part, Index: index)).GroupBy(p => p.Part.Mod, StringComparer.Ordinal))
                {
                    ImGui.TextColored(Theme.Muted, group.Key);
                    using var indent = ImRaii.PushIndent();
                    foreach (var (part, index) in group)
                    {
                        if (ImGui.Selectable($"{part.Label}##{id}-{index}", index == selected))
                        {
                            changed = index != selected;
                            selected = index;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(NeckSeamViews.PartTooltip(part));
                    }
                }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static void ToneSwatch(string id, Vector3 colour, string tooltip)
    {
        ImGui.ColorButton(id, new Vector4(colour, 1), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static void SkinSeamTab(string title, IReadOnlyList<NeckSeamFinding>? findings, Action draw)
    {
        var worst = findings is { Count: > 0 } ? findings.Max(f => f.Severity) : NeckSeamSeverity.Ok;
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Severity(NeckSeamViews.Feedback(worst)).Accent, worst is NeckSeamSeverity.Problem or NeckSeamSeverity.Warning))
            open = ImGui.BeginTabItem(NeckSeamViews.TabLabel(title, findings));
        if (!open)
            return;
        ImGui.Spacing();
        draw();
        ImGui.EndTabItem();
    }

    private void DrawNeckSeamTab(NeckSeamAnalysis analysis, bool fixing)
    {
        if (analysis.Report is not { } report)
        {
            Widgets.HintWrapped("The neck wasn't measured: " + analysis.NeckError);
            return;
        }
        var (accent, _, icon) = Theme.Severity(NeckSeamViews.Feedback(report.Worst));
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, NeckSeamViews.Summary(report));
        Widgets.MutedWrapped($"Face: {report.FaceModelPath} · Body: {report.BodyModelPath} · {report.BodyMaterialPath}");
        ImGui.Spacing();
        DrawSeamFindings("##neck-seam-findings", report.Findings, "Face", "Body");

        if (!fixing)
            return;
        ImGui.Spacing();
        Widgets.SectionHeader("Fix");
        if (!report.AnyFix)
        {
            Widgets.HintWrapped("Nothing here can be fixed automatically.");
            return;
        }
        using var include = IncludeSeam(NeckTab, "Puts the neck's ticked fixes into the preview mod. They change the face's files, and the body's skin material when the settings meet on its side.");
        FixOption("Add neck connection data to the face model", ref _neckSeamMorph, report.NeckMorphs.Count > 0,
            $"Adds {report.NeckMorphs.Count} connection vertices from the face's neck edge, so the game joins the body's edge to the face.");
        FixOption("Bring the face's and body's skin settings together", ref _neckSeamMaterial, report.Material.Any,
            "Changes the skin detail tile's size, strength and pattern, and other skin settings, so the face and body material meet. " +
            "The face material applies to the whole face and the body material to the whole body.");
        if (report.Material.Any)
        {
            using var indent = ImRaii.PushIndent();
            using var disabled = ImRaii.Disabled(!_neckSeamMaterial);
            MeetSlider("##neck-seam-meet", ref _neckSeamMeet, "face", "body",
                "Where the settings meet. Left keeps the face as it is and changes the body to match it, right changes only the face, the middle moves both halfway.");
            foreach (var line in NeckSeamViews.MaterialPlan(report.Material, _neckSeamMeet))
                Widgets.MutedWrapped("• " + line);
        }
        FixOption("Blend the face textures into the body at the neck", ref _neckSeamTextures, report.TexturesDiffer,
            "Shifts the face's colour, mask and normal map towards the body's values at the seam, fading out above it.");
        if (report.TexturesDiffer)
        {
            using var indent = ImRaii.PushIndent();
            using var disabled = ImRaii.Disabled(!_neckSeamTextures);
            ImGui.SetNextItemWidth(Theme.Scaled(220));
            ImGui.SliderFloat("Blend height##neck-seam-band", ref _neckSeamBandCm, 0.5f, 6f, "%.1f cm");
        }
    }

    private void DrawBodySeamTab(NeckSeamAnalysis analysis, BodySeamKind kind, BodySeam? seam, bool fixing)
    {
        if (seam is null)
        {
            Widgets.HintWrapped(analysis.Body?.Notes.GetValueOrDefault(kind) ??
                                (analysis.BodyError.Length > 0 ? "The body seams weren't measured: " + analysis.BodyError : "Not measured."));
            return;
        }
        string first = seam.A.Label.ToLowerInvariant(), second = seam.B.Label.ToLowerInvariant();
        var (accent, _, icon) = Theme.Severity(NeckSeamViews.Feedback(seam.Worst));
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, NeckSeamViews.BodySummary(seam));
        Widgets.MutedWrapped($"{seam.A.Label}: {seam.A.ModelPath} · {seam.B.Label}: {seam.B.ModelPath}");
        ImGui.Spacing();
        DrawSeamFindings($"##body-seam-findings-{kind}", seam.Findings, seam.A.Label, seam.B.Label);

        if (!fixing)
            return;
        ImGui.Spacing();
        Widgets.SectionHeader("Fix");
        if (!seam.AnyFix || !_bodySeamChoices.TryGetValue(kind, out var choice))
        {
            Widgets.HintWrapped("Nothing here can be fixed automatically.");
            return;
        }
        using var include = IncludeSeam(seam.Title,
            $"Puts the {BodySeamAnalyzer.Of(seam.Title.ToLowerInvariant())} ticked fixes into the preview mod. Off by default: they change the " +
            $"{BodySeamAnalyzer.Of(first)} and the {BodySeamAnalyzer.Of(second)} models and the skin textures they share, for every outfit that uses them.");
        FixOption($"Close the gap between the edges##weld-{kind}", ref choice.Weld, seam.CanWeld,
            seam.Chains.Any(c => c.Overlap)
                ? "Moves the edges onto each other, and the skin just behind them a little, so no crease forms. Where one part's edge rests on the other's skin, only that edge moves, onto the skin under it."
                : "Moves the two parts' edges onto each other, and the skin just behind them a little, so no crease forms.");
        FixOption($"Match the vertex normals along the edges##normals-{kind}", ref choice.Normals, seam.NormalsDiffer,
            "Gives both edges the same vertex normals, so the lighting runs on smoothly across the seam.");
        FixOption($"Bring the two skin materials together##material-{kind}", ref choice.Material, seam.MaterialDiffers,
            $"Changes the skin detail tile's size, strength and pattern, and other skin settings, so the {first}'s and the {second}'s materials meet. " +
            "Each material applies to every part that uses it.");
        if (seam.Material is { Any: true } match && choice.Material)
        {
            using var indent = ImRaii.PushIndent();
            foreach (var line in NeckSeamViews.MaterialPlan(match, choice.Meet, first, second))
                Widgets.MutedWrapped("• " + line);
        }
        FixOption($"Blend the skin textures across the seam##textures-{kind}", ref choice.Textures, seam.TexturesDiffer,
            "Shifts both parts' colour, mask and normal map towards each other at the seam, fading out away from it.");
        if (seam.TexturesDiffer)
        {
            using var indent = ImRaii.PushIndent();
            using var disabled = ImRaii.Disabled(!choice.Textures);
            ImGui.SetNextItemWidth(Theme.Scaled(220));
            ImGui.SliderFloat($"Blend width##band-{kind}", ref choice.BandCm, 0.5f, 6f, "%.1f cm");
        }
        using (ImRaii.Disabled(!(choice.Weld && seam.CanWeld || choice.Normals && seam.NormalsDiffer || choice.Material && seam.MaterialDiffers ||
                                   choice.Textures && seam.TexturesDiffer)))
            MeetSlider($"##meet-{kind}", ref choice.Meet, first, second,
                $"Where the two parts meet. Left keeps the {first} as it is and changes only the {second}, right changes only the {first}, the middle moves both halfway.");
        if (choice.Weld && seam.CanWeld || choice.Normals && seam.NormalsDiffer)
            Widgets.HintWrapped("Changing a model changes it for every character and outfit that uses it.");
    }

    /// <summary>
    /// The seam's tick ("Fix the wrists"), then its fix options indented and switched off until it is
    /// ticked. Dispose the result after the options.
    /// </summary>
    private IDisposable IncludeSeam(string title, string help)
    {
        var included = SeamIncluded(title);
        if (ImGui.Checkbox($"Fix the {title.ToLowerInvariant()}##include-{title}", ref included))
            _skinSeamIncluded[title] = included;
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip(help);
        return new SeamOptions(ImRaii.PushIndent(), ImRaii.Disabled(!included));
    }

    private sealed class SeamOptions(IDisposable indent, IDisposable disabled) : IDisposable
    {
        public void Dispose()
        {
            disabled.Dispose();
            indent.Dispose();
        }
    }

    /// <summary> The meeting point slider between two labelled ends: left changes only the second side, right only the first. </summary>
    private static void MeetSlider(string id, ref float meet, string first, string second, string tooltip)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Adjust " + second);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(260));
        ImGui.SliderFloat(id, ref meet, 0f, 1f, NeckSeamViews.MeetLabel(meet, first, second));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Adjust " + first);
    }

    /// <summary> A findings table: the check, the value on each side, and the finding's detail on hover. </summary>
    private static void DrawSeamFindings(string id, IReadOnlyList<NeckSeamFinding> findings, string first, string second)
    {
        using (var table = ImRaii.Table(id, 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthStretch, .46f);
                ImGui.TableSetupColumn(first, ImGuiTableColumnFlags.WidthStretch, .27f);
                ImGui.TableSetupColumn(second, ImGuiTableColumnFlags.WidthStretch, .27f);
                ImGui.TableHeadersRow();
                foreach (var finding in findings)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    var (colour, _, glyph) = Theme.Severity(NeckSeamViews.Feedback(finding.Severity));
                    ImGui.TextColored(colour, glyph);
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextUnformatted(finding.Title);
                    var hovered = ImGui.IsItemHovered();
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextColored(Theme.Label, finding.Face);
                    hovered |= ImGui.IsItemHovered();
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextColored(Theme.Label, finding.Body);
                    hovered |= ImGui.IsItemHovered();
                    if (hovered && finding.Detail.Length > 0)
                    {
                        using var tooltip = ImRaii.Tooltip();
                        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(460));
                        ImGui.TextUnformatted(finding.Detail);
                    }
                }
            }
        }
        Widgets.Hint("Hover a row for what it measures.");
    }

    private static void FixOption(string label, ref bool value, bool available, string help)
    {
        using (ImRaii.Disabled(!available))
        {
            var shown = value && available;
            if (ImGui.Checkbox(label, ref shown))
                value = shown;
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip(available ? help : "Not needed: this part already matches.");
    }
}
