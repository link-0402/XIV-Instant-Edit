using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

/// <summary>
/// The physics builder: body physics for your character's skeleton, started from the game's file,
/// the one your mods load or any file, tuned per body area with plain sliders, previewed on your
/// character and saved into a mod named as you like, which loading it again opens. In GPose it works
/// on your posed character, which can walk or run in place while you watch.
/// </summary>
internal sealed partial class PhysicsWindow : Window, IDisposable
{
    private const string DiscardPopup = "##physics-discard";
    private const string SavePopup = "##physics-save";
    private const int AutoPreviewDelayMs = 400;
    private const int ShapesView = -2;

    private readonly PhysicsBuilderService service;
    private readonly AnimationPlayback playback;
    private readonly Configuration config;
    private readonly Action saveConfig;
    private readonly IPluginLog log;
    private readonly FileDialogManager fileDialog = new();
    private readonly CancellationTokenSource life = new();

    private PhysicsTarget? target;
    private Task<PhysicsTarget>? finding;
    // A read asked for while one ran, which may have read before what changed.
    private bool findAgain;
    private string? findProblem;
    private PhysicsDocument? document;
    private Task<PhysicsDocument>? loading;
    private Task<PhybFile>? importing;
    private string importName = string.Empty;
    private PhybFile? importFile;
    private Task<string?>? working;
    private Func<string?, string>? workDone;
    private string message = string.Empty;
    private FeedbackSeverity messageSeverity = FeedbackSeverity.Info;
    private (PhysicsSourceKind Kind, string? File)? pendingStart;
    private int selected = -1;
    private long autoPreviewDue;
    private int issuesRevision = -1;
    private PhysicsDocument? issuesDocument;
    private IReadOnlyList<PhysicsIssue> issues = [];
    private string lastFolder = string.Empty;
    private string saveName = string.Empty;

