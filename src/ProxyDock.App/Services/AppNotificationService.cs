namespace ProxyDock.App.Services;

public enum NotificationSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

public sealed record AppNotification(
    string Title,
    string Message,
    NotificationSeverity Severity,
    TimeSpan? AutoDismissAfter = null,
    bool ShowOpenHistoryAction = false,
    Guid? RelatedApplicationId = null);

public sealed class AppNotificationService
{
    public event EventHandler<AppNotification>? NotificationRaised;

    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info)
        => Show(title, message, severity, autoDismiss: severity != NotificationSeverity.Error);

    public void Show(
        string title,
        string message,
        NotificationSeverity severity,
        bool autoDismiss,
        TimeSpan? customDuration = null,
        bool showOpenHistoryAction = false,
        Guid? relatedApplicationId = null)
    {
        TimeSpan? duration = autoDismiss
            ? customDuration ?? severity switch
            {
                NotificationSeverity.Success => TimeSpan.FromSeconds(3),
                NotificationSeverity.Info => TimeSpan.FromSeconds(3),
                NotificationSeverity.Warning => TimeSpan.FromSeconds(6),
                _ => null
            }
            : null;

        NotificationRaised?.Invoke(
            this,
            new AppNotification(
                title,
                message,
                severity,
                duration,
                showOpenHistoryAction,
                relatedApplicationId));
    }
}
