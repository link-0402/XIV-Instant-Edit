using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.CharacterWeight;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    /// <summary> What a texture row shows, worked out once per check. </summary>
    private sealed record WeightRow(WeightTexture Texture, string? Blocker, string Size, string Format, string Mips, string Memory, string Notes,
        IReadOnlyList<string> NoteDetails, IReadOnlyList<ShrinkFormat> Formats);

    private const string WeightDialog = "Character weight##character-weight";
    private CharacterWeightService? _weight;
    private IReadOnlyList<OnScreenObject>? _weightSnapshot;
    private OnScreenObject? _weightCharacter;
    private OnScreenObject? _weightActor;
    private CharacterWeightAnalysis? _weightAnalysis;
    private CharacterWeightAnalysis? _weightChoicesFor;
    private IReadOnlyList<WeightRow> _weightRows = [];
    /// <summary> The change set up for each texture, ticked or not, so ticking again restores it. </summary>
    private readonly Dictionary<string, ShrinkChoice> _weightChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _weightTicked = new(StringComparer.OrdinalIgnoreCase);
    private WeightPlanTotals _weightPlan;
    private bool _weightPlanStale = true;
    private string _weightError = string.Empty;
    private string _weightBusyText = string.Empty;
    private int _weightBusy;
    private bool _openWeightDialog;
    private bool _weightConfirmApply;
    private CancellationTokenSource? _weightStop;

    internal void AttachCharacterWeight(CharacterWeightService service) => _weight = service;

    /// <summary> The Quick Actions card, and the dialog it opens. </summary>
    private void DrawCharacterWeightCard()
    {
        QuickActionCard("##quick-character-weight", FontAwesomeIcon.WeightHanging, "Character weight",
            "Totals what your character costs other players in texture memory (VRAM) and triangles, counted the way Lightless, PlayerSync " +
            "and other Mare-based sync plugins count them, ranks the files that cost the most, and builds a preview mod with smaller textures.",
            DrawCharacterWeightAction);
        DrawCharacterWeightDialog();
    }

    private void DrawCharacterWeightAction()
    {
        if (_weight is null)
        {
            Widgets.Hint("Unavailable: the character weight tools did not start.");
            return;
        }
        if (WeightCharacter() is not { } character)
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character isn't in the list yet. Refresh the list once your character is drawn.");
            return;
        }

        using (ImRaii.Disabled(Volatile.Read(ref _weightBusy) != 0))
        {
            if (ImGui.Button("Check weight##quick-character-weight"))
                OpenCharacterWeight(character);
        }
        if (_weightAnalysis is { } analysis && analysis.ActorName == character.Name)
        {
            var report = analysis.Report;
            var level = (WeightLevel)Math.Max((int)CharacterWeightViews.VramLevel(report.SyncVram), (int)CharacterWeightViews.TriangleLevel(report.SyncTriangles));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Severity(CharacterWeightViews.Feedback(level)).Accent, CharacterWeightViews.Totals(report));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The last check, as sync plugins count it.");
        }
        if (_weight.PreviewFor(character.Name) is not null)
            Widgets.HintWrapped("A preview mod is active. Open the check to apply it to your mods or discard it.");
    }

    /// <summary> Your character, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? WeightCharacter()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _weightSnapshot))
        {
            _weightSnapshot = items;
            _weightCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player);
        }
        return _weightCharacter;
    }

    private void OpenCharacterWeight(OnScreenObject actor)
    {
        _weightActor = actor;
        _weightConfirmApply = false;
        _openWeightDialog = true;
        MeasureCharacterWeight();
    }

    /// <summary> Refreshes the On Screen list, so the check sees the files your character uses now, then measures. </summary>
    private void MeasureCharacterWeight()
    {
        if (_weight is not { } service || _weightActor is not { } actor || Interlocked.CompareExchange(ref _weightBusy, 1, 0) != 0)
            return;
        _weightBusyText = "Measuring textures and models";
        _weightError = string.Empty;
        _weightAnalysis = null;
        try { _onScreen.RequestRefresh(); }
        catch (Exception e) { _log.Debug(e, "Could not refresh the On Screen list before the weight check."); }
        _ = Task.Run(async () =>
        {
            try
            {
                var token = _lifetimeCts.Token;
                for (var waited = 0; _onScreen.IsRefreshing && waited < 300; waited++)
                    await Task.Delay(100, token).ConfigureAwait(false);
                var current = _onScreen.Items.FirstOrDefault(item => item.ObjectIndex == actor.ObjectIndex && item.Name == actor.Name) ?? actor;
                _weightActor = current;
                _weightAnalysis = await service.AnalyzeAsync(current, (done, total) => _weightBusyText = $"Reading file {done + 1} of {total}", token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not measure the character's weight.");
                _weightError = error.Message;
            }
            finally { Interlocked.Exchange(ref _weightBusy, 0); }
        });
    }

    private void CreateWeightPreview()
    {
        if (_weight is not { } service || _weightAnalysis is not { } analysis || Interlocked.CompareExchange(ref _weightBusy, 1, 0) != 0)
            return;
        var choices = TickedWeightChoices();
        _weightBusyText = "Preparing the textures";
        _weightError = string.Empty;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _weightStop = stop;
        _ = Task.Run(async () =>
        {
            try
            {
                ReportCharacterWeight(await service.CreatePreviewAsync(analysis, choices, text => _weightBusyText = text, stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (!_lifetimeCts.IsCancellationRequested)
                    _weightError = "Stopped. No preview mod was made and your mods are unchanged.";
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not create the character weight preview.");
                _weightError = error.Message;
            }
            finally
            {
                _weightStop = null;
                stop.Dispose();
                Interlocked.Exchange(ref _weightBusy, 0);
            }
        });
    }

    private void FinishWeightPreview(PreviewMod preview, bool apply)
    {
        if (_weight is not { } service || Interlocked.CompareExchange(ref _weightBusy, 1, 0) != 0)
            return;
        _weightBusyText = apply ? "Writing the smaller textures into your mods" : "Removing the preview mod";
        _weightError = string.Empty;
        _weightConfirmApply = false;
        _ = Task.Run(async () =>
        {
            try
            {
                ReportCharacterWeight(apply ? await service.ApplyAsync(preview).ConfigureAwait(false) : await service.DiscardAsync(preview).ConfigureAwait(false));
                // The files changed, so the numbers are stale until the next check.
                _weightAnalysis = null;
            }
            catch (Exception error)
            {
                _log.Warning(error, apply ? "Could not apply the smaller textures." : "Could not discard the character weight preview.");
                _weightError = error.Message;
            }
            finally { Interlocked.Exchange(ref _weightBusy, 0); }
        });
    }

    private void ReportCharacterWeight(CharacterWeightOutcome outcome)
        => SetStatus(outcome.Warnings.Count == 0 ? outcome.Message : outcome.Message + " " + string.Join(" ", outcome.Warnings),
            outcome.Warnings.Count == 0 ? FeedbackSeverity.Success : FeedbackSeverity.Warning);

    private void DrawCharacterWeightDialog()
    {
        if (_openWeightDialog)
        {
            _openWeightDialog = false;
            ImGui.OpenPopup(WeightDialog);
        }
        ImGui.SetNextWindowSize(Theme.Scaled(1040, 720), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(700, 420), Theme.Scaled(1800, 1300));
        var open = true;
        if (!ImGui.BeginPopupModal(WeightDialog, ref open))
            return;
        try
        {
            if (!open)
            {
                ImGui.CloseCurrentPopup();
                return;
            }
            DrawCharacterWeightContent();
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawCharacterWeightContent()
    {
        var busy = Volatile.Read(ref _weightBusy) != 0;
        var actor = _weightActor;
        var analysis = _weightAnalysis;
        var preview = actor is null ? null : _weight?.PreviewFor(actor.Name);
        ImGui.TextColored(Theme.Text, actor?.Name ?? "Character");
        Widgets.HintWrapped("What your character costs other players, counted the way Lightless, PlayerSync and other Mare-based sync plugins count it: " +
                            "the file size of each modded texture and the triangles of each modded model, every file once. Game files don't count.");

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var body = ImRaii.Child("##weight-body", new Vector2(0, -footer), false))
        {
            if (body.Success)
            {
                if (busy)
                {
                    Widgets.Spinner();
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Muted, _weightBusyText);
                    if (_weightStop is { } stop)
                    {
                        ImGui.SameLine(0, Theme.Gap);
                        if (ImGui.SmallButton("Stop##weight-stop"))
                        {
                            try { stop.Cancel(); }
                            catch (ObjectDisposedException) { }
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Stop after the texture being converted. Nothing is changed.");
                    }
                }
                if (_weightError.Length > 0)
                    Widgets.Banner("##weight-error", FeedbackSeverity.Error, _weightError);
                if (preview is not null)
                    DrawWeightPreview(preview, busy);
                if (analysis is not null)
                    DrawWeightReport(analysis, busy, preview is not null);
                else if (!busy)
                    Widgets.HintWrapped("Press Check again to measure your character.");
            }
        }

        using (ImRaii.Disabled(busy || actor is null))
        {
            if (ImGui.Button("Check again##weight"))
                MeasureCharacterWeight();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Refresh the On Screen list and measure the files your character uses now (with a preview active, that is the preview).");
        ImGui.SameLine();
        var closeWidth = ImGui.CalcTextSize("Close").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - closeWidth);
        if (ImGui.Button("Close##weight"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawWeightPreview(PreviewMod preview, bool busy)
    {
        Widgets.Banner("##weight-preview", FeedbackSeverity.Info, CharacterWeightViews.PreviewLine(preview));
        foreach (var change in preview.Changes)
            Widgets.MutedWrapped("• " + change);
        ImGui.Spacing();
        using (ImRaii.Disabled(busy))
        {
            if (!_weightConfirmApply)
            {
                if (ImGui.Button("Apply to my mods##weight"))
                    _weightConfirmApply = true;
                ImGui.SameLine();
                if (ImGui.Button("Discard preview##weight"))
                    FinishWeightPreview(preview, apply: false);
            }
            else
            {
                Widgets.SectionHeader("Apply the smaller textures", "backups are kept for 7 days");
                foreach (var line in CharacterWeightViews.ApplyLines(preview))
                    Widgets.MutedWrapped("• " + line);
                Widgets.HintWrapped("Each file is overwritten in its mod, so every option, item, character and collection that uses it changes too, " +
                                    "including files several options share. Game files go into a new mod instead. The preview mod is removed afterwards.");
                using (ImRaii.PushColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Important, .45f)))
                {
                    if (ImGui.Button("Apply##weight"))
                        FinishWeightPreview(preview, apply: true);
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##weight-apply"))
                    _weightConfirmApply = false;
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawWeightReport(CharacterWeightAnalysis analysis, bool busy, bool previewActive)
    {
        var report = analysis.Report;
        EnsureWeightChoices(analysis);
        using (var totals = ImRaii.Table("##weight-totals", 4, ImGuiTableFlags.SizingFixedFit))
        {
            if (totals.Success)
            {
                ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##meter", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##status", ImGuiTableColumnFlags.WidthStretch);
                WeightTotalRow("Texture memory", CharacterWeightViews.Bytes(report.SyncVram), report.SyncVram, SyncThresholds.WarningVram,
                    SyncThresholds.AutoPauseVram, CharacterWeightViews.VramLevel(report.SyncVram), CharacterWeightViews.VramStatus(report.SyncVram));
                WeightTotalRow("Triangles", CharacterWeightViews.Count(report.SyncTriangles), report.SyncTriangles, SyncThresholds.WarningTriangles,
                    SyncThresholds.AutoPauseTriangles, CharacterWeightViews.TriangleLevel(report.SyncTriangles), CharacterWeightViews.TriangleStatus(report.SyncTriangles));
            }
        }
        Widgets.MutedWrapped($"Everything your character renders, game files included: {CharacterWeightViews.Bytes(report.AllVram)} of textures " +
                             $"({CharacterWeightViews.Bytes(report.GameVram)} of it game files) and {CharacterWeightViews.Count(report.AllTriangles)} triangles.");
        Widgets.HintWrapped($"Lightless and PlayerSync warn about a player above {CharacterWeightViews.Bytes(SyncThresholds.WarningVram)} or " +
                            $"{CharacterWeightViews.Count(SyncThresholds.WarningTriangles)} triangles by default, and pause them above " +
                            $"{CharacterWeightViews.Bytes(SyncThresholds.AutoPauseVram)} or {CharacterWeightViews.Count(SyncThresholds.AutoPauseTriangles)} " +
                            "triangles if the viewer turned auto-pause on. Viewers can change both.");
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("Sync plugins count what they send: the files your mods replace. Texture memory is the file size of each texture, " +
                        "which is its mip chain; triangles are those of each model's most detailed level. A file used by several models, " +
                        "or the same file in two mods, counts once. Your minion and mount aren't counted.\n\n" +
                        "PlayerSync's server may send its viewers compressed copies of uncompressed textures, so they can see less than this.");
        ImGui.Spacing();

        using var tabs = ImRaii.TabBar("##weight-tabs");
        if (!tabs.Success)
            return;
        if (ImGui.BeginTabItem($"Textures ({report.Textures.Count})###weight-textures"))
        {
            DrawWeightTextures(analysis, busy, previewActive);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem($"Models ({report.Models.Count})###weight-models"))
        {
            DrawWeightModels(report);
            ImGui.EndTabItem();
        }
    }

    private static void WeightTotalRow(string label, string value, long amount, long warning, long autoPause, WeightLevel level, string status)
    {
        var (accent, _, icon) = Theme.Severity(CharacterWeightViews.Feedback(level));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, value);
        ImGui.TableSetColumnIndex(2);
        WeightMeter(amount, warning, autoPause, accent);
        ImGui.TableSetColumnIndex(3);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, status);
    }

    /// <summary> A bar filled up to the value, with ticks at the warning and auto-pause limits. </summary>
    private static void WeightMeter(long value, long warning, long autoPause, Vector4 colour)
    {
        var width = Theme.Scaled(220);
        var height = Theme.Scaled(8);
        var frame = ImGui.GetFrameHeight();
        var scale = Math.Max(value, autoPause) * 1.15;
        var origin = ImGui.GetCursorScreenPos() + new Vector2(0, (frame - height) / 2);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(Theme.WithAlpha(Theme.Muted, .25f)), height / 2);
        var fill = (float)(width * Math.Clamp(value / scale, 0, 1));
        if (fill > 0)
            drawList.AddRectFilled(origin, origin + new Vector2(fill, height), ImGui.GetColorU32(colour), height / 2);
        foreach (var limit in new[] { warning, autoPause })
        {
            var x = origin.X + (float)(width * limit / scale);
            drawList.AddLine(new Vector2(x, origin.Y - Theme.Scaled(3)), new Vector2(x, origin.Y + height + Theme.Scaled(3)),
                ImGui.GetColorU32(Theme.Label), Theme.Scaled(1.5f));
        }
        ImGui.Dummy(new Vector2(width, frame));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The ticks mark the warning and auto-pause defaults.");
    }

    /// <summary> Builds the rows and the preselected changes the first time a new check is shown. </summary>
    private void EnsureWeightChoices(CharacterWeightAnalysis analysis)
    {
        if (ReferenceEquals(_weightChoicesFor, analysis))
            return;
        _weightChoicesFor = analysis;
        _weightRows = analysis.Report.Textures.Select(texture => new WeightRow(texture, ShrinkRules.Blocker(texture),
            texture.Info is { } info ? CharacterWeightViews.Size(info.Width, info.Height) : "",
            texture.Info is { } header ? TextureCost.FormatName(header.Format) : "",
            texture.Info is { } mips ? $"{mips.Mips} mip level{(mips.Mips == 1 ? "" : "s")}" : "",
            CharacterWeightViews.Bytes(texture.Vram), CharacterWeightViews.Notes(texture), CharacterWeightViews.NoteDetails(texture),
            ShrinkRules.Formats(texture))).ToList();
        ResetWeightChoices(analysis);
    }

    /// <summary> Ticks the default changes and sets up a suggested change for every other texture that allows one. </summary>
    private void ResetWeightChoices(CharacterWeightAnalysis analysis)
    {
        _weightChoices.Clear();
        _weightTicked.Clear();
        foreach (var texture in analysis.Report.Textures)
        {
            var choice = ShrinkRules.Default(texture);
            if (!choice.IsNone)
                _weightTicked.Add(texture.Key);
            else
                choice = ShrinkRules.Suggested(texture);
            if (!choice.IsNone)
                _weightChoices[texture.Key] = choice;
        }
        _weightPlanStale = true;
    }

    private Dictionary<string, ShrinkChoice> TickedWeightChoices()
        => _weightTicked.Where(key => _weightChoices.TryGetValue(key, out var choice) && !choice.IsNone)
            .ToDictionary(key => key, key => _weightChoices[key], StringComparer.OrdinalIgnoreCase);

    private void DrawWeightTextures(CharacterWeightAnalysis analysis, bool busy, bool previewActive)
    {
        var report = analysis.Report;
        if (_weightPlanStale)
        {
            _weightPlan = ShrinkRules.Totals(report, TickedWeightChoices());
            _weightPlanStale = false;
        }
        using (ImRaii.Disabled(busy || previewActive))
        {
            if (ImGui.SmallButton("Tick the defaults##weight"))
                ResetWeightChoices(analysis);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Compress uncompressed textures (BC5 for index maps, BC7 for the rest, like Mod Optimizer's Auto rule) " +
                                 $"and halve textures over {ShrinkRules.SizeCap} pixels.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Untick all##weight"))
            {
                _weightTicked.Clear();
                _weightPlanStale = true;
            }
        }
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(_weightPlan.Files == 0 ? Theme.Hint : Theme.Text, CharacterWeightViews.Plan(report, _weightPlan));

        var bottom = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var height = Math.Max(Theme.Scaled(160), ImGui.GetContentRegionAvail().Y - bottom);
        using (var table = ImRaii.Table("##weight-texture-table", 9,
                   ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable,
                   new Vector2(0, height)))
        {
            if (table.Success)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("##tick", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, ImGui.GetFrameHeight());
                ImGui.TableSetupColumn("Texture", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Memory", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Change to", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(150));
                ImGui.TableSetupColumn("New size", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(130));
                ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableHeadersRow();
                var rows = _weightRows;
                var clipper = ImGui.ImGuiListClipper();
                try
                {
                    clipper.Begin(rows.Count, -1);
                    while (clipper.Step())
                        for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                            DrawWeightTextureRow(analysis, rows[index], busy || previewActive);
                    clipper.End();
                }
                finally
                {
                    clipper.Destroy();
                }
            }
        }

        if (previewActive)
        {
            Widgets.HintWrapped("Apply or discard the preview above before making another one.");
            return;
        }
        using (ImRaii.Disabled(busy || _weightPlan.Files == 0))
        {
            if (ImGui.Button("Create preview mod##weight"))
                CreateWeightPreview();
        }
        Widgets.HintWrapped("Puts the smaller textures in a new Penumbra mod enabled for your character. Your mods stay unchanged until you apply them.");
    }

    private void DrawWeightTextureRow(CharacterWeightAnalysis analysis, WeightRow row, bool locked)
    {
        var texture = row.Texture;
        using var id = ImRaii.PushId(texture.Key);
        // Only textures that allow some change have a choice.
        var changeable = row.Blocker is null && _weightChoices.ContainsKey(texture.Key);
        var ticked = changeable && _weightTicked.Contains(texture.Key);
        var choice = _weightChoices.GetValueOrDefault(texture.Key);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        using (ImRaii.Disabled(locked || !changeable))
        {
            if (ImGui.Checkbox("##tick", ref ticked))
            {
                if (ticked)
                {
                    if (choice.IsNone)
                        _weightChoices[texture.Key] = choice = ShrinkRules.Suggested(texture);
                    _weightTicked.Add(texture.Key);
                }
                else
                    _weightTicked.Remove(texture.Key);
                _weightPlanStale = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && (row.Blocker ?? (!changeable ? "Nothing here makes it smaller." : null)) is { } why)
            ImGui.SetTooltip(why);

        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(texture.Source.State == ResourceSourceState.GameData ? Theme.GameSource : Theme.Text, texture.FileName);
        if (ImGui.IsItemHovered())
            WeightTextureTooltip(analysis, texture);

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(texture.Oversized ? Theme.Warning : Theme.Label, row.Size);

        ImGui.TableSetColumnIndex(3);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(texture.Uncompressed ? Theme.Warning : Theme.Label, row.Format);
        if (row.Mips.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(row.Mips);

        ImGui.TableSetColumnIndex(4);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, row.Memory);

        ImGui.TableSetColumnIndex(5);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(texture.Uncompressed || texture.Oversized ? Theme.Warning : Theme.Muted, row.Notes);
        if (row.NoteDetails.Count > 0 && ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            using var wrap = ImRaii.TextWrapPos(Theme.Scaled(420));
            foreach (var line in row.NoteDetails)
                ImGui.TextUnformatted(line);
        }

        if (!changeable)
            return;
        using (ImRaii.Disabled(locked || !ticked))
        {
            ImGui.TableSetColumnIndex(6);
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##format", CharacterWeightViews.FormatLabel(texture, choice.Format)))
            {
                if (combo.Success)
                    foreach (var format in row.Formats)
                        if (ImGui.Selectable(CharacterWeightViews.FormatLabel(texture, format), format == choice.Format))
                            SetWeightChoice(texture, ShrinkRules.WithFormat(texture, choice, format));
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(texture.IndexOnly
                    ? "BC5 keeps red and green, all an index map's shader reads; the game's own index maps are BC5."
                    : "BC7 keeps all four channels, which normal, mask and colour maps use.");

            ImGui.TableSetColumnIndex(7);
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##size", CharacterWeightViews.SizeLabel(texture, choice.Halvings)))
            {
                if (combo.Success)
                    foreach (var halvings in ShrinkRules.Halvings(texture, choice.Format))
                        if (ImGui.Selectable(CharacterWeightViews.SizeLabel(texture, halvings), halvings == choice.Halvings))
                            SetWeightChoice(texture, choice with { Halvings = halvings });
            }
        }

        ImGui.TableSetColumnIndex(8);
        ImGui.AlignTextToFramePadding();
        if (ticked)
            ImGui.TextColored(Theme.Success, CharacterWeightViews.Bytes(ShrinkRules.VramAfter(texture, choice)));
    }

    /// <summary> Stores a changed choice; a choice that changes nothing unticks the texture. </summary>
    private void SetWeightChoice(WeightTexture texture, ShrinkChoice choice)
    {
        if (!choice.IsNone && !ShrinkRules.IsValid(texture, choice))
            return;
        _weightChoices[texture.Key] = choice;
        if (choice.IsNone)
            _weightTicked.Remove(texture.Key);
        _weightPlanStale = true;
    }

    private static void WeightTextureTooltip(CharacterWeightAnalysis analysis, WeightTexture texture)
    {
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(520));
        ImGui.TextColored(Theme.Text, texture.Label);
        foreach (var gamePath in texture.GamePaths)
            ImGui.TextColored(Theme.Muted, gamePath);
        if (texture.Slots.Count > 0)
            ImGui.TextUnformatted("Used by: " + string.Join(", ", texture.Slots));
        foreach (var use in texture.Uses)
            ImGui.TextColored(Theme.Muted, use);
        if (analysis.Usage.TryGetValue(texture.Key, out var usage) && CharacterWeightViews.Usage(usage) is { Length: > 0 } options)
            ImGui.TextUnformatted("In its mod: " + options);
        if (!texture.CountsForSync)
            ImGui.TextColored(Theme.Hint, "Sync plugins don't count this file.");
    }

    private static void DrawWeightModels(CharacterWeightReport report)
    {
        Widgets.HintWrapped("Triangles are reported only; nothing here reduces them.");
        using var table = ImRaii.Table("##weight-model-table", 5,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable,
            new Vector2(0, Math.Max(Theme.Scaled(160), ImGui.GetContentRegionAvail().Y)));
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Share", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Counted by sync plugins", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();
        foreach (var model in report.Models)
        {
            using var id = ImRaii.PushId(model.Key);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(model.Source.State == ResourceSourceState.GameData ? Theme.GameSource : Theme.Text, model.FileName);
            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                ImGui.TextColored(Theme.Text, model.Label);
                foreach (var gamePath in model.GamePaths)
                    ImGui.TextColored(Theme.Muted, gamePath);
                if (model.Error.Length > 0)
                    ImGui.TextColored(Theme.Warning, model.Error);
            }
            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(Theme.Label, string.Join(", ", model.Slots));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(Theme.Text, CharacterWeightViews.Count(model.Triangles));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextColored(Theme.Muted, report.AllTriangles > 0 ? $"{100.0 * model.Triangles / report.AllTriangles:0.0}%" : "");
            ImGui.TableSetColumnIndex(4);
            ImGui.TextColored(Theme.Muted, model.CountsForSync ? "yes" : model.Error.Length > 0 ? "unreadable" : "no, game file");
        }
    }
}
