namespace ProxyDock.Core.Models;

/// <summary>
/// Временная runtime-модель учётных данных прокси. Никогда не сериализуется в JSON-конфигурацию.
/// </summary>
public sealed record ProxyCredentials(string Username, string Password);
