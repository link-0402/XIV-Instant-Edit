using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string NeckSeamDialog = "Neck seam##neck-seam";
    private NeckSeamService? _neckSeam;
    private bool _openNeckSeamDialog;
    private OnScreenObject? _neckSeamActor;
    private NeckSeamAnalysis? _neckSeamAnalysis;
    private NeckSeamFix? _neckSeamFix;
    private string _neckSeamError = string.Empty;
    private string _neckSeamBusyText = string.Empty;
    private int _neckSeamBusy;
    private bool _neckSeamMorph = true, _neckSeamMaterial = true, _neckSeamTextures = true;
    private float _neckSeamBandCm = NeckSeamFixer.DefaultBand * 100;
    /// <summary> Where the skin settings meet: 0 keeps the face's (only the body changes), 1 takes the body's (only the face changes). </summary>
    private float _neckSeamMeet = 0.5f;
    private bool _neckSeamConfirmApply;

    internal void AttachNeckSeam(NeckSeamService service) => _neckSeam = service;

    private void OpenNeckSeam(OnScreenObject actor)
    {
        _neckSeamActor = actor;
        _neckSeamConfirmApply = false;
        _openNeckSeamDialog = true;
        // Mods may have changed since the last look, so every opening measures again.
        MeasureNeckSeam();
    }

    private void MeasureNeckSeam()
    {
        if (_neckSeam is not { } service || _neckSeamActor is not { } actor || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = "Measuring the neck seam";
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
                _neckSeamMorph = analysis.Report.NeckMorphs.Count > 0;
                _neckSeamMaterial = analysis.Report.Material.Any;
                _neckSeamTextures = analysis.Report.TexturesDiffer;
                _neckSeamAnalysis = analysis;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not measure the neck seam.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
        });
    }

    private void CreateNeckSeamPreview()
    {
        if (_neckSeam is not { } service || _neckSeamAnalysis is not { } analysis || Interlocked.CompareExchange(ref _neckSeamBusy, 1, 0) != 0)
            return;
        _neckSeamBusyText = "Building the fixed files";
        _neckSeamError = string.Empty;
        var options = new NeckSeamFixOptions(_neckSeamMorph, _neckSeamMaterial, _neckSeamTextures, _neckSeamBandCm / 100f, _neckSeamMeet);
        _ = Task.Run(async () =>
        {
            try
            {
                var fix = await NeckSeamService.BuildFixAsync(analysis, options, _lifetimeCts.Token).ConfigureAwait(false);
                _neckSeamFix = fix;
                _neckSeamBusyText = "Creating the preview mod";
                var (_, outcome) = await service.CreatePreviewAsync(analysis, fix, _lifetimeCts.Token).ConfigureAwait(false);
                ReportNeckSeam(outcome);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not create the neck seam preview.");
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
                _log.Warning(error, apply ? "Could not apply the neck seam fix." : "Could not discard the neck seam preview.");
                _neckSeamError = error.Message;
            }
            finally { Interlocked.Exchange(ref _neckSeamBusy, 0); }
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
        ImGui.SetNextWindowSize(Theme.Scaled(760, 640), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(560, 360), Theme.Scaled(1100, 1000));
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
        var busy = Volatile.Read(ref _neckSeamBusy) != 0;
        var actor = _neckSeamActor;
        var analysis = _neckSeamAnalysis;
        var preview = actor is null ? null : _neckSeam?.PreviewFor(actor.Name);
        ImGui.TextColored(Theme.Text, actor?.Name ?? "Character");
        if (analysis is not null)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, analysis.Report.FaceModelPath);
        }
        Widgets.HintWrapped("Compares the face and the body where they meet at the neck, the way the game's skin shader draws them, and fixes the face side to match the body.");

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
                    DrawNeckSeamReport(analysis, busy, preview is not null);
            }
        }

        using (ImRaii.Disabled(busy || actor is null))
        {
            if (ImGui.Button("Measure again"))
                MeasureNeckSeam();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Measure with the files the character uses now (with a preview active, that is the preview).");
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
            foreach (var line in NeckSeamViews.Expected(fix))
                Widgets.MutedWrapped("• " + line);
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

    private void DrawNeckSeamReport(NeckSeamAnalysis analysis, bool busy, bool previewActive)
    {
        var report = analysis.Report;
        var (accent, _, icon) = Theme.Severity(NeckSeamViews.Feedback(report.Worst));
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, NeckSeamViews.Summary(report));
        Widgets.MutedWrapped($"Body: {report.BodyModelPath} · {report.BodyMaterialPath}");
        ImGui.Spacing();

        using (var table = ImRaii.Table("##neck-seam-findings", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthStretch, .46f);
                ImGui.TableSetupColumn("Face", ImGuiTableColumnFlags.WidthStretch, .27f);
                ImGui.TableSetupColumn("Body", ImGuiTableColumnFlags.WidthStretch, .27f);
                ImGui.TableHeadersRow();
                foreach (var finding in report.Findings)
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

        if (previewActive || analysis.PreviewSources.Count > 0)
            return;
        ImGui.Spacing();
        Widgets.SectionHeader("Fix");
        if (!report.AnyFix)
        {
            Widgets.HintWrapped("Nothing here can be fixed automatically.");
            return;
        }
        FixOption("Add neck connection data to the face model", ref _neckSeamMorph, report.NeckMorphs.Count > 0,
            $"Adds {report.NeckMorphs.Count} connection vertices from the face's neck edge, so the game joins the body's edge to the face.");
        FixOption("Bring the face's and body's skin settings together", ref _neckSeamMaterial, report.Material.Any,
            "Changes the skin detail tile's size, strength and pattern, and other skin settings, so the face and body material meet. " +
            "The face material applies to the whole face and the body material to the whole body.");
        if (report.Material.Any)
        {
            using var indent = ImRaii.PushIndent();
            using var disabled = ImRaii.Disabled(!_neckSeamMaterial);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Adjust body");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Theme.Scaled(260));
            ImGui.SliderFloat("##neck-seam-meet", ref _neckSeamMeet, 0f, 1f, NeckSeamViews.MeetLabel(_neckSeamMeet));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Where the settings meet. Left keeps the face as it is and changes the body to match it, right changes only the face, " +
                                 "the middle moves both halfway.");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Adjust face");
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
        ImGui.Spacing();
        using (ImRaii.Disabled(busy || !(_neckSeamMorph || _neckSeamMaterial || _neckSeamTextures)))
        {
            if (ImGui.Button("Create preview mod"))
                CreateNeckSeamPreview();
        }
        Widgets.HintWrapped("Puts the fixed files in a new Penumbra mod enabled for this character. Your mods stay unchanged until you apply the fix.");
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
