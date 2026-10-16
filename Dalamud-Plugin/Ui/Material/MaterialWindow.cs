using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Materials;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Previews;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Studio.Services;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The Material window: one material at a time, opened from a row's menu in On Screen, Game Files
/// or a mod window. Every setting is named; edits show on the character (through a temporary mod
/// and a redraw) and Save writes the mod's file with a backup, or a new mod for a game file.
/// </summary>
internal sealed partial class MaterialWindow : Window, IDisposable
{
    private const string SwitchPopup = "##material-switch";
    private const string NewModPopup = "New material mod##material-new-mod";
    private const string MenuPopup = "##material-menu";
    private const int PreviewDelayMs = 300;
    private const string Recheck = "Open the material again";

    private readonly PreviewModService previewMods;
    private readonly MaterialPreviewService preview;
    private readonly PenumbraIpc penumbra;
    private readonly PreviewService previews;
    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly LiveMaterial live;
    private readonly LiveColorTable liveTable;
    private readonly CancellationTokenSource life = new();

    private MaterialSession? session;
    private Task<Opened>? opening;
    private (ResourceView Row, ActorView Actor)? pendingOpen;
    private bool openSwitchPopup;
    private bool openNewModPopup;
    private bool openAfterSave;
    private string newModName = string.Empty;
    private Task<string?>? working;
    private Func<string?, string>? workDone;
    private string message = string.Empty;
    private FeedbackSeverity messageSeverity = FeedbackSeverity.Info;
    private long previewDue;
    private Guid? collection;
    private bool customShader;

    private sealed record Opened(MaterialSource Source, MaterialDocument Document, Guid? Collection, bool CustomShader);

    internal MaterialWindow(PreviewModService previewMods, MaterialPreviewService preview, PenumbraIpc penumbra, PreviewService previews,
        IDataManager data, ITextureProvider textures, IFramework framework, IObjectTable objects, IPluginLog log)
        : base("Material###InstantEditMaterial")
    {
        this.previewMods = previewMods;
        this.preview = preview;
        this.penumbra = penumbra;
        this.previews = previews;
        this.data = data;
        this.textures = textures;
        this.framework = framework;
        this.objects = objects;
        this.log = log;
        live = new LiveMaterial(objects, log);
        liveTable = new LiveColorTable(objects, log);
        Size = new Vector2(1020, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 420), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        AllowPinning = true;
        AllowBackgroundBlur = true;
    }

