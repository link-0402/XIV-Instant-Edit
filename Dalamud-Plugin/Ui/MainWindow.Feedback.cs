namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private readonly StatusFeed _feed = new();

    private static StatusChannel ChannelOf(MainTab tab)
        => tab switch
        {
            MainTab.TextureEdits => StatusChannel.Textures,
            MainTab.Animations => StatusChannel.Animations,
            _ => StatusChannel.Models,
        };

    private FeedbackState GetFeedback(MainTab tab)
    {
        if (tab == MainTab.Animations)
        {
            var service = animations;
            if (service is null)
                return new FeedbackState(string.Empty, FeedbackSeverity.Error);
            return new FeedbackState(service.Status, service.LastResult is { Success: false } && !service.Busy
                ? FeedbackSeverity.Error
                : FeedbackSeverity.Success);
        }

        var latest = _feed.Latest(ChannelOf(tab));
        return latest is null
            ? new FeedbackState(string.Empty, FeedbackSeverity.Success)
            : new FeedbackState(latest.Text, latest.Severity);
    }

    private static void DrawFeedback(FeedbackState feedback)
    {
        if (feedback.Text.Length == 0)
            return;
        Widgets.Banner("##instant-edit-feedback", feedback.Severity, feedback.Text);
    }

    private static float GetFeedbackHeight(FeedbackState feedback)
        => feedback.Text.Length == 0 ? 0 : Widgets.BannerHeight(feedback.Text);

    private void SetStatus(string text, FeedbackSeverity severity) => _feed.Report(StatusChannel.Models, severity, text);
    private void SetTextureStatus(string text, FeedbackSeverity severity) => _feed.Report(StatusChannel.Textures, severity, text);
    private static string Sanitize(string name) => UiText.Sanitize(name);
    private static string Safe(string? value, string fallback = "") => UiText.Safe(value, fallback);
    private static string SafeId(string? value) => UiText.SafeId(value);

    private enum MainTab
    {
        OnScreen,
        ModBrowser,
        TextureEdits,
        Animations,
    }

    private readonly record struct FeedbackState(string Text, FeedbackSeverity Severity);
}
