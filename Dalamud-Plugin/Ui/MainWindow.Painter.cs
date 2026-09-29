using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Painter;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string PainterDialog = "Paint in Substance Painter";
    private const string PainterDiscardDialog = "Discard Painter project";
    private PainterJobService? _painter;
    private PainterDraft? _painterDraft;
    private string _painterDraftError = "";
    private string _painterLoadingText = "";
    private int _painterBusy;
    private bool _openPainterDialog;
    private Guid? _discardPainterJob;
    private PainterStatus _painterStatus = PainterStatus.Offline;
    private DateTime _lastPainterCheck = DateTime.MinValue;
    private bool _painterChecking;

    internal void AttachPainter(PainterJobService painter)
    {
        _painter = painter;
        painter.Message += ReportPainterMessage;
    }

    private void DetachPainter()
    {
        if (_painter is not null)
            _painter.Message -= ReportPainterMessage;
    }

    private void ReportPainterMessage(string text, bool problem)
        => SetTextureStatus(text, problem ? FeedbackSeverity.Warning : FeedbackSeverity.Success);

    private bool PainterEnabled => _painter is not null && _config.PainterIntegrationEnabled;

    /// <summary> Painter only opens On Screen models: their textures are the ones the actor actually renders. </summary>
    private bool CanPaint(ActorView actor, ResourceView node)
        => PainterEnabled && actor.Entity is not null && node.IsModel && ResourceViews.IsSafeModel(node);

    private PainterConnectionState PainterState
    {
        get
        {
            StartPainterCheckIfNeeded();
            lock (_stateLock) return _painterStatus.Classify(_pluginVersion);
        }
    }

    private void StartPainterCheckIfNeeded()
    {
        if (_painter is not { } painter)
            return;
        lock (_stateLock)
        {
            if (_painterChecking || (DateTime.UtcNow - _lastPainterCheck).TotalSeconds <= 5)
                return;
            _painterChecking = true;
        }
        _ = Task.Run(async () =>
        {
            var status = PainterStatus.Offline;
            try { status = await painter.GetStatusAsync(_lifetimeCts.Token).ConfigureAwait(false); }
            catch (Exception e) { _log.Debug($"Painter status check failed: {e.Message}"); }
            finally
            {
                lock (_stateLock)
                {
                    _painterStatus = status;
                    _painterChecking = false;
                    _lastPainterCheck = DateTime.UtcNow;
                }
            }
        });
    }

    private static (string Value, string Detail, Vector4 Colour) PainterDot(PainterConnectionState state, string pluginVersion) => state switch
    {
        PainterConnectionState.Online => ("Online", "Substance Painter's XIV Instant Edit plugin is reachable and matches this version.", Theme.Online),
        PainterConnectionState.VersionMismatch => ("Mismatch", $"Update the Painter plugin from Settings; it doesn't match plugin version {pluginVersion}.", Theme.Mismatch),
        _ => ("Offline", "Start Substance Painter with the XIV Instant Edit plugin enabled to paint textures.", Theme.Offline),
    };

    // ---- Opening the dialog ------------------------------------------------------------------

    private void StartPainter(ActorView actor, ResourceView node)
    {
        var others = actor.Roots.SelectMany(ResourceViews.Flatten)
            .Where(other => other.IsModel && ResourceViews.IsSafeModel(other) && !ReferenceEquals(other, node))
            .Select(ModelRef)
            .ToList();
        PreparePainter(actor, ModelRef(node), others, PainterScope.Model, _config.ApplyRacialScaling, "Reading the model, its materials and textures");
    }

    /// <summary> Opens the dialog and prepares its draft in the background. </summary>
    /// <param name="scale">Whether models of another race are reshaped for the actor, as the game shows them on it.</param>
    private void PreparePainter(ActorView actor, PainterModelRef model, IReadOnlyList<PainterModelRef> others, PainterScope scope, bool scale,
        string loadingText)
    {
        if (_painter is not { } painter || actor.Entity is not { } entity || Interlocked.CompareExchange(ref _painterBusy, 1, 0) != 0)
            return;
        _painterDraft = null;
        _painterDraftError = "";
        _painterLoadingText = loadingText;
        _openPainterDialog = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var resources = await ResolvePreviewResourcesAsync(actor).ConfigureAwait(false);
                var (scaling, scalingProblem) = scale && _skeletons is { } skeletons
                    ? await skeletons.RacialScalingAsync(entity.ObjectIndex, entity.Address, _lifetimeCts.Token).ConfigureAwait(false)
                    : (null, null);
                var request = new PainterRequest(entity.ObjectIndex, entity.Address.ToInt64(), actor.Name, model, others, resources.ToList())
                {
                    Scope = scope,
                    RacialScaling = scaling,
                    RacialScalingProblem = scalingProblem,
                };
                _painterDraft = await painter.PrepareAsync(request, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not prepare the Substance Painter project.");
                _painterDraftError = error.Message;
            }
            finally { Interlocked.Exchange(ref _painterBusy, 0); }
        });
    }

    private static PainterModelRef ModelRef(ResourceView node)
        => new(node.GamePath, node.ActualPath, node.SourceState == ResourceSourceState.GameData);

    // ---- The dialog --------------------------------------------------------------------------

    private void DrawPainterDialogs()
    {
        DrawPainterDiscardDialog();
        if (_openPainterDialog)
        {
            _openPainterDialog = false;
            ImGui.OpenPopup(PainterDialog);
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(560, 200), Theme.Scaled(820, 720));
        if (!ImGui.BeginPopupModal(PainterDialog, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var busy = Volatile.Read(ref _painterBusy) != 0;
        var draft = _painterDraft;
        if (draft is null)
        {
            if (_painterDraftError.Length > 0)
                Widgets.Banner("##painter-error", FeedbackSeverity.Error, _painterDraftError);
            else
                Widgets.HintWrapped(_painterLoadingText);
            ImGui.Spacing();
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var skin = draft.Request.Scope == PainterScope.Skin;
        ImGui.TextColored(Theme.Text, draft.Title);
        Widgets.HintWrapped(skin
            ? "Painter gets every part of your character that shows skin, with the body skin as one texture set and the current textures as its bottom layer, " +
              "so paint can cross the wrists, waist and ankles. Tick the face to paint across the neck too. " +
              "Press Send to game in Painter's XIV Instant Edit panel to apply the ticked textures; each goes through a texture session with a backup."
            : "Painter gets the mesh and one texture set per material, with the current textures as its bottom layer. " +
              "Press Send to game in Painter's XIV Instant Edit panel to apply the ticked textures; each goes through a texture session with a backup.");
        ImGui.Spacing();

        using (var list = ImRaii.Child("##painter-list", new Vector2(Theme.Scaled(720), Theme.Scaled(380)), true))
        {
            if (list.Success)
            {
                foreach (var set in draft.Sets)
                    DrawPainterSet(set, draft.InProject(set));
                if (skin)
                {
                    Widgets.SectionHeader("Models", "every model that shows your skin");
                    using (ImRaii.Disabled())
                    {
                        var always = true;
                        ImGui.Checkbox($"{draft.Main.Model.FileName}##painter-main", ref always);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("Shows the most skin, so it is always part of the project.");
                    DrawPainterModelSets(draft.Main);
                    foreach (var sibling in draft.Siblings)
                        DrawPainterSibling(sibling);
                    ImGui.Spacing();
                }
                else if (draft.Siblings.Count > 0)
                {
                    Widgets.SectionHeader("Models sharing these materials", "their areas can be painted in the same texture set");
                    foreach (var sibling in draft.Siblings)
                        DrawPainterSibling(sibling);
                    ImGui.Spacing();
                }
                if (draft.Warnings.Count > 0)
                {
                    Widgets.SectionHeader("Notes");
                    foreach (var warning in draft.Warnings.Take(8))
                        Widgets.HintWrapped(warning);
                }
            }
        }

        if (draft.NeedsModName)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, "New mod for vanilla textures");
            ImGui.SameLine(0, Theme.Gap);
            var name = draft.NewModName;
            ImGui.SetNextItemWidth(Theme.Scaled(320));
            if (ImGui.InputTextWithHint("##painter-mod-name", "Mod name", ref name, 120))
                draft.NewModName = name;
            Widgets.HelpTip("The first send that changes a vanilla texture creates this mod and enables it in the actor's collection. Later vanilla textures from this project join it.");
        }

        ImGui.Spacing();
        var state = PainterState;
        var (value, detail, colour) = PainterDot(state, _pluginVersion);
        ImGui.AlignTextToFramePadding();
        Widgets.StatusDot("Painter", colour, value, detail);
        if (state == PainterConnectionState.Offline)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.HintWrapped("Painter starts automatically when sending, if its executable is found.");
        }

        ImGui.Spacing();
        var selectedCount = draft.SelectedTextures.Count();
        using (ImRaii.Disabled(busy || selectedCount == 0 || (draft.NeedsModName && string.IsNullOrWhiteSpace(draft.NewModName))))
        {
            if (ImGui.Button(selectedCount == 1 ? "Send to Painter (1 texture)" : $"Send to Painter ({selectedCount} textures)"))
            {
                ImGui.CloseCurrentPopup();
                SendToPainter(draft);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _painterDraft = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <param name="inProject">Whether a ticked model draws the set; its textures can't be ticked otherwise.</param>
    private static void DrawPainterSet(PainterDraftSet set, bool inProject)
    {
        Widgets.SectionHeader(set.Label, set.ShaderPackage);
        if (set.Textures.Count == 0)
        {
            Widgets.HintWrapped(set.Problem.Length > 0 ? set.Problem : "No textures could be read for this material.");
            ImGui.Spacing();
            return;
        }
        if (!inProject)
            Widgets.HintWrapped("Tick its model below to paint it too.");
        using var outside = ImRaii.Disabled(!inProject);
        foreach (var texture in set.Textures)
        {
            using var id = ImRaii.PushId($"{set.Name}|{texture.Texture.SamplerId:X8}|{texture.Texture.GamePath}");
            var selected = texture.Selected && texture.Editable;
            using (ImRaii.Disabled(!texture.Editable))
                if (ImGui.Checkbox("##send", ref selected))
                    texture.Selected = selected;
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Badge(texture.Role, RoleColour(texture.Texture.Usage));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(texture.Editable ? Theme.Text : Theme.Muted, Path.GetFileName(texture.Texture.GamePath));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(texture.Texture.GamePath);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Hint, $"{texture.Texture.Width}×{texture.Texture.Height} {FormatLabel(texture.Texture.Format)} · {SourceLabel(texture.Texture)}");
            var note = texture.Reason.Length > 0 ? texture.Reason : texture.Note;
            if (note.Length > 0)
            {
                ImGui.Indent(ImGui.GetFrameHeight() + Theme.Gap);
                ImGui.TextColored(texture.Reason.Length > 0 ? Theme.Hint : Theme.Warning, note);
                ImGui.Unindent(ImGui.GetFrameHeight() + Theme.Gap);
            }
        }
        ImGui.Spacing();
    }

    private static void DrawPainterSibling(PainterDraftModel sibling)
    {
        var selected = sibling.Selected;
        if (ImGui.Checkbox($"{sibling.Model.FileName}##sibling-{sibling.Model.SourcePath}", ref selected))
            sibling.Selected = selected;
        DrawPainterModelSets(sibling);
    }

    /// <summary> After a model's checkbox: the texture sets it draws, and the builder's note about it. </summary>
    private static void DrawPainterModelSets(PainterDraftModel model)
    {
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, string.Join(", ", model.SetByMaterial.Values.Distinct()));
        if (model.Note.Length == 0)
            return;
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, model.Note);
    }

    private static Vector4 RoleColour(string usage) => usage switch
    {
        "diffuse" => Theme.BaseTexture,
        "normal" => Theme.NormalTexture,
        "mask" => Theme.MaskTexture,
        "index" => Theme.IndexTexture,
        _ => Theme.OtherTexture,
    };

    private static string FormatLabel(uint format)
    {
        try { return TextureFiles.FormatName(format); }
        catch (NotSupportedException) { return $"0x{format:X4}"; }
    }

    private static string SourceLabel(TexturePlanTexture texture) => texture.Locator?.Kind switch
    {
        "mod" => texture.Locator.SourceModDirectory ?? "Mod",
        "game" => "Vanilla",
        _ => "External",
    };

    private void SendToPainter(PainterDraft draft)
    {
        if (_painter is not { } painter || Interlocked.CompareExchange(ref _painterBusy, 1, 0) != 0)
            return;
        SetTextureStatus("Preparing the Painter project", FeedbackSeverity.Info);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await painter.CreateAsync(draft, _lifetimeCts.Token).ConfigureAwait(false);
                var severity = result.JobId is null ? FeedbackSeverity.Error : result.Warnings.Count > 0 ? FeedbackSeverity.Warning : FeedbackSeverity.Success;
                var text = result.Warnings.Count == 0 ? result.Message : $"{result.Message} {string.Join(" ", result.Warnings.Take(3))}";
                SetTextureStatus(text, severity);
                if (result.JobId is not null)
                    _painterDraft = null;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not open the model in Substance Painter.");
                SetTextureStatus("Substance Painter: " + error.Message, FeedbackSeverity.Error);
            }
            finally { Interlocked.Exchange(ref _painterBusy, 0); }
        });
    }

    // ---- Sessions tab ------------------------------------------------------------------------

    private void DrawPainterJobs()
    {
        if (_painter is not { } painter || !_config.PainterIntegrationEnabled)
            return;
        if (painter.StoreError.Length > 0)
        {
            Widgets.Banner("##painter-store-error", FeedbackSeverity.Error, painter.StoreError);
            ImGui.Spacing();
        }
        var jobs = painter.Jobs.OrderByDescending(job => job.LastSent ?? job.Created).ToList();
        if (jobs.Count == 0)
            return;
        Widgets.SectionHeader("Substance Painter projects", jobs.Count == 1 ? "1 project" : $"{jobs.Count} projects");
        var busy = Volatile.Read(ref _painterBusy) != 0;
        foreach (var job in jobs)
        {
            using var id = ImRaii.PushId(job.Id.ToString("N"));
            var (label, colour) = job.State switch
            {
                PainterJobState.Ready => ("Ready", Theme.Watching),
                PainterJobState.Failed => ("Needs attention", Theme.Conflict),
                _ => ("Opening", Theme.Info),
            };
            Widgets.Badge(label, colour);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Text, job.DisplayName);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, job.Targets.Count == 1 ? "1 texture" : $"{job.Targets.Count} textures");
            if (job.Message.Length > 0)
                Widgets.HintWrapped(job.Message + (job.LastSent is { } sent ? $" · last send {sent.ToLocalTime():g}" : ""));
            if (job.ProjectPath.Length > 0)
                Widgets.HintWrapped("Project: " + job.ProjectPath);
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.Button("Open in Painter"))
                    ReopenPainterJob(job);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(job.ProjectPath.Length > 0
                        ? "Opens the saved project in Painter (start Painter from here, or open the file in Painter yourself)."
                        : "Creates the project in Painter again from the files this project started with.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard"))
                _discardPainterJob = job.Id;
            ImGui.Spacing();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void ReopenPainterJob(PainterJob job)
    {
        if (_painter is not { } painter || Interlocked.CompareExchange(ref _painterBusy, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (job.ProjectPath.Length > 0 && File.Exists(job.ProjectPath))
                {
                    var status = await painter.GetStatusAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    if (status.Reachable)
                    {
                        SetTextureStatus($"Painter is running: open {job.ProjectPath} there (File > Open).", FeedbackSeverity.Info);
                        return;
                    }
                    var exe = string.IsNullOrWhiteSpace(_config.PainterExecutablePath) ? PainterInstallation.DetectExecutable() : _config.PainterExecutablePath;
                    if (exe.Length == 0 || !File.Exists(exe))
                    {
                        SetTextureStatus("Set Substance Painter's executable in Settings, or open the project in Painter yourself.", FeedbackSeverity.Warning);
                        return;
                    }
                    var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                    start.ArgumentList.Add(job.ProjectPath);
                    Process.Start(start);
                    SetTextureStatus("Opening the project in Substance Painter", FeedbackSeverity.Info);
                    return;
                }
                var error = await painter.OpenInPainterAsync(job, _lifetimeCts.Token).ConfigureAwait(false);
                SetTextureStatus(error.Length == 0 ? "Opening the project in Substance Painter" : error,
                    error.Length == 0 ? FeedbackSeverity.Info : FeedbackSeverity.Warning);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error)
            {
                SetTextureStatus("Substance Painter: " + error.Message, FeedbackSeverity.Error);
            }
            finally { Interlocked.Exchange(ref _painterBusy, 0); }
        });
    }

    private void DrawPainterDiscardDialog()
    {
        if (_discardPainterJob.HasValue && !ImGui.IsPopupOpen(PainterDiscardDialog))
            ImGui.OpenPopup(PainterDiscardDialog);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (!ImGui.BeginPopupModal(PainterDiscardDialog, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        Widgets.SectionHeader("Discard this Painter project?");
        Widgets.HintWrapped("Instant Edit forgets the project: Painter can no longer send its textures, and the texture sessions it opened and its cached files are removed. " +
                            "The saved Painter file, the mods and their backups stay.");
        ImGui.Spacing();
        if (ImGui.Button("Discard project") && _discardPainterJob is { } id && _painter is { } painter)
        {
            _discardPainterJob = null;
            ImGui.CloseCurrentPopup();
            _ = Task.Run(async () =>
            {
                try
                {
                    await painter.DiscardAsync(id).ConfigureAwait(false);
                    SetTextureStatus("Painter project discarded.", FeedbackSeverity.Success);
                }
                catch (Exception error) { SetTextureStatus(error.Message, FeedbackSeverity.Error); }
            });
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep"))
        {
            _discardPainterJob = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
