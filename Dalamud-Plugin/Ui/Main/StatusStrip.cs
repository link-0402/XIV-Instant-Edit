using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Main;

internal readonly record struct FeedbackState(string Text, FeedbackSeverity Severity);

/// <summary> The main window's bottom row: latest message for the active tab, busy indicator, and the history popover. </summary>
internal sealed class StatusStrip
{
    private const string HistoryPopupName = "##instant-edit-status-history";
    private readonly StatusFeed _feed;

    public StatusStrip(StatusFeed feed) => _feed = feed;

    /// <param name="cancel">What the Cancel button stops, or null when nothing running can be cancelled.</param>
    public void Draw(FeedbackState feedback, bool busy, Action? cancel)
    {
        var canCancel = cancel is not null;
        var style = ImGui.GetStyle();
        var frame = ImGui.GetFrameHeight();
        using var strip = ImRaii.Child("##instant-edit-status-strip", new Vector2(0, frame), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!strip.Success)
            return;

        var rightWidth = frame;
        if (busy)
            rightWidth += frame + style.ItemSpacing.X + ImGui.CalcTextSize("Working").X + style.ItemSpacing.X;
        if (canCancel)
            rightWidth += ImGui.CalcTextSize("Cancel").X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        var messageWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - rightWidth - style.ItemSpacing.X);
        using (var message = ImRaii.Child("##instant-edit-status-message", new Vector2(messageWidth, frame), false,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (message.Success)
            {
                ImGui.AlignTextToFramePadding();
                if (feedback.Text.Length == 0)
                    ImGui.TextColored(Theme.Hint, "Ready");
                else
                {
                    var (accent, _, icon) = Theme.Severity(feedback.Severity);
                    ImGui.TextColored(accent, icon);
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextUnformatted(FirstLine(feedback.Text));
                    if (ImGui.IsItemHovered())
                    {
                        using var tooltip = ImRaii.Tooltip();
                        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(480));
                        ImGui.TextUnformatted(feedback.Text);
                    }
                }
            }
        }

        ImGui.SameLine();
        if (busy)
        {
            Widgets.Spinner();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Working");
            ImGui.SameLine();
            if (canCancel && ImGui.SmallButton("Cancel"))
                cancel!();
            if (canCancel)
                ImGui.SameLine();
        }
        if (Widgets.IconButton("##history", FontAwesomeIcon.History, "Recent messages"))
            ImGui.OpenPopup(HistoryPopupName);
        DrawStatusHistory();
    }

    private void DrawStatusHistory()
    {
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(380, 0), Theme.Scaled(680, 440));
        using var popup = ImRaii.Popup(HistoryPopupName);
        if (!popup.Success)
            return;
        var entries = _feed.History(20);
        if (entries.Count == 0)
        {
            ImGui.TextColored(Theme.Hint, "No messages yet.");
            return;
        }

        foreach (var entry in entries)
        {
            var (accent, _, icon) = Theme.Severity(entry.Severity);
            ImGui.TextColored(Theme.Muted, entry.Time.ToLocalTime().ToString("HH:mm:ss"));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, Theme.Gap);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextUnformatted(entry.Text);
        }
    }

    public static string FirstLine(string text)
    {
        var end = text.IndexOf('\n');
        return end < 0 ? text : text[..end];
    }
}
