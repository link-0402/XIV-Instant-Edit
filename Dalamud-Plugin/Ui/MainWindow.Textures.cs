using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    private void DrawTextureAction(ResourceView resource, ActorView actor)
    {
        if (!resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return;
        var available = resource.SourceState is ResourceSourceState.GameData or ResourceSourceState.LoadedMod;
        using (ImRaii.Disabled(!available || Volatile.Read(ref _textureBusy) != 0))
        {
            if (ImGui.SmallButton("Edit texture"))
            {
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
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(available
            ? "Open as a 32-bit TGA in your configured editor. Shared references to this texture will also change."
            : "This texture has no verified writable mod source.");
    }

    private void DrawTextureSessions()
    {
        ImGui.TextWrapped("Save the TGA file to update the texture in game. You can also change its resolution. " + (_config.RecompressTextures
            ? "Saves keep the original compression."
            : "Saves are written uncompressed (BGRA32).") + " Change this under Options.");
        if (_textures.StartupError.Length > 0) ImGui.TextWrapped(_textures.StartupError);
        if (Volatile.Read(ref _textureBusy) != 0) ImGui.TextDisabled("Updating texture session…");
        var sessions = _textures.Sessions;
        if (sessions.Count == 0) ImGui.TextWrapped("No texture sessions. Select Textures in On Screen or Mod Browser, then choose Edit texture. Set your editor executable in Settings first.");
        foreach (var s in sessions)
        {
            using var id = ImRaii.PushId(s.Id.ToString("N"));
            ImGui.Separator();
            ImGui.TextUnformatted(Path.GetFileName(s.GamePath));
            var format = TextureFiles.FormatName(s.SavedFormat) + (s.SavedFormat == s.Format ? "" : $" (originally {TextureFiles.FormatName(s.Format)})");
            ImGui.TextDisabled($"{s.Width} × {s.Height} · {format} · {(s.MipMaps ? "Mipmaps" : "No mipmaps")}");
            var color = s.Conflict ? Theme.Conflict : s.Paused ? Theme.Paused : Theme.Watching;
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextColored(color, s.Status);
            ImGui.TextWrapped(s.NeedsMod ? $"First save creates mod: {s.NewModName}" : $"Destination: {s.TargetFile}");
            ImGui.TextWrapped($"Working file: {s.WorkingFile}");
            if (s.LastSaved is { } saved) ImGui.TextDisabled($"Last saved: {saved.ToLocalTime():g}");
            using (ImRaii.Disabled(Volatile.Read(ref _textureBusy) != 0))
            {
                if (ImGui.Button("Open in editor")) TextureAction(() => _textures.OpenEditorAsync(s.Id));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open the working TGA. A paused session resumes watching for saves.");
                ImGui.SameLine();
                if (ImGui.Button("Open folder")) TextureAction(() => { _textures.OpenFolder(s.Id); return Task.CompletedTask; });
                ImGui.SameLine();
                using (ImRaii.Disabled(s.Conflict || string.IsNullOrEmpty(s.PixelHash)))
                {
                    if (ImGui.Button(s.Paused ? "Resume" : "Pause")) TextureAction(() => _textures.SetPausedAsync(s.Id, !s.Paused));
                    ImGui.SameLine();
                    if (ImGui.Button("Retry")) TextureAction(() => _textures.RetryAsync(s.Id));
                }
                using (ImRaii.Disabled(s.NeedsMod || s.Conflict))
                {
                    if (ImGui.Button("Restore backup")) TextureAction(() => _textures.RestoreAsync(s.Id));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Restore the previous saved texture (or the original before the first edit), then pause. The working TGA is retained.");
                }
                ImGui.SameLine();
                if (ImGui.Button("Discard…")) _discardTexture = s.Id;
            }
        }
    }

    private void DrawTextureDialogs()
    {
        if (_newTexture is not null && !ImGui.IsPopupOpen("New texture mod")) ImGui.OpenPopup("New texture mod");
        if (ImGui.BeginPopupModal("New texture mod", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("The first successful save creates a new mod and enables it in this actor's collection.");
            ImGui.SetNextItemWidth(Theme.Scaled(420));
            ImGui.InputText("Mod name", ref _textureModName, 120);
            if (ImGui.Button("Open texture"))
            {
                var request = _newTexture! with { NewModName = _textureModName.Trim() };
                _newTexture = null;
                ImGui.CloseCurrentPopup();
                TextureAction(async () => { await _textures.StartAsync(request).ConfigureAwait(false); });
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { _newTexture = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        if (_discardTexture.HasValue && !ImGui.IsPopupOpen("Discard texture session")) ImGui.OpenPopup("Discard texture session");
        if (ImGui.BeginPopupModal("Discard texture session", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Delete this session's working directory, including its TGA and any editing documents saved there? The committed mod and managed backups are retained.");
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
