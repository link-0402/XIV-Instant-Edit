using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Previews;
using DroppedPaths = InstantEdit.Studio.Windows.UI.DroppedPaths;
using FileDrop = InstantEdit.Studio.Windows.UI.FileDrop;
using HeldDrop = InstantEdit.Studio.Windows.UI.HeldDrop;

namespace InstantEdit.Ui.Main;

/// <summary>
/// Texture editing: the Sessions tab with its session cards, the brush of a texture row, an image
/// dropped on a texture row, and the dialogs they open. One texture change runs at a time (<see cref="Busy"/>).
/// </summary>
internal sealed class TextureSessionsTab
{
    private const string CleanUpTexturesDialog = "Clean up texture sessions";

    /// <summary> How long a dragged image has to rest on a texture row before it can be dropped there. </summary>
    private static readonly TimeSpan TextureDropHold = TimeSpan.FromSeconds(1);

    private readonly Configuration _config;
    private readonly TextureEditService _textures;
    private readonly PreviewService _previews;
    private readonly PainterPanel _painter;
    private readonly StatusFeed _feed;
    private readonly IPluginLog _log;
    private readonly CancellationToken _lifetime;
    private TextureEditRequest? _newTexture;
    /// <summary> The image dropped on a vanilla texture while its new mod is named, or null for an edit. </summary>
    private string? _newTextureImage;
    private string _textureModName = "";
    private Guid? _discardTexture;
    private bool _cleanUpTextures;
    private readonly Dictionary<Guid, float> _sessionCardHeights = new();
    private string _sessionsTabLabel = "Sessions";
    private int _sessionsTabCount = -1;

    public TextureSessionsTab(Configuration config, TextureEditService textures, PreviewService previews, PainterPanel painter,
        StatusFeed feed, IPluginLog log, CancellationToken lifetime)
    {
        _config = config;
        _textures = textures;
        _previews = previews;
        _painter = painter;
        _feed = feed;
        _log = log;
        _lifetime = lifetime;
    }

    /// <summary> Set while a texture session action, replace or undo runs. </summary>
    public BusyFlag Busy { get; } = new();

