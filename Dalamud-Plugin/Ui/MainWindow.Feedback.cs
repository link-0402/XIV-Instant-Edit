using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private FeedbackState GetFeedback(MainTab tab)
    {
        if (tab == MainTab.TextureEdits)
        {
            lock (_stateLock)
                return new FeedbackState(_textureStatus, _textureStatusSeverity);
        }

        if (tab == MainTab.Animations)
        {
            var service = animations;
            if (service is null)
                return new FeedbackState(string.Empty, FeedbackSeverity.Error);
            return new FeedbackState(service.Status, service.LastResult is { Success: false } && !service.Busy
                ? FeedbackSeverity.Error
                : FeedbackSeverity.Success);
        }

        lock (_stateLock)
            return new FeedbackState(_status, _statusSeverity);
    }

    private void DrawFeedback(FeedbackState feedback)
    {
        var text = feedback.Text;
        var severity = feedback.Severity;

        if (text.Length == 0)
            return;

        var (accent, background, icon) = severity switch
        {
            FeedbackSeverity.Warning => (new Vector4(1f, .78f, .2f, 1), new Vector4(.22f, .16f, .04f, 1), "⚠"),
            FeedbackSeverity.Error => (new Vector4(1f, .3f, .3f, 1), new Vector4(.24f, .055f, .055f, 1), "✕"),
            _ => (new Vector4(.35f, .85f, .55f, 1), new Vector4(.08f, .18f, .12f, 1), "✓"),
        };

        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1);
        if (ImGui.BeginChild("##instant-edit-feedback", new Vector2(0, CalculateFeedbackHeight(text)), true))
        {
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, 6);
            ImGui.PushTextWrapPos();
            ImGui.TextColored(new Vector4(.92f, .93f, .96f, 1), text);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private float GetFeedbackHeight(FeedbackState feedback)
        => feedback.Text.Length == 0 ? 0 : CalculateFeedbackHeight(feedback.Text);

    private static float CalculateFeedbackHeight(string text)
    {
        var style = ImGui.GetStyle();
        var iconAndSpacingWidth = ImGui.CalcTextSize("⚠").X + 6;
        var wrapWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - style.WindowPadding.X * 2 - iconAndSpacingWidth);
        var textHeight = ImGui.CalcTextSize(text, false, wrapWidth).Y;
        return Math.Max(ImGui.GetFrameHeightWithSpacing() * 2, textHeight + style.WindowPadding.Y * 2 + 4);
    }

    private void SetStatus(string text, FeedbackSeverity severity) { lock (_stateLock) { _status = text; _statusSeverity = severity; } }
    private void SetTextureStatus(string text, FeedbackSeverity severity) { lock (_stateLock) { _textureStatus = text; _textureStatusSeverity = severity; } }
    private static string Sanitize(string name) { var invalid = Path.GetInvalidFileNameChars(); var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(); return value.Length == 0 ? "Object" : value; }

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SafeId(string? value)
    {
        var id = Safe(value, "resource");
        return id.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private enum FeedbackSeverity
    {
        Success,
        Warning,
        Error,
    }

    private enum MainTab
    {
        OnScreen,
        ModBrowser,
        TextureEdits,
        Animations,
    }

    private readonly record struct FeedbackState(string Text, FeedbackSeverity Severity);

    private sealed record ActorView(
        OnScreenObject? Entity,
        string Category,
        string Name,
        List<ResourceView> Roots,
        int ImportObjectIndex);
    private sealed record ResourceView(
        string Type,
        string Icon,
        string Name,
        string GamePath,
        string ActualPath,
        string SourceLabel,
        string SourceModName,
        string SourceModDirectory,
        string SourceModRootPath,
        string SourceRelativePath,
        Guid? SourceModStableId,
        ResourceSourceState SourceState,
        string Section,
        string Slot,
        int Order,
        string OptionMapping,
        IReadOnlyList<string> OptionMemberships,
        List<ResourceView> Children);
}
