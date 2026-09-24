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
    private Guid? _discardTexture;
    private int _textureBusy;

    private static bool IsTextureRow(ResourceView resource)
        => resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);

    private static bool TextureEditAvailable(ResourceView resource)
        => resource.SourceState is ResourceSourceState.GameData or ResourceSourceState.LoadedMod;

    /// <summary> Opens (or resumes) a texture edit session for the row's texture. </summary>
    private void StartTextureEdit(ResourceView resource, ActorView actor)
    {
        if (!IsTextureRow(resource) || !TextureEditAvailable(resource) || Volatile.Read(ref _textureBusy) != 0)
            return;
        var request = new TextureEditRequest(resource.GamePath, resource.ActualPath,
            resource.SourceState == ResourceSourceState.GameData ? "" : resource.SourceModDirectory,
            resource.SourceModRootPath, resource.SourceRelativePath,
            actor.Entity?.ObjectIndex, actor.Entity?.Address.ToInt64() ?? 0);
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
            : "Saves are written uncompressed (BGRA32).") + " Change this under Options.");
        if (_textures.StartupError.Length > 0)
        {
            ImGui.Spacing();
            Widgets.Banner("##texture-startup-error", FeedbackSeverity.Error, _textures.StartupError);
        }
        ImGui.Spacing();

        var sessions = _textures.Sessions;
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
        var lines = s.LastSaved is null ? 4 : 5;
        var textHeight = lines * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing();
        var height = Math.Max(Theme.ThumbSize, textHeight) + style.WindowPadding.Y * 2;
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
            ImGui.TextColored(Theme.Hint, "Destination");
            ImGui.SameLine(0, Theme.Gap);
            Widgets.PathText(s.TargetFile, s.TargetFile);
        }
        ImGui.TextColored(Theme.Hint, "Working file");
        ImGui.SameLine(0, Theme.Gap);
        Widgets.PathText(s.WorkingFile, s.WorkingFile);
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
            if (Widgets.IconButton("##discard", FontAwesomeIcon.Trash, "Discard the session's working files…"))
                _discardTexture = s.Id;
        }
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
