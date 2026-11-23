namespace ProxyDock.Core.Enums;

/// <summary>
/// Итоговый статус попытки запуска приложения.
/// </summary>
public enum LaunchStatus
{
    Started = 0,
    StartedWithWarning = 1,
    Failed = 2,
    Cancelled = 3
}