    /// <summary>Why the row's material can't be opened here, or null when it can.</summary>
    public static string? EditBlock(ResourceView row)
    {
        if ((row.Kinds & ResourceKinds.Material) == 0 || !row.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            return "Not a material.";
        return row.SourceState switch
        {
            ResourceSourceState.GameData => null,
            ResourceSourceState.LoadedMod when Path.IsPathRooted(row.ActualPath) && row.SourceModDirectory.Length > 0 => null,
            _ => "This material's file can't be found in a mod.",
        };
    }

    /// <summary>The material at <paramref name="gamePath"/> is the one open here.</summary>
    public bool IsOpenFor(string gamePath)
        => IsOpen && session != null && string.Equals(session.Source.GamePath, PathRules.NormalizeGamePath(gamePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a row's material; asks first when another one has unsaved changes.</summary>
    public void Open(ResourceView row, ActorView actor)
    {
        IsOpen = true;
        if (EditBlock(row) is { } blocked)
        {
            Say(FeedbackSeverity.Warning, blocked);
            return;
        }
        if (session != null && SameFile(session.Source, row) && opening == null)
            return;
        if (session is { History.Dirty: true })
        {
            pendingOpen = (row, actor);
            openSwitchPopup = true;
            return;
        }
        Load(row, actor);
    }

    private static bool SameFile(MaterialSource source, ResourceView row)
        => string.Equals(source.GamePath, PathRules.NormalizeGamePath(row.GamePath), StringComparison.OrdinalIgnoreCase)
           && string.Equals(source.ActualPath, row.ActualPath, StringComparison.OrdinalIgnoreCase);

    private void Load(ResourceView row, ActorView actor)
    {
        if (opening != null)
            return;
        previewDue = 0;
        opening = Task.Run(async () =>
        {
            var bytes = row.SourceState == ResourceSourceState.GameData
                ? (await data.GetFileAsync<Lumina.Data.FileResource>(row.GamePath, life.Token).ConfigureAwait(false))?.Data
                  ?? throw new IOException("The game file could not be read.")
                : await File.ReadAllBytesAsync(row.ActualPath, life.Token).ConfigureAwait(false);
            return await ReadAsync(MaterialSource.FromRow(row, actor, bytes), bytes).ConfigureAwait(false);
        });
    }

    /// <summary>Reads the session's own file again, after a restore put an older one back.</summary>
    private void Reload(MaterialSource source)
    {
        if (opening != null)
            return;
        opening = Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(source.ActualPath, life.Token).ConfigureAwait(false);
            return await ReadAsync(source with { Sha256 = PreviewSource.Hash(bytes) }, bytes).ConfigureAwait(false);
        });
    }

    private async Task<Opened> ReadAsync(MaterialSource source, byte[] bytes)
    {
        if (!MtrlReader.LooksLikeMtrl(bytes))
            throw new InvalidDataException("This file is not a material.");
        var info = MtrlReader.Read(bytes);
        var target = await penumbra.GetCollectionTargetAsync(source.ObjectIndex).ConfigureAwait(false);
        var custom = false;
        if (target != null && info.ShaderPackageName.Length > 0)
        {
            var shader = (await penumbra.ResolveCollectionPathsAsync(target.Id, ["shader/sm5/shpk/" + info.ShaderPackageName]).ConfigureAwait(false))[0];
            custom = shader != null && Path.IsPathRooted(shader);
        }
        return new Opened(source, MaterialDocument.From(info), target?.Id, custom);
    }

    public override void OnClose()
    {
        previewDue = 0;
        // The previews show unsaved edits; closing puts the character's own look back. The edits stay until another material opens.
        RestoreLive();
        if (session is not { PreviewShown: true } current)
            return;
        loadedDoc = current.History.Saved;
        var stop = working ?? Task.FromResult<string?>(null);
        _ = stop.ContinueWith(_ => IsOpen ? Task.CompletedTask : current.DiscardAsync(preview), TaskScheduler.Default).Unwrap()
            .ContinueWith(task => log.Warning(task.Exception!, "Could not stop the material preview."), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Puts the loaded material's own values back (on the framework thread, waiting briefly when called elsewhere).</summary>
    public void Dispose()
    {
        life.Cancel();
        try
        {
            if (framework.IsInFrameworkUpdateThread)
                RestoreLive();
            else
                framework.RunOnFrameworkThread(RestoreLive).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not put the material's own values back while unloading.");
        }
        ReleaseUsage();
        life.Dispose();
    }

    private bool Busy => opening != null || working != null || preview.Busy;

    public override void Draw()
    {
        previews.Pump();
        Poll();
        if (openSwitchPopup)
        {
            openSwitchPopup = false;
            ImGui.OpenPopup(SwitchPopup);
        }
        if (openNewModPopup)
        {
            openNewModPopup = false;
            ImGui.OpenPopup(NewModPopup);
        }
        if (session is not { } current)
        {
            Widgets.EmptyState(FontAwesomeIcon.Palette, opening != null ? "Opening" : "No material open",
                "Open one from a material row's menu in On Screen, Game Files or a mod window.");
            DrawMessage();
            DrawSwitchPopup();
            return;
        }
        WindowName = $"Material: {current.Source.FileName}###InstantEditMaterial";
        var doc = current.Current;
        DrawToolbar(current, doc);
        DrawMessage();
        DrawBanners(doc);
        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        var left = Math.Min(Theme.Scaled(400), ImGui.GetContentRegionAvail().X * .45f);
        using (var child = ImRaii.Child("##material-left", new Vector2(left, height), true))
        {
            if (child.Success)
            {
                DrawShader(current, doc);
                ImGui.Spacing();
                DrawTextures(current, doc);
            }
        }
        ImGui.SameLine();
        using (var child = ImRaii.Child("##material-main", new Vector2(-1, height), true))
        {
            if (child.Success)
                DrawMain(current, doc);
        }
        DrawSwitchPopup();
        DrawNewModPopup(current);
        HandleShortcuts(current);
        // A control that went away while held never reports letting go.
        if (current.History.InGesture && !ImGui.IsAnyItemActive())
            current.History.EndGesture();
        PumpLive(current);
        PumpPreview(current);
        liveTable.Update((float)ImGui.GetTime());
    }

    /// <summary>The main column: the values, and the colour rows when the shader reads them.</summary>
    private void DrawMain(MaterialSession current, MaterialDocument doc)
    {
        if (!doc.Editable)
        {
            Widgets.EmptyState(FontAwesomeIcon.Lock, "Read only", "This material's shader data can't be read. Only its texture paths can change.");
            return;
        }
        if (!ShowsRows(doc))
        {
            DrawValues(current, doc);
            return;
        }
        using var tabs = ImRaii.TabBar("##material-tabs");
        if (!tabs.Success)
            return;
        if (ImGui.BeginTabItem("Colour rows###material-rows"))
        {
            DrawRows(current, doc);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Values###material-values"))
        {
            DrawValues(current, doc);
            ImGui.EndTabItem();
        }
    }

    // ---- Tasks ----

    private void Poll()
    {
        if (opening is { IsCompleted: true } opened)
        {
            opening = null;
            if (opened.IsCompletedSuccessfully)
                Show(opened.Result);
            else
                Say(FeedbackSeverity.Error, Unwrap(opened.Exception));
        }
        if (working is { IsCompleted: true } work)
        {
            working = null;
            var done = workDone;
            workDone = null;
            if (work.IsCompletedSuccessfully)
                Say(work.Result is null ? FeedbackSeverity.Success : FeedbackSeverity.Warning, done?.Invoke(work.Result) ?? work.Result ?? "Done.");
            else
                Say(FeedbackSeverity.Error, Unwrap(work.Exception));
            if (openAfterSave)
            {
                openAfterSave = false;
                if (work.IsCompletedSuccessfully && session is { History.Dirty: false } && pendingOpen is { } next)
                {
                    pendingOpen = null;
                    Load(next.Row, next.Actor);
                }
            }
        }
        PollTextures();
        PollUsage();
    }

    /// <summary>Makes a freshly read material the open one; the last one's preview comes down first.</summary>
    private void Show(Opened opened)
    {
        var old = session;
        var reload = old != null && string.Equals(old.Source.ActualPath, opened.Source.ActualPath, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(old.Source.GamePath, opened.Source.GamePath, StringComparison.OrdinalIgnoreCase);
        if (old is { PreviewShown: true } && !reload)
            Run(async () =>
            {
                await old.DiscardAsync(preview).ConfigureAwait(false);
                return null;
            }, _ => $"Opened {opened.Source.FileName}.");
        if (!reload)
            RestoreLive();
        if (reload && old != null)
            old.Reload(opened.Source, opened.Document);
        else
        {
            session = new MaterialSession(opened.Source, opened.Document, OnEdit);
            selectedPair = 0;
            pathEdits.Clear();
            ReleaseUsage();
        }
        collection = opened.Collection;
        customShader = opened.CustomShader;
        loadedDoc = opened.Document;
        shownDoc = null;
        everBound = false;
        RestoreLive();
        BindLive(session!);
        ResolveTextures(session!.Current);
        if (!reload)
            Say(FeedbackSeverity.Info, $"Opened {opened.Source.FileName} from {(opened.Source.IsGameData ? "the game's files" : opened.Source.ModName)}.");
    }

    /// <summary>
    /// Every edit, undo and redo lands here. Value edits are written into the loaded material at
    /// once; edits that change what the game loads (or values it can't take live) redraw through a
    /// temporary mod once the controls let go.
    /// </summary>
    private Task OnEdit(MaterialDocument doc, EditClass kind)
    {
        if (kind == EditClass.Structural)
            ResolveTextures(doc);
        else if (WriteLive(doc))
            return Task.CompletedTask;
        if (kind == EditClass.Structural || everBound)
            previewDue = Environment.TickCount64 + PreviewDelayMs;
        return Task.CompletedTask;
    }

    private void PumpPreview(MaterialSession current)
    {
        if (previewDue == 0 || Environment.TickCount64 < previewDue || working != null || ImGui.IsAnyItemActive() || current.History.InGesture)
            return;
        previewDue = 0;
        if (!current.Current.Editable && current.Current.Slots.Count == 0)
            return;
        var shown = current.Current;
        Run(() => current.PreviewStructuralAsync(preview, life.Token),
            warning =>
            {
                // The redraw loads the preview's file; the live binding finds it again and adds what changed since.
                loadedDoc = shown;
                shownDoc = null;
                nextLiveCheck = Environment.TickCount64 + LiveCheckMs;
                return warning ?? (preview.Shared
                    ? $"Shown on the character. Others using {preview.CollectionName} see it too. Nothing is saved yet."
                    : "Shown on the character. Nothing is saved yet.");
            });
    }

    private void Run(Func<Task<string?>> action, Func<string?, string> done)
    {
        if (working != null)
            return;
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

    // ---- Toolbar ----

    private void DrawToolbar(MaterialSession current, MaterialDocument doc)
    {
        Widgets.Icon(FontAwesomeIcon.Palette, Theme.MaterialKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Text, current.Source.FileName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.Source.IsGameData ? current.Source.GamePath : $"{current.Source.GamePath}\n{current.Source.ActualPath}");
        ImGui.SameLine(0, Theme.Gap);
        if (current.Source.IsGameData)
            Widgets.Badge("Game file", Theme.GameSource, Theme.Muted);
        else
            Widgets.Badge(current.Source.ModName, Theme.ModSource, Theme.Label);
        if (current.History.Dirty)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Badge("Unsaved", Theme.Warning);
        }

        var state = LiveState(out var stateColour);
        var reload = !live.Bound;
        var buttons = ImGui.GetFrameHeight() * (reload ? 5 : 4) + ImGui.GetStyle().ItemSpacing.X * (reload ? 5 : 4) + Theme.Gap * 4
                      + ImGui.CalcTextSize("Save").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.CalcTextSize(state).X;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttons));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(stateColour, state);
        Tip(live.Bound ? "Value edits show on the character at once. Nothing is saved yet."
            : everBound ? "The material was redrawn or left the screen. Reload finds it again."
            : "This material isn't drawn on the character. Edits still save.");
        if (reload)
        {
            ImGui.SameLine();
            if (Widgets.IconButton("##material-reload-live", FontAwesomeIcon.Sync, "Reload live preview", working == null))
                BindLive(current);
        }
        ImGui.SameLine(0, Theme.Gap * 2);
        using (ImRaii.Disabled(Busy || !current.History.Dirty))
        {
            if (ImGui.Button("Save"))
                StartSave(current, afterwards: false);
        }
        Tip(current.Source.IsGameData ? "Saves into a new mod." : $"Writes the material into {current.Source.ModName}. The old file is backed up.");
        ImGui.SameLine();
        if (Widgets.IconButton("##material-undo", FontAwesomeIcon.Undo, null, current.History.CanUndo && working == null))
            _ = current.Undo();
        Tip(current.History.UndoLabel.Length > 0 ? $"Undo (Ctrl+Z): {current.History.UndoLabel}" : "Undo (Ctrl+Z)");
        ImGui.SameLine();
        if (Widgets.IconButton("##material-redo", FontAwesomeIcon.Redo, null, current.History.CanRedo && working == null))
            _ = current.Redo();
        Tip(current.History.RedoLabel.Length > 0 ? $"Redo (Ctrl+Y): {current.History.RedoLabel}" : "Redo (Ctrl+Y)");
        ImGui.SameLine();
        using (ImRaii.Disabled(true))
            Widgets.IconButton("##material-presets", FontAwesomeIcon.Magic, null, false);
        Tip("Presets come in a later step.");
        ImGui.SameLine();
        if (Widgets.GhostIconButton("##material-more", FontAwesomeIcon.EllipsisH, "More"))
            ImGui.OpenPopup(MenuPopup);
        DrawMenu(current, doc);
        if (Busy)
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
    }

    private void DrawMenu(MaterialSession current, MaterialDocument doc)
    {
        using var popup = ImRaii.Popup(MenuPopup);
        if (!popup.Success)
            return;
        using (ImRaii.Disabled(!current.History.Dirty || Busy))
        {
            if (ImGui.MenuItem("Discard changes"))
            {
                previewDue = 0;
                _ = current.Revert();
            }
        }
        if (doc.Table is { Kind: ColorTableKind.Legacy })
        {
            using (ImRaii.Disabled(Busy || !doc.Editable))
            {
                if (ImGui.MenuItem("Convert to Dawntrail"))
                    ConvertToDawntrail(current, doc);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Moves the material to character.shpk with 32 Dawntrail rows. Each old row fills both rows of its pair.");
        }
        IReadOnlyList<PreviewBackupGroup> backups = current.Source.IsGameData ? [] : previewMods.Backups([current.Source.ToPreviewSource()]);
        using (ImRaii.Disabled(backups.Count == 0 || Busy))
        {
            if (ImGui.MenuItem("Undo last save") && backups.Count > 0)
                UndoSave(current, backups[0]);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(backups.Count > 0 ? $"Puts back the file from before {backups[0].Created.ToLocalTime():d MMM, HH:mm}." : "No backup of this material yet.");
        ImGui.Separator();
        if (ImGui.MenuItem("Copy game path"))
            ImGui.SetClipboardText(current.Source.GamePath);
        if (!current.Source.IsGameData && ImGui.MenuItem("Open in Penumbra"))
        {
            try { penumbra.OpenModInPenumbra(current.Source.ModDirectory, current.Source.ModName); }
            catch (Exception e) { Say(FeedbackSeverity.Warning, $"Could not open the mod in Penumbra: {e.Message}"); }
        }
    }

    private void DrawMessage()
    {
        if (message.Length == 0)
            return;
        var (accent, _, icon) = Theme.Severity(messageSeverity);
        ImGui.TextColored(accent, icon);
        ImGui.SameLine(0, Theme.Gap);
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Text, message);
    }

    private void DrawBanners(MaterialDocument doc)
    {
        if (customShader)
            Widgets.Banner("##material-custom-shader", FeedbackSeverity.Warning, "A mod replaces this shader. Labels may not match.");
        if (ShaderPackages.Get(doc.ShaderPackage) == null)
            Widgets.Banner("##material-unknown-shader", FeedbackSeverity.Info, $"{doc.ShaderPackage} is not a character shader. Settings show by id.");
    }

    // ---- Save, switch, restore ----

    private void StartSave(MaterialSession current, bool afterwards)
    {
        openAfterSave = afterwards;
        if (current.Source.IsGameData)
        {
            newModName = UiText.Sanitize("Material Edit " + Path.GetFileNameWithoutExtension(current.Source.FileName));
            openNewModPopup = true;
            return;
        }
        Save(current, string.Empty);
    }

    private void Save(MaterialSession current, string modName)
    {
        previewDue = 0;
        PreviewApplyResult? result = null;
        Run(async () =>
        {
            // The saved file is what the character loads next; the temporary preview of it comes down without a redraw of its own.
            await current.DiscardAsync(preview, redraw: false).ConfigureAwait(false);
            result = await current.SaveAsync(previewMods, modName).ConfigureAwait(false);
            return result.Warnings.Count == 0 ? null : string.Join(" ", result.Warnings);
        }, warning =>
        {
            previews.Invalidate(current.Source.ActualPath);
            loadedDoc = current.History.Saved;
            shownDoc = null;
            return warning ?? result?.Describe("Saved") ?? "Saved.";
        });
    }

    private void UndoSave(MaterialSession current, PreviewBackupGroup group)
    {
        previewDue = 0;
        var source = current.Source;
        Run(async () =>
        {
            await current.DiscardAsync(preview, redraw: false).ConfigureAwait(false);
            var warnings = await previewMods.RestoreAsync(group, source.ObjectIndex, Recheck).ConfigureAwait(false);
            return warnings.Count == 0 ? null : string.Join(" ", warnings);
        }, warning =>
        {
            Reload(source);
            return warning ?? $"Put back the material from before {group.Created.ToLocalTime():d MMM, HH:mm}. The file it replaced is backed up too.";
        });
    }

    private void DrawSwitchPopup()
    {
        using var popup = ImRaii.Popup(SwitchPopup);
        if (!popup.Success)
            return;
        if (session is not { } current || pendingOpen is not { } next)
        {
            ImGui.CloseCurrentPopup();
            return;
        }
        ImGui.TextColored(Theme.Text, $"{current.Source.FileName} has unsaved changes.");
        ImGui.TextColored(Theme.Muted, $"Open {Path.GetFileName(next.Row.GamePath)} anyway?");
        using (ImRaii.Disabled(Busy))
        {
            if (ImGui.Button("Save first##material-switch-save"))
            {
                ImGui.CloseCurrentPopup();
                StartSave(current, afterwards: true);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Discard##material-switch-discard"))
        {
            ImGui.CloseCurrentPopup();
            pendingOpen = null;
            current.History.MarkSaved();
            Load(next.Row, next.Actor);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##material-switch-cancel"))
        {
            pendingOpen = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawNewModPopup(MaterialSession current)
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (!ImGui.BeginPopupModal(NewModPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        Widgets.SectionHeader("Save a game material");
        Widgets.HintWrapped("The material goes into a new mod with this name, enabled in the character's collection.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var enter = ImGui.InputTextWithHint("##material-mod-name", "Mod name", ref newModName, 120, ImGuiInputTextFlags.EnterReturnsTrue);
        var name = newModName.Trim();
        var valid = ModPaths.IsSafeNewModName(name);
        if (name.Length > 0 && !valid)
            ImGui.TextColored(Theme.Warning, "Use a name without \\ / : * ? \" < > |.");
        ImGui.Spacing();
        using (ImRaii.Disabled(!valid || Busy))
        {
            if ((ImGui.Button("Save##material-new-mod-save") || enter) && valid && !Busy)
            {
                ImGui.CloseCurrentPopup();
                Save(current, name);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##material-new-mod-cancel"))
        {
            openAfterSave = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void HandleShortcuts(MaterialSession current)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput || !ImGui.GetIO().KeyCtrl || working != null)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z))
            _ = current.Undo();
        else if (ImGui.IsKeyPressed(ImGuiKey.Y))
            _ = current.Redo();
    }

    // ---- Shared helpers ----

    /// <summary>
    /// Call right after a control: grabbing it starts one undo step for the whole drag, a change is
    /// applied (and previewed), letting go ends the step.
    /// </summary>
    private static void Track(MaterialSession current, bool changed, Func<MaterialDocument, MaterialDocument> edit, string label)
    {
        if (ImGui.IsItemActivated())
            current.History.BeginGesture();
        if (changed)
            _ = current.Apply(edit, label);
        if (ImGui.IsItemDeactivated())
            current.History.EndGesture();
    }

    private static void Tip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(420));
        ImGui.TextUnformatted(text);
    }
}