    internal PhysicsWindow(PhysicsBuilderService service, AnimationPlayback playback, Configuration config, Action saveConfig, IPluginLog log)
        : base("Body Physics (Beta)###InstantEditPhysics")
    {
        this.service = service;
        this.playback = playback;
        this.config = config;
        this.saveConfig = saveConfig;
        this.log = log;
        overlayWindow = new PhysicsOverlay(this);
        Size = new Vector2(960, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(720, 420), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        AllowPinning = true;
        AllowBackgroundBlur = true;
    }

    public void Open()
    {
        IsOpen = true;
        Find();
    }

    public void DrawFileDialog() => fileDialog.Draw();

    public override void OnClose()
    {
        StopMoving();
        autoPreviewDue = 0;
        // The preview is a look at unsaved settings; closing the builder puts your saved physics back,
        // also after a preview that was still on its way (unless the builder is open again by then).
        if (!service.Preview.Active && working == null)
            return;
        var found = target;
        _ = ((Task?)working ?? Task.CompletedTask)
            .ContinueWith(_ => IsOpen ? Task.CompletedTask : service.StopPreviewAsync(found), TaskScheduler.Default).Unwrap()
            .ContinueWith(task => log.Warning(task.Exception!, "Could not stop the physics preview."), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        life.Cancel();
        life.Dispose();
    }

    private bool Busy => finding != null || loading != null || importing != null || working != null || fitting != null || refitting != null ||
                         service.Preview.Busy;

    private void Find()
    {
        if (finding != null)
        {
            // The one running may have read before a preview or save landed: read again after it.
            findAgain = true;
            return;
        }
        findAgain = false;
        findProblem = null;
        finding = Task.Run(service.FindAsync);
    }

    public override void Draw()
    {
        Poll();
        TakeHover();
        DrawHeader();
        ImGui.Separator();
        DrawStart();
        if (document is not { } current)
        {
            Widgets.EmptyState(FontAwesomeIcon.Feather, loading != null ? "Loading" : "Pick a starting point",
                "The game's file has your race's own skirt, hair, earring and tail physics. Start there to build body physics from scratch, " +
                "or from what your mods load now to change it.");
            DrawDiscardPopup();
            return;
        }
        if (current.Race != target?.Character.Race && target != null)
            Widgets.Banner("##physics-race", FeedbackSeverity.Warning,
                $"This physics is for c{current.Race:D4}, and your character is c{target.Character.Race:D4} now. Start again to edit it for your race.");
        DrawEditingRow(current);
        var bottom = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y - bottom);
        DrawAreas(current, height);
        ImGui.Separator();
        DrawActions(current);
        DrawSavePopup(current);
        DrawDiscardPopup();
        DrawImportPopup(current);
        DrawFitPopup(current);
        HandleShortcuts(current);
        PumpAutoPreview(current);
    }

    // ---- Tasks ----

    private void Poll()
    {
        HandlePicked();
        PollFit();
        KeepMoving();
        if (finding is { IsCompleted: true } found)
        {
            finding = null;
            if (found.IsCompletedSuccessfully)
                target = found.Result;
            else
                findProblem = Unwrap(found.Exception);
            if (findAgain)
                Find();
        }
        if (loading is { IsCompleted: true } loaded)
        {
            loading = null;
            if (loaded.IsCompletedSuccessfully)
            {
                document = loaded.Result;
                selected = document.File.Simulators.Count > 0 ? 0 : -1;
                Say(FeedbackSeverity.Info, $"Started from {document.Source.Label}.");
            }
            else
                Say(FeedbackSeverity.Error, Unwrap(loaded.Exception));
        }
        if (importing is { IsCompleted: true } imported)
        {
            importing = null;
            if (imported.IsCompletedSuccessfully)
            {
                importFile = imported.Result;
                ImGui.OpenPopup(ImportPopup);
            }
            else
                Say(FeedbackSeverity.Error, Unwrap(imported.Exception));
        }
        if (working is { IsCompleted: true } work)
        {
            working = null;
            var done = workDone;
            workDone = null;
            // Previews, saves and turning the mod off all redraw your character.
            motionCheckDue = Environment.TickCount64 + MotionCheckDelayMs;
            if (work.IsCompletedSuccessfully)
                Say(work.Result is null ? FeedbackSeverity.Success : FeedbackSeverity.Warning, done?.Invoke(work.Result) ?? work.Result ?? "Done.");
            else
                Say(FeedbackSeverity.Error, Unwrap(work.Exception));
            Find();
        }
        if (service.Preview.TakeNotice() is { } notice)
        {
            // The check after a preview's redraw added the preview again and redrew your character.
            motionCheckDue = Environment.TickCount64 + MotionCheckDelayMs;
            if (notice.Warning != null)
                Say(FeedbackSeverity.Warning, notice.Warning);
            Find();
        }
    }

    private void Run(Func<Task<string?>> action, Func<string?, string> done)
    {
        if (working != null)
            return;
        // A save, a stop or a preview of its own replaces a pending automatic preview, which would show the preview again after it.
        autoPreviewDue = 0;
        workDone = done;
        working = Task.Run(action);
    }

    private void Say(FeedbackSeverity severity, string text)
    {
        messageSeverity = severity;
        message = text;
    }

    private static string Unwrap(AggregateException? error)
    {
        var inner = error?.InnerExceptions.Count == 1 ? error.InnerExceptions[0] : error;
        return inner switch
        {
            OperationCanceledException => "Cancelled.",
            null => "Something went wrong.",
            _ => inner.Message,
        };
    }

    // ---- Header and starting points ----

    private void DrawHeader()
    {
        if (target is { } found)
        {
            var character = found.Character;
            Widgets.Icon(FontAwesomeIcon.User, Theme.Accent);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Text, character.Name);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, $"{Services.GameFiles.GameRaces.Label(character.Race)} (c{character.Race:D4})");
            ImGui.SameLine(0, Theme.Gap * 2);
            ImGui.TextColored(Theme.Label, $"{character.Skeleton.Family} skeleton");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, $"{character.Skeleton.Count} bones, from {found.Skeleton.Label}");
        }
        else
        {
            Widgets.Icon(FontAwesomeIcon.User, Theme.Inactive);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Hint, finding != null ? "Looking for your character" : findProblem ?? "Your character isn't found yet.");
        }
        var button = ImGui.GetFrameHeight();
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - button));
        if (Widgets.IconButton("##physics-refresh", FontAwesomeIcon.Sync, "Read your character and its mods again", finding == null))
            Find();
        if (target is { } loaded)
        {
            ImGui.TextColored(Theme.Muted, "Body physics loaded now:");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(loaded.Physics.Mine ? Theme.Success : loaded.Physics.Preview ? Theme.Info : Theme.Label, loaded.Physics.Label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{loaded.Physics.ResolvedPath}\nCollection: {loaded.CollectionName}");
        }
    }

    private void DrawStart()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Start from");
        ImGui.SameLine(0, Theme.Gap * 2);
        using (ImRaii.Disabled(target == null || Busy))
        {
            if (ImGui.Button("The game's file"))
                Start(PhysicsSourceKind.Vanilla, null);
            Tip("Your race's own physics: skirt, hair, earrings, tail and chest. Add body areas to it.");
            ImGui.SameLine();
            if (ImGui.Button("Loaded now"))
                Start(PhysicsSourceKind.Loaded, null);
            Tip(target is { } found ? $"What your mods load now: {found.Physics.Label}." : "What your mods load now.");
            ImGui.SameLine();
            if (ImGui.Button("A file"))
                OpenFileDialog(import: false);
            Tip("Any .phyb file, such as a preset from a physics mod.");
        }
        if (loading != null)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
    }

    private void Start(PhysicsSourceKind kind, string? file)
    {
        if (document is { Dirty: true })
        {
            pendingStart = (kind, file);
            ImGui.OpenPopup(DiscardPopup);
            return;
        }
        Load(kind, file);
    }

    private void Load(PhysicsSourceKind kind, string? file)
    {
        if (target is not { } found || loading != null)
            return;
        loading = Task.Run(() => service.LoadAsync(found, kind, file));
    }

    private void OpenFileDialog(bool import)
    {
        var start = lastFolder.Length > 0 && Directory.Exists(lastFolder) ? lastFolder : null;
        // The dialog calls back while it draws, outside this window; the pick is handled in Poll.
        fileDialog.OpenFileDialog(import ? "Import an area from a physics file" : "Open a physics file", "Physics files{.phyb}", (success, files) =>
        {
            if (success && files.Count > 0)
                picked = (files[0], import);
        }, 1, start, false);
    }

    private (string File, bool Import)? picked;

    private void HandlePicked()
    {
        if (picked is not { } pick)
            return;
        picked = null;
        lastFolder = Path.GetDirectoryName(pick.File) ?? lastFolder;
        if (pick.Import)
        {
            importName = Path.GetFileName(pick.File);
            importing = Task.Run(() => PhysicsBuilderService.ReadFileAsync(pick.File));
        }
        else
            Start(PhysicsSourceKind.File, pick.File);
    }

    private void DrawDiscardPopup()
    {
        using var popup = ImRaii.Popup(DiscardPopup);
        if (!popup.Success)
            return;
        ImGui.TextColored(Theme.Text, "Discard your unsaved changes?");
        if (ImGui.Button("Discard##physics-discard-yes") && pendingStart is { } start)
        {
            pendingStart = null;
            Load(start.Kind, start.File);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing##physics-discard-no"))
        {
            pendingStart = null;
            ImGui.CloseCurrentPopup();
        }
    }

    // ---- Editing row, actions ----

    private void DrawEditingRow(PhysicsDocument current)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Editing");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, current.Source.Label);
        if (ImGui.IsItemHovered() && current.Source.Path.Length > 0)
            ImGui.SetTooltip(current.Source.Path);
        if (current.Dirty)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Badge("Unsaved", Theme.Warning);
        }
        ImGui.SameLine();
        var history = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X;
        var right = ImGui.GetFrameHeight() + Theme.Gap * 3 + FitButtonWidth + Theme.Gap * 3 + MotionsWidth + Theme.Gap * 3 + history;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - right));
        DrawOverlayToggle();
        ImGui.SameLine(0, Theme.Gap * 3);
        DrawFitButton(current);
        ImGui.SameLine(0, Theme.Gap * 3);
        DrawMotions();
        ImGui.SameLine(0, Theme.Gap * 3);
        if (Widgets.IconButton("##physics-undo", FontAwesomeIcon.Undo, null, current.CanUndo))
            Undo(current);
        HistoryTip(current, undo: true);
        ImGui.SameLine();
        if (Widgets.IconButton("##physics-redo", FontAwesomeIcon.Redo, null, current.CanRedo))
            Redo(current);
        HistoryTip(current, undo: false);
    }

    private (PhysicsDocument? Document, int Revision, int History) historyShown;
    private string undoTip = string.Empty;
    private string redoTip = string.Empty;

    /// <summary> Undo's and redo's tooltip: what the step changes, in the controls' own words and values. </summary>
    private void HistoryTip(PhysicsDocument current, bool undo)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        if (!ReferenceEquals(historyShown.Document, current) || historyShown.Revision != current.Revision || historyShown.History != current.History)
        {
            historyShown = (current, current.Revision, current.History);
            undoTip = StepText("Undo (Ctrl+Z)", current, current.NextUndo());
            redoTip = StepText("Redo (Ctrl+Y)", current, current.NextRedo());
        }
        Tip(undo ? undoTip : redoTip);
    }

    private static string StepText(string action, PhysicsDocument current, PhysicsStep? step)
    {
        if (step is null)
            return action;
        var lines = new List<string> { action };
        if (step.Name is { } name)
            lines.Add(name);
        lines.AddRange(PhysicsChanges.Describe(current.File, step.File));
        return string.Join("\n", lines);
    }

    private void Undo(PhysicsDocument current)
    {
        if (current.Undo())
            Edited(current);
    }

    private void Redo(PhysicsDocument current)
    {
        if (current.Redo())
            Edited(current);
    }

    private void HandleShortcuts(PhysicsDocument current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput || !ImGui.GetIO().KeyCtrl)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z))
            Undo(current);
        else if (ImGui.IsKeyPressed(ImGuiKey.Y))
            Redo(current);
    }

    /// <summary> After a finished change: keep the selection valid and queue the automatic preview. </summary>
    private void Edited(PhysicsDocument current)
    {
        if (selected >= current.File.Simulators.Count)
            selected = current.File.Simulators.Count - 1;
        if (config.PhysicsAutoPreview && target != null)
            autoPreviewDue = Environment.TickCount64 + AutoPreviewDelayMs;
    }

    private void PumpAutoPreview(PhysicsDocument current)
    {
        if (autoPreviewDue == 0 || Environment.TickCount64 < autoPreviewDue || working != null || ImGui.IsAnyItemActive())
            return;
        autoPreviewDue = 0;
        if (Issues(current).Any(issue => issue.Level == PhysicsIssueLevel.Error))
        {
            Say(FeedbackSeverity.Warning, "Not previewed: fix the problems marked in red first.");
            return;
        }
        StartPreview(current);
    }

    /// <summary> The file to write, made here on the thread that edits the document; null after saying why it can't be. </summary>
    private byte[]? Prepare(PhysicsDocument current, PhysicsTarget found)
    {
        try
        {
            return PhysicsBuilderService.Prepare(current, found);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            Say(FeedbackSeverity.Error, e.Message);
            return null;
        }
    }

    private void StartPreview(PhysicsDocument current)
    {
        if (target is not { } found || Prepare(current, found) is not { } bytes)
            return;
        Run(() => service.PreviewAsync(found, bytes, life.Token),
            warning => warning ?? (service.Preview.Shared
                ? $"Previewing on your character. Others using {service.Preview.CollectionName} see it too."
                : "Previewing on your character. Your mods aren't changed until you save."));
    }

    private void DrawActions(PhysicsDocument current)
    {
        var errors = Issues(current).Count(issue => issue.Level == PhysicsIssueLevel.Error);
        using (ImRaii.Disabled(target == null || Busy || errors > 0))
        {
            if (Widgets.TabButton(FontAwesomeIcon.Eye, "Preview", false))
                StartPreview(current);
        }
        Tip(errors > 0 ? "Fix the problems marked in red first." : "Shows these settings on your character. Your mods aren't changed until you save.");
        ImGui.SameLine();
        var auto = config.PhysicsAutoPreview;
        if (ImGui.Checkbox("Auto##physics-auto", ref auto))
        {
            config.PhysicsAutoPreview = auto;
            saveConfig();
        }
        Tip("Preview after every change, once you let go of the control.");
        ImGui.SameLine();
        using (ImRaii.Disabled(!service.Preview.Active || Busy))
        {
            if (ImGui.Button("Stop preview"))
                Run(async () =>
                {
                    await service.StopPreviewAsync(target).ConfigureAwait(false);
                    return null;
                }, _ => "Preview off. Your character shows your saved physics again.");
        }
        ImGui.SameLine(0, Theme.Gap * 3);
        using (ImRaii.Disabled(target == null || Busy || errors > 0))
        {
            if (Widgets.TabButton(FontAwesomeIcon.Save, "Save", current.Dirty))
            {
                saveName = SuggestedName(current);
                ImGui.OpenPopup(SavePopup);
            }
        }
        Tip("Saves into a mod above your other mods. Give each version its own name to keep several.");
        if (service.HasMod)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(target == null || Busy))
            {
                if (ImGui.Button("Turn mine off"))
                    TurnOff();
            }
            Tip("Stops the preview and turns off the physics mods you saved here. Your other physics mods apply again.");
        }
        if (working != null || service.Preview.Busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }

        if (message.Length > 0)
        {
            var (accent, _, icon) = Theme.Severity(messageSeverity);
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, Theme.Gap);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Text, message);
        }
    }

    private void DrawSavePopup(PhysicsDocument current)
    {
        using var popup = ImRaii.Popup(SavePopup);
        if (!popup.Success)
            return;
        ImGui.TextColored(Theme.Label, "Mod name");
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var enter = ImGui.InputText("##physics-save-name", ref saveName, 100, ImGuiInputTextFlags.EnterReturnsTrue);
        var name = saveName.Trim();
        var valid = Services.PenumbraMods.ModPaths.IsSafeNewModName(name);
        ImGui.TextColored(valid ? Theme.Muted : Theme.Warning, name.Length == 0 ? "Type a name."
            : !valid ? "Use a name without \\ / : * ? \" < > |."
            : service.IsMine(name) ? $"Replaces what you saved in {name}."
            : "Makes a new mod.");
        if (service.ModDirectories.Any(mod => !string.Equals(mod, name, StringComparison.OrdinalIgnoreCase)))
            ImGui.TextColored(Theme.Muted, "Your other saved physics mods turn off.");
        // A save waits for a running preview, which Run would otherwise drop it for.
        var waiting = working != null;
        if (waiting)
            ImGui.TextColored(Theme.Muted, "Waiting for the preview to finish.");
        using (ImRaii.Disabled(!valid || waiting))
        {
            if ((ImGui.Button("Save##physics-save-yes") || enter) && valid && !waiting)
            {
                Save(current, name);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##physics-save-no"))
            ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// The name the save offers: the builder mod the file came from, followed to its new folder when it
    /// was renamed in Penumbra (what loads now, if that is one of yours), else a free name.
    /// </summary>
    private string SuggestedName(PhysicsDocument current)
    {
        if (current.Source is not { Kind: PhysicsSourceKind.Mine, Mod.Length: > 0 } source)
            return service.FreeModName();
        if (service.IsMine(source.Mod))
            return source.Mod;
        return target?.Physics is { Mine: true, Mod.Length: > 0 } loaded && service.IsMine(loaded.Mod) ? loaded.Mod : service.FreeModName();
    }

    private void Save(PhysicsDocument current, string name)
    {
        if (target is not { } found || Prepare(current, found) is not { } bytes)
            return;
        PhysicsSaveResult? result = null;
        Run(async () =>
        {
            result = await service.SaveAsync(found, bytes, name, life.Token).ConfigureAwait(false);
            return result.Warnings.Count == 0 ? null : string.Join(" ", result.Warnings);
        }, warning =>
        {
            var into = result?.Directory ?? name;
            current.MarkSaved(new PhysicsSource(PhysicsSourceKind.Mine, into, into, into), bytes);
            var off = result is { TurnedOff.Count: > 0 } ? $" {string.Join(", ", result.TurnedOff)} {(result.TurnedOff.Count == 1 ? "is" : "are")} off." : "";
            return (warning ?? $"Saved into {into}. Your character uses it now.") + off;
        });
    }

    private void TurnOff()
    {
        if (target is not { } found)
            return;
        var previewing = service.Preview.Active;
        IReadOnlyList<string> turnedOff = [];
        Run(async () =>
        {
            (turnedOff, var warning) = await service.TurnOffModsAsync(found).ConfigureAwait(false);
            return warning;
        }, warning => warning ?? (turnedOff.Count == 0
            ? previewing ? "Preview off. None of your saved physics mods was on." : "None of your saved physics mods was on."
            : $"{(previewing ? "Preview off. " : "")}{string.Join(", ", turnedOff)} {(turnedOff.Count == 1 ? "is" : "are")} off. Your other physics mods apply again."));
    }

    // ---- Shared helpers ----

    private IReadOnlyList<PhysicsIssue> Issues(PhysicsDocument current)
    {
        if (!ReferenceEquals(issuesDocument, current) || issuesRevision != current.Revision)
        {
            issues = PhysicsValidation.Check(current.File, target?.Character.Skeleton);
            issuesDocument = current;
            issuesRevision = current.Revision;
        }
        return issues;
    }

    private static void Tip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(480));
        ImGui.TextUnformatted(text);
    }
}
