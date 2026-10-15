using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace InstantEdit.Ui.Main;

/// <summary> Dalamud notifications for the main window: status messages worth a toast, and toasts of their own. </summary>
internal sealed class UiNotifications
{
    private readonly Configuration _config;
    private readonly INotificationManager _notifications;
    private readonly IPluginLog _log;

    public UiNotifications(Configuration config, INotificationManager notifications, IPluginLog log)
    {
        _config = config;
        _notifications = notifications;
        _log = log;
    }

    /// <summary> Mirrors warnings, errors and model handoff results as Dalamud notifications. </summary>
    public void Notify(StatusEntry entry)
    {
        if (!_config.ShowNotifications)
            return;
        if (entry.Severity == FeedbackSeverity.Success && entry.Channel != StatusChannel.Models)
            return;
        ShowNotification(entry.Text, entry.Severity switch
        {
            FeedbackSeverity.Warning => NotificationType.Warning,
            FeedbackSeverity.Error => NotificationType.Error,
            FeedbackSeverity.Info => NotificationType.Info,
            _ => NotificationType.Success,
        });
    }

    public void ShowNotification(string content, NotificationType type)
    {
        try
        {
            _notifications.AddNotification(new Notification
            {
                Title = "XIV Instant Edit",
                Content = content,
                Type = type,
            });
        }
        catch (Exception e)
        {
            _log.Debug($"Could not show a notification: {e.Message}");
        }
    }
}
