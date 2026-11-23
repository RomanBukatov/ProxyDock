namespace ProxyDock.Core.Enums;

/// <summary>
/// Результат последней диагностики прокси-профиля.
/// </summary>
public enum ProxyCheckStatus
{
    Unknown = 0,
    Testing = 1,
    Reachable = 2,
    TcpOnly = 3,
    AuthRequired = 4,
    Unreachable = 5,
    Invalid = 6
}
