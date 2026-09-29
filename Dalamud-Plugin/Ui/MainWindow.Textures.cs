using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private readonly TextureEditService _textures;
    private TextureEditRequest? _newTexture;
    private string _textureModName = "";
    private const string CleanUpTexturesDialog = "Clean up texture sessions";
    private Guid? _discardTexture;
    private bool _cleanUpTextures;
    private int _textureBusy;
    private readonly Dictionary<Guid, float> _sessionCardHeights = new();

    private static bool IsTextureRow(ResourceView resource)
        => resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);

    private static bool TextureEditAvailable(ResourceView resource)
        => resource.SourceState is ResourceSourceState.GameData or ResourceSourceState.LoadedMod;

    /// <summary> Opens (or resumes) a texture edit session for the row's texture. </summary>
    private void StartTextureEdit(ResourceView resource, ActorView actor)
    {
        if (!IsTextureRow(resource) || !TextureEditAvailable(resource) || Volatile.Read(ref _textureBusy) != 0)
            return;
        // Game Files rows have no actor; their new mod goes to the import object's collection.
        var request = new TextureEditRequest(resource.GamePath, resource.ActualPath,
            resource.SourceState == ResourceSourceState.GameData ? "" : resource.SourceModDirectory,
            resource.SourceModRootPath, resource.SourceRelativePath,
            actor.Entity?.ObjectIndex, actor.Entity?.Address.ToInt64() ?? 0,
            CollectionObjectIndex: actor.Entity is null && resource.SourceState == ResourceSourceState.GameData ? actor.ImportObjectIndex : null);
        if (resource.SourceState == ResourceSourceState.GameData)
        {
            // Reopen existing vanilla work without asking for a second destination.
            var existing = _textures.FindReusable(request);
            if (existing is not null) TextureAction(() => _textures.OpenEditorAsync(existing.Id));
            else
            {
                _newTexture = request;
                _textureModName = Sanitize("Texture Edit " + Path.GetFileNameWithoutExtension(resource.GamePath));
            }
        }
        else TextureAction(async () => { await _textures.StartAsync(request).ConfigureAwait(false); });
    }

    private static Vector4 SessionColour(SessionState state)
        => state switch
        {
            SessionState.Conflict => Theme.Conflict,
            SessionState.Paused => Theme.Paused,
            SessionState.NeedsMod => Theme.Info,
            _ => Theme.Watching,
        };

    private static string SessionLabel(SessionState state)
        => state switch
        {
            SessionState.Conflict => "Conflict",
            SessionState.Paused => "Paused",
            SessionState.NeedsMod => "Needs mod",
            _ => "Watching",
        };

    private void DrawTextureSessions()
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
        if (sessions.Count > 0 || _painter?.Jobs.Count > 0)
        {
            ImGui.Spacing();
            using (ImRaii.Disabled(Volatile.Read(ref _textureBusy) != 0 || Volatile.Read(ref _painterBusy) != 0))
                if (ImGui.Button("Clean up all sessions"))
                    _cleanUpTextures = true;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Discard every texture session and Substance Painter project, deleting their working files. Mods and backups are kept.");
        }
        ImGui.Spacing();
        DrawPainterJobs();

        if (sessions.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.Images, "No texture sessions",
                "Choose Textures in On Screen or Mod Browser, then use the brush action on a texture. Set your editor executable in Settings first.");
            return;
        }

        var busy = Volatile.Read(ref _textureBusy) != 0;
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

        ImGui.TextColored(SessionColour(state), FirstLine(s.Status));
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
            if (Widgets.IconButton("##retry", FontAwesomeIcon.Redo, "Retry the last save: resume, reload the mod and redraw", canPause))
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

    private void DrawTextureDialogs()
    {
        if (_newTexture is not null && !ImGui.IsPopupOpen("New texture mod")) ImGui.OpenPopup("New texture mod");
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (ImGui.BeginPopupModal("New texture mod", ImGuiWindowFlags.AlwaysAutoResize))
        {
            Widgets.SectionHeader("Edit a vanilla texture");
            Widgets.HintWrapped("The first successful save creates a new mod with this name and enables it in the actor's collection.");
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##texture-mod-name", "Mod name", ref _textureModName, 120);
            ImGui.Spacing();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_textureModName)))
            {
                if (ImGui.Button("Open texture"))
                {
                    var request = _newTexture! with { NewModName = _textureModName.Trim() };
                    _newTexture = null;
                    ImGui.CloseCurrentPopup();
                    TextureAction(async () => { await _textures.StartAsync(request).ConfigureAwait(false); });
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { _newTexture = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (_discardTexture.HasValue && !ImGui.IsPopupOpen("Discard texture session")) ImGui.OpenPopup("Discard texture session");
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (ImGui.BeginPopupModal("Discard texture session", ImGuiWindowFlags.AlwaysAutoResize))
        {
            Widgets.SectionHeader("Discard working files?");
            Widgets.HintWrapped("This deletes the session's working directory, including its TGA and any editing documents saved there. The committed mod and managed backups are retained.");
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
        var projects = _painter?.Jobs.Count ?? 0;
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
        if (Interlocked.CompareExchange(ref _textureBusy, 1, 0) != 0) return;
        // A Painter project being created would link sessions this is about to remove.
        if (Interlocked.CompareExchange(ref _painterBusy, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _textureBusy, 0);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var before = _textures.Sessions.Select(s => s.Id).ToList();
                var projects = 0;
                if (_painter is { } painter)
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
                    SetTextureStatus(text, FeedbackSeverity.Success);
                else
                    SetTextureStatus($"{text} {kept.Count} could not be removed; close their files and try again. {string.Join(" ", kept.Take(3))}",
                        FeedbackSeverity.Warning);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error) { _log.Warning(error, "Could not clean up texture sessions."); SetTextureStatus(error.Message, FeedbackSeverity.Error); }
            finally
            {
                Interlocked.Exchange(ref _painterBusy, 0);
                Interlocked.Exchange(ref _textureBusy, 0);
            }
        });
    }

    private void TextureAction(Func<Task> action)
    {
        if (Interlocked.CompareExchange(ref _textureBusy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); SetTextureStatus("Texture session updated.", FeedbackSeverity.Success); }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
            catch (Exception error) { _log.Warning(error, "Texture session action failed."); SetTextureStatus(error.Message, FeedbackSeverity.Error); }
            finally { Interlocked.Exchange(ref _textureBusy, 0); }
        });
    }
}
