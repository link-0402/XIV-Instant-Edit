namespace InstantEdit.Ui.Main;

/// <summary> The main window's two everyday status channels, as one call each. </summary>
internal static class StatusReports
{
    public static void SetStatus(this StatusFeed feed, string text, FeedbackSeverity severity)
        => feed.Report(StatusChannel.Models, severity, text);

    public static void SetTextureStatus(this StatusFeed feed, string text, FeedbackSeverity severity)
        => feed.Report(StatusChannel.Textures, severity, text);
}