    public static bool IsTextureRow(ResourceView resource)
        => resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);

    public static bool TextureEditAvailable(ResourceView resource)
        => resource.SourceState is ResourceSourceState.GameData or ResourceSourceState.LoadedMod && TextureEditBlock(resource).Length == 0;

    /// <summary> Why a shared game texture such as white.tex can't be edited; empty for every other texture. </summary>
    public static string TextureEditBlock(ResourceView resource)
        => SharedTextures.EditBlock(resource.GamePath, resource.SourceState == ResourceSourceState.GameData);

    /// <summary> The tab's label, with the number of open sessions. </summary>
    public string SessionsTabLabel()
    {
        var count = _textures.Sessions.Count;
        if (count != _sessionsTabCount)
        {
            _sessionsTabCount = count;
            _sessionsTabLabel = count == 0 ? "Sessions" : $"Sessions ({count})";
        }
        return _sessionsTabLabel;
    }

    /// <summary> The texture session request for the row's texture, as the actor shows it. </summary>
    private static TextureEditRequest TextureRequest(ResourceView resource, ActorView actor)
        // Game Files rows have no actor; their new mod goes to the import object's collection.
        => new(resource.GamePath, resource.ActualPath,
            resource.SourceState == ResourceSourceState.GameData ? "" : resource.SourceModDirectory,
            resource.SourceModRootPath, resource.SourceRelativePath,
            actor.Entity?.ObjectIndex, actor.Entity?.Address.ToInt64() ?? 0,
            CollectionObjectIndex: actor.Entity is null && resource.SourceState == ResourceSourceState.GameData ? actor.ImportObjectIndex : null);

    /// <summary> Opens (or resumes) a texture edit session for the row's texture. </summary>
    public void StartTextureEdit(ResourceView resource, ActorView actor)
    {
        if (!IsTextureRow(resource) || !TextureEditAvailable(resource) || Busy.IsBusy)
            return;
        var request = TextureRequest(resource, actor);
        if (resource.SourceState == ResourceSourceState.GameData)
        {
            // Reopen existing vanilla work without asking for a second destination.
            var existing = _textures.FindReusable(request);
            if (existing is not null) TextureAction(() => _textures.OpenEditorAsync(existing.Id));
            else
            {
                _newTexture = request;
                _textureModName = UiText.Sanitize("Texture Edit " + Path.GetFileNameWithoutExtension(resource.GamePath));
            }
        }
        else TextureAction(async () => { await _textures.StartAsync(request).ConfigureAwait(false); });
    }

    private static Vector4 SessionColour(SessionState state)
        => state switch
        {
            SessionState.Conflict => Theme.Conflict,
            SessionState.Paused or SessionState.Waiting => Theme.Paused,
            SessionState.NeedsMod => Theme.Info,
            _ => Theme.Watching,
        };

    private static string SessionLabel(SessionState state)
        => state switch
        {
            SessionState.Conflict => "Conflict",
            SessionState.Paused => "Paused",
            SessionState.Waiting => "Waiting",
            SessionState.NeedsMod => "Needs mod",
            _ => "Watching",
        };

    public void Draw()
    {
        ImGui.Spacing();
        Widgets.HintWrapped("Save the TGA file to update the texture in game. You can also change its resolution. " + (_config.RecompressTextures
            ? "Saves keep the original compression."
            : "Saves are written uncompressed (BGRA32).") + " Change this under Options. " +
            "Save a copy under another name in the same folder to add it as a Penumbra option named after the file.");
        if (_textures.StartupError.Length > 0)
        {
            ImGui.Spacing();
            Widgets.Banner("##texture-startup-error", FeedbackSeverity.Error, _textures.StartupError);
        }
        var sessions = _textures.Sessions;
        if (sessions.Count > 0 || _painter.Service?.Jobs.Count > 0)
        {
            ImGui.Spacing();
            using (ImRaii.Disabled(Busy.IsBusy || _painter.Busy.IsBusy))
                if (ImGui.Button("Clean up all sessions"))
                    _cleanUpTextures = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Discard every texture session and Substance Painter project, deleting their working files. Mods and backups are kept.");
        }
        ImGui.Spacing();
        _painter.DrawJobs();

        if (sessions.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.Images, "No texture sessions",
                "Use the brush on a texture in On Screen, or in a mod opened from the Mod Browser. Set your editor executable in Settings first.");
            return;
        }

        var busy = Busy.IsBusy;
        using var scroll = ImRaii.Child("##texture-sessions", new Vector2(0, 0), false);
        if (!scroll.Success)
            return;
        foreach (var (group, members) in SessionViews.Group(sessions))
        {
            Widgets.SectionHeader(group, members.Count == 1 ? "1 session" : $"{members.Count} sessions");
            foreach (var session in members)
                DrawSessionCard(session, busy);
            ImGui.Spacing();
        }
    }

    private void DrawSessionCard(TextureEditSession s, bool busy)
    {
        using var id = ImRaii.PushId(s.Id.ToString("N"));
        var style = ImGui.GetStyle();
        // The estimate covers a row appearing this frame; the height measured last frame covers
        // fonts (mono paths, icon buttons) that run taller than the estimate assumes.
        var height = Math.Max(EstimateSessionCardHeight(s), _sessionCardHeights.GetValueOrDefault(s.Id));
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.RowAlt);
        using var card = ImRaii.Child("##session-card", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!card.Success)
            return;

        var state = SessionViews.StateOf(s);
        DrawSessionThumbnail(s);
        ImGui.SameLine(0, Theme.Gap);
        using var details = ImRaii.Group();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, Path.GetFileName(s.GamePath));
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.Badge(SessionLabel(state), SessionColour(state)))
            ImGui.SetTooltip(s.Status);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, SessionViews.Caption(s));

        ImGui.TextColored(SessionColour(state), StatusStrip.FirstLine(s.Status));
        if (ImGui.IsItemHovered() && s.Status.Length > 0)
            ImGui.SetTooltip(s.Status);
        if (s.NeedsMod)
            ImGui.TextColored(Theme.Info, SessionViews.DestinationLine(s));
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "Destination");
            ImGui.SameLine(0, Theme.Gap);
            Widgets.PathText(s.TargetFile, s.TargetFile);
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, "Working file");
        ImGui.SameLine(0, Theme.Gap);
        Widgets.PathText(s.WorkingFile, s.WorkingFile);
        foreach (var variant in s.Variants)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "Variant");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Text, variant.Name);
            if (ImGui.IsItemHovered() && variant.Status.Length > 0)
                ImGui.SetTooltip(variant.Status);
            ImGui.SameLine(0, Theme.Gap);
            Widgets.PathText(Path.GetFileName(variant.RelativePath), s.VariantTargetFile(variant));
        }
        if (s.LastSaved is { } saved)
            ImGui.TextColored(Theme.Muted, $"Last saved {saved.ToLocalTime():g}");

        using (ImRaii.Disabled(busy))
        {
            if (Widgets.IconButton("##open-editor", FontAwesomeIcon.Edit, "Open the working TGA in your editor. A paused session resumes watching for saves."))
                TextureAction(() => _textures.OpenEditorAsync(s.Id));
            ImGui.SameLine();
            if (Widgets.IconButton("##open-folder", FontAwesomeIcon.FolderOpen, "Open the session's working folder"))
                TextureAction(() => { _textures.OpenFolder(s.Id); return Task.CompletedTask; });
            ImGui.SameLine();
            var canPause = !s.Conflict && !string.IsNullOrEmpty(s.PixelHash);
            if (Widgets.IconButton("##pause", s.Paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                    s.Paused ? "Resume watching for saves" : "Pause: saves are ignored until resumed", canPause))
                TextureAction(() => _textures.SetPausedAsync(s.Id, !s.Paused));
            ImGui.SameLine();
            // A conflict stops the session for good, unless it takes the mod's file as it is now.
            if (s.Conflict)
            {
                if (Widgets.IconButton("##reconnect", FontAwesomeIcon.Link,
                        "Reconnect: keep this session and use the mod's file as it is now. Your next save goes over it.",
                        !s.NeedsMod && !string.IsNullOrEmpty(s.PixelHash)))
                    TextureAction(() => _textures.ReconnectAsync(s.Id));
            }
            else if (Widgets.IconButton("##retry", FontAwesomeIcon.Redo, "Retry the last save: resume, reload the mod and redraw", canPause))
                TextureAction(() => _textures.RetryAsync(s.Id));
            ImGui.SameLine();
            if (Widgets.IconButton("##restore", FontAwesomeIcon.History,
                    "Restore the previous saved texture (or the original before the first edit), then pause. The working TGA is retained.",
                    !s.NeedsMod && !s.Conflict))
                TextureAction(() => _textures.RestoreAsync(s.Id));
            ImGui.SameLine(0, Theme.Scaled(12));
            if (Widgets.IconButton("##discard", FontAwesomeIcon.Trash, "Discard the session's working files"))
                _discardTexture = s.Id;
        }
        var contentBottom = Math.Max(ImGui.GetCursorPosY() - style.ItemSpacing.Y, style.WindowPadding.Y + Theme.ThumbSize);
        _sessionCardHeights[s.Id] = contentBottom + style.WindowPadding.Y;
    }

    /// <summary> Header, path rows and buttons sit at frame height; the status and last-saved lines are plain text. </summary>
    private static float EstimateSessionCardHeight(TextureEditSession s)
    {
        var framedRows = (s.NeedsMod ? 2 : 3) + s.Variants.Count;
        var textRows = (s.NeedsMod ? 2 : 1) + (s.LastSaved is null ? 0 : 1);
        var content = framedRows * ImGui.GetFrameHeightWithSpacing() + textRows * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeight();
        return Math.Max(Theme.ThumbSize, content) + ImGui.GetStyle().WindowPadding.Y * 2;
    }

    /// <summary> A session's committed texture (or the vanilla source before its first save). </summary>
    private void DrawSessionThumbnail(TextureEditSession session)
    {
        PreviewKey key;
        if (!session.NeedsMod && session.TargetFile.Length > 0 && Path.IsPathRooted(session.TargetFile))
            key = _previews.KeyFor(session.TargetFile, false);
        else
            key = _previews.KeyFor(UiText.Safe(session.ResolvedGamePath, session.GamePath), true);
        var entry = _previews.GetTexture(key);
        Widgets.PreviewImage(entry, new Vector2(Theme.ThumbSize));
        if (!ImGui.IsItemHovered())
            return;
        using var tooltip = ImRaii.Tooltip();
        Widgets.PreviewImage(entry, new Vector2(Theme.Scaled(256)));
        Widgets.PreviewCaption(entry);
    }

    public void DrawDialogs()
    {
        if (_newTexture is not null && !ImGui.IsPopupOpen("New texture mod")) ImGui.OpenPopup("New texture mod");
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (ImGui.BeginPopupModal("New texture mod", ImGuiWindowFlags.AlwaysAutoResize))
        {
            // A dropped image goes into the new mod straight away; an edit with its first save.
            var image = _newTextureImage;
            Widgets.SectionHeader(image is null ? "Edit a vanilla texture" : "Replace a vanilla texture");
            Widgets.HintWrapped(image is null
                ? "The first successful save creates a new mod with this name and enables it in the actor's collection."
                : $"{Path.GetFileName(image)} goes into a new mod with this name, enabled in the actor's collection.");
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##texture-mod-name", "Mod name", ref _textureModName, 120);
            ImGui.Spacing();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_textureModName)))
            {
                if (ImGui.Button(image is null ? "Open texture" : "Replace texture"))
                {
                    var request = _newTexture! with { NewModName = _textureModName.Trim() };
                    _newTexture = null;
                    _newTextureImage = null;
                    ImGui.CloseCurrentPopup();
                    if (image is null)
                        TextureAction(async () => { await _textures.StartAsync(request).ConfigureAwait(false); });
                    else
                        RunTextureReplace(request, image);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { _newTexture = null; _newTextureImage = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (_discardTexture.HasValue && !ImGui.IsPopupOpen("Discard texture session")) ImGui.OpenPopup("Discard texture session");
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (ImGui.BeginPopupModal("Discard texture session", ImGuiWindowFlags.AlwaysAutoResize))
        {
            Widgets.SectionHeader("Discard working files?");
            Widgets.HintWrapped("This deletes the session's working directory, including its TGA and any editing documents saved there. The committed mod and managed backups are retained.");
            if (_discardTexture is { } linked && _painter.Service?.Jobs.FirstOrDefault(job => job.Targets.Any(target => target.SessionId == linked)) is { } project)
            {
                using var wrap = ImRaii.TextWrapPos(0f);
                ImGui.TextColored(Theme.Paused, $"The Substance Painter project {project.DisplayName} sends this texture through this session. Afterwards it can't send it anymore.");
            }
            ImGui.Spacing();
            if (ImGui.Button("Delete working files"))
            {
                var id = _discardTexture!.Value;
                _discardTexture = null;
                ImGui.CloseCurrentPopup();
                TextureAction(() => _textures.DiscardAsync(id));
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep session")) { _discardTexture = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        DrawCleanUpTexturesDialog();
    }

    private void DrawCleanUpTexturesDialog()
    {
        if (_cleanUpTextures && !ImGui.IsPopupOpen(CleanUpTexturesDialog)) ImGui.OpenPopup(CleanUpTexturesDialog);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (!ImGui.BeginPopupModal(CleanUpTexturesDialog, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var sessions = _textures.Sessions.Count;
        var projects = _painter.Service?.Jobs.Count ?? 0;
        Widgets.SectionHeader("Discard all texture sessions?");
        var sessionText = sessions switch
        {
            0 => "",
            1 => "This discards the texture session: its working folder is deleted, including the TGA and any editing documents saved there. ",
            _ => $"This discards all {sessions} texture sessions: their working folders are deleted, including the TGAs and any editing documents saved there. ",
        };
        var projectText = projects switch
        {
            0 => "",
            1 => "The Substance Painter project is discarded as well, so Painter can no longer send its textures; the saved Painter file stays. ",
            _ => $"The {projects} Substance Painter projects are discarded as well, so Painter can no longer send their textures; the saved Painter files stay. ",
        };
        Widgets.HintWrapped(sessionText + projectText + "The mods and their backups are kept.");
        ImGui.Spacing();
        if (ImGui.Button("Delete all working files"))
        {
            _cleanUpTextures = false;
            ImGui.CloseCurrentPopup();
            CleanUpTextureSessions();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep sessions")) { _cleanUpTextures = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    /// <summary> Discards every Painter project, then every texture session left, and reports any that stay. </summary>
    private void CleanUpTextureSessions()
    {
        if (!Busy.TryEnter()) return;
        // A Painter project being created would link sessions this is about to remove.
        if (!_painter.Busy.TryEnter())
        {
            Busy.Exit();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var before = _textures.Sessions.Select(s => s.Id).ToList();
                var projects = 0;
                if (_painter.Service is { } painter)
                    foreach (var job in painter.Jobs.ToList())
                    {
                        try
                        {
                            await painter.DiscardAsync(job.Id).ConfigureAwait(false);
                            projects++;
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            _log.Warning(error, "Could not discard a Painter project while cleaning up texture sessions.");
                        }
                    }
                var kept = await _textures.DiscardAllAsync().ConfigureAwait(false);
                var discarded = before.Count(id => _textures.Sessions.All(s => s.Id != id));
                var parts = new List<string>();
                if (discarded > 0 || projects == 0)
                    parts.Add(discarded == 1 ? "1 texture session" : $"{discarded} texture sessions");
                if (projects > 0)
                    parts.Add(projects == 1 ? "1 Painter project" : $"{projects} Painter projects");
                var text = $"Discarded {string.Join(" and ", parts)}.";
                if (kept.Count == 0)
                    _feed.SetTextureStatus(text, FeedbackSeverity.Success);
                else
                    _feed.SetTextureStatus($"{text} {kept.Count} could not be removed; close their files and try again. {string.Join(" ", kept.Take(3))}",
                        FeedbackSeverity.Warning);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _log.Warning(error, "Could not clean up texture sessions."); _feed.SetTextureStatus(error.Message, FeedbackSeverity.Error); }
            finally
            {
                _painter.Busy.Exit();
                Busy.Exit();
            }
        });
    }

    /// <summary>
    /// Makes the row a drop target for an image that replaces its texture. The drag has to rest on the
    /// row for <see cref="TextureDropHold"/> first, so an image carried across the list can't land on
    /// the wrong texture. Called in the actions cell before its buttons; the cursor is left where it was.
    /// </summary>
    public void DrawTextureDrop(ActorView actor, ResourceView node, float rowLeft)
    {
        if (!FileDrop.IsDragging)
            return;
        var start = ImGui.GetCursorScreenPos();
        // The row from its first cell to this one, within the visible part of the list. The cells'
        // padding stays out, so the target never makes the row taller.
        var windowMin = ImGui.GetWindowPos();
        var min = Vector2.Max(new Vector2(rowLeft, start.Y), windowMin);
        var max = Vector2.Min(new Vector2(start.X + ImGui.GetContentRegionAvail().X, start.Y + ImGui.GetFrameHeight()), windowMin + ImGui.GetWindowSize());
        if (max.X <= min.X || max.Y <= min.Y)
            return;
        // The table clips each cell to its column; the target spans them all.
        ImGui.PushClipRect(min, max, false);
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(max - min);
        var name = Path.GetFileName(node.GamePath);
        var outcome = FileDrop.HeldTarget("##texture-drop", TextureDropHold, paths => TextureDropBlock(node, paths),
            (paths, ready) => ready ? $"Let go to replace {name} with {paths.Describe()}" : $"Hold here to replace {name}", out var dropped);
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(start);
        if (outcome == HeldDrop.Dropped)
            ReplaceTexture(node, actor, dropped.Files[0]);
        else if (outcome == HeldDrop.TooSoon)
            _feed.SetStatus("Hold the image on the texture until its row lights up, then let go.", FeedbackSeverity.Warning);
    }

    /// <summary> Why the dragged files can't replace the row's texture, or null when they can. </summary>
    private string? TextureDropBlock(ResourceView node, DroppedPaths paths)
    {
        if (paths.Files.Count != 1 || paths.Folders.Count != 0)
            return "Drop one image.";
        if (!FileDrop.IsImage(paths.Files[0]))
            return $"{Path.GetFileName(paths.Files[0])} is not a png, jpeg, tga, dds or .tex image.";
        if (!TextureEditAvailable(node))
            return TextureEditBlock(node) is { Length: > 0 } blocked ? blocked : "This texture has no verified writable mod source.";
        return Busy.IsBusy ? "Another texture change is still running." : null;
    }

    /// <summary> Replaces the row's texture with a dropped image. A vanilla texture first asks for the new mod's name, as editing it does. </summary>
    private void ReplaceTexture(ResourceView resource, ActorView actor, string image)
    {
        var request = TextureRequest(resource, actor);
        if (resource.SourceState == ResourceSourceState.GameData && _textures.FindReusable(request) is null)
        {
            _newTexture = request;
            _newTextureImage = image;
            _textureModName = UiText.Sanitize("Texture Edit " + Path.GetFileNameWithoutExtension(resource.GamePath));
            return;
        }
        RunTextureReplace(request, image);
    }

    private void RunTextureReplace(TextureEditRequest request, string image)
    {
        if (!Busy.TryEnter()) return;
        var texture = Path.GetFileName(request.GamePath);
        var file = Path.GetFileName(image);
        _feed.SetStatus($"Replacing {texture} with {file}", FeedbackSeverity.Info);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _textures.ReplaceAsync(request, image).ConfigureAwait(false);
                // The session's status after the drop says whether the reload and redraw went through.
                var refresh = _textures.Sessions.FirstOrDefault(s => s.Id == result.SessionId)?.Status ?? "";
                if (result.Outcome == ExternalTextureOutcome.Applied && refresh.Contains("attention", StringComparison.Ordinal))
                    _feed.SetStatus($"Replaced {texture} with {file}, but {refresh}", FeedbackSeverity.Warning);
                else if (result.Outcome == ExternalTextureOutcome.Applied)
                    _feed.SetStatus($"Replaced {texture} with {file}. Undo is in the row's menu.", FeedbackSeverity.Success);
                else if (result.Outcome == ExternalTextureOutcome.Unchanged)
                    _feed.SetStatus($"{file} is the same as {texture}, so nothing changed.", FeedbackSeverity.Info);
                else
                    _feed.SetStatus($"Could not replace {texture}: {result.Message}", FeedbackSeverity.Error);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not replace a texture with a dropped image.");
                _feed.SetStatus($"Could not replace {texture}: {error.Message}", FeedbackSeverity.Error);
            }
            finally { Busy.Exit(); }
        });
    }

    /// <summary> The session holding an image dropped on the row's texture, while the drop can still be undone. </summary>
    public TextureEditSession? UndoableReplace(ResourceView resource, ActorView actor)
        => IsTextureRow(resource) ? _textures.FindUndoableReplace(TextureRequest(resource, actor)) : null;

    /// <summary> Puts back the texture an image was dropped on. </summary>
    public void UndoTextureReplace(TextureEditSession session)
    {
        if (!Busy.TryEnter()) return;
        var texture = Path.GetFileName(session.GamePath);
        _ = Task.Run(async () =>
        {
            try
            {
                await _textures.UndoReplaceAsync(session.Id).ConfigureAwait(false);
                var status = _textures.Sessions.FirstOrDefault(s => s.Id == session.Id)?.Status ?? "";
                _feed.SetStatus(status.Contains("attention", StringComparison.Ordinal)
                    ? $"{texture} is back, but {status}"
                    : $"{texture} is back to how it was before {session.Replacement?.File}.",
                    status.Contains("attention", StringComparison.Ordinal) ? FeedbackSeverity.Warning : FeedbackSeverity.Success);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning(error, "Could not undo a texture replacement.");
                _feed.SetStatus($"Could not undo the replace of {texture}: {error.Message}", FeedbackSeverity.Error);
            }
            finally { Busy.Exit(); }
        });
    }

    private void TextureAction(Func<Task> action)
    {
        if (!Busy.TryEnter()) return;
        _ = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); _feed.SetTextureStatus("Texture session updated.", FeedbackSeverity.Success); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _log.Warning(error, "Texture session action failed."); _feed.SetTextureStatus(error.Message, FeedbackSeverity.Error); }
            finally { Busy.Exit(); }
        });
    }
}
