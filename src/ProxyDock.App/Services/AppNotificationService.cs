namespace ProxyDock.App.Services;

public enum NotificationSeverity
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

public sealed record AppNotification(string Title, string Message, NotificationSeverity Severity);

public sealed class AppNotificationService
{
    public event EventHandler<AppNotification>? NotificationRaised;

    public void Show(string title, string message, NotificationSeverity severity = NotificationSeverity.Info)
        => NotificationRaised?.Invoke(this, new AppNotification(title, message, severity));
}
