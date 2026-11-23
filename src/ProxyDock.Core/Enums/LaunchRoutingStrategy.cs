namespace ProxyDock.Core.Enums;

/// <summary>
/// Фактически применённая стратегия маршрутизации при запуске процесса.
/// </summary>
public enum LaunchRoutingStrategy
{
    EnvironmentOnly = 0,
    EnvironmentAndChromium = 1,
    CliTerminal = 2,
    DirectNoProxy = 3
}
